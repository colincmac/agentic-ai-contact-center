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

    /// <summary>Atomically verify, decrement retries, and consume successful challenges.</summary>
    Task<bool> TryValidateAsync(string challengeId, string callId, string userId, string secret,
        CancellationToken cancellationToken = default);
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
    int AttemptsRemaining = 3,
    string? CallId = null);

/// <summary>Process-local <see cref="IChallengeStore"/>.</summary>
public sealed class InMemoryChallengeStore : IChallengeStore
{
    private readonly ConcurrentDictionary<string, ChallengeRecord> _records = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public Task<bool> TryValidateAsync(string challengeId, string callId, string userId, string secret,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_records.TryGetValue(challengeId, out var record)) { return Task.FromResult(false); }
            if (record.ExpiresAt <= DateTimeOffset.UtcNow || record.AttemptsRemaining <= 0)
            {
                _records.TryRemove(challengeId, out _);
                return Task.FromResult(false);
            }
            if (record.UserId != userId || record.CallId != callId) { return Task.FromResult(false); }
            var match = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(record.Secret), System.Text.Encoding.UTF8.GetBytes(secret));
            if (match || record.AttemptsRemaining == 1) { _records.TryRemove(challengeId, out _); }
            else { _records[challengeId] = record with { AttemptsRemaining = record.AttemptsRemaining - 1 }; }
            return Task.FromResult(match);
        }
    }

    public Task SaveAsync(string challengeId, ChallengeRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(challengeId);
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate) { _records[challengeId] = record; }
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
            lock (_gate) { _records.TryRemove(challengeId, out _); }
            return Task.FromResult<ChallengeRecord?>(null);
        }
        return Task.FromResult<ChallengeRecord?>(record);
    }

    public Task RemoveAsync(string challengeId, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(challengeId))
        {
            lock (_gate) { _records.TryRemove(challengeId, out _); }
        }
        return Task.CompletedTask;
    }
}
