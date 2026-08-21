using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace Extensions.AI.RealtimeVoice.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConversationVoiceProvider
{
    Azure,
    OpenAI,
    Custom
}
public class ConversationVoiceOptions
{
    public required string Name { get; set; }
    public ConversationVoiceProvider Provider { get; set; } = ConversationVoiceProvider.OpenAI;
    public float? Temperature { get; set; } = default;
}
