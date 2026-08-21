using global::Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using global::Agents.AI.ContactCenter.IvrWorkflow.Loading;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Loading;

public sealed class CallWorkflowDirectoryLoaderTests : IDisposable
{
    private readonly string _temp;

    public CallWorkflowDirectoryLoaderTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), $"cwd-loader-{Guid.NewGuid():n}");
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
        }
    }

    private void WriteYaml(string filename, string content)
        => File.WriteAllText(Path.Combine(_temp, filename), content);

    [Fact]
    public void Load_DiscoversYamlFilesRecursively()
    {
        WriteYaml("a.yaml", "id: alpha\ninitialStage: s\nstages:\n  - id: s\n    terminal: true\n");
        Directory.CreateDirectory(Path.Combine(_temp, "nested"));
        File.WriteAllText(
            Path.Combine(_temp, "nested", "b.yml"),
            "id: beta\ninitialStage: s\nstages:\n  - id: s\n    terminal: true\n");

        var blueprints = CallWorkflowDirectoryLoader.Load(_temp);

        Assert.Equal(2, blueprints.Count);
        Assert.Contains(blueprints, b => b.Id == "alpha");
        Assert.Contains(blueprints, b => b.Id == "beta");
    }

    [Fact]
    public void Load_NonexistentDirectory_ReturnsEmpty()
    {
        var blueprints = CallWorkflowDirectoryLoader.Load(Path.Combine(_temp, "does-not-exist"));
        Assert.Empty(blueprints);
    }

    [Fact]
    public void AddCallWorkflowsFromDirectory_RegistersEachBlueprint()
    {
        WriteYaml("one.yaml", "id: one\ninitialStage: s\nstages:\n  - id: s\n    terminal: true\n");
        WriteYaml("two.yaml", "id: two\ninitialStage: s\nstages:\n  - id: s\n    terminal: true\n");

        var services = new ServiceCollection();
        services.AddCallWorkflowsFromDirectory(_temp);

        var sp = services.BuildServiceProvider();
        var catalog = sp.GetRequiredService<ICallWorkflowCatalog>();

        Assert.Equal(2, catalog.Workflows.Count);
        Assert.True(catalog.TryGet("one", out _));
        Assert.True(catalog.TryGet("two", out _));
    }
}
