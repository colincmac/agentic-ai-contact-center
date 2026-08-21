using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Agents.AI.ContactCenter.State;
using Agents.AI.ContactCenter.State.Projections;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Testing;

/// <summary>A deterministic, in-memory workflow scenario backed by the real executor and projections.</summary>
public sealed class ContactCenterScenarioSession : IAsyncDisposable
{
    private readonly ServiceProvider _root;
    private readonly AsyncServiceScope _scope;
    private readonly CallStateProjector _projector;
    private readonly WorkflowExecutor _executor;
    private readonly ScenarioEventRecorder _recorder;
    private readonly List<string> _renderedStages;
    private readonly List<AuthStepRender> _authenticationPrompts;
    private int _enterStarted;
    private int _disposed;

    internal ContactCenterScenarioSession(
        ServiceProvider root,
        AsyncServiceScope scope,
        CallStateProjector projector,
        WorkflowExecutor executor,
        ScenarioEventRecorder recorder,
        List<string> renderedStages,
        List<AuthStepRender> authenticationPrompts)
    {
        _root = root;
        _scope = scope;
        _projector = projector;
        _executor = executor;
        _recorder = recorder;
        _renderedStages = renderedStages;
        _authenticationPrompts = authenticationPrompts;
    }

    public async Task<ScenarioResult> EnterAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _enterStarted, 1) != 0)
        {
            throw new InvalidOperationException("The scenario has already entered its workflow.");
        }

        await _executor.EnterAsync(cancellationToken).ConfigureAwait(false);
        return Snapshot();
    }

    public async Task<AdvanceOutcome> AdvanceAsync(
        string transitionLabel,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureEntered();
        ArgumentException.ThrowIfNullOrWhiteSpace(transitionLabel);

        var edge = _executor.CurrentStage?.FindEdgeByLabel(transitionLabel);
        if (edge is null)
        {
            return new AdvanceOutcome.Invalid(
                $"Stage '{_executor.CurrentStage?.Id}' has no transition labelled '{transitionLabel}'.");
        }

        return await _executor.AdvanceAlongAsync(edge, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScenarioResult> SubmitCredentialAsync(
        string authenticatorName,
        string value,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureEntered();
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticatorName);
        ArgumentNullException.ThrowIfNull(value);

        await _executor.SubmitCredentialAsync(
            authenticatorName,
            new CredentialInput(value),
            cancellationToken).ConfigureAwait(false);
        return Snapshot();
    }

    public ScenarioResult Snapshot()
    {
        ThrowIfDisposed();
        return new ScenarioResult(
            CurrentStageId: _executor.CurrentStage?.Id,
            IsComplete: _executor.IsComplete,
            Workflow: _projector.Get<IvrSnapshot>(),
            Authentication: _projector.Get<AuthSnapshot>(),
            Events: _recorder.Snapshot(),
            RenderedStages: [.. _renderedStages],
            AuthenticationPrompts: [.. _authenticationPrompts]);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _recorder.TryComplete();
        await _scope.DisposeAsync().ConfigureAwait(false);
        await _root.DisposeAsync().ConfigureAwait(false);
    }

    private void EnsureEntered()
    {
        if (Volatile.Read(ref _enterStarted) == 0)
        {
            throw new InvalidOperationException("Call EnterAsync before performing scenario actions.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
