using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.Media.Transcription;

/// <summary>
/// A single segment of transcribed speech, produced by an STT engine or
/// extracted from a realtime AI model response.
/// </summary>
public sealed class TranscriptSegment
{
    /// <summary>The transcribed text for this segment.</summary>
    public required string Text { get; init; }

    /// <summary>Speaker role (e.g. "user", "assistant").</summary>
    public ChatRole? Role { get; init; }

    /// <summary>Whether this is a final (committed) segment or an interim hypothesis.</summary>
    public bool IsFinal { get; init; }

    /// <summary>Timestamp when the utterance started.</summary>
    public DateTimeOffset? UtteranceStart { get; init; }

    /// <summary>Timestamp when the utterance ended (null for interim segments).</summary>
    public DateTimeOffset? UtteranceEnd { get; init; }

    /// <summary>Confidence score from the STT engine, if available (0.0–1.0).</summary>
    public double? Confidence { get; init; }
}
