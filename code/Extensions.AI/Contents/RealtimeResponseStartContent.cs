using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.AI;

namespace Extensions.AI.Contents;

public class RealtimeResponseStartContent : AIContent
{
    public RealtimeResponseStartContent(string? referenceItemId = null)
    {
        ReferenceItemId = referenceItemId;
    }
    public string? ReferenceItemId { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
}

