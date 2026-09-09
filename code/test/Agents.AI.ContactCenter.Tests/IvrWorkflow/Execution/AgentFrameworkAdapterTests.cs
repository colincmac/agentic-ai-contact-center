using System.Threading.Channels;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.IvrWorkflow.AgentFramework;
using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Microsoft.Agents.AI.Workflows;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Execution;

public sealed class AgentFrameworkAdapterTests
{
    [Fact]
    public async Task PinnedAgentFramework_ExecutesDiscreteCallCommand()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var compiled = new WorkflowGraphCompiler().Compile(new()
        {
            Id = "adapter", InitialStageId = "start",
            Stages =
            [
                new() { Id = "start", Transitions = [new() { TargetStageId = "done", Label = "finish" }] },
                new() { Id = "done", Terminal = true },
            ],
        });
        var session = new CallWorkflowSession(compiled, services, new CallerElevationDispatcher([], services));
        var events = Channel.CreateUnbounded<StrategyEvent>();
        var executor = new WorkflowExecutor(session, events.Writer, (_, _) => ValueTask.CompletedTask);
        var adapter = new CallWorkflowCommandExecutor(executor);
        var workflow = new WorkflowBuilder(adapter).WithOutputFrom(adapter).Build();
        await using var run = await InProcessExecution.RunAsync(workflow, new CallWorkflowCommand("finish"),
            cancellationToken: TestContext.Current.CancellationToken);
        var output = Assert.Single(run.NewEvents.OfType<WorkflowOutputEvent>());
        var result = Assert.IsType<CallWorkflowCommandResult>(output.Data);
        Assert.True(result.Accepted);
        Assert.Equal("done", result.StageId);
    }
}
