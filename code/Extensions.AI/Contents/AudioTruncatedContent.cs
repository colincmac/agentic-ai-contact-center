using Microsoft.Extensions.AI;

namespace Extensions.AI.Contents;

public class AudioTruncatedContent : AIContent
{
    public AudioTruncatedContent(string? referenceItemId = null, int? audioEndMs = null)
    {
        AudioEndMs = audioEndMs;
        ReferenceItemId = referenceItemId;
    }
    public string? ReferenceItemId { get; set; }
    public int? AudioEndMs { get; set; }
}
