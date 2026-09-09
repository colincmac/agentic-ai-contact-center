using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.Media.Transcription;
using Microsoft.CognitiveServices.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Azure;

/// <summary>
/// Composite Azure Speech service providing both speech recognition (STT) and synthesis (TTS).
/// Implements both <see cref="ISpeechRecognizer"/> and <see cref="ISpeechSynthesizer"/> for
/// direct use in Contact Center strategies.
/// </summary>
/// <remarks>
/// This service wraps <see cref="AzureSpeechRecognizer"/> and <see cref="AzureSpeechSynthesizer"/>
/// with shared configuration and lifecycle management. Use this when you need both STT and TTS
/// capabilities in the same application context.
/// 
/// The recognizer implementation is session-scoped: each instance manages one recognition session.
/// The synthesizer implementation is stateless and thread-safe: can be reused across multiple calls.
/// </remarks>
public sealed class AzureSpeechService : ISpeechRecognizer, ISpeechSynthesizer
{
    private readonly AzureSpeechServiceOptions _options;
    private readonly AzureSpeechEndpointOptions _endpoint;
    private readonly ILogger<AzureSpeechService> _logger;
    private readonly SpeechConfig _speechConfig;

    // One lazily constructed synthesizer pool shared by concurrent calls.
    private AzureSpeechSynthesizer? _synthesizer;

    // Recognizer backing (created lazily per instance)
    private AzureSpeechRecognizer? _recognizer;

    private bool _isDisposed;
    private readonly Lock _resourceGate = new();

    public AzureSpeechService(
        IOptions<AzureSpeechServiceOptions> options,
        ILogger<AzureSpeechService>? logger = null)
        : this(options.Value, endpoint: null, logger)
    {
    }

    public AzureSpeechService(
        AzureSpeechServiceOptions options,
        ILogger<AzureSpeechService>? logger = null)
        : this(options, endpoint: null, logger)
    {
    }

    /// <summary>
    /// Creates an <see cref="AzureSpeechService"/> bound to a single endpoint
    /// from the configured <see cref="AzureSpeechServiceOptions.Endpoints"/> list.
    /// </summary>
    /// <remarks>
    /// When <paramref name="endpoint"/> is <c>null</c> the service falls back to
    /// the legacy single-endpoint shim (<see cref="AzureSpeechServiceOptions.Endpoint"/>
    /// + <see cref="AzureSpeechServiceOptions.Credential"/>) so existing callers
    /// continue to work unchanged.
    /// </remarks>
    public AzureSpeechService(
        AzureSpeechServiceOptions options,
        AzureSpeechEndpointOptions? endpoint,
        ILogger<AzureSpeechService>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<AzureSpeechService>.Instance;

        _endpoint = endpoint ?? ResolveLegacyEndpoint(options);

        // Create shared SpeechConfig
        _speechConfig = SpeechConfig.FromEndpoint(_endpoint.Endpoint, credential: _endpoint.Credential);
        _speechConfig.SpeechRecognitionLanguage = options.RecognitionLocale;

        _speechConfig.SetSpeechSynthesisOutputFormat(options.OutputFormat);
        _speechConfig.SpeechSynthesisVoiceName = options.SynthesisVoiceName;
        _speechConfig.SpeechSynthesisLanguage = options.SynthesisLocale;
        _logger.LogInformation(
            "Azure Speech Service initialized: Endpoint={Endpoint} Name={EndpointName} Region={Region} RecognitionLocale={RecognitionLocale} SynthesisVoice={SynthesisVoice}",
            _endpoint.Endpoint,
            _endpoint.Name,
            _endpoint.Region,
            options.RecognitionLocale,
            options.SynthesisVoiceName);
    }

    private static AzureSpeechEndpointOptions ResolveLegacyEndpoint(AzureSpeechServiceOptions options)
    {
        if (options.Endpoints.Count > 0)
        {
            return options.Endpoints[0];
        }

        if (options.Endpoint is null)
        {
            throw new InvalidOperationException(
                "AzureSpeechServiceOptions requires either 'Endpoint' or 'Endpoints' to be configured.");
        }

        return new AzureSpeechEndpointOptions
        {
            Name = "primary",
            Endpoint = options.Endpoint,
            Credential = options.Credential,
        };
    }

    /// <summary>
    /// Creates a new <see cref="ISpeechRecognizer"/> instance for continuous speech-to-text recognition.
    /// Each recognizer owns an independent push stream and recognition session.
    /// </summary>
    /// <returns>A new speech recognizer instance. The caller must dispose it after use.</returns>
    public ISpeechRecognizer CreateRecognizer()
    {
        lock (_resourceGate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return new AzureSpeechRecognizer(_speechConfig, logger: _logger as ILogger<AzureSpeechRecognizer>,
                operationTimeout: _options.Resilience.AttemptTimeout);
        }
    }

    /// <summary>
    /// Gets the shared <see cref="ISpeechSynthesizer"/> instance for text-to-speech synthesis.
    /// Synthesizer instances are reused across calls without synchronous network warm-up.
    /// </summary>
    /// <returns>The shared synthesizer instance.</returns>
    public ISpeechSynthesizer GetSynthesizer()
    {
        lock (_resourceGate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return _synthesizer ??= new AzureSpeechSynthesizer(
                _speechConfig,
                concurrency: _options.Concurrency,
                gender: _options.SynthesisGender,
                logger: _logger as ILogger<AzureSpeechSynthesizer>,
                maximumRetainedCapacity: _options.MaximumRetainedCapacity);
        }
    }

    private AzureSpeechRecognizer GetRecognizer()
    {
        lock (_resourceGate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return _recognizer ??= new AzureSpeechRecognizer(_speechConfig, logger: _logger as ILogger<AzureSpeechRecognizer>,
                operationTimeout: _options.Resilience.AttemptTimeout);
        }
    }

    #region ISpeechSynthesizer Implementation

    /// <inheritdoc />
    IAsyncEnumerable<ReadOnlyMemory<byte>> ISpeechSynthesizer.SynthesizeAsync(
        string text,
        SynthesizerInputFormat inputFormat,
        CancellationToken cancellationToken)
    {
        return GetSynthesizer().SynthesizeAsync(text, inputFormat, cancellationToken);
    }

    #endregion

    #region ISpeechRecognizer Implementation

    /// <inheritdoc />
    async Task ISpeechRecognizer.WriteAudioAsync(ReadOnlyMemory<byte> audioData, CancellationToken cancellationToken)
    {
        await GetRecognizer().WriteAudioAsync(audioData, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    IAsyncEnumerable<TranscriptSegment> ISpeechRecognizer.GetTranscriptsAsync(CancellationToken cancellationToken)
    {
        return GetRecognizer().GetTranscriptsAsync(cancellationToken);
    }

    /// <inheritdoc />
    async Task ISpeechRecognizer.CompleteAsync(CancellationToken cancellationToken)
    {
        if (_recognizer is not null)
        {
            await _recognizer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    #endregion

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        AzureSpeechRecognizer? recognizer;
        AzureSpeechSynthesizer? synthesizer;
        lock (_resourceGate)
        {
            if (_isDisposed) { return; }
            _isDisposed = true;
            recognizer = _recognizer;
            synthesizer = _synthesizer;
        }

        _logger.LogInformation("Disposing Azure Speech Service");

        // Dispose recognizer if it was created
        try
        {
            if (recognizer is not null) { await recognizer.DisposeAsync().ConfigureAwait(false); }
        }
        finally { synthesizer?.Dispose(); }

        // SpeechConfig doesn't implement IDisposable in the Azure Speech SDK
        // It will be garbage collected when no longer referenced
    }
}
