
using Agents.AI.ContactCenter.IvrWorkflow;

namespace Agents.AI.ContactCenter.Authorization.FraudCheck;

public interface IFraudDetectionMonitor : IAsyncDisposable
{
    Task<FraudAssessment> AnalyzeTurnAsync(string sessionId, RealtimeConversationTurn turn, CancellationToken cancellationToken = default);
    void ClearAssessment(string sessionId);
    FraudAssessment? GetAssessment(string sessionId);
}
