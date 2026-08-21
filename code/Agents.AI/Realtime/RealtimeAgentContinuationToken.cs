using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Shared.Diagnostics;

namespace Agents.AI.Realtime;

internal class RealtimeAgentContinuationToken : ResponseContinuationToken
{
    private const string TokenTypeName = "realtimeAgentContinuationToken";
    private const string TypeDiscriminator = "type";

    /// <summary>
    /// Initializes a new instance of the <see cref="RealtimeAgentContinuationToken"/> class.
    /// </summary>
    /// <param name="innerToken">A continuation token provided by the underlying <see cref="IRealtimeClient"/>.</param>
    [JsonConstructor]
    internal RealtimeAgentContinuationToken(ResponseContinuationToken innerToken)
    {
        this.InnerToken = innerToken;
    }

    public override ReadOnlyMemory<byte> ToBytes()
    {
        using MemoryStream stream = new();
        using Utf8JsonWriter writer = new(stream);

        writer.WriteStartObject();

        // This property should be the first one written to identify the type during deserialization.
        writer.WriteString(TypeDiscriminator, TokenTypeName);

        writer.WriteString("innerToken", JsonSerializer.Serialize(this.InnerToken, AgentsJsonContext.DefaultOptions.GetTypeInfo(typeof(ResponseContinuationToken))));

        if (this.InputMessages?.Any() is true)
        {
            writer.WriteString("inputMessages", JsonSerializer.Serialize(this.InputMessages, AgentsJsonContext.DefaultOptions.GetTypeInfo(typeof(IEnumerable<ChatMessage>))));
        }

        if (this.ResponseUpdates?.Count > 0)
        {
            writer.WriteString("responseUpdates", JsonSerializer.Serialize(this.ResponseUpdates, AgentsJsonContext.DefaultOptions.GetTypeInfo(typeof(IReadOnlyList<ChatResponseUpdate>))));
        }

        writer.WriteEndObject();

        writer.Flush();

        return stream.ToArray();
    }

    /// <summary>
    /// Create a new instance of <see cref="RealtimeAgentContinuationToken"/> from the provided <paramref name="token"/>.
    /// </summary>
    /// <param name="token">The token to create the <see cref="RealtimeAgentContinuationToken"/> from.</param>
    /// <returns>A <see cref="RealtimeAgentContinuationToken"/> equivalent of the provided <paramref name="token"/>.</returns>
    internal static RealtimeAgentContinuationToken FromToken(ResponseContinuationToken token)
    {
        if (token is RealtimeAgentContinuationToken chatClientContinuationToken)
        {
            return chatClientContinuationToken;
        }

        ReadOnlyMemory<byte> data = token.ToBytes();

        if (data.Length == 0)
        {
            Throw.ArgumentException(nameof(token), "Failed to create RealtimeAgentContinuationToken from provided token because it does not contain any data.");
        }

        Utf8JsonReader reader = new(data.Span);

        // Move to the start object token.
        _ = reader.Read();

        // Validate that the token is of this type.
        ValidateTokenType(reader, token);

        ResponseContinuationToken? innerToken = null;
        IEnumerable<ChatMessage>? inputMessages = null;
        IReadOnlyList<ChatResponseUpdate>? responseUpdates = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }
            switch (reader.GetString())
            {
                case "innerToken":
                    _ = reader.Read();
                    var innerTokenJson = reader.GetString() ?? throw new ArgumentException("No content for innerToken property.", nameof(token));
                    innerToken = (ResponseContinuationToken?)JsonSerializer.Deserialize(innerTokenJson, AgentsJsonContext.DefaultOptions.GetTypeInfo(typeof(ResponseContinuationToken)));
                    break;
                case "inputMessages":
                    _ = reader.Read();
                    var innerMessagesJson = reader.GetString() ?? throw new ArgumentException("No content for inputMessages property.", nameof(token));
                    inputMessages = (IEnumerable<ChatMessage>?)JsonSerializer.Deserialize(innerMessagesJson, AgentsJsonContext.DefaultOptions.GetTypeInfo(typeof(IEnumerable<ChatMessage>)));
                    break;
                case "responseUpdates":
                    _ = reader.Read();
                    var responseUpdatesJson = reader.GetString() ?? throw new ArgumentException("No content for responseUpdates property.", nameof(token));
                    responseUpdates = (IReadOnlyList<ChatResponseUpdate>?)JsonSerializer.Deserialize(responseUpdatesJson, AgentsJsonContext.DefaultOptions.GetTypeInfo(typeof(IReadOnlyList<ChatResponseUpdate>)));
                    break;
                default:
                    break;
            }
        }

        if (innerToken is null)
        {
            Throw.ArgumentException(nameof(token), "Failed to create RealtimeAgentContinuationToken from provided token because it does not contain an inner token.");
        }

        return new RealtimeAgentContinuationToken(innerToken)
        {
            InputMessages = inputMessages,
            ResponseUpdates = responseUpdates
        };
    }

    private static void ValidateTokenType(Utf8JsonReader reader, ResponseContinuationToken token)
    {
        try
        {
            // Move to the first property.
            _ = reader.Read();

            // If the first property name is not "type", or its value does not match this token type name, then we know its not this token type.
            if (reader.GetString() != TypeDiscriminator || !reader.Read() || reader.GetString() != TokenTypeName)
            {
                Throw.ArgumentException(nameof(token), "Failed to create RealtimeAgentContinuationToken from provided token because it is not of the correct type.");
            }
        }
        catch (JsonException ex)
        {
            Throw.ArgumentException(nameof(token), "Failed to create RealtimeAgentContinuationToken from provided token because it could not be parsed.", ex);
        }
    }

    /// <summary>
    /// Gets a continuation token provided by the underlying <see cref="IChatClient"/>.
    /// </summary>
    internal ResponseContinuationToken InnerToken { get; }

    /// <summary>
    /// Gets or sets the input messages used for streaming run.
    /// </summary>
    internal IEnumerable<ChatMessage>? InputMessages { get; set; }

    /// <summary>
    /// Gets or sets the response updates received so far.
    /// </summary>
    internal IReadOnlyList<ChatResponseUpdate>? ResponseUpdates { get; set; }
}
