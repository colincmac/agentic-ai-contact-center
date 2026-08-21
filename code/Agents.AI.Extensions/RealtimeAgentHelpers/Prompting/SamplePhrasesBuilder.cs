namespace Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

/// <summary>
/// Builder for <see cref="SamplePhrases"/>.
/// </summary>
public sealed class SamplePhrasesBuilder
{
    private List<string>? _acknowledgements;
    private List<string>? _clarifiers;
    private List<string>? _bridges;
    private List<string>? _empathy;
    private List<string>? _closers;

    /// <summary>
    /// Adds acknowledgement phrases.
    /// </summary>
    public SamplePhrasesBuilder Acknowledgements(params string[] phrases)
    {
        _acknowledgements = [.. phrases];

        return this;
    }

    /// <summary>
    /// Adds clarifier phrases.
    /// </summary>
    public SamplePhrasesBuilder Clarifiers(params string[] phrases)
    {
        _clarifiers = [.. phrases];

        return this;
    }

    /// <summary>
    /// Adds bridge/transition phrases.
    /// </summary>
    public SamplePhrasesBuilder Bridges(params string[] phrases)
    {
        _bridges = [.. phrases];

        return this;
    }

    /// <summary>
    /// Adds empathy phrases.
    /// </summary>
    public SamplePhrasesBuilder Empathy(params string[] phrases)
    {
        _empathy = [.. phrases];

        return this;
    }

    /// <summary>
    /// Adds closing phrases.
    /// </summary>
    public SamplePhrasesBuilder Closers(params string[] phrases)
    {
        _closers = [.. phrases];

        return this;
    }

    /// <summary>
    /// Applies a default set of sample phrases.
    /// </summary>
    public SamplePhrasesBuilder UseDefaults()
    {
        _acknowledgements = ["On it.", "One moment.", "Good question."];
        _clarifiers = ["Do you want A or B?", "What's the deadline?"];
        _bridges = ["Here's the quick plan.", "Let's keep it simple."];
        _empathy = ["That's frustrating—let's fix it."];
        _closers = ["Anything else before we wrap?", "Happy to help next time."];

        return this;
    }

    internal SamplePhrases Build()
    {
        return new SamplePhrases
        {
            Acknowledgements = _acknowledgements,
            Clarifiers = _clarifiers,
            Bridges = _bridges,
            Empathy = _empathy,
            Closers = _closers
        };
    }
}
