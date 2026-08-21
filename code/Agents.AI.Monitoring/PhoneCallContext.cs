using System;
using System.Collections.Generic;
using System.Text;

namespace Agents.AI.Monitoring;

public class PhoneCallContext
{
    /// <summary> Dictionary of VOIP headers. </summary>
    public IDictionary<string, string> VoipHeaders { get; }

    /// <summary> Dictionary of SIP headers. </summary>
    public IDictionary<string, string> SipHeaders { get; }


    public PhoneCallContext(IDictionary<string, string> voipHeaders, IDictionary<string, string> sipHeaders)
    {
        VoipHeaders = voipHeaders ?? throw new ArgumentNullException(nameof(voipHeaders));
        SipHeaders = sipHeaders ?? throw new ArgumentNullException(nameof(sipHeaders));
    }
}
