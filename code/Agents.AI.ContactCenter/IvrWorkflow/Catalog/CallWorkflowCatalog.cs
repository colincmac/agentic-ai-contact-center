using Agents.AI.ContactCenter.IvrWorkflow.Compilation;

namespace Agents.AI.ContactCenter.IvrWorkflow.Catalog;

/// <summary>
/// Process-wide read-only catalog of compiled call workflows. Loads + compiles every workflow at host startup, so the catalog is a flat dictionary
/// keyed by <see cref="CompiledCallWorkflow.Id"/>.
/// </summary>
public interface ICallWorkflowCatalog
{
    /// <summary>Every workflow registered with the catalog. Order is registration order.</summary>
    IReadOnlyList<CompiledCallWorkflow> Workflows { get; }

    bool TryGet(string id, out CompiledCallWorkflow workflow);

    CompiledCallWorkflow Get(string id);
}

/// <summary>Default in-memory implementation backed by a <see cref="Dictionary{TKey, TValue}"/>.</summary>
public sealed class CallWorkflowCatalog : ICallWorkflowCatalog
{
    private readonly Dictionary<string, CompiledCallWorkflow> _byId;
    private readonly List<CompiledCallWorkflow> _ordered;

    public CallWorkflowCatalog(IEnumerable<CompiledCallWorkflow> workflows)
    {
        ArgumentNullException.ThrowIfNull(workflows);

        _byId = new(StringComparer.Ordinal);
        _ordered = [];
        foreach (var workflow in workflows)
        {
            if (!_byId.TryAdd(workflow.Id, workflow))
            {
                throw new ArgumentException(
                    $"Duplicate workflow id '{workflow.Id}' registered with the catalog.",
                    nameof(workflows));
            }
            _ordered.Add(workflow);
        }
    }

    public IReadOnlyList<CompiledCallWorkflow> Workflows => _ordered;

    public bool TryGet(string id, out CompiledCallWorkflow workflow)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return _byId.TryGetValue(id, out workflow!);
    }

    public CompiledCallWorkflow Get(string id) =>
        TryGet(id, out var workflow)
            ? workflow
            : throw new KeyNotFoundException($"No workflow '{id}' registered with the catalog.");
}
