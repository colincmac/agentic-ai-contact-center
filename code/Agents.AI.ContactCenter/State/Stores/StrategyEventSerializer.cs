using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agents.AI.ContactCenter.Calling;

namespace Agents.AI.ContactCenter.State.Stores;

/// <summary>
/// Serializes <see cref="StrategyEvent"/> instances for the distributed event logs. The concrete
/// record name is stored alongside the JSON payload; on read the name is resolved back to the nested
/// record type via reflection over <see cref="StrategyEvent"/>'s declared subtypes.
/// </summary>
/// <remarks>
/// <see cref="StrategyEvent.Faulted"/> carries an <see cref="Exception"/>, which is not round-trippable;
/// a converter writes it as its message string and reads it back as <see langword="null"/>. The event
/// log is an audit/replay aid, so this lossy edge on a single field is acceptable.
/// </remarks>
internal static class StrategyEventSerializer
{
    private static readonly FrozenDictionary<string, Type> typesByName =
        typeof(StrategyEvent)
            .GetNestedTypes()
            .Where(t => typeof(StrategyEvent).IsAssignableFrom(t) && !t.IsAbstract)
            .ToFrozenDictionary(t => t.Name, t => t);

    private static readonly JsonSerializerOptions jsonOptions = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerOptions.Default)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new ExceptionConverter());
        return options;
    }

    public static string TypeName(StrategyEvent strategyEvent) => strategyEvent.GetType().Name;

    public static string Serialize(StrategyEvent strategyEvent)
        => JsonSerializer.Serialize(strategyEvent, strategyEvent.GetType(), jsonOptions);

    public static StrategyEvent? Deserialize(string typeName, string json)
    {
        if (!typesByName.TryGetValue(typeName, out var type))
        {
            return null;
        }

        return JsonSerializer.Deserialize(json, type, jsonOptions) as StrategyEvent;
    }

    private sealed class ExceptionConverter : JsonConverter<Exception>
    {
        public override Exception? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            reader.Skip();
            return null;
        }

        public override void Write(Utf8JsonWriter writer, Exception value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Message);
    }
}
