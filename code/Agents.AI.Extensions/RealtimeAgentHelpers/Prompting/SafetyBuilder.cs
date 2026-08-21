namespace Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

/// <summary>
/// Builder for <see cref="SafetyAndEscalation"/>.
/// </summary>
public sealed class SafetyBuilder
{
    private List<string> _escalateWhen = [];
    private List<string>? _escalationPhrases;
    private int? _maxFailedToolAttempts;
    private int? _maxNoMatchEvents;
    private List<string>? _escalationExamples;

    /// <summary>
    /// Adds a condition that triggers escalation.
    /// </summary>
    public SafetyBuilder EscalateWhen(string condition)
    {
        _escalateWhen.Add(condition);

        return this;
    }

    /// <summary>
    /// Adds multiple escalation conditions.
    /// </summary>
    public SafetyBuilder EscalateWhen(params string[] conditions)
    {
        _escalateWhen.AddRange(conditions);

        return this;
    }

    /// <summary>
    /// Applies default escalation conditions.
    /// </summary>
    public SafetyBuilder UseDefaultEscalationConditions()
    {
        _escalateWhen.AddRange([
            "Safety risk (self-harm, threats, harassment)",
            "User explicitly asks for a human",
            "Severe dissatisfaction (e.g., extremely frustrated, repeated complaints, profanity)",
            "Out-of-scope or restricted (e.g., real-time news, financial/legal/medical advice)"
        ]);

        return this;
    }

    /// <summary>
    /// Sets the maximum failed tool attempts before escalation.
    /// </summary>
    public SafetyBuilder MaxFailedToolAttempts(int max)
    {
        _maxFailedToolAttempts = max;

        return this;
    }

    /// <summary>
    /// Sets the maximum no-match events before escalation.
    /// </summary>
    public SafetyBuilder MaxNoMatchEvents(int max)
    {
        _maxNoMatchEvents = max;

        return this;
    }

    /// <summary>
    /// Adds phrases to use when escalating.
    /// </summary>
    public SafetyBuilder EscalationPhrases(params string[] phrases)
    {
        _escalationPhrases = [.. phrases];

        return this;
    }

    /// <summary>
    /// Adds example scenarios that require escalation.
    /// </summary>
    public SafetyBuilder EscalationExamples(params string[] examples)
    {
        _escalationExamples = [.. examples];

        return this;
    }

    internal SafetyAndEscalation Build()
    {
        if (_escalateWhen.Count == 0)
        {
            throw new InvalidOperationException("At least one escalation condition is required.");
        }

        return new SafetyAndEscalation
        {
            EscalateWhen = _escalateWhen,
            EscalationPhrases = _escalationPhrases,
            MaxFailedToolAttempts = _maxFailedToolAttempts,
            MaxNoMatchEvents = _maxNoMatchEvents,
            EscalationExamples = _escalationExamples
        };
    }
}
