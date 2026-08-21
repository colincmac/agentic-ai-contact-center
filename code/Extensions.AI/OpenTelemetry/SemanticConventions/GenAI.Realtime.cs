using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Extensions.AI.OpenTelemetry.SemanticConventions;

public static partial class GenAI
{
    public const string InvokeAgentName = "invoke_agent";

    public static class Calling
    {
        public const string Workflow = "gen_ai.call.workflow_name";
        public const string CallId = "gen_ai.call.call_id";
        public const string TargetUri = "gen_ai.call.call_id";

    }

    public static class Realtime
    {
        /// <summary>
        /// The unique identifier for the real-time session.
        /// </summary>
        public const string SessionId = "gen_ai.realtime.session_id";

        /// <summary>
        /// The kind/type of realtime session (e.g., "TextInTextOut", "AudioInAudioOut").
        /// Custom attribute: "gen_ai.realtime.session_kind".
        /// </summary>
        public const string SessionKind = "gen_ai.realtime.session_kind";

        /// <summary>
        /// The type of real-time event (e.g., conversation.item.created, response.audio.delta).
        /// </summary>
        public const string EventType = "gen_ai.realtime.event_type";

        /// <summary>
        /// The modality of the real-time interaction (e.g., text, audio).
        /// </summary>
        public const string OutputModalities = "gen_ai.realtime.output_modalities";

        /// <summary>
        /// The modalities actually received in a realtime response (e.g., "text", "audio", "transcription").
        /// Custom attribute: "gen_ai.realtime.received_modalities".
        /// </summary>
        public const string ReceivedModalities = "gen_ai.realtime.received_modalities";

        /// <summary>
        /// The voice used for audio responses (e.g., alloy, echo, shimmer).
        /// </summary>
        public const string Voice = "gen_ai.realtime.voice";

        /// <summary>
        /// Indicates whether turn detection is enabled.
        /// </summary>
        public const string TurnDetectionEnabled = "gen_ai.realtime.turn_detection.enabled";

        /// <summary>
        /// The type of turn detection (e.g., server_vad).
        /// </summary>
        public const string TurnDetectionType = "gen_ai.realtime.turn_detection.type";

        /// <summary>
        /// The threshold value used for turn detection.
        /// </summary>
        public const string TurnDetectionThreshold = "gen_ai.realtime.turn_detection.threshold";

        /// <summary>
        /// The silence duration in milliseconds for turn detection.
        /// </summary>
        public const string TurnDetectionSilenceDurationMs = "gen_ai.realtime.turn_detection.silence_duration_ms";

        /// <summary>
        /// The audio format (e.g., pcm16, g711_ulaw, g711_alaw).
        /// </summary>
        public const string AudioFormat = "gen_ai.realtime.audio.format";

        /// <summary>
        /// The audio sample rate.
        /// </summary>
        public const string AudioSampleRate = "gen_ai.realtime.audio.sample_rate";

        /// <summary>
        /// The transcript of the audio content.
        /// </summary>
        public const string AudioTranscript = "gen_ai.realtime.audio.transcript";

        /// <summary>
        /// The duration of the audio in milliseconds.
        /// </summary>
        public const string AudioDurationMs = "gen_ai.realtime.audio.duration_ms";

        /// <summary>
        /// Time to first byte latency in milliseconds.
        /// </summary>
        public const string LatencyFirstByteMs = "gen_ai.realtime.latency.first_byte_ms";

        /// <summary>
        /// The duration of the websocket connection in milliseconds.
        /// </summary>
        public const string ConnectionDurationMs = "gen_ai.realtime.connection.duration_ms";

        /// <summary>
        /// The number of items in the conversation.
        /// </summary>
        public const string ConversationItemsCount = "gen_ai.realtime.conversation.items.count";
    }
}
