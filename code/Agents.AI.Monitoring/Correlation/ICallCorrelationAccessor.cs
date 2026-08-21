namespace Agents.AI.Monitoring.Correlation;

/// <summary>
/// Ambient accessor for the correlation context of the call currently being handled on
/// this async flow. Set at ACS ingress (and re-hydrated per webhook callback), read by the
/// <see cref="CallCorrelationEnrichmentProcessor"/> and by the transfer path.
/// </summary>
public interface ICallCorrelationAccessor
{
    /// <summary>The correlation context bound to the current async flow, if any.</summary>
    CallCorrelationContext? Current { get; set; }
}

/// <summary><see cref="AsyncLocal{T}"/>-backed <see cref="ICallCorrelationAccessor"/>.</summary>
internal sealed class CallCorrelationAccessor : ICallCorrelationAccessor
{
    private static readonly AsyncLocal<CallCorrelationContext?> value = new();

    public CallCorrelationContext? Current
    {
        get => value.Value;
        set => CallCorrelationAccessor.value.Value = value;
    }
}
