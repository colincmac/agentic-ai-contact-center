// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.AI.VoiceLive;
using Azure.Core;
using Azure.Identity;
using Microsoft.Shared.DiagnosticIds;
using Microsoft.Shared.Diagnostics;
using Microsoft.Extensions.AI;

#pragma warning disable MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates.
#pragma warning disable OPENAI002 // OpenAI Realtime API is experimental

namespace Extensions.AI.Realtime.AzureVoiceLive;

/// <summary>Represents an <see cref="IRealtimeClient"/> for the OpenAI Realtime API.</summary>
public sealed class AzureVoiceLiveClient : IRealtimeClient
{
    private readonly VoiceLiveClient _realtimeClient;

    /// <summary>The model to use for realtime sessions.</summary>
    private readonly SessionTarget _sessionTarget;

    public const string AzureVoiceLiveProvider = "azure_voice_live";
    /// <summary>Metadata about this client's provider and model, used for OpenTelemetry.</summary>
    private readonly ChatClientMetadata _metadata;

    public AzureVoiceLiveClient(Uri endpoint, AzureKeyCredential credential, string model) : this(new VoiceLiveClient(endpoint, credential), SessionTarget.FromModel(model))
    {
    }

    public AzureVoiceLiveClient(Uri endpoint, TokenCredential credential, string model) : this(new VoiceLiveClient(endpoint, credential), SessionTarget.FromModel(model))
    {
    }

    public AzureVoiceLiveClient(VoiceLiveClient realtimeClient, SessionTarget sessionTarget)
    {
        _realtimeClient = Throw.IfNull(realtimeClient);
        _sessionTarget = Throw.IfNull(sessionTarget);
        _metadata = new(AzureVoiceLiveProvider, defaultModelId: _sessionTarget.Model);
    }

    /// <inheritdoc />
    public async Task<IRealtimeClientSession> CreateSessionAsync(RealtimeSessionOptions? options = null, CancellationToken cancellationToken = default)
    {
        // Voice Live applies the supplied VoiceLiveSessionOptions during the connection
        // handshake, so pass the caller's options through StartSessionAsync rather than
        // sending a follow-up session.update. This is also the only opportunity to set
        // the session model — the API rejects model changes via session.update once the
        // session has been initialized.
        VoiceLiveSession sessionClient;
        if (options is not null)
        {
            var initialSessionOptions = AzureVoiceLiveClientSession.BuildInitialSessionOptions(options, _sessionTarget);
            sessionClient = await _realtimeClient.StartSessionAsync(_sessionTarget, initialSessionOptions, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            sessionClient = await _realtimeClient.StartSessionAsync(_sessionTarget, cancellationToken).ConfigureAwait(false);
        }

        // Seed the session's Options with whatever the caller supplied. The first
        // session.created / session.updated server event will reconcile it with the
        // authoritative server-side state via AzureVoiceLiveClientSession.HandleSessionEvent.
        return new AzureVoiceLiveClientSession(sessionClient, _sessionTarget, options);
    }

    /// <inheritdoc />
    object? IRealtimeClient.GetService(Type serviceType, object? serviceKey)
    {
        _ = Throw.IfNull(serviceType);

        return
            serviceKey is not null ? null :
            serviceType == typeof(ChatClientMetadata) ? _metadata :
            serviceType.IsInstanceOfType(this) ? this :
            serviceType.IsInstanceOfType(_realtimeClient) ? _realtimeClient :
            null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Client itself has no resources to dispose.
        // Sessions are disposed independently.
    }
}
