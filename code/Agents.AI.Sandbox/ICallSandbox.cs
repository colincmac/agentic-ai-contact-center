namespace Agents.AI.Sandbox;

/// <summary>
/// Result of executing a snippet inside a per-call sandbox.
/// </summary>
/// <param name="Succeeded">True when the process exited with code 0.</param>
/// <param name="Stdout">Captured standard output.</param>
/// <param name="Stderr">Captured standard error.</param>
/// <param name="ExitCode">Process exit code.</param>
public readonly record struct SandboxExecutionResult(bool Succeeded, string Stdout, string Stderr, int ExitCode);

/// <summary>
/// A per-call, hardware-isolated (microVM) execution environment on the Azure Container
/// Apps Sandboxes (ADC) data plane. One instance is provisioned per call and reused across
/// all tool invocations on that call. Provisioning is lazy — the first call to
/// <see cref="EnsureProvisionedAsync"/> (or any execute method) creates the sandbox.
/// </summary>
/// <remarks>
/// This abstraction deliberately hides the <b>preview</b> ADC data-plane contract so that
/// preview-to-GA churn is isolated to <c>CallSandbox</c> (see ADR-0013).
/// </remarks>
public interface ICallSandbox : IAsyncDisposable
{
    /// <summary>The call this sandbox is bound to (used for telemetry and naming).</summary>
    string CallId { get; }

    /// <summary>Whether the underlying microVM has been provisioned.</summary>
    bool IsProvisioned { get; }

    /// <summary>
    /// Ensures the underlying microVM exists, provisioning it on first call. Subsequent
    /// calls are no-ops.
    /// </summary>
    ValueTask EnsureProvisionedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a code snippet inside the sandbox and returns its captured output. The
    /// snippet is marshaled as data (never interpolated into a shell command).
    /// </summary>
    /// <param name="language">Snippet language. Currently <c>python</c> is supported.</param>
    /// <param name="code">The source to execute.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<SandboxExecutionResult> ExecuteCodeAsync(string language, string code, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invokes a named tool implemented by a runner pre-installed in the sandbox disk image.
    /// The tool name and its JSON arguments are marshaled as data (base64) — never
    /// interpolated into a shell command — so model-supplied arguments cannot inject shell.
    /// </summary>
    /// <param name="runnerCommand">
    /// Operator-configured entrypoint that dispatches to the sandbox-side tool (e.g.
    /// <c>python3 /tools/run.py</c>). Receives the base64 tool name and base64 arguments JSON
    /// as two positional arguments.
    /// </param>
    /// <param name="toolName">The sandbox-side tool to dispatch to.</param>
    /// <param name="argumentsJson">The tool arguments serialized as a JSON object.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<SandboxExecutionResult> InvokeToolAsync(string runnerCommand, string toolName, string argumentsJson, CancellationToken cancellationToken = default);
}
