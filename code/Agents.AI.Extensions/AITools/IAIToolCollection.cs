using Microsoft.Extensions.AI;

namespace Agents.AI.Extensions.AITools;

public interface IAIToolCollection
{
    IEnumerable<AITool> AsAITools();
}
