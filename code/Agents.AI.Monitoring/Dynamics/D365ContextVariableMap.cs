using Agents.AI.Monitoring.Correlation;

namespace Agents.AI.Monitoring.Dynamics;

/// <summary>
/// The contract that keeps ACS custom-context header keys in lockstep with the Dynamics 365
/// context variables that read them on the receiving <c>IncomingCall</c>. D365 context-variable
/// names are case-sensitive and must match the header names <em>exactly</em>, so both sides
/// reference these constants. Names ≤ 100 chars, values ≤ 4000 chars per D365 limits.
/// </summary>
public static class D365ContextVariableMap
{
    /// <summary>Durable transfer context id — the primary join key back to the IVR call.</summary>
    public const string ContextId = "cc_contextId";

    /// <summary>Canonical end-to-end call id (UUIDv7).</summary>
    public const string E2ECallId = "cc_e2eCallId";

    /// <summary>Detected/selected caller intent.</summary>
    public const string Intent = "cc_intent";

    /// <summary>Caller language code.</summary>
    public const string Language = "cc_lang";

    /// <summary>Target workstream hint.</summary>
    public const string Workstream = "cc_workstream";

    /// <summary>Maximum D365 context-variable name length.</summary>
    public const int MaxNameLength = 100;

    /// <summary>Maximum D365 context-variable value length.</summary>
    public const int MaxValueLength = 4000;

    /// <summary>
    /// Project a correlation context (plus optional intent/language overrides) into the ordered
    /// set of context-variable key/value pairs to carry across the transfer. Null/empty values
    /// are skipped so only populated fields ride along.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> FromCorrelation(
        CallCorrelationContext context,
        string? intent = null,
        string? language = null)
    {
        var pairs = new List<KeyValuePair<string, string>>(5);
        Add(pairs, ContextId, context.ContextId);
        Add(pairs, E2ECallId, context.E2ECallId);
        Add(pairs, Intent, intent ?? context.Intent);
        Add(pairs, Language, language ?? context.LanguageCode);
        Add(pairs, Workstream, context.Workstream);
        return pairs;
    }

    /// <summary>True if the name/value satisfy the D365 context-variable limits.</summary>
    public static bool IsValid(string name, string value) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= MaxNameLength
        && value.Length <= MaxValueLength;

    private static void Add(List<KeyValuePair<string, string>> pairs, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value) && IsValid(name, value))
        {
            pairs.Add(new KeyValuePair<string, string>(name, value));
        }
    }
}
