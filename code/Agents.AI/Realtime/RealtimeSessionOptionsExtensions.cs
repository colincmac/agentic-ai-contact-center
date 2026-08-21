using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.AI;

namespace Agents.AI.Realtime;

public static class RealtimeSessionOptionsExtensions
{
    /// <summary>
    /// Creates a new <see cref="RealtimeSessionOptions"/> instance by cloning the current options
    /// and optionally overwriting individual properties.
    /// </summary>
    /// <remarks>
    /// Session options are immutable, so this method allows you to create a modified copy of the options without changing the original instance.
    /// </remarks>
    public static RealtimeSessionOptions With(
        this RealtimeSessionOptions? options,
        RealtimeSessionKind? sessionKind = null,
        string? model = null,
        RealtimeAudioFormat? inputAudioFormat = null,
        TranscriptionOptions? transcriptionOptions = null,
        RealtimeAudioFormat? outputAudioFormat = null,
        string? voice = null,
        string? instructions = null,
        int? maxOutputTokens = null,
        IReadOnlyList<string>? outputModalities = null,
        ChatToolMode? toolMode = null,
        IReadOnlyList<AITool>? tools = null,
        VoiceActivityDetectionOptions? voiceActivityDetection = null,
        Func<object?>? rawRepresentationFactory = null)
    {
        options ??= new RealtimeSessionOptions();
        return new()
        {
            SessionKind = sessionKind ?? options.SessionKind,
            Model = model ?? options.Model,
            InputAudioFormat = inputAudioFormat ?? options.InputAudioFormat,
            TranscriptionOptions = transcriptionOptions ?? options.TranscriptionOptions,
            OutputAudioFormat = outputAudioFormat ?? options.OutputAudioFormat,
            Voice = voice ?? options.Voice,
            Instructions = instructions ?? options.Instructions,
            MaxOutputTokens = maxOutputTokens ?? options.MaxOutputTokens,
            OutputModalities = outputModalities ?? options.OutputModalities,
            ToolMode = toolMode ?? options.ToolMode,
            Tools = tools ?? options.Tools,
            VoiceActivityDetection = voiceActivityDetection ?? options.VoiceActivityDetection,
            RawRepresentationFactory = rawRepresentationFactory ?? options.RawRepresentationFactory,
        };
    }
}
