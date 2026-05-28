namespace SharedLibrary
{
    /// <summary>
    /// Immutable definition of a spell ability.
    /// Loaded from the database at server startup; shared with Unity for
    /// client-side tooltips, cooldown display, and ability range indicators.
    /// Never mutated during a match.
    /// </summary>
    public struct SpellDefinition
    {
        public int             SpellId;
        public string          Name;
        public SpellTargetType TargetType;
        public float           Range;
        public TargetFactionFilter TargetFactionFilter;
        public float           AoERadius;     // 0 for single-target spells
        public int             BaseDamage;
        public int             CooldownTicks;      // e.g. 60 ticks = 2 s at 30 Hz
        public DamageType      DamageType;
        /// <summary>
        /// Portion of dealt hit damage returned as health to the attacker (0-1).
        /// Applied on successful direct hits only.
        /// </summary>
        public float           LifeStealPercent;
        public float           ProjectileSpeed;    // units/s; 0 for non-projectile spells
        public float           ProjectileHitRadius; // collision radius for IsInAoE checks

        // ── Status effects ───────────────────────────────────────────────────
        /// <summary>
        /// Optional status effect applied when this spell or weapon hit lands.
        /// 0 = no status effect.
        /// </summary>
        public int                   StatusEffectId;
        /// <summary>Duration of the applied effect in server ticks.</summary>
        public int                   StatusEffectDurationTicks;
        /// <summary>How broadly the server should reveal this effect to clients.</summary>
        public StatusEffectVisibility StatusEffectVisibility;
        /// <summary>Chance to apply the effect on hit. 0 = never, 1 = always.</summary>
        public float                 StatusEffectChance;
        /// <summary>Stack count written to the client when the effect is applied.</summary>
        public int                   StatusEffectStacks;
        /// <summary>Damage dealt per status tick. 0 means non-damaging effect.</summary>
        public int                   StatusEffectTickDamage;
        /// <summary>Interval in ticks between DoT ticks. 30 = once per second at 30 Hz.</summary>
        public int                   StatusEffectTickIntervalTicks;
        /// <summary>
        /// Portion (0-1) of DoT tick damage healed to the effect owner each tick.
        /// </summary>
        public float                 StatusEffectOwnerHealPercentPerTick;

        // ── Projectile accuracy ───────────────────────────────────────
        /// <summary>
        /// Probability of landing a hit at spawn point (0–1). 0 or negative is treated
        /// as 1.0 (always-hit) by ProjectileSystem, so non-projectile spells are safe
        /// if left at the struct default of 0.
        /// </summary>
        public float           BaseHitChance;
        /// <summary>
        /// How much hit chance is lost per unit of distance traveled.
        /// 0 = no falloff (instant spells / crossbow variant builds).
        /// Example: 0.015 means −1.5% per unit, so at 20 units: −30% from base.
        /// </summary>
        public float           HitFalloffPerUnit;

        // ── Pierce ──────────────────────────────────────────────────────────
        /// <summary>
        /// Number of additional targets this projectile can pass through after
        /// the first successful hit. 0 = standard (stops on first hit).
        /// Stacks additively with PlayerSession.ProjectilePierceBonus from items/skills.
        /// </summary>
        public int             BasePierceCount;

        /// <summary>
        /// Probability (0–1) that this spell's hit bypasses the target's resist mitigation.
        /// Absorb is always applied regardless of pierce. 0 = no pierce (default).
        /// Example: 0.30 means a 30% chance to skip PhysicalResistPercent or MagicResistPercent.
        /// </summary>
        public float           PierceChance;
    }
}
