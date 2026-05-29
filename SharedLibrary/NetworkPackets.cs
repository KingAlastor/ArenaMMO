namespace SharedLibrary
{
    // ── Enums ─────────────────────────────────────────────────────────────────

    public enum SpellTargetType : byte
    {
        SingleTarget = 0,
        AoE          = 1,
        Projectile   = 2,   // bow / crossbow — server simulates travel each tick
        MeleeSplash  = 3,   // caster-centred AoE within AoERadius (Whirlwind, Cleave, etc.)
    }

    public enum DamageType : byte
    {
        Physical = 0,
        Magic    = 1,
        True     = 2,
    }

    public enum FactionId : byte
    {
        Alpha = 0,
        Beta  = 1,
    }

    public enum StatusEffectVisibility : byte
    {
        AlliesOnly = 0,
        Everyone   = 1,
    }
    public enum TargetFactionFilter : byte
    {
        EnemiesOnly = 0,
        AlliesOnly  = 1,
        Any         = 2,
    }

    // ── Value Types ───────────────────────────────────────────────────────────

    /// <summary>Zero-allocation 2D position/vector used throughout all position math.</summary>
    public struct Vec2
    {
        public float X;
        public float Y;

        public Vec2(float x, float y) { X = x; Y = y; }

        /// <summary>Convenience constant — equivalent to default(Vec2).</summary>
        public static readonly Vec2 Zero = new Vec2(0f, 0f);
    }

    // ── Client → Server Packets ───────────────────────────────────────────────

    /// <summary>Sent every tick the player holds a movement key.</summary>
    public class PlayerInputPacket
    {
        public int   TickNumber { get; set; }
        /// <summary>
        /// Quantized input axis: -127..127 maps to -1..1.
        /// Using sbyte eliminates NaN/Inf and makes client/server dequantization identical.
        /// </summary>
        public sbyte InputX { get; set; }
        public sbyte InputY { get; set; }
    }

    /// <summary>Sent when the player performs a basic melee auto-attack.</summary>
    public class AttackRequestPacket
    {
        public int TickNumber     { get; set; }
        public int ActionSequenceId { get; set; }
        public int TargetEntityId { get; set; }
    }

    /// <summary>
    /// Sent when the player activates a spell.
    /// Single-target spells: set TargetEntityId.
    /// Ground-targeted AoE spells: set AoECenterX / AoECenterY (TargetEntityId = 0).
    /// The server derives TargetType from its own SpellDatabase — never trust the client.
    /// </summary>
    public class SpellCastRequestPacket
    {
        public int   TickNumber     { get; set; }
        public int   ActionSequenceId { get; set; }
        public int   SpellId        { get; set; }
        public int   TargetEntityId { get; set; }
        public float AoECenterX     { get; set; }
        public float AoECenterY     { get; set; }
    }

    /// <summary>
    /// Sent when the player wants to activate one of their pre-loaded gear sets.
    /// SetIndex must be 0 or 1 and must correspond to a gear set that was pre-loaded from Redis.
    /// Gear swaps are always permitted; the old respawn-window restriction has been removed now
    /// that an inventory system is in place.
    /// </summary>
    public class GearSetSwapRequestPacket
    {
        public byte SetIndex { get; set; }  // 0 = primary set, 1 = alternate set
    }

    /// <summary>
    /// Sent when the player equips or unequips an individual item from their inventory.
    /// Equip: set ItemInstanceId to the item's InstanceId. The server derives the target
    /// slot from the item definition — the client cannot override slot assignment.
    /// Unequip: set ItemInstanceId to 0 and Slot to the slot to clear.
    /// Always honoured — gear changes are permitted at any time now that an inventory system
    /// is in place; the old respawn-window restriction has been removed.
    /// </summary>
    public class EquipItemRequestPacket
    {
        /// <summary>InstanceId of the item to equip. 0 = unequip the specified Slot.</summary>
        public int      ItemInstanceId { get; set; }
        /// <summary>Only used when ItemInstanceId == 0 to identify which slot to clear.</summary>
        public EquipSlot Slot          { get; set; }
    }

    /// <summary>
    /// Sent once after connection to prove lobby-issued identity and authorization.
    /// Signature is HMAC-SHA256 over canonical ticket fields (without Signature).
    /// </summary>
    public class AuthTicketPacket
    {
        public int   PlayerId        { get; set; }
        public string PlayerName     { get; set; } = string.Empty;
        public byte  Faction         { get; set; }
        public string AllowedSpellIdsCsv { get; set; } = string.Empty;
        public long  IssuedAtUnixMs  { get; set; }
        public long  ExpiresAtUnixMs { get; set; }
        public string Nonce          { get; set; } = string.Empty;
        public string Signature      { get; set; } = string.Empty;
    }

    // ── Server → Client Packets ───────────────────────────────────────────────

    /// <summary>Broadcast every tick with the authoritative position of one entity.</summary>
    public class EntityPositionPacket
    {
        public int   EntityId         { get; set; }
        public float X                { get; set; }
        public float Y                { get; set; }
        /// <summary>The server tick that produced this snapshot. Used by the client to replay buffered inputs during reconciliation.</summary>
        public int   ServerTick       { get; set; }
        /// <summary>The last client TickNumber the server consumed for this entity. The client discards buffered inputs older than this before replaying.</summary>
        public int   AcknowledgedTick { get; set; }
    }

    /// <summary>Broadcast only to clients that are allowed to see the entity's health.</summary>
    public class EntityHealthPacket
    {
        public int   EntityId { get; set; }
        public float Health   { get; set; }
    }

    /// <summary>Broadcast when a melee attack or single-target spell lands.</summary>
    public class CombatEventPacket
    {
        public int  AttackerId { get; set; }
        public int  TargetId   { get; set; }
        public int  Damage     { get; set; }
        public bool IsCritical { get; set; }
    }

    /// <summary>
    /// Broadcast once per entity hit inside an AoE.
    /// The client correlates multiple packets by CasterId + SpellId to play
    /// one VFX while applying damage to each unique HitEntityId.
    /// </summary>
    public class AoEHitEventPacket
    {
        public int  CasterId    { get; set; }
        public int  SpellId     { get; set; }
        public int  HitEntityId { get; set; }
        public int  Damage      { get; set; }
        public bool IsCritical  { get; set; }
    }

    /// <summary>
    /// Broadcast when a status effect is applied or refreshed on a target.
    /// The server filters delivery based on StatusEffectVisibility.
    /// </summary>
    public class StatusEffectAppliedPacket
    {
        public int                    TargetEntityId { get; set; }
        public int                    SourceEntityId { get; set; }
        public int                    EffectId       { get; set; }
        public int                    RemainingTicks { get; set; }
        public int                    Stacks         { get; set; }
        public StatusEffectVisibility Visibility     { get; set; }
    }

    /// <summary>
    /// Broadcast when a status effect expires or is removed.
    /// </summary>
    public class StatusEffectRemovedPacket
    {
        public int                    TargetEntityId { get; set; }
        public int                    EffectId       { get; set; }
        public StatusEffectVisibility Visibility     { get; set; }
    }

    // ── Projectile Packets ────────────────────────────────────────────────────

    /// <summary>
    /// Sent by the client to fire a bow or crossbow.
    /// Direction must be the normalised world-space aim vector (the server re-normalises it).
    /// The server spawns the projectile at the shooter's authoritative position.
    /// </summary>
    public class ShootRequestPacket
    {
        public int   TickNumber  { get; set; }
        public int   ActionSequenceId { get; set; }
        public int   SpellId     { get; set; }
        public float DirectionX  { get; set; }  // normalised aim direction
        public float DirectionY  { get; set; }
    }

    /// <summary>
    /// Broadcast when the server spawns a projectile so Unity can render it
    /// and interpolate its visual position independently of server ticks.
    /// </summary>
    public class ProjectileSpawnPacket
    {
        public int   ProjectileId { get; set; }
        public int   OwnerId      { get; set; }
        public int   SpellId      { get; set; }
        public float StartX       { get; set; }
        public float StartY       { get; set; }
        public float DirectionX   { get; set; }
        public float DirectionY   { get; set; }
        public float Speed        { get; set; }
        /// <summary>
        /// Authoritative maximum travel distance. Unity uses this to despawn the visual
        /// client-side and can also drive a range-indicator or crosshair-fade effect.
        /// </summary>
        public float MaxRange     { get; set; }
    }

    /// <summary>
    /// Broadcast when the server removes a projectile — either because it hit
    /// a target (HitSomething = true, a CombatEventPacket is also sent) or
    /// because it exceeded its maximum travel range (HitSomething = false).
    /// </summary>
    public class ProjectileDestroyPacket
    {
        public int  ProjectileId { get; set; }
        public bool HitSomething { get; set; }
    }

    // ── Entity lifecycle packets ───────────────────────────────────────────────────────

    /// <summary>
    /// Sent to all existing peers when a new player joins the match,
    /// and to the joining peer for each already-connected player.
    /// The client must create and register an entity for EntityId on receipt.
    /// </summary>
    public class EntitySpawnPacket
    {
        public int    EntityId   { get; set; }
        public string PlayerName { get; set; } = string.Empty;
        public byte   Faction    { get; set; }
        public float  X          { get; set; }
        public float  Y          { get; set; }
    }

    /// <summary>
    /// Sent to all peers when a player leaves the match permanently (disconnect).
    /// The client must destroy the entity for EntityId on receipt.
    /// </summary>
    public class EntityDespawnPacket
    {
        public int EntityId { get; set; }
    }

    // ── Match flow packets ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Broadcast when a player's health reaches zero.
    /// The client plays a death animation and suppresses movement input for the entity.
    /// KillerEntityId is 0 when the kill source cannot be attributed.
    /// </summary>
    public class PlayerDeathPacket
    {
        public int KilledEntityId { get; set; }
        public int KillerEntityId { get; set; }
    }

    /// <summary>
    /// Broadcast when a dead player's respawn timer expires and they re-enter play.
    /// The client repositions and plays a spawn animation.
    /// </summary>
    public class PlayerRespawnPacket
    {
        public int   EntityId { get; set; }
        public float X        { get; set; }
        public float Y        { get; set; }
        public float Health   { get; set; }
    }

    /// <summary>
    /// Sent only to the owning client after the server applies a gear set swap.
    /// Carries the full authoritative stat snapshot so the client can update its HUD and
    /// character sheet without needing to re-request stats.
    /// </summary>
    public class PlayerStatsRefreshedPacket
    {
        public byte  ActiveGearSetIndex    { get; set; }
        public float MaxHealth             { get; set; }
        public float AttackPower           { get; set; }
        public float PhysicalAbsorbPercent { get; set; }
        public float PhysicalResistPercent { get; set; }
        public float MagicAbsorbPercent    { get; set; }
        public float MagicResistPercent    { get; set; }
        public float CritChance            { get; set; }
        public float MeleeLifeStealPercent { get; set; }
    }

    /// <summary>
    /// Broadcast once when the win condition is satisfied. WinnerFaction maps to FactionId.
    /// The server shuts down after sending this packet.
    /// </summary>
    public class MatchEndPacket
    {
        public byte WinnerFaction { get; set; }
    }

    // ── Ground-item Packets ───────────────────────────────────────────────────

    /// <summary>Client → Server: player wants to pick up a ground item by its server-assigned ID.</summary>
    public class GroundItemPickupRequestPacket
    {
        public int GroundItemId { get; set; }
    }

    /// <summary>
    /// Server → All: a lootable item has appeared on the ground.
    /// The client uses DefinitionId to look up the item icon and name.
    /// InstanceId is included so the client can display stack counts for stackable items.
    /// </summary>
    public class GroundItemSpawnedPacket
    {
        public int   GroundItemId { get; set; }
        public int   DefinitionId { get; set; }
        public float X            { get; set; }
        public float Y            { get; set; }
    }

    /// <summary>
    /// Server → All (interested): a ground item was picked up or despawned.
    /// Clients destroy the world-object for this GroundItemId on receipt.
    /// </summary>
    public class GroundItemRemovedPacket
    {
        public int GroundItemId { get; set; }
    }

    /// <summary>
    /// Server → owning client: confirms that an item was added to the player's inventory.
    /// Sent in addition to <see cref="GroundItemRemovedPacket"/> after a successful pickup.
    /// The client adds the item to its inventory panel on receipt.
    /// </summary>
    public class ItemAddedToInventoryPacket
    {
        public int DefinitionId { get; set; }
        public int InstanceId   { get; set; }
    }

    // ── Session continuity packets ──────────────────────────────────────────────

    /// <summary>
    /// Server → All: a player's UDP connection dropped but their session is preserved
    /// for up to the grace-period window (default 5 minutes).  Their entity remains in the
    /// world as a stationary target; the client should display a disconnected indicator.
    ///
    /// If the player reconnects within the grace period they receive
    /// <see cref="PlayerReconnectedPacket"/> and resume normally.
    /// If the grace period expires they receive <see cref="EntityDespawnPacket"/> instead.
    /// </summary>
    public class PlayerGraceDisconnectPacket
    {
        public int EntityId { get; set; }
    }

    /// <summary>
    /// Server → All: a player who was in the grace-period window has successfully reconnected.
    /// The client removes any disconnected indicator and resumes treating the entity as live.
    /// </summary>
    public class PlayerReconnectedPacket
    {
        public int EntityId { get; set; }
    }

    // ── Arena end-of-match reward packets ──────────────────────────────────────

    /// <summary>
    /// Server → owning client: sent at Arena match end with the crafting ingredient rewards
    /// the player has earned.  Items picked up during the match do NOT persist in Arena mode;
    /// rewards are always crafting ingredients added to the character's crafting pouch.
    ///
    /// Format: comma-separated "ingredientId:quantity" pairs, e.g. "1:3,5:1".
    /// The ProfileServer claims these from Redis key <c>crafting-reward:{accountId}</c>.
    /// </summary>
    public class CraftingRewardPacket
    {
        public string RewardsCsv { get; set; } = string.Empty;
    }

    // ── Lobby Packets ─────────────────────────────────────────────────────────

    /// <summary>Client → LobbyServer: authenticate with a credential token.</summary>
    public class LobbyLoginRequestPacket
    {
        public string PlayerName      { get; set; } = string.Empty;
        /// <summary>
        /// Opaque credential token validated server-side (e.g. hashed password, JWT, Steam ticket).
        /// Never used as a plaintext password — the lobby verifies it against its own auth scheme.
        /// </summary>
        public string CredentialToken { get; set; } = string.Empty;
    }

    /// <summary>LobbyServer → Client: result of the login attempt.</summary>
    public class LobbyLoginResponsePacket
    {
        public bool   Success    { get; set; }
        public int    PlayerId   { get; set; }
        public string PlayerName { get; set; } = string.Empty;
        public string Error      { get; set; } = string.Empty;
    }

    /// <summary>Client → LobbyServer: enter the matchmaking queue.</summary>
    public class LobbyQueueJoinPacket
    {
        // Reserved for future use: preferred game mode, region, etc.
    }

    /// <summary>LobbyServer → Client: periodic queue position update.</summary>
    public class LobbyQueueStatusPacket
    {
        public int QueuePosition  { get; set; }
        public int PlayersInQueue { get; set; }
        public int PlayersNeeded  { get; set; }
    }

    /// <summary>
    /// LobbyServer → Client: a match has been formed.
    /// Contains arena connection info plus all fields required to build an AuthTicketPacket.
    /// The client connects to ArenaIp:ArenaPort and sends these fields verbatim as AuthTicketPacket.
    /// </summary>
    public class MatchFoundPacket
    {
        public string ArenaIp            { get; set; } = string.Empty;
        public int    ArenaPort          { get; set; }
        // AuthTicket fields — signed by the lobby, verified by the arena's AuthTicketValidator.
        public int    PlayerId           { get; set; }
        public string PlayerName         { get; set; } = string.Empty;
        public byte   Faction            { get; set; }
        public string AllowedSpellIdsCsv { get; set; } = string.Empty;
        public long   IssuedAtUnixMs     { get; set; }
        public long   ExpiresAtUnixMs    { get; set; }
        public string Nonce              { get; set; } = string.Empty;
        public string Signature          { get; set; } = string.Empty;
    }
}