using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Agents.AI.Extensions.Helpers.Streaming;


public class RawMediaStreamChannelOptions
{
    /// <summary>
    /// Maximum number of bytes to buffer before write backpressure is applied.
    /// </summary>
    public int? Capacity { get; set; } = null;
    /// <summary>
    /// Minimum segment size and WebSocket read chunk size.
    /// </summary>
    public int? ChunkSize { get; set; } = null;
    /// <summary>
    /// Optional memory pool for pipe segments. If null, the shared pool is used.
    /// </summary>
    public MemoryPool<byte>? MemoryPool { get; set; } = null;
}

/// <summary>
/// Distributes a continuous media byte stream to multiple independent consumers with
/// bounded buffering, backpressure and efficient memory usage.
/// </summary>
/// <remarks>
/// Internally a single <see cref="Pipe"/> is used for buffering the incoming stream data.
/// Each consumer has its own bounded channel to decouple slow readers while preventing
/// unbounded memory growth. Data chunks are copied once per consumer to ensure safety when
/// pooled buffers are returned.
/// </remarks>
public sealed class RawMediaStreamChannel : IAsyncDisposable
{

    public const int DEFAULT_CAPACITY = 1024 * 1024;

    public const int DEFAULT_CHUNK_SIZE = 4096;


    internal readonly Pipe _pipe;

    public PipeReader Reader => _pipe.Reader;
    public PipeWriter Writer => _pipe.Writer;

    public int ChunkSize => _chunkSize;

    private readonly ConcurrentDictionary<Guid, RawMediaPipeSubscription> _consumers;
    private readonly int _chunkSize;
    private readonly CancellationTokenSource _disposalTokenSource;
    private readonly Task _distributionTask;

    private long _totalBytesWritten;
    private long _totalBytesDistributed;
    private int _disposed;

    /// <summary>
    /// Gets the number of currently active consumers.
    /// </summary>
    public int ConsumerCount => _consumers.Count;

    /// <summary>
    /// Gets the maximum buffered capacity (in bytes) before write backpressure is applied.
    /// </summary>
    public int Capacity { get; }

    /// <summary>
    /// Gets the number of bytes currently buffered (written but not yet distributed to consumers).
    /// </summary>
    public long BufferedBytes => _totalBytesWritten - _totalBytesDistributed;

    /// <summary>
    /// Gets the cumulative number of bytes written into this channel since creation.
    /// </summary>
    public long TotalBytesWritten => Interlocked.Read(ref _totalBytesWritten);

    /// <summary>
    /// Gets the cumulative number of bytes distributed to consumers since creation.
    /// </summary>
    public long TotalBytesDistributed => Interlocked.Read(ref _totalBytesDistributed);

    /// <summary>
    /// Initializes a new instance of the <see cref="RawMediaStreamChannel"/> class.
    /// </summary>
    /// <param name="capacity">Maximum number of bytes to buffer before pausing writes.</param>
    /// <param name="chunkSize">Minimum segment size and WebSocket read chunk size.</param>
    /// <param name="memoryPool">Optional memory pool for pipe segments. If null, the shared pool is used.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="capacity"/> or <paramref name="chunkSize"/> are not positive.</exception>
    public RawMediaStreamChannel(RawMediaStreamChannelOptions? options = null) : this(options?.Capacity ?? DEFAULT_CAPACITY, options?.ChunkSize ?? DEFAULT_CHUNK_SIZE, options?.MemoryPool) { }

    public RawMediaStreamChannel(
        int capacity = DEFAULT_CAPACITY,
        int chunkSize = DEFAULT_CHUNK_SIZE,
        MemoryPool<byte>? memoryPool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);

        Capacity = capacity;
        _chunkSize = chunkSize;
        _consumers = new ConcurrentDictionary<Guid, RawMediaPipeSubscription>();
        _disposalTokenSource = new CancellationTokenSource();

        // Single pipe handles all buffering and backpressure
        var pipeOptions = new PipeOptions(
            pool: memoryPool ?? MemoryPool<byte>.Shared,
            readerScheduler: PipeScheduler.ThreadPool,
            writerScheduler: PipeScheduler.Inline, // Inline for lower latency
            pauseWriterThreshold: capacity + 1, // // +1 prevents pause when remote window is exactly filled
            resumeWriterThreshold: capacity / 2,
            minimumSegmentSize: chunkSize,
            useSynchronizationContext: false
        );

        _pipe = new Pipe(pipeOptions);
        _distributionTask = Task.Run(() => DistributeAsync(_disposalTokenSource.Token));
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte[]> data, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (data.Length == 0)
        {
            return;
        }

        foreach (var chunk in data.ToArray())
        {
            await WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (data.Length == 0)
        {
            return;
        }

        var flushResult = await _pipe.Writer.WriteAsync(data, cancellationToken).ConfigureAwait(false);

        if (flushResult.IsCompleted)
        {
            throw new InvalidOperationException("Buffer has been closed");
        }

        Interlocked.Add(ref _totalBytesWritten, data.Length);
    }

    /// <summary>
    /// Copies the content of a <see cref="Stream"/> into the distributor.
    /// </summary>
    /// <param name="source">Source stream to read from.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <exception cref="ObjectDisposedException">If the distributor has been disposed.</exception>
    public async ValueTask WriteAsync(Stream source, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await source.CopyToAsync(_pipe.Writer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes an async enumerable of byte array chunks into the distributor.
    /// </summary>
    /// <param name="source">Async enumerable producing byte array chunks.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <exception cref="ObjectDisposedException">If the distributor has been disposed.</exception>
    public async ValueTask WriteAsync(IAsyncEnumerable<byte[]> source, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await foreach (var chunk in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            await WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads frames from an open <see cref="WebSocket"/> and writes them into the distributor.
    /// </summary>
    /// <param name="webSocket">The open web socket to receive from.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    /// <exception cref="ObjectDisposedException">If the distributor has been disposed.</exception>
    public async ValueTask WriteAsync(WebSocket webSocket, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var writer = _pipe.Writer;
        while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var memory = writer.GetMemory(_chunkSize);
            var result = await webSocket.ReceiveAsync(memory, cancellationToken).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close || result.Count == 0)
            {
                break;
            }

            writer.Advance(result.Count);

            // Only flush periodically or when buffer is getting full
            if (writer.UnflushedBytes >= _chunkSize)
            {
                var flushResult = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (flushResult.IsCompleted)
                {
                    break;
                }
            }

            Interlocked.Add(ref _totalBytesWritten, result.Count);
        }

        // Final flush
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a new consumer that can independently read distributed media chunks.
    /// </summary>
    /// <returns>A new <see cref="RawMediaPipeSubscription"/> instance.</returns>
    /// <exception cref="ObjectDisposedException">If the distributor has been disposed.</exception>
    public RawMediaPipeSubscription Subscribe()
    {
        ThrowIfDisposed();

        var consumer = new RawMediaPipeSubscription(this);
        _consumers.TryAdd(consumer.Id, consumer);
        return consumer;
    }

    internal void RemoveConsumer(Guid consumerId)
    {
        _consumers.TryRemove(consumerId, out _);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _disposalTokenSource.CancelAsync();

        // Complete the writer to signal EOF
        await _pipe.Writer.CompleteAsync().ConfigureAwait(false);

        // Wait for distribution to complete
        await _distributionTask.ConfigureAwait(false);

        // Dispose all consumers
        var disposeTasks = _consumers.Values.Select(c => c.DisposeAsync().AsTask());
        await Task.WhenAll(disposeTasks).ConfigureAwait(false);

        _consumers.Clear();
    }


    private async Task DistributeAsync(CancellationToken cancellationToken)
    {
        var reader = _pipe.Reader;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

                if (result.IsCompleted)
                    break;

                var buffer = result.Buffer;
                if (!buffer.IsEmpty && !_consumers.IsEmpty)
                {
                    // Copy once from the pipe buffer; each consumer's EnqueueAsync makes its
                    // own copy so we can safely return the rented array after all enqueues complete.
                    var data = ArrayPool<byte>.Shared.Rent((int)buffer.Length);
                    try
                    {
                        buffer.CopyTo(data);
                        var memory = data.AsMemory(0, (int)buffer.Length);

                        var consumers = _consumers.Values;
                        var enqueueTasks = new ValueTask[consumers.Count];
                        var i = 0;
                        foreach (var consumer in consumers)
                        {
                            enqueueTasks[i++] = consumer.EnqueueAsync(memory, cancellationToken);
                        }

                        for (var j = 0; j < i; j++)
                        {
                            try
                            {
                                await enqueueTasks[j].ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch
                            {
                                // Individual consumer failure should not block distribution
                            }
                        }

                        Interlocked.Add(ref _totalBytesDistributed, buffer.Length);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(data);
                    }
                }

                reader.AdvanceTo(buffer.End);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }


}
/// <summary>
/// Represents a consumer of media stream data.
/// </summary>
public sealed class RawMediaPipeSubscription : IAsyncDisposable
{
    private readonly RawMediaStreamChannel _buffer;
    private readonly Channel<ReadOnlyMemory<byte>> _channel;
    private int _disposed;

    /// <summary>
    /// Gets the unique identifier for this consumer instance.
    /// </summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>
    /// Gets the number of buffered chunks currently available to read.
    /// </summary>
    public int Available => Math.Max(0, _channel.Reader.Count);

    /// <summary>
    /// Initializes a new instance of the <see cref="RawMediaPipeSubscription"/> class.
    /// </summary>
    /// <param name="buffer">The parent distributor.</param>
    internal RawMediaPipeSubscription(RawMediaStreamChannel buffer)
    {
        _buffer = buffer;

        // Bounded channel prevents runaway memory if consumer is slow
        _channel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = true // Lower latency
        });
    }



    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        await foreach (var data in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return data;
        }
    }

    /// <summary>
    /// Attempts to read a single chunk into the provided buffer.
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="cancellationToken">Token to observe for cancellation while waiting for data.</param>
    /// <returns>The number of bytes copied; 0 if no data could be read.</returns>
    /// <exception cref="ObjectDisposedException">If the consumer has been disposed.</exception>
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        if (_channel.Reader.TryRead(out var data))
        {
            var bytesToCopy = Math.Min(buffer.Length, data.Length);
            data[..bytesToCopy].CopyTo(buffer);
            return bytesToCopy;
        }

        if (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_channel.Reader.TryRead(out data))
            {
                var bytesToCopy = Math.Min(buffer.Length, data.Length);
                data[..bytesToCopy].CopyTo(buffer);
                return bytesToCopy;
            }
        }

        return 0;
    }

    /// <summary>
    /// Asynchronously disposes the consumer, completes its channel and deregisters it.
    /// </summary>
    /// <returns>A task representing the dispose operation.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();

        // Drain any remaining data
        await foreach (var _ in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            // Discard
        }

        _buffer.RemoveConsumer(Id);
    }

    /// <summary>
    /// Enqueues a new data chunk for the consumer. Internal use by distributor.
    /// </summary>
    /// <param name="data">Chunk to enqueue.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    internal async ValueTask EnqueueAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (_disposed != 0)
        {
            return;
        }

        // Make a copy since the source buffer will be returned to pool
        var copy = new byte[data.Length];
        data.CopyTo(copy);

        await _channel.Writer.WriteAsync(copy, cancellationToken).ConfigureAwait(false);
    }
}
