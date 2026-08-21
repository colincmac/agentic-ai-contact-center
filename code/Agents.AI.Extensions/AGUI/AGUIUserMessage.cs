// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json.Serialization;

#if ASPNETCORE
namespace Microsoft.Agents.AI.Hosting.AGUI.AspNetCore.Shared;
#else
namespace Agents.AI.Extensions.AGUI;
#endif

internal sealed class AGUIUserMessage : AGUIMessage
{
    public AGUIUserMessage()
    {
        this.Role = AGUIRoles.User;
    }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
