using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Shared.Diagnostics;

namespace Extensions.AI.Realtime;

public sealed class ConfigureOptionsRealtimeClient : DelegatingRealtimeClient
{
    /// <summary>The callback delegate used to configure options.</summary>
    private readonly Func<RealtimeSessionOptions, RealtimeSessionOptions> _configureOptions;

    /// <summary>Initializes a new instance of the <see cref="ConfigureOptionsRealtimeClient    "/> class with the specified <paramref name="configure"/> callback.</summary>
    /// <param name="innerClient">The inner client.</param>
    /// <param name="configure">
    /// The delegate to invoke to configure the <see cref="RealtimeSessionOptions"/> instance. It is passed a clone of the caller-supplied <see cref="RealtimeSessionOptions"/> instance
    /// (or a newly constructed instance if the caller-supplied instance is <see langword="null"/>).
    /// </param>
    /// <remarks>
    /// The <paramref name="configure"/> delegate is passed either a new instance of <see cref="RealtimeSessionOptions"/> if
    /// the caller didn't supply a <see cref="RealtimeSessionOptions"/> instance, or a clone (via <see cref="RealtimeSessionOptions.Clone"/> of the caller-supplied
    /// instance if one was supplied.
    /// </remarks>
    public ConfigureOptionsRealtimeClient(IRealtimeClient innerClient, Func<RealtimeSessionOptions, RealtimeSessionOptions> configure)
        : base(innerClient)
    {
        _configureOptions = Throw.IfNull(configure);
    }

    // <inheritdoc/>
    public override async Task<IRealtimeClientSession> CreateSessionAsync(RealtimeSessionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var innerSession = await base.CreateSessionAsync(Configure(options), cancellationToken).ConfigureAwait(false);
        return new ConfigureOptionsRealtimeClientSession(innerSession, _configureOptions);
    }

    /// <summary>Creates and configures the <see cref="RealtimeSessionOptions"/> to pass along to the inner client.</summary>
    private RealtimeSessionOptions Configure(RealtimeSessionOptions? options)
    {
        options = options?.Clone() ?? new();

        return _configureOptions(options);
    }
}
