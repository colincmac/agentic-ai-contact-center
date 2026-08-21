using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Extensions.AI;
using Microsoft.Agents.AI;

namespace Agents.AI.Extensions.Helpers.Streaming;


public static partial class AgentsAIJsonUtilities
{
    public static JsonSerializerOptions DefaultOptions { get; } = CreateDefaultOptions();

    private static JsonSerializerOptions CreateDefaultOptions()
    {
        JsonSerializerOptions options = new()
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(ExtensionsAIJsonUtilities.DefaultOptions.TypeInfoResolver, AgentAbstractionsJsonUtilities.DefaultOptions.TypeInfoResolver, JsonContext.Default),
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        if (JsonSerializer.IsReflectionEnabledByDefault)
        {
            // If reflection-based serialization is enabled by default, use it as a fallback for all other types.
            // Also turn on string-based enum serialization for all unknown enums.
            options.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());
            options.Converters.Add(new JsonStringEnumConverter());
        }
        options.MakeReadOnly();

        return options;
    }
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
        UseStringEnumConverter = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true)]
    [JsonSerializable(typeof(MessageUpdate))]
    [JsonSerializable(typeof(IReadOnlyList<MessageUpdate>))]
    public partial class JsonContext : JsonSerializerContext;
}


