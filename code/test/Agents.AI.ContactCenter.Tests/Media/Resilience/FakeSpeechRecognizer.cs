using System.Threading.Channels;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.Media.Transcription;

namespace Agents.AI.ContactCenter.Tests.Media.Resilience;

/// <summary>
/// Scriptable <see cref="ISpeechRecognizer"/> test double. Each instance
/// represents one "session" that the resilient decorator may create, and the
/// behavior of that session is driven by a <see cref="RecognizerScript"/>
/// dequeued from a shared queue. This allows tests to script multi-attempt
/// failover scenarios deterministically.
/// </summary>
internal sealed class FakeSpeechRecognizer : ISpeechRecognizer
{
    private readonly Channel<TranscriptSegment> _transcripts =
        Channel.CreateUnbounded<TranscriptSegment>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly RecognizerScript _script;
    private int _audioWrites;
    private int _completeCount;
    private int _disposed;

    public FakeSpeechRecognizer(RecognizerScript script)
    {
        _script = script ?? throw new ArgumentNullException(nameof(script));
    }

    /// <summary>Number of times <see cref="WriteAudioAsync"/> was called.</summary>
    public int AudioWriteCount => Volatile.Read(ref _audioWrites);

    /// <summary>Number of times <see cref="CompleteAsync"/> was called.</summary>
    public int CompleteCount => Volatile.Read(ref _completeCount);

    /// <summary>Whether this instance was disposed.</summary>
    public bool WasDisposed => Volatile.Read(ref _disposed) != 0;

    public Task WriteAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _audioWrites);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<TranscriptSegment> GetTranscriptsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Kick off scripted producer.
        _ = Task.Run(() => RunScriptAsync(cancellationToken), CancellationToken.None);

        await foreach (var segment in _transcripts.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return segment;
        }
    }

    public Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _completeCount);
        _transcripts.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _transcripts.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private async Task RunScriptAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_script.PreStartDelay > TimeSpan.Zero)
            {
                await Task.Delay(_script.PreStartDelay, cancellationToken).ConfigureAwait(false);
            }

            foreach (var step in _script.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (step.Delay > TimeSpan.Zero)
                {
                    await Task.Delay(step.Delay, cancellationToken).ConfigureAwait(false);
                }

                if (step.Segment is not null)
                {
                    await _transcripts.Writer
                        .WriteAsync(step.Segment, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (step.Exception is not null)
                {
                    _transcripts.Writer.TryComplete(step.Exception);
                    return;
                }
            }

            _transcripts.Writer.TryComplete();
        }
        catch (OperationCanceledException)
        {
            _transcripts.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _transcripts.Writer.TryComplete(ex);
        }
    }
}

/// <summary>A scripted sequence of behaviors for a single fake recognizer session.</summary>
internal sealed record RecognizerScript(IReadOnlyList<RecognizerStep> Steps, TimeSpan PreStartDelay = default);

/// <summary>One step in a recognizer script: optional delay, then emit a segment and/or fault.</summary>
internal sealed record RecognizerStep(
    TimeSpan Delay = default,
    TranscriptSegment? Segment = null,
    Exception? Exception = null);
