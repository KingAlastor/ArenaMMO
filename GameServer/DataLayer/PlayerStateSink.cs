using System.Text.Json;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace GameServer.DataLayer
{
    // ── PlayerStateSink ───────────────────────────────────────────────────────────────────
    //
    // WHY THIS CLASS EXISTS:
    //
    //  1. Crash recovery / heartbeat
    //     The game server writes each player's live state to Redis every 60 seconds.
    //     If the process crashes mid-match, the ProfileServer can pick up the last known
    //     state (position, health, inventory) from Redis rather than rolling back the
    //     character to the start of the session.  At most 60 s of progress is lost.
    //
    //  2. Zone handoff (server-to-server transfer)
    //     When a player crosses a zone boundary the current zone server calls FlushAsync()
    //     with TargetZoneId set to the next zone, then publishes a ZoneTransferPayload to
    //     Redis Pub/Sub.  The target zone server reads the live-state key to bootstrap the
    //     player session without a PostgreSQL round-trip during the tick loop.
    //
    //  3. Graceful shutdown
    //     On SIGTERM the server calls FlushAsync() for every connected player so that
    //     nothing is lost on a rolling deployment.
    //
    // REDIS KEY SCHEMA:
    //     live-state:{accountId}   → JSON(LivePlayerState)   TTL: 2 hours
    //
    //  The 2-hour TTL ensures stale entries are cleaned up automatically even if the server
    //  crashes without a shutdown flush.
    // ──────────────────────────────────────────────────────────────────────────────────────

    public sealed class PlayerStateSink
    {
        private readonly IDatabase _redis;

        // JSON options shared across all serialisation calls to avoid repeated reflection.
        private static readonly JsonSerializerOptions s_jsonOptions =
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        private static readonly TimeSpan s_ttl = System.TimeSpan.FromHours(2);

        public PlayerStateSink(IDatabase redis)
        {
            _redis = redis;
        }

        /// <summary>
        /// Writes <paramref name="state"/> to Redis under <c>live-state:{accountId}</c>
        /// with a 2-hour TTL.
        ///
        /// Fire-and-forget from the game loop is fine:
        /// <code>
        ///   _ = _dataService.Sink.FlushAsync(player.TakeSnapshot(!_zone.IsArenaMode));
        /// </code>
        /// The async machinery queues the write on a thread-pool thread.  The tick loop is
        /// never blocked because <see cref="IDatabase.StringSetAsync"/> is non-blocking.
        /// </summary>
        public async Task FlushAsync(LivePlayerState state)
        {
            string key   = $"live-state:{state.AccountId}";
            string value = JsonSerializer.Serialize(state, s_jsonOptions);

            await _redis.StringSetAsync(key, value, s_ttl).ConfigureAwait(false);
        }
    }
}
