using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agents.AI.ContactCenter.State;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for serializing call-state slices into and out of the
/// snapshot store. Enums are written as strings so persisted snapshots stay stable across reorderings
/// of the underlying enum, and nulls are omitted to keep stored documents compact.
/// </summary>
internal static class CallStateJson
{
    public static readonly JsonSerializerOptions Default = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerOptions.Default)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
