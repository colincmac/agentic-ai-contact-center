using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Agents.AI.Extensions.Helpers.Streaming;

public sealed class MessageUpdateChannel : IAsyncDisposable
{
    private readonly Lock _sync = new();
    private readonly ConcurrentDictionary<Guid, MessageUpdateChannelSubscription> _consumers = new();

    private readonly Channel<MessageUpdate> _outboundChannel;
    private bool _disposed = false;
    private readonly CancellationTokenSource _disposalTokenSource;
    private readonly Task _distributionTask;

    public MessageUpdateChannel()
    {
        _outboundChannel = Channel.CreateUnbounded<MessageUpdate>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true
        });
        _disposalTokenSource = new CancellationTokenSource();
        _distributionTask = Task.Run(() => DistributeAsync(_disposalTokenSource.Token));
    }
    public async ValueTask WriteAsync(MessageUpdate messageUpdate, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _outboundChannel.Writer.WriteAsync(messageUpdate, cancellationToken).ConfigureAwait(false);
    }

    public MessageUpdateChannelSubscription Subscribe()
    {
        var consumer = new MessageUpdateChannelSubscription(this);
        _consumers.TryAdd(consumer.Id, consumer);
        return consumer;
    }
    internal void RemoveConsumer(Guid consumerId)
    {
        _consumers.TryRemove(consumerId, out _);
    }
    private async Task DistributeAsync(CancellationToken cancellationToken)
    {
        try
        {
            while(!cancellationToken.IsCancellationRequested)
            {
                var messageUpdate = await _outboundChannel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                // Distribute to all consumers
                var distributeTasks = _consumers.Values.Select(c => c.EnqueueAsync(messageUpdate, cancellationToken));
                // Fire and forget for each consumer - they handle their own backpressure
                foreach (var consumer in _consumers.Values)
                {
#pragma warning disable CA2012 // Use ValueTasks correctly
                    _ = consumer.EnqueueAsync(messageUpdate, cancellationToken).ConfigureAwait(false);
#pragma warning restore CA2012 // Use ValueTasks correctly
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (ChannelClosedException)
        {
            // Expected during shutdown
        }

    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return; 
        }

        await _disposalTokenSource.CancelAsync();

        // Complete the writer to signal EOF
        _outboundChannel.Writer.TryComplete();

        // Wait for distribution to complete
        await _distributionTask.ConfigureAwait(false);

        // Dispose all consumers
        var disposeTasks = _consumers.Values.Select(c => c.DisposeAsync().AsTask());
        await Task.WhenAll(disposeTasks).ConfigureAwait(false);

        _consumers.Clear();
    }
    
}
public sealed class MessageUpdateChannelSubscription : IAsyncDisposable
{
    private readonly MessageUpdateChannel _buffer;
    private readonly Channel<MessageUpdate> _channel;
    private bool _disposed;
    /// <summary>
    /// Gets the unique identifier for this consumer instance.
    /// </summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>
    /// Gets the number of buffered chunks currently available to read.
    /// </summary>
    public int Available => Math.Max(0, _channel.Reader.Count);


    public MessageUpdateChannelSubscription(
        MessageUpdateChannel buffer)
    {
        _buffer = buffer;
        _channel = Channel.CreateBounded<MessageUpdate>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = true // Lower latency
        });
    }

    public async IAsyncEnumerable<MessageUpdate> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await foreach (var data in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return data;
        }
    }
    /// <summary>
    /// Enqueues a new data chunk for the consumer. Internal use by distributor.
    /// </summary>
    /// <param name="data">Chunk to enqueue.</param>
    /// <param name="cancellationToken">Token to observe for cancellation.</param>
    internal async ValueTask EnqueueAsync(MessageUpdate data, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        await _channel.Writer.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;

        _channel.Writer.TryComplete();

        // Drain any remaining data
        await foreach (var _ in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            // Discard
        }

        _buffer.RemoveConsumer(Id);
    }
}
