namespace Agents.AI.Extensions.ToolApproval.Authorization;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public class OnBehalfOfContextAttribute : Attribute
{
    // If true, the obo flow won't be triggered on ToolExecutionUnauthorizedException
    public bool DisableOnBehalfOf { get; set; }
    // The scope to acquire the obo token for.
    public string Scope { get; set; }

    public OnBehalfOfContextAttribute(string scope, bool disableOnBehalfOf = false)
    {
        DisableOnBehalfOf = disableOnBehalfOf;
        Scope = scope;
    }
}
