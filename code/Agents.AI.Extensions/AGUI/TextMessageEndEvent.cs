// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json.Serialization;


#if ASPNETCORE
namespace Microsoft.Agents.AI.Hosting.AGUI.AspNetCore.Shared;
#else
namespace Agents.AI.Extensions.AGUI;
#endif

internal sealed class TextMessageEndEvent : BaseEvent
{
    public TextMessageEndEvent()
    {
        this.Type = AGUIEventTypes.TextMessageEnd;
    }

    [JsonPropertyName("messageId")]
    public string MessageId { get; set; } = string.Empty;
}
