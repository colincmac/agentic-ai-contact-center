# Caller verification and action authorization

Transport authentication, the caller's identity, and the operator's identity are
different trust boundaries. ACS callback authentication does not verify a caller;
ANI lookup does not prove knowledge of a PIN.

## Required evidence

[AuthenticationPlanBlueprint](../IvrWorkflow/Blueprint/AuthenticationPlanBlueprint.cs)
defines ordered named-method groups, retry limits, evidence age, and an explicit
failure route. A successful method produces subject-bound, timestamped
[CredentialProgress](CredentialRequest.cs) in
[AuthSnapshot](../State/Projections/AuthStateProjection.cs).

`IdentifyLast4` followed by `Pin` requires both methods even though both produce
`KnowledgeBased`. Another method's numeric rank is never enough to satisfy a
named step. Expired evidence and evidence for another subject cannot satisfy it.
Old snapshots lacking those proof fields require re-verification.

Plans with missing/invalid failure targets or unknown registered methods fail
startup validation. Exhausted or unavailable verification takes the configured
unprotected failure stage; it never exposes the protected business stage.
Only the active group's methods can receive submissions.

## Capture

[CredentialCapture](CredentialCapture.cs) is the shared per-call numeric collector.
It supports explicit any-of method selection, pound submission, star clearing,
and bounded input lengths. DTMF, NLU, and realtime use this same collector.

During verification, the realtime strategy does not forward caller audio/digits
to the model, does not expose a `submit_credential` tool, and does not emit model
transcripts/function arguments. Trusted synthesis prompts the caller to use DTMF.
NLU does not forward verification audio to its classifier. Credential digits are
consumed before normal DTMF events are emitted. Raw credential values are never
written as `WorkflowDataRecorded`.

This built-in adapter does not collect spoken secrets. Hosts requiring another
capture channel must implement a trusted adapter with equivalent isolation and
redaction. The recorded-DTMF adapter routes authentication to the explicit failure
stage when no suitable recorded credential capture is available.

## Providers and challenges

Keep backend verification in [ICredentialAuthenticator](ICredentialAuthenticator.cs)
and its host-supplied dependencies: directory, PIN validator, SMS sender, and
challenge store. Authentication is not an LLM decision.

[IChallengeStore](IChallengeStore.cs) requires atomic validation/consumption with
call/subject binding and a server-side attempt budget. The built-in in-memory
store is for a single process; distributed hosts must supply the same atomic
semantics in their store. A get-then-delete implementation is insufficient.
No distributed challenge-store implementation is added here.

## Shared authorization

[WorkflowActionAuthorization](../Authorization/WorkflowActionAuthorization.cs)
uses ASP.NET Core `IAuthorizationService` and a resource requirement. Both
scripted [CallActionDispatcher](../IvrWorkflow/Execution/CallActionDispatcher.cs)
operations and realtime function bindings check the active stage and required
caller evidence before invoking protected code.

Realtime-specific tool approval attributes are also checked by
[AuthorizingAIFunction](../AITools/AuthorizingAIFunction.cs); missing handlers deny
execution. Backend operations must still validate resource ownership and honor
the stable idempotency key. Stage authorization is not a substitute for checking
which account or transaction the caller may operate on.

## Test and deployment boundary

The [authentication tests](../../test/Agents.AI.ContactCenter.Tests/Authentication/)
and [inline-plan tests](../../test/Agents.AI.ContactCenter.Tests/IvrWorkflow/Execution/InlineAuthDriverTests.cs)
exercise isolation, equal-rank methods, invalid ordering, expiry, retry exhaustion,
and one-shot OTP consumption locally.

Biometric model quality, liveness/spoof resistance, fraud services, caller directory
assurance, production SMS delivery, and live ACS transport validation are not
certified by these tests. Existing demonstration biometric evaluators must not be
registered as production verification providers.
