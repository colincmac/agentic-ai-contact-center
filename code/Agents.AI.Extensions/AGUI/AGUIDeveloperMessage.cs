// Copyright (c) Microsoft. All rights reserved.

#if ASPNETCORE
namespace Microsoft.Agents.AI.Hosting.AGUI.AspNetCore.Shared;
#else
namespace Agents.AI.Extensions.AGUI;
#endif

internal sealed class AGUIDeveloperMessage : AGUIMessage
{
    public AGUIDeveloperMessage()
    {
        this.Role = AGUIRoles.Developer;
    }
}
