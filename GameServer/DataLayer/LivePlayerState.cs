using SharedLibrary;

namespace GameServer.DataLayer
{
    // ── LivePlayerState ────────────────────────────────────────────────────────────────────
    //
    // A point-in-time, serialisable snapshot of everything a player server needs to know
    // about a player to restore their session after a crash, zone transfer, or rejoin.
    //
    // Written to Redis under key "live-state:{accountId}" with a 2-hour TTL:
    //   • Every 60 s as a heartbeat (so a crash loses at most 60 s of progress).
    //   • Immediately on a zone-transfer handoff.
    //   • At match end (Arena) or logout (MMO).
    //
    // The ProfileServer reads this key when a player starts the next session so it can
    // restore their last known position, health, and inventory without touching PostgreSQL
    // during the hot path.
    // ──────────────────────────────────────────────────────────────────────────────────────

    public sealed class LivePlayerState
    {
        public int           AccountId     { get; set; }
        public string        PlayerName    { get; set; } = string.Empty;

        // Authoritative world position at snapshot time.
        public Vec2          Position      { get; set; }

        // Current and max health at snapshot time.  On load the receiving server clamps
        // current health to the new MaxHealth in case equipment changed between sessions.
        public float         Health        { get; set; }
        public float         MaxHealth     { get; set; }

        // Gear set index that was active at snapshot time (0 or 1).
        public int           ActiveGearSet { get; set; }

        // Inventory at snapshot time.  May be an empty array in Arena mode — see
        // PlayerSession.TakeSnapshot(includeInventory: false).
        public ItemInstance[] Inventory    { get; set; } = System.Array.Empty<ItemInstance>();

        // Both gear-set loadouts.  Stored so the receiving zone can reconstruct equipped
        // items and compute the correct authoritative stats on load.
        public GearSetLoadout[] GearSets  { get; set; } = System.Array.Empty<GearSetLoadout>();

        // The zone the player should be placed into on next login (set to the current zone
        // during a heartbeat, updated to the target zone just before a zone-transfer publish).
        // Empty string means "use character default zone" (handled by ProfileServer).
        public string        TargetZoneId  { get; set; } = string.Empty;

        // Unix epoch milliseconds when this snapshot was taken.  Used by the receiving zone
        // to detect stale snapshots (e.g. if two servers both write within the same window).
        public long          SnapshotTimeMs { get; set; }
    }
}
