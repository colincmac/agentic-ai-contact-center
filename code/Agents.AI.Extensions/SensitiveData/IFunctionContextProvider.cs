namespace Agents.AI.Extensions.SensitiveData;

public interface IFunctionContextProvider
{
    ValueTask<string> SetAsync(object value, CancellationToken cancellationToken = default);

    ValueTask<T> GetAsync<T>(string reference, CancellationToken cancellationToken = default);
}
