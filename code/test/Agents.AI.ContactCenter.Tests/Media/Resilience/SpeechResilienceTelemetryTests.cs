using System.Diagnostics.Metrics;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.Media.Audio.Resilience;
using Microsoft.CognitiveServices.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Tests.Media.Resilience;

public class SpeechResilienceTelemetryTests
{
    [Fact]
    public async Task FallbackBetweenEndpoints_EmitsFallbackAndRetryMetrics()
    {
        using var listener = new MetricCapture();
        listener.Subscribe(SpeechResilienceTelemetry.MeterName);

        var primary = new FakeSpeechSynthesizer(new[]
        {
            SynthesizerScript.FailOnStart(new SpeechSdkException(CancellationErrorCode.ServiceTimeout, "boom1")),
            SynthesizerScript.FailOnStart(new SpeechSdkException(CancellationErrorCode.ServiceTimeout, "boom2")),
        });
        var secondary = new FakeSpeechSynthesizer(new[] { SynthesizerScript.Succeed([1]) });

        var sut = new ResilientSpeechSynthesizer(
            [("primary", primary), ("secondary", secondary)],
            new SpeechResilienceOptions
            {
                AttemptTimeout = TimeSpan.FromSeconds(2),
                MaxRetryAttempts = 1,
                BaseRetryDelay = TimeSpan.FromMilliseconds(1),
                MaxRetryDelay = TimeSpan.FromMilliseconds(5),
                BreakerFailureRatio = 0.99,
                BreakerMinimumThroughput = 1000,
                BreakerSamplingDuration = TimeSpan.FromSeconds(30),
                BreakerDuration = TimeSpan.FromSeconds(30),
                EnableFallback = true,
            },
            NullLogger<ResilientSpeechSynthesizer>.Instance);

        await foreach (var _ in sut.SynthesizeAsync("hi"))
        {
        }

        listener.Flush();

        Assert.True(
            listener.Sum("speech.resilience.retries_total") >= 1,
            "Expected at least one retry metric");
        Assert.True(
            listener.Sum("speech.resilience.fallbacks_total") >= 1,
            "Expected at least one fallback metric");

        var fallbackTags = listener.Tags("speech.resilience.fallbacks_total").FirstOrDefault();
        Assert.NotNull(fallbackTags);
        Assert.Equal("primary", fallbackTags!["speech.endpoint.from"]);
        Assert.Equal("secondary", fallbackTags["speech.endpoint.to"]);
    }

    [Fact]
    public async Task SuccessfulCall_EmitsAttemptDurationOutcomeSuccess()
    {
        using var listener = new MetricCapture();
        listener.Subscribe(SpeechResilienceTelemetry.MeterName);

        var primary = new FakeSpeechSynthesizer(new[] { SynthesizerScript.Succeed([1]) });

        var sut = new ResilientSpeechSynthesizer(
            [("primary", primary)],
            new SpeechResilienceOptions
            {
                AttemptTimeout = TimeSpan.FromSeconds(2),
                MaxRetryAttempts = 0,
                BreakerMinimumThroughput = 1000,
                BreakerFailureRatio = 0.99,
                EnableFallback = false,
            },
            NullLogger<ResilientSpeechSynthesizer>.Instance);

        await foreach (var _ in sut.SynthesizeAsync("hi"))
        {
        }

        listener.Flush();

        var outcomes = listener.Tags("speech.resilience.attempt.duration")
            .Select(t => t.GetValueOrDefault("outcome")?.ToString())
            .ToArray();

        Assert.Contains("success", outcomes);
    }

    /// <summary>
    /// Minimal <see cref="MeterListener"/> wrapper that records measurements for a
    /// single <see cref="Meter"/> name into in-memory buckets keyed by instrument name.
    /// </summary>
    private sealed class MetricCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly object _lock = new();
        private readonly Dictionary<string, List<(double Value, Dictionary<string, object?> Tags)>> _measurements = new();
        private string? _meterName;

        public void Subscribe(string meterName)
        {
            _meterName = meterName;
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == meterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>(Record);
            _listener.SetMeasurementEventCallback<double>(Record);
            _listener.Start();
        }

        private void Record<T>(Instrument instrument, T measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
            where T : struct
        {
            var bag = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                bag[tag.Key] = tag.Value;
            }

            var value = Convert.ToDouble(measurement);
            lock (_lock)
            {
                if (!_measurements.TryGetValue(instrument.Name, out var bucket))
                {
                    bucket = new();
                    _measurements[instrument.Name] = bucket;
                }
                bucket.Add((value, bag));
            }
        }

        public void Flush() => _listener.RecordObservableInstruments();

        public double Sum(string instrument)
        {
            lock (_lock)
            {
                return _measurements.TryGetValue(instrument, out var bucket)
                    ? bucket.Sum(b => b.Value)
                    : 0d;
            }
        }

        public IReadOnlyList<Dictionary<string, object?>> Tags(string instrument)
        {
            lock (_lock)
            {
                return _measurements.TryGetValue(instrument, out var bucket)
                    ? bucket.Select(b => b.Tags).ToArray()
                    : Array.Empty<Dictionary<string, object?>>();
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
