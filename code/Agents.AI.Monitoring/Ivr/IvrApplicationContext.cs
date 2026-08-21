namespace Agents.AI.Monitoring.Ivr;


public sealed record IvrApplicationContext
{
    public required string ContextId { get; init; }

    /// <summary>IVR application name (e.g. "ivr-hello-world").</summary>
    public required string ApplicationName { get; init; }

    /// <summary>IVR application version (e.g. "1.0.0").</summary>
    public required string ApplicationVersion { get; init; }
}
