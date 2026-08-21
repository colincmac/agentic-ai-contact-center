using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.AI;

namespace Extensions.AI.Realtime;

public static class RealtimeSessionOptionsExtensions
{
    public static RealtimeSessionOptions Clone(this RealtimeSessionOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }
        return new RealtimeSessionOptions
        {
            InputAudioFormat = options.InputAudioFormat,
            Instructions = options.Instructions,
            MaxOutputTokens = options.MaxOutputTokens,
            Model = options.Model,
            OutputAudioFormat = options.OutputAudioFormat,
            OutputModalities = options.OutputModalities,
            RawRepresentationFactory = options.RawRepresentationFactory,
            SessionKind = options.SessionKind,
            ToolMode = options.ToolMode,
            Tools = options.Tools,
            TranscriptionOptions = options.TranscriptionOptions,
            Voice = options.Voice,
            VoiceActivityDetection = options.VoiceActivityDetection
        };
    }
}
