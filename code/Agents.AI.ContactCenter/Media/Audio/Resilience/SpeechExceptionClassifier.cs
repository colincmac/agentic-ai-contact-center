using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.CognitiveServices.Speech;

namespace Agents.AI.ContactCenter.Media.Audio.Resilience;

/// <summary>
/// Classifies exceptions thrown by the Azure Speech SDK and other transport-level
/// faults into transient (worth retrying / failing over) versus terminal categories.
/// </summary>
internal static class SpeechExceptionClassifier
{
    /// <summary>
    /// Returns <c>true</c> when the exception represents a transient transport or
    /// service condition that warrants a retry against the same endpoint or a
    /// fallback to the next endpoint.
    /// </summary>
    public static bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            SpeechSdkException sdk => IsTransientSdkError(sdk.ErrorCode),
            TimeoutException => true,
            WebSocketException => true,
            SocketException => true,
            HttpRequestException => true,
            IOException => true,
            // Unwrap aggregated faults from Task continuations.
            AggregateException agg when agg.InnerException is not null => IsTransient(agg.InnerException),
            _ => false,
        };
    }

    /// <summary>
    /// Returns <c>true</c> when the exception is caused by the caller's
    /// <see cref="CancellationToken"/> being signalled (not a service-side fault).
    /// Callers should never retry in this case.
    /// </summary>
    public static bool IsCallerCancellation(Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is OperationCanceledException oce)
        {
            return cancellationToken.IsCancellationRequested
                && (oce.CancellationToken == cancellationToken || oce.CancellationToken == CancellationToken.None);
        }

        return false;
    }

    private static bool IsTransientSdkError(CancellationErrorCode code) => code switch
    {
        CancellationErrorCode.ConnectionFailure => true,
        CancellationErrorCode.ServiceTimeout => true,
        CancellationErrorCode.ServiceError => true,
        CancellationErrorCode.ServiceUnavailable => true,
        CancellationErrorCode.RuntimeError => true,
        CancellationErrorCode.TooManyRequests => true,
        _ => false,
    };
}
