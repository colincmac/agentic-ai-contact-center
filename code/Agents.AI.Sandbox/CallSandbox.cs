using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.Sandbox;

/// <summary>
/// <see cref="ICallSandbox"/> implementation over the Azure Container Apps Sandboxes (ADC)
/// preview data plane. Provisions a microVM with <c>PUT /sandboxes</c>, runs snippets with
/// <c>POST /sandboxes/{id}/executeShellCommand</c>, and tears the sandbox down with
/// <c>DELETE /sandboxes/{id}</c> on dispose.
/// </summary>
internal sealed class CallSandbox : ICallSandbox
{
    private static readonly TimeSpan tokenRefreshSkew = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly CallSandboxOptions _options;
    private readonly SandboxTelemetry _telemetry;
    private readonly ILogger _logger;
    private readonly string _scope;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AccessToken _cachedToken;
    private string? _sandboxId;
    private int _disposed;

    public CallSandbox(
        HttpClient http,
        CallSandboxOptions options,
        string callId,
        SandboxTelemetry telemetry,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);

        options.Validate();

        _http = http;
        _options = options;
        CallId = callId;
        _telemetry = telemetry;
        _logger = logger ?? NullLogger.Instance;
        _scope = options.BuildSandboxGroupScope();
    }

    public string CallId { get; }

    public bool IsProvisioned => _sandboxId is not null;

    public async ValueTask EnsureProvisionedAsync(CancellationToken cancellationToken = default)
    {
        if (_sandboxId is not null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sandboxId is not null)
            {
                return;
            }

            using var span = _telemetry.StartChildActivity("contact_center.sandbox.provision", CallId);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ProvisionTimeout);

            try
            {
                var body = new JsonObject
                {
                    ["sourcesRef"] = new JsonObject
                    {
                        ["diskImage"] = new JsonObject
                        {
                            ["name"] = _options.DiskImageName,
                            ["isPublic"] = _options.DiskImageIsPublic,
                        },
                    },
                    ["resources"] = new JsonObject
                    {
                        ["cpu"] = _options.Cpu,
                        ["memory"] = _options.Memory,
                    },
                };

                if (!string.IsNullOrWhiteSpace(_options.AgentIdentityResourceId))
                {
                    body["agentIdentity"] = new JsonObject { ["resourceId"] = _options.AgentIdentityResourceId };
                }
                if (!string.IsNullOrWhiteSpace(_options.Region))
                {
                    body["location"] = _options.Region;
                }
                if (_options.EgressDenyByDefault)
                {
                    var allowed = new JsonArray();
                    foreach (var host in _options.EgressAllowedHosts)
                    {
                        if (!string.IsNullOrWhiteSpace(host))
                        {
                            allowed.Add(host);
                        }
                    }

                    body["egress"] = new JsonObject
                    {
                        ["defaultAction"] = "Deny",
                        ["allowedHosts"] = allowed,
                    };
                }

                using var request = await CreateRequestAsync(HttpMethod.Put, $"{_scope}/sandboxes", body, timeout.Token).ConfigureAwait(false);
                using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                await EnsureSuccessAsync(response, "provision sandbox", timeout.Token).ConfigureAwait(false);

                using var doc = await ReadJsonAsync(response, timeout.Token).ConfigureAwait(false);
                _sandboxId = ReadString(doc.RootElement, "id") ?? ReadString(doc.RootElement, "name")
                    ?? throw new InvalidOperationException("Sandbox provisioning response did not include an id.");

                _logger.LogInformation("Provisioned sandbox {SandboxId} for call {CallId}", _sandboxId, CallId);
            }
            catch (Exception ex)
            {
                SandboxTelemetry.SetError(span, ex);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<SandboxExecutionResult> ExecuteCodeAsync(string language, string code, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentNullException.ThrowIfNull(code);

        var command = BuildExecutionCommand(language, code);
        return await ExecuteShellAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SandboxExecutionResult> InvokeToolAsync(string runnerCommand, string toolName, string argumentsJson, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runnerCommand);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(argumentsJson);

        var command = BuildToolRunnerCommand(runnerCommand, toolName, argumentsJson);
        return await ExecuteShellAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<SandboxExecutionResult> ExecuteShellAsync(string command, CancellationToken cancellationToken)
    {
        await EnsureProvisionedAsync(cancellationToken).ConfigureAwait(false);

        using var span = _telemetry.StartChildActivity("contact_center.sandbox.execute", CallId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ExecutionTimeout);

        try
        {
            var body = new JsonObject { ["command"] = command };

            using var request = await CreateRequestAsync(
                HttpMethod.Post, $"{_scope}/sandboxes/{_sandboxId}/executeShellCommand", body, timeout.Token).ConfigureAwait(false);
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "execute in sandbox", timeout.Token).ConfigureAwait(false);

            using var doc = await ReadJsonAsync(response, timeout.Token).ConfigureAwait(false);
            var stdout = ReadString(doc.RootElement, "stdout") ?? string.Empty;
            var stderr = ReadString(doc.RootElement, "stderr") ?? string.Empty;
            var exitCode = ReadInt(doc.RootElement, "exitCode") ?? 0;

            return new SandboxExecutionResult(exitCode == 0, stdout, stderr, exitCode);
        }
        catch (Exception ex)
        {
            SandboxTelemetry.SetError(span, ex);
            throw;
        }
    }

    /// <summary>
    /// Builds an injection-safe shell command. The snippet is base64-encoded — whose
    /// alphabet contains no shell metacharacters — decoded to a temp file inside the
    /// sandbox, then executed. The model-supplied code is never interpolated as shell text.
    /// </summary>
    private static string BuildExecutionCommand(string language, string code)
    {
        var interpreter = language.ToLowerInvariant() switch
        {
            "python" or "python3" or "py" => "python3",
            _ => throw new NotSupportedException($"Sandbox language '{language}' is not supported."),
        };

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(code));
        var file = $"/tmp/cc_snippet_{Guid.NewGuid():N}.py";
        return $"set -e; printf '%s' '{encoded}' | base64 -d > {file} && {interpreter} {file}";
    }

    /// <summary>
    /// Builds an injection-safe tool-runner command. The tool name and arguments JSON are
    /// base64-encoded and passed as single-quoted positional arguments to the operator-configured
    /// runner; base64's alphabet contains no shell metacharacters or single quotes, so
    /// model-supplied arguments cannot break out of the quoting.
    /// </summary>
    private static string BuildToolRunnerCommand(string runnerCommand, string toolName, string argumentsJson)
    {
        var nameEncoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(toolName));
        var argsEncoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(argumentsJson));
        return $"{runnerCommand} '{nameEncoded}' '{argsEncoded}'";
    }

    private async ValueTask<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string url, JsonObject body, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_cachedToken.Token is not null && _cachedToken.ExpiresOn > DateTimeOffset.UtcNow + tokenRefreshSkew)
        {
            return _cachedToken.Token;
        }

        var context = new TokenRequestContext([_options.TokenScope]);
        _cachedToken = await _options.Credential.GetTokenAsync(context, cancellationToken).ConfigureAwait(false);
        return _cachedToken.Token;
    }

    private static async ValueTask EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException($"Failed to {operation}: {(int)response.StatusCode} {response.ReasonPhrase}. {detail}");
    }

    private static async ValueTask<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var sandboxId = _sandboxId;
        if (sandboxId is not null)
        {
            using var span = _telemetry.StartChildActivity("contact_center.sandbox.dispose", CallId);
            try
            {
                using var timeout = new CancellationTokenSource(_options.ProvisionTimeout);
                using var request = await CreateRequestAsync(HttpMethod.Delete, $"{_scope}/sandboxes/{sandboxId}", new JsonObject(), timeout.Token).ConfigureAwait(false);
                request.Content = null;
                using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Best-effort sandbox teardown for {SandboxId} returned {Status}", sandboxId, (int)response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                // Teardown is best-effort; the ADC idle reaper will reclaim the sandbox.
                SandboxTelemetry.SetError(span, ex);
                _logger.LogWarning(ex, "Best-effort sandbox teardown failed for {SandboxId} (call {CallId})", sandboxId, CallId);
            }
        }

        _gate.Dispose();
    }
}
