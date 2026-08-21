namespace Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

/// <summary>
/// Configures language constraints for the agent.
/// </summary>
public record LanguageConstraint
{
    /// <summary>
    /// Gets or sets the primary language.
    /// </summary>
    public required string PrimaryLanguage { get; init; }

    /// <summary>
    /// Gets or sets whether other languages are allowed.
    /// </summary>
    public bool AllowOtherLanguages { get; init; }

    /// <summary>
    /// Gets or sets instructions for handling non-primary language requests.
    /// </summary>
    public string? NonPrimaryLanguageResponse { get; init; }

    /// <summary>
    /// Gets or sets special instructions for language switching scenarios (e.g., language tutoring).
    /// </summary>
    public string? CodeSwitchingRules { get; init; }
}
