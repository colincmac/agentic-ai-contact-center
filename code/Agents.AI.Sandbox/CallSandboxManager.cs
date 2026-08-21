using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agents.AI.Sandbox;

/// <summary>
/// Default scoped <see cref="ICallSandboxManager"/>. Lazily provisions one
/// <see cref="CallSandbox"/> per call and disposes it with the call scope.
/// </summary>
public sealed class CallSandboxManager : ICallSandboxManager
{
    /// <summary>Named <see cref="HttpClient"/> used for ADC data-plane calls.</summary>
    public const string HttpClientName = "CallSandbox";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CallSandboxOptions _options;
    private readonly string _sessionId;
    private readonly SandboxTelemetry _telemetry;   
    private readonly ILoggerFactory _loggerFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ICallSandbox? _sandbox;
    private int _disposed;

    public CallSandboxManager(
        IHttpClientFactory httpClientFactory,
        IOptions<CallSandboxOptions> options,
        SandboxTelemetry telemetry,
        string sessionId,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _telemetry = telemetry;
        _loggerFactory = loggerFactory;
        _sessionId = sessionId;
    }

    public bool IsEnabled => _options.Enabled;

    public async ValueTask<ICallSandbox> EnsureAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        if (!_options.Enabled)
        {
            throw new InvalidOperationException(
                "Sandboxed tool execution is not enabled. Configure the 'CallSandbox' section and call AddCallSandbox(), or check ICallSandboxManager.IsEnabled before invoking.");
        }

        if (_sandbox is not null)
        {
            await _sandbox.EnsureProvisionedAsync(cancellationToken).ConfigureAwait(false);
            return _sandbox;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sandbox is null)
            {
                var http = _httpClientFactory.CreateClient(HttpClientName);
                _sandbox = new CallSandbox(http, _options, _sessionId, _telemetry, _loggerFactory.CreateLogger<CallSandbox>());
            }
        }
        finally
        {
            _gate.Release();
        }

        await _sandbox.EnsureProvisionedAsync(cancellationToken).ConfigureAwait(false);
        return _sandbox;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_sandbox is not null)
        {
            await _sandbox.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }
}
