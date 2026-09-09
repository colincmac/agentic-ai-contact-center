using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Authentication.Authenticators;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Agents.AI.ContactCenter.DependencyInjection;

/// <summary>
/// DI extensions for plugging caller-authentication methods into the
/// <see cref="CallSessionContainerBuilder"/> pipeline.
/// </summary>
public static class CallerAuthenticationContainerExtensions
{
    /// <summary>
    /// Registers the per-call <see cref="CallerAuthenticationState"/> store, the default
    /// <see cref="AuthenticationOrchestrator"/>, and a <see cref="AnonymousCallerAuthenticator"/>
    /// fallback. Strategies (e.g. <c>RealtimeVoiceStrategy</c>) automatically pick up the
    /// orchestrator from DI when present.
    /// </summary>
    /// <remarks>
    /// Adding concrete authenticators is done by chaining
    /// <see cref="AddCallerAuthenticator{TAuthenticator}"/> after this method.
    /// </remarks>
    public static CallSessionContainerBuilder WithCallerAuthentication(this CallSessionContainerBuilder builder, Action<CallSessionAuthenticationBuilder>? authBuilder = null)
    {
        var services = builder.Services;

        services.TryAddScoped<IAuthenticationOrchestrator>(sp => new AuthenticationOrchestrator(
            sp.GetServices<ICallerAuthenticator>().Where(a => a is not ICredentialAuthenticator),
            sp.GetService<ILogger<AuthenticationOrchestrator>>()));
        services.TryAddScoped<ICallerElevationDispatcher, CallerElevationDispatcher>();


        authBuilder?.Invoke(new CallSessionAuthenticationBuilder(builder));

        services.TryAddSingleton<IChallengeStore, InMemoryChallengeStore>();
        // Always-present fallback so the orchestrator never enumerates an empty list.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICallerAuthenticator, AnonymousCallerAuthenticator>());

        return builder;
    }


    public sealed class CallSessionAuthenticationBuilder(CallSessionContainerBuilder builder)
    {
        private readonly IServiceCollection _services = builder.Services;

        public CallSessionAuthenticationBuilder WithCallerDirectory<TDirectory, TIdentity>(ServiceLifetime directoryLifetime = ServiceLifetime.Singleton)
            where TDirectory : class, ICallerDirectory<TIdentity>
            where TIdentity : class, ICallerIdentity
        {
            // The constraint only guarantees TDirectory implements the generic ICallerDirectory<TIdentity>,
            // so always register that service type.
            _services.TryAdd(ServiceDescriptor.Describe(typeof(ICallerDirectory<TIdentity>), typeof(TDirectory), directoryLifetime));

            // Only register the non-generic ICallerDirectory facade when TDirectory actually implements it.
            // A type implementing only the base ICallerDirectory<TIdentity> is NOT assignable to the more-derived
            // ICallerDirectory, which would otherwise produce an invalid descriptor that fails DI validation.
            if (typeof(ICallerDirectory).IsAssignableFrom(typeof(TDirectory)))
            {
                _services.TryAdd(ServiceDescriptor.Describe(typeof(ICallerDirectory), typeof(TDirectory), directoryLifetime));
            }

            return this;
        }

        public CallSessionAuthenticationBuilder WithChallengeStore<TChallengeStore>(ServiceLifetime challengeStoreLifetime = ServiceLifetime.Singleton)
            where TChallengeStore : class, IChallengeStore
        {
            _services.TryAdd(ServiceDescriptor.Describe(typeof(IChallengeStore), typeof(TChallengeStore), challengeStoreLifetime));

            return this;
        }

        /// <summary>Adds an <see cref="ICallerAuthenticator"/> implementation to the chain.</summary>
        /// <remarks>
        /// Authenticators run in DI registration order. Register stronger / more-expensive
        /// authenticators after passive ones (e.g. ANI lookup → MFA → voice biometric).
        /// </remarks>
        public CallSessionAuthenticationBuilder AddCallerAuthenticator<TAuthenticator>(
            ServiceLifetime lifetime = ServiceLifetime.Singleton)
            where TAuthenticator : class, ICallerAuthenticator
        {
            _services.TryAddEnumerable(
                ServiceDescriptor.Describe(typeof(ICallerAuthenticator), typeof(TAuthenticator), lifetime));
            return this;
        }

        /// <summary>
        /// Adds the ANI-based <see cref="AniIdentityLookupAuthenticator"/> backed by the
        /// supplied <see cref="ICallerDirectory"/> implementation. Convenience over the
        /// two-step "register directory + add authenticator" call.
        /// </summary>
        public CallSessionAuthenticationBuilder AddAniIdentityLookupAuthenticator(ServiceLifetime serviceLifetime = ServiceLifetime.Singleton)
        {
            return AddCallerAuthenticator<AniIdentityLookupAuthenticator>(serviceLifetime);
        }

        /// <summary>
        /// Adds the <see cref="IdentifyByLast4Authenticator"/>, which establishes a
        /// knowledge-based identity from the last four digits of the caller's account.
        /// Registers the per-call <see cref="Last4Attempt"/> buffer.
        /// </summary>
        public CallSessionAuthenticationBuilder AddIdentifyByLast4Authenticator(ServiceLifetime serviceLifetime = ServiceLifetime.Singleton)
        {
            _services.TryAddScoped<Last4Attempt>();
            return AddCallerAuthenticator<IdentifyByLast4Authenticator>(serviceLifetime);
        }

        /// <summary>
        /// Registers the <see cref="PinAuthenticator"/> together with the supplied
        /// <typeparamref name="TPinValidator"/> and a per-call <see cref="PinAttempt"/>. Tools
        /// (DTMF collectors, realtime function calls, etc.) set <see cref="PinAttempt.Digits"/>
        /// and then invoke <see cref="IAuthenticationOrchestrator"/> to elevate the caller to
        /// <see cref="CallerVerificationLevel.KnowledgeBased"/>.
        /// </summary>
        public CallSessionAuthenticationBuilder AddPinAuthenticator<TPinValidator>(
            ServiceLifetime validatorLifetime = ServiceLifetime.Singleton)
            where TPinValidator : class, IPinValidator
        {
            _services.TryAdd(ServiceDescriptor.Describe(typeof(IPinValidator), typeof(TPinValidator), validatorLifetime));
            _services.TryAddScoped<PinAttempt>();
            return AddCallerAuthenticator<PinAuthenticator>();
        }

        /// <summary>
        /// Registers the <see cref="SmsOtpAuthenticator"/> together with a per-call
        /// <see cref="SmsOtpAttempt"/> buffer. The host must also register an
        /// <see cref="Authenticators.ISmsOtpSender"/> to deliver codes.
        /// </summary>
        public CallSessionAuthenticationBuilder AddSmsOtpAuthenticator()
        {
            _services.TryAddScoped<SmsOtpAttempt>();
            return AddCallerAuthenticator<SmsOtpAuthenticator>();
        }
    }
}

