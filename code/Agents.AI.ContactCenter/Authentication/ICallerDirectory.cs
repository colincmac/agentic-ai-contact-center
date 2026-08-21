using System.Collections.Concurrent;
using Agents.AI.ContactCenter.Authentication.Authenticators;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.Authentication;

public interface ICallerIdentity
{
    string UserId { get; }
    CallerVerificationLevel VerificationLevel { get; }
}

public interface ICallerDirectory<TIdentity> where TIdentity : class, ICallerIdentity
{
    Task<TIdentity?> FindByPhoneNumberAsync(string phoneNumberE164, CancellationToken cancellationToken = default);
    Task<TIdentity?> FindByUserIdAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolve a caller by the last four digits of their account / card, optionally
    /// disambiguated by a spoken-name hint. Used by <c>IdentifyByLast4Authenticator</c> to
    /// establish a knowledge-based identity for callers who couldn't be matched by ANI.
    /// Returns <see langword="null"/> when no record matches.
    /// </summary>
    Task<TIdentity?> FindByAccountLast4Async(string last4, string? displayNameHint = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Lightweight directory used by <see cref="AniIdentityLookupAuthenticator"/> to translate the
/// inbound caller's E.164 number into a <see cref="CallerIdentity"/>. Hosts plug their CRM /
/// customer database in by implementing this interface and registering it as a singleton.
/// </summary>
/// <remarks>
/// Returning <see langword="null"/> means "no record matched"; the authenticator will surface
/// an <see cref="AuthenticationOutcome.NotApplicable"/> so the orchestrator falls through to
/// stronger methods (e.g. MFA, biometric) that can establish identity from caller input.
/// </remarks>
public interface ICallerDirectory : ICallerDirectory<CallerIdentity>
{
}

/// <summary>
/// Demo <see cref="ICallerDirectory"/> implementation. Hosts a small set of seeded customer
/// records so the showcase can resolve callers by ANI without a real CRM dependency.
/// Replace with a real lookup service in production.
/// </summary>
public sealed class InMemoryCallerDirectory : ICallerDirectory
{
    private readonly ILogger<InMemoryCallerDirectory> _logger;
    private readonly ConcurrentDictionary<string, CallerIdentity> _byPhone = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryCallerDirectory(ILogger<InMemoryCallerDirectory> logger)
    {
        _logger = logger;

        Seed(new CallerIdentity(
            UserId: "cust-001",
            DisplayName: "Jordan Reyes",
            PhoneNumber: "+14123236796",
            //PhoneNumber: "+15551234567",
            Email: "jordan.reyes@example.com",
            ObjectId: null,
            VerificationLevel: CallerVerificationLevel.AniMatch,
            AuthenticatedAt: DateTimeOffset.UtcNow,
            AuthenticatedBy: nameof(AniIdentityLookupAuthenticator),
            Claims: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["accountTier"] = "Premium",
                ["preferredLanguage"] = "en-US",
                ["pin"] = "4242",
                ["accountLast4"] = "1234",
                ["balance"] = 8421.33m,
                ["balancePending"] = 125.00m
            }));

        Seed(new CallerIdentity(
            UserId: "cust-002",
            DisplayName: "Sam Patel",
            PhoneNumber: "+18146449033",
            //PhoneNumber: "+15559876543",
            Email: "sam.patel@example.com",
            ObjectId: null,
            VerificationLevel: CallerVerificationLevel.AniMatch,
            AuthenticatedAt: DateTimeOffset.UtcNow,
            AuthenticatedBy: nameof(AniIdentityLookupAuthenticator),
            Claims: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["accountTier"] = "Standard",
                ["preferredLanguage"] = "es-MX",
                ["pin"] = "1357",
                ["accountLast4"] = "5678",
                ["balance"] = 312.07m,
                ["balancePending"] = 0m
            }));
    }

    public Task<CallerIdentity?> FindByPhoneNumberAsync(string phoneNumberE164, CancellationToken cancellationToken = default)
    {
        var match = _byPhone.TryGetValue(phoneNumberE164, out var identity) ? identity : null;
        if (match is null)
        {
            _logger.LogInformation("No directory entry for {Phone}", phoneNumberE164);
        }
        else
        {
            _logger.LogInformation("Resolved {UserId} ({DisplayName}) for {Phone}", match.UserId, match.DisplayName, phoneNumberE164);
        }
        return Task.FromResult(match);
    }

    /// <summary>Lookup by user id; used by <see cref="PinChallengeAuthenticator"/> to verify PINs.</summary>
    public Task<CallerIdentity?> FindByUserIdAsync(string userId, CancellationToken cancellationToken = default)
        => Task.FromResult(_byPhone.Values.FirstOrDefault(i => string.Equals(i.UserId, userId, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Lookup by the last four digits of the account, optionally narrowed by spoken name.</summary>
    public Task<CallerIdentity?> FindByAccountLast4Async(string last4, string? displayNameHint = null, CancellationToken cancellationToken = default)
    {
        var candidates = _byPhone.Values.Where(i =>
            i.Claims.TryGetValue("accountLast4", out var v)
            && string.Equals(v?.ToString(), last4, StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(displayNameHint))
        {
            var byName = candidates.FirstOrDefault(i =>
                i.DisplayName.Contains(displayNameHint, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                _logger.LogInformation("Resolved {UserId} by account last-4 + name hint '{Hint}'", byName.UserId, displayNameHint);
                return Task.FromResult<CallerIdentity?>(byName);
            }
        }

        var match = candidates.FirstOrDefault();
        if (match is null)
        {
            _logger.LogInformation("No directory entry for account last-4 {Last4}", last4);
        }
        return Task.FromResult(match);
    }

    private void Seed(IEnumerable<CallerIdentity> identities)
    {
        foreach (var identity in identities)
        {
            Seed(identity);
        }
    }

    private void Seed(CallerIdentity identity)
    {
        if (identity.PhoneNumber is { Length: > 0 } phone)
        {
            _byPhone[phone] = identity;
        }
    }
}
