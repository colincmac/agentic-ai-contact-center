using System.ComponentModel;
using Agents.AI.Extensions.AITools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.Sandbox;

/// <summary>
/// Exposes a code-interpreter tool that runs model-generated Python inside the call's
/// hardware-isolated (microVM) sandbox via <see cref="ICallSandboxManager"/>. Resolve from
/// the per-call DI scope (it depends on the scoped sandbox manager), exactly like
/// <see cref="AITools.CallControlTools"/>. Register with
/// <see cref="DependencyInjection.CallSessionContainerBuilder.AddCodeInterpreterTool"/>.
/// </summary>
public sealed class CodeInterpreterTools : IAIToolCollection
{
    /// <summary>Stable tool name used in YAML workflows for the code-interpreter verb.</summary>
    public const string ExecutePythonToolName = "execute_python";

    private readonly ICallSandboxManager _sandboxManager;
    private readonly ILogger<CodeInterpreterTools> _logger;
    private readonly AIFunction _executePython;

    public CodeInterpreterTools(
        ICallSandboxManager sandboxManager,
        ILogger<CodeInterpreterTools>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(sandboxManager);
        _sandboxManager = sandboxManager;
        _logger = logger ?? NullLogger<CodeInterpreterTools>.Instance;
        _executePython = AIFunctionFactory.Create(ExecutePythonAsync, ExecutePythonToolName);
    }

    public IEnumerable<AITool> AsAITools() => [_executePython];

    [Description(
        "Execute a short Python 3 snippet in an isolated, per-call sandbox and return its " +
        "stdout and stderr. Use this for arithmetic, data parsing, formatting, or other " +
        "deterministic computation. Print results to stdout. Do not assume network access.")]
    public async Task<CodeExecutionResult> ExecutePythonAsync(
        [Description("The Python 3 source code to execute. Print any result you need to stdout.")]
        string code,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return new CodeExecutionResult(false, string.Empty, "No code was provided.", -1);
        }

        if (!_sandboxManager.IsEnabled)
        {
            return new CodeExecutionResult(false, string.Empty, "Code execution is currently unavailable.", -1);
        }

        try
        {
            var sandbox = await _sandboxManager.EnsureAsync(cancellationToken).ConfigureAwait(false);
            var result = await sandbox.ExecuteCodeAsync("python", code, cancellationToken).ConfigureAwait(false);
            return new CodeExecutionResult(result.Succeeded, result.Stdout, result.Stderr, result.ExitCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sandboxed code execution failed");
            return new CodeExecutionResult(false, string.Empty, $"Execution failed: {ex.Message}", -1);
        }
    }
}

/// <summary>Result returned to the model from <see cref="CodeInterpreterTools.ExecutePythonAsync"/>.</summary>
/// <param name="Succeeded">True when the snippet exited with code 0.</param>
/// <param name="Stdout">Captured standard output.</param>
/// <param name="Stderr">Captured standard error (or a failure reason).</param>
/// <param name="ExitCode">Process exit code (-1 when the snippet never ran).</param>
public readonly record struct CodeExecutionResult(bool Succeeded, string Stdout, string Stderr, int ExitCode);
