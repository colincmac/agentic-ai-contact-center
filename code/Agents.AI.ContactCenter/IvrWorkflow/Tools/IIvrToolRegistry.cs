using System.Collections.Frozen;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.IvrWorkflow.Tools;

/// <summary>
/// Per-agent catalog of <see cref="AIFunction"/> bindings referenced by name from
/// <see cref="Blueprint.WorkflowBlueprint.CommonToolNames"/>,
/// <see cref="Blueprint.StageBlueprint.ToolNames"/>, and
/// <see cref="Blueprint.StageRealtimePrompt.ToolNames"/>.
/// </summary>
/// <remarks>
/// <para>
/// The registry is keyed by the same DI service key that resolves the agent
/// (e.g. <c>AgentConfig.TriageAgent</c>). This mirrors the Microsoft Agent
/// Framework convention where tools live alongside the keyed
/// <see cref="Microsoft.Agents.AI.AIAgent"/> they are intended for, without
/// requiring a one-agent-per-stage model: a single realtime agent is reused
/// across stages, with its prompt and tool surface re-projected on each
/// transition from the per-stage <see cref="ToolBinding"/> list.
/// </para>
/// <para>
/// Registrations are immutable once <see cref="IvrToolRegistry"/> is built;
/// the <see cref="WorkflowGraphCompiler"/> resolves every blueprint tool name
/// against the registry at host startup so missing names fail fast in
/// <see cref="WorkflowCompilationException"/> instead of mid-call.
/// </para>
/// </remarks>
public interface IIvrToolRegistry
{
    /// <summary>Returns the <see cref="AITool"/> registered under <paramref name="name"/>, or <see langword="false"/> if no binding exists.</summary>
    bool TryGetBinding(string name, out AITool? binding);

    /// <summary>Every registered tool name. Diagnostic; ordering matches first-insertion order.</summary>
    IReadOnlyCollection<string> Names { get; }
}

/// <summary>
/// Default immutable <see cref="IIvrToolRegistry"/>.
/// </summary>
internal sealed class IvrToolRegistry : IIvrToolRegistry
{
    private readonly FrozenDictionary<string, AITool> _tools;

    public IvrToolRegistry(IEnumerable<AITool> tools)
    {

        _tools = tools.ToFrozenDictionary(b => b.Name, StringComparer.Ordinal);
    }


    public bool TryGetBinding(string name, out AITool? tool) =>
        _tools.TryGetValue(name, out tool);

    public IReadOnlyCollection<string> Names => _tools.Keys;
}

