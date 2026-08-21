namespace Agents.AI.ContactCenter.Calling;

/// <summary>
/// Optional capability surface implemented by call edges that can issue platform-level
/// call-control verbs (hang up, transfer). Distinct from <see cref="ICallEdge"/> so
/// non-platform edges (synthetic test edges, future browser-side supervisor edges)
/// don't need to implement these.
/// </summary>
/// <remarks>
/// The <see cref="ICallSession"/> queries its caller edge for this interface and
/// delegates session-level transfer / hang-up requests to it. AI-callable tools
/// (see <c>CallControlTools</c>) reach the same surface through the session.
/// </remarks>
public interface ICallControl
{
    /// <summary>Whether call-control verbs are currently supported on this edge.</summary>
    bool CanControl { get; }

    /// <summary>Hang up the call leg this edge owns.</summary>
    /// <param name="hangUpForEveryone">
    /// When <see langword="true"/>, ends the call for all participants. When <see langword="false"/>,
    /// only removes this leg.
    /// </param>
    Task HangUpAsync(bool hangUpForEveryone, CancellationToken cancellationToken = default);

    /// <summary>
    /// Transfer the call leg to <paramref name="request"/>. Implementations issue the
    /// platform-level transfer and return immediately; the actual disconnect of the
    /// edge will arrive via the platform's normal disconnect callback.
    /// </summary>
    Task TransferAsync(TransferRequest request, CancellationToken cancellationToken = default);
}
