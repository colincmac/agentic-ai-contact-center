using System.Threading.Channels;
using Agents.AI.ContactCenter.Authentication;
using Agents.AI.ContactCenter.Calling;
using Agents.AI.ContactCenter.Calling.Core;
using Agents.AI.ContactCenter.Calling.Strategies;
using Agents.AI.ContactCenter.Calling.Strategies.Composite;
using Agents.AI.ContactCenter.Configuration;
using Agents.AI.ContactCenter.IvrWorkflow;
using Agents.AI.ContactCenter.IvrWorkflow.Blueprint;
using Agents.AI.ContactCenter.IvrWorkflow.Compilation;
using Agents.AI.ContactCenter.IvrWorkflow.Execution;
using Agents.AI.ContactCenter.State;
using Agents.AI.ContactCenter.State.Projections;
using Agents.AI.ContactCenter.State.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agents.AI.ContactCenter.Tests.Calling;

public sealed class CompositeFallbackStrategyTests
{
    [Fact]
    public async Task Activation_DrainsBoundedLeafOutputBeforeStartCompletes()
    {
        var first = new TestStrategy(AgentTier.RealtimeVoice, outputCapacity: 1);
        first.OnStart = async ct =>
        {
            await first.EmitAudioAsync(ct);
            await first.EmitAudioAsync(ct);
        };
        await using var fixture = CreateComposite([AgentTier.RealtimeVoice], (AgentTier.RealtimeVoice, first));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await fixture.Composite.StartAsync(CreateStartContext(), timeout.Token);
        Assert.IsType<OutboundDirective.Audio>(await fixture.Composite.Outbound.ReadAsync(timeout.Token));
        Assert.IsType<OutboundDirective.Audio>(await fixture.Composite.Outbound.ReadAsync(timeout.Token));
    }
    [Fact]
    public async Task Inner_fault_degrades_once_and_exhaustion_emits_terminal_fault()
    {
        var first = new TestStrategy(AgentTier.RealtimeVoice);
        var second = new TestStrategy(AgentTier.IntentNlu);
        await using var fixture = CreateComposite(
            [AgentTier.RealtimeVoice, AgentTier.IntentNlu],
            (AgentTier.RealtimeVoice, first),
            (AgentTier.IntentNlu, second));

        await fixture.Composite.StartAsync(CreateStartContext());
        await first.EmitFaultAsync("first failed");

        var degraded = Assert.IsType<StrategyEvent.TierDegraded>(
            await ReadUntilAsync(fixture.Composite.Events, static value => value is StrategyEvent.TierDegraded));
        Assert.Equal(AgentTier.RealtimeVoice, degraded.From);
        Assert.Equal(AgentTier.IntentNlu, degraded.To);
        Assert.Equal(1, second.StartCount);
        Assert.Equal(1, first.DisposeCount);

        await second.EmitFaultAsync("second failed");
        var terminal = Assert.IsType<StrategyEvent.Faulted>(
            await ReadUntilAsync(fixture.Composite.Events, static value => value is StrategyEvent.Faulted));
        Assert.Equal("No fallback available", terminal.Message);
    }

    [Fact]
    public async Task Fallback_start_failure_advances_to_next_tier()
    {
        var first = new TestStrategy(AgentTier.RealtimeVoice);
        var failing = new TestStrategy(AgentTier.IntentNlu)
        {
            StartException = new InvalidOperationException("start failed")
        };
        var final = new TestStrategy(AgentTier.DtmfOnly);
        await using var fixture = CreateComposite(
            [AgentTier.RealtimeVoice, AgentTier.IntentNlu, AgentTier.DtmfOnly],
            (AgentTier.RealtimeVoice, first),
            (AgentTier.IntentNlu, failing),
            (AgentTier.DtmfOnly, final));

        await fixture.Composite.StartAsync(CreateStartContext());
        await first.EmitFaultAsync("degrade");

        var degraded = Assert.IsType<StrategyEvent.TierDegraded>(
            await ReadUntilAsync(fixture.Composite.Events, static value => value is StrategyEvent.TierDegraded));
        Assert.Equal(AgentTier.RealtimeVoice, degraded.From);
        Assert.Equal(AgentTier.DtmfOnly, degraded.To);
        Assert.Equal(1, failing.StartCount);
        Assert.Equal(1, failing.DisposeCount);
        Assert.Equal(1, final.StartCount);
    }

    [Fact]
    public async Task Unexpected_inner_event_completion_triggers_degradation()
    {
        var first = new TestStrategy(AgentTier.RealtimeVoice);
        var second = new TestStrategy(AgentTier.IntentNlu);
        await using var fixture = CreateComposite(
            [AgentTier.RealtimeVoice, AgentTier.IntentNlu],
            (AgentTier.RealtimeVoice, first),
            (AgentTier.IntentNlu, second));
        await fixture.Composite.StartAsync(CreateStartContext());

        first.CompleteEvents();

        var degraded = Assert.IsType<StrategyEvent.TierDegraded>(
            await ReadUntilAsync(fixture.Composite.Events, static value => value is StrategyEvent.TierDegraded));
        Assert.Equal(AgentTier.IntentNlu, degraded.To);
        Assert.Equal(1, second.StartCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_failed_activation_moves_the_reservation_before_starting_the_next_tier(bool midCall)
    {
        var resolver = new RecordingResolver(AgentTier.IntentNlu, AgentTier.DtmfOnly);
        var admission = CreateAdmission(resolver);
        var first = new TestStrategy(AgentTier.RealtimeVoice)
        {
            StartException = midCall ? null : new InvalidOperationException("initial start failed")
        };
        var second = new TestStrategy(AgentTier.IntentNlu)
        {
            StartException = new InvalidOperationException("fallback start failed"),
            OnStart = _ =>
            {
                Assert.Equal(AgentTier.IntentNlu, admission.Tier);
                return Task.CompletedTask;
            }
        };
        var final = new TestStrategy(AgentTier.DtmfOnly)
        {
            OnStart = _ =>
            {
                Assert.Equal(AgentTier.DtmfOnly, admission.Tier);
                return Task.CompletedTask;
            }
        };
        await using var fixture = CreateComposite(
            [AgentTier.RealtimeVoice, AgentTier.IntentNlu, AgentTier.DtmfOnly],
            admission, null,
            (AgentTier.RealtimeVoice, first), (AgentTier.IntentNlu, second), (AgentTier.DtmfOnly, final));

        await fixture.Composite.StartAsync(CreateStartContext());
        if (midCall)
        {
            await first.EmitFaultAsync("midcall failed");
            await ReadUntilAsync(fixture.Composite.Events, static e => e is StrategyEvent.TierDegraded);
        }

        Assert.Equal([AgentTier.RealtimeVoice, AgentTier.IntentNlu], resolver.FallbackRequests);
        Assert.Equal([AgentTier.RealtimeVoice, AgentTier.IntentNlu], resolver.Releases);
        Assert.Equal(1, final.StartCount);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
        await fixture.Composite.StopAsync();
        Assert.Equal([AgentTier.RealtimeVoice, AgentTier.IntentNlu, AgentTier.DtmfOnly], resolver.Releases);
        Assert.Equal(1, final.DisposeCount);
    }

    [Fact]
    public async Task Missing_initial_factory_cannot_bypass_denied_admission()
    {
        var resolver = new RecordingResolver();
        var final = new TestStrategy(AgentTier.DtmfOnly);
        await using var fixture = CreateComposite(
            [AgentTier.RealtimeVoice, AgentTier.DtmfOnly], CreateAdmission(resolver), null,
            (AgentTier.DtmfOnly, final));

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Composite.StartAsync(CreateStartContext()));
        await ReadUntilAsync(fixture.Composite.Events, static e => e is StrategyEvent.Faulted);

        Assert.Equal(0, final.StartCount);
        Assert.Equal([AgentTier.RealtimeVoice], resolver.FallbackRequests);
        Assert.Equal([AgentTier.RealtimeVoice], resolver.Releases);
    }

    [Fact]
    public async Task Missing_initial_factory_moves_admission_before_fallback()
    {
        var resolver = new RecordingResolver(AgentTier.DtmfOnly);
        var admission = CreateAdmission(resolver);
        var final = new TestStrategy(AgentTier.DtmfOnly)
        {
            OnStart = _ =>
            {
                Assert.Equal(AgentTier.DtmfOnly, admission.Tier);
                Assert.Equal([AgentTier.RealtimeVoice], resolver.Releases);
                return Task.CompletedTask;
            }
        };
        await using var fixture = CreateComposite(
            [AgentTier.RealtimeVoice, AgentTier.DtmfOnly], admission, null, (AgentTier.DtmfOnly, final));

        await fixture.Composite.StartAsync(CreateStartContext());

        Assert.Equal(1, final.StartCount);
    }

    [Fact]
    public async Task Initial_activation_uses_the_tier_actually_admitted()
    {
        var resolver = new RecordingResolver();
        var admission = new CallTierAdmission();
        admission.Initialize(resolver, AgentTier.IntentNlu);
        var first = new TestStrategy(AgentTier.RealtimeVoice);
        var second = new TestStrategy(AgentTier.IntentNlu);
        await using var fixture = CreateComposite(
            [AgentTier.RealtimeVoice, AgentTier.IntentNlu], admission, null,
            (AgentTier.RealtimeVoice, first), (AgentTier.IntentNlu, second));

        await fixture.Composite.StartAsync(CreateStartContext());

        Assert.Equal(0, first.StartCount);
        Assert.Equal(1, second.StartCount);
        Assert.Empty(resolver.FallbackRequests);
    }

    [Fact]
    public async Task Midcall_degradation_disabled_still_allows_initial_activation_fallback()
    {
        var resolver = new RecordingResolver(AgentTier.IntentNlu, AgentTier.DtmfOnly);
        var options = ConfiguredOptions();
        options.AllowMidCallDegradation = false;
        var first = new TestStrategy(AgentTier.RealtimeVoice) { StartException = new IOException("unavailable") };
        var second = new TestStrategy(AgentTier.IntentNlu);
        var final = new TestStrategy(AgentTier.DtmfOnly);
        await using var fixture = CreateComposite(
            options.FallbackOrder.ToArray(), CreateAdmission(resolver), options,
            (AgentTier.RealtimeVoice, first), (AgentTier.IntentNlu, second), (AgentTier.DtmfOnly, final));

        await fixture.Composite.StartAsync(CreateStartContext());
        await second.EmitFaultAsync("active failed");
        var fault = Assert.IsType<StrategyEvent.Faulted>(
            await ReadUntilAsync(fixture.Composite.Events, static e => e is StrategyEvent.Faulted));

        Assert.Contains("disabled", fault.Message, StringComparison.Ordinal);
        Assert.Equal([AgentTier.RealtimeVoice], resolver.FallbackRequests);
        Assert.Equal([AgentTier.RealtimeVoice, AgentTier.IntentNlu], resolver.Releases);
        Assert.Equal(0, final.StartCount);
    }

    [Fact]
    public async Task Disabled_tier_is_not_started_even_if_a_custom_resolver_returns_it()
    {
        var resolver = new RecordingResolver(AgentTier.IntentNlu, AgentTier.DtmfOnly);
        var options = ConfiguredOptions();
        options.Tiers[AgentTier.IntentNlu].Enabled = false;
        var first = new TestStrategy(AgentTier.RealtimeVoice) { StartException = new IOException("unavailable") };
        var second = new TestStrategy(AgentTier.IntentNlu);
        var final = new TestStrategy(AgentTier.DtmfOnly);
        await using var fixture = CreateComposite(
            options.FallbackOrder.ToArray(), CreateAdmission(resolver), options,
            (AgentTier.RealtimeVoice, first), (AgentTier.IntentNlu, second), (AgentTier.DtmfOnly, final));

        await fixture.Composite.StartAsync(CreateStartContext());

        Assert.Equal(0, second.StartCount);
        Assert.Equal(1, final.StartCount);
        Assert.Equal([AgentTier.RealtimeVoice, AgentTier.IntentNlu], resolver.Releases);
    }

    [Fact]
    public async Task Caller_cancellation_during_failed_start_does_not_request_a_fallback()
    {
        using var cancellation = new CancellationTokenSource();
        var resolver = new RecordingResolver(AgentTier.IntentNlu);
        var first = new TestStrategy(AgentTier.RealtimeVoice)
        {
            OnStart = _ =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
        };
        var second = new TestStrategy(AgentTier.IntentNlu);
        await using var fixture = CreateComposite(
            [AgentTier.RealtimeVoice, AgentTier.IntentNlu], CreateAdmission(resolver), null,
            (AgentTier.RealtimeVoice, first), (AgentTier.IntentNlu, second));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Composite.StartAsync(CreateStartContext(), cancellation.Token));

        Assert.Empty(resolver.FallbackRequests);
        Assert.Equal([AgentTier.RealtimeVoice], resolver.Releases);
        Assert.Equal(0, second.StartCount);
        Assert.Equal(1, first.DisposeCount);
    }

    [Fact]
    public async Task Admission_backend_failure_emits_terminal_fault_and_releases_current_slot()
    {
        var resolver = new RecordingResolver
        {
            OnFallback = (_, _) => ValueTask.FromException<AgentTier?>(new IOException("capacity backend offline"))
        };
        var first = new TestStrategy(AgentTier.RealtimeVoice);
        var second = new TestStrategy(AgentTier.IntentNlu);
        await using var fixture = CreateComposite(
            [AgentTier.RealtimeVoice, AgentTier.IntentNlu], CreateAdmission(resolver), null,
            (AgentTier.RealtimeVoice, first), (AgentTier.IntentNlu, second));
        await fixture.Composite.StartAsync(CreateStartContext());

        await first.EmitFaultAsync("active failed");
        var fault = Assert.IsType<StrategyEvent.Faulted>(
            await ReadUntilAsync(fixture.Composite.Events, static e => e is StrategyEvent.Faulted));

        Assert.IsType<IOException>(fault.Exception);
        Assert.Equal([AgentTier.RealtimeVoice], resolver.Releases);
        Assert.Equal(0, second.StartCount);
    }

    [Fact]
    public async Task Concurrent_stop_and_inflight_admission_release_each_slot_once_without_starting_next()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource<AgentTier?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new RecordingResolver
        {
            OnFallback = (_, _) =>
            {
                entered.TrySetResult();
                return new ValueTask<AgentTier?>(proceed.Task);
            }
        };
        var first = new TestStrategy(AgentTier.RealtimeVoice);
        var second = new TestStrategy(AgentTier.IntentNlu);
        await using var fixture = CreateComposite(
            [AgentTier.RealtimeVoice, AgentTier.IntentNlu], CreateAdmission(resolver), null,
            (AgentTier.RealtimeVoice, first), (AgentTier.IntentNlu, second));
        await fixture.Composite.StartAsync(CreateStartContext());
        await first.EmitFaultAsync("active failed");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = fixture.Composite.StopAsync();
        var otherStop = fixture.Composite.StopAsync();
        proceed.SetResult(AgentTier.IntentNlu);
        await Task.WhenAll(stop, otherStop).WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Composite.DisposeAsync();

        Assert.Equal(0, second.StartCount);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal([AgentTier.IntentNlu, AgentTier.RealtimeVoice], resolver.Releases);
    }

    [Fact]
    public async Task Concurrent_admission_move_and_release_do_not_release_the_old_slot_twice()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource<AgentTier?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new RecordingResolver
        {
            OnFallback = (_, _) =>
            {
                entered.TrySetResult();
                return new ValueTask<AgentTier?>(proceed.Task);
            }
        };
        await using var admission = CreateAdmission(resolver);
        var move = admission.MoveToFallbackAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var release = admission.ReleaseAsync().AsTask();
        var secondRelease = admission.ReleaseAsync().AsTask();

        proceed.SetResult(AgentTier.IntentNlu);
        await Task.WhenAll(move, release, secondRelease).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([AgentTier.RealtimeVoice, AgentTier.IntentNlu], resolver.Releases);
        Assert.Null(await admission.MoveToFallbackAsync());
    }

    [Fact]
    public async Task Release_backend_failure_is_terminal_and_does_not_retry_an_ambiguous_release()
    {
        var resolver = new RecordingResolver(AgentTier.IntentNlu)
        {
            OnRelease = tier => tier == AgentTier.RealtimeVoice
                ? ValueTask.FromException(new IOException("release failed"))
                : ValueTask.CompletedTask
        };
        var first = new TestStrategy(AgentTier.RealtimeVoice);
        var second = new TestStrategy(AgentTier.IntentNlu);
        await using var fixture = CreateComposite(
            [AgentTier.RealtimeVoice, AgentTier.IntentNlu], CreateAdmission(resolver), null,
            (AgentTier.RealtimeVoice, first), (AgentTier.IntentNlu, second));
        await fixture.Composite.StartAsync(CreateStartContext());

        await first.EmitFaultAsync("active failed");
        await ReadUntilAsync(fixture.Composite.Events, static e => e is StrategyEvent.Faulted);
        await fixture.Composite.StopAsync();

        Assert.Equal([AgentTier.RealtimeVoice, AgentTier.IntentNlu], resolver.Releases);
        Assert.Equal(0, second.StartCount);
    }

    [Fact]
    public async Task Fault_queued_by_new_tier_during_swap_is_not_lost()
    {
        var resolver = new RecordingResolver(AgentTier.IntentNlu, AgentTier.DtmfOnly);
        var first = new TestStrategy(AgentTier.RealtimeVoice);
        var second = new TestStrategy(AgentTier.IntentNlu);
        await second.EmitFaultAsync("already failed");
        var final = new TestStrategy(AgentTier.DtmfOnly);
        await using var fixture = CreateComposite(
            [AgentTier.RealtimeVoice, AgentTier.IntentNlu, AgentTier.DtmfOnly],
            CreateAdmission(resolver), null,
            (AgentTier.RealtimeVoice, first), (AgentTier.IntentNlu, second), (AgentTier.DtmfOnly, final));
        await fixture.Composite.StartAsync(CreateStartContext());

        await first.EmitFaultAsync("active failed");
        await ReadUntilAsync(fixture.Composite.Events,
            static e => e is StrategyEvent.TierDegraded { To: AgentTier.DtmfOnly });

        Assert.Equal(1, final.StartCount);
        Assert.Equal([AgentTier.RealtimeVoice, AgentTier.IntentNlu], resolver.Releases);
    }

    [Fact]
    public async Task Stop_during_initial_start_cancels_activation_and_releases_once()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new RecordingResolver(AgentTier.IntentNlu);
        var first = new TestStrategy(AgentTier.RealtimeVoice)
        {
            OnStart = async token =>
            {
                entered.TrySetResult();
                await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(token);
            }
        };
        var second = new TestStrategy(AgentTier.IntentNlu);
        await using var fixture = CreateComposite(
            [AgentTier.RealtimeVoice, AgentTier.IntentNlu], CreateAdmission(resolver), null,
            (AgentTier.RealtimeVoice, first), (AgentTier.IntentNlu, second));
        var start = fixture.Composite.StartAsync(CreateStartContext());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = fixture.Composite.StopAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        await stop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([AgentTier.RealtimeVoice], resolver.Releases);
        Assert.Empty(resolver.FallbackRequests);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(0, second.StartCount);
    }

    [Fact]
    public async Task Failed_backend_cleanup_is_terminal_instead_of_reporting_a_successful_fallback()
    {
        var resolver = new RecordingResolver(AgentTier.IntentNlu);
        var first = new TestStrategy(AgentTier.RealtimeVoice) { StopException = new IOException("backend cleanup failed") };
        var second = new TestStrategy(AgentTier.IntentNlu);
        await using var fixture = CreateComposite(
            [AgentTier.RealtimeVoice, AgentTier.IntentNlu], CreateAdmission(resolver), null,
            (AgentTier.RealtimeVoice, first), (AgentTier.IntentNlu, second));
        await fixture.Composite.StartAsync(CreateStartContext());

        await first.EmitFaultAsync("active failed");
        var fault = Assert.IsType<StrategyEvent.Faulted>(
            await ReadUntilAsync(fixture.Composite.Events, static e => e is StrategyEvent.Faulted));

        Assert.IsType<IOException>(fault.Exception);
        Assert.Empty(resolver.FallbackRequests);
        Assert.Equal([AgentTier.RealtimeVoice], resolver.Releases);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(0, second.StartCount);
    }

    [Fact]
    public async Task Initial_stage_profile_rejection_uses_admission_before_starting_eligible_tier()
    {
        var resolver = new RecordingResolver(AgentTier.IntentNlu);
        var admission = CreateAdmission(resolver);
        var first = new TestStrategy(AgentTier.RealtimeVoice);
        var second = new TestStrategy(AgentTier.IntentNlu)
        {
            OnStart = _ =>
            {
                Assert.Equal(AgentTier.IntentNlu, admission.Tier);
                Assert.Equal([AgentTier.RealtimeVoice], resolver.Releases);
                return Task.CompletedTask;
            }
        };
        await using var fixture = CreateComposite(
            [AgentTier.RealtimeVoice, AgentTier.IntentNlu], admission, null,
            ProfileWorkflow(["intent"]), InteractionOptions(),
            (AgentTier.RealtimeVoice, first), (AgentTier.IntentNlu, second));

        await fixture.Composite.StartAsync(CreateStartContext());

        Assert.Equal(0, first.StartCount);
        Assert.Equal(0, first.DisposeCount);
        Assert.Equal(1, second.StartCount);
        Assert.Equal([AgentTier.RealtimeVoice], resolver.FallbackRequests);
    }

    [Fact]
    public async Task Midcall_profile_selection_uses_current_stage_without_advancing_or_restarting_workflow()
    {
        await using var projector = new CallStateProjector(
            "composite-call", [new IvrStateProjection()], new InMemoryCallStateStore(), new CallStateOptions());
        await projector.HydrateAsync();
        projector.Fold(new StrategyEvent.WorkflowStepEntered("start", DateTimeOffset.UtcNow));
        var resolver = new RecordingResolver(AgentTier.IntentNlu, AgentTier.DtmfOnly);
        var admission = CreateAdmission(resolver);
        var first = new TestStrategy(AgentTier.RealtimeVoice);
        var second = new TestStrategy(AgentTier.IntentNlu);
        var final = new TestStrategy(AgentTier.DtmfOnly)
        {
            OnStart = _ =>
            {
                Assert.Equal("current", projector.Get<IvrSnapshot>().CurrentStepId);
                Assert.Equal(AgentTier.DtmfOnly, admission.Tier);
                return Task.CompletedTask;
            }
        };
        await using var fixture = CreateComposite(
            [AgentTier.RealtimeVoice, AgentTier.IntentNlu, AgentTier.DtmfOnly], admission, null,
            ProfileWorkflow(["voice", "intent"]), InteractionOptions(),
            (AgentTier.RealtimeVoice, first), (AgentTier.IntentNlu, second), (AgentTier.DtmfOnly, final));
        await fixture.Composite.StartAsync(CreateStartContext() with { StateProjector = projector });
        projector.Fold(new StrategyEvent.WorkflowStepEntered("current", DateTimeOffset.UtcNow));
        projector.Fold(new StrategyEvent.WorkflowDataRecorded(
            new Dictionary<string, string?> { ["account"] = "preserved" }, DateTimeOffset.UtcNow));
        var snapshot = projector.Get<IvrSnapshot>();

        await first.EmitFaultAsync("stage requires a different profile");
        await ReadUntilAsync(fixture.Composite.Events,
            static e => e is StrategyEvent.TierDegraded { To: AgentTier.DtmfOnly });

        Assert.Same(snapshot, projector.Get<IvrSnapshot>());
        Assert.Equal(0, second.StartCount);
        Assert.Equal(1, final.StartCount);
        Assert.Equal([AgentTier.RealtimeVoice, AgentTier.IntentNlu], resolver.Releases);
    }

    [Fact]
    public async Task Profile_edge_requirement_rejects_tier_even_when_leaf_emits_no_directives()
    {
        var resolver = new RecordingResolver(AgentTier.IntentNlu);
        var first = new TestStrategy(AgentTier.RealtimeVoice);
        var second = new TestStrategy(AgentTier.IntentNlu);
        var interaction = InteractionOptions();
        interaction.Profiles[0].RequiredEdgeCapabilities = EdgeCapabilities.Streaming;
        await using var fixture = CreateComposite(
            [AgentTier.RealtimeVoice, AgentTier.IntentNlu], CreateAdmission(resolver), null,
            ProfileWorkflow(["voice", "intent"]), interaction,
            (AgentTier.RealtimeVoice, first), (AgentTier.IntentNlu, second));

        await fixture.Composite.StartAsync(CreateStartContext() with { EdgeCapabilities = EdgeCapabilities.Verb });

        Assert.Equal(0, first.StartCount);
        Assert.Equal(1, second.StartCount);
        Assert.Equal([AgentTier.RealtimeVoice], resolver.Releases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Leaf_directives_are_checked_only_when_caller_edge_capabilities_are_known(bool knownEdge)
    {
        var resolver = new RecordingResolver(AgentTier.IntentNlu);
        var first = new TestStrategy(AgentTier.RealtimeVoice) { EmittedDirectives = EdgeCapabilities.Streaming };
        var second = new TestStrategy(AgentTier.IntentNlu) { EmittedDirectives = EdgeCapabilities.SpeakText };
        await using var fixture = CreateComposite(
            [AgentTier.RealtimeVoice, AgentTier.IntentNlu], CreateAdmission(resolver), null,
            (AgentTier.RealtimeVoice, first), (AgentTier.IntentNlu, second));

        await fixture.Composite.StartAsync(CreateStartContext() with
        {
            EdgeCapabilities = knownEdge ? EdgeCapabilities.Verb : null
        });

        Assert.Equal(knownEdge ? 0 : 1, first.StartCount);
        Assert.Equal(knownEdge ? 1 : 0, first.DisposeCount);
        Assert.Equal(knownEdge ? 1 : 0, second.StartCount);
        Assert.Equal(knownEdge ? [AgentTier.RealtimeVoice] : Array.Empty<AgentTier>(), resolver.Releases);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Missing_or_empty_profile_configuration_preserves_only_unrestricted_legacy_stages(
        bool registerPolicy, bool restricted)
    {
        var resolver = new RecordingResolver();
        var first = new TestStrategy(AgentTier.RealtimeVoice);
        await using var fixture = CreateComposite(
            [AgentTier.RealtimeVoice], CreateAdmission(resolver), null,
            ProfileWorkflow(restricted ? ["voice"] : []),
            registerPolicy ? new CallInteractionOptions() : null,
            (AgentTier.RealtimeVoice, first));

        if (restricted)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Composite.StartAsync(CreateStartContext()));
            await ReadUntilAsync(fixture.Composite.Events, static e => e is StrategyEvent.Faulted);
            Assert.Equal(0, first.StartCount);
            Assert.Equal([AgentTier.RealtimeVoice], resolver.Releases);
        }
        else
        {
            await fixture.Composite.StartAsync(CreateStartContext());
            Assert.Equal(1, first.StartCount);
            Assert.Empty(resolver.FallbackRequests);
        }
    }

    [Fact]
    public async Task Unknown_projected_stage_fails_without_resetting_to_initial_stage_or_skipping_ahead()
    {
        await using var projector = new CallStateProjector(
            "composite-call", [new IvrStateProjection()], new InMemoryCallStateStore(), new CallStateOptions());
        await projector.HydrateAsync();
        projector.Fold(new StrategyEvent.WorkflowStepEntered("missing", DateTimeOffset.UtcNow));
        var snapshot = projector.Get<IvrSnapshot>();
        var resolver = new RecordingResolver(AgentTier.IntentNlu);
        var first = new TestStrategy(AgentTier.RealtimeVoice);
        await using var fixture = CreateComposite(
            [AgentTier.RealtimeVoice, AgentTier.IntentNlu], CreateAdmission(resolver), null,
            (AgentTier.RealtimeVoice, first));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Composite.StartAsync(
            CreateStartContext() with { StateProjector = projector }));

        Assert.Same(snapshot, projector.Get<IvrSnapshot>());
        Assert.Equal(0, first.StartCount);
        Assert.Empty(resolver.FallbackRequests);
        Assert.Equal([AgentTier.RealtimeVoice], resolver.Releases);
    }

    private static WorkflowBlueprint ProfileWorkflow(string[] initialProfiles) => new()
    {
        Id = "profile-composite",
        InitialStageId = "start",
        Stages =
        [
            new StageBlueprint
            {
                Id = "start", Terminal = true, InteractionProfiles = initialProfiles,
                Channels = new StageChannelConfig { Realtime = new StageRealtimePrompt() }
            },
            new StageBlueprint
            {
                Id = "current", Terminal = true, InteractionProfiles = ["touch"],
                Channels = new StageChannelConfig { Scripted = new StageScriptedConfig() }
            }
        ]
    };

    private static CallInteractionOptions InteractionOptions() => new()
    {
        Profiles =
        [
            new() { Name = "voice", Tier = AgentTier.RealtimeVoice, MaxConcurrent = 2, RequiredEdgeCapabilities = EdgeCapabilities.None },
            new() { Name = "intent", Tier = AgentTier.IntentNlu, MaxConcurrent = 2, RequiredEdgeCapabilities = EdgeCapabilities.None },
            new() { Name = "touch", Tier = AgentTier.DtmfOnly, MaxConcurrent = 2, RequiredEdgeCapabilities = EdgeCapabilities.None }
        ]
    };

    private static AgentTierOptions ConfiguredOptions() => new()
    {
        Tiers = new()
        {
            [AgentTier.RealtimeVoice] = new() { MaxConcurrent = 2 },
            [AgentTier.IntentNlu] = new() { MaxConcurrent = 2 },
            [AgentTier.DtmfOnly] = new() { MaxConcurrent = 2 }
        }
    };

    private static CallTierAdmission CreateAdmission(IAgentTierResolver resolver)
    {
        var admission = new CallTierAdmission();
        admission.Initialize(resolver, AgentTier.RealtimeVoice);
        return admission;
    }

    private static CompositeFixture CreateComposite(
        AgentTier[] tiers,
        params (AgentTier Tier, TestStrategy Strategy)[] strategies)
        => CreateComposite(tiers, new CallTierAdmission(), null, strategies);

    private static CompositeFixture CreateComposite(
        AgentTier[] tiers,
        CallTierAdmission admission,
        AgentTierOptions? options,
        params (AgentTier Tier, TestStrategy Strategy)[] strategies)
        => CreateComposite(tiers, admission, options, null, null, strategies);

    private static CompositeFixture CreateComposite(
        AgentTier[] tiers,
        CallTierAdmission admission,
        AgentTierOptions? options,
        WorkflowBlueprint? blueprint,
        CallInteractionOptions? interaction,
        params (AgentTier Tier, TestStrategy Strategy)[] strategies)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICallerElevationDispatcher, CallerElevationDispatcher>();
        if (interaction is not null)
        {
            services.AddSingleton(new CallInteractionPolicy(Options.Create(interaction)));
        }
        foreach (var (tier, strategy) in strategies)
        {
            services.AddKeyedSingleton<ILeafConversationStrategyFactory>(
                tier,
                new LeafConversationStrategyFactory(() => strategy));
        }

        var provider = services.BuildServiceProvider();
        var workflow = new WorkflowGraphCompiler().Compile(blueprint ?? new WorkflowBlueprint
        {
            Id = "composite-test",
            InitialStageId = "start",
            Stages = [new StageBlueprint { Id = "start" }]
        });
        var session = new CallWorkflowSession(
            workflow,
            provider,
            provider.GetRequiredService<ICallerElevationDispatcher>());
        return new CompositeFixture(
            new CompositeFallbackStrategy(
                tiers,
                session,
                admission,
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(),
                new TestOptionsMonitor(options ?? ConfiguredOptions())),
            provider);
    }

    private static StrategyStartContext CreateStartContext() => new()
    {
        CallId = "composite-call",
        InboundAudio = Channel.CreateUnbounded<AudioFrame>().Reader,
        InboundDtmf = Channel.CreateUnbounded<DtmfTone>().Reader,
        StateProjector = null
    };

    private static async Task<StrategyEvent> ReadUntilAsync(
        ChannelReader<StrategyEvent> reader,
        Func<StrategyEvent, bool> predicate)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var strategyEvent in reader.ReadAllAsync(cts.Token))
        {
            if (predicate(strategyEvent))
            {
                return strategyEvent;
            }
        }
        throw new InvalidOperationException("The composite event stream completed before the expected event.");
    }

    private sealed class CompositeFixture(
        CompositeFallbackStrategy composite,
        ServiceProvider services) : IAsyncDisposable
    {
        public CompositeFallbackStrategy Composite { get; } = composite;

        public async ValueTask DisposeAsync()
        {
            await Composite.DisposeAsync();
            await services.DisposeAsync();
        }
    }

    private sealed class TestStrategy(AgentTier tier, int outputCapacity = 0) : IConversationStrategy
    {
        private readonly Channel<OutboundDirective> _outbound = outputCapacity > 0
            ? Channel.CreateBounded<OutboundDirective>(outputCapacity) : Channel.CreateUnbounded<OutboundDirective>();
        private readonly Channel<StrategyEvent> _events = Channel.CreateUnbounded<StrategyEvent>();

        public StrategyKind Kind => StrategyKind.Dtmf;
        public AgentTier Tier { get; } = tier;
        public IvrSnapshot WorkflowState => IvrSnapshot.Empty;
        public EdgeCapabilities EmittedDirectives { get; init; } = EdgeCapabilities.None;
        public ChannelReader<OutboundDirective> Outbound => _outbound.Reader;
        public ChannelReader<StrategyEvent> Events => _events.Reader;
        public int StartCount { get; private set; }
        public int DisposeCount { get; private set; }
        public Exception? StartException { get; init; }
        public Exception? StopException { get; init; }
        public Func<CancellationToken, Task>? OnStart { get; set; }
        public ValueTask EmitAudioAsync(CancellationToken ct) => _outbound.Writer.WriteAsync(
            new OutboundDirective.Audio(new AudioFrame(new byte[] { 0, 0 }, DateTimeOffset.UtcNow)), ct);

        public async Task StartAsync(StrategyStartContext context, CancellationToken cancellationToken = default)
        {
            StartCount++;
            if (OnStart is not null)
            {
                await OnStart(cancellationToken);
            }
            if (StartException is not null)
            {
                throw StartException;
            }
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _outbound.Writer.TryComplete();
            _events.Writer.TryComplete();
            return StopException is null ? Task.CompletedTask : Task.FromException(StopException);
        }

        public ValueTask SuspendAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ResumeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask EmitFaultAsync(string message)
            => _events.Writer.WriteAsync(new StrategyEvent.Faulted(message, null, DateTimeOffset.UtcNow));

        public void CompleteEvents() => _events.Writer.TryComplete();
    }

    private sealed class TestOptionsMonitor(AgentTierOptions options) : IOptionsMonitor<AgentTierOptions>
    {
        public AgentTierOptions CurrentValue => options;
        public AgentTierOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<AgentTierOptions, string?> listener) => null;
    }

    private sealed class RecordingResolver(params AgentTier[] fallbackTiers) : IAgentTierResolver
    {
        private readonly Queue<AgentTier> _fallbackTiers = new(fallbackTiers);
        public List<AgentTier> Releases { get; } = [];
        public List<AgentTier> FallbackRequests { get; } = [];
        public Func<AgentTier, CancellationToken, ValueTask<AgentTier?>>? OnFallback { get; init; }
        public Func<AgentTier, ValueTask>? OnRelease { get; init; }

        public ValueTask<AgentTier> ResolveAsync(AgentTier? preferredTier = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentTier?> ResolveFallbackAsync(AgentTier currentTier, CancellationToken cancellationToken = default)
        {
            FallbackRequests.Add(currentTier);
            return OnFallback is not null
                ? OnFallback(currentTier, cancellationToken)
                : ValueTask.FromResult<AgentTier?>(_fallbackTiers.TryDequeue(out var tier) ? tier : null);
        }

        public ValueTask ReleaseAsync(AgentTier tier, CancellationToken cancellationToken = default)
        {
            Assert.False(cancellationToken.CanBeCanceled);
            Releases.Add(tier);
            return OnRelease?.Invoke(tier) ?? ValueTask.CompletedTask;
        }
    }
}
