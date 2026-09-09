using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;

namespace Agents.AI.ContactCenter.Tests.Authentication;

public sealed class CredentialCaptureTests
{
    [Fact]
    public void AnyOf_SelectsRequestedMethod_AndDoesNotTreatChoiceAsCredential()
    {
        var stage = new CompiledStage(new StageBlueprint { Id = "verify" }, [], []);
        var capture = new CredentialCapture();
        var prompt = capture.Begin(new AuthStepRender(stage, 0,
        [
            new() { AuthenticatorName = "Pin", Kind = CredentialKind.Digits, Purpose = "PIN", MinLength = 4, MaxLength = 4, Secret = true },
            new() { AuthenticatorName = "SmsOtp", Kind = CredentialKind.OutOfBandCode, Purpose = "OTP", MinLength = 6, MaxLength = 6, Secret = true },
        ], null));
        Assert.Contains("Press 2", prompt);
        Assert.Null(capture.Accept('2').Input);
        foreach (var digit in "12345") { Assert.Null(capture.Accept(digit).Input); }
        var result = capture.Accept('6');
        Assert.Equal("SmsOtp", result.AuthenticatorName);
        Assert.Equal("123456", result.Input?.Value);
        var bufferedCredentialAudioTime = DateTimeOffset.UtcNow.AddSeconds(-1);
        capture.End();
        Assert.False(capture.IsActive);
        Assert.True(capture.SuppressesAudio(bufferedCredentialAudioTime));
        Assert.False(capture.SuppressesAudio(DateTimeOffset.UtcNow.AddSeconds(1)));
        Assert.Throws<InvalidOperationException>(() => capture.Accept('1'));
    }
}
