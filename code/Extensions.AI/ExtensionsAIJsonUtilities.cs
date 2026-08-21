using System;
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Extensions.AI.Contents;
using Microsoft.Extensions.AI;

namespace Extensions.AI;

public static partial class ExtensionsAIJsonUtilities
{
    public static JsonSerializerOptions DefaultOptions { get; } = CreateDefaultOptions();

    private static JsonSerializerOptions CreateDefaultOptions()
    {

        JsonSerializerOptions options = new()
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(AIJsonUtilities.DefaultOptions.TypeInfoResolver, JsonContext.Default),

            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        options.AddAIContentType<AudioTranscriptionContent>(typeDiscriminatorId: "audioTranscriptionContent");
        options.AddAIContentType<RealtimeVadContent>(typeDiscriminatorId: "realtimeVadContent");
        options.AddAIContentType<DtmfToneContent>(typeDiscriminatorId: "dtmfToneContent");
        options.AddAIContentType<RealtimeResponseFinishedContent>(typeDiscriminatorId: "realtimeResponseFinishedContent");
        options.AddAIContentType<AudioTruncatedContent>(typeDiscriminatorId: "audioTruncatedContent");



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
    [JsonSerializable(typeof(AudioTranscriptionContent))]
    [JsonSerializable(typeof(RealtimeVadContent))]
    [JsonSerializable(typeof(DtmfToneContent))]
    [JsonSerializable(typeof(RealtimeResponseStartContent))]
    [JsonSerializable(typeof(RealtimeResponseFinishedContent))]
    [JsonSerializable(typeof(AudioTruncatedContent))]
    public partial class JsonContext : JsonSerializerContext;
}
