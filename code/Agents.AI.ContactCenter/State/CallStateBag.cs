using System.Collections.Immutable;

namespace Agents.AI.ContactCenter.State;

/// <summary>
/// Per-call, string-keyed bag of immutable state slices. Reads are lock-free (every slice is an
/// immutable snapshot published via a single atomic reference swap); writes are performed only by
/// the single-writer projector fold, so no per-field locking is required.
/// </summary>
/// <remarks>
/// This is the call-scoped analogue of agent-framework's session <c>StateBag</c>: many independent
/// providers coexist in one call by using distinct <c>sliceId</c> keys. Unlike the agent-framework
/// bag, mutation here is funneled through the single event-pump thread rather than a serialized turn.
/// </remarks>
public sealed class CallStateBag
{
    private ImmutableDictionary<string, object> _slices = ImmutableDictionary<string, object>.Empty;

    /// <summary>Lock-free typed read of the slice stored under <paramref name="key"/>.</summary>
    public bool TryGet<TState>(string key, out TState? value) where TState : class
    {
        if (Volatile.Read(ref _slices).TryGetValue(key, out var boxed) && boxed is TState typed)
        {
            value = typed;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>Lock-free read of the raw boxed slice, or <see langword="null"/> when absent.</summary>
    public object? GetRaw(string key)
        => Volatile.Read(ref _slices).TryGetValue(key, out var boxed) ? boxed : null;

    /// <summary>Slice keys currently present in the bag.</summary>
    public IReadOnlyCollection<string> Keys => Volatile.Read(ref _slices).Keys.ToArray();

    /// <summary>
    /// Publish <paramref name="value"/> as the current slice for <paramref name="key"/>. Internal so
    /// only the projector (the single writer) can mutate the bag.
    /// </summary>
    internal void Set(string key, object value)
        => Volatile.Write(ref _slices, Volatile.Read(ref _slices).SetItem(key, value));
}
