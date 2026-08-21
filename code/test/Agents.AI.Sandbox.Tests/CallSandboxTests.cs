using System.Text;
using System.Text.Json;
using Agents.AI.Sandbox;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agents.AI.Sandbox.Tests;

public sealed class CallSandboxTests
{
    private static CallSandboxOptions EnabledOptions(TokenCredential credential) => new()
    {
        Enabled = true,
        SubscriptionId = "sub",
        ResourceGroup = "rg",
        SandboxGroup = "sg",
        Credential = credential,
    };

    [Fact]
    public async Task ExecuteCode_ProvisionsOnce_ReusesSandbox_AndTearsDownOnDispose()
    {
        var handler = new SandboxRecordingHandler();
        var http = new HttpClient(handler);
        var sandbox = new CallSandbox(http, EnabledOptions(new FakeSandboxTokenCredential()), "call-1", TestTelemetry.Sandbox, NullLogger.Instance);

        var first = await sandbox.ExecuteCodeAsync("python", "print('hi')", TestContext.Current.CancellationToken);
        var second = await sandbox.ExecuteCodeAsync("python", "print('bye')", TestContext.Current.CancellationToken);
        await sandbox.DisposeAsync();

        Assert.True(first.Succeeded);
        Assert.Equal("hello\n", first.Stdout);
        Assert.True(second.Succeeded);

        Assert.Equal(1, handler.Requests.Count(r => r.Method == HttpMethod.Put));
        Assert.Equal(2, handler.Requests.Count(r => r.Method == HttpMethod.Post));
        Assert.Equal(1, handler.Requests.Count(r => r.Method == HttpMethod.Delete));
    }

    [Fact]
    public async Task ExecuteCode_AlwaysSendsBearerToken()
    {
        var handler = new SandboxRecordingHandler();
        var http = new HttpClient(handler);
        await using var sandbox = new CallSandbox(http, EnabledOptions(new FakeSandboxTokenCredential()), "call-1", TestTelemetry.Sandbox, NullLogger.Instance);

        await sandbox.ExecuteCodeAsync("python", "print('hi')", TestContext.Current.CancellationToken);

        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, r => Assert.Equal("Bearer fake-token", r.Authorization));
    }

    [Fact]
    public async Task ExecuteCode_MarshalsCodeAsBase64_NeverInterpolatesRawShell()
    {
        var handler = new SandboxRecordingHandler();
        var http = new HttpClient(handler);
        await using var sandbox = new CallSandbox(http, EnabledOptions(new FakeSandboxTokenCredential()), "call-1", TestTelemetry.Sandbox, NullLogger.Instance);

        const string malicious = "print('x'); import os; os.system('rm -rf /tmp/data')";
        await sandbox.ExecuteCodeAsync("python", malicious, TestContext.Current.CancellationToken);

        var post = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        using var doc = JsonDocument.Parse(post.Body);
        var command = doc.RootElement.GetProperty("command").GetString()!;

        // The raw, attacker-controlled snippet must never appear verbatim in the shell command.
        Assert.DoesNotContain("os.system", command);
        Assert.DoesNotContain("rm -rf /tmp/data", command);
        // It must be carried as base64 (whose alphabet has no shell metacharacters) and decoded inside the VM.
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(malicious)), command);
        Assert.Contains("base64 -d", command);
    }

    [Fact]
    public async Task Provision_SendsConfiguredDiskImageAndResources()
    {
        var handler = new SandboxRecordingHandler();
        var http = new HttpClient(handler);
        var options = EnabledOptions(new FakeSandboxTokenCredential());
        options.DiskImageName = "contoso-cc-runtime";
        options.Cpu = "2";
        options.Memory = "2Gi";
        await using var sandbox = new CallSandbox(http, options, "call-1", TestTelemetry.Sandbox, NullLogger.Instance);

        await sandbox.EnsureProvisionedAsync(TestContext.Current.CancellationToken);

        var put = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        Assert.EndsWith("/sandboxGroups/sg/sandboxes", put.Uri.AbsolutePath);
        using var doc = JsonDocument.Parse(put.Body);
        var root = doc.RootElement;
        Assert.Equal("contoso-cc-runtime", root.GetProperty("sourcesRef").GetProperty("diskImage").GetProperty("name").GetString());
        Assert.Equal("2", root.GetProperty("resources").GetProperty("cpu").GetString());
        Assert.Equal("2Gi", root.GetProperty("resources").GetProperty("memory").GetString());
    }

    [Fact]
    public async Task Provision_AppliesDenyByDefaultEgress_WithAllowedHosts()
    {
        var handler = new SandboxRecordingHandler();
        var http = new HttpClient(handler);
        var options = EnabledOptions(new FakeSandboxTokenCredential());
        options.EgressAllowedHosts.Add("*.openai.azure.com");
        await using var sandbox = new CallSandbox(http, options, "call-1", TestTelemetry.Sandbox, NullLogger.Instance);

        await sandbox.EnsureProvisionedAsync(TestContext.Current.CancellationToken);

        var put = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        using var doc = JsonDocument.Parse(put.Body);
        var egress = doc.RootElement.GetProperty("egress");
        Assert.Equal("Deny", egress.GetProperty("defaultAction").GetString());
        var hosts = egress.GetProperty("allowedHosts").EnumerateArray().Select(h => h.GetString()).ToList();
        Assert.Equal(["*.openai.azure.com"], hosts);
    }

    [Fact]
    public async Task Provision_DefaultsToDenyAll_EgressWithNoAllowedHosts()
    {
        var handler = new SandboxRecordingHandler();
        var http = new HttpClient(handler);
        await using var sandbox = new CallSandbox(http, EnabledOptions(new FakeSandboxTokenCredential()), "call-1", TestTelemetry.Sandbox, NullLogger.Instance);

        await sandbox.EnsureProvisionedAsync(TestContext.Current.CancellationToken);

        var put = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        using var doc = JsonDocument.Parse(put.Body);
        var egress = doc.RootElement.GetProperty("egress");
        Assert.Equal("Deny", egress.GetProperty("defaultAction").GetString());
        Assert.Empty(egress.GetProperty("allowedHosts").EnumerateArray());
    }

    [Fact]
    public async Task Provision_OmitsEgress_WhenDenyByDefaultDisabled()
    {
        var handler = new SandboxRecordingHandler();
        var http = new HttpClient(handler);
        var options = EnabledOptions(new FakeSandboxTokenCredential());
        options.EgressDenyByDefault = false;
        await using var sandbox = new CallSandbox(http, options, "call-1", TestTelemetry.Sandbox, NullLogger.Instance);

        await sandbox.EnsureProvisionedAsync(TestContext.Current.CancellationToken);

        var put = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        using var doc = JsonDocument.Parse(put.Body);
        Assert.False(doc.RootElement.TryGetProperty("egress", out _));
    }

    [Fact]
    public async Task Provision_AssignsAgentIdentity_WhenConfigured()
    {
        var handler = new SandboxRecordingHandler();
        var http = new HttpClient(handler);
        var options = EnabledOptions(new FakeSandboxTokenCredential());
        options.AgentIdentityResourceId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/agent";
        await using var sandbox = new CallSandbox(http, options, "call-1", TestTelemetry.Sandbox, NullLogger.Instance);

        await sandbox.EnsureProvisionedAsync(TestContext.Current.CancellationToken);

        var put = handler.Requests.Single(r => r.Method == HttpMethod.Put);
        using var doc = JsonDocument.Parse(put.Body);
        Assert.Equal(
            options.AgentIdentityResourceId,
            doc.RootElement.GetProperty("agentIdentity").GetProperty("resourceId").GetString());
    }

    [Fact]
    public void Validate_Throws_WhenEnabledWithoutSandboxGroupCoordinates()
    {
        var options = new CallSandboxOptions { Enabled = true };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}

public sealed class CallSandboxManagerTests
{
    public const string TestCallId = "call-1";
    private static CallSandboxOptions EnabledOptions() => new()
    {
        Enabled = true,
        SubscriptionId = "sub",
        ResourceGroup = "rg",
        SandboxGroup = "sg",
        Credential = new FakeSandboxTokenCredential(),
    };

    [Fact]
    public async Task Disabled_ReportsNotEnabled_AndEnsureThrows()
    {
        var manager = new CallSandboxManager(
            new SingleClientHttpClientFactory(new SandboxRecordingHandler()),
            Options.Create(new CallSandboxOptions { Enabled = false }),
            TestTelemetry.Sandbox,
            TestCallId,
            NullLoggerFactory.Instance);

        Assert.False(manager.IsEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await manager.EnsureAsync(TestContext.Current.CancellationToken));
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task Enabled_ProvisionsOneSandbox_SharedAcrossCalls_AndDisposesIt()
    {
        var handler = new SandboxRecordingHandler();
        var manager = new CallSandboxManager(
            new SingleClientHttpClientFactory(handler),
            Options.Create(EnabledOptions()),
            TestTelemetry.Sandbox,
            TestCallId,
            NullLoggerFactory.Instance);

        var first = await manager.EnsureAsync(TestContext.Current.CancellationToken);
        var second = await manager.EnsureAsync(TestContext.Current.CancellationToken);

        Assert.Same(first, second);
        Assert.Equal(1, handler.Requests.Count(r => r.Method == HttpMethod.Put));

        await manager.DisposeAsync();
        Assert.Equal(1, handler.Requests.Count(r => r.Method == HttpMethod.Delete));
    }
}

public sealed class CodeInterpreterToolsTests
{
    [Fact]
    public async Task Disabled_ReturnsFailure_WithoutThrowing()
    {
        var manager = new CallSandboxManager(
            new SingleClientHttpClientFactory(new SandboxRecordingHandler()),
            Options.Create(new CallSandboxOptions { Enabled = false }),
            TestTelemetry.Sandbox,
            CallSandboxManagerTests.TestCallId,
            NullLoggerFactory.Instance);
        var tools = new CodeInterpreterTools(manager);

        var function = Assert.Single(tools.AsAITools());
        Assert.Equal(CodeInterpreterTools.ExecutePythonToolName, function.Name);

        var result = await tools.ExecutePythonAsync("print('x')", TestContext.Current.CancellationToken);
        Assert.False(result.Succeeded);
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task Enabled_RunsThroughSandbox()
    {
        var handler = new SandboxRecordingHandler();
        var manager = new CallSandboxManager(
            new SingleClientHttpClientFactory(handler),
            Options.Create(new CallSandboxOptions
            {
                Enabled = true,
                SubscriptionId = "sub",
                ResourceGroup = "rg",
                SandboxGroup = "sg",
                Credential = new FakeSandboxTokenCredential(),
            }),
            TestTelemetry.Sandbox,
            CallSandboxManagerTests.TestCallId,
            NullLoggerFactory.Instance);
        var tools = new CodeInterpreterTools(manager);

        var result = await tools.ExecutePythonAsync("print('hello')", TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("hello\n", result.Stdout);
        await manager.DisposeAsync();
    }
}

// ---- Test doubles -------------------------------------------------------------------------

internal sealed record RecordedSandboxRequest(HttpMethod Method, Uri Uri, string Body, string? Authorization);

internal sealed class SandboxRecordingHandler : HttpMessageHandler
{
    public List<RecordedSandboxRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedSandboxRequest(request.Method, request.RequestUri!, body, request.Headers.Authorization?.ToString()));

        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Put && path.EndsWith("/sandboxes", StringComparison.Ordinal))
        {
            return Json("{\"id\":\"sbx-1\"}");
        }
        if (request.Method == HttpMethod.Post && path.EndsWith("/executeShellCommand", StringComparison.Ordinal))
        {
            return Json("{\"stdout\":\"hello\\n\",\"stderr\":\"\",\"exitCode\":0}");
        }
        if (request.Method == HttpMethod.Delete)
        {
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string content) =>
        new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
}

internal sealed class SingleClientHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

internal sealed class FakeSandboxTokenCredential : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new("fake-token", DateTimeOffset.UtcNow.AddHours(1));

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new(GetToken(requestContext, cancellationToken));
}
