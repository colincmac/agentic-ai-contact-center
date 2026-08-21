using Agents.AI.ContactCenter.Azure;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.Media.Audio.Resilience;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.DependencyInjection;

/// <summary>
/// Extension methods for registering Azure Speech services with dependency injection.
/// </summary>
/// <remarks>
/// The registered <see cref="ISpeechRecognizer"/> and <see cref="ISpeechSynthesizer"/>
/// are the resilient decorators (<see cref="ResilientSpeechRecognizer"/> /
/// <see cref="ResilientSpeechSynthesizer"/>) wrapping one
/// <see cref="AzureSpeechService"/> per entry in
/// <see cref="AzureSpeechServiceOptions.Endpoints"/>. With a single endpoint the
/// decorators still apply Timeout/Retry/CircuitBreaker; Fallback only kicks in
/// when more than one endpoint is configured.
/// </remarks>
public static class AzureSpeechServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Azure Speech Service with the service collection as both
    /// <see cref="ISpeechRecognizer"/> and <see cref="ISpeechSynthesizer"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration section containing Azure Speech options (defaults to "AzureSpeech").</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAzureSpeech(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        return services.AddAzureSpeech(configuration.GetSection(AzureSpeechServiceOptions.SectionName));
    }

    /// <summary>
    /// Registers the Azure Speech Service with the service collection as both
    /// <see cref="ISpeechRecognizer"/> and <see cref="ISpeechSynthesizer"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configurationSection">Configuration section containing Azure Speech options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAzureSpeech(
        this IServiceCollection services,
        IConfigurationSection configurationSection,
        Action<AzureSpeechServiceOptions>? configure = null)
    {
        var optionsBuilder = services.AddOptions<AzureSpeechServiceOptions>()
            .Bind(configurationSection)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        if (configure != null)
        {
            optionsBuilder.Configure(configure);
        }

        RegisterResilientPipeline(services);

        return services;
    }

    /// <summary>
    /// Registers the Azure Speech Service with the service collection using a configuration delegate.
    /// Service is registered as both <see cref="ISpeechRecognizer"/> and <see cref="ISpeechSynthesizer"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">A delegate to configure Azure Speech options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAzureSpeech(
        this IServiceCollection services,
        Action<AzureSpeechServiceOptions> configure)
    {
        services.AddOptions<AzureSpeechServiceOptions>()
            .Configure(configure)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        RegisterResilientPipeline(services);

        return services;
    }

    /// <summary>
    /// Registers the Azure Speech Service with explicit options.
    /// Service is registered as both <see cref="ISpeechRecognizer"/> and <see cref="ISpeechSynthesizer"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">The Azure Speech options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAzureSpeech(
        this IServiceCollection services,
        AzureSpeechServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        services.AddOptions<AzureSpeechServiceOptions>()
            .Configure(o =>
            {
                o.Endpoint = options.Endpoint;
                o.Credential = options.Credential;
                o.Endpoints.Clear();
                foreach (var ep in options.Endpoints)
                {
                    o.Endpoints.Add(ep);
                }

                o.RecognitionLocale = options.RecognitionLocale;
                o.SynthesisVoiceName = options.SynthesisVoiceName;
                o.SynthesisLocale = options.SynthesisLocale;
                o.SynthesisGender = options.SynthesisGender;
                o.OutputFormat = options.OutputFormat;
                o.Concurrency = options.Concurrency;
                o.MaximumRetainedCapacity = options.MaximumRetainedCapacity;
                o.Resilience = options.Resilience;
            });

        RegisterResilientPipeline(services);

        return services;
    }

    private static void RegisterResilientPipeline(IServiceCollection services)
    {
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AzureSpeechServiceOptions>, AzureSpeechServiceOptionsValidator>());

        services.TryAddSingleton<AzureSpeechServiceEndpointRegistry>();

        services.TryAddSingleton<ResilientSpeechSynthesizer>(static sp =>
        {
            var registry = sp.GetRequiredService<AzureSpeechServiceEndpointRegistry>();
            var options = sp.GetRequiredService<IOptions<AzureSpeechServiceOptions>>().Value;
            var logger = sp.GetService<ILogger<ResilientSpeechSynthesizer>>();

            var endpoints = registry.Services
                .Select(entry => (entry.Name, (ISpeechSynthesizer)entry.Service))
                .ToArray();

            return new ResilientSpeechSynthesizer(endpoints, options.Resilience, logger);
        });

        services.TryAddSingleton<ResilientSpeechRecognizer>(static sp =>
        {
            var registry = sp.GetRequiredService<AzureSpeechServiceEndpointRegistry>();
            var options = sp.GetRequiredService<IOptions<AzureSpeechServiceOptions>>().Value;
            var logger = sp.GetService<ILogger<ResilientSpeechRecognizer>>();

            var endpoints = registry.Services
                .Select(entry => (entry.Name, new Func<ISpeechRecognizer>(() => entry.Service.CreateRecognizer())))
                .ToArray();

            return new ResilientSpeechRecognizer(endpoints, options.Resilience, logger);
        });

        services.TryAddSingleton<ISpeechSynthesizer>(static sp => sp.GetRequiredService<ResilientSpeechSynthesizer>());
        services.TryAddSingleton<ISpeechRecognizer>(static sp => sp.GetRequiredService<ResilientSpeechRecognizer>());
    }

    /// <summary>
    /// Internal registry that materializes one <see cref="AzureSpeechService"/>
    /// instance per configured endpoint. Resolved lazily so options validation
    /// runs first.
    /// </summary>
    internal sealed class AzureSpeechServiceEndpointRegistry
    {
        public AzureSpeechServiceEndpointRegistry(
            IOptions<AzureSpeechServiceOptions> options,
            ILoggerFactory loggerFactory)
        {
            var value = options.Value;
            Services = value.Endpoints
                .Select(endpoint => new EndpointEntry(
                    endpoint.Name ?? endpoint.Endpoint.ToString(),
                    new AzureSpeechService(value, endpoint, loggerFactory.CreateLogger<AzureSpeechService>())))
                .ToArray();
        }

        public IReadOnlyList<EndpointEntry> Services { get; }

        internal sealed record EndpointEntry(string Name, AzureSpeechService Service);
    }
}

