using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Extensions.AI.Contents;

/// <summary>
/// Represents transcription of audio content.
/// </summary>
/// <remarks>
/// <see cref="AudioTranscriptionContent"/> is distinct from <see cref="TextContent"/>. <see cref="AudioTranscriptionContent"/>
/// is the speech to text transcription of audio performed by the model and is distinct from the actual output text from
/// the model, which is represented by <see cref="TextContent"/>. Neither types derives from the other.
/// </remarks>
public class AudioTranscriptionContent : AIContent
{
    private string? _text;

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioTranscriptionContent"/> class.
    /// </summary>
    /// <param name="text">The text reasoning content.</param>
    public AudioTranscriptionContent(string? text = null, string? referenceItemId = null, int? referenceContentIndex = null)
    {
        _text = text;
        ReferenceItemId = referenceItemId;
        ReferenceContentIndex = referenceContentIndex;
    }

    /// <summary>
    /// Gets or sets the audio transcription content.
    /// </summary>
    [AllowNull]
    public string Text
    {
        get => _text ?? string.Empty;
        set => _text = value;
    }

    /// <summary>Gets or sets the start time of the text segment associated with this update in relation to the full audio speech length.</summary>
    [AllowNull]
    public TimeSpan? StartTime { get; set; }

    /// <summary>Gets or sets the end time of the text segment associated with this update in relation to the full audio speech length.</summary>
    [AllowNull]
    public TimeSpan? EndTime { get; set; }

    /// <summary>
    /// The ID of the item containing the audio that is being transcribed.
    /// </summary>
    public string? ReferenceItemId { get; set; }

    /// <summary>
    /// The index of the content part containing the audio.
    /// </summary>
    public int? ReferenceContentIndex { get; set; }


    /// <inheritdoc/>
    public override string ToString() => Text;


    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private string DebuggerDisplay => $"Transcription = \"{Text}\"";
}



