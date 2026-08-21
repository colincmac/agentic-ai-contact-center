using Agents.AI.Extensions.Helpers.Streaming;

namespace Agents.AI.Extensions.Tests.Helpers.Streaming;

public class RawMediaStreamChannelTests
{
    [Fact]
    public async Task WriteAsync_WithBackpressure_ThrottlesAppropriately()
    {
        // Arrange
        await using var channel = new RawMediaStreamChannel(capacity: 1024, chunkSize: 256, memoryPool: null);
        var data = new byte[512];

        // Act & Assert
        await channel.WriteAsync(data, TestContext.Current.CancellationToken);
        Assert.Equal(512, channel.BufferedBytes);
    }

    [Fact]
    public async Task MultipleConsumers_ReceiveAllData()
    {
        // Arrange
        await using var channel = new RawMediaStreamChannel(capacity: RawMediaStreamChannel.DEFAULT_CAPACITY, chunkSize: RawMediaStreamChannel.DEFAULT_CHUNK_SIZE, memoryPool: null);
        var consumer1 = channel.Subscribe();
        var consumer2 = channel.Subscribe();
        var testData = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        await channel.WriteAsync(testData, TestContext.Current.CancellationToken);

        // Allow distribution to complete
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Assert
        var result1 = await consumer1.ReadAsync(new byte[5], TestContext.Current.CancellationToken);
        var result2 = await consumer2.ReadAsync(new byte[5], TestContext.Current.CancellationToken);

        Assert.Equal(5, result1);
        Assert.Equal(5, result2);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(4096)]
    [InlineData(65536)]
    public async Task DifferentBufferSizes_HandleCorrectly(int size)
    {
        await using var channel = new RawMediaStreamChannel(capacity: size * 2, chunkSize: size, memoryPool: null);
        var data = new byte[size];

        await channel.WriteAsync(data, TestContext.Current.CancellationToken);

        Assert.Equal(size, channel.BufferedBytes);
    }

    [Fact]
    public async Task Subscribe_ReturnsNewSubscription()
    {
        // Arrange
        await using var channel = new RawMediaStreamChannel(capacity: RawMediaStreamChannel.DEFAULT_CAPACITY, chunkSize: RawMediaStreamChannel.DEFAULT_CHUNK_SIZE, memoryPool: null);

        // Act
        var subscription = channel.Subscribe();

        // Assert
        Assert.NotNull(subscription);
        Assert.Equal(1, channel.ConsumerCount);

        await subscription.DisposeAsync();
        Assert.Equal(0, channel.ConsumerCount);
    }

    [Fact]
    public async Task WriteAsync_EmptyData_DoesNotThrow()
    {
        // Arrange
        await using var channel = new RawMediaStreamChannel(capacity: RawMediaStreamChannel.DEFAULT_CAPACITY, chunkSize: RawMediaStreamChannel.DEFAULT_CHUNK_SIZE, memoryPool: null);
        var emptyData = Array.Empty<byte>();

        // Act & Assert - Should not throw
        await channel.WriteAsync(emptyData, TestContext.Current.CancellationToken);
        Assert.Equal(0, channel.BufferedBytes);
    }

    [Fact]
    public async Task Channel_HasDefaultCapacity()
    {
        // Arrange & Act
        await using var channel = new RawMediaStreamChannel(new RawMediaStreamChannelOptions());

        // Assert
        Assert.Equal(RawMediaStreamChannel.DEFAULT_CAPACITY, channel.Capacity);
        Assert.Equal(RawMediaStreamChannel.DEFAULT_CHUNK_SIZE, channel.ChunkSize);
    }

    [Fact]
    public async Task Channel_WithOptions_UsesOptions()
    {
        // Arrange
        var options = new RawMediaStreamChannelOptions
        {
            Capacity = 2048,
            ChunkSize = 512
        };

        // Act
        await using var channel = new RawMediaStreamChannel(options);

        // Assert
        Assert.Equal(2048, channel.Capacity);
        Assert.Equal(512, channel.ChunkSize);
    }

    [Fact]
    public async Task Subscription_Available_ReturnsCorrectCount()
    {
        // Arrange
        await using var channel = new RawMediaStreamChannel(capacity: RawMediaStreamChannel.DEFAULT_CAPACITY, chunkSize: RawMediaStreamChannel.DEFAULT_CHUNK_SIZE, memoryPool: null);
        var subscription = channel.Subscribe();

        // Assert - Initially no data available
        Assert.Equal(0, subscription.Available);

        await subscription.DisposeAsync();
    }
}
