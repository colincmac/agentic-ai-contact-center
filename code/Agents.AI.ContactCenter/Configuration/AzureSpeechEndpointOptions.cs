using System.ComponentModel.DataAnnotations;
using Azure.Core;
using Azure.Identity;

namespace Agents.AI.ContactCenter.Configuration;

/// <summary>
/// Configuration for a single Azure Speech service endpoint that participates
/// in the failover-aware resilient speech pipeline.
/// </summary>
/// <remarks>
/// Multiple endpoints (typically one per Azure region) are listed in order of
/// preference on <see cref="AzureSpeechServiceOptions.Endpoints"/>. The resilient
/// recognizer/synthesizer try them sequentially when transient failures are
/// observed (Timeout &#x2192; Retry &#x2192; Circuit Breaker &#x2192; Fallback).
/// </remarks>
public sealed class AzureSpeechEndpointOptions
{
    /// <summary>
    /// Friendly identifier used in logs, metrics, and activity tags. When not
    /// specified a name like "endpoint-{index}" is assigned during validation.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>Azure Speech service endpoint URI.</summary>
    [Required]
    public required Uri Endpoint { get; set; }

    /// <summary>
    /// Optional Azure region identifier (e.g. "eastus"). Used only for telemetry
    /// tagging; the endpoint URI is what governs traffic routing.
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// Credential used to authenticate against this endpoint. Defaults to
    /// <see cref="DefaultAzureCredential"/> when omitted in configuration.
    /// </summary>
    public TokenCredential Credential { get; set; } = new DefaultAzureCredential();
}
