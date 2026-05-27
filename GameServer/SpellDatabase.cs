using SharedLibrary;
using System.Collections.Generic;

namespace GameServer
{
    /// <summary>
    /// Immutable lookup table of every spell definition, populated once at server startup.
    ///
    /// Production path: hydrate from PostgreSQL via Dapper, cache in Redis, then load here.
    /// During a match the game loop reads this dictionary — no DB round-trips.
    /// </summary>
    public static class SpellDatabase
    {
        private static readonly Dictionary<int, SpellDefinition> _spells =
            new Dictionary<int, SpellDefinition>
            {
                [1] = new SpellDefinition
                {
                    SpellId       = 1,
                    Name          = "Fireball",
                    TargetType    = SpellTargetType.SingleTarget,
                    TargetFactionFilter = TargetFactionFilter.EnemiesOnly,
                    Range         = 15f,
                    AoERadius     = 0f,
                    BaseDamage    = 35,
                    CooldownTicks = 60,    // 2 s at 30 Hz
                    DamageType    = DamageType.Magic,
                    LifeStealPercent = 0.10f,
                    StatusEffectId = 101,
                    StatusEffectDurationTicks = 45,
                    StatusEffectVisibility = StatusEffectVisibility.Everyone,
                    StatusEffectChance = 0.35f,
                    StatusEffectStacks = 1,
                    StatusEffectTickDamage = 4,
                    StatusEffectTickIntervalTicks = 30,
                    StatusEffectOwnerHealPercentPerTick = 0.20f,
                },
                [2] = new SpellDefinition
                {
                    SpellId       = 2,
                    Name          = "Blizzard",
                    TargetType    = SpellTargetType.AoE,
                    TargetFactionFilter = TargetFactionFilter.EnemiesOnly,
                    Range         = 20f,
                    AoERadius     = 5f,
                    BaseDamage    = 25,
                    CooldownTicks = 90,    // 3 s at 30 Hz
                    DamageType    = DamageType.Magic,
                    LifeStealPercent = 0.05f,
                    StatusEffectId = 102,
                    StatusEffectDurationTicks = 60,
                    StatusEffectVisibility = StatusEffectVisibility.Everyone,
                    StatusEffectChance = 1.0f,
                    StatusEffectStacks = 1,
                    StatusEffectTickDamage = 2,
                    StatusEffectTickIntervalTicks = 30,
                    StatusEffectOwnerHealPercentPerTick = 0.10f,
                },
                [3] = new SpellDefinition
                {
                    SpellId       = 3,
                    Name          = "Slash",
                    TargetType    = SpellTargetType.SingleTarget,
                    TargetFactionFilter = TargetFactionFilter.EnemiesOnly,
                    Range         = CombatMath.MeleeRange,
                    AoERadius     = 0f,
                    BaseDamage    = 20,
                    CooldownTicks = 15,    // 0.5 s at 30 Hz
                    DamageType    = DamageType.Physical,
                    LifeStealPercent = 0.15f,
                    StatusEffectId = 103,
                    StatusEffectDurationTicks = 30,
                    StatusEffectVisibility = StatusEffectVisibility.Everyone,
                    StatusEffectChance = 0.20f,
                    StatusEffectStacks = 1,
                    StatusEffectTickDamage = 0,
                    StatusEffectTickIntervalTicks = 30,
                    StatusEffectOwnerHealPercentPerTick = 0f,
                },

                // ── Archery ────────────────────────────────────────────────

                [4] = new SpellDefinition
                {
                    SpellId             = 4,
                    Name                = "Bow Shot",
                    TargetType          = SpellTargetType.Projectile,
                    TargetFactionFilter = TargetFactionFilter.EnemiesOnly,
                    Range               = 30f,         // base max travel distance (scaled by ProjectileRangeMultiplier)
                    AoERadius           = 0f,
                    BaseDamage          = 22,
                    CooldownTicks       = 25,          // ~0.83 s at 30 Hz
                    DamageType          = DamageType.Physical,
                    LifeStealPercent    = 0.08f,
                    ProjectileSpeed     = 18f,         // slower arc, requires leading the target
                    ProjectileHitRadius = 0.4f,
                    BaseHitChance       = 0.95f,       // 95% at point blank
                    HitFalloffPerUnit   = 0.015f,      // −1.5%/unit → 50% at 30 units
                    StatusEffectId      = 104,
                    StatusEffectDurationTicks = 45,
                    StatusEffectVisibility = StatusEffectVisibility.Everyone,
                    StatusEffectChance  = 0.15f,
                    StatusEffectStacks  = 1,
                    StatusEffectTickDamage = 3,
                    StatusEffectTickIntervalTicks = 30,
                    StatusEffectOwnerHealPercentPerTick = 0.25f,
                },
                [5] = new SpellDefinition
                {
                    SpellId             = 5,
                    Name                = "Crossbow Bolt",
                    TargetType          = SpellTargetType.Projectile,
                    TargetFactionFilter = TargetFactionFilter.EnemiesOnly,
                    Range               = 35f,
                    AoERadius           = 0f,
                    BaseDamage          = 28,
                    CooldownTicks       = 40,          // ~1.33 s at 30 Hz (reload time)
                    DamageType          = DamageType.Physical,
                    LifeStealPercent    = 0.12f,
                    ProjectileSpeed     = 35f,         // fast bolt, near-instant at close range
                    ProjectileHitRadius = 0.3f,
                    BaseHitChance       = 1.0f,        // 100% at point blank
                    HitFalloffPerUnit   = 0.008f,      // −0.8%/unit → 72% at 35 units
                    StatusEffectId      = 105,
                    StatusEffectDurationTicks = 30,
                    StatusEffectVisibility = StatusEffectVisibility.Everyone,
                    StatusEffectChance  = 0.10f,
                    StatusEffectStacks  = 1,
                    StatusEffectTickDamage = 2,
                    StatusEffectTickIntervalTicks = 30,
                    StatusEffectOwnerHealPercentPerTick = 0.20f,
                },

                // ── Melee AoE ─────────────────────────────────────────────────

                [6] = new SpellDefinition
                {
                    SpellId       = 6,
                    Name          = "Whirlwind",
                    TargetType    = SpellTargetType.MeleeSplash,
                    TargetFactionFilter = TargetFactionFilter.EnemiesOnly,
                    Range         = 0f,           // unused for MeleeSplash — AoERadius defines reach
                    AoERadius     = 2.5f,         // hits all enemies within 2.5 units of the caster
                    BaseDamage    = 18,
                    CooldownTicks = 45,            // 1.5 s at 30 Hz
                    DamageType    = DamageType.Physical,
                    LifeStealPercent = 0.10f,
                    StatusEffectId = 201,
                    StatusEffectDurationTicks = 60,
                    StatusEffectVisibility = StatusEffectVisibility.Everyone,
                    StatusEffectChance = 1.0f,
                    StatusEffectStacks = 1,
                    StatusEffectTickDamage = 1,
                    StatusEffectTickIntervalTicks = 30,
                    StatusEffectOwnerHealPercentPerTick = 0.05f,
                },

                // ── Explosive Archery ──────────────────────────────────────────

                [7] = new SpellDefinition
                {
                    SpellId             = 7,
                    Name                = "Explosive Arrow",
                    TargetType          = SpellTargetType.Projectile,
                    TargetFactionFilter = TargetFactionFilter.EnemiesOnly,
                    Range               = 25f,
                    AoERadius           = 3.0f,    // detonation radius on final impact
                    BaseDamage          = 30,
                    CooldownTicks       = 60,      // 2 s at 30 Hz
                    DamageType          = DamageType.Physical,
                    LifeStealPercent    = 0.10f,
                    ProjectileSpeed     = 15f,     // slower arc to compensate for AoE payoff
                    ProjectileHitRadius = 0.5f,
                    BaseHitChance       = 0.90f,
                    HitFalloffPerUnit   = 0.010f,  // −1%/unit → 65% at 25 units
                    StatusEffectId      = 106,
                    StatusEffectDurationTicks = 40,
                    StatusEffectVisibility = StatusEffectVisibility.Everyone,
                    StatusEffectChance  = 0.25f,
                    StatusEffectStacks  = 1,
                    StatusEffectTickDamage = 5,
                    StatusEffectTickIntervalTicks = 30,
                    StatusEffectOwnerHealPercentPerTick = 0.30f,
                },

                // ── Allied buff ───────────────────────────────────────────────

                [8] = new SpellDefinition
                {
                    SpellId       = 8,
                    Name          = "Rallying Cry",
                    TargetType    = SpellTargetType.MeleeSplash,
                    TargetFactionFilter = TargetFactionFilter.AlliesOnly,
                    Range         = 0f,
                    AoERadius     = 4f,
                    BaseDamage    = 0,
                    CooldownTicks = 75,
                    DamageType    = DamageType.True,
                    LifeStealPercent = 0f,
                    StatusEffectId = 202,
                    StatusEffectDurationTicks = 90,
                    StatusEffectVisibility = StatusEffectVisibility.AlliesOnly,
                    StatusEffectChance = 1.0f,
                    StatusEffectStacks = 1,
                    StatusEffectTickDamage = 0,
                    StatusEffectTickIntervalTicks = 30,
                    StatusEffectOwnerHealPercentPerTick = 0f,
                },
            };

        /// <summary>Returns false (and a default struct) if the spell ID is not registered.</summary>
        public static bool TryGet(int spellId, out SpellDefinition spell)
            => _spells.TryGetValue(spellId, out spell);
    }
}
