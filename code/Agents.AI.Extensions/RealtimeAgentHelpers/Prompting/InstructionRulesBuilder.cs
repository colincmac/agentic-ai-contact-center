namespace Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

/// <summary>
/// Builder for <see cref="InstructionRules"/>.
/// </summary>
public sealed class InstructionRulesBuilder
{
    private List<string>? _generalRules;
    private UnclearAudioHandling? _unclearAudio;
    private string? _alphanumericPronunciation;
    private List<string>? _suppressSounds;

    /// <summary>
    /// Adds a general instruction rule.
    /// </summary>
    public InstructionRulesBuilder AddRule(string rule)
    {
        _generalRules ??= [];
        _generalRules.Add(rule);

        return this;
    }

    /// <summary>
    /// Adds multiple general instruction rules.
    /// </summary>
    public InstructionRulesBuilder AddRules(params string[] rules)
    {
        _generalRules ??= [];
        _generalRules.AddRange(rules);

        return this;
    }

    /// <summary>
    /// Sets alphanumeric pronunciation rules.
    /// </summary>
    public InstructionRulesBuilder AlphanumericPronunciation(string rules)
    {
        _alphanumericPronunciation = rules;

        return this;
    }

    /// <summary>
    /// Enables character-by-character pronunciation for numbers and codes.
    /// </summary>
    public InstructionRulesBuilder EnableCharacterByCharacterPronunciation()
    {
        _alphanumericPronunciation = "When reading numbers or codes, speak each character separately, separated by hyphens (e.g., 4-1-5). Repeat EXACTLY the provided number, do not forget any.";

        return this;
    }

    /// <summary>
    /// Adds a sound suppression rule.
    /// </summary>
    public InstructionRulesBuilder SuppressSound(string rule)
    {
        _suppressSounds ??= [];
        _suppressSounds.Add(rule);

        return this;
    }

    /// <summary>
    /// Suppresses background music and sound effects.
    /// </summary>
    public InstructionRulesBuilder SuppressBackgroundSounds()
    {
        _suppressSounds ??= [];
        _suppressSounds.Add("Do not include any sound effects or onomatopoeic expressions in your responses.");

        return this;
    }

    /// <summary>
    /// Configures unclear audio handling.
    /// </summary>
    public InstructionRulesBuilder HandleUnclearAudio(bool askForClarification = true, bool repeatLastQuestion = false, params string[] clarificationPhrases)
    {
        _unclearAudio = new UnclearAudioHandling
        {
            AskForClarification = askForClarification,
            RepeatLastQuestion = repeatLastQuestion,
            ClarificationPhrases = clarificationPhrases.Length > 0 ? clarificationPhrases : null
        };

        return this;
    }

    internal InstructionRules Build()
    {
        return new InstructionRules
        {
            GeneralRules = _generalRules,
            UnclearAudio = _unclearAudio,
            AlphanumericPronunciation = _alphanumericPronunciation,
            SuppressSounds = _suppressSounds
        };
    }
}
