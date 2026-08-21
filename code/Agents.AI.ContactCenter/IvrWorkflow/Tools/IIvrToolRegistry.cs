using System.Collections.Frozen;
using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.IvrWorkflow.Tools;

/// <summary>
/// Per-call catalog of <see cref="AIFunction"/> bindings referenced by name from
/// <see cref="Blueprint.WorkflowBlueprint.CommonToolNames"/>,
/// <see cref="Blueprint.StageBlueprint.ToolNames"/>, and
/// <see cref="Blueprint.StageRealtimePrompt.ToolNames"/>.
/// </summary>
/// <remarks>
/// <para>
/// Registrations are immutable once <see cref="IvrToolRegistry"/> is built;
/// startup validation checks every compiled blueprint tool name against a temporary scoped
/// registry, while each call receives its own registry and concrete tool instances. Missing names fail fast in
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
        ArgumentNullException.ThrowIfNull(tools);

        var materialized = tools.ToList();
        var invalidNames = materialized
            .Where(static tool => string.IsNullOrWhiteSpace(tool.Name))
            .ToList();
        if (invalidNames.Count > 0)
        {
            throw new InvalidOperationException(
                $"Every IVR tool must have a non-empty name; found {invalidNames.Count} unnamed registration(s).");
        }

        var duplicates = materialized
            .GroupBy(static tool => tool.Name, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate IVR tool name registration(s): {string.Join(", ", duplicates.Select(static name => $"'{name}'"))}. " +
                "Tool names must be unique within a call scope.");
        }

        _tools = materialized.ToFrozenDictionary(static tool => tool.Name, StringComparer.Ordinal);
    }
    public bool TryGetBinding(string name, out AITool? tool) =>
        _tools.TryGetValue(name, out tool);

    public IReadOnlyCollection<string> Names => _tools.Keys;
}

