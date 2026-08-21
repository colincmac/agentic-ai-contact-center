using System.Text.Json;
using System.Text.Json.Nodes;
using Extensions.AI.RealtimeVoice.Configuration;
using Microsoft.Extensions.AI;

namespace Extensions.AI.RealtimeVoice;

/// <summary>Options for configuring an active session.</summary>
public class LiveConversationSessionOptions : ChatOptions
{

    public LiveConversationSessionOptions() { }

    internal LiveConversationSessionOptions(LiveConversationSessionOptions? other) : base(other)
    {
        if (other is null) return;
        Voice = other.Voice;
        Modalities = other.Modalities;
        InputAudioFormat = other.InputAudioFormat;
        OutputAudioFormat = other.OutputAudioFormat;
        InputTranscription = other.InputTranscription;
        TurnDetection = other.TurnDetection;
        InputNoiceReductionType = other.InputNoiceReductionType;

        RawSessionOptionsJson = other.RawSessionOptionsJson;
    }

    /// <summary>Gets or sets the voice for audio responses.</summary>
    public string? Voice { get; set; }

    public ConversationModalitySet? Modalities { get; set; } = [ConversationModality.Text, ConversationModality.Audio];

    /// <summary>Gets or sets the input audio format.</summary>
    public ConversationAudioFormat? InputAudioFormat { get; set; }

    /// <summary>Gets or sets the output audio format.</summary>
    public ConversationAudioFormat? OutputAudioFormat { get; set; }

    /// <summary>Gets or sets the input audio transcription settings.</summary>
    public RealtimeTranscriptionOptions? InputTranscription { get; set; }

    /// <summary>Gets or sets the turn detection settings.</summary>
    public RealtimeTurnDetection? TurnDetection { get; set; } = new RealtimeTurnDetection
    {
        Type = RealtimeTurnDetectionType.SemanticVad,
    };

    public string? InputNoiceReductionType { get; set; } = "azure_deep_noise_suppression";

    public ConversationVoiceOptions? VoiceOptions { get; set; }

    public bool EnableAsyncToolCalls { get; set; } = false;

    public string? RawSessionOptionsJson { get; set; }


    public override LiveConversationSessionOptions Clone() => new(this);
}

public class LiveConversationResponseOptions
{
    public string? ConversationId { get; set; } = null;
 

    /// <summary>Gets or sets the tool choice behavior.</summary>
    public ChatToolMode? ToolMode { get; set; }

    public IList<AITool>? Tools { get; set; }


    public ConversationSelection? ConversationSelection { get; set; } = Configuration.ConversationSelection.Auto;

    public ConversationModalitySet? Modalities { get; set; } = [ConversationModality.Text, ConversationModality.Audio];

    /// <summary>Gets or sets the instructions/system prompt.</summary>
    public string? Instructions { get; set; }

    /// <summary>Gets or sets the temperature for response generation.</summary>
    public float? Temperature { get; set; }

    /// <summary>Gets or sets the maximum response output tokens.</summary>
    public int? MaxResponseOutputTokens { get; set; }

    /// <summary>Gets or sets additional model-specific properties.</summary>
    public IDictionary<string, object?>? AdditionalProperties { get; set; }
    public string? RawResponseOptionsJson { get; set; }


    public LiveConversationResponseOptions Clone()
    {
        return new LiveConversationResponseOptions
        {
            ConversationId = ConversationId,
            ToolMode = ToolMode,
            Modalities = Modalities,
            Instructions = Instructions,
            Temperature = Temperature,
            MaxResponseOutputTokens = MaxResponseOutputTokens,
            AdditionalProperties = AdditionalProperties is null ? null : new Dictionary<string, object?>(AdditionalProperties)
        };
    }
}
