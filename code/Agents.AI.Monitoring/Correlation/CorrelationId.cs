namespace Agents.AI.Monitoring.Correlation;

/// <summary>
/// Generates canonical correlation identifiers as UUIDv7 (RFC 9562) values: a 48-bit
/// Unix-millisecond timestamp in the high bits followed by randomness, rendered as a
/// standard 36-character <see cref="Guid"/> string. UUIDv7 is a real <see cref="Guid"/>
/// (widely supported and interoperable) and is time-ordered by construction, keeping
/// index locality good for correlation joins in Log Analytics / Grafana.
/// </summary>
public static class CorrelationId
{
    /// <summary>Mint a new UUIDv7-formatted correlation id.</summary>
    public static string NewId() => Guid.CreateVersion7().ToString("D");
}
