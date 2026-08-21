using System.Net;
using System.Net.Http.Headers;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Coordination.Core;

/// <summary>
/// HTTP-based <see cref="IWebhookForwarder"/> that targets a peer pod via
/// its headless-service DNS name per ADR-0011. Adds a tiny in-process retry
/// loop on transient transport / 5xx responses; the retry budget stays
/// small so the forwarding hop fits comfortably inside the answer-window
/// SLA from ADR-0003.
/// </summary>
public sealed class HttpWebhookForwarder : IWebhookForwarder
{
    /// <summary>
    /// Header carrying the original request path + query so the receiver
    /// can dispatch the replayed payload through its normal callback router.
    /// </summary>
    public const string ForwardedPathHeader = "X-Forwarded-Callback-Path";

    /// <summary>
    /// Header carrying the forwarding pod's <see cref="IClusterIdentity.InstanceId"/>
    /// for telemetry / loop-detection on the receiving pod.
    /// </summary>
    public const string ForwardedByHeader = "X-Forwarded-By-Instance";

    private readonly HttpClient _httpClient;
    private readonly IClusterIdentity _identity;
    private readonly IOptionsMonitor<HyperscaleOptions> _options;
    private readonly ILogger<HttpWebhookForwarder> _logger;

    public HttpWebhookForwarder(
        HttpClient httpClient,
        IClusterIdentity identity,
        IOptionsMonitor<HyperscaleOptions> options,
        ILogger<HttpWebhookForwarder> logger)
    {
        _httpClient = httpClient;
        _identity = identity;
        _options = options;
        _logger = logger;
    }

    public async Task<WebhookForwardResult> TryForwardAsync(
        CallOwnership owner,
        string callbackPath,
        ReadOnlyMemory<byte> body,
        string contentType,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        if (string.Equals(owner.ClusterId, _identity.ClusterId, StringComparison.Ordinal)
            && string.Equals(owner.PodId, _identity.PodId, StringComparison.Ordinal))
        {
            return new WebhookForwardResult(WebhookForwardOutcome.LocalOwner, StatusCode: null);
        }

        if (!string.Equals(owner.ClusterId, _identity.ClusterId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Refusing cross-cluster forward for owner cluster={OwnerCluster} pod={OwnerPod}; local cluster={LocalCluster} (ADR-0011)",
                owner.ClusterId, owner.PodId, _identity.ClusterId);
            return new WebhookForwardResult(WebhookForwardOutcome.CrossClusterBlocked, StatusCode: null);
        }

        var settings = _options.CurrentValue.WebhookForwarder;
        if (string.IsNullOrWhiteSpace(settings.HeadlessServiceName) || string.IsNullOrWhiteSpace(settings.Namespace))
        {
            _logger.LogError(
                "WebhookForwarder is not configured (HeadlessServiceName or Namespace missing); cannot forward to {OwnerPod}",
                owner.PodId);
            return new WebhookForwardResult(WebhookForwardOutcome.OwnerUnreachable, StatusCode: null);
        }

        var targetUri = BuildTargetUri(owner.PodId, settings);
        var attempts = Math.Max(1, settings.MaxRetryAttempts + 1);
        var lastStatus = (int?)null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var request = BuildRequest(targetUri, callbackPath, body, contentType, headers);
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(settings.AttemptTimeout);

                using var response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token)
                    .ConfigureAwait(false);

                lastStatus = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    return new WebhookForwardResult(WebhookForwardOutcome.Forwarded, lastStatus);
                }

                if (!IsTransient(response.StatusCode))
                {
                    return new WebhookForwardResult(WebhookForwardOutcome.RemoteRejected, lastStatus);
                }

                _logger.LogWarning(
                    "Forward attempt {Attempt}/{Max} to {Target} returned transient {Status}",
                    attempt, attempts, targetUri, lastStatus);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "Forward attempt {Attempt}/{Max} to {Target} timed out after {Timeout}",
                    attempt, attempts, targetUri, settings.AttemptTimeout);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex,
                    "Forward attempt {Attempt}/{Max} to {Target} failed at the transport layer",
                    attempt, attempts, targetUri);
            }

            if (attempt < attempts && settings.RetryDelay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(settings.RetryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
            }
        }

        return lastStatus is int status
            ? new WebhookForwardResult(WebhookForwardOutcome.RemoteRejected, status)
            : new WebhookForwardResult(WebhookForwardOutcome.OwnerUnreachable, StatusCode: null);
    }

    internal static Uri BuildTargetUri(string podId, WebhookForwarderOptions settings)
    {
        var host = $"{podId}.{settings.HeadlessServiceName}.{settings.Namespace}.{settings.ClusterDomain}";
        var builder = new UriBuilder(settings.Scheme, host, settings.Port, settings.ForwardPath);
        return builder.Uri;
    }

    private HttpRequestMessage BuildRequest(
        Uri targetUri,
        string callbackPath,
        ReadOnlyMemory<byte> body,
        string contentType,
        IReadOnlyDictionary<string, string>? headers)
    {
        var content = new ReadOnlyMemoryContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        var request = new HttpRequestMessage(HttpMethod.Post, targetUri)
        {
            Content = content,
        };
        request.Headers.TryAddWithoutValidation(ForwardedPathHeader, callbackPath);
        request.Headers.TryAddWithoutValidation(ForwardedByHeader, _identity.InstanceId);

        if (headers is not null)
        {
            foreach (var kvp in headers)
            {
                if (string.IsNullOrEmpty(kvp.Key))
                {
                    continue;
                }

                request.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
            }
        }

        return request;
    }

    private static bool IsTransient(HttpStatusCode status)
        => (int)status >= 500
            || status == HttpStatusCode.RequestTimeout
            || status == HttpStatusCode.TooManyRequests;
}
