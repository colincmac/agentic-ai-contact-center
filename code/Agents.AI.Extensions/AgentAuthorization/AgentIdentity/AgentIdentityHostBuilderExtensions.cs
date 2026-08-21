using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Identity.Abstractions;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.TokenCacheProviders.InMemory;
using Microsoft.Shared.Diagnostics;

namespace Agents.AI.Extensions.AgentAuthorization.AgentIdentity;

public static class AgentIdentityExtensions
{
    public const string DefaultAgentManagementEntraConfigurationSection = "AgentManagementEntraConfiguration";
    public const string DefaultDownstreamApisConfigurationSection = "AgentManagementDownstreamApis";
    public const string AgentManagementScheme = "AgentBlueprintBearer";

    public const string DefaultAgentIdentityEntraConfigurationSection = "AgentIdentityEntraConfiguration";

    public static void AddAgentIdentityManagement(this IHostApplicationBuilder builder, string entraConfigurationSection = DefaultAgentManagementEntraConfigurationSection, string downstreamApisConfigurationSection = DefaultDownstreamApisConfigurationSection)
    {
        builder.Services.AddMicrosoftIdentityWebApiAuthentication(builder.Configuration, entraConfigurationSection, AgentManagementScheme, true) // Note that we don't provide the default scheme (there is only one default)
            .EnableTokenAcquisitionToCallDownstreamApi();
        builder.Services.AddDownstreamApis(builder.Configuration.GetSection(downstreamApisConfigurationSection));
    }
    public static void AddAgentIdentity(this IHostApplicationBuilder builder, string entraConfigurationSection = DefaultAgentIdentityEntraConfigurationSection, string downstreamApisConfigurationSection = DefaultDownstreamApisConfigurationSection)
    {
        builder.Services.AddTokenAcquisition();
        builder.Services.AddInMemoryTokenCaches();
        builder.Services.AddHttpClient();

        builder.Services.AddAuthentication() // Note that we don't provide the default scheme (there is only one default)
            .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection(entraConfigurationSection), AgentManagementScheme)
            .EnableTokenAcquisitionToCallDownstreamApi();
        builder.Services.AddDownstreamApis(builder.Configuration.GetSection(downstreamApisConfigurationSection));
    }
    /// <summary>
    /// Enable support for agent identities.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns>The service collection for chaining.</returns>
    //public static IServiceCollection AddAgentIdentities(this IServiceCollection services)
    //{
    //    Throws.IfNull(services);

    //     Register the OidcFic services for agent applications to work.
    //    services.AddOidcFic();

    //     Register a callback to process the agent user identity before acquiring a token.
    //    services.Configure<TokenAcquisitionExtensionOptions>(options =>
    //    {
    //        options.OnBeforeTokenAcquisitionForTestUserAsync += AgentUserIdentityMsalAddIn.OnBeforeUserFicForAgentUserIdentityAsync;
    //    });

    //    return services;
    //}

    /// <summary>
    /// Updates the options to acquire a token for the agent identity.
    /// </summary>
    /// <param name="options">Authorization header provider options.</param>
    /// <param name="agentApplicationId">The agent identity GUID.</param>
    /// <returns>The updated authorization header provider options.</returns>
    //public static AuthorizationHeaderProviderOptions WithAgentIdentity(this AuthorizationHeaderProviderOptions options, string agentApplicationId)
    //{
    //     It's possible to start with no options, so we initialize it if it's null.
    //    if (options == null)
    //        options = new AuthorizationHeaderProviderOptions();

    //     AcquireTokenOptions holds the information needed to acquire a token for the Agent Identity
    //    options.AcquireTokenOptions ??= new AcquireTokenOptions();
    //    options.AcquireTokenOptions.ForAgentIdentity(agentApplicationId);

    //    return options;
    //}

    /// <summary>
    /// Updates the options to acquire a token for the agent user identity.
    /// </summary>
    /// <param name="options">Authorization header provider options.</param>
    /// <param name="agentApplicationId">The agent identity GUID.</param>
    /// <param name="username">UPN of the user.</param>
    /// <returns>The updated authorization header provider options (in place. not a clone of the options).</returns>
    //public static AuthorizationHeaderProviderOptions WithAgentUserIdentity(this AuthorizationHeaderProviderOptions options, string agentApplicationId, string username)
    //{
    //    options ??= new AuthorizationHeaderProviderOptions();
    //    options.AcquireTokenOptions ??= new AcquireTokenOptions();
    //    options.AcquireTokenOptions.ExtraParameters ??= new Dictionary<string, object>();

    //     Set the agent application options
    //    options.AcquireTokenOptions.ExtraParameters[Constants.MicrosoftIdentityOptionsParameter] = new MicrosoftEntraApplicationOptions
    //    {
    //        ClientId = agentApplicationId, // Agent identity Client ID.
    //    };

    //     Set the username and agent identity parameters
    //    options.AcquireTokenOptions.ExtraParameters[Constants.UsernameKey] = username;
    //    options.AcquireTokenOptions.ExtraParameters[Constants.AgentIdentityKey] = agentApplicationId;

    //    return options;
    //}

    /// <summary>
    /// Updates the options to acquire a token for the agent user identity using the user's object id (OID).
    /// </summary>
    /// <param name="options">Authorization header provider options.</param>
    /// <param name="agentApplicationId">The agent identity application (client) ID.</param>
    /// <param name="userId">The user's object id (OID).</param>
    /// <returns>The updated authorization header provider options (in place; not a clone).</returns>
    /// <remarks>
    /// If both a UPN and an OID are present in the options (not expected via the public API), UPN takes precedence.
    /// </remarks>
    //public static AuthorizationHeaderProviderOptions WithAgentUserIdentity(this AuthorizationHeaderProviderOptions options, string agentApplicationId, Guid userId)
    //{
    //    options ??= new AuthorizationHeaderProviderOptions();
    //    options.AcquireTokenOptions ??= new AcquireTokenOptions();
    //    options.AcquireTokenOptions.ExtraParameters ??= new Dictionary<string, object>();

    //     Configure the agent application
    //    options.AcquireTokenOptions.ExtraParameters[Constants.MicrosoftIdentityOptionsParameter] = new MicrosoftEntraApplicationOptions
    //    {
    //        ClientId = agentApplicationId,
    //    };

    //     Identity selection via OID
    //    options.AcquireTokenOptions.ExtraParameters[Constants.AgentIdentityKey] = agentApplicationId;
    //    options.AcquireTokenOptions.ExtraParameters[Constants.UserIdKey] = userId.ToString("D");

    //    return options;
    //}

    // TODO:would it make sense to have it public?
    //internal static AcquireTokenOptions ForAgentIdentity(this AcquireTokenOptions options, string agentApplicationId)
    //{
    //    options.ExtraParameters ??= new Dictionary<string, object>();

    //     Until it makes it way through Abstractions
    //    options.ExtraParameters[Constants.FmiPathForClientAssertion] = agentApplicationId;

    //     TODO: do we want to expose a mechanism to override the MicrosoftIdentityOptions instead of leveraging
    //     the default configuration section / named options?.
    //    options.ExtraParameters[Constants.MicrosoftIdentityOptionsParameter] = new MicrosoftEntraApplicationOptions
    //    {
    //        ClientId = agentApplicationId, // Agent identity Client ID.
    //        ClientCredentials = [ new CredentialDescription() {
    //                SourceType = CredentialSource.CustomSignedAssertion,
    //                CustomSignedAssertionProviderName = "OidcIdpSignedAssertion",
    //                CustomSignedAssertionProviderData = new Dictionary<string, object> {
    //                    { "ConfigurationSection", "AzureAd" },      // Use the default configuration section name
    //                    { "RequiresSignedAssertionFmiPath", true }, // The OidcIdpSignedAssertionProvider will require the fmiPath to be provided in the assertionRequestOptions.
    //                }
    //            }]
    //    };
    //    return options;
    //}

}
