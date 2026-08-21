using Agents.AI.ContactCenter.DependencyInjection;
using global::Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using global::Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using global::Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Catalog;

public sealed class CallWorkflowCatalogTests
{
    private static WorkflowBlueprint Trivial(string id) => new()
    {
        Id = id,
        InitialStageId = "start",
        Stages = [new StageBlueprint { Id = "start", Terminal = true }],
    };

    [Fact]
    public void AddCallWorkflow_CompilesAndExposesViaCatalog()
    {
        var services = new ServiceCollection();
        services.AddCallWorkflow(Trivial("a"));
        services.AddCallWorkflow(Trivial("b"));

        var sp = services.BuildServiceProvider();
        var catalog = sp.GetRequiredService<ICallWorkflowCatalog>();

        Assert.Equal(2, catalog.Workflows.Count);
        Assert.True(catalog.TryGet("a", out _));
        Assert.True(catalog.TryGet("b", out _));
    }

    [Fact]
    public void Catalog_DuplicateWorkflowId_Throws()
    {
        var dup1 = Trivial("a");
        var dup2 = Trivial("a");
        Assert.Throws<ArgumentException>(() => new CallWorkflowCatalog([
            new WorkflowGraphCompiler().Compile(dup1),
            new WorkflowGraphCompiler().Compile(dup2),
        ]));
    }

    [Fact]
    public void Catalog_Get_UnknownIdThrows()
    {
        var services = new ServiceCollection();
        services.AddCallWorkflow(Trivial("only"));
        var sp = services.BuildServiceProvider();

        var catalog = sp.GetRequiredService<ICallWorkflowCatalog>();
        Assert.Throws<KeyNotFoundException>(() => catalog.Get("missing"));
    }
}
