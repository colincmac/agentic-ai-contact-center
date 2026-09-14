using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.State;
using Agents.AI.ContactCenter.State.Projections;
using ContactCenter.AIAgent.Configuration;
using Microsoft.Extensions.Options;

namespace ContactCenter.AIAgent.Services;

public sealed class VerificationTimeoutObserver(ICallCoordinator calls, IOptions<BankingDemoOptions> options) : ICallObserver
{
    private readonly CancellationTokenSource _stop = new();
    private readonly List<(CancellationTokenSource Source, Task Task)> _deadlines = [];
    private Task? _loop;
    private int _disposed;
    public string ObserverId => "demo-verification-timeout";

    public Task StartAsync(CallObservation observation, CancellationToken cancellationToken = default)
    {
        _loop = ObserveAsync(observation);
        return Task.CompletedTask;
    }

    private async Task ObserveAsync(CallObservation observation)
    {
        try
        {
            await foreach (var item in observation.Events.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                if (item is StrategyEvent.CallerAuthenticationChallenge challenge)
                {
                    foreach (var deadline in _deadlines) { await deadline.Source.CancelAsync().ConfigureAwait(false); }
                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    _deadlines.Add((cancellation, WatchAsync(observation, challenge.Challenge.ChallengeId, cancellation.Token)));
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task WatchAsync(CallObservation observation, string challengeId, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(options.Value.OtpInputTimeoutSeconds), ct).ConfigureAwait(false);
            var auth = observation.Services.GetRequiredService<CallStateProjector>().Get<AuthSnapshot>();
            if (auth.PendingChallenge?.ChallengeId == challengeId)
            {
                await calls.TransferAsync(observation.CallId, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_stop.IsCancellationRequested) { await _stop.CancelAsync().ConfigureAwait(false); }
        if (_loop is not null) { await _loop.WaitAsync(cancellationToken).ConfigureAwait(false); }
        await Task.WhenAll(_deadlines.Select(d => d.Task)).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        await StopAsync().ConfigureAwait(false);
        foreach (var deadline in _deadlines) { deadline.Source.Dispose(); }
        _stop.Dispose();
    }
}
