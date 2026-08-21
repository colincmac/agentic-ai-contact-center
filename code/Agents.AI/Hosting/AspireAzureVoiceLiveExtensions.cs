using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Configuration;
using System.Runtime;
using System.Text;
using Aspire.Azure.AI.OpenAI;
using Aspire.Azure.Common;
using Azure;
using Azure.AI.OpenAI;
using Azure.AI.VoiceLive;
using Azure.Core;
using Azure.Core.Extensions;
using Azure.Identity;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using OpenAI;

namespace Agents.AI.Hosting;

public static class AspireAzureVoiceLiveExtensions
{
    internal const string DefaultConfigSectionName = "Aspire:Azure:AI:VoiceLive";
    /// <summary>
    /// Registers <see cref="AzureOpenAIClient"/> as a singleton in the services provided by the <paramref name="builder"/>.
    ///
    /// Additionally, registers the <see cref="AzureOpenAIClient"/> as an <see cref="OpenAIClient"/> service.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="AzureOpenAISettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureClientBuilder">An optional method that can be used for customizing the <see cref="IAzureClientBuilder{AzureOpenAIClient, AzureOpenAIClientOptions}"/>.</param>
    /// <returns>An <see cref="AspireAzureOpenAIClientBuilder"/> that can be used to register additional services.</returns>
    /// <remarks>Reads the configuration from "Aspire.Azure.AI.OpenAI" section.</remarks>
    public static AspireAzureOpenAIClientBuilder AddAzureVoiceLiveClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<AzureVoiceLiveSettings>? configureSettings = null,
        Action<IAzureClientBuilder<VoiceLiveClient, VoiceLiveClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        var settings = new AzureVoiceLiveComponent().AddClient(builder, DefaultConfigSectionName, configureSettings, configureClientBuilder, connectionName, serviceKey: null);

        return new AspireAzureOpenAIClientBuilder(builder, connectionName,
            serviceKey: null, disableTracing: settings.DisableTracing, enableSensitiveTelemetryData: settings.EnableSensitiveTelemetryData);
    }

    /// <summary>
    /// Registers <see cref="AzureOpenAIClient"/> as a singleton for given <paramref name="name"/> in the services provided by the <paramref name="builder"/>.
    ///
    /// Additionally, registers the <see cref="AzureOpenAIClient"/> as an <see cref="OpenAIClient"/> service.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="name">The name of the component, which is used as the <see cref="ServiceDescriptor.ServiceKey"/> of the service and also to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="AzureOpenAISettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureClientBuilder">An optional method that can be used for customizing the <see cref="IAzureClientBuilder{AzureOpenAIClient, OpenAIClientOptions}"/>.</param>
    /// <returns>An <see cref="AspireAzureOpenAIClientBuilder"/> that can be used to register additional services.</returns>
    /// <remarks>Reads the configuration from "Aspire.Azure.AI.OpenAI:{name}" section.</remarks>
    public static AspireAzureOpenAIClientBuilder AddKeyedAzureVoiceLiveClient(
        this IHostApplicationBuilder builder,
        string name,
        Action<AzureVoiceLiveSettings>? configureSettings = null,
        Action<IAzureClientBuilder<VoiceLiveClient, VoiceLiveClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var settings = new AzureVoiceLiveComponent().AddClient(builder, DefaultConfigSectionName, configureSettings, configureClientBuilder, connectionName: name, serviceKey: name);

        return new AspireAzureOpenAIClientBuilder(builder, name, name,
            disableTracing: settings.DisableTracing, enableSensitiveTelemetryData: settings.EnableSensitiveTelemetryData);
    }

    public class AzureVoiceLiveComponent
    {

        protected IAzureClientBuilder<VoiceLiveClient, VoiceLiveClientOptions> AddClient(
            AzureClientFactoryBuilder azureFactoryBuilder,
            AzureVoiceLiveSettings settings,
            string connectionName,
            string configurationSectionName)
        {
            return azureFactoryBuilder.AddClient<VoiceLiveClient, VoiceLiveClientOptions>((options, _, _) =>
            {
                if (settings.Endpoint is null)
                {
                    throw new InvalidOperationException($"A Voice Live client could not be configured. Ensure valid connection information was provided in 'ConnectionStrings:{connectionName}' or specify a '{nameof(AzureOpenAISettings.Endpoint)}' or '{nameof(AzureOpenAISettings.Key)}' in the '{configurationSectionName}' configuration section.");
                }
                else
                {
                    // Connect to Azure OpenAI
                    if (!string.IsNullOrEmpty(settings.Key))
                    {
                        var credential = new AzureKeyCredential(settings.Key);
                        return new VoiceLiveClient(settings.Endpoint, credential, options);
                    }
                    else
                    {

                        return new VoiceLiveClient(settings.Endpoint, settings.Credential ?? new AzureCliCredential(), options);
                    }
                }
            });
        }

        public AzureVoiceLiveSettings AddClient(IHostApplicationBuilder builder,
            string configurationSectionName,
            Action<AzureVoiceLiveSettings>? configureSettings,
            Action<IAzureClientBuilder<VoiceLiveClient, VoiceLiveClientOptions>>? configureClientBuilder,
            string connectionName,
            string? serviceKey)
        {

            var configSection = builder.Configuration.GetSection(configurationSectionName);
            var settings = new AzureVoiceLiveSettings();
            BindSettingsToConfiguration(settings, configSection);
            BindSettingsToConfiguration(settings, configSection.GetSection(connectionName));
            // Support service key-based binding for clients that support it (e.g. WebPubSubServiceClient).
            var serviceKeySection = configSection.GetSection($"{connectionName}:{serviceKey}");
            if (serviceKeySection.Exists())
            {
                BindSettingsToConfiguration(settings, serviceKeySection);
            }
            if (builder.Configuration.GetConnectionString(connectionName) is string connectionString)
            {
                settings.ParseConnectionString(connectionString);
            }

            configureSettings?.Invoke(settings);

            if (!string.IsNullOrEmpty(serviceKey))
            {
                // When named client registration is used (.WithName), Microsoft.Extensions.Azure
                // TRIES to register a factory for given client type and later
                // a call to serviceProvider.GetService<TClient> throws InvalidOperationException:
                // "Unable to find client registration with type 'SecretClient' and name 'Default'."
                // It's not desired, as Microsoft.Extensions.DependencyInjection keyed services
                // factory methods just return null in such cases.
                // To align the behavior across the Components, a null factory is registered up-front.
                builder.Services.AddSingleton<VoiceLiveClient>(static _ => null!);
            }
            builder.Services.AddAzureClients(azureFactoryBuilder =>
            {
                var clientBuilder = AddClient(azureFactoryBuilder, settings, connectionName, configurationSectionName);

                if (GetTokenCredential(settings) is { } credential)
                {
                    clientBuilder.WithCredential(credential);
                }

                BindClientOptionsToConfiguration(clientBuilder, configSection.GetSection("ClientOptions"));
                BindClientOptionsToConfiguration(clientBuilder, configSection.GetSection($"{connectionName}:ClientOptions"));

                configureClientBuilder?.Invoke(clientBuilder);

                if (!string.IsNullOrEmpty(serviceKey))
                {
                    // Set the name for the client registration.
                    clientBuilder.WithName(serviceKey);

                    // To resolve named clients IAzureClientFactory{TClient}.CreateClient needs to be used.
                    builder.Services.AddKeyedSingleton(serviceKey,
                        static (serviceProvider, serviceKey) => serviceProvider.GetRequiredService<IAzureClientFactory<VoiceLiveClient>>().CreateClient((string)serviceKey!));
                }
            });

            //if (GetHealthCheckEnabled(settings))
            //{
            //    var namePrefix = $"Azure_{typeof(VoiceLiveClient).Name}";

            //    builder.TryAddHealthCheck(new HealthCheckRegistration(
            //        serviceKey is null ? namePrefix : $"{namePrefix}_{serviceKey}",
            //        serviceProvider =>
            //        {
            //            // From https://devblogs.microsoft.com/azure-sdk/lifetime-management-and-thread-safety-guarantees-of-azure-sdk-net-clients/:
            //            // "The main rule of Azure SDK client lifetime management is: treat clients as singletons".
            //            // So it's fine to root the client via the health check.
            //            var client = serviceKey is null
            //                ? serviceProvider.GetRequiredService<VoiceLiveClient>()
            //                : serviceProvider.GetRequiredKeyedService<VoiceLiveClient>(serviceKey);

            //            return CreateHealthCheck(client, settings);
            //        },
            //        failureStatus: default,
            //        tags: default,
            //        timeout: default));
            //}


            return settings;
        }

        protected void BindClientOptionsToConfiguration(IAzureClientBuilder<VoiceLiveClient, VoiceLiveClientOptions> clientBuilder, IConfiguration configuration)
        {
#pragma warning disable IDE0200 // Remove unnecessary lambda expression - needed so the ConfigBinder Source Generator works
            clientBuilder.ConfigureOptions(options => configuration.Bind(options));
#pragma warning restore IDE0200
        }

        protected void BindSettingsToConfiguration(AzureVoiceLiveSettings settings, IConfiguration config)
        {
            config.Bind(settings);
        }

        protected IHealthCheck CreateHealthCheck(VoiceLiveClient client, AzureVoiceLiveSettings settings)
        {
            throw new NotImplementedException();
        }

        protected bool GetHealthCheckEnabled(AzureVoiceLiveSettings settings)
        {
            return false;
        }

        protected TokenCredential? GetTokenCredential(AzureVoiceLiveSettings settings)
            => settings.Credential;

        protected bool GetMetricsEnabled(AzureVoiceLiveSettings settings)
            => !settings.DisableMetrics;

        protected bool GetTracingEnabled(AzureVoiceLiveSettings settings)
            => !settings.DisableTracing;
    }
}
