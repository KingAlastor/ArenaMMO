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
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct Vec2
    {
        public float X;
        public float Y;

        public Vec2(float x, float y) { X = x; Y = y; }

        /// <summary>Convenience constant — equivalent to default(Vec2).</summary>
        public static readonly Vec2 Zero = new Vec2(0f, 0f);
    }

    // ── Packet Compression Helpers ────────────────────────────────────────────
    //
    // Fixed-point position encoding:
    //   World coordinates are assumed to fit within ±2048 units (max MMORPG zone size).
    //   Multiplying by PositionScale (16) gives a range of ±32768, exactly fitting a short.
    //   Precision = 1/16 = 0.0625 world units — sufficient for collision and rendering.
    //   Savings: 4 bytes (float) → 2 bytes (short) per axis → 4 bytes saved per position.
    //
    // Health encoding:
    //   ushort stores 0–65535 as raw integer HP, eliminating the float representation.
    //   Savings: 4 bytes (float) → 2 bytes (ushort).
    //
    // CombatEvent flags byte:
    //   Bit 0 = IsCritical.  Upper bits reserved for DamageType and future flags.
    //   Savings: bool (4 bytes aligned) → packed into existing flags byte.
    //
    public static partial class PacketEncoding
    {
        public const float PositionScale    = 16f;
        public const float InvPositionScale = 1f / PositionScale;

        /// <summary>Encodes a world-space float coordinate as a fixed-point short.</summary>
        public static short EncodePosition(float v)
            => (short)(int)(v * PositionScale);

        /// <summary>Decodes a fixed-point short back to a world-space float.</summary>
        public static float DecodePosition(short v)
            => v * InvPositionScale;

        /// <summary>Encodes HP as a raw ushort (0–65535 integer HP).</summary>
        public static ushort EncodeHealth(float hp)
            => (ushort)System.Math.Clamp((int)hp, 0, 65535);

        /// <summary>Decodes HP from ushort back to float.</summary>
        public static float DecodeHealth(ushort hp)
            => hp;

        // ── 24-bit tick encoding ─────────────────────────────────────────────────────────────
        //
        // Replaces the two int fields (ServerTick, AcknowledgedTick) in EntityPositionPacket
        // with a 3-byte layout: ushort (low 16 bits) + byte (high 8 bits).
        //
        // Capacity: 2^24 = 16,777,216 ticks ≈ 154 hours at 30 Hz — safe for any session length.
        // Wire savings: 2 × (4−3) = 2 bytes per EntityPositionPacket.
        //   At 30 Hz, 2,000 players, average 20 viewers each:
        //   2,000 × 20 × 30 × 2 bytes = 2.4 MB/s bandwidth reduction.
        //
        // Client-side decode:
        //   int tick = tickLo | (tickHi << 16);
        //   To handle wrapping use: int tick = (lastKnownTick & ~0xFFFFFF) | raw;
        //   and add 0x1000000 if the result drifts more than half the range behind lastKnownTick.
        public static void EncodeTick24(int tick, out ushort lo, out byte hi)
        {
            uint u = (uint)tick & 0xFFFFFF;
            lo = (ushort)(u & 0xFFFF);
            hi = (byte)(u >> 16);
        }

        public static int DecodeTick24(ushort lo, byte hi)
            => (int)((uint)lo | ((uint)hi << 16));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HOT-PATH STRUCTS  (sent every tick or on every combat event)
    // All are [StructLayout(Sequential, Pack=1)] blittable value types.
    // Written directly to NetDataWriter to bypass reflection-based serialisation.
    // Each struct begins with a 1-byte PacketId so the receiver can dispatch.
    // ─────────────────────────────────────────────────────────────────────────

    public static class PacketId
    {
        // ── Per-tick broadcast (highest volume) ──────────────────────────────
        public const byte EntityPosition         = 1;
        public const byte EntityHealth           = 2;
        public const byte CombatEvent            = 3;
        public const byte AoEHitEvent            = 4;
        // ── Event structs (converted from classes to eliminate tick-loop GC) ─
        public const byte EntityDespawn          = 5;
        public const byte PlayerDeath            = 6;
        public const byte PlayerRespawn          = 7;
        public const byte MatchEnd               = 8;
        public const byte GroundItemSpawned      = 9;
        public const byte GroundItemRemoved      = 10;
        public const byte ItemAddedToInventory   = 11;
        public const byte PlayerGraceDisconnect  = 12;
        public const byte PlayerReconnected      = 13;
        public const byte PlayerStatsRefreshed   = 14;
        // ── Projectile lifecycle (converted to structs — zero-alloc hot path) ─
        public const byte ProjectileSpawn        = 15;
        public const byte ProjectileDestroy      = 16;
        // ── Status effect events (converted to structs — zero-alloc hot path) ─
        public const byte StatusEffectApplied    = 17;
        public const byte StatusEffectRemoved    = 18;
    }

    /// <summary>
    /// Broadcast every tick with the authoritative position of one entity.
    /// Wire size (Pack=1, Sequential): 1 (id) + 4 (entityId) + 2 (X) + 2 (Y)
    ///                               + 3 (serverTick) + 3 (ackedTick) = 15 bytes.
    /// Previous layout used two int fields = 17 bytes (+2 bytes per packet per entity).
    ///
    /// Tick fields use 24-bit wrapping encoding via PacketEncoding.EncodeTick24/DecodeTick24.
    /// Wraps after 16,777,216 ticks ≈ 154 hours at 30 Hz — safe for all session types.
    ///
    /// Bandwidth saving vs. 4-byte int ticks:
    ///   2,000 players × 20 viewers × 30 Hz × 2 bytes = 2.4 MB/s reduction.
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct EntityPositionPacket
    {
        public byte  PacketTypeId;          // always PacketId.EntityPosition
        public int   EntityId;
        public short X;                     // fixed-point, use PacketEncoding.DecodePosition
        public short Y;
        // ServerTick encoded as 24 bits: TickLo (low 16) + TickHi (high 8).
        // Use PacketEncoding.EncodeTick24 / DecodeTick24.
        public ushort ServerTickLo;
        public byte   ServerTickHi;
        public ushort AcknowledgedTickLo;
        public byte   AcknowledgedTickHi;
    }

    /// <summary>
    /// Broadcast only to clients allowed to see the entity's health (same faction).
    /// Wire size: 1 + 4 + 2 = 7 bytes.  Old class: ~16 bytes.
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct EntityHealthPacket
    {
        public byte   PacketTypeId;     // always PacketId.EntityHealth
        public int    EntityId;
        public ushort Health;           // raw integer HP — use PacketEncoding.DecodeHealth
    }

    /// <summary>
    /// Broadcast when a melee attack or single-target spell lands.
    /// Wire size: 1 + 4 + 4 + 2 + 1 = 12 bytes  (was 13 with int Damage).
    /// Damage is a ushort: max 65,535 raw damage per hit — sufficient for all game designs
    /// that don't have arbitrarily scaling numbers. Clamp server-side before assignment.
    /// Flags byte: bit 0 = IsCritical; bits 1-2 = DamageType; bits 3-7 reserved.
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct CombatEventPacket
    {
        public byte   PacketTypeId;     // always PacketId.CombatEvent
        public int    AttackerId;
        public int    TargetId;
        /// <summary>Raw damage value, clamped to [0, 65535] server-side before assignment.</summary>
        public ushort Damage;
        /// <summary>Bit 0 = IsCritical. Bit 1-2 = DamageType.</summary>
        public byte   Flags;

        public bool IsCritical
        {
            get => (Flags & 0x01) != 0;
            set => Flags = value ? (byte)(Flags | 0x01) : (byte)(Flags & ~0x01);
        }
    }

    /// <summary>
    /// Broadcast once per entity hit inside an AoE.
    /// Wire size: 1 + 4 + 4 + 4 + 2 + 1 = 16 bytes (was 17 with int Damage).
    /// Damage clamped to ushort [0, 65535] server-side, matching CombatEventPacket.
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct AoEHitEventPacket
    {
        public byte   PacketTypeId;     // always PacketId.AoEHitEvent
        public int    CasterId;
        public int    SpellId;
        public int    HitEntityId;
        public ushort Damage;           // clamped to [0, 65535]
        public byte   Flags;            // bit 0 = IsCritical

        public bool IsCritical
        {
            get => (Flags & 0x01) != 0;
            set => Flags = value ? (byte)(Flags | 0x01) : (byte)(Flags & ~0x01);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // EVENT STRUCTS  (converted from classes to eliminate mid-tick GC)
    //
    // Why:  Even "rare" events (deaths, respawns, loot drops) allocate a heap
    //       object every time they fire.  Under heavy MMORPG load — AoE wipes,
    //       mass loot drops, zone transfers — this creates sustained GC pressure
    //       inside ProcessTick().  Pre-allocating these as instance fields on
    //       ArenaInstance and writing directly to NetDataWriter is zero-alloc.
    //
    // Wire-size gains (vs. managed class with object header ≈ 16-byte overhead):
    //   EntityDespawnPacket        class ~16 B → struct 5 B   (−11 B)
    //   PlayerDeathPacket          class ~24 B → struct 9 B   (−15 B)
    //   PlayerRespawnPacket        class ~32 B → struct 11 B  (−21 B, X/Y/HP compressed)
    //   MatchEndPacket             class ~17 B → struct 2 B   (−15 B)
    //   GroundItemSpawnedPacket    class ~28 B → struct 13 B  (−15 B, X/Y compressed)
    //   GroundItemRemovedPacket    class ~16 B → struct 5 B   (−11 B)
    //   ItemAddedToInventoryPacket class ~24 B → struct 9 B   (−15 B)
    //   PlayerGraceDisconnectPacket class ~16 B → struct 5 B  (−11 B)
    //   PlayerReconnectedPacket    class ~16 B → struct 5 B   (−11 B)
    //   PlayerStatsRefreshedPacket class ~48 B → struct 19 B  (−29 B, floats→ushort)
    // ─────────────────────────────────────────────────────────────────────────

    // ── PacketEncoding helpers for new compressed fields ─────────────────────
    public static partial class PacketEncoding
    {
        // Stat percentages (0.0–1.0) compressed to ushort ×10 000.
        // Precision: 0.0001 (4 decimal places). Range: 0.0000–6.5535.
        public const float StatScale = 10_000f;
        public static ushort EncodeStat(float v)   => (ushort)System.Math.Clamp((int)(v * StatScale), 0, 65535);
        public static float  DecodeStat(ushort v)  => v / StatScale;

        // AttackPower compressed to ushort ×100. Range: 0–655.35.
        public const float AttackPowerScale = 100f;
        public static ushort EncodeAttackPower(float v) => (ushort)System.Math.Clamp((int)(v * AttackPowerScale), 0, 65535);
        public static float  DecodeAttackPower(ushort v) => v / AttackPowerScale;

        // Unit-vector component compressed to short×32767 (-1..1 → -32767..32767).
        // Precision: 1/32767 ≈ 0.00003 — sufficient for projectile direction.
        public const float DirectionScale    = 32767f;
        public const float InvDirectionScale = 1f / DirectionScale;
        public static short  EncodeDirection(float v)  => (short)(int)(v * DirectionScale);
        public static float  DecodeDirection(short v)  => v * InvDirectionScale;

        // Speed / range compressed to ushort×10.  Range: 0–6553.5 units (or units/s).
        // Precision: 0.1 units — more than sufficient for projectile travel.
        public const float SpeedScale    = 10f;
        public const float InvSpeedScale = 1f / SpeedScale;
        public static ushort EncodeSpeed(float v) => (ushort)System.Math.Clamp((int)(v * SpeedScale), 0, 65535);
        public static float  DecodeSpeed(ushort v) => v * InvSpeedScale;
    }

    /// <summary>
    /// Broadcast when the server permanently removes an entity.
    /// Wire size: 1 + 4 = 5 bytes.
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct EntityDespawnPacket
    {
        public byte PacketTypeId;   // always PacketId.EntityDespawn
        public int  EntityId;
    }

    /// <summary>
    /// Broadcast when a player's health reaches zero.
    /// KillerEntityId is 0 when the kill source is unknown.
    /// Wire size: 1 + 4 + 4 = 9 bytes.
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct PlayerDeathPacket
    {
        public byte PacketTypeId;   // always PacketId.PlayerDeath
        public int  KilledEntityId;
        public int  KillerEntityId;
    }

    /// <summary>
    /// Broadcast when a dead player's respawn timer expires.
    /// X/Y: fixed-point shorts (PacketEncoding.EncodePosition).
    /// Health: raw ushort integer HP (PacketEncoding.EncodeHealth).
    /// Wire size: 1 + 4 + 2 + 2 + 2 = 11 bytes.
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct PlayerRespawnPacket
    {
        public byte   PacketTypeId;   // always PacketId.PlayerRespawn
        public int    EntityId;
        public short  X;              // fixed-point, use PacketEncoding.DecodePosition
        public short  Y;
        public ushort Health;         // raw integer HP, use PacketEncoding.DecodeHealth
    }

    /// <summary>
    /// Sent only to the owning client after a gear swap/equip.
    /// Percentage fields use ushort ×10 000 fixed-point (0.0001 precision).
    /// AttackPower uses ushort ×100 fixed-point (0.01 precision, max 655.35).
    /// MaxHealth uses ushort integer HP (same as EntityHealthPacket).
    /// Wire size: 1 + 1 + 2 + 2 + 2 + 2 + 2 + 2 + 2 + 2 + 2 = 20 bytes (vs. 48+ as a class).
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct PlayerStatsRefreshedPacket
    {
        public byte   PacketTypeId;           // always PacketId.PlayerStatsRefreshed
        public byte   ActiveGearSetIndex;
        public ushort MaxHealth;              // integer HP
        public ushort AttackPower;            // ×100 fixed-point
        public ushort PhysicalAbsorbPercent;  // ×10 000 fixed-point
        public ushort PhysicalResistPercent;
        public ushort MagicAbsorbPercent;
        public ushort MagicResistPercent;
        public ushort CritChance;
        public ushort MeleeLifeStealPercent;
    }

    /// <summary>
    /// Broadcast once when the win condition is satisfied.
    /// Wire size: 1 + 1 = 2 bytes.
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct MatchEndPacket
    {
        public byte PacketTypeId;   // always PacketId.MatchEnd
        public byte WinnerFaction;  // maps to FactionId
    }

    /// <summary>
    /// Broadcast when a lootable item appears on the ground.
    /// X/Y: fixed-point shorts (PacketEncoding.EncodePosition).
    /// Wire size: 1 + 4 + 4 + 2 + 2 = 13 bytes.
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct GroundItemSpawnedPacket
    {
        public byte  PacketTypeId;   // always PacketId.GroundItemSpawned
        public int   GroundItemId;
        public int   DefinitionId;
        public short X;              // fixed-point, use PacketEncoding.DecodePosition
        public short Y;
    }

    /// <summary>
    /// Broadcast when a ground item is picked up or despawned.
    /// Wire size: 1 + 4 = 5 bytes.
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct GroundItemRemovedPacket
    {
        public byte PacketTypeId;   // always PacketId.GroundItemRemoved
        public int  GroundItemId;
    }

    /// <summary>
    /// Confirms to the owning client that an item was added to their inventory.
    /// Wire size: 1 + 4 + 4 = 9 bytes.
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct ItemAddedToInventoryPacket
    {
        public byte PacketTypeId;   // always PacketId.ItemAddedToInventory
        public int  DefinitionId;
        public int  InstanceId;
    }

    /// <summary>
    /// Broadcast when a player's connection drops but their session is preserved.
    /// Wire size: 1 + 4 = 5 bytes.
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct PlayerGraceDisconnectPacket
    {
        public byte PacketTypeId;   // always PacketId.PlayerGraceDisconnect
        public int  EntityId;
    }

    /// <summary>
    /// Broadcast when a grace-period player successfully reconnects.
    /// Wire size: 1 + 4 = 5 bytes.
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct PlayerReconnectedPacket
    {
        public byte PacketTypeId;   // always PacketId.PlayerReconnected
        public int  EntityId;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // INFREQUENT CLASS PACKETS  (C→S input or low-frequency S→C with strings)
    // These retain NetPacketProcessor compat because they carry string fields
    // or are only sent during auth/lobby handshake — never inside ProcessTick.
    // ─────────────────────────────────────────────────────────────────────────

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

    // ── Status Effect Packets (converted from classes to zero-alloc structs) ──
    //
    // Why: TryApplyStatusEffect fires on every weapon hit, spell hit, and projectile hit.
    // Under a 20-player AoE fight this means ~60 status-effect allocations per tick.
    // Converting to structs + passing by `out` eliminates all of them.
    //
    // Visibility is packed into the low bit of VisibilityFlags alongside reserved bits.
    // Wire size: StatusEffectAppliedPacket  class ~40 B → struct 15 B  (−25 B)
    //            StatusEffectRemovedPacket  class ~24 B → struct 10 B  (−14 B)

    /// <summary>
    /// Broadcast when a status effect is applied or refreshed on a target.
    /// Wire size: 1 (id) + 4 + 4 + 4 + 1 + 1 = 15 bytes.
    /// Visibility bit: bit 0 of VisibilityFlags (0 = AlliesOnly, 1 = Everyone).
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct StatusEffectAppliedPacket
    {
        public byte PacketTypeId;   // always PacketId.StatusEffectApplied
        public int  TargetEntityId;
        public int  SourceEntityId;
        public int  EffectId;
        /// <summary>Remaining duration in simulation ticks.</summary>
        public short RemainingTicks;
        /// <summary>bit 0 = Visibility (0=AlliesOnly, 1=Everyone); bits 1-7 reserved.</summary>
        public byte VisibilityFlags;

        public StatusEffectVisibility Visibility
        {
            get => (StatusEffectVisibility)(VisibilityFlags & 0x01);
            set => VisibilityFlags = (byte)((VisibilityFlags & ~0x01) | ((byte)value & 0x01));
        }
    }

    /// <summary>
    /// Broadcast when a status effect expires or is forcibly removed.
    /// Wire size: 1 + 4 + 4 + 1 = 10 bytes.
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct StatusEffectRemovedPacket
    {
        public byte PacketTypeId;   // always PacketId.StatusEffectRemoved
        public int  TargetEntityId;
        public int  EffectId;
        /// <summary>bit 0 = Visibility (0=AlliesOnly, 1=Everyone); bits 1-7 reserved.</summary>
        public byte VisibilityFlags;

        public StatusEffectVisibility Visibility
        {
            get => (StatusEffectVisibility)(VisibilityFlags & 0x01);
            set => VisibilityFlags = (byte)((VisibilityFlags & ~0x01) | ((byte)value & 0x01));
        }
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
    ///
    /// Compression:
    ///   StartX/Y   : fixed-point short  (PacketEncoding.EncodePosition, ±2048 @ 0.0625 precision)
    ///   DirectionX/Y: short×32767       (PacketEncoding.EncodeDirection, unit vector -1..1)
    ///   Speed       : ushort×10         (PacketEncoding.EncodeSpeed,     0–6553.5 units/s)
    ///   MaxRange    : ushort×10         (PacketEncoding.EncodeSpeed,     0–6553.5 units)
    /// Wire size: 1 + 4 + 4 + 4 + 2 + 2 + 2 + 2 + 2 + 2 = 25 bytes (class was ~60 B).
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct ProjectileSpawnPacket
    {
        public byte   PacketTypeId;  // always PacketId.ProjectileSpawn
        public int    ProjectileId;
        public int    OwnerId;
        public int    SpellId;
        public short  StartX;        // fixed-point, use PacketEncoding.DecodePosition
        public short  StartY;
        public short  DirectionX;    // ×32767, use PacketEncoding.DecodeDirection
        public short  DirectionY;
        public ushort Speed;         // ×10, use PacketEncoding.DecodeSpeed
        /// <summary>Authoritative max travel distance. Unity despawns the visual on reaching this.</summary>
        public ushort MaxRange;      // ×10, use PacketEncoding.DecodeSpeed
    }

    /// <summary>
    /// Broadcast when the server removes a projectile — either because it hit
    /// a target (HitSomething = true, a CombatEventPacket is also sent) or
    /// because it exceeded its maximum travel range (HitSomething = false).
    /// Wire size: 1 + 4 + 1 = 6 bytes (class was ~24 B).
    /// Flags byte: bit 0 = HitSomething; bits 1-7 reserved.
    /// </summary>
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    public struct ProjectileDestroyPacket
    {
        public byte PacketTypeId;   // always PacketId.ProjectileDestroy
        public int  ProjectileId;
        public byte Flags;          // bit 0 = HitSomething

        public bool HitSomething
        {
            get => (Flags & 0x01) != 0;
            set => Flags = value ? (byte)(Flags | 0x01) : (byte)(Flags & ~0x01);
        }
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

    // ── Ground-item C→S request — remains a class (NetPacketProcessor compat) ─
    /// <summary>Client → Server: player wants to pick up a ground item by its server-assigned ID.</summary>
    public class GroundItemPickupRequestPacket
    {
        public int GroundItemId { get; set; }
    }

    // ── Arena end-of-match reward packet ─────────────────────────────────────
    /// <summary>
    /// Server → owning client: crafting ingredient rewards earned this match.
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
