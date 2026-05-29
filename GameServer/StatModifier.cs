namespace GameServer
{
    /// <summary>
    /// Additive stat delta applied to a <see cref="PlayerSession"/> from any temporary source:
    /// a buff or debuff <see cref="ActiveStatusEffect"/>, a consumable item, a zone-wide aura,
    /// or any future stat-granting system.
    ///
    /// Values are summed with base stats and equipment bonuses inside
    /// <see cref="PlayerSession.RecomputeStats"/>.  Positive values increase a stat; negative
    /// values decrease it (debuffs, curses, zone penalties).
    ///
    /// Kept as a mutable struct (value type) so iterating a collection of active modifiers
    /// costs no heap allocation and stays in CPU cache.  The <see cref="ActiveStatusEffect"/>
    /// struct embeds one <see cref="StatModifier"/> for its stat contribution while active.
    /// </summary>
    public struct StatModifier
    {
        public float MaxHealth;
        public float AttackPower;
        public float PhysicalAbsorbPercent;
        public float PhysicalResistPercent;
        public float MagicAbsorbPercent;
        public float MagicResistPercent;
        public float CritChance;
        public float MeleeLifeStealPercent;
        public float ProjectileRangeBonus;
        public int   ProjectilePierceBonus;

        /// <summary>
        /// Returns <c>true</c> when at least one field is non-zero.
        /// Used by <see cref="PlayerSession"/> to decide whether <c>RecomputeStats</c> must
        /// be called after this modifier is applied or removed.
        /// </summary>
        public bool HasAnyValue =>
            MaxHealth             != 0f || AttackPower           != 0f ||
            PhysicalAbsorbPercent != 0f || PhysicalResistPercent != 0f ||
            MagicAbsorbPercent    != 0f || MagicResistPercent    != 0f ||
            CritChance            != 0f || MeleeLifeStealPercent != 0f ||
            ProjectileRangeBonus  != 0f || ProjectilePierceBonus  != 0;

        /// <summary>Zero modifier — no stat contribution.  Equivalent to <c>default(StatModifier)</c>.</summary>
        public static readonly StatModifier Zero = default;
    }
}
