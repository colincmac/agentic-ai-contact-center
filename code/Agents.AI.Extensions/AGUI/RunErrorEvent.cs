// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json.Serialization;


#if ASPNETCORE
namespace Microsoft.Agents.AI.Hosting.AGUI.AspNetCore.Shared;
#else
namespace Agents.AI.Extensions.AGUI;
#endif

internal sealed class RunErrorEvent : BaseEvent
{
    public RunErrorEvent()
    {
        this.Type = AGUIEventTypes.RunError;
    }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("code")]
    public string? Code { get; set; }
}
