namespace Extensions.AI.RealtimeVoice.Configuration;

/// <summary>Initializes a new instance of the <see cref="LiveConversationClientMetadata"/> class.</summary>
/// <param name="providerName">
/// The name of the  provider, if applicable. Where possible, this should map to the
/// appropriate name defined in the OpenTelemetry Semantic Conventions for Generative AI systems.
/// </param>
/// <param name="providerUri">The URL for accessing the  provider, if applicable.</param>
/// <param name="defaultModelId">The ID of the used by default, if applicable.</param>
public class LiveConversationClientMetadata(string modelId, string? providerName = null, Uri? providerUri = null)
{

    /// <summary>Gets the name of the provider.</summary>
    /// <remarks>
    /// Where possible, this maps to the appropriate name defined in the
    /// OpenTelemetry Semantic Conventions for Generative AI systems.
    /// </remarks>
    public string? ProviderName { get; } = providerName;

    /// <summary>Gets the URL for accessing the provider.</summary>
    public Uri? ProviderUri { get; } = providerUri;

    public string ModelId { get; } = modelId;

}
