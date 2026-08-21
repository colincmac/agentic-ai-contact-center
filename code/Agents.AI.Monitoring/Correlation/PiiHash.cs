using System.Security.Cryptography;
using System.Text;

namespace Agents.AI.Monitoring.Correlation;

/// <summary>
/// One-way hashing for personally identifiable call identifiers (e.g. the caller ANI).
/// Telemetry stores the hash instead of the raw number so dashboards can still correlate
/// and join on a caller without persisting EUII in Log Analytics / Grafana.
/// </summary>
public static class PiiHash
{
    /// <summary>
    /// Hash a caller/called number to a stable, lowercase hex SHA-256 digest.
    /// Returns <c>null</c> for null/empty input. Digits-only normalization keeps the
    /// hash stable across formatting differences (spaces, dashes, leading '+').
    /// </summary>
    public static string? OfPhoneNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = Normalize(value);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(bytes);
    }

    private static string Normalize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsAsciiDigit(ch))
            {
                sb.Append(ch);
            }
        }

        return sb.Length > 0 ? sb.ToString() : value.Trim();
    }
}
