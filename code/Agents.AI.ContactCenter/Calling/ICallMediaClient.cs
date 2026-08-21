using Agents.AI.ContactCenter.Calling.Core;
using Azure.Communication.CallAutomation;

namespace Agents.AI.ContactCenter.Calling;

/// <summary>
/// Tiny adapter over the bits of <see cref="CallMedia"/> that
/// <see cref="AcsCallAutomationEdge"/> uses. Exists so tests can drive the verb
/// edge without standing up a real ACS call connection.
/// </summary>
public interface ICallMediaClient
{
    Task PlayTextAsync(string text, string? voiceName, string? operationContext, CancellationToken cancellationToken);
    Task PlayFileAsync(Uri fileUri, string? operationContext, CancellationToken cancellationToken);
    Task CancelAllAsync(CancellationToken cancellationToken);
    Task RecognizeDtmfAsync(int maxTones, char? stopTone, TimeSpan? interToneTimeout, TimeSpan? initialSilenceTimeout, string? operationContext, CancellationToken cancellationToken);
}
