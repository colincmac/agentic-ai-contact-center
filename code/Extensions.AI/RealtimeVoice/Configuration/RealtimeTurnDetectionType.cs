using System.Text.Json.Serialization;

namespace Extensions.AI.RealtimeVoice.Configuration;

/// <summary>Turn detection types.</summary>
[JsonConverter(converterType: typeof(JsonStringEnumConverter))]
public enum RealtimeTurnDetectionType
{
    /// <summary>Turn detection is disabled.</summary>
    Disabled,

    /// <summary>Server-side voice activity detection.</summary>
    ServerVad,

    SemanticVad,

    OpenAISemanticVad,

    AzureSemanticVadEN,
    AzureSemanticVadMultiLingual,

}

