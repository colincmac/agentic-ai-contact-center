using System.Threading.Channels;
using Agents.AI.ContactCenter.Calling;

namespace Agents.AI.ContactCenter.Testing;

internal sealed class ScenarioEventRecorder : ChannelWriter<StrategyEvent>
{
    private readonly Lock _gate = new();
    private readonly List<StrategyEvent> _events = [];
    private bool _completed;

    public override bool TryWrite(StrategyEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            if (_completed)
            {
                return false;
            }
            _events.Add(item);
            return true;
        }
    }

    public override ValueTask WriteAsync(
        StrategyEvent item,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryWrite(item))
        {
            return ValueTask.FromException(new ChannelClosedException());
        }
        return ValueTask.CompletedTask;
    }

    public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(!_completed);
        }
    }

    public override bool TryComplete(Exception? error = null)
    {
        lock (_gate)
        {
            if (_completed)
            {
                return false;
            }
            _completed = true;
            return true;
        }
    }

    public IReadOnlyList<StrategyEvent> Snapshot()
    {
        lock (_gate)
        {
            return [.. _events];
        }
    }
}
