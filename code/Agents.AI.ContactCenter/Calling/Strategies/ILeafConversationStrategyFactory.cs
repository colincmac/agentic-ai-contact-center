namespace Agents.AI.ContactCenter.Calling.Strategies;

internal interface ILeafConversationStrategyFactory
{
    IConversationStrategy Create();
}

internal sealed class LeafConversationStrategyFactory(Func<IConversationStrategy> factory)
    : ILeafConversationStrategyFactory
{
    public IConversationStrategy Create() => factory();
}
