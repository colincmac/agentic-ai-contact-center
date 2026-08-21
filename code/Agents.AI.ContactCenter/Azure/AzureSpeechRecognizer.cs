using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.Media.Audio.Resilience;
using Agents.AI.ContactCenter.Media.Transcription;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agents.AI.ContactCenter.Azure;

/// <summary>
/// Pool of pre-warmed <see cref="SpeechRecognizer"/> instances. Creating a recognizer
/// is expensive (it opens a websocket and authenticates), so we recycle them between
/// calls to avoid first-byte latency.
/// </summary>
internal sealed class RecognizerPool : IDisposable
{
    private readonly Func<SpeechRecognizer> _recognizerGenerator;
    private readonly ConcurrentStack<SpeechRecognizer> _recognizerStack = new();
    private readonly int _maximumRetainedCapacity;
    private readonly ILogger _logger;

    public RecognizerPool(
        Func<SpeechRecognizer> recognizerGenerator,
        int initialCapacity = 2,
        int maximumRetainedCapacity = 100,
        ILogger? logger = null)
    {
        _recognizerGenerator = recognizerGenerator;
        _maximumRetainedCapacity = maximumRetainedCapacity;
        _logger = logger ?? NullLogger.Instance;

        _logger.LogInformation("Creating {InitialCapacity} recognizer(s) and warming up", initialCapacity);
        for (var i = 0; i < initialCapacity; i++)
        {
            var item = _recognizerGenerator();
            Put(item);
        }
    }

    public SpeechRecognizer Get()
    {
        if (!_recognizerStack.TryPop(out var item))
        {
            _logger.LogDebug("Pool empty; creating new recognizer");
            item = _recognizerGenerator();
        }

        return item;
    }

    public void Put(SpeechRecognizer item)
    {
        if (_recognizerStack.Count < _maximumRetainedCapacity)
        {
            _recognizerStack.Push(item);
        }
        else
        {
            item.Dispose();
        }
    }

    public void Dispose()
    {
        while (_recognizerStack.TryPop(out var recognizer))
        {
            recognizer.Dispose();
        }
    }
}

/// <summary>
/// <see cref="ISpeechRecognizer"/> implementation backed by the Azure Speech SDK.
/// Wraps a <see cref="RecognizerPool"/> so concurrent callers share warmed-up
/// connections, and streams transcript segments as they arrive using push audio input.
/// </summary>
public sealed class AzureSpeechRecognizer : ISpeechRecognizer
{
    private readonly SpeechConfig _speechConfig;
    private readonly RecognizerPool _pool;
    private readonly ILogger<AzureSpeechRecognizer> _logger;

    private SpeechRecognizer? _recognizer;
    private PushAudioInputStream? _audioInputStream;
    private AudioConfig? _audioConfig;
    private Channel<TranscriptSegment>? _transcriptChannel;
    private Task? _recognitionTask;
    private readonly CancellationTokenSource _cts = new();
    private bool _isStarted;
    private bool _isDisposed;

    public AzureSpeechRecognizer(
        SpeechConfig speechConfig,
        int concurrency = 2,
        ILogger<AzureSpeechRecognizer>? logger = null)
    {
        _speechConfig = speechConfig;
        _logger = logger ?? NullLogger<AzureSpeechRecognizer>.Instance;

        _pool = new RecognizerPool(
            () =>
            {
                var audioInputStream = AudioInputStream.CreatePushStream();
                var audioConfig = AudioConfig.FromStreamInput(audioInputStream);
                return new SpeechRecognizer(_speechConfig, audioConfig);
            },
            initialCapacity: concurrency,
            logger: _logger);
    }

    /// <inheritdoc />
    public async Task WriteAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (!_isStarted)
        {
            await StartRecognitionAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_audioInputStream is null)
        {
            throw new InvalidOperationException("Audio input stream is not initialized.");
        }

        // Convert ReadOnlyMemory<byte> to byte array for the SDK
        var buffer = audioData.ToArray();
        _audioInputStream.Write(buffer);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TranscriptSegment> GetTranscriptsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (!_isStarted)
        {
            await StartRecognitionAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_transcriptChannel is null)
        {
            throw new InvalidOperationException("Transcript channel is not initialized.");
        }

        await foreach (var segment in _transcriptChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return segment;
        }
    }

    /// <inheritdoc />
    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (!_isStarted)
        {
            return;
        }

        try
        {
            // Signal end of audio stream
            _audioInputStream?.Close();

            // Stop continuous recognition
            if (_recognizer is not null)
            {
                await _recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);
            }

            // Complete the transcript channel
            _transcriptChannel?.Writer.Complete();

            // Wait for recognition task to complete
            if (_recognitionTask is not null)
            {
                await _recognitionTask.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error completing speech recognition");
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);

            if (_recognizer is not null)
            {
                try
                {
                    await _recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error stopping recognition during disposal");
                }
            }

            _transcriptChannel?.Writer.Complete();

            if (_recognitionTask is not null)
            {
                try
                {
                    await _recognitionTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error waiting for recognition task during disposal");
                }
            }

            _audioInputStream?.Dispose();
            _audioConfig?.Dispose();

            // Return recognizer to pool if it's still healthy
            if (_recognizer is not null && !_cts.IsCancellationRequested)
            {
                _pool.Put(_recognizer);
            }
            else
            {
                _recognizer?.Dispose();
            }
        }
        finally
        {
            _cts.Dispose();
            _pool.Dispose();
        }
    }

    private async Task StartRecognitionAsync(CancellationToken cancellationToken)
    {
        if (_isStarted)
        {
            return;
        }

        _isStarted = true;

        // Create push audio input stream (16 kHz, 16-bit, mono PCM)
        _audioInputStream = AudioInputStream.CreatePushStream(AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));
        _audioConfig = AudioConfig.FromStreamInput(_audioInputStream);

        // Get recognizer from pool or create new one
        _recognizer = new SpeechRecognizer(_speechConfig, _audioConfig);

        // Create channel for transcript segments
        _transcriptChannel = Channel.CreateUnbounded<TranscriptSegment>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        // Wire up event handlers
        _recognizer.Recognizing += OnRecognizing;
        _recognizer.Recognized += OnRecognized;
        _recognizer.Canceled += OnCanceled;
        _recognizer.SessionStopped += OnSessionStopped;

        // Start continuous recognition
        _recognitionTask = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("Starting continuous speech recognition");
                await _recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting continuous recognition");
                _transcriptChannel.Writer.TryComplete(ex);
            }
        }, cancellationToken);

        await Task.Delay(100, cancellationToken).ConfigureAwait(false); // Give recognition a moment to start
    }

    private void OnRecognizing(object? sender, SpeechRecognitionEventArgs e)
    {
        if (e.Result.Reason == ResultReason.RecognizingSpeech && !string.IsNullOrEmpty(e.Result.Text))
        {
            _logger.LogDebug("Recognizing: {Text}", e.Result.Text);

            var segment = new TranscriptSegment
            {
                Text = e.Result.Text,
                Role = ChatRole.User,
                IsFinal = false,
                UtteranceStart = DateTimeOffset.UtcNow
            };

            _transcriptChannel?.Writer.TryWrite(segment);
        }
    }

    private void OnRecognized(object? sender, SpeechRecognitionEventArgs e)
    {
        if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrEmpty(e.Result.Text))
        {
            _logger.LogInformation("Recognized: {Text}", e.Result.Text);

            var segment = new TranscriptSegment
            {
                Text = e.Result.Text,
                Role = ChatRole.User,
                IsFinal = true,
                UtteranceStart = DateTimeOffset.UtcNow,
                UtteranceEnd = DateTimeOffset.UtcNow,
                Confidence = 1.0 // Azure SDK doesn't expose confidence directly in this API
            };

            _transcriptChannel?.Writer.TryWrite(segment);
        }
        else if (e.Result.Reason == ResultReason.NoMatch)
        {
            _logger.LogDebug("No speech could be recognized");
        }
    }

    private void OnCanceled(object? sender, SpeechRecognitionCanceledEventArgs e)
    {
        _logger.LogWarning("Recognition canceled: Reason={Reason}", e.Reason);

        if (e.Reason == CancellationReason.Error)
        {
            _logger.LogError("Recognition error: ErrorCode={ErrorCode} Details={Details}", e.ErrorCode, e.ErrorDetails);
            _transcriptChannel?.Writer.TryComplete(new SpeechSdkException(e.ErrorCode, e.ErrorDetails));
        }
    }

    private void OnSessionStopped(object? sender, SessionEventArgs e)
    {
        _logger.LogInformation("Recognition session stopped: SessionId={SessionId}", e.SessionId);
        _transcriptChannel?.Writer.TryComplete();
    }
}
