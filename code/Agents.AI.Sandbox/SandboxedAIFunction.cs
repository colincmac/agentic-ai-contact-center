using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.Sandbox;

/// <summary>
/// <see cref="DelegatingAIFunction"/> that redirects execution of a tool marked with
/// <see cref="SandboxedToolAttribute"/> into the call's hardware-isolated sandbox. The inner
/// function's name, description, and JSON schema are forwarded unchanged (so the model-facing
/// tool surface is identical), but its local .NET body is never invoked — arguments are
/// marshaled as JSON to the sandbox-side runner via <see cref="ICallSandbox.InvokeToolAsync"/>.
/// </summary>
/// <remarks>
/// Composed inside <c>AuthorizingAIAgent.ApplyToolMiddleware</c> as the inner function wrapped
/// by <c>AuthorizingAIFunction</c>, so authorization/approval still run in-process before
/// execution leaves the pod. Fails closed (returns an "unavailable" message) when sandboxing
/// is not enabled — it never falls back to in-process execution. See ADR-0013.
/// </remarks>
public sealed class SandboxedAIFunction : DelegatingAIFunction
{
    private readonly SandboxedToolAttribute _attribute;
    private readonly IServiceProvider? _scopedServices;

    private SandboxedAIFunction(AIFunction innerFunction, SandboxedToolAttribute attribute, IServiceProvider? scopedServices)
        : base(innerFunction)
    {
        _attribute = attribute;
        _scopedServices = scopedServices;
    }

    /// <summary>
    /// Wraps <paramref name="function"/> when its underlying method carries a
    /// <see cref="SandboxedToolAttribute"/>; otherwise returns <see langword="null"/>.
    /// </summary>
    public static SandboxedAIFunction? TryWrap(AIFunction function, IServiceProvider? scopedServices)
    {
        ArgumentNullException.ThrowIfNull(function);

        var attribute = function.UnderlyingMethod?
            .GetCustomAttributes(typeof(SandboxedToolAttribute), inherit: true)
            .OfType<SandboxedToolAttribute>()
            .FirstOrDefault();

        return attribute is null ? null : new SandboxedAIFunction(function, attribute, scopedServices);
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var services = arguments.Services ?? _scopedServices;
        var manager = services?.GetService<ICallSandboxManager>();
        var logger = services?.GetService<ILoggerFactory>()?.CreateLogger<SandboxedAIFunction>() ?? NullLogger<SandboxedAIFunction>.Instance;

        if (manager is null || !manager.IsEnabled)
        {
            logger.LogWarning("Sandboxed tool '{Tool}' invoked but no enabled sandbox is available; failing closed.", Name);
            return "This tool is currently unavailable.";
        }

        var toolName = string.IsNullOrWhiteSpace(_attribute.ToolName) ? Name : _attribute.ToolName!;
        var argumentsJson = SerializeArguments(arguments);

        try
        {
            var sandbox = await manager.EnsureAsync(cancellationToken).ConfigureAwait(false);
            var result = await sandbox.InvokeToolAsync(_attribute.RunnerCommand, toolName, argumentsJson, cancellationToken).ConfigureAwait(false);
            return result.Succeeded
                ? result.Stdout
                : $"Tool '{toolName}' failed: {(string.IsNullOrWhiteSpace(result.Stderr) ? "no output" : result.Stderr)}";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sandboxed tool '{Tool}' execution failed.", toolName);
            return $"Tool '{toolName}' failed: {ex.Message}";
        }
    }

    private static string SerializeArguments(AIFunctionArguments arguments)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kvp in arguments)
        {
            map[kvp.Key] = kvp.Value;
        }

        return JsonSerializer.Serialize(map, AIJsonUtilities.DefaultOptions);
    }
}
