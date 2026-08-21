using Microsoft.Extensions.Logging;

namespace Showcase.AgentFramework.LiveVoice.Client;

internal static partial class RealtimeLogger
{

    [LoggerMessage(Level = LogLevel.Debug, Message = "Starting realtime conversation session with\nProvider: {provider},  Model: {model}")]
    public static partial void LogStartingRealtimeConversationSession(this ILogger logger, string? model, string? provider);

    [LoggerMessage(Level = LogLevel.Information, Message = "Started Realtime conversation session with\nProvider: {provider},  Model: {model}, ID: {id}")]
    public static partial void LogRealtimeConversationSessionStarted(this ILogger logger,  string? model, string? id, string? provider);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed starting Realtime conversation session with\nProvider: {provider},  Model: {model}")]
    public static partial void LogRealtimeConversationSessionFailedToStart(this ILogger logger, string? model, string? provider, Exception? innerException);

}
