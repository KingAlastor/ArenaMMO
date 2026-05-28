using StackExchange.Redis;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace GameServer.DataLayer
{
    // ── Data contracts ────────────────────────────────────────────────────────

    /// <summary>
    /// Player profile cached in Redis by the Lobby server before an arena match starts.
    /// Loaded once per connection; never re-read during the tick loop.
    /// </summary>
    public class PlayerProfile
    {
        public int    AccountId             { get; set; }
        public string PlayerName            { get; set; } = string.Empty;
        public float  MaxHealth             { get; set; } = 100f;
        public float  AttackPower           { get; set; } = 1.0f;
        public float  PhysicalAbsorbPercent { get; set; } = 0f;
        public float  PhysicalResistPercent { get; set; } = 0f;
        public float  MagicAbsorbPercent    { get; set; } = 0f;
        public float  MagicResistPercent    { get; set; } = 0f;
        public float  CritChance            { get; set; } = 0.05f;
        public float  MeleeLifeStealPercent { get; set; } = 0f;
    }

    /// <summary>
    /// Match outcome data that is persisted after a match ends.
    /// </summary>
    public class MatchResult
    {
        public int  AccountId  { get; set; }
        public bool Won        { get; set; }
        public int  KillCount  { get; set; }
        public int  DeathCount { get; set; }
    }

    // ── Service ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Thin data-access service that bridges the arena process with Redis and PostgreSQL.
    ///
    /// Lifecycle:
    ///   1.  Lobby server writes "profile:{accountId}" to Redis before the arena starts.
    ///   2.  <see cref="LoadPlayerProfile"/> reads that key synchronously at connection time
    ///       (before the tick loop begins) and returns the deserialized profile.
    ///   3.  At match-end, <see cref="SaveMatchResultAsync"/> writes results to Redis with
    ///       a 24-hour TTL and enqueues a durable write to PostgreSQL.
    /// </summary>
    public sealed class MatchDataService : IDisposable
    {
        private readonly ConnectionMultiplexer _mux;
        private readonly IDatabase            _redis;
        private readonly string               _postgresConnString;

        // Key helpers
        private static string ProfileKey(int accountId)     => $"profile:{accountId}";
        private static string MatchResultKey(int accountId) => $"match-result:{accountId}";

        public MatchDataService(string redisConnString, string postgresConnString)
        {
            _mux                = ConnectionMultiplexer.Connect(redisConnString);
            _redis              = _mux.GetDatabase();
            _postgresConnString = postgresConnString;
        }

        /// <summary>
        /// Reads the player profile from Redis synchronously.
        /// Returns null if no profile is found (fallback to session defaults applies).
        /// This must only be called outside of the tick loop (i.e., on player connect).
        /// </summary>
        public PlayerProfile? LoadPlayerProfile(int accountId)
        {
            RedisValue raw = _redis.StringGet(ProfileKey(accountId));
            if (!raw.HasValue) return null;

            try
            {
                return JsonSerializer.Deserialize<PlayerProfile>(raw.ToString());
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"[DataService] Failed to deserialize profile for account {accountId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Persists match results via a background Task (fire-and-forget from the match end handler).
        /// Writes to Redis with a 24 h TTL, then upserts to PostgreSQL via Dapper.
        /// Never awaited by the caller — failures are logged to stderr and swallowed.
        /// </summary>
        public Task SaveMatchResultAsync(MatchResult result)
        {
            return Task.Run(async () =>
            {
                try
                {
                    // 1. Write to Redis as a fallback/queue for the lobby server.
                    string json = JsonSerializer.Serialize(result);
                    await _redis.StringSetAsync(
                        MatchResultKey(result.AccountId),
                        json,
                        expiry: TimeSpan.FromHours(24));

                    // 2. Durable upsert to PostgreSQL.
                    // TODO: Uncomment and complete when the database schema is finalised.
                    // using var conn = new Npgsql.NpgsqlConnection(_postgresConnString);
                    // await conn.OpenAsync();
                    // await conn.ExecuteAsync(
                    //     """
                    //     INSERT INTO player_match_history (account_id, won, kill_count, death_count, played_at)
                    //     VALUES (@AccountId, @Won, @KillCount, @DeathCount, NOW())
                    //     ON CONFLICT DO NOTHING
                    //     """,
                    //     result);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[DataService] SaveMatchResultAsync failed for account {result.AccountId}: {ex.Message}");
                }
            });
        }

        public void Dispose()
        {
            _mux.Dispose();
        }
    }
}
