using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Extensions.AI;
using Microsoft.Extensions.AI;
using static Extensions.AI.ExtensionsAIJsonUtilities;

namespace Agents.AI;

internal sealed class ConversationFunctionToolParametersSchema
{
    public string? Type { get; set; }
    public IDictionary<string, JsonElement>? Properties { get; set; }
    public IEnumerable<string>? Required { get; set; }
}

/// <summary>Source-generated JSON type information.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    WriteIndented = true)]
[JsonSerializable(typeof(ConversationFunctionToolParametersSchema))]
//[JsonSerializable(typeof(TranscriptTrackingAgentThread.ConversationSessionThreadState))]
internal sealed partial class AgentsJsonContext : JsonSerializerContext
{
    public static JsonSerializerOptions DefaultOptions { get; } = CreateDefaultOptions();

    private static JsonSerializerOptions CreateDefaultOptions()
    {

        JsonSerializerOptions options = new()
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(ExtensionsAIJsonUtilities.DefaultOptions.TypeInfoResolver),

            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        // Chain with all supported types from Microsoft.Extensions.AI.
        options.TypeInfoResolverChain.Add(AIJsonUtilities.DefaultOptions.TypeInfoResolver!);
        options.TypeInfoResolverChain.Add(ExtensionsAIJsonUtilities.DefaultOptions.TypeInfoResolver!);

        options.MakeReadOnly();
        return options;
    }
}
