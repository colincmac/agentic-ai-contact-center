using System;
using System.Collections.Generic;
using System.Text;
using Extensions.AI.RealtimeVoice.Configuration;
using Microsoft.Extensions.AI;

namespace Extensions.AI.RealtimeVoice;

public static class ChatOptionsExtensions
{
    public static LiveConversationSessionOptions AsConversationSessionOptions(this ChatOptions chatOptions) => new (chatOptions as LiveConversationSessionOptions);
}

