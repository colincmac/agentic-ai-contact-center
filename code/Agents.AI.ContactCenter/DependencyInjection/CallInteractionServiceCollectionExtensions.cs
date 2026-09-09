using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.DependencyInjection;

public static class CallInteractionServiceCollectionExtensions
{
    public static IServiceCollection AddCallInteractionProfiles(this IServiceCollection services, IConfigurationSection section)
    {
        services.AddOptions<CallInteractionOptions>().Bind(section).ValidateOnStart();
        services.TryAddSingleton<CallInteractionPolicy>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CallInteractionOptions>, CallInteractionOptionsValidator>());
        services.AddOptions<AgentTierOptions>().Configure<IOptions<CallInteractionOptions>>((tiers, configured) =>
        {
            if (configured.Value.Profiles.Count == 0) { return; }
            tiers.FallbackOrder = configured.Value.Profiles.Where(p => p.Enabled).Select(p => p.Tier).ToList();
            tiers.Tiers = configured.Value.Profiles.ToDictionary(p => p.Tier,
                p => new AgentTierConfig { Enabled = p.Enabled, MaxConcurrent = p.MaxConcurrent });
            tiers.AllowMidCallDegradation = configured.Value.AllowMidCallDegradation;
        });
        return services;
    }
}
