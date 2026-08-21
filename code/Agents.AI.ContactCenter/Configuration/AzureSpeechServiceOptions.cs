using Agents.AI.ContactCenter.Media.Audio.Resilience;
using Azure.Core;
using Azure.Identity;
using Microsoft.CognitiveServices.Speech;

namespace Agents.AI.ContactCenter.Configuration;

/// <summary>
/// Configuration options for Azure Speech Service.
/// </summary>
/// <remarks>
/// Endpoints can be supplied either via the legacy single-endpoint shim
/// (<see cref="Endpoint"/> + <see cref="Credential"/>) or via the ordered
/// <see cref="Endpoints"/> list which enables multi-region failover when the
/// resilient speech decorators are wired up by
/// <c>AzureSpeechServiceCollectionExtensions.AddAzureSpeech</c>.
/// </remarks>
public sealed class AzureSpeechServiceOptions
{
    public const string SectionName = "AzureSpeech";

    /// <summary>
    /// Primary Azure Speech endpoint. Optional when <see cref="Endpoints"/> is
    /// populated. When supplied alongside an empty <see cref="Endpoints"/>
    /// list the validator promotes it into a single-entry endpoint list so the
    /// resilient pipeline still applies (Timeout/Retry/Circuit Breaker without
    /// Fallback).
    /// </summary>
    public Uri? Endpoint { get; set; }

    /// <summary>
    /// Ordered list of Speech endpoints used by the resilient
    /// <see cref="Media.Audio.ISpeechRecognizer"/> /
    /// <see cref="Media.Audio.ISpeechSynthesizer"/> decorators. The first entry
    /// is the primary; subsequent entries are tried in order during fallback.
    /// </summary>
    public IList<AzureSpeechEndpointOptions> Endpoints { get; init; } = [];

    /// <summary>Speech recognition locale (default: en-US).</summary>
    public string RecognitionLocale { get; set; } = "en-US";

    /// <summary>Speech synthesis voice name (default: en-US-Ava:DragonHDLatestNeural).</summary>
    public string SynthesisVoiceName { get; set; } = "en-US-Ava:DragonHDLatestNeural";

    /// <summary>Speech synthesis locale (default: en-US).</summary>
    public string SynthesisLocale { get; set; } = "en-US";

    /// <summary>Speech synthesis voice gender (default: Female).</summary>
    public string SynthesisGender { get; set; } = "Female";

    /// <summary>Speech synthesis output format.</summary>
    public SpeechSynthesisOutputFormat OutputFormat { get; set; } = SpeechSynthesisOutputFormat.Raw24Khz16BitMonoPcm;

    /// <summary>Number of pre-warmed recognizer/synthesizer instances (default: 2).</summary>
    public int Concurrency { get; set; } = 2;

    /// <summary>Maximum number of pooled instances to retain (default: 100).</summary>
    public int MaximumRetainedCapacity { get; set; } = 100;

    /// <summary>
    /// Credential used when only the legacy single-endpoint shim (<see cref="Endpoint"/>)
    /// is specified. Ignored when <see cref="Endpoints"/> is populated (each entry
    /// carries its own credential).
    /// </summary>
    public TokenCredential Credential { get; set; } = new DefaultAzureCredential();

    /// <summary>
    /// Resilience configuration shared by the recognizer and synthesizer
    /// decorators (per-attempt timeout, retry/backoff, circuit-breaker
    /// thresholds, fallback toggle).
    /// </summary>
    public SpeechResilienceOptions Resilience { get; set; } = new();
}

