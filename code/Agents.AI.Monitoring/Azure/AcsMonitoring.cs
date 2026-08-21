namespace Agents.AI.Monitoring.Azure;

/// <summary>
/// Constants for landing ACS telemetry in Azure Monitor / Log Analytics and joining it in
/// Grafana. Category names are the ACS diagnostic-setting log categories to enable; table
/// names are the resulting Log Analytics tables the KQL assets query. Referenced by the
/// AppHost provisioning wiring (thin call site) and by the dashboard queries.
/// </summary>
public static class AcsMonitoring
{
    /// <summary>ACS diagnostic-setting log categories to enable for Call Automation correlation.</summary>
    public static class DiagnosticCategories
    {
        /// <summary>Call Automation API request / operational logs.</summary>
        public const string CallAutomationOperational = "CallAutomationOperationalLogs";

        /// <summary>Call Automation media / events summary logs.</summary>
        public const string CallAutomationEventsSummary = "CallAutomationMediaSummaryLogs";

        /// <summary>Media streaming / transcription usage logs.</summary>
        public const string CallAutomationMediaStreaming = "CallAutomationMediaStreamingUsageLogs";

        /// <summary>All categories that should be enabled for end-to-end call correlation.</summary>
        public static readonly IReadOnlyList<string> All =
        [
            CallAutomationOperational,
            CallAutomationEventsSummary,
            CallAutomationMediaStreaming,
        ];
    }

    /// <summary>Log Analytics table names produced by the ACS diagnostic settings.</summary>
    public static class Tables
    {
        /// <summary>Call Automation incoming operations (holds CorrelationId, CallConnectionId, ServerCallId, OperationId).</summary>
        public const string CallAutomationIncomingOperations = "ACSCallAutomationIncomingOperations";

        /// <summary>Call Automation media summary.</summary>
        public const string CallAutomationMediaSummary = "ACSCallAutomationMediaSummary";
    }

    /// <summary>
    /// ACS Call Automation column that is stable for the duration of a connection. Prefer this
    /// over CorrelationId (which can change mid-call) when joining IVR spans to ACS logs.
    /// </summary>
    public const string StableJoinColumn = "CallConnectionId";
}
