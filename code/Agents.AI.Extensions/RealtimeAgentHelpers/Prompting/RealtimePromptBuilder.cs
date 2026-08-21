namespace Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

/// <summary>
/// Fluent builder for constructing <see cref="RealtimePrompt"/> instances.
/// <remarks>
/// <br />Example: <br /> <br />
/// <code>
/// var prompt = RealtimePrompt.CreateBuilder()
/// .WithRole("You are a customer service agent", "Help users resolve billing issues")
/// .WithPersonality(p => p
///    .Personality("Friendly, calm and approachable")
///    .Tone("Warm, concise, confident")
///    .Length("2-3 sentences per turn")
///    .PinToLanguage("English")
///    .EnforceVariety())
/// .AddPronunciation("SQL", "sequel")
/// .WithSafety(s => s
///    .UseDefaultEscalationConditions()
///    .MaxFailedToolAttempts(2))
/// .BuildAndRender();
/// </code>
/// </remarks>
/// </summary>
public sealed class RealtimePromptBuilder
{
    private RoleAndObjective? _role;
    private PersonalityAndTone? _personality;
    private string? _context;
    private List<ReferencePronunciation>? _pronunciations;
    private ToolConfiguration? _tools;
    private InstructionRules? _instructions;
    private List<ConversationState>? _conversationFlow;
    private SamplePhrases? _phrases;
    private SafetyAndEscalation? _safety;

    /// <summary>
    /// Sets the role and objective for the agent.
    /// </summary>
    public RealtimePromptBuilder WithRole(string identity, string objective, string? characterTraits = null)
    {
        _role = new RoleAndObjective
        {
            Identity = identity,
            Objective = objective,
            CharacterTraits = characterTraits
        };

        return this;
    }

    /// <summary>
    /// Sets the role and objective using a pre-built instance.
    /// </summary>
    public RealtimePromptBuilder WithRole(RoleAndObjective role)
    {
        _role = role;

        return this;
    }

    /// <summary>
    /// Configures personality and tone using a builder action.
    /// </summary>
    public RealtimePromptBuilder WithPersonality(Action<PersonalityBuilder> configure)
    {
        var builder = new PersonalityBuilder();
        configure(builder);
        _personality = builder.Build();

        return this;
    }

    /// <summary>
    /// Sets the personality using a pre-built instance.
    /// </summary>
    public RealtimePromptBuilder WithPersonality(PersonalityAndTone personality)
    {
        _personality = personality;

        return this;
    }

    /// <summary>
    /// Sets additional context information.
    /// </summary>
    public RealtimePromptBuilder WithContext(string context)
    {
        _context = context;

        return this;
    }

    /// <summary>
    /// Adds a reference pronunciation.
    /// </summary>
    public RealtimePromptBuilder AddPronunciation(string word, string pronunciation)
    {
        _pronunciations ??= [];
        _pronunciations.Add(new ReferencePronunciation { Word = word, Pronunciation = pronunciation });

        return this;
    }

    /// <summary>
    /// Adds multiple reference pronunciations.
    /// </summary>
    public RealtimePromptBuilder AddPronunciations(params (string Word, string Pronunciation)[] items)
    {
        _pronunciations ??= [];

        foreach (var (word, pronunciation) in items)
        {
            _pronunciations.Add(new ReferencePronunciation { Word = word, Pronunciation = pronunciation });
        }

        return this;
    }

    /// <summary>
    /// Configures tool usage using a builder action.
    /// </summary>
    public RealtimePromptBuilder WithTools(Action<ToolConfigurationBuilder> configure)
    {
        var builder = new ToolConfigurationBuilder();
        configure(builder);
        _tools = builder.Build();

        return this;
    }

    /// <summary>
    /// Sets the tool configuration using a pre-built instance.
    /// </summary>
    public RealtimePromptBuilder WithTools(ToolConfiguration tools)
    {
        _tools = tools;

        return this;
    }

    /// <summary>
    /// Configures instruction rules using a builder action.
    /// </summary>
    public RealtimePromptBuilder WithInstructions(Action<InstructionRulesBuilder> configure)
    {
        var builder = new InstructionRulesBuilder();
        configure(builder);
        _instructions = builder.Build();

        return this;
    }

    /// <summary>
    /// Sets the instruction rules using a pre-built instance.
    /// </summary>
    public RealtimePromptBuilder WithInstructions(InstructionRules instructions)
    {
        _instructions = instructions;

        return this;
    }

    /// <summary>
    /// Adds a conversation state to the flow.
    /// </summary>
    public RealtimePromptBuilder AddConversationState(Action<ConversationStateBuilder> configure)
    {
        var builder = new ConversationStateBuilder();
        configure(builder);
        _conversationFlow ??= [];
        _conversationFlow.Add(builder.Build());

        return this;
    }

    /// <summary>
    /// Adds a conversation state using a pre-built instance.
    /// </summary>
    public RealtimePromptBuilder AddConversationState(ConversationState state)
    {
        _conversationFlow ??= [];
        _conversationFlow.Add(state);

        return this;
    }

    /// <summary>
    /// Sets the entire conversation flow.
    /// </summary>
    public RealtimePromptBuilder WithConversationFlow(IEnumerable<ConversationState> states)
    {
        _conversationFlow = [.. states];

        return this;
    }

    /// <summary>
    /// Configures sample phrases using a builder action.
    /// </summary>
    public RealtimePromptBuilder WithSamplePhrases(Action<SamplePhrasesBuilder> configure)
    {
        var builder = new SamplePhrasesBuilder();
        configure(builder);
        _phrases = builder.Build();

        return this;
    }

    /// <summary>
    /// Sets the sample phrases using a pre-built instance.
    /// </summary>
    public RealtimePromptBuilder WithSamplePhrases(SamplePhrases phrases)
    {
        _phrases = phrases;

        return this;
    }

    /// <summary>
    /// Configures safety and escalation using a builder action.
    /// </summary>
    public RealtimePromptBuilder WithSafety(Action<SafetyBuilder> configure)
    {
        var builder = new SafetyBuilder();
        configure(builder);
        _safety = builder.Build();

        return this;
    }

    /// <summary>
    /// Sets the safety configuration using a pre-built instance.
    /// </summary>
    public RealtimePromptBuilder WithSafety(SafetyAndEscalation safety)
    {
        _safety = safety;

        return this;
    }

    /// <summary>
    /// Builds the <see cref="RealtimePrompt"/> instance.
    /// </summary>
    public RealtimePrompt Build()
    {
        return new RealtimePrompt
        {
            Role = _role,
            Personality = _personality,
            Context = _context,
            ReferencePronunciations = _pronunciations,
            Tools = _tools,
            Instructions = _instructions,
            ConversationFlow = _conversationFlow,
            Phrases = _phrases,
            Safety = _safety
        };
    }

    /// <summary>
    /// Builds and renders the prompt to a formatted system instruction string.
    /// </summary>
    public string BuildAndRender() => RealtimeAIPromptTemplate.Render(Build());
}
