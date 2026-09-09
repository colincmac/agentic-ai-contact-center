using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.Media.Audio.Resilience;
using Agents.AI.ContactCenter.State;
using Agents.AI.ContactCenter.State.Projections;

namespace ContactCenter.AIAgent.Services;

public sealed record BankingSnapshot(bool ActivationConfirmed = false, string? StageId = null);
public sealed class BankingProjection() : CallStateProjection<BankingSnapshot>("demo-banking", () => new())
{
    protected override BankingSnapshot Apply(BankingSnapshot current, StrategyEvent item) => item switch
    {
        StrategyEvent.WorkflowStepEntered e => current with
        {
            StageId = e.StepId,
            ActivationConfirmed = e.StepId == "confirm-card" && current.StageId != "confirm-card" ? false : current.ActivationConfirmed,
        },
        StrategyEvent.DtmfRecognized { StepId: "confirm-card", Digits: "1" } => current with { ActivationConfirmed = true },
        _ => current,
    };
}

public sealed class BankingPromptSynthesizer(ResilientSpeechSynthesizer inner, CallStateProjector state) : ISpeechSynthesizer
{
    public IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(string text, SynthesizerInputFormat inputFormat = SynthesizerInputFormat.Text,
        CancellationToken cancellationToken = default)
    {
        var data = state.Get<IvrSnapshot>().Slots;
        foreach (var key in new[] { "demo.balanceText", "demo.cardText" })
        {
            var value = data.GetValueOrDefault(key) ?? "The demo account information is unavailable.";
            text = text.Replace("{{" + key + "}}", inputFormat == SynthesizerInputFormat.SSML
                ? SecurityElement.Escape(value) : value, StringComparison.Ordinal);
        }
        return inner.SynthesizeAsync(text, inputFormat, cancellationToken);
    }
}
