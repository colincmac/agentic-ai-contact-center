//using System;
//using System.Collections.Generic;
//using System.IO.Pipelines;
//using System.Text;
//using System.Threading.Channels;
//using Newtonsoft.Json;

//namespace Agents.AI.Extensions.Helpers.Streaming;

//public static class AsyncEnumerableExtensions
//{
//    public static IAsyncEnumerator<object?> MakeAsyncEnumeratorFromChannel<T>(ChannelReader<T> channel, CancellationToken cancellationToken = default)
//    {
//        return new ChannelAsyncEnumerator<T>(channel, cancellationToken);
//    }
//    public static async Task<ChannelReader<TResult>> StreamAsChannelCoreAsync<TResult>(this Stream stream, CancellationToken cancellationToken = default)
//    {

//        var inputChannel = await stream.StreamAsChannelCoreAsync(methodName, typeof(TResult), args, cancellationToken).ConfigureAwait(false);
//        var outputChannel = Channel.CreateUnbounded<TResult>();

//        // Intentionally avoid passing the CancellationToken to RunChannel. The token is only meant to cancel the intial setup, not the enumeration.
//        _ = RunChannel(inputChannel, outputChannel);

//        return outputChannel.Reader;
//    }
//    // Function to provide a way to run async code as fire-and-forget
//    // The output channel is how we signal completion to the caller.
//    private static async Task RunChannel<TResult>(ChannelReader<object?> inputChannel, Channel<TResult> outputChannel)
//    {
//        try
//        {
//            while (await inputChannel.WaitToReadAsync().ConfigureAwait(false))
//            {
//                while (inputChannel.TryRead(out var item))
//                {
//                    while (!outputChannel.Writer.TryWrite((TResult)item!))
//                    {
//                        if (!await outputChannel.Writer.WaitToWriteAsync().ConfigureAwait(false))
//                        {
//                            // Failed to write to the output channel because it was closed. Nothing really we can do but abort here.
//                            return;
//                        }
//                    }
//                }
//            }
//        }
//        catch (Exception ex)
//        {
//            outputChannel.Writer.TryComplete(ex);
//        }
//        finally
//        {
//            // This will safely no-op if the catch block above ran.
//            outputChannel.Writer.TryComplete();

//            // Needed to avoid UnobservedTaskExceptions
//            _ = inputChannel.Completion.Exception;
//        }
//    }

//    private sealed class ChannelAsyncEnumerator<T> : IAsyncEnumerator<object?>
//    {
//        private readonly ChannelReader<T> _channel;
//        private readonly CancellationToken _cancellationToken;
//        public ChannelAsyncEnumerator(ChannelReader<T> channel, CancellationToken cancellationToken)
//        {
//            _channel = channel;
//            _cancellationToken = cancellationToken;
//        }

//        public object? Current { get; private set; }

//        public ValueTask<bool> MoveNextAsync()
//        {
//            if (_channel.TryRead(out var item))
//            {
//                Current = item;
//                return new ValueTask<bool>(true);
//            }

//            return MoveNextAsyncAwaited();
//        }

//        private async ValueTask<bool> MoveNextAsyncAwaited()
//        {
//            while (await _channel.WaitToReadAsync(_cancellationToken).ConfigureAwait(false))
//            {
//                if (_channel.TryRead(out var item))
//                {
//                    Current = item;
//                    return true;
//                }
//            }
//            return false;
//        }

//        public ValueTask DisposeAsync() => default;
//    }
//}
