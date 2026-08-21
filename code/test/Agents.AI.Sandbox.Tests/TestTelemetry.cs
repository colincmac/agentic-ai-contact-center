using Agents.AI.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

internal static class TestTelemetry
{
    public static SandboxTelemetry Sandbox { get; } = new();
    public static ILoggerFactory LoggerFactory => NullLoggerFactory.Instance;
    public static ILogger<T> LoggerFor<T>() => NullLogger<T>.Instance;
}

