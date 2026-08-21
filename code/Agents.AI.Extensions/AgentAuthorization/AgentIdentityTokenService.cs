using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Web;

namespace Agents.AI.Extensions.AgentAuthorization;

public interface IAgentIdentityTokenService
{
    Task<string?> AcquireAgentAppTokenAsync(AgentIdentityConfiguration agentIdentity, string scope, CancellationToken ct = default);
    Task<string?> AcquireAgentUserTokenAsync(AgentIdentityConfiguration agentIdentity, string[] scopes, ClaimsPrincipal? userContext = null, CancellationToken ct = default);
}

public class AgentIdentityTokenService(IHttpContextAccessor httpContextAccessor) : IAgentIdentityTokenService
{
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;

    public async Task<string?> AcquireAgentAppTokenAsync(AgentIdentityConfiguration agentIdentity, string scope, CancellationToken ct)
    {
        var authHeaderProvider = _httpContextAccessor.HttpContext?.RequestServices.GetRequiredService<IAuthorizationHeaderProvider>()
            ?? throw new InvalidOperationException("IAuthorizationHeaderProvider service is not available.");
        var options = new AuthorizationHeaderProviderOptions()
            .WithAgentIdentity(agentIdentity.AgentId);
        var header = await authHeaderProvider.CreateAuthorizationHeaderForAppAsync(scope, options, ct);
        return ExtractToken(header);
    }

    // For your interactive agent application to acquire user tokens for an agent identity on behalf of the user calling a web API
    public async Task<string?> AcquireAgentUserTokenAsync(
        AgentIdentityConfiguration agentIdentity,
        string[] scopes,
        ClaimsPrincipal? userContext = null,
        CancellationToken ct = default)
    {
        var authHeaderProvider = _httpContextAccessor.HttpContext?.RequestServices.GetRequiredService<IAuthorizationHeaderProvider>()
            ?? throw new InvalidOperationException("IAuthorizationHeaderProvider service is not available.");
        var options = !string.IsNullOrEmpty(agentIdentity.AgentUserUpn)
            ? new AuthorizationHeaderProviderOptions().WithAgentUserIdentity(agentIdentity.AgentId, agentIdentity.AgentUserUpn)
            : new AuthorizationHeaderProviderOptions().WithAgentUserIdentity(agentIdentity.AgentId, Guid.Parse(agentIdentity.AgentUserObjectId!));

        var header = await authHeaderProvider.CreateAuthorizationHeaderForUserAsync(scopes, options, userContext, ct);
        return ExtractToken(header);
    }

    private static string? ExtractToken(string authorizationHeader)
    {
        return AuthenticationHeaderValue.TryParse(authorizationHeader, out var headerValue) ? headerValue.Parameter : null;
    }
}
