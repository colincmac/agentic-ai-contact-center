using System.Collections.Concurrent;

namespace Agents.AI.ContactCenter.Media.Analysis;

/// <summary>
/// Correlates text-derived sentiment with audio-derived emotion signals
/// over a sliding window to detect divergence.
/// <para>
/// Session-scoped — each conversation gets its own correlator instance.
/// The correlator is fed from two independent streams (audio analysis + transcript
/// sentiment) and produces a <see cref="ConversationSignalAnalysis"/> when
/// both signals are available for a given window.
/// </para>
/// </summary>
public sealed class CrossSignalCorrelator
{
    private readonly ConcurrentQueue<EmotionSignal> _audioSignals = new();
    private readonly ConcurrentQueue<(DateTimeOffset Timestamp, double Sentiment)> _textSignals = new();
    private readonly int _maxWindowSize;
    private readonly double _divergenceThreshold;

    public CrossSignalCorrelator(
        int maxWindowSize = 20,
        double divergenceThreshold = ConversationSignalAnalysis.DivergenceThreshold)
    {
        _maxWindowSize = maxWindowSize;
        _divergenceThreshold = divergenceThreshold;
    }

    /// <summary>
    /// Records an audio emotion signal from the analysis pipeline.
    /// </summary>
    public void RecordAudioEmotion(EmotionSignal emotion)
    {
        _audioSignals.Enqueue(emotion);
        TrimQueue(_audioSignals);
    }

    /// <summary>
    /// Records a text sentiment score from the transcript analyzer.
    /// </summary>
    public void RecordTextSentiment(double sentiment)
    {
        _textSignals.Enqueue((DateTimeOffset.UtcNow, sentiment));
        TrimQueue(_textSignals);
    }

    /// <summary>
    /// Computes the current cross-signal analysis by comparing recent
    /// audio emotion valence against recent text sentiment.
    /// </summary>
    /// <returns>
    /// A <see cref="ConversationSignalAnalysis"/> with divergence computed,
    /// or null if insufficient data from either signal source.
    /// </returns>
    public ConversationSignalAnalysis? Evaluate(AudioAnalysisResult? latestAudio = null)
    {
        var recentAudio = _audioSignals.ToArray();
        var recentText = _textSignals.ToArray();

        if (recentAudio.Length == 0 || recentText.Length == 0)
        {
            return null;
        }

        // Average recent signals for a stable comparison
        var avgAudioValence = recentAudio
            .TakeLast(5)
            .Average(e => e.ValenceScore);

        var avgTextSentiment = recentText
            .TakeLast(5)
            .Average(t => t.Sentiment);

        var divergence = Math.Abs(avgTextSentiment - avgAudioValence);
        var isDivergent = divergence >= _divergenceThreshold;

        string? description = isDivergent
            ? BuildDivergenceDescription(avgTextSentiment, avgAudioValence, recentAudio[^1])
            : null;

        var latestEmotion = recentAudio.Length > 0 ? recentAudio[^1] : null;

        return new ConversationSignalAnalysis
        {
            AudioEmotion = latestEmotion,
            SpeechRate = latestAudio?.SpeechRate,
            StressLevel = latestAudio?.StressLevel,
            TextSentiment = avgTextSentiment,
            Divergence = divergence,
            IsDivergent = isDivergent,
            DivergenceDescription = description,
            WindowDuration = ComputeWindowDuration(recentAudio, recentText)
        };
    }

    /// <summary>
    /// Gets a snapshot of whether signals are currently diverging.
    /// Cheap check for use in routing/escalation decisions.
    /// </summary>
    public bool IsCurrentlyDivergent
    {
        get
        {
            var audio = _audioSignals.ToArray();
            var text = _textSignals.ToArray();

            if (audio.Length < 2 || text.Length < 2)
            {
                return false;
            }

            var avgValence = audio.TakeLast(3).Average(e => e.ValenceScore);
            var avgSentiment = text.TakeLast(3).Average(t => t.Sentiment);

            return Math.Abs(avgSentiment - avgValence) >= _divergenceThreshold;
        }
    }

    private static string BuildDivergenceDescription(
        double textSentiment, double audioValence, EmotionSignal latestEmotion)
    {
        var textLabel = textSentiment switch
        {
            > 0.3 => "positive",
            < -0.3 => "negative",
            _ => "neutral"
        };

        return $"Text sentiment is {textLabel} ({textSentiment:F2}) but voice emotion " +
               $"is '{latestEmotion.Label}' (valence {audioValence:F2}). " +
               $"This may indicate masked frustration, sarcasm, or emotional suppression.";
    }

    private static TimeSpan ComputeWindowDuration(
        EmotionSignal[] audio,
        (DateTimeOffset Timestamp, double Sentiment)[] text)
    {
        var earliest = DateTimeOffset.UtcNow;
        if (audio.Length > 0 && audio[0].Timestamp < earliest)
        {
            earliest = audio[0].Timestamp;
        }

        if (text.Length > 0 && text[0].Timestamp < earliest)
        {
            earliest = text[0].Timestamp;
        }

        return DateTimeOffset.UtcNow - earliest;
    }

    private void TrimQueue<T>(ConcurrentQueue<T> queue)
    {
        while (queue.Count > _maxWindowSize)
        {
            queue.TryDequeue(out _);
        }
    }
}
