using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Authentication.Authenticators;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.DependencyInjection;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Agents.AI.ContactCenter.IvrWorkflow.Loading;
using Agents.AI.ContactCenter.IvrWorkflow.Predicates;
using Agents.AI.ContactCenter.Media.Audio;
using Agents.AI.ContactCenter.State;
using Agents.AI.Realtime;
using Azure.AI.VoiceLive;
using Azure.Communication.CallAutomation;
using Azure.Communication.Sms;
using Azure.Core;
using Azure.Identity;
using ContactCenter.AIAgent.Services;
using Extensions.AI.Realtime.AzureVoiceLive;
using Microsoft.CognitiveServices.Speech;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ContactCenter.AIAgent.Configuration;

public static class DemoRegistration
{
    public static BankingDemoOptions AddBankingDemo(this WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration.GetSection(BankingDemoOptions.SectionName);
        var options = configuration.Get<BankingDemoOptions>() ?? new();

        builder.Services.AddOptions<BankingDemoOptions>()
            .Bind(configuration)
            .ValidateOnStart();

        builder.Services.AddSingleton<IValidateOptions<BankingDemoOptions>, BankingDemoOptionsValidator>();
        if (!options.Enabled) 
        { 
            return options; 
        }

        //var validation = new BankingDemoOptionsValidator().Validate(null, options);

        //if (validation.Failed) 
        //{ 
        //    throw new OptionsValidationException("", typeof(BankingDemoOptions), validation.Failures); 
        //}

        TokenCredential credential = builder.Environment.IsDevelopment()
            ? new AzureCliCredential()
            : string.IsNullOrWhiteSpace(options.ManagedIdentityClientId)
                ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
                : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(options.ManagedIdentityClientId));

        builder.Services.AddSingleton(credential);

        builder.Services.AddSingleton(_ =>
        {
            var clientOptions = new CallAutomationClientOptions();
            clientOptions.Retry.MaxRetries = 0;
            clientOptions.Retry.NetworkTimeout = TimeSpan.FromSeconds(15);
            return string.IsNullOrWhiteSpace(options.AcsConnectionString)
                ? new CallAutomationClient(options.AcsEndpoint!, credential, clientOptions)
                : new CallAutomationClient(options.AcsConnectionString, clientOptions);
        });

        builder.Services.AddSingleton(_ =>
        {
            var clientOptions = new SmsClientOptions();
            clientOptions.Retry.MaxRetries = 0;
            clientOptions.Retry.NetworkTimeout = TimeSpan.FromSeconds(15);
            return string.IsNullOrWhiteSpace(options.AcsConnectionString)
                ? new SmsClient(options.AcsEndpoint!, credential, clientOptions)
                : new SmsClient(options.AcsConnectionString, clientOptions);
        });

        builder.Services.AddAzureSpeech(speech =>
        {
            speech.Endpoint = options.SpeechEndpoint;
            speech.Credential = credential;
            speech.SynthesisVoiceName = options.VoiceName;
            speech.OutputFormat = SpeechSynthesisOutputFormat.Raw24Khz16BitMonoPcm;
        });

        //builder.Services.RemoveAll<ISpeechSynthesizer>();
        //builder.Services.AddScoped<ISpeechSynthesizer, BankingPromptSynthesizer>();

        builder.Services.AddSingleton(sp =>
            new AzureVoiceLiveClient(options.VoiceLiveEndpoint!, credential, options.VoiceLiveModel)
                .AsBuilder()
                .UseFunctionInvocation(sp.GetRequiredService<ILoggerFactory>(), pipeline =>
                {
                    pipeline.IncludeDetailedErrors = false;
                    pipeline.AllowConcurrentInvocation = false;
                    pipeline.MaximumIterationsPerRequest = 8;
                }).Build());

        builder.Services.AddKeyedSingleton<RealtimeAIAgent>("bank-demo", (sp, _) =>
            new RealtimeAIAgent(sp.GetRequiredService<IRealtimeClient>(), new RealtimeAgentOptions
            {
                Name = "Banking demo",
                SessionOptions = new RealtimeSessionOptions
                {
                    Model = options.VoiceLiveModel, Voice = options.VoiceName,
                    InputAudioFormat = new RealtimeAudioFormat("audio/pcm", 24000),
                    OutputAudioFormat = new RealtimeAudioFormat("audio/pcm", 24000),
                    RawRepresentationFactory = () => new VoiceLiveSessionOptions
                    {
                        InputAudioSamplingRate = 24000,
                        TurnDetection = new ServerVadTurnDetection { CreateResponse = true, InterruptResponse = true },
                    },
                },
            }, sp.GetRequiredService<ILoggerFactory>()));

        builder.Services.AddSingleton<IDemoBankingService, DemoBankingService>();
        //builder.Services.AddSingleton<IDemoBankingService>(sp => sp.GetRequiredService<DemoBankingService>());
        builder.Services.AddSingleton<ICallerDirectory, DemoBankingService>(sp => sp.GetRequiredService<DemoBankingService>());
        builder.Services.AddSingleton<ISmsOtpSender, AcsSmsOtpSender>();
        builder.Services.AddSingleton<IAcsCallGateway, AcsCallGateway>();
        builder.Services.AddSingleton<ICallCoordinator, CallCoordinator>();
        //builder.Services.AddSingleton<ICallCoordinator>(sp => sp.GetRequiredService<CallCoordinator>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<CallCoordinator>());
        builder.Services.AddScoped<ICallObserver, VerificationTimeoutObserver>();

        builder.Services.AddScoped<ICallWorkflowAction, BalanceAction>();
        builder.Services.AddScoped<ICallWorkflowAction, ActivateCardAction>();
        builder.Services.AddScoped<ICallWorkflowAction, TransferOperatorAction>();
        builder.Services.AddScoped<ICallWorkflowAction, FinishCallAction>();

        builder.Services.AddNamedEdgePredicate("card-confirmed", sp =>
        {
            var state = sp.GetRequiredService<CallStateProjector>();
            return (_, _) => ValueTask.FromResult(state.Get<BankingSnapshot>().ActivationConfirmed
                ? EdgePredicateResult.Allow() : EdgePredicateResult.Deny("Press 1 on your keypad to confirm card activation."));
        }, ServiceLifetime.Scoped);
        builder.Services.AddCallWorkflowsFromDirectory(Path.Combine(builder.Environment.ContentRootPath, "Workflows"));
        
        builder.AddStandardContactCenter(StandardContactCenterProfile.LocalDevelopment).Advanced
            .WithCallerAuthentication(auth => auth.AddAniIdentityLookupAuthenticator().AddSmsOtpAuthenticator())
            .AddCallStateProjection<BankingProjection>()
            .AddRealtimeCallWorkflowStrategy("bank-demo")
            .AddDtmfCallWorkflowStrategy()
            .AddCompositeFallbackStrategy(AgentTier.RealtimeVoice, AgentTier.RealtimeVoice, AgentTier.DtmfOnly)
            .AddCompositeFallbackStrategy(AgentTier.DtmfOnly, AgentTier.DtmfOnly);
        return options;
    }
}
