using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace Extensions.AI.Contents;

public class RealtimeVadContent : AIContent
{
    public RealtimeVadContent(VadEventType vadEvent)
    {
        VadEvent = vadEvent;
    }
    public VadEventType VadEvent { get; set; }
    public DateTime TimeStamp { get; set; } = DateTime.UtcNow;
    public TimeSpan? StartTime { get; set; }
    public TimeSpan? EndTime { get; set; }
}
[JsonConverter(typeof(JsonStringEnumConverter<VadEventType>))]
public enum VadEventType
{
    InputSpeechStarted,
    InputSpeechEnded,
    OutputSpeechStarted,
    OutputSpeechEnded,
}
