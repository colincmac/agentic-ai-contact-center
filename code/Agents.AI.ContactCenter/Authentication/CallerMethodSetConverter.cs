using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agents.AI.ContactCenter.Authentication;

public sealed class CallerMethodSetConverter : JsonConverter<IReadOnlySet<string>>
{
    public override IReadOnlySet<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => (JsonSerializer.Deserialize<string[]>(ref reader, options)
            ?? throw new JsonException("Caller authentication methods must be an array."))
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public override void Write(Utf8JsonWriter writer, IReadOnlySet<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var method in value) { writer.WriteStringValue(method); }
        writer.WriteEndArray();
    }
}
