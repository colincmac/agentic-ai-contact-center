using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Agents.AI.ContactCenter.Coordination.Core;

public sealed class RedisIncomingCallAdmissionController : IIncomingCallAdmissionController
{
    private static readonly LuaScript ClaimScript = LuaScript.Prepare(@"
local state = redis.call('HGET', @key, 'state')
if not state then
  redis.call('HSET', @key,
    'eventId', @eventId, 'clusterId', @clusterId, 'podId', @podId,
    'instanceId', @instanceId, 'state', @claimedState, 'leaseUntil', @leaseUntil,
    'callConnectionId', '')
  redis.call('PEXPIRE', @key, @recordTtlMs)
  return 1
end
local leaseUntil = tonumber(redis.call('HGET', @key, 'leaseUntil') or '0')
if state == @failedRetryableState or (state == @claimedState and leaseUntil <= tonumber(@nowMs)) then
  redis.call('HSET', @key,
    'eventId', @eventId, 'clusterId', @clusterId, 'podId', @podId,
    'instanceId', @instanceId, 'state', @claimedState, 'leaseUntil', @leaseUntil,
    'callConnectionId', '')
  redis.call('PEXPIRE', @key, @recordTtlMs)
  return 1
end
return 0
");

    private static readonly LuaScript TransitionScript = LuaScript.Prepare(@"
local state = redis.call('HGET', @key, 'state')
if not state or state ~= @expectedState then return 0 end
if redis.call('HGET', @key, 'instanceId') ~= @instanceId then return 0 end
redis.call('HSET', @key, 'state', @nextState, 'leaseUntil', @leaseUntil,
  'callConnectionId', @callConnectionId)
redis.call('PEXPIRE', @key, @recordTtlMs)
return 1
");

    private static readonly LuaScript FailScript = LuaScript.Prepare(@"
local state = redis.call('HGET', @key, 'state')
if not state or (state ~= @claimedState and state ~= @answeringState) then return 0 end
if redis.call('HGET', @key, 'instanceId') ~= @instanceId then return 0 end
redis.call('HSET', @key, 'state', @nextState, 'leaseUntil', @leaseUntil)
redis.call('PEXPIRE', @key, @recordTtlMs)
return 1
");

    private static readonly RedisValue[] Fields =
    [
        "eventId", "clusterId", "podId", "instanceId", "state", "leaseUntil", "callConnectionId"
    ];

    private readonly IConnectionMultiplexer _connection;
    private readonly IClusterIdentity _identity;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _leaseDuration;
    private readonly TimeSpan _recordLifetime;

    public RedisIncomingCallAdmissionController(
        IConnectionMultiplexer connection,
        IClusterIdentity identity,
        IOptions<HyperscaleOptions> options,
        TimeProvider timeProvider)
    {
        _connection = connection;
        _identity = identity;
        _timeProvider = timeProvider;
        _leaseDuration = options.Value.IncomingCallAdmission.LeaseDuration;
        _recordLifetime = options.Value.IncomingCallAdmission.RecordLifetime;
    }

    public async Task<IncomingCallAdmissionResult> TryAcquireAsync(
        string serverCallId,
        string eventGridEventId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverCallId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventGridEventId);
        cancellationToken.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();
        var key = (RedisKey)CoordinationRedisKeys.IncomingCallAdmission(serverCallId);
        var acquired = await _connection.GetDatabase().ScriptEvaluateAsync(ClaimScript, new
        {
            key,
            eventId = (RedisValue)eventGridEventId,
            clusterId = (RedisValue)_identity.ClusterId,
            podId = (RedisValue)_identity.PodId,
            instanceId = (RedisValue)_identity.InstanceId,
            claimedState = (RedisValue)StateValue(IncomingCallAdmissionState.Claimed),
            failedRetryableState = (RedisValue)StateValue(IncomingCallAdmissionState.FailedRetryable),
            nowMs = (RedisValue)now.ToUnixTimeMilliseconds(),
            leaseUntil = (RedisValue)(now + _leaseDuration).ToUnixTimeMilliseconds(),
            recordTtlMs = (RedisValue)(long)_recordLifetime.TotalMilliseconds
        }).ConfigureAwait(false);

        var admission = await GetRequiredAsync(serverCallId).ConfigureAwait(false);
        return new IncomingCallAdmissionResult(
            (long)acquired == 1 ? IncomingCallAdmissionOutcome.Acquired : Classify(admission, now),
            admission);
    }

    public Task<bool> MarkAnsweringAsync(string serverCallId, CancellationToken cancellationToken = default)
        => TransitionAsync(
            serverCallId,
            IncomingCallAdmissionState.Claimed,
            IncomingCallAdmissionState.Answering,
            _timeProvider.GetUtcNow() + _leaseDuration,
            callConnectionId: null,
            cancellationToken);

    public Task<bool> MarkAnsweredAsync(
        string serverCallId,
        string callConnectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callConnectionId);
        return TransitionAsync(
            serverCallId,
            IncomingCallAdmissionState.Answering,
            IncomingCallAdmissionState.Answered,
            _timeProvider.GetUtcNow(),
            callConnectionId,
            cancellationToken);
    }

    public Task<bool> MarkAnswerFailedAsync(
        string serverCallId,
        bool retryable,
        CancellationToken cancellationToken = default)
        => FailAsync(serverCallId, retryable, cancellationToken);

    public async Task<IncomingCallAdmission?> GetAsync(
        string serverCallId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverCallId);
        cancellationToken.ThrowIfCancellationRequested();
        return await ReadAsync(serverCallId).ConfigureAwait(false);
    }

    private async Task<bool> TransitionAsync(
        string serverCallId,
        IncomingCallAdmissionState expectedState,
        IncomingCallAdmissionState nextState,
        DateTimeOffset leaseUntil,
        string? callConnectionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverCallId);
        cancellationToken.ThrowIfCancellationRequested();

        var result = await _connection.GetDatabase().ScriptEvaluateAsync(TransitionScript, new
        {
            key = (RedisKey)CoordinationRedisKeys.IncomingCallAdmission(serverCallId),
            expectedState = (RedisValue)StateValue(expectedState),
            instanceId = (RedisValue)_identity.InstanceId,
            nextState = (RedisValue)StateValue(nextState),
            leaseUntil = (RedisValue)leaseUntil.ToUnixTimeMilliseconds(),
            callConnectionId = (RedisValue)(callConnectionId ?? string.Empty),
            recordTtlMs = (RedisValue)(long)_recordLifetime.TotalMilliseconds
        }).ConfigureAwait(false);
        return (long)result == 1;
    }

    private async Task<bool> FailAsync(
        string serverCallId,
        bool retryable,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverCallId);
        cancellationToken.ThrowIfCancellationRequested();

        var result = await _connection.GetDatabase().ScriptEvaluateAsync(FailScript, new
        {
            key = (RedisKey)CoordinationRedisKeys.IncomingCallAdmission(serverCallId),
            claimedState = (RedisValue)StateValue(IncomingCallAdmissionState.Claimed),
            answeringState = (RedisValue)StateValue(IncomingCallAdmissionState.Answering),
            instanceId = (RedisValue)_identity.InstanceId,
            nextState = (RedisValue)StateValue(retryable
                ? IncomingCallAdmissionState.FailedRetryable
                : IncomingCallAdmissionState.RecoveryRequired),
            leaseUntil = (RedisValue)_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            recordTtlMs = (RedisValue)(long)_recordLifetime.TotalMilliseconds
        }).ConfigureAwait(false);
        return (long)result == 1;
    }

    private async Task<IncomingCallAdmission> GetRequiredAsync(string serverCallId)
        => await ReadAsync(serverCallId).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"IncomingCall admission '{serverCallId}' disappeared during an atomic operation.");

    private async Task<IncomingCallAdmission?> ReadAsync(string serverCallId)
    {
        var values = await _connection.GetDatabase()
            .HashGetAsync(CoordinationRedisKeys.IncomingCallAdmission(serverCallId), Fields)
            .ConfigureAwait(false);
        if (values[0].IsNull)
        {
            return null;
        }

        return new IncomingCallAdmission(
            serverCallId,
            values[0]!,
            values[1]!,
            values[2]!,
            values[3]!,
            (IncomingCallAdmissionState)int.Parse(values[4]!),
            DateTimeOffset.FromUnixTimeMilliseconds((long)values[5]),
            values[6].IsNullOrEmpty ? null : values[6].ToString());
    }

    private static IncomingCallAdmissionOutcome Classify(IncomingCallAdmission admission, DateTimeOffset now)
        => admission.State switch
        {
            IncomingCallAdmissionState.Answered => IncomingCallAdmissionOutcome.AlreadyAnswered,
            IncomingCallAdmissionState.RecoveryRequired => IncomingCallAdmissionOutcome.RecoveryRequired,
            IncomingCallAdmissionState.Answering when admission.LeaseUntil <= now
                => IncomingCallAdmissionOutcome.RecoveryRequired,
            _ => IncomingCallAdmissionOutcome.Busy
        };

    private static string StateValue(IncomingCallAdmissionState state) => ((int)state).ToString();
}
