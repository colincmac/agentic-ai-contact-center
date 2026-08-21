using Azure.Core;
using Azure.Identity;

namespace Agents.AI.Sandbox;

/// <summary>
/// Configuration for per-call sandboxed tool execution on the Azure Container Apps
/// Sandboxes (Azure Dev Compute / ADC) data plane. See
/// <see href="https://learn.microsoft.com/en-us/azure/container-apps/sandboxes-overview"/>.
/// </summary>
/// <remarks>
/// The feature is <b>off</b> unless <see cref="Enabled"/> is set and the sandbox-group
/// coordinates (<see cref="SubscriptionId"/>, <see cref="ResourceGroup"/>,
/// <see cref="SandboxGroup"/>) are supplied. This mirrors the configuration-driven,
/// disabled-by-default posture used for voice biometrics (ADR-0009).
/// </remarks>
public sealed class CallSandboxOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "CallSandbox";

    /// <summary>
    /// When <see langword="false"/> (default) no sandbox is provisioned and sandboxed
    /// tools fail closed with a friendly message rather than running untrusted code
    /// in-process.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>ADC data-plane base endpoint.</summary>
    public Uri DataPlaneEndpoint { get; set; } = new("https://management.azuredevcompute.io");

    /// <summary>OAuth scope requested for the data-plane token.</summary>
    public string TokenScope { get; set; } = "https://management.azuredevcompute.io/.default";

    /// <summary>Azure subscription id that owns the sandbox group. Required when enabled.</summary>
    public string? SubscriptionId { get; set; }

    /// <summary>Resource group that owns the sandbox group. Required when enabled.</summary>
    public string? ResourceGroup { get; set; }

    /// <summary>Sandbox group name. Required when enabled.</summary>
    public string? SandboxGroup { get; set; }

    /// <summary>Disk image the per-call sandbox is created from.</summary>
    public string DiskImageName { get; set; } = "python3.12";

    /// <summary>Whether <see cref="DiskImageName"/> refers to a public ADC image.</summary>
    public bool DiskImageIsPublic { get; set; } = true;

    /// <summary>vCPU request for the sandbox (ADC resource string, e.g. "1").</summary>
    public string Cpu { get; set; } = "1";

    /// <summary>Memory request for the sandbox (ADC resource string, e.g. "1Gi").</summary>
    public string Memory { get; set; } = "1Gi";

    /// <summary>
    /// Optional Entra Agent Identity resource id assigned to the sandbox so in-sandbox
    /// code obtains tokens as the agent identity via <c>IDENTITY_ENDPOINT</c>. Hardening
    /// phase; null leaves the sandbox group default in effect.
    /// </summary>
    public string? AgentIdentityResourceId { get; set; }

    /// <summary>Optional region pin for sandbox data residency (hardening phase).</summary>
    public string? Region { get; set; }

    /// <summary>
    /// When <see langword="true"/> (default) the sandbox is provisioned with a deny-by-default
    /// egress policy: only hosts in <see cref="EgressAllowedHosts"/> are reachable. Prompt-injected
    /// code therefore cannot exfiltrate to arbitrary destinations. Set <see langword="false"/> only
    /// for trusted experiments where the sandbox needs open egress.
    /// </summary>
    public bool EgressDenyByDefault { get; set; } = true;

    /// <summary>
    /// Host patterns the sandbox is allowed to reach when <see cref="EgressDenyByDefault"/> is
    /// in effect (e.g. <c>*.openai.azure.com</c>, a package mirror, a partner API). Empty means
    /// no outbound network access — the safest default for a pure code interpreter.
    /// </summary>
    public IList<string> EgressAllowedHosts { get; init; } = [];

    /// <summary>Maximum time to wait for sandbox provisioning.</summary>
    public TimeSpan ProvisionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum time to wait for a single in-sandbox execution.</summary>
    public TimeSpan ExecutionTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Credential used to acquire the data-plane token. Not bindable from configuration;
    /// defaults to <see cref="DefaultAzureCredential"/>.
    /// </summary>
    public TokenCredential Credential { get; set; } = new DefaultAzureCredential();

    /// <summary>Throws when the options are inconsistent for an enabled feature.</summary>
    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SubscriptionId))
        {
            throw new InvalidOperationException($"{nameof(CallSandboxOptions)}.{nameof(SubscriptionId)} is required when the sandbox is enabled.");
        }
        if (string.IsNullOrWhiteSpace(ResourceGroup))
        {
            throw new InvalidOperationException($"{nameof(CallSandboxOptions)}.{nameof(ResourceGroup)} is required when the sandbox is enabled.");
        }
        if (string.IsNullOrWhiteSpace(SandboxGroup))
        {
            throw new InvalidOperationException($"{nameof(CallSandboxOptions)}.{nameof(SandboxGroup)} is required when the sandbox is enabled.");
        }
    }

    /// <summary>The sandbox-group data-plane scope path (without trailing slash).</summary>
    internal string BuildSandboxGroupScope()
    {
        var baseUrl = DataPlaneEndpoint.ToString().TrimEnd('/');
        return $"{baseUrl}/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}/sandboxGroups/{SandboxGroup}";
    }
}
