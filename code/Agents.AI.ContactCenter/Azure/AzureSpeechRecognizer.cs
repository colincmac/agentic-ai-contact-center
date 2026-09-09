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

/// <summary>One independently owned recognition session. Construction does not connect to Speech.</summary>
public sealed class AzureSpeechRecognizer : ISpeechRecognizer
{
    private readonly SpeechConfig _speechConfig;
    private readonly ILogger<AzureSpeechRecognizer> _logger;
    private readonly TimeSpan _operationTimeout;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<TranscriptSegment> _transcripts = Channel.CreateBounded<TranscriptSegment>(
        new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private SpeechRecognizer? _recognizer;
    private PushAudioInputStream? _input;
    private AudioConfig? _audioConfig;
    private Task? _start;
    private Task? _completion;
    private Task? _disposal;
    private bool _disposed;

    public AzureSpeechRecognizer(SpeechConfig speechConfig, int concurrency = 2,
        ILogger<AzureSpeechRecognizer>? logger = null, TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(speechConfig);
        ArgumentOutOfRangeException.ThrowIfNegative(concurrency);
        _speechConfig = speechConfig;
        _logger = logger ?? NullLogger<AzureSpeechRecognizer>.Instance;
        _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(8);
        if (_operationTimeout <= TimeSpan.Zero || _operationTimeout.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }
    }

    private Task EnsureStarted(CancellationToken ct, bool allowCompleted = false)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completion is not null)
            {
                if (allowCompleted) { return _start?.WaitAsync(ct) ?? Task.CompletedTask; }
                throw new InvalidOperationException("This recognition session has completed.");
            }
            return (_start ??= StartCoreAsync()).WaitAsync(ct);
        }
    }

    private async Task StartCoreAsync()
    {
        try
        {
            using var format = AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1);
            _input = AudioInputStream.CreatePushStream(format);
            _audioConfig = AudioConfig.FromStreamInput(_input);
            _recognizer = new SpeechRecognizer(_speechConfig, _audioConfig);
            _recognizer.Recognizing += OnRecognizing;
            _recognizer.Recognized += OnRecognized;
            _recognizer.Canceled += OnCanceled;
            _recognizer.SessionStopped += OnStopped;
            await _recognizer.StartContinuousRecognitionAsync().WaitAsync(_operationTimeout, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _transcripts.Writer.TryComplete(ex);
            throw;
        }
    }

    public async Task WriteAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
    {
        await EnsureStarted(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_completion is not null) { throw new InvalidOperationException("This recognition session has completed."); }
            _input!.Write(audioData.ToArray());
        }
    }

    public async IAsyncEnumerable<TranscriptSegment> GetTranscriptsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureStarted(cancellationToken, allowCompleted: true).ConfigureAwait(false);
        await foreach (var segment in _transcripts.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return segment;
        }
    }

    public Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return (_completion ??= CompleteCoreAsync()).WaitAsync(cancellationToken);
        }
    }

    private async Task CompleteCoreAsync()
    {
        try
        {
            if (_start is not null) { await _start.ConfigureAwait(false); }
            lock (_gate) { _input?.Close(); }
            if (_recognizer is not null)
            {
                await _recognizer.StopContinuousRecognitionAsync().WaitAsync(_operationTimeout, _lifetime.Token).ConfigureAwait(false);
            }
            _transcripts.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _transcripts.Writer.TryComplete(ex);
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is not null) { return new(_disposal); }
            _disposed = true;
            return new(_disposal = DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_start is not null)
            {
                try { await _start.ConfigureAwait(false); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
                catch (Exception ex) { _logger.LogDebug(ex, "Recognition startup failed before disposal."); }
            }
            if (_completion is not null)
            {
                try { await _completion.ConfigureAwait(false); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
                catch (Exception ex) { _logger.LogDebug(ex, "Recognition completion failed before disposal."); }
            }
            if (_recognizer is not null)
            {
                await _recognizer.StopContinuousRecognitionAsync().WaitAsync(_operationTimeout).ConfigureAwait(false);
            }
        }
        finally
        {
            _transcripts.Writer.TryComplete();
            if (_recognizer is not null)
            {
                _recognizer.Recognizing -= OnRecognizing;
                _recognizer.Recognized -= OnRecognized;
                _recognizer.Canceled -= OnCanceled;
                _recognizer.SessionStopped -= OnStopped;
                _recognizer.Dispose();
            }
            _input?.Dispose();
            _audioConfig?.Dispose();
            _lifetime.Dispose();
        }
    }

    private void Publish(SpeechRecognitionEventArgs e, bool final)
    {
        if (string.IsNullOrEmpty(e.Result.Text)) { return; }
        var at = DateTimeOffset.UtcNow;
        if (!_transcripts.Writer.TryWrite(new TranscriptSegment
        {
            Text = e.Result.Text, Role = ChatRole.User, IsFinal = final,
            UtteranceStart = at, UtteranceEnd = final ? at : null,
        }))
        {
            _transcripts.Writer.TryComplete(new InvalidOperationException("Speech transcript buffer exceeded its capacity."));
        }
    }

    private void OnRecognizing(object? sender, SpeechRecognitionEventArgs e) => Publish(e, final: false);
    private void OnRecognized(object? sender, SpeechRecognitionEventArgs e)
    {
        if (e.Result.Reason == ResultReason.RecognizedSpeech) { Publish(e, final: true); }
    }
    private void OnCanceled(object? sender, SpeechRecognitionCanceledEventArgs e)
    {
        if (e.Reason == CancellationReason.Error) { _transcripts.Writer.TryComplete(new SpeechSdkException(e.ErrorCode, e.ErrorDetails)); }
    }
    private void OnStopped(object? sender, SessionEventArgs e) => _transcripts.Writer.TryComplete();
}
