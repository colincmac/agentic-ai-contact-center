using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Agents.AI.Extensions.SensitiveData;

public class InMemoryFunctionContextProvider(JsonSerializerOptions? jsonSerializerOptions = null) : IFunctionContextProvider
{
    private readonly ConcurrentDictionary<string, string> _store = new();
    private long _counter;
    public JsonSerializerOptions JsonSerializerOptions { get; } = jsonSerializerOptions ?? AIJsonUtilities.DefaultOptions;
    public ValueTask<string> SetAsync(object value, CancellationToken cancellationToken = default)
    {
        var token = $"ref_{Interlocked.Increment(ref _counter)}_{Guid.NewGuid():N}";
        _store[token] = JsonSerializer.Serialize(value, JsonSerializerOptions);
        return ValueTask.FromResult(token);
    }

    public ValueTask<T> GetAsync<T>(string reference, CancellationToken cancellationToken = default)
    {

        if (!_store.TryGetValue(reference, out var value) || string.IsNullOrEmpty(value))
        {
            throw new KeyNotFoundException($"Reference with ID {reference} not found.");
        }
        var deserialized = JsonSerializer.Deserialize<T>(value, JsonSerializerOptions) ?? throw new JsonException($"Stored value for reference '{reference}' could not be deserialized to type '{typeof(T).FullName}'.");
        return ValueTask.FromResult(deserialized);
    }

}
