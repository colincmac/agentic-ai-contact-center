using Agents.AI.ContactCenter.Exceptions;
using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Loading;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Compilation;

public sealed class WorkflowContractTests
{
    [Fact]
    public void AllShippedSamples_LoadAndCompile()
    {
        var samples = CallWorkflowDirectoryLoader.Load(Path.Combine(AppContext.BaseDirectory, "IvrWorkflow", "Samples"));
        Assert.Equal(3, samples.Count);
        foreach (var sample in samples) { new WorkflowGraphCompiler().Compile(sample); }
    }

    [Fact]
    public void Authentication_RequiresExplicitFailureRoute()
    {
        var flow = new WorkflowBlueprint
        {
            Id = "test", InitialStageId = "verify",
            Stages = [new() { Id = "verify", Authentication = new() { Steps = [new(["Pin"])] } }],
        };
        Assert.Throws<WorkflowCompilationException>(() => new WorkflowGraphCompiler().Compile(flow));
    }

    [Theory]
    [InlineData("misspelled: true")]
    [InlineData("schemaVersion: 9")]
    [InlineData("version: 0")]
    [InlineData("id: duplicate")]
    public void InvalidOrUnknownYaml_IsRejected(string extra)
    {
        Assert.Throws<CallWorkflowYamlException>(() => CallWorkflowYamlReader.Read($"""
            id: test
            initialStage: done
            {extra}
            stages:
              - id: done
                terminal: true
            """));
    }

    [Fact]
    public void WorkflowVersions_Coexist_AndRequireExplicitSelection()
    {
        CompiledCallWorkflow Compile(int version) => new WorkflowGraphCompiler().Compile(new()
        {
            Id = "test", Version = version, InitialStageId = "done", Stages = [new() { Id = "done", Terminal = true }],
        });
        var catalog = new CallWorkflowCatalog([Compile(1), Compile(2)]);
        Assert.Equal(1, catalog.Get("test@1").Version);
        Assert.Equal(2, catalog.Get("test@2").Version);
        Assert.Throws<InvalidOperationException>(() => catalog.Get("test"));
    }
}
