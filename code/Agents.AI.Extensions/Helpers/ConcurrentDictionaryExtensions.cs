using System.Collections.Concurrent;

namespace Agents.AI.Extensions.Helpers;

public static class ConcurrentDictionaryExtensions
{
    public static async Task<TValue> GetOrAddAsync<TKey, TValue>(
        this ConcurrentDictionary<TKey, TValue> dictionary,
        TKey key,
        Func<TKey, Task<TValue>> valueFactory) where TKey : notnull
    {
        if (dictionary.TryGetValue(key, out var existingValue))
        {
            return existingValue;
        }

        var newValue = await valueFactory(key);
        return dictionary.GetOrAdd(key, newValue);
    }
}
