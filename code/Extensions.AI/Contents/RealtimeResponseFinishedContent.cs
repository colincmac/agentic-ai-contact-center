using Microsoft.Extensions.AI;

namespace Extensions.AI.Contents;

public class RealtimeResponseFinishedContent : AIContent
{
    public RealtimeResponseFinishedContent(string? referenceItemId = null)
    {
        ReferenceItemId = referenceItemId;
    }
    public string? ReferenceItemId { get; set; }
    public DateTimeOffset FinishedAt { get; set; } = DateTimeOffset.UtcNow;
}
