using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace Agents.AI.Extensions.ToolApproval.Authorization;



[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequiresAIToolScopesAttribute : Attribute, IToolApprovalRequirementData
{

    public RequiresAIToolScopesAttribute(
        string[]? acceptedScopes = null,
        string[]? acceptedAppPermissions = null)
    {
        AcceptedScopes = acceptedScopes;
        AcceptedAppPermissions = acceptedAppPermissions;
        
    }

    /// <summary>
    /// If true, acquire a user token (Agent User Identity). If false, app-only token for agent identity.
    /// </summary>
    public bool UseAgentUserIdentity { get; } = false;

    /// <summary>
    /// Whether to assert the agent user facet (validate xms_sub_fct claim after acquisition).
    /// </summary>
    public bool EnforceAgentUserFacet { get; set; } = false;

    public IReadOnlyList<string>? AcceptedAppPermissions { get; init; }
    public string? RequiredAppPermissionsConfigurationKey { get; init; }

    public IReadOnlyList<string>? AcceptedScopes { get; init; }
    public string? RequiredScopesConfigurationKey { get; init; }

    public IEnumerable<IToolApprovalRequirement> GetRequirements() => [
        new RequiresAIToolScopesRequirement(
            UseAgentUserIdentity,
            AcceptedAppPermissions,
            RequiredAppPermissionsConfigurationKey,
            AcceptedScopes,
            RequiredScopesConfigurationKey
        )
    ];
}

public class RequiresAIToolScopesRequirement(
        bool UseAgentUserIdentity,
        IReadOnlyList<string>? AcceptedAppPermissions,
        string? RequiredAppPermissionsConfigurationKey,
        IReadOnlyList<string>? AcceptedScopes,
        string? RequiredScopesConfigurationKey
    ) : ToolApprovalHandler<RequiresAIToolScopesRequirement>, IToolApprovalRequirement
{
    public bool UseAgentUserIdentity { get; } = UseAgentUserIdentity;
    public IReadOnlyList<string>? AcceptedAppPermissions { get; } = AcceptedAppPermissions;
    public string? RequiredAppPermissionsConfigurationKey { get; } = RequiredAppPermissionsConfigurationKey;
    public IReadOnlyList<string>? AcceptedScopes { get; } = AcceptedScopes;
    public string? RequiredScopesConfigurationKey { get; } = RequiredScopesConfigurationKey;

    public AIContent? OnFailureResponse => new TextContent("Invoking user did not have the required scopes to invoke the underlying function.");

    protected override Task HandleRequirementAsync(
        ToolApprovalContext context,
        RequiresAIToolScopesRequirement requirement)
    {
        IReadOnlyList<string>? appPermissions = AcceptedAppPermissions;
        IReadOnlyList<string>? scopes = AcceptedScopes;

        var configurationStore = context.Tool.GetService<IConfiguration>();
        if( configurationStore is { } configStore)
        {
            if (appPermissions is null or { Count: 0 }  && !string.IsNullOrEmpty(RequiredAppPermissionsConfigurationKey))
            {
                appPermissions = configStore.GetSection(RequiredAppPermissionsConfigurationKey).Get<IReadOnlyList<string>>() ?? [];
            }

            if (scopes is null or { Count: 0 } && !string.IsNullOrEmpty(RequiredScopesConfigurationKey))
            {
                scopes = configStore.GetSection(RequiredScopesConfigurationKey).Get<IReadOnlyList<string>>() ?? [];
            }
        }


        return Task.CompletedTask;
    }
}


public class RequiresScopesAIFunction(RequiresAIToolScopesRequirement scopeContext, AIFunction inner) : DelegatingAIFunction(inner)
{
    private readonly RequiresAIToolScopesRequirement _scopeContext = scopeContext;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        return InnerFunction.InvokeAsync(arguments,  cancellationToken);
    }

    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(RequiresAIToolScopesRequirement)
        ? _scopeContext : InnerFunction.GetService(serviceType, serviceKey);
}
