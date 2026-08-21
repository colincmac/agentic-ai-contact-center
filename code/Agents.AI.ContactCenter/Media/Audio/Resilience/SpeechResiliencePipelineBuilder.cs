using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Agents.AI.ContactCenter.Media.Audio.Resilience;

/// <summary>
/// Builds the per-<c>(endpoint, operation)</c> Polly v8 pipeline used by the
/// resilient recognizer/synthesizer decorators. The pipeline composition is:
/// <c>Timeout (per-attempt) -&gt; Retry (exp backoff + jitter, transient-only) -&gt;
/// CircuitBreaker (per endpoint + operation)</c>.
/// </summary>
/// <remarks>
/// Fallback across endpoints is handled by the decorator itself (as an explicit
/// loop) rather than a Polly fallback strategy, which keeps the per-endpoint
/// telemetry tags clean and makes the recognizer's "before-first-final" boundary
/// trivial to enforce.
/// </remarks>
internal static class SpeechResiliencePipelineBuilder
{
    public static ResiliencePipeline Build(
        string endpointName,
        string operation,
        SpeechResilienceOptions options,
        bool includeAttemptTimeout = true)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = new ResiliencePipelineBuilder
        {
            Name = $"speech:{operation}:{endpointName}",
        };

        // Innermost: enforce a per-attempt timeout so a single hung call cannot
        // block the entire fallback chain. Skipped for the recognizer where the
        // unit of work is an open-ended streaming session.
        if (includeAttemptTimeout)
        {
            builder.AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = options.AttemptTimeout,
                Name = "speech-timeout",
            });
        }

        // Retry against the same endpoint for transient faults only. Skipped when
        // MaxRetryAttempts is 0 (Polly requires >= 1) so callers can opt out.
        if (options.MaxRetryAttempts > 0)
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                Name = "speech-retry",
                MaxRetryAttempts = options.MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = options.BaseRetryDelay,
                MaxDelay = options.MaxRetryDelay,
                ShouldHandle = static args => ValueTask.FromResult(
                    args.Outcome.Exception is { } ex
                    && !SpeechExceptionClassifier.IsCallerCancellation(ex, args.Context.CancellationToken)
                    && SpeechExceptionClassifier.IsTransient(ex)),
                OnRetry = args =>
                {
                    var ex = args.Outcome.Exception!;
                    SpeechResilienceTelemetry.RecordRetry(operation, endpointName, args.AttemptNumber + 1, ex);
                    return default;
                },
            });
        }

        // Outermost: per-endpoint circuit breaker so a sick region stops swallowing
        // traffic and the decorator can short-circuit to fallback immediately.
        builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            Name = "speech-circuit-breaker",
            FailureRatio = options.BreakerFailureRatio,
            SamplingDuration = options.BreakerSamplingDuration,
            MinimumThroughput = options.BreakerMinimumThroughput,
            BreakDuration = options.BreakerDuration,
            ShouldHandle = static args => ValueTask.FromResult(
                args.Outcome.Exception is { } ex
                && !SpeechExceptionClassifier.IsCallerCancellation(ex, args.Context.CancellationToken)
                && SpeechExceptionClassifier.IsTransient(ex)),
            OnOpened = args =>
            {
                SpeechResilienceTelemetry.RecordCircuitTransition(operation, endpointName, "opened");
                return default;
            },
            OnClosed = args =>
            {
                SpeechResilienceTelemetry.RecordCircuitTransition(operation, endpointName, "closed");
                return default;
            },
            OnHalfOpened = args =>
            {
                SpeechResilienceTelemetry.RecordCircuitTransition(operation, endpointName, "half_opened");
                return default;
            },
        });

        return builder.Build();
    }
}
