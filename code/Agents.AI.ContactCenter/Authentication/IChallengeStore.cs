using System.Collections.Concurrent;

namespace Agents.AI.ContactCenter.Authentication;

/// <summary>
/// Per-process store for in-flight <see cref="AuthenticationChallenge"/> state. Replaces
/// the legacy <c>MfaVerificationSession</c> bag that lived under the deleted
/// <c>Authentication.UserIdentity</c> namespace. Implementations are responsible for
/// generating + validating challenge secrets (OTPs, magic-link tokens, etc.) and for
/// expiring entries.
/// </summary>
public interface IChallengeStore
{
    /// <summary>Persist a freshly-issued challenge.</summary>
    Task SaveAsync(string challengeId, ChallengeRecord record, CancellationToken cancellationToken = default);

    /// <summary>Look up a challenge by id. Returns <see langword="null"/> when missing or expired.</summary>
    Task<ChallengeRecord?> GetAsync(string challengeId, CancellationToken cancellationToken = default);

    /// <summary>Remove a challenge (after consumption or expiry).</summary>
    Task RemoveAsync(string challengeId, CancellationToken cancellationToken = default);
}

/// <summary>Stored secret + metadata for an outstanding challenge.</summary>
/// <param name="UserId">Caller the challenge belongs to (matches <see cref="CallerIdentity.UserId"/>).</param>
/// <param name="Method">Authentication method the challenge fulfils.</param>
/// <param name="Secret">Server-side secret to compare against caller-supplied input (e.g. OTP digits, magic-link token).</param>
/// <param name="ExpiresAt">UTC instant after which the challenge is considered invalid.</param>
/// <param name="AttemptsRemaining">Number of validation attempts the caller has left before lockout.</param>
public sealed record ChallengeRecord(
    string UserId,
    AuthenticationMethod Method,
    string Secret,
    DateTimeOffset ExpiresAt,
    int AttemptsRemaining = 3);

/// <summary>Process-local <see cref="IChallengeStore"/>.</summary>
public sealed class InMemoryChallengeStore : IChallengeStore
{
    private readonly ConcurrentDictionary<string, ChallengeRecord> _records = new(StringComparer.Ordinal);

    public Task SaveAsync(string challengeId, ChallengeRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(challengeId);
        ArgumentNullException.ThrowIfNull(record);
        _records[challengeId] = record;
        return Task.CompletedTask;
    }

    public Task<ChallengeRecord?> GetAsync(string challengeId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(challengeId) || !_records.TryGetValue(challengeId, out var record))
        {
            return Task.FromResult<ChallengeRecord?>(null);
        }
        if (record.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _records.TryRemove(challengeId, out _);
            return Task.FromResult<ChallengeRecord?>(null);
        }
        return Task.FromResult<ChallengeRecord?>(record);
    }

    public Task RemoveAsync(string challengeId, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(challengeId))
        {
            _records.TryRemove(challengeId, out _);
        }
        return Task.CompletedTask;
    }
}
