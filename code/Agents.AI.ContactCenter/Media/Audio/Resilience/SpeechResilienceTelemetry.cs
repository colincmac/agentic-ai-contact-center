using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Agents.AI.ContactCenter.Media.Audio.Resilience;

/// <summary>
/// Shared <see cref="ActivitySource"/> and <see cref="Meter"/> for the
/// resilient speech pipeline. Both are static so all decorator instances feed
/// into the same OpenTelemetry stream.
/// </summary>
internal static class SpeechResilienceTelemetry
{
    public const string SourceName = "Agents.AI.ContactCenter.Speech.Resilience";
    public const string MeterName = "Agents.AI.ContactCenter.Speech";

    public static readonly ActivitySource ActivitySource = new(SourceName);
    public static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> RetriesTotal = Meter.CreateCounter<long>(
        "speech.resilience.retries_total",
        unit: "{retry}",
        description: "Number of retry attempts performed against a speech endpoint.");

    public static readonly Counter<long> FallbacksTotal = Meter.CreateCounter<long>(
        "speech.resilience.fallbacks_total",
        unit: "{fallback}",
        description: "Number of endpoint-to-endpoint fallbacks performed by the resilient pipeline.");

    public static readonly Counter<long> CircuitBreakerTransitionsTotal = Meter.CreateCounter<long>(
        "speech.resilience.circuit_breaker_transitions_total",
        unit: "{transition}",
        description: "Number of circuit breaker state transitions (opened, half_opened, closed).");

    public static readonly Counter<long> AudioFramesDroppedTotal = Meter.CreateCounter<long>(
        "speech.resilience.audio_frames_dropped_total",
        unit: "{frame}",
        description: "Audio frames dropped because the recognizer restarted between fault detection and the new session being ready (no PCM replay).");

    public static readonly Histogram<double> AttemptDuration = Meter.CreateHistogram<double>(
        "speech.resilience.attempt.duration",
        unit: "ms",
        description: "Duration of a single resilient speech attempt against one endpoint.");

    public const string TagOperation = "speech.operation";
    public const string TagEndpointName = "speech.endpoint.name";
    public const string TagEndpointIndex = "speech.endpoint.index";
    public const string TagFromEndpoint = "speech.endpoint.from";
    public const string TagToEndpoint = "speech.endpoint.to";
    public const string TagAttempt = "speech.attempt";
    public const string TagOutcome = "outcome";
    public const string TagExceptionType = "exception.type";
    public const string TagErrorCode = "error.code";
    public const string TagState = "state";

    public const string OperationRecognizeStart = "recognize.start";
    public const string OperationSynthesizeStart = "synthesize.start";

    public const string OutcomeSuccess = "success";
    public const string OutcomeTransientError = "transient_error";
    public const string OutcomeTerminalError = "terminal_error";
    public const string OutcomeCancelled = "cancelled";

    public static void RecordRetry(string operation, string endpointName, int attempt, Exception exception)
    {
        RetriesTotal.Add(1,
            new KeyValuePair<string, object?>(TagOperation, operation),
            new KeyValuePair<string, object?>(TagEndpointName, endpointName),
            new KeyValuePair<string, object?>(TagAttempt, attempt),
            new KeyValuePair<string, object?>(TagExceptionType, exception.GetType().FullName));
    }

    public static void RecordFallback(string operation, string fromEndpoint, string toEndpoint, Exception? lastException)
    {
        FallbacksTotal.Add(1,
            new KeyValuePair<string, object?>(TagOperation, operation),
            new KeyValuePair<string, object?>(TagFromEndpoint, fromEndpoint),
            new KeyValuePair<string, object?>(TagToEndpoint, toEndpoint),
            new KeyValuePair<string, object?>(TagExceptionType, lastException?.GetType().FullName));
    }

    public static void RecordCircuitTransition(string operation, string endpointName, string state)
    {
        CircuitBreakerTransitionsTotal.Add(1,
            new KeyValuePair<string, object?>(TagOperation, operation),
            new KeyValuePair<string, object?>(TagEndpointName, endpointName),
            new KeyValuePair<string, object?>(TagState, state));
    }

    public static void RecordDroppedFrames(string endpointName, long frames)
    {
        if (frames <= 0)
        {
            return;
        }

        AudioFramesDroppedTotal.Add(frames,
            new KeyValuePair<string, object?>(TagEndpointName, endpointName));
    }

    public static void RecordAttemptDuration(string operation, string endpointName, string outcome, double elapsedMs)
    {
        AttemptDuration.Record(elapsedMs,
            new KeyValuePair<string, object?>(TagOperation, operation),
            new KeyValuePair<string, object?>(TagEndpointName, endpointName),
            new KeyValuePair<string, object?>(TagOutcome, outcome));
    }
}
