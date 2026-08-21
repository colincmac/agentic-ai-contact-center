using System.Globalization;
using System.Security.Claims;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Agents.AI.ContactCenter.Authentication;

/// <summary>
/// Identity established for the caller on an active <see cref="ICallSession"/>.
/// One instance is built per successful authenticator, then merged into the
/// session-scoped <see cref="CallerAuthenticationState"/>.
/// </summary>
/// <param name="UserId">Stable user identifier in the back-office system (account number, CRM id, etc.).</param>
/// <param name="DisplayName">Human-readable name to address the caller in conversation.</param>
/// <param name="PhoneNumber">E.164 phone number associated with this identity, if known.</param>
/// <param name="Email">Email associated with this identity, if known.</param>
/// <param name="ObjectId">Microsoft Entra object id, if the caller is an enterprise user.</param>
/// <param name="VerificationLevel">Strength of the verification that produced this identity.</param>
/// <param name="AuthenticatedAt">UTC timestamp at which the verifying authenticator finished.</param>
/// <param name="AuthenticatedBy">Name of the <see cref="ICallerAuthenticator"/> that produced this identity.</param>
/// <param name="Claims">Arbitrary claims surfaced by the authenticator (department, tier, account flags, etc.).</param>
/// <param name="Methods">Authentication methods used to establish this identity.</param>
public sealed record CallerIdentity(
    string UserId,
    string DisplayName,
    string? PhoneNumber,
    string? Email,
    string? ObjectId,
    CallerVerificationLevel VerificationLevel,
    DateTimeOffset AuthenticatedAt,
    string AuthenticatedBy,
    IReadOnlyDictionary<string, object?> Claims,
    IReadOnlySet<string>? Methods = null): ICallerIdentity
{
    private static readonly IReadOnlySet<string> emptyMethods =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static CallerIdentity Anonymous { get; } = new(
        UserId: "anonymous",
        DisplayName: "Anonymous Caller",
        PhoneNumber: null,
        Email: null,
        ObjectId: null,
        VerificationLevel: CallerVerificationLevel.None,
        AuthenticatedAt: DateTimeOffset.MinValue,
        AuthenticatedBy: "(none)",
        Claims: new Dictionary<string, object?>(),
        Methods: emptyMethods);

    public IReadOnlySet<string> AuthenticationMethods => Methods ?? emptyMethods;


    /// <summary>
    /// Returns a copy of this identity with the provided <paramref name="level"/> if it is stronger
    /// than the current one, otherwise returns the current instance.
    /// </summary>
    public CallerIdentity WithAtLeast(CallerVerificationLevel level)
        => level > VerificationLevel ? this with { VerificationLevel = level } : this;

    /// <summary>Returns a copy with <paramref name="method"/> added to the method set.</summary>
    public CallerIdentity WithMethod(string method)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        var next = new HashSet<string>(AuthenticationMethods, StringComparer.OrdinalIgnoreCase) { method };
        return this with { Methods = next };
    }

    public ClaimsPrincipal ToClaimsPrincipal()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, UserId),
            new(ClaimTypes.Name, DisplayName),
            // acr/amr are the standard OIDC vocabulary for "level of assurance" and "methods used"
            new(JwtRegisteredClaimNames.Acr, ((int)VerificationLevel).ToString(CultureInfo.InvariantCulture)),
            new(JwtRegisteredClaimNames.AuthTime, AuthenticatedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
        };

        foreach (var method in AuthenticationMethods.Count == 0 ? new[] { AuthenticatedBy } : (IEnumerable<string>)AuthenticationMethods)
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Amr, method));
        }

        if (PhoneNumber is not null) { claims.Add(new Claim(JwtRegisteredClaimNames.PhoneNumber, PhoneNumber)); }
        if (Email is not null) { claims.Add(new Claim(ClaimTypes.Email, Email)); }
        if (ObjectId is not null) { claims.Add(new Claim(ClaimConstants.Oid, ObjectId)); }

        foreach (var (key, value) in Claims)
        {
            if (value is not null) { claims.Add(new Claim(key, value.ToString() ?? string.Empty)); }
        }

        var identity = new ClaimsIdentity(claims, authenticationType: AuthenticatedBy);
        return new ClaimsPrincipal(identity);
    }
}
