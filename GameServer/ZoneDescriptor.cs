using SharedLibrary;

namespace GameServer
{
    /// <summary>
    /// Describes the static topology and rules of a single zone or arena instance.
    /// Injected into <see cref="ArenaInstance"/> at startup so all map-specific constants
    /// live in one place rather than being scattered as hardcoded fields across the
    /// simulation loop.
    ///
    /// To add a new map or zone type: construct a <see cref="ZoneDescriptor"/> with the
    /// appropriate bounds, spawn points, view radius, win condition, and interest filter.
    /// No changes to <see cref="ArenaInstance"/>, movement math, or any system code are needed.
    /// </summary>
    public sealed class ZoneDescriptor
    {
        /// <summary>Human-readable identifier used in logs and Redis zone-transfer keys.</summary>
        public string ZoneId { get; init; } = "arena-default";

        /// <summary>
        /// Navigable world boundaries enforced by <see cref="CombatMath.Move"/> during
        /// movement validation.  Replaces the old compile-time constant ArenaBoundsHalf.
        /// </summary>
        public WorldBounds Bounds { get; init; } = WorldBounds.DefaultArena;

        /// <summary>
        /// Distance (world units) beyond which entities are not replicated to a viewer.
        /// For arena maps this should cover the full map diagonal.
        /// For large open-world zones, reduce this to cut O(N²) replication bandwidth.
        /// </summary>
        public float ViewRadius { get; init; } = 120f;

        /// <summary>
        /// Faction spawn points indexed by <see cref="FactionId"/> cast to int.
        /// Index 0 = Alpha, index 1 = Beta.  Must have exactly two entries.
        /// </summary>
        public Vec2[] FactionSpawnPoints { get; init; } = new[]
        {
            new Vec2(-30f, 0f),  // Alpha
            new Vec2( 30f, 0f),  // Beta
        };

        /// <summary>
        /// Win condition evaluated once per tick after all combat resolution.
        /// Use <see cref="EliminationWinCondition"/> for arena deathmatch.
        /// Use <see cref="NoWinCondition"/> for open-world MMO zones that run indefinitely.
        /// </summary>
        public IWinCondition WinCondition { get; init; } = new EliminationWinCondition();

        /// <summary>
        /// Determines which connected peers receive a given event packet based on proximity
        /// to the event's world-space origin.
        ///
        /// Arena zones use <see cref="BroadcastFilter"/> (everyone receives everything).
        /// Open-world zones use a <see cref="RadiusFilter"/> to cull distant players from
        /// combat events, preventing the O(N) per-event send cost from becoming an O(N²)
        /// bottleneck as the active player count grows.
        /// </summary>
        public IInterestFilter EventFilter { get; init; } = BroadcastFilter.Instance;

        /// <summary>
        /// Maximum number of item slots in a player's inventory within this zone.
        /// Ground-pickup requests are rejected server-side when the inventory is full.
        /// </summary>
        public int MaxInventorySize { get; init; } = 20;

        /// <summary>
        /// When <c>true</c>, this is a closed Arena match; in-session picked-up items are
        /// NOT flushed to persistent storage.  Players receive crafting ingredient rewards
        /// at match end instead.
        ///
        /// When <c>false</c> (open-world MMO zone), the full inventory is included in
        /// heartbeat flushes so zone transfers and server crashes don't lose item pickups.
        /// </summary>
        public bool IsArenaMode { get; init; } = true;

        /// <summary>
        /// How long (in ticks) a disconnected player's session is preserved before being
        /// permanently removed from the simulation.  Players who reconnect within this window
        /// have their session restored (Dota2-style rejoin).
        ///
        /// Default: 9 000 ticks = 5 minutes at 30 Hz.
        /// </summary>
        public int RejoinGraceTicks { get; init; } = 9000;

        /// <summary>Returns the authoritative spawn position for a given faction.</summary>
        public Vec2 GetSpawnPoint(FactionId faction)
        {
            int index = (int)faction;
            return index >= 0 && index < FactionSpawnPoints.Length
                ? FactionSpawnPoints[index]
                : Vec2.Zero;
        }
    }
}
