using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;

namespace Agents.AI.ContactCenter.Exceptions;

/// <summary>
/// Thrown when <see cref="WorkflowGraphCompiler"/> can't produce a <see cref="CompiledCallWorkflow"/>
/// from a <see cref="WorkflowBlueprint"/>. The exception aggregates every validation error so
/// authors can fix them all in one pass.
/// </summary>
public sealed class WorkflowCompilationException(string workflowId, IReadOnlyList<string> errors)
    : InvalidOperationException(BuildMessage(workflowId, errors))
{
    public string WorkflowId { get; } = workflowId;
    public IReadOnlyList<string> Errors { get; } = errors;

    private static string BuildMessage(string workflowId, IReadOnlyList<string> errors)
    {
        ArgumentException.ThrowIfNullOrEmpty(workflowId);
        ArgumentNullException.ThrowIfNull(errors);
        return $"Workflow '{workflowId}' failed to compile with {errors.Count} error(s):" +
            Environment.NewLine +
            string.Join(Environment.NewLine, errors.Select(e => "  - " + e));
    }
}
