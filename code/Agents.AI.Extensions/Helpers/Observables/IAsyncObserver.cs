namespace Agents.AI.Extensions.Helpers.Observables;


public interface IAsyncObservable<out T>
{
    IAsyncDisposable SubscribeAsync(IAsyncDelegateObserver<T> observer);
}

public interface IAsyncDelegateObserver<in T>
{
    Task OnNextAsync(T item);

    Task OnCompletedAsync() => Task.CompletedTask;

    Task OnErrorAsync(Exception ex);
}
public class AsyncDelegateObserver<T> : IAsyncDelegateObserver<T>
{
    private readonly Func<T, Task> _onNextAsync;
    private readonly Func<Exception, Task>? _onErrorAsync;
    private readonly Func<Task>? _onCompletedAsync;
    public AsyncDelegateObserver(Func<T, Task> onNextAsync, Func<Exception, Task>? onErrorAsync = null, Func<Task>? onCompletedAsync = null)
    {
        _onNextAsync = onNextAsync;
        _onErrorAsync = onErrorAsync;
        _onCompletedAsync = onCompletedAsync;
    }
    public Task OnNextAsync(T value) => _onNextAsync(value);

    public Task OnErrorAsync(Exception error)
    {
        if(_onErrorAsync != null) { return _onErrorAsync(error); }
        return Task.CompletedTask;
    }

    public Task OnCompletedAsync()
    {
        if (_onCompletedAsync != null) { return _onCompletedAsync(); }
        return Task.CompletedTask;
    }
}
