namespace Agents.AI.ContactCenter.Media.Audio;

/// <summary>
/// Configuration for <see cref="BlobAudioConsumer"/>.
/// </summary>
public sealed class BlobAudioConsumerOptions
{
    /// <summary>
    /// The name of the blob container to write audio chunks to.
    /// Defaults to <c>"audio-recordings"</c>.
    /// </summary>
    public string ContainerName { get; set; } = "audio-recordings";

    /// <summary>
    /// Number of bytes to buffer before flushing a chunk to blob storage.
    /// Defaults to 64 KB.
    /// </summary>
    public int ChunkSizeBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// Optional prefix applied to every blob name (e.g. <c>"sessions/"</c>).
    /// When set, blobs are stored as <c>{BlobPrefix}{sessionId}/{chunkIndex}.pcm</c>.
    /// </summary>
    public string? BlobPrefix { get; set; }
}
