//using System;
//using System.Collections.Generic;
//using System.Runtime.CompilerServices;
//using System.Text;
//using System.Text.Json;
//using Azure.AI.VoiceLive;
//using Extensions.AI.Contents;
//using Microsoft.Extensions.AI;

//namespace Extensions.AI.Realtime.AzureVoiceLive;

//public static class AzureVoiceLiveServerEventHandler
//{
//    public static async IAsyncEnumerable<RealtimeServerMessage> FromVoiceLiveSessionUpdatesAsync(
//         IAsyncEnumerable<SessionUpdate> streamingRealtimeUpdates,
//         [EnumeratorCancellation] CancellationToken cancellationToken = default)
//    {
//        DateTimeOffset? createdAt = null;
//        string? responseId = null;
//        string? conversationId = null;
//        string? sessionId = null;
//        string? modelId = null;
//        string? eventId = null;
//        ChatRole? lastRole = null;

//        // Local helper to construct updates with current accumulated state.
//        RealtimeServerMessage CreateUpdate(List<AIContent>? contents = null)
//        {
//            var update = new RealtimeServerMessage(lastRole, contents)
//            {
//                MessageId = eventId,
//                RawRepresentation = null, // set below after switch when we have streamingUpdate
//            };

//            return update;
//        }

//        await foreach (var streamingUpdate in streamingRealtimeUpdates.WithCancellation(cancellationToken).ConfigureAwait(false))
//        {
//            switch (streamingUpdate)
//            {
//                case SessionUpdateSessionCreated sessionStarted:
//                    {
//                        lastRole = ChatRole.Assistant;
//                        modelId = sessionStarted.Session.Model;
//                        conversationId ??= sessionStarted.Session.Id;
//                        sessionId ??= sessionStarted.Session.Id;
//                        createdAt ??= DateTimeOffset.UtcNow;

//                        var update = CreateUpdate();
//                        update.RawRepresentation = sessionStarted;
//                        yield return update;
//                        break;
//                    }

//                case SessionUpdateSessionUpdated sessionConfigured:
//                    {
//                        modelId = sessionConfigured.Session.Model;
//                        lastRole = ChatRole.Assistant;

//                        var update = CreateUpdate();
//                        update.RawRepresentation = sessionConfigured;
//                        yield return update;
//                        break;
//                    }

//                case SessionUpdateResponseCreated responseStarted:
//                    {
//                        responseId = responseStarted.Response.Id;
//                        conversationId = responseStarted.Response.ConversationId;
//                        lastRole = ChatRole.Assistant;
//                        var update = CreateUpdate();
//                        update.Contents.Add(new RealtimeResponseStartContent(responseStarted.EventId));

//                        update.RawRepresentation = responseStarted;

//                        yield return update;
//                        break;
//                    }

//                case SessionUpdateResponseAudioDelta audioDelta when !audioDelta.Delta.IsEmpty:
//                    {
//                        eventId = audioDelta.ItemId;
//                        responseId = audioDelta.ResponseId;
//                        lastRole = ChatRole.Assistant;
//                        var update = CreateUpdate([new DataContent(audioDelta.Delta.ToArray(), "audio/pcm")]);

//                        update.RawRepresentation = audioDelta;
//                        yield return update;

//                        break;
//                    }

//                case SessionUpdateResponseAudioTranscriptDelta transcriptDelta:
//                    {
//                        eventId = transcriptDelta.ItemId;
//                        responseId = transcriptDelta.ResponseId;
//                        lastRole = ChatRole.Assistant;
//                        var update = CreateUpdate([new AudioTranscriptionContent(transcriptDelta.Delta)]);

//                        update.RawRepresentation = transcriptDelta;
//                        yield return update;

//                        break;
//                    }

//                case SessionUpdateResponseFunctionCallArgumentsDone functionCall:
//                    {
//                        eventId = functionCall.ItemId;
//                        responseId = functionCall.ResponseId;
//                        lastRole = ChatRole.Assistant;
//                        var parameters = JsonSerializer.Deserialize<Dictionary<string, object?>>(functionCall.Arguments);

//                        var update = CreateUpdate([new FunctionCallContent(functionCall.CallId, functionCall.Name, parameters)]);
//                        update.RawRepresentation = functionCall;
//                        yield return update;
//                        break;
//                    }

//                case SessionUpdateResponseAudioTranscriptDone textFinished:
//                    {
//                        eventId = textFinished.ItemId;
//                        responseId = textFinished.ResponseId;
//                        lastRole = ChatRole.Assistant;

//                        var update = CreateUpdate([new TextContent(textFinished.Transcript)]);
//                        update.RawRepresentation = textFinished;
//                        yield return update;
//                        break;
//                    }


//                case SessionUpdateResponseDone responseFinished:
//                    {
//                        responseId = responseFinished.Response.Id;
//                        conversationId = responseFinished.Response.ConversationId;
//                        lastRole = ChatRole.Assistant;
//                        var update = CreateUpdate();

//                        update.FinishReason = ChatFinishReason.Stop; //ToFinishReason(responseFinished.Response.Status?.IncompleteReason) ?? (unHandledFunctionCalls.Count != 0 ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop);
//                        update.Contents.Add(new UsageContent(ToUsageDetails(responseFinished.Response.Usage)));
//                        update.Contents.Add(new RealtimeResponseFinishedContent(responseFinished.Response.Id));
//                        update.RawRepresentation = responseFinished;

//                        yield return update;
//                        break;
//                    }


//                case SessionUpdateInputAudioBufferSpeechStarted speechStarted:
//                    {
//                        eventId = speechStarted.ItemId;
//                        lastRole = ChatRole.User;
//                        var update = CreateUpdate([
//                            new AudioTranscriptionContent(referenceItemId: speechStarted.ItemId) { StartTime = speechStarted.AudioStart },
//                            new RealtimeVadContent(VadEventType.InputSpeechStarted) { StartTime = speechStarted.AudioStart }
//                            ]);
//                        update.RawRepresentation = speechStarted;

//                        yield return update;
//                        break;
//                    }

//                case SessionUpdateConversationItemInputAudioTranscriptionDelta inputAudioTxDelta:
//                    {
//                        if (!string.IsNullOrEmpty(inputAudioTxDelta.Delta))
//                        {
//                            eventId = inputAudioTxDelta.ItemId;
//                            lastRole = ChatRole.User;

//                            var update = CreateUpdate([new AudioTranscriptionContent(text: inputAudioTxDelta.Delta, referenceItemId: inputAudioTxDelta.ItemId, referenceContentIndex: inputAudioTxDelta.ContentIndex)]);
//                            update.RawRepresentation = inputAudioTxDelta;
//                            yield return update;
//                        }
//                        break;
//                    }

//                case SessionUpdateConversationItemInputAudioTranscriptionCompleted inputAudioTxFinished:
//                    {
//                        if (!string.IsNullOrEmpty(inputAudioTxFinished.Transcript))
//                        {
//                            eventId = inputAudioTxFinished.ItemId;
//                            lastRole = ChatRole.User;

//                            var update = CreateUpdate([new TextContent(inputAudioTxFinished.Transcript)]);
//                            update.RawRepresentation = inputAudioTxFinished;
//                            yield return update;
//                        }
//                        break;
//                    }



//                case SessionUpdateInputAudioBufferSpeechStopped speechFinished:
//                    {
//                        eventId = speechFinished.ItemId;
//                        lastRole = ChatRole.User;
//                        var update = CreateUpdate([
//                            new RealtimeVadContent(VadEventType.InputSpeechEnded)
//                            {
//                                EndTime = speechFinished.AudioEnd
//                            }]);
//                        update.RawRepresentation = speechFinished;
//                        yield return update;
//                        break;
//                    }

//                default:
//                    {
//                        // Future-proof: unknown subclass we didn’t explicitly handle.
//                        var update = CreateUpdate();
//                        update.RawRepresentation = streamingUpdate;
//                        yield return update;
//                        break;
//                    }
//            }
//        }
//    }

//    private static UsageDetails ToUsageDetails(ResponseTokenStatistics usage) =>
//        new()
//        {
//            InputTokenCount = usage.InputTokens,
//            OutputTokenCount = usage.OutputTokens,
//            TotalTokenCount = usage.TotalTokens,
//        };
//}
