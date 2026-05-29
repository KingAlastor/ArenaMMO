using SharedLibrary;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace GameServer.DataLayer
{
    // ── Data contracts ────────────────────────────────────────────────────────

    /// <summary>
    /// One item instance in a player's inventory.
    /// DefinitionId references ItemDatabase for the archetype (slot, name, base stats).
    /// InstanceId uniquely identifies this ownership record — two "Iron Swords" have
    /// different InstanceIds even though they share a DefinitionId.
    ///
    /// CraftedStats: non-null when the player has customised this item via the crafting system.
    /// At runtime, RecomputeStats() uses CraftedStats in preference to the archetype's base
    /// stats, so the GameServer never needs to know anything about how crafting works.
    /// </summary>
    public class ItemInstance
    {
        public int                InstanceId   { get; set; }
        public int                DefinitionId { get; set; }
        /// <summary>
        /// Per-instance crafted stat overrides. Null = use the archetype's default stats from
        /// ItemDatabase. Non-null = fully replaces the archetype stats for this instance only.
        /// Serialized into Redis by the lobby after crafting; the GameServer treats it as opaque data.
        /// </summary>
        public ItemStatModifiers? CraftedStats { get; set; }
    }

    /// <summary>
    /// A preset gear set loadout configured by the player in the lobby.
    /// Maps EquipSlot (stored as int key for JSON compatibility) to an ItemInstance.InstanceId
    /// from the player's inventory. Slots absent from the dictionary are left empty.
    /// </summary>
    public class GearSetLoadout
    {
        public string Name { get; set; } = string.Empty;
        /// <summary>EquipSlot (as int) → ItemInstance.InstanceId. Absent key = empty slot.</summary>
        public Dictionary<int, int> SlotItems { get; set; } = new Dictionary<int, int>();
    }

    /// <summary>
    /// Player profile cached in Redis by the Lobby server before an arena match starts.
    /// Loaded once per connection; never re-read during the tick loop.
    /// </summary>
    public class PlayerProfile
    {
        public int    AccountId   { get; set; }
        public string PlayerName  { get; set; } = string.Empty;

        // Base character stats applied before any equipment bonuses.
        // Set by the lobby from character class and progression data.
        public float BaseMaxHealth             { get; set; } = 100f;
        public float BaseAttackPower           { get; set; } = 1.0f;
        public float BasePhysicalAbsorbPercent { get; set; } = 0f;
        public float BasePhysicalResistPercent { get; set; } = 0f;
        public float BaseMagicAbsorbPercent    { get; set; } = 0f;
        public float BaseMagicResistPercent    { get; set; } = 0f;
        public float BaseCritChance            { get; set; } = 0.05f;
        public float BaseMeleeLifeStealPercent { get; set; } = 0f;

        /// <summary>Items available to the player during this match. Populated in the lobby (max ~7).</summary>
        public ItemInstance[] Inventory { get; set; } = Array.Empty<ItemInstance>();

        /// <summary>
        /// Up to 2 preset gear set loadouts configured in the lobby.
        /// Index 0 is equipped on spawn; index 1 is the alternate quickswap set.
        /// </summary>
        public GearSetLoadout[] GearSets { get; set; } = Array.Empty<GearSetLoadout>();
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

        /// <summary>
        /// Crafting ingredient rewards earned during this match (participation + win bonus + kills).
        /// Written to Redis key <c>crafting-reward:{accountId}</c> so the ProfileServer can
        /// credit the player's ingredient pouch on their next lobby session.
        ///
        /// In Arena mode, items picked up during the match are intentionally NOT persisted
        /// (loot drops are match-scoped).  These ingredient rewards are the only durable
        /// progression output from an Arena match.
        /// </summary>
        public CraftingIngredientReward[] CraftingRewards { get; set; } = System.Array.Empty<CraftingIngredientReward>();
    }

    /// <summary>
    /// A single crafting ingredient reward line item: ingredient type + quantity earned.
    /// Serialised to the CSV string in <see cref="CraftingRewardPacket"/> for the client
    /// and separately to Redis for the ProfileServer.
    /// </summary>
    public class CraftingIngredientReward
    {
        public int IngredientId { get; set; }
        public int Quantity     { get; set; }
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
        private static string ProfileKey(int accountId)        => $"profile:{accountId}";
        private static string MatchResultKey(int accountId)    => $"match-result:{accountId}";
        private static string CraftingRewardKey(int accountId) => $"crafting-reward:{accountId}";

        /// <summary>
        /// Heartbeat / zone-handoff state writer backed by the same Redis connection.
        /// ArenaInstance calls <c>Sink.FlushAsync(player.TakeSnapshot(...))</c> every 60 s
        /// and at match end for crash recovery and zone transfers.
        /// </summary>
        public PlayerStateSink Sink { get; }

        public MatchDataService(string redisConnString, string postgresConnString)
        {
            _mux                = ConnectionMultiplexer.Connect(redisConnString);
            _redis              = _mux.GetDatabase();
            _postgresConnString = postgresConnString;
            Sink                = new PlayerStateSink(_redis);
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
                    // 1a. Write crafting ingredient rewards so ProfileServer can credit the
                    //     player's ingredient pouch without waiting for the SQL write.
                    //     TTL matches match-result so both expire together.
                    if (result.CraftingRewards != null && result.CraftingRewards.Length > 0)
                    {
                        string rewardsJson = JsonSerializer.Serialize(result.CraftingRewards);
                        await _redis.StringSetAsync(
                            CraftingRewardKey(result.AccountId),
                            rewardsJson,
                            expiry: TimeSpan.FromHours(24));
                    }

                    // 2. Write full match result to Redis as fallback queue for the lobby server.
                    string json = JsonSerializer.Serialize(result);
                    await _redis.StringSetAsync(
                        MatchResultKey(result.AccountId),
                        json,
                        expiry: TimeSpan.FromHours(24));

                    // 3. Durable upsert to PostgreSQL.
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
