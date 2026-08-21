using System.Globalization;

namespace Agents.AI.ContactCenter.Coordination.Core;

/// <summary>
/// Pipe-delimited wire format for <see cref="CallOwnership"/>:
/// <c>{InstanceId}|{ClusterId}|{PodId}|{Kind}|{LeaseUntilUnixMs}</c>.
/// <see cref="CallOwnership.InstanceId"/> is always a 32-char GUID hex string,
/// so server-side Lua scripts can compare ownership by extracting the prefix
/// up to the first <c>|</c> without parsing the rest of the value.
/// </summary>
internal static class CallOwnershipCodec
{
    private const char Separator = '|';

    public static string Encode(CallOwnership owner)
    {
        if (owner.ClusterId.IndexOf(Separator) >= 0 || owner.PodId.IndexOf(Separator) >= 0 || owner.InstanceId.IndexOf(Separator) >= 0)
        {
            throw new InvalidOperationException($"CallOwnership identity fields cannot contain '{Separator}'.");
        }

        var leaseMs = owner.LeaseUntil.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var kind = ((int)owner.Kind).ToString(CultureInfo.InvariantCulture);
        return string.Concat(owner.InstanceId, "|", owner.ClusterId, "|", owner.PodId, "|", kind, "|", leaseMs);
    }

    public static CallOwnership Decode(string value)
    {
        var parts = value.Split(Separator, 5);
        if (parts.Length != 5)
        {
            throw new FormatException($"CallOwnership value must have 5 fields, got {parts.Length}.");
        }

        var kind = (CallOwnershipKind)int.Parse(parts[3], CultureInfo.InvariantCulture);
        var leaseUntil = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(parts[4], CultureInfo.InvariantCulture));
        return new CallOwnership(parts[1], parts[2], parts[0], kind, leaseUntil);
    }
}
