# ADR-0009 — Voice biometrics: pluggable `IVoiceBiometricEvaluator` with stub default and gRPC adapter to a Python service

- **Status:** Accepted
- **Date:** initial deployment

## Context

The imported design supports voice biometric identity verification — analysing a caller's voice to confirm identity before sensitive operations (account access, fund transfers, PII retrieval). The former end-to-end implementation guide has not yet been migrated into this repository.

The realistic implementation choices fall on two axes:

1. **Where the speaker-verification model lives.** Best-of-breed open speaker-verification stacks (SpeechBrain on PyTorch, ECAPA-TDNN, etc.) are Python-native; the rest of the platform is .NET. Reimplementing the model in .NET is impractical and would lag upstream improvements.
2. **How the .NET agent talks to it.** Options are an in-process .NET implementation (would require rewriting / repackaging the model), an HTTP/REST call out to a Python service, or a gRPC call out to a Python service. Audio is the payload, so framing/streaming matters.

The agent also needs a working developer-loop experience without standing up the Python service for every test, and a clean seam for unit tests that should not depend on any external biometrics dependency.

## Decision

- Model voice biometrics as a **pluggable abstraction**: `IVoiceBiometricEvaluator` (the seam called by the agent's voice-biometric tools).
- Ship **two** implementations behind that interface:
  - **Stub mode (default for dev/test).** In-memory implementation that records enrolments, echoes verification decisions deterministically (configurable per test), and requires zero external dependencies. This is the default registration in the agent host.
  - **API mode (gRPC).** Adapter that talks to the Python speaker-verification service in `src/python-services/voice-biometrics/` (SpeechBrain / PyTorch on the Python side) over **gRPC**. Audio frames stream over the gRPC channel; the Python service returns enrolment IDs and verification decisions.
- Selection between stub and API mode is **configuration-driven** (the same options-binding pattern used elsewhere in the agent host, per the repo's `Copilot Instructions`).

## Consequences

- The agent code path is unaware of which implementation is wired in; tests inject the stub, production environments inject the gRPC adapter.
- The Python service can iterate on model architecture, model weights, and accelerator choice (CPU vs GPU) independently of the .NET agent. The interface contract — enrol, verify, identify — is the only thing the .NET side depends on.
- gRPC streaming is well-suited to the audio payload: low overhead per frame, native back-pressure, bidirectional streaming when the Python side wants to return interim scores. HTTP/REST would have required either large request bodies or chunked streaming retrofits.
- Latency is bounded by the gRPC call plus model inference time. Since voice biometrics is invoked at discrete moments in the dialog (not on every audio frame the realtime AI hears), this latency lives outside the realtime-AI hot path and does not affect the latency budget tracked by [ADR-0006](0006-realtime-ai-voicelive-vs-gpt-realtime.md).
- Local-loop development is fast: stub mode is the default and requires no Python service, no model download, no CUDA, and no extra container. The Aspire `Showcase.AppHost` does not currently wire the biometrics service into the dev orchestrator (it is one of the services explicitly noted as "exists in the repo but not yet wired into the AppHost" — see the repo's `Copilot Instructions`); when wired, it will be an opt-in resource.
- Tests stay clean: the stub is the test double. Tests that need to exercise specific decision outcomes configure the stub directly rather than mocking the gRPC channel.
- Production deployments take on a Python service as a runtime dependency: container image, model weights distribution, GPU sizing if applicable, gRPC TLS termination, and authentication between the .NET agent and the Python service.

## Alternatives considered

- **Single in-process .NET implementation.** Rejected. Would either ship a wrapper around an ML runtime (ONNX Runtime, TorchSharp) hosting an exported model — adding model-conversion fragility — or reimplement the speaker-verification stack in .NET, lagging upstream improvements indefinitely.
- **HTTP/REST instead of gRPC.** Workable but inferior for streaming audio: more bytes per frame, weaker streaming ergonomics, less natural back-pressure. gRPC is the standard answer for service-to-service streaming inside the platform.
- **Embed the Python service as a sidecar in the .NET agent pod.** Rejected as the default. Couples agent scaling to biometrics scaling, complicates GPU scheduling, and forces a Python runtime into the agent's deployment topology. The biometrics service stays as a separately scalable workload behind gRPC.
- **No stub; require the Python service even for tests.** Rejected — would slow the developer loop, make CI heavier, and couple unrelated unit tests to model behaviour.
- **3rd-party voice biometrics SaaS (e.g., Pindrop, Nuance Gatekeeper).** Out of scope for this ADR. The `IVoiceBiometricEvaluator` interface admits such a provider as a future implementation if a customer requirement justifies it.
