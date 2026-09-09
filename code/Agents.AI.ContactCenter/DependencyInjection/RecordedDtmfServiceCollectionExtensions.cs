using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Calling.Strategies;
using Agents.AI.ContactCenter.Calling.Strategies.Dtmf;
using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.DependencyInjection;

public static class RecordedDtmfServiceCollectionExtensions
{
    public static CallSessionContainerBuilder AddRecordedDtmfCallWorkflowStrategy(this CallSessionContainerBuilder builder)
    {
        builder.Services.AddCallWorkflowFramework();
        builder.Services.AddKeyedTransient<ILeafConversationStrategyFactory>(AgentTier.DtmfOnly,
            (sp, _) => new LeafConversationStrategyFactory(() => ActivatorUtilities.CreateInstance<RecordedDtmfCallWorkflowStrategy>(sp)));
        builder.Services.AddKeyedTransient<IConversationStrategy>(AgentTier.DtmfOnly,
            (sp, _) => sp.GetRequiredKeyedService<ILeafConversationStrategyFactory>(AgentTier.DtmfOnly).Create());
        return builder;
    }
}
