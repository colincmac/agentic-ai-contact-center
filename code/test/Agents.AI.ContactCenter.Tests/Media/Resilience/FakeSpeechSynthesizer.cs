using System.Runtime.CompilerServices;
using Agents.AI.ContactCenter.Media.Audio;

namespace Agents.AI.ContactCenter.Tests.Media.Resilience;

/// <summary>
/// Scriptable <see cref="ISpeechSynthesizer"/> test double. Per-call behavior is
/// dictated by a queue of <see cref="SynthesizerScript"/> instances; each call
/// to <see cref="SynthesizeAsync"/> dequeues the next script.
/// </summary>
internal sealed class FakeSpeechSynthesizer : ISpeechSynthesizer
{
    private readonly Queue<SynthesizerScript> _scripts;
    private int _callCount;

    public FakeSpeechSynthesizer(IEnumerable<SynthesizerScript> scripts)
    {
        _scripts = new Queue<SynthesizerScript>(scripts ?? throw new ArgumentNullException(nameof(scripts)));
    }

    public int CallCount => Volatile.Read(ref _callCount);

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        SynthesizerInputFormat inputFormat = SynthesizerInputFormat.Text,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _callCount);

        SynthesizerScript script;
        lock (_scripts)
        {
            script = _scripts.Count > 0
                ? _scripts.Dequeue()
                : SynthesizerScript.Empty;
        }

        if (script.StartException is not null && script.ChunksBeforeStartException == 0)
        {
            throw script.StartException;
        }

        var emittedChunks = 0;
        foreach (var chunk in script.Chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (script.PerChunkDelay > TimeSpan.Zero)
            {
                await Task.Delay(script.PerChunkDelay, cancellationToken).ConfigureAwait(false);
            }

            yield return chunk;
            emittedChunks++;

            if (script.MidStreamException is not null && emittedChunks == script.ChunksBeforeMidStreamException)
            {
                throw script.MidStreamException;
            }
        }
    }
}

/// <summary>
/// Script for a single synthesis call.
/// </summary>
/// <param name="Chunks">Audio chunks to emit, in order.</param>
/// <param name="StartException">If set and <paramref name="ChunksBeforeStartException"/> is 0, thrown before any chunk is yielded.</param>
/// <param name="ChunksBeforeStartException">Number of chunks to emit before throwing <paramref name="StartException"/>.</param>
/// <param name="MidStreamException">If set, thrown after <paramref name="ChunksBeforeMidStreamException"/> chunks.</param>
/// <param name="ChunksBeforeMidStreamException">Chunk count at which <paramref name="MidStreamException"/> is thrown.</param>
/// <param name="PerChunkDelay">Optional artificial delay between chunks.</param>
internal sealed record SynthesizerScript(
    IReadOnlyList<ReadOnlyMemory<byte>> Chunks,
    Exception? StartException = null,
    int ChunksBeforeStartException = 0,
    Exception? MidStreamException = null,
    int ChunksBeforeMidStreamException = 0,
    TimeSpan PerChunkDelay = default)
{
    public static SynthesizerScript Empty { get; } = new(Array.Empty<ReadOnlyMemory<byte>>());

    public static SynthesizerScript Succeed(params byte[][] chunks) =>
        new(chunks.Select(c => new ReadOnlyMemory<byte>(c)).ToArray());

    public static SynthesizerScript FailOnStart(Exception exception) =>
        new(Array.Empty<ReadOnlyMemory<byte>>(), StartException: exception);
}
