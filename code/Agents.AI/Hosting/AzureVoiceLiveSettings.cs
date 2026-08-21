using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.Common;
using System.Text;
using Azure.Core;

namespace Agents.AI.Hosting;

public class AzureVoiceLiveSettings()
{
    private const string ConnectionStringEndpoint = "Endpoint";
    private const string ConnectionStringKey = "Key";
    private const string ConnectionStringModel = "Model";

    /// <summary>
    /// Gets or sets a <see cref="Uri"/> referencing the Azure OpenAI endpoint.
    /// This is likely to be similar to "https://{account_name}.openai.azure.com".
    /// </summary>
    /// <remarks>
    /// Leave empty and provide a <see cref="Key"/> value to use a non-Azure OpenAI inference endpoint.
    /// Used along with <see cref="Credential"/> or <see cref="Key"/> to establish the connection.
    /// </remarks>
    public Uri? Endpoint { get; set; }

    /// <summary>
    /// Gets or sets the credential used to authenticate to the Azure OpenAI resource.
    /// </summary>
    public TokenCredential? Credential { get; set; }

    /// <summary>
    /// Gets or sets the key to use to authenticate to the Azure OpenAI endpoint.
    /// </summary>
    /// <remarks>
    /// When defined it will use an <see cref="ApiKeyCredential"/> instance instead of <see cref="Credential"/>.
    /// </remarks>
    public string? Key { get; set; }

    public string? Model { get; set; }

    /// <summary>
    /// Gets or sets a boolean value that indicates whether the OpenTelemetry metrics are enabled or not.
    /// </summary>
    /// <remarks>
    /// Telemetry is recorded by Microsoft.Extensions.AI.
    /// </remarks>
    /// <value>
    /// The default value is <see langword="false"/>.
    /// </value>
    public bool DisableMetrics { get; set; }

    /// <summary>
    /// Gets or sets a boolean value that indicates whether the OpenTelemetry tracing is disabled or not.
    /// </summary>
    /// <remarks>
    /// Telemetry is recorded by Microsoft.Extensions.AI.
    /// </remarks>
    /// <value>
    /// The default value is <see langword="false"/>.
    /// </value>
    public bool DisableTracing { get; set; } = false;

    /// <summary>
    /// Gets or sets a boolean value indicating whether potentially sensitive information should be included in telemetry.
    /// </summary>
    /// <value>
    /// <see langword="true"/> if potentially sensitive information should be included in telemetry;
    /// <see langword="false"/> if telemetry shouldn't include raw inputs and outputs.
    /// The default value is <see langword="false"/>, unless the <c>OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT</c>
    /// environment variable is set to "true" (case-insensitive).
    /// </value>
    /// <remarks>
    /// By default, telemetry includes metadata, such as token counts, but not raw inputs
    /// and outputs, such as message content, function call arguments, and function call results.
    /// The default value can be overridden by setting the <c>OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT</c>
    /// environment variable to "true". Explicitly setting this property will override the environment variable.
    /// </remarks>
    public bool EnableSensitiveTelemetryData { get; set; } = false;

    public string? APIVersion { get; set; } = "2026-01-01-preview";

    public void ParseConnectionString(string? connectionString)
    {
        if (Uri.TryCreate(connectionString, UriKind.Absolute, out var uri))
        {
            Endpoint = uri;
        }
        else
        {
            var connectionBuilder = new DbConnectionStringBuilder
            {
                ConnectionString = connectionString
            };

            if (connectionBuilder.ContainsKey(ConnectionStringEndpoint) && Uri.TryCreate(connectionBuilder[ConnectionStringEndpoint].ToString(), UriKind.Absolute, out var serviceUri))
            {
                Endpoint = serviceUri;
            }

            if (connectionBuilder.ContainsKey(ConnectionStringKey))
            {
                Key = connectionBuilder[ConnectionStringKey].ToString();
            }

            if (connectionBuilder.ContainsKey(ConnectionStringModel))
            {
                Model = connectionBuilder[ConnectionStringModel].ToString();
            }
        }
    }
}
