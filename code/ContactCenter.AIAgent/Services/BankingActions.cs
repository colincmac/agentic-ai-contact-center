using System.Globalization;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Agents.AI.ContactCenter.State;
using Agents.AI.ContactCenter.State.Projections;

namespace ContactCenter.AIAgent.Services;

public sealed class BalanceAction(IDemoBankingService bank, CallStateProjector state) : ICallWorkflowAction
{
    public string Name => "read-balance";
    public async Task<CallActionResult> ExecuteAsync(CallActionContext context, CancellationToken cancellationToken)
    {
        DemoAuthorization.RequireOtp(state, context);
        var account = await bank.GetAccountAsync(context.Caller.UserId, cancellationToken).ConfigureAwait(false);
        state.Fold(new StrategyEvent.WorkflowDataRecorded(new Dictionary<string, string?>
        {
            ["demo.balanceText"] = $"Your demo balance is {account.Balance.ToString("C2", CultureInfo.GetCultureInfo("en-US"))} US dollars.",
            ["demo.cardText"] = $"Your demo debit card ends in {account.CardLastFour}.",
        }, DateTimeOffset.UtcNow));
        return new(true, "balance-ready");
    }
}

public sealed class ActivateCardAction(IDemoBankingService bank, CallStateProjector state) : ICallWorkflowAction
{
    public string Name => "activate-card";
    public async Task<CallActionResult> ExecuteAsync(CallActionContext context, CancellationToken cancellationToken)
    {
        DemoAuthorization.RequireOtp(state, context);
        if (!state.Get<BankingSnapshot>().ActivationConfirmed) { return new(false, "keypad-confirmation-required"); }
        var account = await bank.ActivateCardAsync(context.Caller.UserId, context.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        state.Fold(new StrategyEvent.WorkflowDataRecorded(new Dictionary<string, string?>
        {
            ["demo.cardText"] = $"Your demo debit card ending in {account.CardLastFour} is now active. No real card was changed.",
        }, DateTimeOffset.UtcNow));
        return new(true, "activated");
    }
}

public sealed class TransferOperatorAction(ICallCoordinator calls) : ICallWorkflowAction
{
    public string Name => "transfer-operator";
    public async Task<CallActionResult> ExecuteAsync(CallActionContext context, CancellationToken cancellationToken)
    {
        await calls.TransferAsync(context.CallId, cancellationToken).ConfigureAwait(false);
        return new(true, "transfer-requested");
    }
}

public sealed class FinishCallAction(ICallCoordinator calls) : ICallWorkflowAction
{
    public string Name => "finish-call";
    public async Task<CallActionResult> ExecuteAsync(CallActionContext context, CancellationToken cancellationToken)
    {
        await calls.FinishAsync(context.CallId, "Thank you for trying the demo contact center. Goodbye.", cancellationToken).ConfigureAwait(false);
        return new(true, "goodbye-requested");
    }
}

internal static class DemoAuthorization
{
    public static void RequireOtp(CallStateProjector state, CallActionContext context)
    {
        var auth = state.Get<AuthSnapshot>();
        if (context.CallId != state.CallId || context.Caller.UserId != auth.UserId
            || !CallerEvidencePolicy.Satisfies(auth, new AuthStepGroup(["SmsOtp"]), TimeSpan.FromMinutes(5), DateTimeOffset.UtcNow))
        {
            throw new UnauthorizedAccessException("A current SMS OTP verification is required.");
        }
    }
}
