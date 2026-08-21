using Microsoft.Shared.Diagnostics;

namespace Agents.AI.Extensions.Helpers.Observables;


public static class ObservableExtensions
{

    public static IObserver<T> CreateDelegateObserver<T>(Action<T> onNext, Action<Exception>? onError = null, Action? onCompleted = null)
    {
        Throw.IfNull(onNext);

        return new DelegateObserver<T>(onNext, onError, onCompleted);
    }

    public static IDisposable Subscribe<T>(this IObservable<T> observable, Action<T> onNext, Action<Exception>? onError = null, Action? onCompleted = null)
    {
        Throw.IfNull(observable);
        Throw.IfNull(onNext);
        var observer = CreateDelegateObserver(onNext, onError, onCompleted);
        return observable.Subscribe(observer);
    }

    public static IAsyncDelegateObserver<T> CreateAsyncDelegateObserver<T>(Func<T, Task> onNext, Func<Exception, Task>? onError = null, Func<Task>? onCompleted = null)
    {
        Throw.IfNull(onNext);

        return new AsyncDelegateObserver<T>(onNext, onError, onCompleted);
    }

    public static IAsyncDisposable SubscribeAsync<T>(this IAsyncObservable<T> observable, Func<T, Task> onNext, Func<Exception, Task>? onError = null, Func<Task>? onCompleted = null)
    {
        Throw.IfNull(observable);
        Throw.IfNull(onNext);
        var observer = CreateAsyncDelegateObserver(onNext, onError, onCompleted);
        return observable.SubscribeAsync(observer);
    }
}
