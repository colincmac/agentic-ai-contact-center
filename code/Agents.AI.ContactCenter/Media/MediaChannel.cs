using Agents.AI.Extensions.Helpers.Streaming;

namespace Agents.AI.ContactCenter.Media;

public sealed class MediaChannel : IAsyncDisposable
{
    public const int DEFAULT_CAPACITY = 1024 * 1024;

    public const int DEFAULT_CHUNK_SIZE = 4096;

    private readonly RawMediaStreamChannel _inboundMediaStreamChannel;
    private readonly RawMediaStreamChannel _outboundMediaStreamChannel;

    public MediaChannel(string connectionId, string mediaStreamId, string mediaType, RawMediaStreamChannelOptions? inboundStreamOptions = null, RawMediaStreamChannelOptions? outboundStreamOptions = null)
    {
        ConnectionId = connectionId;
        MediaStreamId = mediaStreamId;
        MediaType = mediaType;
        var inboundOptions = inboundStreamOptions ?? new RawMediaStreamChannelOptions()
        {
            Capacity = DEFAULT_CAPACITY,
            ChunkSize = DEFAULT_CHUNK_SIZE
        };
        _inboundMediaStreamChannel = new RawMediaStreamChannel(inboundOptions);

        var outboundOptions = outboundStreamOptions ?? new RawMediaStreamChannelOptions()
        {
            Capacity = DEFAULT_CAPACITY,
            ChunkSize = DEFAULT_CHUNK_SIZE
        };
        _outboundMediaStreamChannel = new RawMediaStreamChannel(outboundOptions);
    }
    
    public string ConnectionId { get; }
    public string MediaStreamId { get; }
    public string MediaType { get; }

    public async ValueTask DisposeAsync()
    {
       await _inboundMediaStreamChannel.DisposeAsync();
       await _outboundMediaStreamChannel.DisposeAsync();
    }
}
