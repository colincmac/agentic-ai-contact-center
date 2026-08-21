using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;

namespace Agents.AI.ContactCenter.IvrWorkflow.Execution;

/// <summary>
/// Hand-off the executor passes to a strategy's auth-render callback when a stage's inline
/// <see cref="Blueprint.AuthenticationPlanBlueprint"/> has an unsatisfied step. The strategy
/// renders the credential acquisition surface for its modality (realtime: a spoken prompt +
/// <c>submit_credential</c> tool per request; DTMF: SSML + digit collection) and calls
/// <see cref="WorkflowExecutor.SubmitCredentialAsync"/> when the caller supplies a value.
/// </summary>
/// <param name="Stage">The stage whose plan is being satisfied.</param>
/// <param name="StepIndex">Zero-based index of the current step within the plan.</param>
/// <param name="Requests">
/// One <see cref="CredentialRequest"/> per authenticator acceptable for this step. A single
/// entry is a required step; multiple entries are an <c>anyOf</c> the caller chooses between.
/// </param>
/// <param name="Challenge">
/// For an out-of-band step (SMS OTP), the issued challenge — its <c>Prompt</c> carries the
/// caller-facing "a code was sent to …" text. <see langword="null"/> for in-band steps.
/// </param>
public sealed record AuthStepRender(
    CompiledStage Stage,
    int StepIndex,
    IReadOnlyList<CredentialRequest> Requests,
    AuthenticationChallenge? Challenge);
