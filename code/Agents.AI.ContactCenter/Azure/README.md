# Azure Speech adapters

[AddAzureSpeech](../DependencyInjection/AzureSpeechServiceCollectionExtensions.cs)
registers endpoint-based Speech adapters and the existing resilience decorators.

## Lifetimes

| Service | Lifetime | Ownership |
| --- | --- | --- |
| `ISpeechRecognizer` / `ResilientSpeechRecognizer` | Scoped per call | One mutable session; never resolve from the root provider |
| `ISpeechSynthesizer` / `ResilientSpeechSynthesizer` | Singleton | Shared synthesis pipeline and reusable native synthesizers |
| Endpoint registry | Singleton | Owns the configured `AzureSpeechService` instances and disposes them at shutdown |
| `IvrIntentAgent` and its keyed aliases | Scoped per call | Captures that call's recognizer, not a global recognition stream |

The interfaces do not resolve to one common singleton. `AzureSpeechService` is
used internally for each endpoint; it is not registered as a concrete injectable
singleton by this helper.

## Configuration

Use [AzureSpeechServiceOptions](../Configuration/AzureSpeechServiceOptions.cs)
with an ordered `Endpoints` collection or the legacy `Endpoint`/`Credential`
shortcut. Endpoint validation rejects missing configuration and duplicate names.
Credentials default to `DefaultAzureCredential`; supply an appropriate explicit
production credential rather than assuming local developer authentication.

`Concurrency` controls initial native synthesizer allocation, not a provider
quota or a measured concurrent-call limit. `MaximumRetainedCapacity` bounds the
idle synthesizer pool. `Resilience.AttemptTimeout` bounds recognizer SDK startup
and stop waits as well as the existing synthesis-start resilience behavior.

No synchronous constructor invokes a remote synthesis warm-up. Each recognizer
creates one independent push stream/session when first used, waits for actual SDK
startup, and rejects writes after completion. The old unused recognizer pool and
fixed startup delay are removed.

## Usage and disposal

Create a call DI scope, resolve the recognizer and NLU strategy in that scope,
and dispose the scope asynchronously when the call ends. A shared `IChatClient`
is fine; a shared mutable speech recognizer is not.

For direct construction, `AzureSpeechService.CreateRecognizer()` returns a new,
caller-owned recognizer. `GetSynthesizer()` returns the shared, service-owned
synthesizer. Do not dispose that shared synthesizer at the end of one call.

Recognition uses mono 16-bit PCM at 16 kHz. Synthesis output format is configured
separately; the host must match or resample formats at transport boundaries.
The recognizer transcript buffer is bounded and faults rather than silently
discarding final results on overflow.

## Evidence boundary

[SpeechSessionIsolationTests](../../test/Agents.AI.ContactCenter.Tests/Media/SpeechSessionIsolationTests.cs)
checks actual DI lifetimes, independent concurrent NLU streams using fakes, and
construction without credential acquisition. Existing resilience tests check
scripted provider failures. These tests do not establish live Azure compatibility,
regional capacity, latency, or throughput.
