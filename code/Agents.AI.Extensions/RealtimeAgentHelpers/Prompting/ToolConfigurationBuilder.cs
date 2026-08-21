namespace Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;

/// <summary>
/// Builder for <see cref="ToolConfiguration"/>.
/// </summary>
public sealed class ToolConfigurationBuilder
{
    private string? _globalPreamble;
    private bool _requireConfirmation;
    private List<ToolUsageRule>? _toolRules;
    private SupervisorToolConfig? _supervisorTool;

    /// <summary>
    /// Sets the global preamble for all tool calls.
    /// </summary>
    public ToolConfigurationBuilder GlobalPreamble(string preamble)
    {
        _globalPreamble = preamble;

        return this;
    }

    /// <summary>
    /// Sets whether user confirmation is required before tool calls.
    /// </summary>
    public ToolConfigurationBuilder RequireConfirmation(bool require = true)
    {
        _requireConfirmation = require;

        return this;
    }

    /// <summary>
    /// Adds a proactive tool rule.
    /// </summary>
    public ToolConfigurationBuilder AddProactiveTool(string name, string useWhen, string? doNotUseWhen = null)
    {
        _toolRules ??= [];
        _toolRules.Add(new ToolUsageRule
        {
            Name = name,
            UseWhen = useWhen,
            DoNotUseWhen = doNotUseWhen,
            Behavior = ToolBehavior.Proactive
        });

        return this;
    }

    /// <summary>
    /// Adds a tool that requires confirmation before calling.
    /// </summary>
    public ToolConfigurationBuilder AddConfirmationTool(string name, string useWhen, string confirmationPhrase, string? doNotUseWhen = null)
    {
        _toolRules ??= [];
        _toolRules.Add(new ToolUsageRule
        {
            Name = name,
            UseWhen = useWhen,
            DoNotUseWhen = doNotUseWhen,
            Behavior = ToolBehavior.ConfirmationFirst,
            ConfirmationPhrase = confirmationPhrase
        });

        return this;
    }

    /// <summary>
    /// Adds a tool with preamble phrases.
    /// </summary>
    public ToolConfigurationBuilder AddPreambleTool(string name, string useWhen, IEnumerable<string> preamblePhrases, string? doNotUseWhen = null)
    {
        _toolRules ??= [];
        _toolRules.Add(new ToolUsageRule
        {
            Name = name,
            UseWhen = useWhen,
            DoNotUseWhen = doNotUseWhen,
            Behavior = ToolBehavior.Preambles,
            PreamblePhrases = [.. preamblePhrases]
        });

        return this;
    }

    /// <summary>
    /// Adds a custom tool rule.
    /// </summary>
    public ToolConfigurationBuilder AddTool(ToolUsageRule rule)
    {
        _toolRules ??= [];
        _toolRules.Add(rule);

        return this;
    }

    /// <summary>
    /// Configures a supervisor tool for responder-thinker architecture.
    /// </summary>
    public ToolConfigurationBuilder WithSupervisorTool(
        IEnumerable<string> callWhen,
        IEnumerable<string> doNotCallWhen,
        IEnumerable<string>? approvedFillers = null,
        string? rephraseInstructions = null)
    {
        _supervisorTool = new SupervisorToolConfig
        {
            CallWhen = [.. callWhen],
            DoNotCallWhen = [.. doNotCallWhen],
            ApprovedFillers = approvedFillers is not null ? [.. approvedFillers] : null,
            RephraseInstructions = rephraseInstructions
        };

        return this;
    }

    internal ToolConfiguration Build()
    {
        return new ToolConfiguration
        {
            GlobalPreamble = _globalPreamble,
            RequireConfirmation = _requireConfirmation,
            ToolRules = _toolRules,
            SupervisorTool = _supervisorTool
        };
    }
}
