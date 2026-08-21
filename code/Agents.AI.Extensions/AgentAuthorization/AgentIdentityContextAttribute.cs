namespace Agents.AI.Extensions.AgentAuthorization;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class AgentIdentityContextAttribute : Attribute
{
    /// <summary>
    /// The GUID (client id) of the Agent Identity to use (not the blueprint app id).
    /// </summary>
    public string AgentId { get; }

    /// <summary>
    /// If true, acquire a user token (Agent User Identity). If false, app-only token for agent identity.
    /// </summary>
    public bool UseAgentUserIdentity { get; }

    /// <summary>
    /// Optional UPN to resolve agent user identity (fallback to OID if provided separately).
    /// </summary>
    public string? UserUpn { get; set; }

    /// <summary>
    /// Optional user object id (OID) if no UPN.
    /// </summary>
    public string? UserObjectId { get; set; }

    /// <summary>
    /// Scopes (resource) for token acquisition (Graph / custom API / Azure resource).
    /// </summary>
    public string[] Scopes { get; set; } = Array.Empty<string>();

    /// <summary>
    /// If set, require that approval was granted before token acquisition.
    /// </summary>
    public bool RequireApprovalGate { get; set; } = true;

    /// <summary>
    /// Whether to assert the agent user facet (validate xms_sub_fct claim after acquisition).
    /// </summary>
    public bool EnforceAgentUserFacet { get; set; }

    public AgentIdentityContextAttribute(string agentId, bool useAgentUserIdentity = false)
    {
        AgentId = agentId;
        UseAgentUserIdentity = useAgentUserIdentity;
    }
}



//public partial record Approval
//{
//    public string? AgentId { get; init; }
//    public string? ParentAgentBlueprintId { get; init; }
//    public string? IdentityMode { get; init; } // "AgentAppOnly" | "AgentUser" | "OBO" | "Hybrid"
//    public string? ExecutingUserUpn { get; init; }
//    public string? ExecutingUserOid { get; init; }
//}


