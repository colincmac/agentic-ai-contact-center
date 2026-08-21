using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Extensions.AI.RealtimeVoice.Configuration;

public class LiveConversationSessionMetadata(string modelId, string? providerName = null, Uri? providerUri = null) : LiveConversationClientMetadata(modelId, providerName, providerUri)
{


}
