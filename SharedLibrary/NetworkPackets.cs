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
}