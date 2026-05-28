using SharedLibrary;

namespace GameServer
{
    /// <summary>
    /// Live server-side state of one in-flight projectile.
    /// Created when a ShootRequestPacket is validated; removed on hit or range expiry.
    /// Class (reference type) so mutations inside ProjectileSystem.Tick are in-place.
    /// </summary>
    public sealed class ProjectileState
    {
        public int   ProjectileId     { get; set; }
        public int   OwnerId          { get; set; }
        public int   SpellId          { get; set; }

        // Authoritative world position, updated every tick
        public Vec2  Position         { get; set; }

        // Normalised direction the projectile travels
        public float DirectionX       { get; set; }
        public float DirectionY       { get; set; }

        public float Speed            { get; set; }   // units/second
        public float HitRadius        { get; set; }   // collision detection radius
        public float MaxRange         { get; set; }   // despawn distance

        // ── AoE on impact ─────────────────────────────────────────────────────
        /// <summary>
        /// If > 0 the projectile detonates on final impact, dealing damage to all players
        /// within this radius of the hit point (explosive arrows, etc.).
        /// 0 = point-impact only. Snapshotted from SpellDefinition.AoERadius at spawn time.
        /// </summary>
        public float AoERadius        { get; set; }

        // Snapshot of owner stats at spawn time — prevents retroactive stat changes mid-flight
        public int   BaseDamage       { get; set; }
        public float AttackPower      { get; set; }
        public float CritChance       { get; set; }
        public float LifeStealPercent { get; set; }
        public TargetFactionFilter    TargetFactionFilter { get; set; }

        // Optional status effect snapshot for projectile hits
        public int                    StatusEffectId      { get; set; }
        public int                    StatusEffectDurationTicks { get; set; }
        public float                  StatusEffectChance  { get; set; }
        public int                    StatusEffectStacks  { get; set; }
        public int                    StatusEffectTickDamage { get; set; }
        public int                    StatusEffectTickIntervalTicks { get; set; }
        public float                  StatusEffectOwnerHealPercentPerTick { get; set; }
        public StatusEffectVisibility  StatusEffectVisibility { get; set; }

        // Travel tracking
        public float TraveledDistance { get; set; }

        // ── Accuracy ──────────────────────────────────────────────────
        /// <summary>Snapshot of SpellDefinition.BaseHitChance at launch (0–1).</summary>
        public float BaseHitChance  { get; set; }
        /// <summary>Snapshot of SpellDefinition.HitFalloffPerUnit at launch.</summary>
        public float FalloffPerUnit { get; set; }

        // ── Pierce ────────────────────────────────────────────────────────
        /// <summary>
        /// Remaining pierce charges. Decremented on each successful hit while > 0.
        /// When it reaches 0 the next hit destroys the projectile normally.
        /// Snapshotted at spawn: spell.BasePierceCount + shooter.ProjectilePierceBonus.
        /// </summary>
        public int   PierceCount    { get; set; }

        // ── Damage type (snapshotted at spawn) ────────────────────────────────
        /// <summary>Snapshot of SpellDefinition.DamageType at launch time.</summary>
        public DamageType DamageType  { get; set; }

        /// <summary>
        /// Snapshot of SpellDefinition.PierceChance at launch time.
        /// Probability (0–1) that each hit bypasses the target's resist mitigation.
        /// </summary>
        public float PierceChance     { get; set; }
    }
}
