using Agents.AI.ContactCenter.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Agents.AI.ContactCenter.Coordination.Core;

/// <summary>
/// Distributed <see cref="ITierCeilingProvider"/> backed by Azure Managed
/// Redis Enterprise per ADR-0008. The cluster ceiling lives at
/// <c>ceiling:cluster:{clusterId}</c>; a Pub/Sub channel of the same name
/// fans changes out to every pod in the cluster so the answer-path read of
/// <see cref="Current"/> is in-process.
/// </summary>
/// <remarks>
/// <para>
/// Runs as an <see cref="IHostedService"/> so the subscription is established
/// before the first <c>IncomingCall</c> arrives and torn down cleanly on
/// shutdown. Register via
/// <see cref="CoordinationServiceCollectionExtensions.AddRedisTierCeilingProvider"/>,
/// which wires both the <see cref="ITierCeilingProvider"/> and the
/// <see cref="IHostedService"/> registrations against the same singleton.
/// </para>
/// <para>
/// On a missed Pub/Sub message the cached value drifts. The Pod heartbeat in
/// ADR-0011 will eventually call <see cref="RefreshAsync"/> as a periodic
/// safety net; until then admission may briefly admit one tier above or
/// below the true ceiling, which the per-tier capacity counter
/// (<c>cap:tier:*</c>) catches independently.
/// </para>
/// </remarks>
public sealed class RedisTierCeilingProvider : ITierCeilingProvider, IHostedService
{
    private readonly IConnectionMultiplexer _connection;
    private readonly IClusterIdentity _identity;
    private readonly ILogger<RedisTierCeilingProvider> _logger;
    private int _current;
    private ChannelMessageQueue? _subscription;

    public RedisTierCeilingProvider(
        IConnectionMultiplexer connection,
        IClusterIdentity identity,
        IOptions<HyperscaleOptions> options,
        ILogger<RedisTierCeilingProvider> logger)
    {
        _connection = connection;
        _identity = identity;
        _logger = logger;
        _current = (int)options.Value.TierCeiling.DefaultCeiling;
    }

    public AgentTier Current => (AgentTier)Volatile.Read(ref _current);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var channel = new RedisChannel(GetChannelName(), RedisChannel.PatternMode.Literal);
        var subscriber = _connection.GetSubscriber();
        _subscription = await subscriber.SubscribeAsync(channel).ConfigureAwait(false);
        _subscription.OnMessage(OnInvalidation);

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
        {
            await _subscription.UnsubscribeAsync().ConfigureAwait(false);
            _subscription = null;
        }
    }

    public async Task<AgentTier> RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var db = _connection.GetDatabase();
        var value = await db.StringGetAsync(GetKey()).ConfigureAwait(false);
        if (value.IsNullOrEmpty)
        {
            return Current;
        }

        if (TryParseCeiling(value!, out var ceiling))
        {
            Volatile.Write(ref _current, (int)ceiling);
        }

        return Current;
    }

    public async Task SetAsync(AgentTier ceiling, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var key = GetKey();
        var channel = new RedisChannel(GetChannelName(), RedisChannel.PatternMode.Literal);
        var encoded = (RedisValue)(int)ceiling;

        var db = _connection.GetDatabase();
        var subscriber = _connection.GetSubscriber();

        await db.StringSetAsync(key, encoded).ConfigureAwait(false);
        Volatile.Write(ref _current, (int)ceiling);
        await subscriber.PublishAsync(channel, encoded).ConfigureAwait(false);
    }

    private void OnInvalidation(ChannelMessage message)
    {
        if (TryParseCeiling(message.Message.ToString(), out var ceiling))
        {
            Volatile.Write(ref _current, (int)ceiling);
        }
        else
        {
            _logger.LogWarning("Ignoring malformed tier ceiling broadcast on {Channel}: {Payload}", message.Channel, message.Message);
        }
    }

    private string GetKey() => CoordinationRedisKeys.ClusterTierCeiling(_identity.ClusterId);

    private string GetChannelName() => CoordinationRedisKeys.ClusterTierCeiling(_identity.ClusterId);

    internal static bool TryParseCeiling(string? message, out AgentTier ceiling)
    {
        if (int.TryParse(message, out var raw) && Enum.IsDefined(typeof(AgentTier), raw))
        {
            ceiling = (AgentTier)raw;
            return true;
        }

        ceiling = default;
        return false;
    }
}
