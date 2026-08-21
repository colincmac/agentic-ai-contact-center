using System.Net.WebSockets;

namespace Agents.AI.Extensions.Helpers.Streaming;

/// <summary>
/// Proxies bidirectional audio streams between two WebSockets using efficient buffering.
/// </summary>
public sealed class WebSocketProxy(int bufferCapacity = 1024 * 1024, int chunkSize = 4096) : IAsyncDisposable
{
    private readonly RawMediaStreamChannel _forwardBuffer = new(bufferCapacity, chunkSize);
    private readonly RawMediaStreamChannel _backwardBuffer = new(bufferCapacity, chunkSize);
    private readonly CancellationTokenSource _disposalCts = new();
    private int _disposed;

    /// <summary>
    /// Proxies audio between two WebSockets bidirectionally.
    /// </summary>
    public async Task ProxyAsync(
        WebSocket source,
        WebSocket destination,
        WebSocketMessageType sourceMessageType = WebSocketMessageType.Binary, 
        WebSocketMessageType destinationMessageType = WebSocketMessageType.Binary, 
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposalCts.Token);

        // Start both directions concurrently
        var forwardTask = ProxyDirectionAsync(
            source, destination, _forwardBuffer, destinationMessageType, linkedCts.Token);
        var backwardTask = ProxyDirectionAsync(
            destination, source, _backwardBuffer, sourceMessageType, linkedCts.Token);

        try
        {
            // Wait for either direction to complete
            await Task.WhenAny(forwardTask, backwardTask).ConfigureAwait(false);
        }
        finally
        {
            linkedCts.Cancel();

            // Ensure both directions stop
            try
            {
#pragma warning disable CA2016 // Forward the 'CancellationToken' parameter to methods
                await Task.WhenAll(forwardTask, backwardTask)
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
#pragma warning restore CA2016 // Forward the 'CancellationToken' parameter to methods
            }
            catch
            {
                // Best effort cleanup
            }

            await CloseWebSocketsAsync(source, destination).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates consumers to tap into the bidirectional streams.
    /// </summary>
    public (RawMediaPipeSubscription forward, RawMediaPipeSubscription backward) CreateTap()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return (_forwardBuffer.Subscribe(), _backwardBuffer.Subscribe());
    }

    private static async Task ProxyDirectionAsync(
        WebSocket source,
        WebSocket destination,
        RawMediaStreamChannel buffer,
        WebSocketMessageType destinationMessageType = WebSocketMessageType.Binary,
        CancellationToken cancellationToken = default)
    {
        // Start reader and writer tasks
        var writeTask = buffer.WriteAsync(source, cancellationToken);
        var readTask = ForwardToWebSocketAsync(buffer, destination, destinationMessageType, cancellationToken);

        await Task.WhenAll(writeTask.AsTask(), readTask).ConfigureAwait(false);
    }

    private static async Task ForwardToWebSocketAsync(
        RawMediaStreamChannel buffer,
        WebSocket destination,
        WebSocketMessageType messageType = WebSocketMessageType.Binary,
        CancellationToken cancellationToken = default)
    {
        await using var consumer = buffer.Subscribe();
        
        await foreach (var data in consumer.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (destination.State != WebSocketState.Open)
                break;
           
            await destination.SendAsync(
                data,
                messageType,
                endOfMessage: true,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CloseWebSocketsAsync(params WebSocket[] webSockets)
    {
        var closeTasks = webSockets
            .Where(ws => ws.State == WebSocketState.Open)
            .Select(ws => CloseWebSocketAsync(ws));

        await Task.WhenAll(closeTasks).ConfigureAwait(false);
    }

    private static async Task CloseWebSocketAsync(WebSocket webSocket)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await webSocket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "Proxy completed",
                cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Best effort
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        _disposalCts.Cancel();

        return new ValueTask(Task.WhenAll(
            _forwardBuffer.DisposeAsync().AsTask(),
            _backwardBuffer.DisposeAsync().AsTask()
        ).ContinueWith(_ => _disposalCts.Dispose()));
    }
}
