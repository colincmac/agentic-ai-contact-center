using System.Security.Claims;

namespace Agents.AI.Extensions.AgentAuthorization;

/// <summary>
/// No-op implementation of IAgentIdentityTokenService for scenarios where agent identity is not required.
/// Use this when you don't need Entra ID integration for agent identities.
/// </summary>
public class NoOpAgentIdentityTokenService : IAgentIdentityTokenService
{
    public Task<string?> AcquireAgentAppTokenAsync(AgentIdentityConfiguration agentIdentity, string scope, CancellationToken ct = default)
    {
        // Return null when tokens are not needed
        return Task.FromResult<string?>(null);
    }

    public Task<string?> AcquireAgentUserTokenAsync(
        AgentIdentityConfiguration agentIdentity,
        string[] scopes,
        ClaimsPrincipal? userContext = null,
        CancellationToken ct = default)
    {
        // Return null when tokens are not needed
        return Task.FromResult<string?>(null);
    }
}
