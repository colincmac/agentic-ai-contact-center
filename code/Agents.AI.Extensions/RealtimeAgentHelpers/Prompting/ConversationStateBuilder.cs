namespace Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

/// <summary>
/// Builder for <see cref="ConversationState"/>.
/// </summary>
public sealed class ConversationStateBuilder
{
    private string _id = string.Empty;
    private string _description = string.Empty;
    private string? _goal;
    private List<string> _instructions = [];
    private List<string>? _examples;
    private string? _exitWhen;
    private List<StateTransition>? _transitions;

    /// <summary>
    /// Sets the state identifier.
    /// </summary>
    public ConversationStateBuilder Id(string id)
    {
        _id = id;

        return this;
    }

    /// <summary>
    /// Sets the state description.
    /// </summary>
    public ConversationStateBuilder Description(string description)
    {
        _description = description;

        return this;
    }

    /// <summary>
    /// Sets the goal for this state.
    /// </summary>
    public ConversationStateBuilder Goal(string goal)
    {
        _goal = goal;

        return this;
    }

    /// <summary>
    /// Adds an instruction for this state.
    /// </summary>
    public ConversationStateBuilder AddInstruction(string instruction)
    {
        _instructions.Add(instruction);

        return this;
    }

    /// <summary>
    /// Adds multiple instructions.
    /// </summary>
    public ConversationStateBuilder AddInstructions(params string[] instructions)
    {
        _instructions.AddRange(instructions);

        return this;
    }

    /// <summary>
    /// Adds an example phrase.
    /// </summary>
    public ConversationStateBuilder AddExample(string example)
    {
        _examples ??= [];
        _examples.Add(example);

        return this;
    }

    /// <summary>
    /// Adds multiple example phrases.
    /// </summary>
    public ConversationStateBuilder AddExamples(params string[] examples)
    {
        _examples ??= [];
        _examples.AddRange(examples);

        return this;
    }

    /// <summary>
    /// Sets the exit condition.
    /// </summary>
    public ConversationStateBuilder ExitWhen(string condition)
    {
        _exitWhen = condition;

        return this;
    }

    /// <summary>
    /// Adds a transition to another state.
    /// </summary>
    public ConversationStateBuilder TransitionTo(string nextStep, string condition)
    {
        _transitions ??= [];
        _transitions.Add(new StateTransition { NextStep = nextStep, Condition = condition });

        return this;
    }

    internal ConversationState Build()
    {
        if (string.IsNullOrWhiteSpace(_id))
        {
            throw new InvalidOperationException("Conversation state ID is required.");
        }

        if (string.IsNullOrWhiteSpace(_description))
        {
            throw new InvalidOperationException("Conversation state description is required.");
        }

        if (_instructions.Count == 0)
        {
            throw new InvalidOperationException("At least one instruction is required for a conversation state.");
        }

        return new ConversationState
        {
            Id = _id,
            Description = _description,
            Goal = _goal,
            Instructions = _instructions,
            Examples = _examples,
            ExitWhen = _exitWhen,
            Transitions = _transitions
        };
    }
}
