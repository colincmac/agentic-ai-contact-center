using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Configuration;

/// <summary>
/// Normalizes and validates <see cref="AzureSpeechServiceOptions"/>:
/// <list type="bullet">
///   <item>Promotes the legacy single-endpoint shim (<c>Endpoint</c>/<c>Credential</c>) into
///         a single-entry <see cref="AzureSpeechServiceOptions.Endpoints"/> list when none
///         was supplied.</item>
///   <item>Fails validation if no endpoints remain after normalization.</item>
///   <item>Assigns a default <c>endpoint-{index}</c> name to entries that omit one so
///         telemetry tags are always populated.</item>
/// </list>
/// </summary>
internal sealed class AzureSpeechServiceOptionsValidator : IValidateOptions<AzureSpeechServiceOptions>
{
    public ValidateOptionsResult Validate(string? name, AzureSpeechServiceOptions options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail("AzureSpeechServiceOptions instance is required.");
        }

        // Promote legacy single-endpoint shim into the ordered list.
        if (options.Endpoints.Count == 0 && options.Endpoint is not null)
        {
            options.Endpoints.Add(new AzureSpeechEndpointOptions
            {
                Name = "primary",
                Endpoint = options.Endpoint,
                Credential = options.Credential,
            });
        }

        if (options.Endpoints.Count == 0)
        {
            return ValidateOptionsResult.Fail(
                "AzureSpeechServiceOptions requires at least one endpoint. Configure either 'Endpoint' or 'Endpoints'.");
        }

        var failures = new List<string>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < options.Endpoints.Count; i++)
        {
            var endpoint = options.Endpoints[i];
            if (endpoint is null)
            {
                failures.Add($"AzureSpeechServiceOptions.Endpoints[{i}] is null.");
                continue;
            }

            if (endpoint.Endpoint is null)
            {
                failures.Add($"AzureSpeechServiceOptions.Endpoints[{i}].Endpoint is required.");
            }

            if (string.IsNullOrWhiteSpace(endpoint.Name))
            {
                endpoint.Name = $"endpoint-{i}";
            }

            if (!seenNames.Add(endpoint.Name!))
            {
                failures.Add($"AzureSpeechServiceOptions.Endpoints[{i}].Name '{endpoint.Name}' is duplicated.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
