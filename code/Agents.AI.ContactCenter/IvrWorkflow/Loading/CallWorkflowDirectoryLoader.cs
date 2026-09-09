using Agents.AI.ContactCenter.DependencyInjection;
using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.IvrWorkflow.Loading;

/// <summary>
/// File-system loader for the new <see cref="WorkflowBlueprint"/> YAML schema. Discovers
/// every <c>*.yaml</c> / <c>*.yml</c> file in <paramref name="rootDirectory"/> (recursively)
/// and parses each one through <see cref="CallWorkflowYamlReader"/>.
/// </summary>
public static class CallWorkflowDirectoryLoader
{
    private static readonly EnumerationOptions enumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Load every YAML blueprint. A missing directory is a configuration error.</summary>
    public static IReadOnlyList<WorkflowBlueprint> Load(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        if (!Directory.Exists(rootDirectory))
        {
            throw new DirectoryNotFoundException($"Workflow directory '{rootDirectory}' does not exist.");
        }

        var blueprints = new List<WorkflowBlueprint>();
        foreach (var path in EnumerateFiles(rootDirectory))
        {
            var yaml = File.ReadAllText(path);
            blueprints.Add(CallWorkflowYamlReader.Read(yaml, sourceName: path));
        }
        return blueprints;
    }

    /// <summary>
    /// Register every blueprint in <paramref name="rootDirectory"/> with the workflow
    /// framework (idempotently calling <see cref="CallWorkflowServiceCollectionExtensions.AddCallWorkflowFramework(IServiceCollection)"/>).
    /// </summary>
    public static IServiceCollection AddCallWorkflowsFromDirectory(this IServiceCollection services, string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);

        services.AddCallWorkflowFramework();
        RegisterDiscoveredBlueprints(services, rootDirectory);
        return services;
    }

    private static void RegisterDiscoveredBlueprints(IServiceCollection services, string rootDirectory)
    {
        foreach (var path in EnumerateFiles(rootDirectory))
        {
            services.AddSingleton<WorkflowBlueprint>(_ =>
                CallWorkflowYamlReader.Read(File.ReadAllText(path), sourceName: path));
        }
    }

    private static IEnumerable<string> EnumerateFiles(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory))
        {
            throw new DirectoryNotFoundException($"Workflow directory '{rootDirectory}' does not exist.");
        }
        foreach (var path in Directory.EnumerateFiles(rootDirectory, "*.yaml", enumerationOptions)
            .Concat(Directory.EnumerateFiles(rootDirectory, "*.yml", enumerationOptions))
            .OrderBy(p => p, StringComparer.Ordinal))
        {
            yield return path;
        }
    }
}
