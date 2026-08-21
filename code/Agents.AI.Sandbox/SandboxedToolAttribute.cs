namespace Agents.AI.Sandbox;

/// <summary>
/// Marks an AI tool method whose implementation runs inside the per-call, hardware-isolated
/// (microVM) sandbox rather than in-process. When present, <see cref="SandboxedAIFunction"/>
/// redirects invocation to the sandbox-side runner via <see cref="ICallSandbox.InvokeToolAsync"/>;
/// the local .NET body is never executed.
/// </summary>
/// <remarks>
/// Use this for tools the host trusts less than its own pod — third-party / partner MCP
/// servers, document parsing on caller-uploaded content, or any tool that processes
/// untrusted input. Authorization and approval still run in-process (the decorator is wrapped
/// by <c>AuthorizingAIFunction</c>) before execution leaves the pod. See ADR-0013.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SandboxedToolAttribute : Attribute
{
    /// <param name="toolName">
    /// Optional sandbox-side tool name to dispatch to. Defaults to the AI function's name.
    /// </param>
    public SandboxedToolAttribute(string? toolName = null) => ToolName = toolName;

    /// <summary>The sandbox-side tool name, or <see langword="null"/> to use the function name.</summary>
    public string? ToolName { get; }

    /// <summary>
    /// Operator-configured entrypoint that dispatches to the sandbox-side tool. Receives the
    /// base64 tool name and base64 arguments JSON as two positional arguments.
    /// </summary>
    public string RunnerCommand { get; set; } = "python3 /tools/run.py";
}
