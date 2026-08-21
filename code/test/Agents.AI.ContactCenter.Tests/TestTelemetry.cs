using Agents.AI.ContactCenter.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

internal static class TestTelemetry
{
    public static CallingTelemetry Calling { get; } = new();
    public static ILoggerFactory LoggerFactory => NullLoggerFactory.Instance;
    public static ILogger<T> LoggerFor<T>() => NullLogger<T>.Instance;
}

