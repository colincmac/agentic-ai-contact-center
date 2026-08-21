namespace Agents.AI.ContactCenter.Utilities;

public interface IAsyncInitialized
{
    public Task InitializeAsync();
}

public abstract class AsyncInitialized : IAsyncInitialized
{
    private readonly Lazy<Task> _initializationTask;

    protected AsyncInitialized()
    {
        _initializationTask = new Lazy<Task>(InitializeAsyncCore);
    }

    protected abstract Task InitializeAsyncCore();

    public Task InitializeAsync() => _initializationTask.Value;
}
