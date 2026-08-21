using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Agents.AI.ContactCenter.Agents.IntentAgent;
using Microsoft.Extensions.AI;

namespace Agents.AI.ContactCenter.Tests;

/// <summary>
/// Coverage for the chat-client-based classification path inside <see cref="IvrIntentAgent"/>.
/// Mirrors the JSON parsing, candidate validation, and prompt-construction behavior the
/// agent inherited from the now-removed standalone classifier.
/// </summary>
public class IvrIntentAgentClassifierTests
{
    private static readonly string[] candidateIntents = ["check_balance", "pay_bill", "speak_to_agent"];

    [Fact]
    public async Task ClassifyAsync_returns_none_for_empty_utterance()
    {
        var chat = new FakeChatClient(_ => throw new InvalidOperationException("Chat client should not be invoked"));
        var agent = new IvrIntentAgent(chat);

        var result = await agent.ClassifyAsync("   ", candidateIntents);

        Assert.True(result.IsNone);
    }

    [Fact]
    public async Task ClassifyAsync_returns_none_when_candidates_empty()
    {
        var chat = new FakeChatClient(_ => throw new InvalidOperationException("Chat client should not be invoked"));
        var agent = new IvrIntentAgent(chat);

        var result = await agent.ClassifyAsync("what's my balance", []);

        Assert.True(result.IsNone);
    }

    [Fact]
    public async Task ClassifyAsync_parses_well_formed_json_response()
    {
        var chat = new FakeChatClient(_ =>
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"intent":"check_balance","confidence":0.92,"entities":{"account":"primary"}}""")));
        var agent = new IvrIntentAgent(chat);

        var result = await agent.ClassifyAsync("what is my checking balance", candidateIntents);

        Assert.False(result.IsNone);
        Assert.Equal("check_balance", result.IntentName);
        Assert.Equal(0.92, result.Confidence, precision: 2);
        Assert.NotNull(result.Entities);
        Assert.Equal("primary", result.Entities!["account"]);
    }

    [Fact]
    public async Task ClassifyAsync_tolerates_markdown_fenced_json()
    {
        var chat = new FakeChatClient(_ =>
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """
                Sure, here is the classification:
                ```json
                {"intent": "pay_bill", "confidence": 0.81}
                ```
                """)));
        var agent = new IvrIntentAgent(chat);

        var result = await agent.ClassifyAsync("I want to pay my bill", candidateIntents);

        Assert.Equal("pay_bill", result.IntentName);
        Assert.Equal(0.81, result.Confidence, precision: 2);
        Assert.Null(result.Entities);
    }

    [Fact]
    public async Task ClassifyAsync_coerces_unknown_intent_to_none()
    {
        var chat = new FakeChatClient(_ =>
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"intent":"order_pizza","confidence":0.99}""")));
        var agent = new IvrIntentAgent(chat);

        var result = await agent.ClassifyAsync("I'd like a pepperoni pie", candidateIntents);

        Assert.True(result.IsNone);
    }

    [Fact]
    public async Task ClassifyAsync_treats_explicit_none_as_no_match()
    {
        var chat = new FakeChatClient(_ =>
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"intent":"none","confidence":0.10}""")));
        var agent = new IvrIntentAgent(chat);

        var result = await agent.ClassifyAsync("hello there", candidateIntents);

        Assert.True(result.IsNone);
    }

    [Fact]
    public async Task ClassifyAsync_returns_none_when_confidence_below_threshold()
    {
        var chat = new FakeChatClient(_ =>
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"intent":"check_balance","confidence":0.4}""")));
        var agent = new IvrIntentAgent(chat, options: new IvrIntentAgentOptions { MinimumConfidence = 0.6 });

        var result = await agent.ClassifyAsync("balance?", candidateIntents);

        Assert.True(result.IsNone);
    }

    [Fact]
    public async Task ClassifyAsync_uses_canonical_intent_spelling_from_candidates()
    {
        var chat = new FakeChatClient(_ =>
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"intent":"CHECK_BALANCE","confidence":0.9}""")));
        var agent = new IvrIntentAgent(chat);

        var result = await agent.ClassifyAsync("balance please", candidateIntents);

        Assert.Equal("check_balance", result.IntentName); // matches the candidate casing
    }

    [Fact]
    public async Task ClassifyAsync_defaults_to_full_confidence_when_field_omitted()
    {
        var chat = new FakeChatClient(_ =>
            new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"intent":"pay_bill"}""")));
        var agent = new IvrIntentAgent(chat);

        var result = await agent.ClassifyAsync("pay my bill", candidateIntents);

        Assert.Equal("pay_bill", result.IntentName);
        Assert.Equal(1.0, result.Confidence);
    }

    [Fact]
    public async Task ClassifyAsync_returns_none_on_unparseable_response()
    {
        var chat = new FakeChatClient(_ =>
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "I think you want to check your balance.")));
        var agent = new IvrIntentAgent(chat);

        var result = await agent.ClassifyAsync("how much do I have", candidateIntents);

        Assert.True(result.IsNone);
    }

    [Fact]
    public async Task ClassifyAsync_returns_none_on_chat_client_failure()
    {
        var chat = new FakeChatClient(_ => throw new InvalidOperationException("backend down"));
        var agent = new IvrIntentAgent(chat);

        var result = await agent.ClassifyAsync("balance?", candidateIntents);

        Assert.True(result.IsNone);
    }

    [Fact]
    public async Task ClassifyAsync_propagates_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var chat = new FakeChatClient(_ => throw new OperationCanceledException());
        var agent = new IvrIntentAgent(chat);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await agent.ClassifyAsync("hello", candidateIntents, cts.Token));
    }

    [Fact]
    public async Task ClassifyAsync_sends_candidate_intents_and_examples_to_chat_client()
    {
        IList<ChatMessage>? captured = null;
        var chat = new FakeChatClient(messages =>
        {
            captured = messages.ToList();
            return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"intent":"check_balance","confidence":0.9}"""));
        });

        var options = new IvrIntentAgentOptions
        {
            IntentExamples =
            {
                ["check_balance"] = ["what's my balance", "tell me my balance"],
                ["pay_bill"] = ["pay this", "settle my balance"],
            },
        };
        var agent = new IvrIntentAgent(chat, options: options);

        _ = await agent.ClassifyAsync("balance please", candidateIntents);

        Assert.NotNull(captured);
        Assert.Equal(2, captured!.Count);
        var userText = captured[1].Text;
        Assert.NotNull(userText);
        foreach (var intent in candidateIntents)
        {
            Assert.Contains(intent, userText!, StringComparison.Ordinal);
        }
        Assert.Contains("what's my balance", userText, StringComparison.Ordinal);
        Assert.Contains("settle my balance", userText, StringComparison.Ordinal);
        Assert.Contains("Utterance:", userText, StringComparison.Ordinal);
        Assert.Contains("balance please", userText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClassifyAsync_requests_json_response_format_by_default()
    {
        ChatOptions? capturedOptions = null;
        var chat = new FakeChatClient((messages, options) =>
        {
            capturedOptions = options;
            return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"intent":"check_balance","confidence":0.9}"""));
        });
        var agent = new IvrIntentAgent(chat);

        _ = await agent.ClassifyAsync("balance", candidateIntents);

        Assert.NotNull(capturedOptions);
        Assert.Equal(ChatResponseFormat.Json, capturedOptions!.ResponseFormat);
        Assert.Equal(0f, capturedOptions.Temperature);
        Assert.Equal(128, capturedOptions.MaxOutputTokens);
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly Func<IEnumerable<ChatMessage>, ChatOptions?, ChatResponse> _respond;

        public FakeChatClient(Func<IEnumerable<ChatMessage>, ChatResponse> respond)
            : this((messages, _) => respond(messages))
        {
        }

        public FakeChatClient(Func<IEnumerable<ChatMessage>, ChatOptions?, ChatResponse> respond)
        {
            _respond = respond;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_respond(messages, options));
        }

#pragma warning disable CS1998 // Async method lacks 'await' operators
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = _respond(messages, options);
            foreach (var message in response.Messages)
            {
                yield return new ChatResponseUpdate(message.Role, message.Contents);
            }
        }
#pragma warning restore CS1998

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
