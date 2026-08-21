using System.Net;
using System.Text;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Coordination;
using Agents.AI.ContactCenter.Coordination.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Tests.Coordination;

public class WebhookForwarderTests
{
    private const string CallbackPath = "/automation/callbacks/server-call-123";
    private const string ContentType = "application/cloudevents+json";

    private static readonly byte[] sampleBody = Encoding.UTF8.GetBytes("[{\"type\":\"Microsoft.Communication.RecognizeCompleted\"}]");

    // ---------- NullWebhookForwarder ----------

    [Fact]
    public async Task Null_Forwarder_Returns_LocalOwner_When_Owner_Matches_Identity()
    {
        var identity = new MutableClusterIdentity { ClusterId = "c-1", PodId = "pod-A" };
        var forwarder = new NullWebhookForwarder(identity);
        var owner = CreateOwner("c-1", "pod-A");

        var result = await forwarder.TryForwardAsync(owner, CallbackPath, sampleBody, ContentType);

        Assert.Equal(WebhookForwardOutcome.LocalOwner, result.Outcome);
        Assert.True(result.IsSuccess is false);
        Assert.Null(result.StatusCode);
    }

    [Fact]
    public async Task Null_Forwarder_Returns_OwnerUnreachable_For_Foreign_Pod()
    {
        var identity = new MutableClusterIdentity { ClusterId = "c-1", PodId = "pod-A" };
        var forwarder = new NullWebhookForwarder(identity);
        var owner = CreateOwner("c-1", "pod-B");

        var result = await forwarder.TryForwardAsync(owner, CallbackPath, sampleBody, ContentType);

        Assert.Equal(WebhookForwardOutcome.OwnerUnreachable, result.Outcome);
    }

    [Fact]
    public async Task Null_Forwarder_Respects_Cancellation()
    {
        var forwarder = new NullWebhookForwarder(new MutableClusterIdentity());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => forwarder.TryForwardAsync(CreateOwner("c", "p"), CallbackPath, sampleBody, ContentType, headers: null, cts.Token));
    }

    // ---------- URL building ----------

    [Fact]
    public void BuildTargetUri_Composes_Headless_Service_Dns()
    {
        var settings = new WebhookForwarderOptions
        {
            HeadlessServiceName = "voice-agent-headless",
            Namespace = "voice-agents",
            Port = 8080,
            Scheme = "http",
            ForwardPath = "/automation/callbacks/forward",
        };

        var uri = HttpWebhookForwarder.BuildTargetUri("voice-agent-7", settings);

        Assert.Equal("http", uri.Scheme);
        Assert.Equal("voice-agent-7.voice-agent-headless.voice-agents.svc.cluster.local", uri.Host);
        Assert.Equal(8080, uri.Port);
        Assert.Equal("/automation/callbacks/forward", uri.AbsolutePath);
    }

    [Fact]
    public void BuildTargetUri_Honors_Custom_Cluster_Domain()
    {
        var settings = new WebhookForwarderOptions
        {
            HeadlessServiceName = "svc",
            Namespace = "ns",
            ClusterDomain = "svc.example.internal",
            Port = 8080,
        };

        var uri = HttpWebhookForwarder.BuildTargetUri("pod-x", settings);

        Assert.Equal("pod-x.svc.ns.svc.example.internal", uri.Host);
    }

    // ---------- HttpWebhookForwarder ----------

    [Fact]
    public async Task Http_Forwarder_Returns_LocalOwner_Without_Sending_When_Owner_Is_Local()
    {
        var harness = CreateHarness();
        var owner = CreateOwner(harness.Identity.ClusterId, harness.Identity.PodId);

        var result = await harness.Forwarder.TryForwardAsync(owner, CallbackPath, sampleBody, ContentType);

        Assert.Equal(WebhookForwardOutcome.LocalOwner, result.Outcome);
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task Http_Forwarder_Blocks_Cross_Cluster_Forwards()
    {
        var harness = CreateHarness();
        var owner = CreateOwner(clusterId: "other-cluster", podId: "pod-B");

        var result = await harness.Forwarder.TryForwardAsync(owner, CallbackPath, sampleBody, ContentType);

        Assert.Equal(WebhookForwardOutcome.CrossClusterBlocked, result.Outcome);
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task Http_Forwarder_Returns_OwnerUnreachable_When_Not_Configured()
    {
        var harness = CreateHarness(options =>
        {
            options.WebhookForwarder.HeadlessServiceName = string.Empty;
            options.WebhookForwarder.Namespace = string.Empty;
        });

        var result = await harness.Forwarder.TryForwardAsync(
            CreateOwner(harness.Identity.ClusterId, "pod-B"), CallbackPath, sampleBody, ContentType);

        Assert.Equal(WebhookForwardOutcome.OwnerUnreachable, result.Outcome);
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task Http_Forwarder_Posts_Body_And_Forwarding_Headers_To_Owner_Pod()
    {
        var harness = CreateHarness();
        harness.Handler.SetResponses(new HttpResponseMessage(HttpStatusCode.OK));

        var owner = CreateOwner(harness.Identity.ClusterId, "pod-B");
        var extra = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["traceparent"] = "00-abcdef0123456789abcdef0123456789-0123456789abcdef-01",
        };

        var result = await harness.Forwarder.TryForwardAsync(owner, CallbackPath, sampleBody, ContentType, extra);

        Assert.Equal(WebhookForwardOutcome.Forwarded, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Equal(200, result.StatusCode);

        var sent = Assert.Single(harness.Handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("http", sent.Uri.Scheme);
        Assert.Equal("pod-b.voice-agent-headless.voice-agents.svc.cluster.local", sent.Uri.Host);
        Assert.Equal(8080, sent.Uri.Port);
        Assert.Equal("/automation/callbacks/forward", sent.Uri.AbsolutePath);
        Assert.Equal(ContentType, sent.ContentType);
        Assert.Equal(sampleBody, sent.Body);
        Assert.Equal(CallbackPath, sent.Headers[HttpWebhookForwarder.ForwardedPathHeader]);
        Assert.Equal(harness.Identity.InstanceId, sent.Headers[HttpWebhookForwarder.ForwardedByHeader]);
        Assert.Equal(extra["traceparent"], sent.Headers["traceparent"]);
    }

    [Fact]
    public async Task Http_Forwarder_Retries_Transient_5xx_Until_Success()
    {
        var harness = CreateHarness();
        harness.Handler.SetResponses(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            new HttpResponseMessage(HttpStatusCode.Accepted));

        var result = await harness.Forwarder.TryForwardAsync(
            CreateOwner(harness.Identity.ClusterId, "pod-B"), CallbackPath, sampleBody, ContentType);

        Assert.Equal(WebhookForwardOutcome.Forwarded, result.Outcome);
        Assert.Equal(202, result.StatusCode);
        Assert.Equal(2, harness.Handler.Requests.Count);
    }

    [Fact]
    public async Task Http_Forwarder_Does_Not_Retry_Permanent_4xx()
    {
        var harness = CreateHarness();
        harness.Handler.SetResponses(
            new HttpResponseMessage(HttpStatusCode.NotFound),
            new HttpResponseMessage(HttpStatusCode.OK));

        var result = await harness.Forwarder.TryForwardAsync(
            CreateOwner(harness.Identity.ClusterId, "pod-B"), CallbackPath, sampleBody, ContentType);

        Assert.Equal(WebhookForwardOutcome.RemoteRejected, result.Outcome);
        Assert.Equal(404, result.StatusCode);
        Assert.Single(harness.Handler.Requests);
    }

    [Fact]
    public async Task Http_Forwarder_Returns_RemoteRejected_After_Persistent_5xx()
    {
        var harness = CreateHarness(options => options.WebhookForwarder.MaxRetryAttempts = 1);
        harness.Handler.SetResponses(
            new HttpResponseMessage(HttpStatusCode.BadGateway),
            new HttpResponseMessage(HttpStatusCode.BadGateway));

        var result = await harness.Forwarder.TryForwardAsync(
            CreateOwner(harness.Identity.ClusterId, "pod-B"), CallbackPath, sampleBody, ContentType);

        Assert.Equal(WebhookForwardOutcome.RemoteRejected, result.Outcome);
        Assert.Equal(502, result.StatusCode);
        Assert.Equal(2, harness.Handler.Requests.Count);
    }

    [Fact]
    public async Task Http_Forwarder_Returns_OwnerUnreachable_When_Transport_Always_Fails()
    {
        var harness = CreateHarness(options => options.WebhookForwarder.MaxRetryAttempts = 2);
        harness.Handler.AlwaysThrow(new HttpRequestException("connection refused"));

        var result = await harness.Forwarder.TryForwardAsync(
            CreateOwner(harness.Identity.ClusterId, "pod-B"), CallbackPath, sampleBody, ContentType);

        Assert.Equal(WebhookForwardOutcome.OwnerUnreachable, result.Outcome);
        Assert.Null(result.StatusCode);
        Assert.Equal(3, harness.Handler.Requests.Count);
    }

    [Fact]
    public async Task Http_Forwarder_Honors_Cancellation()
    {
        var harness = CreateHarness();
        harness.Handler.OnRequest(_ => throw new InvalidOperationException("handler should not run"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            harness.Forwarder.TryForwardAsync(
                CreateOwner(harness.Identity.ClusterId, "pod-B"), CallbackPath, sampleBody, ContentType, headers: null, cts.Token));

        Assert.Empty(harness.Handler.Requests);
    }

    // ---------- helpers ----------

    private static CallOwnership CreateOwner(string clusterId, string podId, CallOwnershipKind kind = CallOwnershipKind.Streaming)
        => new(clusterId, podId, InstanceId: Guid.NewGuid().ToString("N"), kind, LeaseUntil: DateTimeOffset.UtcNow.AddMinutes(1));

    private static Harness CreateHarness(Action<HyperscaleOptions>? configure = null)
    {
        var options = new HyperscaleOptions
        {
            WebhookForwarder = new WebhookForwarderOptions
            {
                HeadlessServiceName = "voice-agent-headless",
                Namespace = "voice-agents",
                ClusterDomain = "svc.cluster.local",
                Port = 8080,
                Scheme = "http",
                ForwardPath = "/automation/callbacks/forward",
                AttemptTimeout = TimeSpan.FromSeconds(2),
                MaxRetryAttempts = 2,
                RetryDelay = TimeSpan.Zero,
            },
        };
        configure?.Invoke(options);

        var identity = new MutableClusterIdentity
        {
            ClusterId = "c-1",
            PodId = "pod-A",
            InstanceId = Guid.NewGuid().ToString("N"),
        };

        var handler = new RecordingHandler();
        var httpClient = new HttpClient(handler);
        var forwarder = new HttpWebhookForwarder(
            httpClient,
            identity,
            new TestOptionsMonitor<HyperscaleOptions>(options),
            NullLogger<HttpWebhookForwarder>.Instance);

        return new Harness(forwarder, handler, identity);
    }

    private sealed record Harness(HttpWebhookForwarder Forwarder, RecordingHandler Handler, MutableClusterIdentity Identity);

    private sealed class MutableClusterIdentity : IClusterIdentity
    {
        public string ClusterId { get; set; } = "cluster-1";
        public string PodId { get; set; } = "pod-1";
        public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? ContentType, byte[] Body, IReadOnlyDictionary<string, string> Headers);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<RecordedRequest> _requests = new();
        private readonly Queue<HttpResponseMessage> _responses = new();
        private Exception? _alwaysThrow;
        private Action<HttpRequestMessage>? _hook;

        public IReadOnlyList<RecordedRequest> Requests => _requests;

        public void SetResponses(params HttpResponseMessage[] responses)
        {
            _responses.Clear();
            foreach (var r in responses)
            {
                _responses.Enqueue(r);
            }
        }

        public void AlwaysThrow(Exception exception) => _alwaysThrow = exception;

        public void OnRequest(Action<HttpRequestMessage> hook) => _hook = hook;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _hook?.Invoke(request);

            var body = request.Content is null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, values) in request.Headers)
            {
                headers[key] = string.Join(",", values);
            }
            if (request.Content is not null)
            {
                foreach (var (key, values) in request.Content.Headers)
                {
                    headers[key] = string.Join(",", values);
                }
            }

            _requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Content?.Headers.ContentType?.ToString(),
                body,
                headers));

            if (_alwaysThrow is not null)
            {
                throw _alwaysThrow;
            }

            if (_responses.Count == 0)
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return _responses.Dequeue();
        }
    }
}
