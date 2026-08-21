using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using Extensions.AI.AudioHelpers;
using Microsoft.Extensions.AI;

namespace Extensions.AI.Contents;

public class DtmfToneContent : AIContent
{
    public const string DtmfToneMediaType = "application/dtmf-tone";
    public DtmfToneContent(DtmfTone tone)
    {
        Tone = tone;
    }

    public DtmfTone Tone { get; }
}

