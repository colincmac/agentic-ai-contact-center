using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace Agents.AI.ContactCenter.Authorization.IdentityVerification;

public class DidOptions
{
    public const string ConfigurationSectionName = "DidOptions";
    [ConfigurationKeyName("DidConfiguration")]
    public required DidConfiguration Configuration { get; set; }
    [ConfigurationKeyName("DidDocument")]
    public required DidDocument Document { get; set; }
}

/// <summary>
/// Raw (untyped) DID options for scenarios where the JSON needs to be returned exactly as configured.
/// </summary>
public class DidRawOptions
{
    public const string ConfigurationSectionName = DidOptions.ConfigurationSectionName;

    [ConfigurationKeyName("DidDocument")]
    public JsonElement DidDocument { get; set; }

    [ConfigurationKeyName("DidConfiguration")]
    public JsonElement DidConfiguration { get; set; }

    /// <summary>
    /// Indicates whether both JSON elements were populated.
    /// </summary>
    public bool IsLoaded => DidDocument.ValueKind != JsonValueKind.Undefined && DidConfiguration.ValueKind != JsonValueKind.Undefined;
}

public class DidConfiguration
{
    [JsonPropertyName("@context")]
    public required string Context { get; set; }

    [JsonPropertyName("linked_dids")]
    public required List<string> LinkedDids { get; set; }
}
/// <summary>
/// Represents a Decentralized Identifier (DID) document.
/// </summary>
public class DidDocument
{

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("@context")]
    [ConfigurationKeyName("@context")]
    public JsonArray Context { get; set; } = new();

    [JsonPropertyName("service")]
    public List<DidService> Service { get; set; } = new();

    [JsonPropertyName("verificationMethod")]
    public List<VerificationMethod> VerificationMethod { get; set; } = new();

    [JsonPropertyName("authentication")]
    public List<string> Authentication { get; set; } = new();

    [JsonPropertyName("assertionMethod")]
    public List<string> AssertionMethod { get; set; } = new();
}

/// <summary>
/// Represents an item in the DID document context which can be a URI string or an object with a base.
/// </summary>
[JsonConverter(typeof(ContextItemJsonConverter))]
public class ContextItem
{
    [JsonIgnore]
    public bool IsObject => Base is not null;

    [JsonPropertyName("@base")]
    public string? Base { get; set; }

    [JsonIgnore]
    public string? Uri { get; set; }
}

public class ContextItemJsonConverter : JsonConverter<ContextItem>
{
    public override ContextItem? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var uri = reader.GetString();
            return new ContextItem { Uri = uri };
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            string? baseValue = null;
            if (root.TryGetProperty("@base", out var baseProp) && baseProp.ValueKind == JsonValueKind.String)
            {
                baseValue = baseProp.GetString();
            }
            return new ContextItem { Base = baseValue };
        }

        throw new JsonException("Invalid @context element; expected string or object with '@base'.");
    }

    public override void Write(Utf8JsonWriter writer, ContextItem value, JsonSerializerOptions options)
    {
        if (value.Base is not null)
        {
            writer.WriteStartObject();
            writer.WriteString("@base", value.Base);
            writer.WriteEndObject();
            return;
        }

        if (value.Uri is not null)
        {
            writer.WriteStringValue(value.Uri);
            return;
        }

        writer.WriteNullValue();
    }
}

public class DidService
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("serviceEndpoint")]
    public ServiceEndpoint ServiceEndpoint { get; set; } = new();
}

public class ServiceEndpoint
{
    [JsonPropertyName("origins")]
    public List<string>? Origins { get; set; }

    [JsonPropertyName("instances")]
    public List<string>? Instances { get; set; }
}

public class VerificationMethod
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("controller")]
    public string Controller { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("publicKeyJwk")]
    public PublicKeyJwk PublicKeyJwk { get; set; } = new();
}

public class PublicKeyJwk
{
    [JsonPropertyName("crv")]
    public string Crv { get; set; } = string.Empty;

    [JsonPropertyName("kty")]
    public string Kty { get; set; } = string.Empty;

    [JsonPropertyName("x")]
    public string X { get; set; } = string.Empty;

    [JsonPropertyName("y")]
    public string Y { get; set; } = string.Empty;
}
