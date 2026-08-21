using Agents.AI.ContactCenter.Calling.Strategies.Dtmf;

namespace Agents.AI.ContactCenter.Tests.Calling.Strategies.Nlu;

/// <summary>
/// Focused tests for the SSML detection branch used by the call-workflow strategies to
/// decide whether a prompt should be sent to the synthesizer as <c>SynthesizerInputFormat.SSML</c>
/// or <c>SynthesizerInputFormat.Text</c>. Lives on <see cref="DtmfCallWorkflowStrategy"/>
/// after the Phase-5 cutover (the legacy <c>NluConversationStrategy.LooksLikeSsml</c> is gone).
/// </summary>
public class CallWorkflowStrategySsmlDetectionTests
{
    [Theory]
    [InlineData("<speak version=\"1.0\">hi</speak>", true)]
    [InlineData("   <speak>hi</speak>", true)]
    [InlineData("<SPEAK>hi</SPEAK>", true)]
    [InlineData("Just plain text.", false)]
    [InlineData("<voice>hi</voice>", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LooksLikeSsml_recognizes_speak_root_element(string? input, bool expected)
    {
        Assert.Equal(expected, DtmfCallWorkflowStrategy.LooksLikeSsml(input));
    }
}
