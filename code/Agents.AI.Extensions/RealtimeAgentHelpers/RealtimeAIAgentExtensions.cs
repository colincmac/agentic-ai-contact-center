using System.Text.RegularExpressions;
using Agents.AI.Extensions.AITools;
using Agents.AI.Extensions.RealtimeAgentHelpers;
using Agents.AI.Extensions.RealtimeAgentHelpers.Prompting;
using Agents.AI.Extensions.SessionManagement;
using Agents.AI.Realtime;
using Microsoft.Agents.AI;
namespace Agents.AI.Extensions;


public static partial class RealtimeAIAgentExtensions
{
    [GeneratedRegex("[^0-9A-Za-z]+")]
    private static partial Regex InvalidNameCharsRegex();

    public static AuthorizingRealtimeAIAgent AsAuthorizingAgent(this RealtimeAIAgent agent, IAgentSessionRegistry agentSessionRegistry, IEnumerable<IAIToolCollection>? toolCollections = null, AgentFunctionInvocationMiddleware? middleware = null, IServiceProvider? scopedServices = null)
    {
        return new AuthorizingRealtimeAIAgent(agent, agentSessionRegistry, middleware, toolCollections, scopedServices);
    }

    public static string GetDescriptiveId(this AIAgent agent)
    {
        string id = string.IsNullOrEmpty(agent.Name) ? agent.Id : $"{agent.Name}_{agent.Id}";
        return InvalidNameCharsRegex().Replace(id, "_");
    }
}
