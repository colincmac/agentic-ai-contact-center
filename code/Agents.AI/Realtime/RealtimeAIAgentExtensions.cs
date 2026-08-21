using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Shared.Diagnostics;

namespace Agents.AI.Realtime;

public static class RealtimeAIAgentExtensions
{
    public static Task SendAsync(
        this IRealtimeAgent realtimeAIAgent,
        RealtimeAIAgentSession session,
        ChatMessage message,
        CancellationToken cancellationToken = default)
    {
        _ = Throw.IfNull(session);
        _ = Throw.IfNull(message);
        return realtimeAIAgent.SendAsync(session, FromChatMessage(message), cancellationToken);
    }
    public static Task SendAudioAsync(
    this IRealtimeAgent realtimeAIAgent,
    RealtimeAIAgentSession session,
    DataContent audioContent,
    CancellationToken cancellationToken = default)
    {
        _ = Throw.IfNull(session);
        _ = Throw.IfNull(audioContent);
        return realtimeAIAgent.SendAsync(session, FromAudioContent(audioContent), cancellationToken);
    }

    public static RealtimeClientMessage FromChatMessage(this ChatMessage chatMessage)
    {
        _ = Throw.IfNull(chatMessage);
        return new CreateConversationItemRealtimeClientMessage(new RealtimeConversationItem(contents: chatMessage.Contents, role: chatMessage.Role))
        {
            MessageId = chatMessage.MessageId,
            RawRepresentation = chatMessage
        };
    }

    public static RealtimeClientMessage FromAudioContent(this DataContent audioContent)
    {
        _ = Throw.IfNull(audioContent);
        return new InputAudioBufferAppendRealtimeClientMessage(audioContent);
    }
}
