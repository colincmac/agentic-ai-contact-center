using Agents.AI.ContactCenter.Exceptions;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using Agents.AI.ContactCenter.Calling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agents.AI.ContactCenter.IvrWorkflow.Compilation;

internal sealed class WorkflowStartupValidationService(IServiceScopeFactory scopeFactory) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetService<ICallContextAccessor>()?.Set(new IncomingCallContext
        {
            CallId = "workflow-startup-validation",
            CallerIdentifier = "validation-caller",
            CallTargetIdentifier = "validation-target"
        });
        var catalog = scope.ServiceProvider.GetRequiredService<ICallWorkflowCatalog>();
        var errors = new List<string>();

        WorkflowRuntimeBinder binder;
        try
        {
            binder = scope.ServiceProvider.GetRequiredService<WorkflowRuntimeBinder>();
        }
        catch (Exception ex)
        {
            errors.Add($"Runtime binding services could not be created: {ex.Message}");
            throw new WorkflowCompilationException("registered-workflows", errors);
        }

        foreach (var workflow in catalog.Workflows)
        {
            try
            {
                binder.Bind(workflow);
            }
            catch (WorkflowCompilationException ex)
            {
                errors.AddRange(ex.Errors.Select(error => $"Workflow '{workflow.Id}': {error}"));
            }
            catch (Exception ex)
            {
                errors.Add($"Workflow '{workflow.Id}': {ex.Message}");
            }
        }

        if (errors.Count > 0)
        {
            throw new WorkflowCompilationException("registered-workflows", errors);
        }

    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
