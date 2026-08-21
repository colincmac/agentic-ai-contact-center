namespace Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

/// <summary>
/// Builder for <see cref="PersonalityAndTone"/>.
/// </summary>
public sealed class PersonalityBuilder
{
    private string _personality = "Friendly and helpful";
    private string _tone = "Warm, concise, confident";
    private string? _length;
    private string? _pacing;
    private string? _enthusiasm;
    private string? _formality;
    private string? _emotion;
    private string? _fillerWords;
    private bool _enforceVariety;
    private LanguageConstraint? _language;

    /// <summary>
    /// Sets the personality description.
    /// </summary>
    public PersonalityBuilder Personality(string personality)
    {
        _personality = personality;

        return this;
    }

    /// <summary>
    /// Sets the tone description.
    /// </summary>
    public PersonalityBuilder Tone(string tone)
    {
        _tone = tone;

        return this;
    }

    /// <summary>
    /// Sets the response length guideline.
    /// </summary>
    public PersonalityBuilder Length(string length)
    {
        _length = length;

        return this;
    }

    /// <summary>
    /// Sets the speaking pacing.
    /// </summary>
    public PersonalityBuilder Pacing(string pacing)
    {
        _pacing = pacing;

        return this;
    }

    /// <summary>
    /// Sets the level of enthusiasm.
    /// </summary>
    public PersonalityBuilder Enthusiasm(string enthusiasm)
    {
        _enthusiasm = enthusiasm;

        return this;
    }

    /// <summary>
    /// Sets the level of formality.
    /// </summary>
    public PersonalityBuilder Formality(string formality)
    {
        _formality = formality;

        return this;
    }

    /// <summary>
    /// Sets the emotional expression level.
    /// </summary>
    public PersonalityBuilder Emotion(string emotion)
    {
        _emotion = emotion;

        return this;
    }

    /// <summary>
    /// Sets filler word usage (e.g., "none", "occasionally", "often").
    /// </summary>
    public PersonalityBuilder FillerWords(string fillerWords)
    {
        _fillerWords = fillerWords;

        return this;
    }

    /// <summary>
    /// Enables variety enforcement to avoid repetitive responses.
    /// </summary>
    public PersonalityBuilder EnforceVariety(bool enforce = true)
    {
        _enforceVariety = enforce;

        return this;
    }

    /// <summary>
    /// Pins the conversation to a single language.
    /// </summary>
    public PersonalityBuilder PinToLanguage(string language, string? nonPrimaryResponse = null)
    {
        _language = new LanguageConstraint
        {
            PrimaryLanguage = language,
            AllowOtherLanguages = false,
            NonPrimaryLanguageResponse = nonPrimaryResponse ?? $"If the user speaks another language, politely explain that support is limited to {language}."
        };

        return this;
    }

    /// <summary>
    /// Configures language with code-switching support (e.g., for language tutoring).
    /// </summary>
    public PersonalityBuilder WithLanguage(string primaryLanguage, bool allowOthers, string? codeSwitchingRules = null)
    {
        _language = new LanguageConstraint
        {
            PrimaryLanguage = primaryLanguage,
            AllowOtherLanguages = allowOthers,
            CodeSwitchingRules = codeSwitchingRules
        };

        return this;
    }

    internal PersonalityAndTone Build()
    {
        return new PersonalityAndTone
        {
            Personality = _personality,
            Tone = _tone,
            Length = _length,
            Pacing = _pacing,
            Enthusiasm = _enthusiasm,
            Formality = _formality,
            Emotion = _emotion,
            FillerWords = _fillerWords,
            EnforceVariety = _enforceVariety,
            Language = _language
        };
    }
}
