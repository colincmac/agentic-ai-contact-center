using System.Text.Json.Serialization;

namespace Agents.AI.Extensions.AgentAuthorization.AgentIdentity;


public class AgentIdentityGraphModel
{
    [JsonPropertyName("@odata.type")]
    public string @odata_type { get; set; } = "#Microsoft.Graph.AgentIdentity";

    [JsonPropertyName("displayName")]
    public string? displayName { get; set; }

    [JsonPropertyName("agentIdentityBlueprintId")]
    public string? agentIdentityBlueprintId { get; set; }

    [JsonPropertyName("id")]
    public string? id { get; set; }

    [JsonPropertyName("sponsors@odata.bind")]
    public string[]? sponsorsOdataBind { get; set; }

    [JsonPropertyName("owners@odata.bind")]
    public string[]? ownersOdataBind { get; set; }
}
public class AgentUserIdentity
{
    [JsonPropertyName("@odata.type")]
    public string @odata_type { get; set; } = "#Microsoft.Graph.AgentUser";

    [JsonPropertyName("displayName")]
    public string? displayName { get; set; }

    // "agentuserupn@tenant.onmicrosoft.com"
    [JsonPropertyName("userPrincipalName")]
    public string? userPrincipalName { get; set; }

    [JsonPropertyName("id")]
    public string? id { get; set; }

    // Parent agent identity ID
    [JsonPropertyName("identityParentId")]
    public string? identityParentId { get; set; }

    [JsonPropertyName("mailNickname")]
    public string? mailNickname { get; set; }

    [JsonPropertyName("accountEnabled")]
    public bool accountEnabled { get; set; } = true;
}
public class AgentUserIdentityRequest
{
    [JsonPropertyName("displayName")]
    public string? displayName { get; set; }

    // "agentuserupn@tenant.onmicrosoft.com"
    [JsonPropertyName("userPrincipalName")]
    public string? userPrincipalName { get; set; }

    [JsonPropertyName("mailNickname")]
    public string? mailNickname { get; set; }

    [JsonPropertyName("accountEnabled")]
    public bool accountEnabled { get; set; } = true;
}
