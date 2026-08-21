using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.Extensions.Helpers.Streaming;
using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.Tests;

public class CoreModelTests
{
    [Fact]
    public void RealtimeConversationTurn_DefaultValues()
    {
        // Arrange & Act
        var turn = new RealtimeConversationTurn();

        // Assert
        Assert.Equal(default, turn.Timestamp);
        Assert.Null(turn.UserMessageText);
        Assert.Null(turn.AgentResponseText);
        Assert.NotNull(turn.Metadata);
        Assert.Empty(turn.Metadata);
    }

    [Fact]
    public void RealtimeConversationTurn_WithValues()
    {
        // Arrange & Act
        var timestamp = DateTimeOffset.UtcNow;
        var turn = new RealtimeConversationTurn
        {
            Timestamp = timestamp,
            UserMessageText = "Hello",
            AgentResponseText = "Hi there!",
            Metadata = new Dictionary<string, object> { { "key", "value" } }
        };

        // Assert
        Assert.Equal(timestamp, turn.Timestamp);
        Assert.Equal("Hello", turn.UserMessageText);
        Assert.Equal("Hi there!", turn.AgentResponseText);
        Assert.Single(turn.Metadata);
        Assert.Equal("value", turn.Metadata["key"]);
    }

    [Fact]
    public void MessageUpdate_DefaultValues()
    {
        // Arrange & Act
        var message = new MessageUpdate();

        // Assert
        Assert.NotNull(message.Contents);
        Assert.Empty(message.Contents);
        Assert.Null(message.SenderParticipantId);
        Assert.Null(message.Role);
        Assert.Null(message.ResponseId);
        Assert.Null(message.MessageId);
        Assert.Null(message.ConversationId);
        Assert.Null(message.CreatedAt);
        Assert.Null(message.RawRepresentation);
    }

    [Fact]
    public void MessageUpdate_WithContents()
    {
        // Arrange & Act
        var message = new MessageUpdate
        {
            SenderParticipantId = "sender-123",
            Role = "user",
            CreatedAt = DateTimeOffset.UtcNow,
            Contents = [new TextContent("Hello world")]
        };

        // Assert
        Assert.Equal("sender-123", message.SenderParticipantId);
        Assert.Equal("user", message.Role);
        Assert.NotNull(message.CreatedAt);
        Assert.Single(message.Contents);
        Assert.IsType<TextContent>(message.Contents[0]);
        Assert.Equal("Hello world", ((TextContent)message.Contents[0]).Text);
    }

    [Fact]
    public void MessageUpdate_Contents_LazyInitialization()
    {
        // Arrange
        var message = new MessageUpdate();

        // Act - Access contents before setting
        var contents = message.Contents;

        // Assert - Should get empty list, not null
        Assert.NotNull(contents);
        Assert.Empty(contents);
    }

    [Fact]
    public void MessageUpdate_Contents_CanAddItems()
    {
        // Arrange
        var message = new MessageUpdate();

        // Act
        message.Contents.Add(new TextContent("First"));
        message.Contents.Add(new TextContent("Second"));

        // Assert
        Assert.Equal(2, message.Contents.Count);
    }

    [Fact]
    public void MessageUpdate_AllProperties()
    {
        // Arrange
        var timestamp = DateTimeOffset.UtcNow;

        // Act
        var message = new MessageUpdate
        {
            SenderParticipantId = "sender-1",
            Role = "assistant",
            ResponseId = "response-123",
            MessageId = "message-456",
            ConversationId = "conv-789",
            CreatedAt = timestamp,
            RawRepresentation = new { custom = "data" }
        };

        // Assert
        Assert.Equal("sender-1", message.SenderParticipantId);
        Assert.Equal("assistant", message.Role);
        Assert.Equal("response-123", message.ResponseId);
        Assert.Equal("message-456", message.MessageId);
        Assert.Equal("conv-789", message.ConversationId);
        Assert.Equal(timestamp, message.CreatedAt);
        Assert.NotNull(message.RawRepresentation);
    }
}

