using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.Media.Audio.Resilience;
using Microsoft.CognitiveServices.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Azure;


/// <summary>
/// Pool of pre-warmed <see cref="SpeechSynthesizer"/> instances. Creating a synthesizer
/// is expensive (it opens a websocket and authenticates), so we recycle them between
/// calls to avoid first-byte latency.
/// </summary>
internal sealed class SynthesizerPool : IDisposable
{
    private readonly Func<SpeechSynthesizer> _synthesizerGenerator;
    private readonly ConcurrentStack<SpeechSynthesizer> _synthesizerStack = new();
    private readonly int _maximumRetainedCapacity;
    private readonly ILogger _logger;

    public SynthesizerPool(
        Func<SpeechSynthesizer> synthesizerGenerator,
        int initialCapacity = 2,
        int maximumRetainedCapacity = 100,
        ILogger? logger = null)
    {
        _synthesizerGenerator = synthesizerGenerator;
        _maximumRetainedCapacity = maximumRetainedCapacity;
        _logger = logger ?? NullLogger.Instance;

        _logger.LogInformation("Creating {InitialCapacity} synthesizer(s) and warming up", initialCapacity);
        for (var i = 0; i < initialCapacity; i++)
        {
            var item = _synthesizerGenerator();

            // warm up synthesizer so the first real request doesn't pay the connection cost
            item.SpeakTextAsync("1").GetAwaiter().GetResult();
            Put(item);
        }
    }

    public SpeechSynthesizer Get()
    {
        if (!_synthesizerStack.TryPop(out var item))
        {
            _logger.LogDebug("Pool empty; creating new synthesizer");
            item = _synthesizerGenerator();
        }

        return item;
    }

    public void Put(SpeechSynthesizer item)
    {
        if (_synthesizerStack.Count < _maximumRetainedCapacity)
        {
            _synthesizerStack.Push(item);
        }
        else
        {
            item.Dispose();
        }
    }

    public void Dispose()
    {
        while (_synthesizerStack.TryPop(out var synthesizer))
        {
            synthesizer.Dispose();
        }
    }
}
/// <summary>
/// <see cref="ISpeechSynthesizer"/> implementation backed by the Azure Speech SDK.
/// Wraps a <see cref="SynthesizerPool"/> so concurrent callers share warmed-up
/// connections, and streams audio frames as they arrive instead of buffering
/// the full utterance.
/// </summary>
public sealed class AzureSpeechSynthesizer : ISpeechSynthesizer, IDisposable
{
    private const int ReadBufferSize = 4096;

    private readonly SpeechConfig _speechConfig;
    private readonly SynthesizerPool _pool;
    private readonly ILogger<AzureSpeechSynthesizer> _logger;
    private readonly string _locale;
    private readonly string _gender;

    public AzureSpeechSynthesizer(
        SpeechConfig speechConfig,
        int concurrency = 2,
        string gender = "Female",
        ILogger<AzureSpeechSynthesizer>? logger = null)
    {
        _speechConfig = speechConfig;

        _gender = gender;
        _locale = speechConfig.SpeechSynthesisLanguage ?? "en-US";
        _logger = logger ?? NullLogger<AzureSpeechSynthesizer>.Instance;

        _pool = new SynthesizerPool(
            () => new SpeechSynthesizer(_speechConfig, null),
            initialCapacity: concurrency,
            logger: _logger);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        SynthesizerInputFormat inputFormat = SynthesizerInputFormat.SSML,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var ssml = inputFormat == SynthesizerInputFormat.SSML ? text : GenerateSsml(_locale, _gender, _speechConfig.SpeechSynthesisVoiceName, text);
        var synthesizer = _pool.Get();

        void OnCanceled(object? sender, SpeechSynthesisEventArgs e)
        {
            var details = SpeechSynthesisCancellationDetails.FromResult(e.Result);
            if (details.Reason == CancellationReason.Error)
            {
                _logger.LogError(
                    "Speech synthesis canceled: ErrorCode={ErrorCode} Details={Details}",
                    details.ErrorCode,
                    details.ErrorDetails);
            }
            else
            {
                _logger.LogInformation("Speech synthesis canceled: {Reason}", details.Reason);
            }
        }

        synthesizer.SynthesisCanceled += OnCanceled;

        SpeechSynthesisResult? result = null;
        AudioDataStream? audioStream = null;
        var faulted = false;

        // Wire cancellation through to the SDK so an in-flight request stops promptly.
        await using var registration = cancellationToken.Register(
            static s => _ = ((SpeechSynthesizer)s!).StopSpeakingAsync(),
            synthesizer);

        try
        {
            try
            {
                var startTimestamp = Environment.TickCount64;
                result = await synthesizer.StartSpeakingSsmlAsync(ssml).ConfigureAwait(false);

                if (result.Reason != ResultReason.SynthesizingAudioStarted)
                {
                    if (result.Reason == ResultReason.Canceled)
                    {
                        var details = SpeechSynthesisCancellationDetails.FromResult(result);
                        _logger.LogWarning(
                            "Synthesis did not start: {Reason} {ErrorCode} {Details}",
                            details.Reason,
                            details.ErrorCode,
                            details.ErrorDetails);

                        if (details.Reason == CancellationReason.Error)
                        {
                            faulted = true;
                            throw new SpeechSdkException(details.ErrorCode, details.ErrorDetails);
                        }
                    }

                    yield break;
                }

                audioStream = AudioDataStream.FromResult(result);
                _logger.LogDebug(
                    "First byte latency: {LatencyMs} ms",
                    Environment.TickCount64 - startTimestamp);
            }
            catch (Exception ex)
            {
                faulted = true;
                _logger.LogError(ex, "Failed to start speech synthesis");
                throw;
            }

            var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    uint filledSize;
                    try
                    {
                        // ReadData is a blocking call that pulls the next chunk off the websocket;
                        // hop to the thread pool so we don't stall the caller's sync context.
                        filledSize = await Task.Run(
                            () => audioStream.ReadData(buffer),
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        faulted = true;
                        _logger.LogError(ex, "Error reading synthesized audio stream");
                        throw;
                    }

                    if (filledSize == 0)
                    {
                        break;
                    }

                    // Copy out of the rented buffer; the consumer owns the yielded memory
                    // and we can't track when it's safe to return the rental.
                    var chunk = new byte[filledSize];
                    Buffer.BlockCopy(buffer, 0, chunk, 0, (int)filledSize);
                    yield return chunk;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        finally
        {
            synthesizer.SynthesisCanceled -= OnCanceled;
            audioStream?.Dispose();
            result?.Dispose();

            if (faulted || cancellationToken.IsCancellationRequested)
            {
                // The synthesizer's connection state is suspect after an error
                // or hard cancel; drop it rather than reusing.
                synthesizer.Dispose();
            }
            else
            {
                _pool.Put(synthesizer);
            }
        }
    }

    public void Dispose() => _pool.Dispose();

    /// <summary>
    /// Generates SSML.
    /// </summary>
    private static string GenerateSsml(string locale, string gender, string name, string text)
    {
        // SSML requires this namespace; without it Azure Speech rejects the payload.
        XNamespace ssml = "http://www.w3.org/2001/10/synthesis";

        var ssmlDoc = new XDocument(
            new XElement(ssml + "speak",
                new XAttribute("version", "1.0"),
                new XAttribute(XNamespace.Xml + "lang", locale),
                new XElement(ssml + "voice",
                    new XAttribute("name", name),
                    new XElement(ssml + "prosody",
                        new XAttribute("rate", "-5%"),
                        text))));

        return ssmlDoc.ToString(SaveOptions.DisableFormatting);
    }


}
