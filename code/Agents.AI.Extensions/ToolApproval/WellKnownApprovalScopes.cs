namespace Agents.AI.Extensions.ToolApproval;

public static class WellKnownApprovalScopes
{
    public static class Azure
    {
        public const string AzureResourceManagerOboTokenScope = "https://management.core.windows.net/.default";
        public const string AzureKeyVaultOboTokenScope = "https://vault.azure.net/.default";
        public const string AzureStorageOboTokenScope = "https://storage.azure.com/.default";
        public const string AppInsightsTokenScope = "https://api.applicationinsights.io/.default";
    }
}
