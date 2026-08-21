namespace Agents.AI.ContactCenter.Calling;

/// <summary>
/// Renders a slice of per-call state into prompt text. Implemented by the call-state projector and
/// composed by the workflow stage compiler, which concatenates each renderer's contribution into the
/// active stage prompt.
/// </summary>
public interface IPromptStateRenderer
{
    /// <summary>Render the current state as prompt text; may be empty when there is nothing to contribute.</summary>
    string RenderAsPrompt();
}
