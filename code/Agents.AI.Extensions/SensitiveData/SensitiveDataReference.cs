using System.Text.Json.Serialization;

namespace Agents.AI.Extensions.SensitiveData;

/// <summary>
/// Represents a reference to sensitive data stored elsewhere.
/// </summary>
public sealed class SensitiveDataReference
{
    public SensitiveDataReference(string referenceToken)
    {
        ReferenceToken = referenceToken;
    }

    [JsonPropertyName("$ref")]
    public string ReferenceToken { get; }

    [JsonPropertyName("$type")]
    public string? Type { get; }
}
