using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.Channels;
using Agents.AI.ContactCenter.Authentication.Authenticators;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.Media.Audio.Resilience;
using Agents.AI.ContactCenter.Tests.Helpers;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using ContactCenter.AIAgent.Configuration;
using ContactCenter.AIAgent.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace Agents.AI.ContactCenter.Tests.DemoHost;

public sealed class BankingDemoHostTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownCaller_OrSmsFailure_EscalatesWithoutAuthorizing(bool knownCaller)
    {
        await using var factory = new DemoFactory();
        factory.Sms.FailSending = knownCaller;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("eventgrid-demo"));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/events/incoming-call",
            Incoming(phone: knownCaller ? DemoFactory.CustomerPhone : "+15555550108"), timeout.Token)).StatusCode);
        var ws = factory.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + factory.Token("acs-demo");
        using var socket = await ws.ConnectAsync(new Uri("ws://localhost" + factory.Gateway.MediaUri!.AbsolutePath), timeout.Token);
        var edge = await factory.Gateway.Edge.Task.WaitAsync(timeout.Token);
        await factory.Speech.WaitForAsync("Welcome", timeout.Token);
        await edge.PushDtmfAsync('1');
        Assert.Equal(DemoFactory.OperatorPhone, await factory.Gateway.Transferred.Task.WaitAsync(timeout.Token));
        Assert.Empty(factory.Sms.Codes);
        Assert.Equal(knownCaller ? 1 : 0, factory.Sms.Attempts);
        Assert.DoesNotContain(factory.Speech.All, text => text.Contains("Your demo balance", StringComparison.Ordinal));
        Assert.False((await factory.Services.GetRequiredService<IDemoBankingService>()
            .GetAccountAsync("customer-demo", timeout.Token)).CardActive);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("acs-demo"));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(factory.Gateway.CallbackUri!.AbsolutePath,
            Callback("Microsoft.Communication.CallTransferAccepted", "operator"), timeout.Token)).StatusCode);
    }

    [Fact]
    public async Task AmbiguousAnswerFailure_IsNotRetried_AndLateConnectionIsHungUp()
    {
        await using var factory = new DemoFactory();
        factory.Gateway.FailAnswer = true;
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("eventgrid-demo"));
        Assert.Equal(HttpStatusCode.BadGateway, (await client.PostAsJsonAsync("/events/incoming-call", Incoming())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/events/incoming-call", Incoming("redelivery"))).StatusCode);
        Assert.Equal(1, factory.Gateway.Answers);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("acs-demo"));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(factory.Gateway.CallbackUri!.AbsolutePath,
            Callback("Microsoft.Communication.CallConnected", null))).StatusCode);
        Assert.Equal(1, factory.Gateway.Hangups);
    }

    [Theory]
    [InlineData("another-connection", "server-demo")]
    [InlineData("connection-demo", "another-server")]
    public async Task CallbackForAnotherCall_IsRejected(string connectionId, string serverCallId)
    {
        await using var factory = new DemoFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("eventgrid-demo"));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/events/incoming-call", Incoming())).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("acs-demo"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(factory.Gateway.CallbackUri!.AbsolutePath,
            Callback("Microsoft.Communication.CallDisconnected", null, connectionId: connectionId, serverCallId: serverCallId))).StatusCode);
        Assert.Equal(0, factory.Gateway.Hangups);
        Assert.True(factory.Services.GetRequiredService<ICallCoordinator>().HasRoute(factory.Gateway.MediaUri!.Segments[^1]));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(factory.Gateway.CallbackUri.AbsolutePath,
            Callback("Microsoft.Communication.CallDisconnected", null))).StatusCode);
    }

    [Fact]
    public async Task OtpSilence_RoutesToOperator()
    {
        await using var factory = new DemoFactory(otpTimeoutSeconds: 1);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("eventgrid-demo"));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/events/incoming-call", Incoming(), timeout.Token)).StatusCode);
        var ws = factory.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + factory.Token("acs-demo");
        using var socket = await ws.ConnectAsync(new Uri("ws://localhost" + factory.Gateway.MediaUri!.AbsolutePath), timeout.Token);
        var edge = await factory.Gateway.Edge.Task.WaitAsync(timeout.Token);
        await factory.Speech.WaitForAsync("Welcome", timeout.Token);
        await edge.PushDtmfAsync('1');
        await factory.Speech.WaitForAsync("6-digit", timeout.Token);
        Assert.Equal(DemoFactory.OperatorPhone, await factory.Gateway.Transferred.Task.WaitAsync(timeout.Token));
        Assert.DoesNotContain(factory.Speech.All, text => text.Contains("Your demo balance", StringComparison.Ordinal));
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("acs-demo"));
        await client.PostAsJsonAsync(factory.Gateway.CallbackUri!.AbsolutePath,
            Callback("Microsoft.Communication.CallTransferAccepted", "operator"), timeout.Token);
    }

    [Fact]
    public async Task RealtimePrimary_UsesProtectedWorkflowTools_AndKeypadConfirmation()
    {
        await using var factory = new DemoFactory(realtime: true);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("eventgrid-demo"));
        var incoming = await client.PostAsJsonAsync("/events/incoming-call", Incoming(), timeout.Token);
        Assert.True(incoming.IsSuccessStatusCode, factory.Errors.Last?.ToString());
        var ws = factory.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + factory.Token("acs-demo");
        using var socket = await ws.ConnectAsync(new Uri("ws://localhost" + factory.Gateway.MediaUri!.AbsolutePath), timeout.Token);
        var edge = await factory.Gateway.Edge.Task.WaitAsync(timeout.Token);
        var welcome = await factory.Realtime.Session.WaitForAsync("Current Stage: welcome", timeout.Token);
        Assert.Equal(24000, factory.Realtime.InitialOptions!.InputAudioFormat!.SampleRate);
        var advance = Assert.Single(welcome.Tools!.OfType<AIFunction>());
        await advance.InvokeAsync(new AIFunctionArguments { ["target"] = "card" }, timeout.Token);
        var verification = await factory.Realtime.Session.WaitForAsync("verification is in progress", timeout.Token);
        Assert.Empty(verification.Tools!);
        var otp = await factory.Sms.Sent.Reader.ReadAsync(timeout.Token);
        await factory.Speech.WaitForAsync("6-digit", timeout.Token);
        foreach (var digit in otp.Code) { await edge.PushDtmfAsync(digit); }
        var confirm = await factory.Realtime.Session.WaitForAsync("Current Stage: confirm-card", timeout.Token);
        var denied = await Assert.Single(confirm.Tools!.OfType<AIFunction>())
            .InvokeAsync(new AIFunctionArguments { ["target"] = "activate" }, timeout.Token);
        var deniedResult = Assert.IsType<JsonElement>(denied)
            .Deserialize<global::Agents.AI.ContactCenter.IvrWorkflow.Execution.AdvanceFunctionResult>(JsonSerializerOptions.Web);
        Assert.NotNull(deniedResult);
        Assert.False(deniedResult.Advanced);
        var bank = factory.Services.GetRequiredService<IDemoBankingService>();
        Assert.False((await bank.GetAccountAsync("customer-demo", timeout.Token)).CardActive);
        await edge.PushDtmfAsync('1');
        var result = await factory.Realtime.Session.WaitForAsync("Current Stage: card-result", timeout.Token);
        Assert.Contains("is now active", result.Instructions);
        Assert.True((await bank.GetAccountAsync("customer-demo", timeout.Token)).CardActive);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("acs-demo"));
        await client.PostAsJsonAsync(factory.Gateway.CallbackUri!.AbsolutePath,
            Callback("Microsoft.Communication.CallDisconnected", null), timeout.Token);
    }

    [Fact]
    public async Task ExhaustedBotCapacity_RedirectsOnceWithoutAnswering()
    {
        await using var factory = new DemoFactory(noCapacity: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("eventgrid-demo"));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/events/incoming-call", Incoming())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/events/incoming-call", Incoming("duplicate"))).StatusCode);
        Assert.Equal(0, factory.Gateway.Answers);
        Assert.Equal(1, factory.Gateway.Redirects);
        Assert.Equal(DemoFactory.OperatorPhone, await factory.Gateway.Transferred.Task);
    }

    [Fact]
    public async Task SubscriptionEcho_IsHarmless_AndCallDeliveryRequiresCorrectSender()
    {
        await using var factory = new DemoFactory();
        using var client = factory.CreateClient();
        var validation = new[] { new { id = "validation", eventType = "Microsoft.EventGrid.SubscriptionValidationEvent",
            eventTime = DateTimeOffset.UtcNow, subject = "demo", dataVersion = "1.0", data = new { validationCode = "echo-me" } } };
        var echo = await client.PostAsJsonAsync("/events/incoming-call", validation);
        Assert.Equal(HttpStatusCode.OK, echo.StatusCode);
        Assert.Contains("echo-me", await echo.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/events/incoming-call", Incoming())).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("eventgrid-demo", Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/events/incoming-call", Incoming())).StatusCode);
        Assert.Equal(0, factory.Gateway.Answers);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/automation/callbacks/unknown", Array.Empty<object>())).StatusCode);
    }

    [Fact]
    public async Task IncomingAnswer_Otp_Balance_CardActivation_AndGoodbye_RunEndToEndWithFakes()
    {
        await using var factory = new DemoFactory();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("eventgrid-demo"));
        var incomingResponse = await client.PostAsJsonAsync("/events/incoming-call", Incoming(), timeout.Token);
        Assert.True(incomingResponse.StatusCode == HttpStatusCode.OK, factory.Errors.Last?.ToString() ?? await incomingResponse.Content.ReadAsStringAsync(timeout.Token));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/events/incoming-call", Incoming("duplicate-event"), timeout.Token)).StatusCode);
        Assert.Equal(1, factory.Gateway.Answers);
        Assert.Equal("wss", factory.Gateway.MediaUri!.Scheme);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("acs-demo"));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(factory.Gateway.CallbackUri!.AbsolutePath,
            Callback("Microsoft.Communication.PlayCompleted", null, "unsolicited-play"), timeout.Token)).StatusCode);
        Assert.Equal(0, factory.Gateway.Hangups);
        var webSockets = factory.Server.CreateWebSocketClient();
        webSockets.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + factory.Token("acs-demo");
        using var socket = await webSockets.ConnectAsync(new Uri("ws://localhost" + factory.Gateway.MediaUri.AbsolutePath), timeout.Token);
        var edge = await factory.Gateway.Edge.Task.WaitAsync(timeout.Token);
        await factory.Speech.WaitForAsync("Welcome", timeout.Token);
        Assert.Empty(factory.Sms.Codes);
        await edge.PushDtmfAsync('1');
        var otp = await factory.Sms.Sent.Reader.ReadAsync(timeout.Token);
        Assert.Equal(DemoFactory.CustomerPhone, otp.Phone);
        await factory.Speech.WaitForAsync("6-digit", timeout.Token);
        foreach (var digit in otp.Code) { await edge.PushDtmfAsync(digit); }
        var balancePrompt = await factory.Speech.WaitForAsync("Your demo balance", timeout.Token);
        Assert.Contains("1,250.50", balancePrompt);
        await edge.PushDtmfAsync('1');
        await factory.Speech.WaitForAsync("Welcome", timeout.Token);
        await edge.PushDtmfAsync('2');
        await factory.Speech.WaitForAsync("confirm activation", timeout.Token);
        var bank = factory.Services.GetRequiredService<IDemoBankingService>();
        Assert.False((await bank.GetAccountAsync("customer-demo", timeout.Token)).CardActive);
        await edge.PushDtmfAsync('1');
        await factory.Speech.WaitForAsync("is now active", timeout.Token);
        Assert.True((await bank.GetAccountAsync("customer-demo", timeout.Token)).CardActive);
        Assert.Single(factory.Sms.Codes); // Still-valid OTP proof was reused; ANI did not trigger extra SMS.
        await edge.PushDtmfAsync('9');
        var goodbye = await factory.Gateway.Played.Reader.ReadAsync(timeout.Token);
        Assert.Equal(0, factory.Gateway.Hangups);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("acs-demo"));
        var wrong = await client.PostAsJsonAsync(factory.Gateway.CallbackUri!.AbsolutePath,
            Callback("Microsoft.Communication.PlayCompleted", "unrelated-operation"), timeout.Token);
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Equal(0, factory.Gateway.Hangups);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(factory.Gateway.CallbackUri.AbsolutePath,
            Callback("Microsoft.Communication.PlayCompleted", goodbye.Operation, "finish-event"), timeout.Token)).StatusCode);
        Assert.Equal(1, factory.Gateway.Hangups);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedOtp_EscalatesWithoutExposingBalance_AndHandlesTransferOutcome(bool accepted)
    {
        await using var factory = new DemoFactory();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("eventgrid-demo"));
        var incomingResponse = await client.PostAsJsonAsync("/events/incoming-call", Incoming(), timeout.Token);
        Assert.True(incomingResponse.StatusCode == HttpStatusCode.OK, factory.Errors.Last?.ToString() ?? await incomingResponse.Content.ReadAsStringAsync(timeout.Token));
        var ws = factory.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + factory.Token("acs-demo");
        using var socket = await ws.ConnectAsync(new Uri("ws://localhost" + factory.Gateway.MediaUri!.AbsolutePath), timeout.Token);
        var edge = await factory.Gateway.Edge.Task.WaitAsync(timeout.Token);
        await factory.Speech.WaitForAsync("Welcome", timeout.Token);
        await edge.PushDtmfAsync('1');
        var otp = await factory.Sms.Sent.Reader.ReadAsync(timeout.Token);
        var wrongCode = otp.Code == "000000" ? "111111" : "000000";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await factory.Speech.WaitForAsync("6-digit", timeout.Token);
            foreach (var digit in wrongCode) { await edge.PushDtmfAsync(digit); }
        }
        var target = await factory.Gateway.Transferred.Task.WaitAsync(timeout.Token);
        Assert.Equal(DemoFactory.OperatorPhone, target);
        Assert.DoesNotContain(factory.Speech.All, text => text.Contains("Your demo balance", StringComparison.Ordinal));
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("acs-demo"));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(factory.Gateway.CallbackUri!.AbsolutePath,
            Callback(accepted ? "Microsoft.Communication.CallTransferAccepted" : "Microsoft.Communication.CallTransferFailed", "operator"), timeout.Token)).StatusCode);
        if (accepted) { Assert.Equal(0, factory.Gateway.Hangups); }
        else
        {
            var apology = await factory.Gateway.Played.Reader.ReadAsync(timeout.Token);
            Assert.Contains("could not connect", apology.Text);
            Assert.Equal(0, factory.Gateway.Hangups);
            await client.PostAsJsonAsync(factory.Gateway.CallbackUri.AbsolutePath,
                Callback("Microsoft.Communication.PlayCompleted", apology.Operation, "apology-done"), timeout.Token);
            Assert.Equal(1, factory.Gateway.Hangups);
        }
    }

    [Fact]
    public async Task OpenApi_ListsDemoAndCallRoutes_WithoutBankingDataEndpoints()
    {
        await using var factory = new DemoFactory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("/events/incoming-call", json);
        Assert.Contains("/automation/callbacks/{routeId}", json);
        Assert.Contains("/automation/media/{routeId}", json);
        Assert.DoesNotContain("/weatherforecast", json);
        Assert.DoesNotContain(DemoFactory.CustomerPhone, json);
    }

    private static object[] Incoming(string id = "incoming-event", string phone = DemoFactory.CustomerPhone) =>
    [
        new
        {
            id, eventType = "Microsoft.Communication.IncomingCall", eventTime = DateTimeOffset.UtcNow, subject = "demo", dataVersion = "1.0",
            data = new
            {
                incomingCallContext = "opaque-local-test-context", serverCallId = "server-demo", correlationId = "correlation-demo",
                callerDisplayName = "Demo Caller",
                from = new { rawId = "4:" + phone, kind = "phoneNumber", phoneNumber = new { value = phone } },
                to = new { rawId = "4:+15555550102", kind = "phoneNumber", phoneNumber = new { value = "+15555550102" } },
            },
        },
    ];

    private static object[] Callback(string type, string? operationContext, string id = "callback-event",
        string connectionId = "connection-demo", string serverCallId = "server-demo") =>
    [
        new { id, type, source = "acs", specversion = "1.0", time = DateTimeOffset.UtcNow,
            data = new { callConnectionId = connectionId, serverCallId, operationContext,
                resultInformation = new { code = 200, subCode = 0, message = "" } } },
    ];

    private sealed class DemoFactory(bool realtime = false, bool noCapacity = false, int otpTimeoutSeconds = 60) : WebApplicationFactory<global::Program>
    {
        public const string CustomerPhone = "+15555550101";
        public const string OperatorPhone = "+15555550109";
        private const string Sender = "00000000-0000-0000-0000-000000000002";
        private readonly SymmetricSecurityKey _key = new(RandomNumberGenerator.GetBytes(32));
        public Gateway Gateway { get; } = new();
        public Sms SenderService { get; } = new();
        public Sms Sms => SenderService;
        public Speech Speech { get; } = new();
        public RealtimeClient Realtime { get; } = new();
        public CaptureErrors Errors { get; } = new();
        public string Token(string audience, string sender = Sender) => new JwtSecurityTokenHandler().WriteToken(
            new JwtSecurityToken("local-test-issuer", audience, [new Claim("oid", sender)],
                DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5), new SigningCredentials(_key, SecurityAlgorithms.HmacSha256)));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseContentRoot(FindApiRoot());
            var settings = new Dictionary<string, string?>
            {
                ["BankingDemo:Enabled"] = "true", ["BankingDemo:PublicBaseUri"] = "https://localhost/",
                ["BankingDemo:AcsEndpoint"] = "https://acs.example.invalid/", ["BankingDemo:AcsAudience"] = "acs-demo",
                ["BankingDemo:SpeechEndpoint"] = "https://speech.example.invalid/", ["BankingDemo:VoiceLiveEndpoint"] = "https://speech.example.invalid/",
                ["BankingDemo:VoiceLiveModel"] = "not-invoked", ["BankingDemo:SmsFrom"] = "+15555550103",
                ["BankingDemo:OperatorNumber"] = OperatorPhone, ["BankingDemo:RedirectCallerId"] = "+15555550102",
                ["BankingDemo:EventGridTenantId"] = "00000000-0000-0000-0000-000000000001",
                ["BankingDemo:EventGridAudience"] = "eventgrid-demo", ["BankingDemo:EventGridObjectId"] = Sender,
                ["BankingDemo:Customers:0:Id"] = "customer-demo", ["BankingDemo:Customers:0:DisplayName"] = "Demo Caller",
                ["BankingDemo:Customers:0:PhoneNumber"] = CustomerPhone, ["BankingDemo:Customers:0:CardLastFour"] = "1234",
                ["BankingDemo:OtpInputTimeoutSeconds"] = otpTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["AgentTiers:Tiers:RealtimeVoice:MaxConcurrent"] = realtime ? "1" : "0",
                ["AgentTiers:Tiers:DtmfOnly:MaxConcurrent"] = noCapacity ? "0" : "5",
            };
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
            foreach (var pair in settings) { builder.UseSetting(pair.Key, pair.Value); }
            builder.ConfigureTestServices(services =>
            {
                services.Insert(0, ServiceDescriptor.Singleton<IExceptionHandler>(Errors));
                services.RemoveAll<IAcsCallGateway>(); services.AddSingleton<IAcsCallGateway>(Gateway);
                services.RemoveAll<ISmsOtpSender>(); services.AddSingleton<ISmsOtpSender>(Sms);
                services.RemoveAll<IRealtimeClient>(); services.AddSingleton<IRealtimeClient>(Realtime);
                services.RemoveAll<ResilientSpeechSynthesizer>();
                services.AddSingleton(new ResilientSpeechSynthesizer([("fake", Speech)], new SpeechResilienceOptions { MaxRetryAttempts = 0 }));
                foreach (var scheme in new[] { WebhookAuthentication.AcsScheme, WebhookAuthentication.EventGridScheme })
                {
                    services.PostConfigure<JwtBearerOptions>(scheme, options =>
                    {
                        options.Authority = string.Empty; options.MetadataAddress = string.Empty;
                        options.Configuration = new OpenIdConnectConfiguration { Issuer = "local-test-issuer" };
                        options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(options.Configuration);
                        options.TokenValidationParameters = new()
                        {
                            ValidateIssuer = true, ValidIssuer = "local-test-issuer", ValidateAudience = true,
                            ValidAudience = scheme == WebhookAuthentication.AcsScheme ? "acs-demo" : "eventgrid-demo",
                            ValidateLifetime = true, ValidateIssuerSigningKey = true, IssuerSigningKey = _key,
                            RequireSignedTokens = true, ClockSkew = TimeSpan.Zero,
                        };
                    });
                }
            });
        }

        private static string FindApiRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ContactCenter.slnx"))) { return Path.Combine(dir.FullName, "ContactCenter.AIAgent"); }
            }
            throw new DirectoryNotFoundException("Could not locate the demo API.");
        }
    }

    private sealed class Sms : ISmsOtpSender
    {
        public bool FailSending { get; set; }
        public int Attempts { get; private set; }
        public ConcurrentQueue<string> Codes { get; } = new();
        public Channel<(string Phone, string Code)> Sent { get; } = Channel.CreateUnbounded<(string, string)>();
        public Task SendAsync(string phoneNumberE164, string code, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (FailSending) { throw new global::Azure.RequestFailedException(503, "Simulated SMS failure."); }
            Codes.Enqueue(code); Sent.Writer.TryWrite((phoneNumberE164, code)); return Task.CompletedTask;
        }

    }

    private sealed class CaptureErrors : IExceptionHandler
    {
        public Exception? Last { get; private set; }
        public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
        {
            Last = exception;
            return ValueTask.FromResult(false);
        }
    }

    private sealed class Speech : ISpeechSynthesizer
    {
        public ConcurrentQueue<string> All { get; } = new();
        private readonly Channel<string> _prompts = Channel.CreateUnbounded<string>();
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(string text, SynthesizerInputFormat inputFormat = SynthesizerInputFormat.Text,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            All.Enqueue(text); await _prompts.Writer.WriteAsync(text, cancellationToken);
            yield return new byte[] { 0, 0 };
        }
        public async Task<string> WaitForAsync(string fragment, CancellationToken ct)
        {
            await foreach (var text in _prompts.Reader.ReadAllAsync(ct)) { if (text.Contains(fragment, StringComparison.OrdinalIgnoreCase)) { return text; } }
            throw new InvalidOperationException("Expected prompt was not produced.");
        }
    }

    private sealed class Gateway : IAcsCallGateway
    {
        public bool FailAnswer { get; set; }
        public int Answers;
        public int Redirects;
        public int Hangups;
        public Uri? CallbackUri;
        public Uri? MediaUri;
        public TaskCompletionSource<FakeCallerEdge> Edge { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Transferred { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<(string Text, string Operation)> Played { get; } = Channel.CreateUnbounded<(string, string)>();
        public Task RedirectAsync(string incomingContext, string target, CancellationToken ct)
        {
            Interlocked.Increment(ref Redirects);
            Transferred.TrySetResult(target);
            return Task.CompletedTask;
        }
        public Task<CallConnectionProperties> AnswerAsync(string incomingContext, Uri callback, Uri media, CancellationToken ct)
        {
            Interlocked.Increment(ref Answers); CallbackUri = callback; MediaUri = media;
            if (FailAnswer) { throw new global::Azure.RequestFailedException(503, "Simulated ambiguous answer failure."); }
            return Task.FromResult(CallAutomationModelFactory.CallConnectionProperties(
                callConnectionId: "connection-demo", serverCallId: "server-demo"));
        }
        public ICallEdge CreateEdge(WebSocket socket, CallConnectionProperties connection, IncomingCallContext caller, CancellationToken ct)
        {
            var edge = new FakeCallerEdge(caller.CallerIdentifier, canControl: true);
            Edge.TrySetResult(edge); return edge;
        }
        public Task PlayAsync(string connectionId, string text, string operation, CancellationToken ct)
        {
            Played.Writer.TryWrite((text, operation)); return Task.CompletedTask;
        }
        public Task TransferAsync(string connectionId, string target, string contextId, CancellationToken ct)
        {
            Transferred.TrySetResult(target); return Task.CompletedTask;
        }
        public Task HangUpAsync(string connectionId, CancellationToken ct) { Interlocked.Increment(ref Hangups); return Task.CompletedTask; }
    }

    private sealed class RealtimeClient : IRealtimeClient
    {
        public RealtimeSession Session { get; } = new();
        public RealtimeSessionOptions? InitialOptions { get; private set; }
        public Task<IRealtimeClientSession> CreateSessionAsync(RealtimeSessionOptions? options = null, CancellationToken cancellationToken = default)
        {
            InitialOptions = options;
            return Task.FromResult<IRealtimeClientSession>(Session);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class RealtimeSession : IRealtimeClientSession
    {
        private readonly Channel<RealtimeServerMessage> _responses = Channel.CreateUnbounded<RealtimeServerMessage>();
        private readonly Channel<RealtimeSessionOptions> _updates = Channel.CreateUnbounded<RealtimeSessionOptions>();
        public RealtimeSessionOptions? Options { get; private set; }
        public Task SendAsync(RealtimeClientMessage message, CancellationToken cancellationToken = default)
        {
            if (message is SessionUpdateRealtimeClientMessage update)
            {
                Options = update.Options;
                _updates.Writer.TryWrite(Options);
            }
            return Task.CompletedTask;
        }
        public IAsyncEnumerable<RealtimeServerMessage> GetStreamingResponseAsync(CancellationToken cancellationToken = default)
            => _responses.Reader.ReadAllAsync(cancellationToken);
        public async Task<RealtimeSessionOptions> WaitForAsync(string fragment, CancellationToken ct)
        {
            await foreach (var update in _updates.Reader.ReadAllAsync(ct))
            {
                if (update.Instructions?.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true) { return update; }
            }
            throw new InvalidOperationException("Expected realtime stage was not configured.");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public ValueTask DisposeAsync() { _responses.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
