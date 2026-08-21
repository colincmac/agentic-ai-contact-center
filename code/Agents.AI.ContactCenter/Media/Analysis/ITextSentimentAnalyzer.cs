namespace Agents.AI.ContactCenter.Media.Analysis;

/// <summary>
/// Extracts sentiment from transcript text. Implementations can wrap
/// Azure AI Language, a local model, or an A2A agent.
/// </summary>
public interface ITextSentimentAnalyzer
{
    /// <summary>
    /// Analyzes a transcript segment and returns a sentiment score.
    /// </summary>
    /// <returns>Sentiment score from −1.0 (negative) to +1.0 (positive), or null if insufficient text.</returns>
    Task<double?> AnalyzeSentimentAsync(string text, CancellationToken cancellationToken = default);
}
