using Microsoft.Shared.Diagnostics;

namespace Agents.AI.Extensions.ToolApproval;

public interface IToolApprovalHandlerProvider
{
    Task<IEnumerable<IToolApprovalHandler>> GetHandlersAsync(ToolApprovalContext context);
}


public class ToolApprovalHandlerProvider : IToolApprovalHandlerProvider
{
    private readonly Task<IEnumerable<IToolApprovalHandler>> _handlersTask;

    public ToolApprovalHandlerProvider(IEnumerable<IToolApprovalHandler> handlers)
    {
        Throw.IfNull(handlers);
        _handlersTask = Task.FromResult(handlers);
    }

    /// <inheritdoc />
    public Task<IEnumerable<IToolApprovalHandler>> GetHandlersAsync(ToolApprovalContext context)
        => _handlersTask;
}

public class NoOpToolApprovalHandlerProvider : IToolApprovalHandlerProvider
{
    private readonly Task<IEnumerable<IToolApprovalHandler>> _handlersTask;

    public NoOpToolApprovalHandlerProvider()
    {
        _handlersTask = Task.FromResult(Enumerable.Empty<IToolApprovalHandler>());
    }

    /// <inheritdoc />
    public Task<IEnumerable<IToolApprovalHandler>> GetHandlersAsync(ToolApprovalContext context)
        => _handlersTask;
}
