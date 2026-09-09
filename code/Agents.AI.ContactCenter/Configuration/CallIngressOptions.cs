using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.IvrWorkflow.Catalog;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Configuration;

/// <summary>Route only facts obtained from authenticated ACS ingress, never caller-supplied workflow IDs.</summary>
public sealed class CallIngressOptions
{
    public const string SectionName = "CallIngress";
    public Dictionary<string, CallIngressRoute> Routes { get; set; } = new(StringComparer.Ordinal);
}

public sealed class CallIngressRoute
{
    public string WorkflowId { get; set; } = string.Empty;
    public AgentTier PreferredTier { get; set; } = AgentTier.DtmfOnly;
    public string Locale { get; set; } = "en-US";
}

public sealed class CallIngressRouter(ICallWorkflowCatalog catalog, IOptions<CallIngressOptions> options)
{
    public CallSessionRequest Route(IncomingCallContext trustedCall)
    {
        ArgumentNullException.ThrowIfNull(trustedCall);
        if (!options.Value.Routes.TryGetValue(trustedCall.CallTargetIdentifier, out var route))
        {
            throw new InvalidOperationException("No ingress route is configured for the called identifier.");
        }
        var workflow = catalog.Get(route.WorkflowId);
        return new CallSessionRequest
        {
            CallContext = trustedCall with { Locale = route.Locale },
            WorkflowId = $"{workflow.Id}@{workflow.Version}",
            PreferredTier = route.PreferredTier,
        };
    }

}

internal sealed class CallIngressOptionsValidator(ICallWorkflowCatalog catalog) : IValidateOptions<CallIngressOptions>
{
    public ValidateOptionsResult Validate(string? name, CallIngressOptions value)
    {
        var errors = new List<string>();
        foreach (var (target, route) in value.Routes)
        {
            if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(route.WorkflowId)
                || string.IsNullOrWhiteSpace(route.Locale) || !Enum.IsDefined(route.PreferredTier))
            {
                errors.Add("Ingress routes require a target, workflow revision, locale and valid tier.");
                continue;
            }
            try
            {
                if (!catalog.TryGet(route.WorkflowId, out _)) { errors.Add($"Unknown workflow '{route.WorkflowId}'."); }
            }
            catch (InvalidOperationException ex) { errors.Add(ex.Message); }
        }
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
