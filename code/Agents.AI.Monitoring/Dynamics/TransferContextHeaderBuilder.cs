using Agents.AI.Monitoring.Correlation;

namespace Agents.AI.Monitoring.Dynamics;

/// <summary>Which ACS custom-context transport carries the correlation payload on a transfer.</summary>
public enum TransferTransport
{
    /// <summary>Same-tenant transfer over the Microsoft calling backbone — use VoIP headers.</summary>
    Voip,

    /// <summary>Cross-tenant / SBC transfer — real SIP; use SIP headers + UUI.</summary>
    Sip,
}

/// <summary>
/// Transport-neutral result of building transfer context. The ACS call site applies these to
/// <c>CustomCallingContext</c> (VoIP headers for same-tenant, or the SIP UUI for cross-tenant).
/// Kept free of any ACS SDK dependency so <c>Agents.AI.Monitoring</c> stays a leaf library.
/// </summary>
public sealed record TransferContextHeaders(
    IReadOnlyList<KeyValuePair<string, string>> VoipHeaders,
    string? SipUui);

/// <summary>
/// Builds the correlation payload attached to an ACS transfer so Dynamics 365 can hydrate a
/// screen-pop and route on it. Enforces the documented ACS custom-context limits. The keys are
/// the <see cref="D365ContextVariableMap"/> names so D365 context variables read them 1:1.
/// </summary>
public static class TransferContextHeaderBuilder
{
    /// <summary>Max VoIP headers (ACS limit is 1,000).</summary>
    public const int MaxVoipHeaders = 1000;

    /// <summary>Max VoIP header value length (ACS limit is 1,024).</summary>
    public const int MaxVoipValueLength = 1024;

    /// <summary>Max SIP User-to-User Information length (ACS limit is 256).</summary>
    public const int MaxUuiLength = 256;

    /// <summary>Build the transfer context from a correlation context.</summary>
    public static TransferContextHeaders Build(
        CallCorrelationContext context,
        TransferTransport transport,
        string? intent = null,
        string? language = null)
        => Build(D365ContextVariableMap.FromCorrelation(context, intent, language), transport, context.ContextId);

    /// <summary>
    /// Build the transfer context from an arbitrary set of key/value pairs (e.g. an existing
    /// custom-context dictionary). VoIP transport carries all valid pairs as headers; SIP
    /// transport carries the <paramref name="contextId"/> in the UUI (the only field guaranteed
    /// to fit the tighter cross-tenant limits) — the full payload stays in the correlation store.
    /// </summary>
    public static TransferContextHeaders Build(
        IReadOnlyList<KeyValuePair<string, string>> pairs,
        TransferTransport transport,
        string? contextId = null)
    {
        if (transport == TransferTransport.Sip)
        {
            var uui = contextId;
            if (!string.IsNullOrEmpty(uui) && uui.Length > MaxUuiLength)
            {
                uui = uui[..MaxUuiLength];
            }

            return new TransferContextHeaders(Array.Empty<KeyValuePair<string, string>>(), uui);
        }

        var voip = new List<KeyValuePair<string, string>>(Math.Min(pairs.Count, MaxVoipHeaders));
        foreach (var pair in pairs)
        {
            if (voip.Count >= MaxVoipHeaders)
            {
                break;
            }

            if (string.IsNullOrEmpty(pair.Key) || pair.Value.Length > MaxVoipValueLength)
            {
                continue;
            }

            voip.Add(pair);
        }

        return new TransferContextHeaders(voip, SipUui: null);
    }
}
