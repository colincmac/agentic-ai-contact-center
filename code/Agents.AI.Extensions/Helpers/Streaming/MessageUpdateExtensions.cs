using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agents.AI.Extensions.Helpers.Streaming;

public static class MessageUpdateExtensions
{

    public static MessageUpdate FromChatMessage(ChatMessage update, string? responseId = null, string? conversationId = null) => new()
    {
        Contents = update.Contents,
        CreatedAt = update.CreatedAt,
        Role = update.Role.ToString(),
        MessageId = update.MessageId,
        ConversationId = conversationId ?? update.MessageId,
        ResponseId = responseId ?? update.MessageId,
        SenderParticipantId = update.AuthorName,
        RawRepresentation = update,
    };
    public static MessageUpdate FromChatResponseUpdate(ChatResponseUpdate update) => new()
    {
        Contents = update.Contents,
        CreatedAt = update.CreatedAt,
        Role = update.Role.ToString(),
        MessageId = update.MessageId,
        ConversationId = update.ConversationId,
        ResponseId = update.ResponseId,
        SenderParticipantId = update.AuthorName,
        RawRepresentation = update,
    };

    public static MessageUpdate FromAgentResponseUpdate(AgentResponseUpdate update) => new()
    {
        Contents = update.Contents,
        CreatedAt = update.CreatedAt,
        Role = update.Role.ToString(),
        MessageId = update.MessageId,
        ResponseId = update.ResponseId,
        SenderParticipantId = update.AgentId,
        RawRepresentation = update,
    };


    public static IEnumerable<MessageUpdate> FromAgentResponse(AgentResponse response) => response.Messages.Select(mu => FromChatMessage(mu, responseId: response.ResponseId));

    public static ChatResponseUpdate ToChatResponseUpdate(this MessageUpdate update)
    {
        var roleParsed = !string.IsNullOrEmpty(update.Role) ? new ChatRole(update.Role) : default;

        return new ChatResponseUpdate
        {
            Contents = update.Contents,
            CreatedAt = update.CreatedAt,
            Role = roleParsed,
            MessageId = update.MessageId,
            ConversationId = update.ConversationId,
            ResponseId = update.ResponseId,
            AuthorName = update.SenderParticipantId,
            RawRepresentation = update.RawRepresentation,
        };
    }

    public static ChatMessage ToChatMessage(this MessageUpdate update)
    {
        var roleParsed = !string.IsNullOrEmpty(update.Role) ? new ChatRole(update.Role) : default;

        return new ChatMessage
        {
            Contents = update.Contents,
            CreatedAt = update.CreatedAt,
            Role = roleParsed,
            MessageId = update.MessageId,
            AuthorName = update.SenderParticipantId,
            RawRepresentation = update.RawRepresentation
        };
    }

    public static AgentResponseUpdate ToAgentResponseUpdate(this MessageUpdate update)
    {
        var roleParsed = !string.IsNullOrEmpty(update.Role) ? new ChatRole(update.Role) : default;

        return new AgentResponseUpdate
        {
            Contents = update.Contents,
            CreatedAt = update.CreatedAt,
            Role = roleParsed,
            MessageId = update.MessageId,
            ResponseId = update.ResponseId,
            AgentId = update.SenderParticipantId,
            RawRepresentation = update.RawRepresentation
        };
    }
}
