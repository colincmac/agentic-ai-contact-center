using Microsoft.CognitiveServices.Speech;

namespace Agents.AI.ContactCenter.Media.Audio.Resilience;

/// <summary>
/// Strongly typed exception surfaced when the Azure Speech SDK reports a
/// cancellation with <see cref="CancellationReason.Error"/>. Carries the
/// underlying <see cref="CancellationErrorCode"/> and SDK details so the
/// resilient pipeline can classify the failure as transient or terminal
/// without parsing log strings.
/// </summary>
public sealed class SpeechSdkException : Exception
{
    public SpeechSdkException(
        CancellationErrorCode errorCode,
        string? errorDetails,
        string? message = null,
        Exception? innerException = null)
        : base(message ?? $"Azure Speech SDK error: {errorCode} - {errorDetails}", innerException)
    {
        ErrorCode = errorCode;
        ErrorDetails = errorDetails ?? string.Empty;
    }

    /// <summary>The Azure Speech SDK error code that produced this cancellation.</summary>
    public CancellationErrorCode ErrorCode { get; }

    /// <summary>Additional details supplied by the SDK alongside <see cref="ErrorCode"/>.</summary>
    public string ErrorDetails { get; }
}
