using System.Diagnostics.CodeAnalysis;

namespace Agents.AI.Extensions.AgentAuthorization;

public record AgentIdentityConfiguration
{
    /// <summary>
    /// The GUID (client id) of the Agent Identity to use (not the blueprint app id).
    /// </summary>
    [StringSyntax(StringSyntaxAttribute.GuidFormat)]
    public required string AgentId { get; init; }

    /// <summary>
    /// Optional UPN to resolve agent user identity (fallback to OID if provided separately).
    /// </summary>
    public string? AgentUserUpn { get; set; }

    /// <summary>
    /// Optional user object id (OID) if no UPN.
    /// </summary>
    public string? AgentUserObjectId { get; set; }
}

public record AgentIdentityAuthorizationContext
{
    public string? AgentId { get; init; }
    public string? ParentAgentBlueprintId { get; init; }
    public AgentIdentityMode? IdentityMode { get; init; } // "AgentAppOnly" | "AgentUser" | "OBO" | "Hybrid"
    public string? ExecutingUserUpn { get; init; }
    public string? ExecutingUserOid { get; init; }
}

public enum AgentIdentityMode
{
    AgentAppOnly,
    AgentUser,
    OBO,
    Hybrid
}
