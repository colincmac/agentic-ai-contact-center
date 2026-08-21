# Azure Speech Service

Composite service providing both **speech recognition (STT)** and **speech synthesis (TTS)** backed by the Azure Speech SDK. Implements both `ISpeechRecognizer` and `ISpeechSynthesizer` interfaces for seamless integration with Contact Center strategies.

## Overview

The `AzureSpeechService` implements:

- **`ISpeechRecognizer`** — Continuous speech-to-text using push audio input streams
- **`ISpeechSynthesizer`** — Text-to-speech with streaming PCM audio output

And orchestrates:

- **`AzureSpeechRecognizer`** — Backing recognizer with connection pooling
- **`AzureSpeechSynthesizer`** — Backing synthesizer with connection pooling

Both components maintain **pre-warmed connection pools** to minimize first-byte latency and support high-concurrency scenarios.

## Features

- ✅ **Interface implementation** — Direct `ISpeechRecognizer` and `ISpeechSynthesizer` support
- ✅ **Unified configuration** — Single options object for both STT and TTS
- ✅ **Connection pooling** — Reuses warmed WebSocket connections across requests
- ✅ **Streaming API** — Low-latency audio/transcript streaming via `IAsyncEnumerable<T>`
- ✅ **Dependency injection** — Registered as both interfaces automatically
- ✅ **Azure CLI authentication** — Uses `AzureCliCredential` by default
- ✅ **Interim transcripts** — Real-time partial results during recognition
- ✅ **SSML support** — Fine-grained control over synthesis prosody

## Quick Start

### 1. Configuration

Add to `appsettings.json`:

```json
{
  "AzureSpeech": {
    "Endpoint": "https://your-resource.cognitiveservices.azure.com",
    "RecognitionLocale": "en-US",
    "SynthesisVoiceName": "en-US-Ava:DragonHDLatestNeural",
    "SynthesisLocale": "en-US",
    "SynthesisGender": "Female",
    "Concurrency": 4,
    "MaximumRetainedCapacity": 100
  }
}
```

### 2. Register Service

```csharp
using Agents.AI.ContactCenter.DependencyInjection;

// From configuration (registers as AzureSpeechService, ISpeechRecognizer, and ISpeechSynthesizer)
services.AddAzureSpeech(configuration);

// Or with inline configuration
services.AddAzureSpeech(options =>
{
    options.Endpoint = new Uri("https://your-resource.cognitiveservices.azure.com");
    options.RecognitionLocale = "en-US";
    options.SynthesisVoiceName = "en-US-Ava:DragonHDLatestNeural";
});
```

### 3. Inject and Use

```csharp
// Option A: Inject as ISpeechSynthesizer (for strategies that only need TTS)
public class MyTtsStrategy
{
    private readonly ISpeechSynthesizer _synthesizer;

    public MyTtsStrategy(ISpeechSynthesizer synthesizer)
    {
        _synthesizer = synthesizer;
    }

    public async Task Speak(CancellationToken ct)
    {
        await foreach (var frame in _synthesizer.SynthesizeAsync("Hello!", cancellationToken: ct))
        {
            // Process audio frame
        }
    }
}

// Option B: Inject as ISpeechRecognizer (for strategies that only need STT)
public class MySttStrategy
{
    private readonly ISpeechRecognizer _recognizer;

    public MySttStrategy(ISpeechRecognizer recognizer)
    {
        _recognizer = recognizer;
    }

    public async Task Listen(CancellationToken ct)
    {
        await foreach (var transcript in _recognizer.GetTranscriptsAsync(ct))
        {
            Console.WriteLine($"{(transcript.IsFinal ? "FINAL" : "INTERIM")}: {transcript.Text}");
        }
    }
}

// Option C: Inject as AzureSpeechService (for factory methods)
public class MyCompositeService
{
    private readonly AzureSpeechService _speechService;

    public MyCompositeService(AzureSpeechService speechService)
    {
        _speechService = speechService;
    }

    public async Task ProcessCall(CancellationToken ct)
    {
        // Create independent recognizer instances
        await using var recognizer1 = _speechService.CreateRecognizer();
        await using var recognizer2 = _speechService.CreateRecognizer();

        // Get shared synthesizer
        var synthesizer = _speechService.GetSynthesizer();
    }
}
```
```

## Dependency Injection

The service is registered as:

1. **`AzureSpeechService`** (concrete singleton)
2. **`ISpeechRecognizer`** (interface, forwarding to the same singleton)
3. **`ISpeechSynthesizer`** (interface, forwarding to the same singleton)

All three resolve to the **same singleton instance**:

```csharp
var speechService = provider.GetRequiredService<AzureSpeechService>();
var recognizer = provider.GetRequiredService<ISpeechRecognizer>();
var synthesizer = provider.GetRequiredService<ISpeechSynthesizer>();

Console.WriteLine(ReferenceEquals(speechService, recognizer)); // True
Console.WriteLine(ReferenceEquals(speechService, synthesizer)); // True
```

This allows Contact Center strategies to depend only on `ISpeechRecognizer` or `ISpeechSynthesizer` without knowing about the concrete implementation.

## Configuration Options

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Endpoint` | `Uri` | **required** | Azure Speech service endpoint URL |
| `RecognitionLocale` | `string` | `en-US` | Speech recognition locale |
| `SynthesisVoiceName` | `string` | `en-US-Ava:DragonHDLatestNeural` | TTS voice name |
| `SynthesisLocale` | `string` | `en-US` | TTS locale |
| `SynthesisGender` | `string` | `Female` | TTS voice gender |
| `OutputFormat` | `SpeechSynthesisOutputFormat` | `Raw24Khz16BitMonoPcm` | Audio output format |
| `Concurrency` | `int` | `2` | Number of pre-warmed instances |
| `MaximumRetainedCapacity` | `int` | `100` | Maximum pooled instances |

## Usage Patterns

### Pattern 1: Interface Injection (Recommended for Strategies)

Contact Center strategies should depend on interfaces for testability:

```csharp
// TTS-only strategy
public class GreetingStrategy
{
    private readonly ISpeechSynthesizer _synthesizer;

    public GreetingStrategy(ISpeechSynthesizer synthesizer)
    {
        _synthesizer = synthesizer;
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var frame in _synthesizer.SynthesizeAsync("Welcome!", cancellationToken: ct))
        {
            // Send to transport
        }
    }
}

// STT-only strategy
public class TranscriptionStrategy
{
    private readonly ISpeechRecognizer _recognizer;

    public TranscriptionStrategy(ISpeechRecognizer recognizer)
    {
        _recognizer = recognizer;
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var transcript in _recognizer.GetTranscriptsAsync(ct))
        {
            // Process transcript
        }
    }
}
```

### Pattern 2: Factory Methods

### Pattern 2: Factory Methods

Use the concrete type when you need to create multiple independent recognizer sessions:

```csharp
public class MultiSessionService
{
    private readonly AzureSpeechService _speechService;

    public MultiSessionService(AzureSpeechService speechService)
    {
        _speechService = speechService;
    }

    public async Task ProcessMultipleCallersAsync(CancellationToken ct)
    {
        // Create independent recognizer per caller
        await using var caller1Recognizer = _speechService.CreateRecognizer();
        await using var caller2Recognizer = _speechService.CreateRecognizer();

        // Both share the underlying pool but maintain separate sessions
    }
}
```

### Pattern 3: SSML Synthesis

### Pattern 3: SSML Synthesis

Use SSML for prosody control (works with both interface and concrete type):

```csharp
var ssml = @"
<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>
    <voice name='en-US-Ava:DragonHDLatestNeural'>
        <prosody rate='slow' pitch='high'>
            Welcome!
        </prosody>
    </voice>
</speak>";

// Via interface
await foreach (var frame in synthesizer.SynthesizeAsync(ssml, SynthesizerInputFormat.SSML, ct))
{
    // Handle frame
}

// Or via concrete type's GetSynthesizer()
var synth = speechService.GetSynthesizer();
await foreach (var frame in synth.SynthesizeAsync(ssml, SynthesizerInputFormat.SSML, ct))
{
    // Handle frame
}
```

## Architecture

```
AzureSpeechService (implements ISpeechRecognizer + ISpeechSynthesizer)
│
├── ISpeechSynthesizer Implementation ──> GetSynthesizer() ──> AzureSpeechSynthesizer (shared singleton)
│                                                              └── SynthesizerPool (pre-warmed)
│
└── ISpeechRecognizer Implementation ──> Lazy _recognizer ───> AzureSpeechRecognizer (per-instance)
                                                               └── RecognizerPool (pre-warmed)

Factory Methods (optional):
├── CreateRecognizer() ────────────────> New AzureSpeechRecognizer instance
└── GetSynthesizer() ──────────────────> Shared AzureSpeechSynthesizer instance
```

**Key Points:**

- **Single Singleton**: One `AzureSpeechService` instance per application
- **Recognizer**: Lazy-initialized per service instance (session-scoped via interface calls)
- **Synthesizer**: Shared across all calls (stateless, thread-safe)
- **Pools**: Both maintain pre-warmed Azure SDK connections

## Connection Pooling

### Synthesizer Pool

```csharp
internal sealed class SynthesizerPool
{
    // Pre-warms 'Concurrency' synthesizers on startup
    // Reuses healthy instances after each request
    // Disposes faulted instances (replaced lazily)
}
```

### Recognizer Pool

```csharp
internal sealed class RecognizerPool
{
    // Pre-warms 'Concurrency' recognizers on startup
    // Returned to pool after session completes successfully
    // Discarded if session fails or is canceled
}
```

## Audio Format Requirements

- **Recognition input**: 16 kHz, 16-bit, mono PCM
- **Synthesis output**: Configurable via `OutputFormat` (default: 24 kHz, 16-bit, mono PCM)

## Error Handling

The service propagates errors from the Azure Speech SDK with detailed logging:

```csharp
try
{
    await foreach (var transcript in speechService.RecognizeAsync(audioStream, ct))
    {
        // ...
    }
}
catch (InvalidOperationException ex)
{
    // SDK errors (e.g., authentication, quota, network)
    logger.LogError(ex, "Speech recognition failed");
}
catch (OperationCanceledException)
{
    // Graceful cancellation
}
```

## Observability

The service logs at appropriate levels:

- **Information**: Service initialization, session start/stop
- **Debug**: Pool operations, recognition events (interim/final), first-byte latency
- **Warning**: SDK cancellations, transient errors
- **Error**: SDK errors with error codes/details

Integrate with OpenTelemetry for distributed tracing.

## Testing

Prefer testing against `ISpeechRecognizer` and `ISpeechSynthesizer` interfaces:

```csharp
// Mock for unit tests
var mockSynthesizer = new Mock<ISpeechSynthesizer>();
mockSynthesizer
    .Setup(s => s.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesizerInputFormat>(), It.IsAny<CancellationToken>()))
    .Returns(AsyncEnumerable.Empty<ReadOnlyMemory<byte>>());

// Or use fakes from AI.TestingFramework (if available)
```

## Performance Considerations

1. **Pool Size**: Set `Concurrency` to match expected concurrent sessions (default: 2)
2. **First Request**: ~100-200ms to establish WebSocket (avoided via pre-warming)
3. **Subsequent Requests**: <10ms to acquire from pool
4. **Memory**: Each pooled instance holds an open WebSocket (~1-2 MB overhead)

## Related Types

- `ISpeechRecognizer` — Speech-to-text interface
- `ISpeechSynthesizer` — Text-to-speech interface
- `TranscriptSegment` — Recognition result (text, role, confidence, timestamps)
- `SynthesizerInputFormat` — Text or SSML

## See Also

- [AzureSpeechRecognizer.cs](./AzureSpeechRecognizer.cs)
- [AzureSpeechSynthesizer.cs](./AzureSpeechSynthesizer.cs)
- [AzureSpeechServiceExample.cs](../Samples/AzureSpeechServiceExample.cs)
- [Azure Speech SDK Documentation](https://learn.microsoft.com/azure/cognitive-services/speech-service/)

## Resiliency & Multi-Region Failover

`AddAzureSpeech(...)` always registers the resilient decorators
(`ResilientSpeechRecognizer` and `ResilientSpeechSynthesizer`) for the public
`ISpeechRecognizer` / `ISpeechSynthesizer` interfaces. Each underlying
`AzureSpeechService` instance is constructed per configured endpoint and wrapped
with a Polly v8 pipeline of **Timeout → Retry → Circuit Breaker** per
`(endpoint, operation)`, plus an explicit fallback loop across endpoints.

### Configuration

Supply an ordered list of endpoints (each can have its own credential) and tune
the resilience parameters under `AzureSpeech:Resilience`. The legacy single
`Endpoint` property is still honored and is promoted into a single-entry
`Endpoints` list at validation time.

```json
{
  "AzureSpeech": {
    "Endpoints": [
      { "Name": "eastus", "Endpoint": "https://eastus.cognitiveservices.azure.com", "Region": "eastus" },
      { "Name": "westus2", "Endpoint": "https://westus2.cognitiveservices.azure.com", "Region": "westus2" }
    ],
    "RecognitionLocale": "en-US",
    "SynthesisVoiceName": "en-US-Ava:DragonHDLatestNeural",
    "Resilience": {
      "AttemptTimeout": "00:00:08",
      "MaxRetryAttempts": 2,
      "BaseRetryDelay": "00:00:00.250",
      "MaxRetryDelay": "00:00:05",
      "BreakerFailureRatio": 0.5,
      "BreakerSamplingDuration": "00:00:30",
      "BreakerMinimumThroughput": 5,
      "BreakerDuration": "00:00:30",
      "EnableFallback": true
    }
  }
}
```

With a single endpoint configured the decorators still apply
**Timeout/Retry/Circuit Breaker**; fallback is a no-op until a second endpoint is added.

### Streaming-vs-retry boundary for synthesis

`ISpeechSynthesizer.SynthesizeAsync` returns `IAsyncEnumerable<ReadOnlyMemory<byte>>`,
so resilience can only be applied to the **start phase** (acquire enumerator +
advance to the first audio chunk). Retry and endpoint fallback happen
transparently before any byte is yielded to the caller. Once the first chunk
crosses the API boundary, subsequent errors propagate to the caller unmodified
- transports must not be asked to re-play audio mid-utterance.

### No-replay recognition restarts

`ResilientSpeechRecognizer` owns one logical session that is composed from one
or more inner recognizer sessions over time. On a transient failure, the
decorator will restart on the next endpoint **only if no final transcript
segment (`TranscriptSegment.IsFinal == true`) has been emitted to the caller
yet**; after that boundary, any failure is surfaced as-is on the transcript
stream. Audio frames that arrive while the inner recognizer is being recycled
are dropped (no PCM replay) and counted in the
`speech.resilience.audio_frames_dropped_total` metric so the operational
impact remains observable.

### Observability

- **ActivitySource**: `Agents.AI.ContactCenter.Speech.Resilience` (per-attempt
  spans with `speech.endpoint.name`, `speech.attempt`, `outcome`, and `error.code` tags)
- **Meter**: `Agents.AI.ContactCenter.Speech`
  - `speech.resilience.retries_total`
  - `speech.resilience.fallbacks_total`
  - `speech.resilience.circuit_breaker_transitions_total`
  - `speech.resilience.audio_frames_dropped_total`
  - `speech.resilience.attempt.duration` (histogram, ms)

