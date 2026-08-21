using Agents.AI.Monitoring.Correlation;

namespace Agents.AI.ContactCenter.Tests;

/// <summary>
/// Verifies that <see cref="CorrelationId.NewId"/> mints standard UUIDv7 (RFC 9562)
/// identifiers: parseable as a <see cref="Guid"/>, version 7, unique, and time-ordered
/// by construction so correlation joins keep good index locality.
/// </summary>
public class CorrelationIdTests
{
    [Fact]
    public void NewId_is_a_parseable_uuid_v7()
    {
        var id = CorrelationId.NewId();

        Assert.True(Guid.TryParse(id, out var guid), $"'{id}' is not a valid Guid");
        Assert.Equal(7, guid.Version);
    }

    [Fact]
    public void NewId_uses_the_canonical_36_char_hyphenated_form()
    {
        var id = CorrelationId.NewId();

        Assert.Equal(36, id.Length);
        Assert.Equal(id, Guid.Parse(id).ToString("D"));
    }

    [Fact]
    public void NewId_returns_unique_values()
    {
        var ids = Enumerable.Range(0, 1_000).Select(_ => CorrelationId.NewId()).ToArray();

        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void NewId_values_are_time_ordered_by_guid_comparison()
    {
        var first = Guid.Parse(CorrelationId.NewId());
        Thread.Sleep(5);
        var second = Guid.Parse(CorrelationId.NewId());

        // UUIDv7 embeds a millisecond timestamp in the high bits, so a later id
        // compares greater than an earlier one.
        Assert.True(second.CompareTo(first) > 0);
    }
}
