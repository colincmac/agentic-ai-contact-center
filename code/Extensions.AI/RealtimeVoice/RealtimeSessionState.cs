namespace Extensions.AI.RealtimeVoice;

/// <summary>Event args for session state changes.</summary>
public sealed class RealtimeSessionStateChangedEventArgs : EventArgs
{
    /// <summary>Gets the previous state.</summary>
    public required RealtimeSessionState PreviousState { get; init; }

    /// <summary>Gets the new state.</summary>
    public required RealtimeSessionState NewState { get; init; }

    /// <summary>Gets error information if applicable.</summary>
    public Microsoft.Extensions.AI.ErrorContent? Error { get; init; }

    /// <summary>Gets the reason for the state change if applicable.</summary>
    public string? Reason { get; init; } = string.Empty;
}
/// <summary>Represents the state of a realtime session.</summary>
public enum RealtimeSessionState
{
    /// <summary>Session has not begun connecting.</summary>
    None,

    /// <summary>Session is being created.</summary>
    Connecting,

    /// <summary>Session is connected and ready.</summary>
    Connected,

    /// <summary>Session encountered an error.</summary>
    Error,

    /// <summary>Session is being closed.</summary>
    Closing,

    /// <summary>Session is closed.</summary>
    Closed
}
