using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;

namespace Agents.AI.ContactCenter.Media.Audio.Resilience;

/// <summary>
/// <see cref="ISpeechSynthesizer"/> decorator that wraps an ordered list of inner
/// synthesizers (typically one per Azure region) with a Polly v8 pipeline
/// (Timeout &#x2192; Retry &#x2192; Circuit Breaker) and walks the list on fallback.
/// </summary>
/// <remarks>
/// <para>
/// Because <see cref="SynthesizeAsync"/> returns an <see cref="IAsyncEnumerable{T}"/>,
/// resilience can only be safely applied to the <em>start phase</em> (acquire
/// enumerator + advance to first chunk). Once the first byte has been yielded to
/// the caller it is impossible to retry or fail over without breaking playback,
/// so subsequent errors propagate to the caller unmodified.
/// </para>
/// <para>
/// This decorator is safe for concurrent use as long as the inner synthesizers
/// are thread-safe (the Azure pool-backed implementation is).
/// </para>
/// </remarks>
public sealed class ResilientSpeechSynthesizer : ISpeechSynthesizer
{
    private readonly IReadOnlyList<EndpointEntry> _endpoints;
    private readonly SpeechResilienceOptions _options;
    private readonly ILogger<ResilientSpeechSynthesizer> _logger;

    public ResilientSpeechSynthesizer(
        IReadOnlyList<(string Name, ISpeechSynthesizer Inner)> endpoints,
        SpeechResilienceOptions options,
        ILogger<ResilientSpeechSynthesizer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);

        if (endpoints.Count == 0)
        {
            throw new ArgumentException("At least one synthesizer endpoint is required.", nameof(endpoints));
        }

        _options = options;
        _logger = logger ?? NullLogger<ResilientSpeechSynthesizer>.Instance;
        _endpoints = endpoints
            .Select(e => new EndpointEntry(
                e.Name,
                e.Inner,
                SpeechResiliencePipelineBuilder.Build(e.Name, SpeechResilienceTelemetry.OperationSynthesizeStart, options)))
            .ToArray();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        SynthesizerInputFormat inputFormat = SynthesizerInputFormat.Text,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var endpointCount = _options.EnableFallback ? _endpoints.Count : 1;
        Exception? lastException = null;
        PrimedEnumerator? primed = null;
        var endpointIndex = -1;

        try
        {
            for (var i = 0; i < endpointCount; i++)
            {
                var entry = _endpoints[i];
                cancellationToken.ThrowIfCancellationRequested();

                using var activity = SpeechResilienceTelemetry.ActivitySource.StartActivity(
                    "speech.synthesize.start",
                    ActivityKind.Client);
                activity?.SetTag(SpeechResilienceTelemetry.TagOperation, SpeechResilienceTelemetry.OperationSynthesizeStart);
                activity?.SetTag(SpeechResilienceTelemetry.TagEndpointName, entry.Name);
                activity?.SetTag(SpeechResilienceTelemetry.TagEndpointIndex, i);

                var startTimestamp = Stopwatch.GetTimestamp();
                try
                {
                    primed = await entry.Pipeline
                        .ExecuteAsync(
                            static async (state, ct) =>
                            {
                                var enumerator = state.Inner
                                    .SynthesizeAsync(state.Text, state.Format, ct)
                                    .GetAsyncEnumerator(ct);

                                try
                                {
                                    var hasFirst = await enumerator.MoveNextAsync().ConfigureAwait(false);
                                    return new PrimedEnumerator(enumerator, hasFirst ? enumerator.Current : null);
                                }
                                catch
                                {
                                    await enumerator.DisposeAsync().ConfigureAwait(false);
                                    throw;
                                }
                            },
                            (Inner: entry.Inner, Text: text, Format: inputFormat),
                            cancellationToken)
                        .ConfigureAwait(false);

                    activity?.SetTag(SpeechResilienceTelemetry.TagOutcome, SpeechResilienceTelemetry.OutcomeSuccess);
                    SpeechResilienceTelemetry.RecordAttemptDuration(
                        SpeechResilienceTelemetry.OperationSynthesizeStart,
                        entry.Name,
                        SpeechResilienceTelemetry.OutcomeSuccess,
                        GetElapsedMs(startTimestamp));

                    endpointIndex = i;
                    break;
                }
                catch (Exception ex) when (SpeechExceptionClassifier.IsCallerCancellation(ex, cancellationToken))
                {
                    activity?.SetTag(SpeechResilienceTelemetry.TagOutcome, SpeechResilienceTelemetry.OutcomeCancelled);
                    SpeechResilienceTelemetry.RecordAttemptDuration(
                        SpeechResilienceTelemetry.OperationSynthesizeStart,
                        entry.Name,
                        SpeechResilienceTelemetry.OutcomeCancelled,
                        GetElapsedMs(startTimestamp));
                    throw;
                }
                catch (Exception ex)
                {
                    var transient = SpeechExceptionClassifier.IsTransient(ex);
                    var outcome = transient
                        ? SpeechResilienceTelemetry.OutcomeTransientError
                        : SpeechResilienceTelemetry.OutcomeTerminalError;

                    activity?.SetTag(SpeechResilienceTelemetry.TagOutcome, outcome);
                    activity?.SetTag(SpeechResilienceTelemetry.TagExceptionType, ex.GetType().FullName);
                    if (ex is SpeechSdkException sdk)
                    {
                        activity?.SetTag(SpeechResilienceTelemetry.TagErrorCode, sdk.ErrorCode.ToString());
                    }

                    SpeechResilienceTelemetry.RecordAttemptDuration(
                        SpeechResilienceTelemetry.OperationSynthesizeStart,
                        entry.Name,
                        outcome,
                        GetElapsedMs(startTimestamp));

                    lastException = ex;
                    _logger.LogWarning(
                        ex,
                        "Synthesis start failed on endpoint {EndpointName} (index {Index}); {Action}",
                        entry.Name,
                        i,
                        transient && i + 1 < endpointCount ? "falling back" : "no more endpoints");

                    if (!transient)
                    {
                        throw;
                    }

                    if (i + 1 < endpointCount)
                    {
                        SpeechResilienceTelemetry.RecordFallback(
                            SpeechResilienceTelemetry.OperationSynthesizeStart,
                            entry.Name,
                            _endpoints[i + 1].Name,
                            ex);
                    }
                }
            }

            if (primed is null)
            {
                // All transient: surface the most recent failure.
                throw lastException ?? new InvalidOperationException(
                    "ResilientSpeechSynthesizer exhausted all endpoints without producing audio.");
            }

            // Drain the primed first chunk (if any) and then the rest of the
            // stream WITHOUT resilience - the caller is already consuming bytes.
            var winnerName = _endpoints[endpointIndex].Name;
            if (primed.FirstChunk is { } firstChunk)
            {
                yield return firstChunk;
            }
            else
            {
                // Empty stream; nothing more to do.
                yield break;
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool hasMore;
                try
                {
                    hasMore = await primed.Enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Synthesis failed mid-stream on endpoint {EndpointName}; cannot fail over after first byte was yielded",
                        winnerName);
                    throw;
                }

                if (!hasMore)
                {
                    yield break;
                }

                yield return primed.Enumerator.Current;
            }
        }
        finally
        {
            if (primed is not null)
            {
                await primed.Enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static double GetElapsedMs(long startTimestamp) =>
        Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

    private sealed record EndpointEntry(string Name, ISpeechSynthesizer Inner, ResiliencePipeline Pipeline);

    private sealed class PrimedEnumerator
    {
        public PrimedEnumerator(IAsyncEnumerator<ReadOnlyMemory<byte>> enumerator, ReadOnlyMemory<byte>? firstChunk)
        {
            Enumerator = enumerator;
            FirstChunk = firstChunk;
        }

        public IAsyncEnumerator<ReadOnlyMemory<byte>> Enumerator { get; }
        public ReadOnlyMemory<byte>? FirstChunk { get; }
    }
}
