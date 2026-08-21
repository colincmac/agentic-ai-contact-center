using System.Buffers;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Shared.Diagnostics;

namespace Agents.AI.ContactCenter.Media.Audio;

/// <summary>
/// An <see cref="IAudioConsumer"/> that buffers incoming audio frames into
/// configurable-sized chunks and persists each chunk as a blob in Azure Blob
/// Storage.
/// <para>
/// Each instance is scoped to a single recording session identified by a
/// <see cref="Guid"/> supplied at construction time. Blobs are named
/// <c>{options.BlobPrefix}{sessionId}/{chunkIndex:D6}.pcm</c>.
/// </para>
/// <para>
/// Call <see cref="DisposeAsync"/> (or <see cref="Dispose"/>) when the session
/// ends to flush any remaining buffered bytes as a final, smaller chunk.
/// </para>
/// </summary>
public sealed class BlobAudioConsumer : IAudioConsumer, IAsyncDisposable, IDisposable
{
    private readonly BlobContainerClient _containerClient;
    private readonly BlobAudioConsumerOptions _options;
    private readonly ILogger<BlobAudioConsumer> _logger;
    private readonly string _sessionId;

    private byte[] _buffer;
    private int _bufferPosition;
    private int _chunkIndex;
    private bool _disposed;
    private readonly Lock _lock = new();

    public BlobAudioConsumer(
        BlobServiceClient blobServiceClient,
        IOptions<BlobAudioConsumerOptions> options,
        ILogger<BlobAudioConsumer> logger,
        Guid sessionId)
    {
        Throw.IfNull(blobServiceClient);
        Throw.IfNull(options);
        Throw.IfNull(logger);

        _options = options.Value;
        _logger = logger;
        _sessionId = sessionId.ToString("N");
        _containerClient = blobServiceClient.GetBlobContainerClient(_options.ContainerName);
        _buffer = ArrayPool<byte>.Shared.Rent(_options.ChunkSizeBytes);
    }

    /// <inheritdoc />
    public async Task SendAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int dataLength = audioData.Length;
        int offset = 0;

        while (offset < dataLength)
        {
            bool shouldFlush = false;
            int chunkToFlush = -1;
            byte[]? bufferToFlush = null;
            int bytesToFlush = 0;

            lock (_lock)
            {
                int remaining = _options.ChunkSizeBytes - _bufferPosition;
                int toCopy = Math.Min(remaining, dataLength - offset);

                audioData.Span.Slice(offset, toCopy).CopyTo(_buffer.AsSpan(_bufferPosition));
                _bufferPosition += toCopy;
                offset += toCopy;

                if (_bufferPosition >= _options.ChunkSizeBytes)
                {
                    shouldFlush = true;
                    chunkToFlush = _chunkIndex++;
                    bufferToFlush = _buffer;
                    bytesToFlush = _bufferPosition;

                    _buffer = ArrayPool<byte>.Shared.Rent(_options.ChunkSizeBytes);
                    _bufferPosition = 0;
                }
            }

            if (shouldFlush)
            {
                await UploadChunkAsync(bufferToFlush!, bytesToFlush, chunkToFlush, cancellationToken).ConfigureAwait(false);
                ArrayPool<byte>.Shared.Return(bufferToFlush!);
            }
        }
    }

    /// <summary>
    /// Flushes any remaining buffered audio and releases resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        byte[] bufferToFlush;
        int bytesToFlush;
        int chunkToFlush;

        lock (_lock)
        {
            bufferToFlush = _buffer;
            bytesToFlush = _bufferPosition;
            chunkToFlush = _chunkIndex;
            _buffer = [];
            _bufferPosition = 0;
        }

        if (bytesToFlush > 0)
        {
            try
            {
                await UploadChunkAsync(bufferToFlush, bytesToFlush, chunkToFlush, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to flush final audio chunk {ChunkIndex} for session {SessionId}.", chunkToFlush, _sessionId);
            }
        }

        ArrayPool<byte>.Shared.Return(bufferToFlush);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_lock)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
            _bufferPosition = 0;
        }
    }

    private async Task UploadChunkAsync(byte[] buffer, int length, int chunkIndex, CancellationToken cancellationToken)
    {
        string blobName = string.IsNullOrEmpty(_options.BlobPrefix)
            ? $"{_sessionId}/{chunkIndex:D6}.pcm"
            : $"{_options.BlobPrefix}{_sessionId}/{chunkIndex:D6}.pcm";

        var blobClient = _containerClient.GetBlobClient(blobName);
        using var stream = new MemoryStream(buffer, 0, length, writable: false);

        await blobClient.UploadAsync(stream, overwrite: true, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Uploaded audio chunk {BlobName} ({Bytes} bytes) for session {SessionId}.", blobName, length, _sessionId);
    }
}
