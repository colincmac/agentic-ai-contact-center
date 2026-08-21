using System.Diagnostics.CodeAnalysis;

namespace Agents.AI.Extensions.AgentAuthorization;

public class EntraAgentIdentity
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
