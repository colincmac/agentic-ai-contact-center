# Caller Authentication

Single, AspNetCore-style authentication framework for the **Agents.AI.ContactCenter** SDK.
Authenticate inbound callers passively (ANI lookup), elevate them mid-call through pluggable
challenges (PIN, SMS OTP, voice biometric, Entra Verified ID, …), and gate AI tools / IVR
steps on the resulting verification level.

> **Interactive elevation is now driven by inline `authenticate:` plans** (see
> [Inline authentication plans](#inline-authentication-plans-authenticate)). The older
> `CallerAuthenticationTools` / `ElevatingAIFunction` tool path and `onBlocked` verify-stage
> detours have been removed.

## Core concepts

| Concept | Type | Notes |
|---|---|---|
| Verification level | [`CallerVerificationLevel`](./CallerVerificationLevel.cs) | Single ordered enum: `None` → `AniMatch` → `KnowledgeBased` → `MultiFactor` → `VoiceBiometric` → `EntraVerifiedId` → `Strong`. |
| Established caller | [`CallerIdentity`](./CallerIdentity.cs) | Immutable record. `CallerIdentity.Anonymous` is the call-start default. |
| Per-call state | [`CallerAuthenticationState`](./CallerAuthenticationState.cs) | Scoped, thread-safe. Holds current `Identity`, audit `Steps`, `PendingChallenge`. `TryPromote` is the **only** way to mutate identity. |
| One verification method | [`ICallerAuthenticator`](./ICallerAuthenticator.cs) | Stateless. Returns an `AuthenticationOutcome`. |
| Outcome | [`AuthenticationOutcome`](./AuthenticationOutcome.cs) | `Authenticated` \| `NotApplicable` \| `Failed` \| `NeedsChallenge`. |
| Chain runner | [`IAuthenticationOrchestrator`](./IAuthenticationOrchestrator.cs) → [`AuthenticationOrchestrator`](./AuthenticationOrchestrator.cs) | Runs authenticators in DI order, short-circuits on `Failed` / `NeedsChallenge`, records every attempt. |
| Mid-call dispatch | [`ICallerElevationDispatcher`](./ICallerElevationDispatcher.cs) → [`CallerElevationDispatcher`](./CallerElevationDispatcher.cs) | Tools call this to run a single named authenticator and emit the same `StrategyEvent`s the call-start runner produces. |
| Call-start helper | [`CallerAuthenticationRunner`](./CallerAuthenticationRunner.cs) | Strategies (`RealtimeCallWorkflowStrategy`, `DtmfCallWorkflowStrategy`, `NluCallWorkflowStrategy`) `await` this once from `StartAsync` (before `_executor.EnterAsync`). Resolves the orchestrator + state from the per-call DI scope, runs the chain against `StrategyStartContext.CallerMetadata`, and mirrors `CallerIdentified` / `CallerAuthenticationFailed` / `CallerAuthenticationChallenge` / `CallerVerificationLevelChanged` onto the strategy event channel — same event shape as `CallerElevationDispatcher`. No-ops when `AddCallerAuthentication()` was not called. |

## Built-in authenticators

| Authenticator | Source | Elevates to | Trigger |
|---|---|---|---|
| `AniIdentityLookupAuthenticator` | [`AniIdentityLookupAuthenticator.cs`](./AniIdentityLookupAuthenticator.cs) | `AniMatch` | Passive — fires on every call start, uses `ICallerDirectory`. |
| `PinAuthenticator` | [`PinAuthenticator.cs`](./PinAuthenticator.cs) | `KnowledgeBased` | Tool sets `PinAttempt.Digits` then dispatches `"Pin"`. Validation delegated to host `IPinValidator`. |
| `SmsOtpAuthenticator` | [`SmsOtpAuthenticator.cs`](./SmsOtpAuthenticator.cs) | `MultiFactor` | Two-phase: first run emits `NeedsChallenge` + sends code via `ISmsOtpSender`; second run consumes `SmsOtpAttempt.{ChallengeId,Code}` from `IChallengeStore`. |
| `AnonymousCallerAuthenticator` | [`AnonymousCallerAuthenticator.cs`](./AnonymousCallerAuthenticator.cs) | — | Always `NotApplicable`. Registered by default so the chain never enumerates an empty list. |

Voice-biometric and Entra Verified ID flows live under [`../Authorization/Biometrics/`](../Authorization/Biometrics/)
and [`../Authorization/IdentityVerification/`](../Authorization/IdentityVerification/) respectively. Wrap them in
an `ICallerAuthenticator` to plug them into the same chain.

## DI registration

```csharp
builder.AddContactCenter()
    .WithCallerAuthentication(auth => auth
        .WithCallerDirectory<MyCallerDirectory, CallerIdentity>()
        .WithChallengeStore<InMemoryChallengeStore>()
        .AddAniIdentityLookupAuthenticator()    // passive ANI identity
        .AddIdentifyByLast4Authenticator()      // knowledge-based identity from account last-4
        .AddPinAuthenticator<MyPinValidator>()  // PIN elevation (KnowledgeBased)
        .AddSmsOtpAuthenticator());             // OTP elevation (MultiFactor)
// + host-supplied `ISmsOtpSender` registration for OTP delivery
```

`WithCallerAuthentication(...)` (see [`../DependencyInjection/CallerAuthenticationContainerExtensions.cs`](../DependencyInjection/CallerAuthenticationContainerExtensions.cs)) registers:
- `CallerAuthenticationState` (scoped) — now also tracks per-step `Requirements` progress
- `IAuthenticationOrchestrator` → `AuthenticationOrchestrator` (scoped) — call-start passive chain (ANI)
- `ICallerElevationDispatcher` → `CallerElevationDispatcher` (scoped) — runs a single named authenticator
- `IChallengeStore` → `InMemoryChallengeStore` (singleton; replace for distributed deployments)
- `AnonymousCallerAuthenticator` fallback (singleton, enumerable)

## Inline authentication plans (`authenticate:`)

Interactive elevation (PIN, OTP, last-4 identify) is authored **once** as an ordered
`authenticate:` plan on the stage that needs it. The `WorkflowExecutor` drives the plan
**inline** on stage entry — there is no `onBlocked` detour and no per-channel verify stages.

```yaml
stages:
  - id: balance
    authenticate:
      - use: IdentifyLast4        # establish identity from account last-4
      - anyOf: [Pin, SmsOtp]      # confirm with PIN or OTP (caller's choice)
    realtime:
      instructions: [ "Read the caller their balance...", ... ]
```

Each step names one or more `ICredentialAuthenticator`s by `Name`. An authenticator advertises
its `CredentialRequest` (digits / spoken text / out-of-band code, prompts, length, secrecy);
each strategy renders that request with its own transport:

- **Realtime** — pushes the spoken instruction + a `submit_credential` tool; the model collects the value and calls it.
- **DTMF / NLU** — synthesizes the SSML prompt and collects digits, submitting on `#` or max length.

The executor stashes the value on the authenticator's attempt buffer, dispatches it through
`ICallerElevationDispatcher`, records progress on `CallerAuthenticationState.Requirements`, then
renders the next step or the business stage. State is scoped to the call, so a mid-call tier
swap resumes the plan exactly where it left off.


## Two-layer tool / step gating

### Per-tool — `[RequiresCallerVerification]`

Decorate any AI tool method with [`RequiresCallerVerificationAttribute`](./RequiresCallerVerification.cs). The
[`AuthorizingAgentFunction`](../../Agents.AI.Extensions/RealtimeAgentHelpers/AuthorizingRealtimeAIAgent.cs)
discovers it through the existing `IToolApprovalRequirementData` contract; the registered
`CallerVerificationApprovalHandler` reads `CallerAuthenticationState.Identity.VerificationLevel`
from the per-call DI scope and short-circuits the tool invocation when the caller is below the
required level, returning `OnFailureResponse` to the agent transcript.

```csharp
[Description("Move funds between caller's accounts.")]
[RequiresCallerVerification(
    CallerVerificationLevel.MultiFactor,
    FailureMessage = "Transfers require multi-factor verification.")]
public Task<TransferResult> TransferFunds(string fromId, string toId, decimal amount) { … }
```

Failure path returns the requirement's `OnFailureResponse` as the tool result instead of
invoking the underlying method — the caller never sees the privileged code path.

### Per-IVR-step — `MinimumVerificationGuard`

Plug [`MinimumVerificationGuard`](./MinimumVerificationGuard.cs) into any step's `Guards` to
gate entry on the live `CallerVerificationLevel`:

```csharp
new RealtimeIvrWorkflowStep("transfer-prompt", …)
{
    Guards = [ new MinimumVerificationGuard(CallerVerificationLevel.MultiFactor) ],
}
```

For YAML-driven flows the built-in `auth` guard factory accepts the same levels —
see [`../IvrWorkflow/Guards/BuiltInGuardFactories.cs`](../IvrWorkflow/Guards/BuiltInGuardFactories.cs).
Accepted aliases per level:

| Level | YAML aliases |
|---|---|
| `None` | `none` |
| `AniMatch` | `aniMatch`, `ani`, `phone` |
| `KnowledgeBased` | `knowledgeBased`, `knowledge`, `kba`, `pin` |
| `MultiFactor` | `multiFactor`, `mfa` |
| `VoiceBiometric` | `voiceBiometric`, `biometric`, `voice` |
| `EntraVerifiedId` | `entraVerifiedId`, `verifiedId` |
| `Strong` | `strong` |

```yaml
stages:
  - id: capability:card.activate
    requires:
      - type: auth
        level: multiFactor
```

## Mid-call elevation flow

Elevation is normally driven by the inline `authenticate:` plan (above): the executor stashes
the collected value and dispatches the named authenticator for you. When you need full control
from a custom tool (custom result envelope, non-standard authenticator name, side-effects), call
`ICallerElevationDispatcher.DispatchAsync` yourself:

```csharp
[Description("Validate the caller's PIN.")]
[RequiresCallerVerification(CallerVerificationLevel.AniMatch)]
AuthValidationResult ValidatePin(string digits, IServiceProvider services)
{
    var dispatcher = services.GetRequiredService<ICallerElevationDispatcher>();
    var attempt    = services.GetRequiredService<PinAttempt>();
    attempt.Digits = digits;

    var result = dispatcher.DispatchAsync("Pin", callId).GetAwaiter().GetResult();
    var step   = result.Steps.LastOrDefault(s => s.AuthenticatorName == "Pin");
    return step?.Outcome switch
    {
        AuthenticationOutcome.Authenticated => new AuthValidationResult(true, "Verified."),
        AuthenticationOutcome.Failed f       => new AuthValidationResult(false, f.Reason),
        _                                    => new AuthValidationResult(false, "PIN authenticator not registered."),
    };
}
```

Tools must never call `CallerAuthenticationState.TryPromote` directly — always go through the
inline plan or the dispatcher so the state-machine + event surface stays consistent.

The dispatcher itself:
1. Looks up the named `ICallerAuthenticator` (case-insensitive).
2. Runs it inside the per-call DI scope.
3. Records the step on `CallerAuthenticationState` and calls `TryPromote` on `Authenticated`.
4. When a `ChannelWriter<StrategyEvent>` is supplied, emits the same events the call-start
   runner produces — `CallerIdentified`, `CallerAuthenticationFailed`,
   `CallerAuthenticationChallenge`, `CallerVerificationLevelChanged` — so observers can't
   tell call-start from mid-call elevations apart.

## Strategy events

All authentication-related events flow through the strategy event channel observers already
consume (see [`../Calling/StrategyEvent.cs`](../Calling/StrategyEvent.cs)):

- `StrategyEvent.CallerIdentified(identity, authenticatorName, at)`
- `StrategyEvent.CallerAuthenticationFailed(authenticatorName, reason, at)`
- `StrategyEvent.CallerAuthenticationChallenge(challenge, at)`
- `StrategyEvent.CallerVerificationLevelChanged(from, to, at)`

## Writing a new authenticator

```csharp
public sealed class EntraVerifiedIdAuthenticator(IEntraVidClient client) : ICallerAuthenticator
{
    public string Name => "EntraVerifiedId";

    public async Task<AuthenticationOutcome> AuthenticateAsync(
        AuthenticationContext context,
        CancellationToken cancellationToken = default)
    {
        var presentation = await client.RequestPresentationAsync(context.CallId, cancellationToken);
        if (presentation is null) { return new AuthenticationOutcome.NotApplicable("VID not configured."); }
        if (!presentation.IsValid) { return new AuthenticationOutcome.Failed(presentation.Error ?? "VID rejected."); }

        var identity = context.CurrentIdentity with
        {
            VerificationLevel = CallerVerificationLevel.EntraVerifiedId,
            AuthenticatedBy   = Name,
            AuthenticatedAt   = DateTimeOffset.UtcNow,
            EntraObjectId     = presentation.Subject,
        };
        return new AuthenticationOutcome.Authenticated(identity);
    }
}

// Registration
builder.AddCallerAuthenticator<EntraVerifiedIdAuthenticator>();
```

Rules of thumb:
- **Stateless.** All per-call state lives on `CallerAuthenticationState` / `IChallengeStore`.
- **Idempotent.** The chain may run more than once per call. Return `NotApplicable` quickly
  when nothing actionable is in scope.
- **Honest about elevation.** Only set `VerificationLevel` to a level you actually proved.
- **No back-channel writes.** Always go through `Authenticated` → orchestrator/dispatcher
  → `TryPromote`. Never mutate `CallerAuthenticationState.Identity` directly.

## Testing

See [`test/Agents.AI.ContactCenter.Tests/Authentication/AuthenticationTests.cs`](../../../../test/Agents.AI.ContactCenter.Tests/Authentication/AuthenticationTests.cs)
for end-to-end coverage of the orchestrator, dispatcher, both built-in chain authenticators,
the IVR guard, and the tool-approval requirement.

## Strategy execution model

Every conversation strategy in [`../Calling/Strategies/`](../Calling/Strategies/) follows the
same authentication shape — only the *surface* the caller interacts with changes. There are
three enforcement points and three layered guards, and **all** strategies hit them in the
same order:

```
┌── Call start ────────────────┐   ┌── Step transition ───────────┐   ┌── Tool invocation ───────────┐
│ CallerAuthenticationRunner   │   │ RealtimeIvrWorkflowController│   │ IvrStepGuards (wrap or pre)  │
│   .RunAsync(...)             │ → │   .TransitionToStepAsync     │ → │ + AuthorizingAgentFunction   │
│   → orchestrator runs ANI    │   │   .EvaluateGuardsAsync       │   │   ([RequiresCallerVerif…])   │
│   → state.TryPromote         │   │   blocks on guard failure    │   │   blocks on requirement fail │
└──────────────────────────────┘   └──────────────────────────────┘   └──────────────────────────────┘
```

Mid-call elevation (PIN, OTP) is driven by tools dispatching through
`ICallerElevationDispatcher` between steps — the dispatcher promotes
`CallerAuthenticationState`, which `MinimumVerificationGuard` re-reads on the next
transition attempt, which unblocks the gated step. No strategy needs custom auth code.

### Per-strategy behavior

| Strategy | Source | Call-start auth | Step-transition guards | Tool gating |
|---|---|---|---|---|
| **Realtime voice** | [`RealtimeCallWorkflowStrategy`](../Calling/Strategies/RealtimeVoice/RealtimeCallWorkflowStrategy.cs) | `await CallerAuthenticationRunner.RunAsync(context, _events.Writer, _logger, ct)` in `StartAsync`, before `_executor.EnterAsync`. ANI fires once, `CallerIdentified` is emitted, `CallerAuthenticationState` is promoted. The matched `CallerIdentity` is then surfaced into every realtime stage prompt as a `## Caller hint (unverified)` section by [`StagePromptRenderer`](../IvrWorkflow/Navigation/StagePromptRenderer.cs) — the model is told the match may be wrong (spoofed/shared phone) and must confirm the name with the caller. | `CallWorkflowNavigator.TryAdvance` evaluates `requires:` predicates per edge (`BuiltInPredicates.AuthVerificationLevel`). Denied edges route via `onBlocked`. | **Two layers stack**: function-invocation middleware [`CallerVerificationFilter`](../IvrWorkflow/Authorization/CallerVerificationFilter.cs) evaluates `[RequiresCallerVerification]` on every tool invocation, AND the tool-approval pipeline runs [`RequiresCallerVerificationHandler`](./RequiresCallerVerification.cs) for tools surfaced through `AuthorizingAgentFunction`. Either layer can block. |
| **NLU + DTMF fallback** | [`NluCallWorkflowStrategy`](../Calling/Strategies/Nlu/NluCallWorkflowStrategy.cs) | Same `CallerAuthenticationRunner.RunAsync` call from `StartAsync` before the audio/DTMF/classify pumps start. | Same navigator/edge-predicate path. Step transitions driven by intent classifier results route through the same `BuiltInPredicates.AuthVerificationLevel`. | NLU surface plays scripted SSML (no per-step tools); elevation tools dispatched through `ICallerElevationDispatcher` from the realtime tier survive a tier swap because state is scoped to the call. |
| **DTMF** | [`DtmfCallWorkflowStrategy`](../Calling/Strategies/Dtmf/DtmfCallWorkflowStrategy.cs) | Same `CallerAuthenticationRunner.RunAsync` call from `StartAsync` before the `_dtmfPump` task is scheduled. | Same navigator/edge-predicate path. | DTMF stages are SSML-only; transitions driven by digit-to-label mapping in `scripted.menu`. Privileged transitions are gated by `requires: { type: auth, level: … }` on the edge, not by per-tool attributes. |
| **Composite fallback** | [`CompositeFallbackStrategy`](../Calling/Strategies/Composite/CompositeFallbackStrategy.cs) | The composite owns the call's DI scope. When the active inner strategy degrades, the new inner strategy's `StartAsync` runs the runner again against the SAME `CallerAuthenticationState` and `IvrWorkflowState` — the orchestrator returns `NotApplicable` quickly when ANI is already matched and no incremental events fire. Caller never re-authenticates. | Inherited from the active inner strategy. | Inherited from the active inner strategy. |

### End-to-end auth flow against an IVR Realtime Workflow

Take [`authenticated-realtime-bank.callworkflow.yaml`](../../../../src/Agents/Showcase.Agent.VoiceAgent/Workflow/Samples/authenticated-realtime-bank.callworkflow.yaml)
running under `RealtimeCallWorkflowStrategy`:

1. **Inbound call attached.** `RealtimeCallWorkflowStrategy.StartAsync` awaits
   `CallerAuthenticationRunner.RunAsync`. `AniIdentityLookupAuthenticator` resolves the
   caller from `ICallerDirectory`; `CallerAuthenticationState.Identity` is promoted to
   `AniMatch`. `StrategyEvent.CallerIdentified` is emitted.
2. **Initial step (`welcome`).** No edge requirement → no predicate. `StagePromptRenderer`
   surfaces the matched identity into the system prompt as a `## Caller hint (unverified)`
   section — name / phone / verification level / source authenticator — together with
   explicit guidance that caller IDs may be spoofed or shared. The model confirms the
   name with the caller (or asks for it when no hint is present) and writes the spoken
   name into `IvrWorkflowState` via `record_caller_name`. **The hint is never treated as
   a fact.**
3. **Transition to `verify`** (triggered when the caller picks an intent that needs
   knowledge-based verification). The step exposes `validate-pin` as a tool. The model
   asks the caller for their PIN. `validate-pin` is decorated with
   `[RequiresCallerVerification(AniMatch)]`; `CallerVerificationFilter` (function-invocation
   middleware) reads `CallerAuthenticationState.Identity.VerificationLevel`, sees
   `AniMatch`, and lets the call through.
4. **Caller speaks PIN → model calls `validate-pin`.** The tool stashes digits on
   `PinAttempt`, calls `ICallerElevationDispatcher.DispatchAsync("Pin", callId)`. The
   dispatcher runs `PinAuthenticator` → `IPinValidator.ValidateAsync` → promotes the
   identity to `KnowledgeBased`. `StrategyEvent.CallerVerificationLevelChanged(AniMatch,
   KnowledgeBased)` is emitted. Tool returns `{ Success = true, … }`.
5. **(Optional) MFA step.** If the workflow requires it, the model calls
   `request-otp` → `SmsOtpAuthenticator` returns `NeedsChallenge`, OTP is sent,
   `StrategyEvent.CallerAuthenticationChallenge` is emitted. Model reads the prompt to
   the caller, collects the code, calls `submit-otp(challengeId, code)` → promotes
   to `MultiFactor`.
6. **Transition to a guarded edge** (e.g. `welcome → balance` with
   `requires: { type: auth, level: multiFactor }`). `CallWorkflowNavigator.TryAdvance`
   runs `BuiltInPredicates.AuthVerificationLevel(MultiFactor)`, which reads
   `CallerAuthenticationState.Identity.VerificationLevel` — promoted to `MultiFactor`
   in step 5 — and passes. Transition completes.
7. **Caller invokes a privileged tool.** `CallerVerificationFilter` (or, for
   approval-wrapped tools, `RequiresCallerVerificationHandler`) re-reads the live
   `CallerVerificationLevel`. Below-threshold callers get the requirement's
   `OnFailureResponse` text back as the tool result and the underlying method never runs.

The DTMF and NLU paths follow the identical sequence — only step 4 changes:
DTMF collects digits with `CollectDtmf`/in-band detection and routes them through the
stage's `scripted.menu` map; NLU classifies free-form intent via `IvrIntentAgent`. Both
share the same `CallerAuthenticationState`, the same edge predicates, and the same
elevation dispatcher — only the input modality differs.

### Two-layer gating recap

| Layer | Runs when | Reads from | Enforced by |
|---|---|---|---|
| Step entry | Before `TransitionToStepAsync` allows the move | `IvrWorkflowState.VerificationLevel` (mirrored from `CallerAuthenticationState` by the runner / dispatcher) | `MinimumVerificationGuard` (or any `IIvrStepGuard`) in `step.Guards` / YAML `requires:` |
| Tool entry (per-step) | DTMF menu invocation or model-issued tool call wrapped by `WrapToolsWithCurrentGuards` | Same as above | `GuardedAIFunction` |
| Tool entry (per-tool) | Every model-issued tool call (Realtime + NLU) | `CallerAuthenticationState` resolved from the per-call DI scope | `AuthorizingAgentFunction` + `CallerVerificationApprovalHandler` evaluating `[RequiresCallerVerification(level)]` |

Step-entry guards stop the workflow from *advancing* into privileged territory. Tool-entry
guards stop privileged side-effects even if a caller / model finds a way to invoke them
out of band. Use both — they're cheap and independent.

### Common pitfalls

- **Adding `[RequiresCallerVerification(MultiFactor)]` to the elevation tool itself.**
  This locks callers out of the path that would have elevated them. Gate elevation tools
  at `AniMatch` (so an identified caller can try) and gate the privileged tools at the
  target level.
- **Skipping the step guard and relying only on the tool attribute.** Without
  `MinimumVerificationGuard` on the step, the workflow can transition into the
  privileged step and the model will narrate the privileged context before the tool
  attribute kicks in. Always gate both.
- **Bypassing the dispatcher from a tool** (calling `state.TryPromote` directly).
  Observers, telemetry spans, and the audit trail on `CallerAuthenticationState.Steps`
  all flow from the dispatcher / orchestrator. Direct mutation skips them and the call
  becomes effectively unobservable from a security-audit standpoint.
- **Treating the DTMF path as a tool-attribute path.** DTMF menu actions go through
  `InvokeActionAsync`, which only evaluates `CurrentStep.Guards` — it does NOT
  re-evaluate the `[RequiresCallerVerification]` attribute (that's a Realtime/NLU
  function-invocation concern). For DTMF flows, always express the requirement as a step
  guard.
