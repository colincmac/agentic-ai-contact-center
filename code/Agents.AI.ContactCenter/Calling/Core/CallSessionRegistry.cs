using System.Collections.Concurrent;

namespace Agents.AI.ContactCenter.Calling.Core;

/// <summary>
/// Default in-process registry of active call sessions.
/// </summary>
public sealed class CallSessionRegistry : ICallSessionRegistry
{
    private readonly ConcurrentDictionary<string, ICallSession> _sessions = new();

    public bool TryAdd(ICallSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _sessions.TryAdd(session.CallId, session);
    }

    public ICallSession? TryGet(string callId)
    {
        _sessions.TryGetValue(callId, out var session);
        return session;
    }

    public IReadOnlyCollection<ICallSession> ActiveSessions => _sessions.Values.ToArray();

    public bool TryRemove(string callId, ICallSession expectedSession)
    {
        ArgumentException.ThrowIfNullOrEmpty(callId);
        ArgumentNullException.ThrowIfNull(expectedSession);

        return ((ICollection<KeyValuePair<string, ICallSession>>)_sessions)
            .Remove(new KeyValuePair<string, ICallSession>(callId, expectedSession));
    }
}
