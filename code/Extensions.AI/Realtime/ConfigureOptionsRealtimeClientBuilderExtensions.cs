using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Shared.Diagnostics;

namespace Extensions.AI.Realtime;

public static class ConfigureOptionsRealtimeClientBuilderExtensions
{
    /// <summary>
    /// Adds a callback that configures a <see cref="RealtimeSessionOptions"/> to be passed to the next client in the pipeline.
    /// </summary>
    /// <param name="builder">The <see cref="RealtimeClientBuilder"/>.</param>
    /// <param name="configure">
    /// The delegate to invoke to configure the <see cref="RealtimeSessionOptions"/> instance.
    /// It is passed a clone of the caller-supplied <see cref="RealtimeSessionOptions"/> instance (or a newly constructed instance if the caller-supplied instance is <see langword="null"/>).
    /// </param>
    /// <remarks>
    /// This method can be used to set default options. The <paramref name="configure"/> delegate is passed either a new instance of
    /// <see cref="ChatOptions"/> if the caller didn't supply a <see cref="RealtimeSessionOptions"/> instance, or a clone (via <see cref="ChatOptions.Clone"/>)
    /// of the caller-supplied instance if one was supplied.
    /// </remarks>
    /// <returns>The <paramref name="builder"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <related type="Article" href="https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai#provide-options">Provide options.</related>
    public static RealtimeClientBuilder ConfigureOptions(
        this RealtimeClientBuilder builder, Func<RealtimeSessionOptions, RealtimeSessionOptions> configure)
    {
        _ = Throw.IfNull(builder);
        _ = Throw.IfNull(configure);

        return builder.Use(innerClient => new ConfigureOptionsRealtimeClient(innerClient, configure));
    }
}
