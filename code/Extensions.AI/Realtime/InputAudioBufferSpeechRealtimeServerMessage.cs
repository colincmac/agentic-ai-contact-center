// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Microsoft.Extensions.AI;

namespace Extensions.AI.Realtime;

/// <summary>
/// Represents a real-time server message emitted by server-side voice activity detection (VAD)
/// when the user starts or stops speaking into the input audio buffer.
/// </summary>
/// <remarks>
/// Microsoft.Extensions.AI does not yet define a built-in <see cref="RealtimeServerMessage"/>
/// type for these events; this type fills that gap for the Azure Voice Live and OpenAI
/// realtime providers. The specific event is indicated by <see cref="RealtimeServerMessage.Type"/>,
/// which will be either <see cref="InputAudioBufferSpeechStarted"/> or
/// <see cref="InputAudioBufferSpeechStopped"/>.
/// </remarks>
#pragma warning disable MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates.
public class InputAudioBufferSpeechRealtimeServerMessage : RealtimeServerMessage
{
    /// <summary>The message type used for server-side VAD "speech started" events.</summary>
    public static readonly RealtimeServerMessageType InputAudioBufferSpeechStarted = new("InputAudioBufferSpeechStarted");

    /// <summary>The message type used for server-side VAD "speech stopped" events.</summary>
    public static readonly RealtimeServerMessageType InputAudioBufferSpeechStopped = new("InputAudioBufferSpeechStopped");

    /// <summary>
    /// Initializes a new instance of the <see cref="InputAudioBufferSpeechRealtimeServerMessage"/> class.
    /// </summary>
    /// <param name="type">
    /// Either <see cref="InputAudioBufferSpeechStarted"/> or <see cref="InputAudioBufferSpeechStopped"/>.
    /// </param>
    public InputAudioBufferSpeechRealtimeServerMessage(RealtimeServerMessageType type)
    {
        Type = type;
    }

    /// <summary>
    /// Gets or sets the ID of the conversation item being created by the user audio that triggered VAD.
    /// </summary>
    public string? ItemId { get; set; }

    /// <summary>
    /// Gets or sets the timestamp within the input audio buffer at which speech was detected
    /// (populated for <see cref="InputAudioBufferSpeechStarted"/>).
    /// </summary>
    public TimeSpan? AudioStart { get; set; }

    /// <summary>
    /// Gets or sets the timestamp within the input audio buffer at which speech stopped
    /// (populated for <see cref="InputAudioBufferSpeechStopped"/>).
    /// </summary>
    public TimeSpan? AudioEnd { get; set; }
}
#pragma warning restore MEAI001
