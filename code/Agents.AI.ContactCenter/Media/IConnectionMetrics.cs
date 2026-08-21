namespace Agents.AI.ContactCenter.Media;

/// <summary>
/// Exposes real-time memory and throughput metrics for a single call connection,
/// enabling operators to estimate per-instance capacity and make scaling decisions.
/// </summary>
/// <remarks>
/// Implementations should be lock-free or use <see cref="Interlocked"/>
/// counters on the hot path to avoid adding latency to audio routing.
/// </remarks>
public interface IConnectionMetrics
{
    /// <summary>Bytes currently buffered for outbound audio delivery across all subscriptions.</summary>
    long AudioBufferBytes { get; }

    /// <summary>Bytes currently buffered for outbound message delivery.</summary>
    long MessageBufferBytes { get; }

    /// <summary>Total bytes buffered across all media types.</summary>
    long TotalBufferedBytes { get; }

    /// <summary>Number of active media stream subscriptions (audio pumps, transcript taps, etc.).</summary>
    int ActiveSubscriptions { get; }

    /// <summary>Cumulative bytes written to audio buffers since the connection started.</summary>
    long TotalAudioBytesWritten { get; }

    /// <summary>Cumulative bytes distributed to consumers since the connection started.</summary>
    long TotalAudioBytesDistributed { get; }

    /// <summary>Timestamp when the connection was established.</summary>
    DateTimeOffset ConnectedAt { get; }
}
