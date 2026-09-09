namespace Agents.AI.ContactCenter.Calling;


/// <summary>Portable, transport-agnostic facts about an incoming call. Drives id, routing, and caller auth.</summary>
public sealed record IncomingCallContext
{
    public required string CallId { get; init; }
    public required string CallerIdentifier { get; init; }   // ANI
    public required string CallTargetIdentifier { get; init; }   // DNIS / called number
    public string? CallerDisplayName { get; init; } = null;
    public string? CorrelationId { get; init; }
    public string? Locale { get; init; }
    public IReadOnlyDictionary<string, object?>? CustomContext { get; init; }
}
