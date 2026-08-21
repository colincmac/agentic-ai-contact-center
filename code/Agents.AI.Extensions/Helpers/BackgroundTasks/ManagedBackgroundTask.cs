//using System.Diagnostics;
//using Microsoft.Extensions.Logging;

//namespace Agents.AI.Extensions.Helpers.BackgroundTasks;

//internal sealed class ManagedBackgroundTask : IAsyncDisposable
//{
//    private readonly Func<CancellationToken, Task> _run;
//    private readonly string _name;
//    private readonly ILogger _logger;
//    private readonly CancellationTokenSource _cts = new();
//    private readonly SemaphoreSlim _startSemaphore = new(1, 1);
//    private readonly TaskCompletionSource _disposalTcs = new();
//    private Task? _task;
//    private bool _disposed;

//    public ManagedBackgroundTask(string name, Func<CancellationToken, Task> run, ILogger logger)
//    {
//        ArgumentNullException.ThrowIfNull(run);
//        ArgumentNullException.ThrowIfNull(logger);
//        ArgumentException.ThrowIfNullOrEmpty(name);

//        _name = name;
//        _run = run;
//        _logger = logger;
//    }

//    public bool IsRunning => _task?.IsCompleted == false;

//    public async Task StartAsync(CancellationToken cancellationToken = default)
//    {
//        ObjectDisposedException.ThrowIf(_disposed, nameof(ManagedBackgroundTask));

//        await _startSemaphore.WaitAsync(cancellationToken);
//        try
//        {
//            if (_task != null)
//                return;

//            _task = RunInternalAsync();
//        }
//        finally
//        {
//            _startSemaphore.Release();
//        }
//    }

//    private async Task RunInternalAsync()
//    {
//        using var activity = Activity.Current ?? Activity.StartActivity($"BackgroundTask.{_name}");
//        var sw = Stopwatch.StartNew();

//        try
//        {
//            _logger.LogDebug("Background task {TaskName} started.", _name);
//            await _run(_cts.Token).ConfigureAwait(false);
//            _logger.LogDebug("Background task {TaskName} completed successfully after {ElapsedMs}ms.",
//                _name, sw.ElapsedMilliseconds);
//        }
//        catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested)
//        {
//            _logger.LogDebug("Background task {TaskName} was cancelled after {ElapsedMs}ms.",
//                _name, sw.ElapsedMilliseconds);
//        }
//        catch (Exception ex)
//        {
//            _logger.LogError(ex, "Background task {TaskName} failed after {ElapsedMs}ms.",
//                _name, sw.ElapsedMilliseconds);
//            throw; // Consider if you want to rethrow
//        }
//        finally
//        {
//            _disposalTcs.TrySetResult();
//        }
//    }

//    public async ValueTask DisposeAsync()
//    {
//        if (_disposed)
//            return;

//        _disposed = true;

//        _cts.Cancel();

//        if (_task != null)
//        {
//            try
//            {
//                await _task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
//            }
//            catch (TimeoutException)
//            {
//                _logger.LogWarning("Background task {TaskName} did not complete within timeout.", _name);
//            }
//        }

//        _cts.Dispose();
//        _startSemaphore.Dispose();
//    }
//}
