using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Agents.AI.Extensions.Helpers.Streaming;

public class MessageUpdate
{
    [JsonConstructor]
    public MessageUpdate()
    {
    }
    private IList<AIContent>? _contents;

    [AllowNull]
    public IList<AIContent> Contents
    {
        get => _contents ??= [];
        set => _contents = value;
    }

    public string? SenderParticipantId { get; set; }

    /// <summary>
    /// When set, the session router delivers this message only to the
    /// specified participant instead of broadcasting to all participants.
    /// </summary>
    public string? TargetParticipantId { get; set; }

    public string? Role { get; set; }
    public string? ResponseId { get; set; }
    public string? MessageId { get; set; }
    public string? ConversationId { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }

    public object? RawRepresentation { get; set; }

}
