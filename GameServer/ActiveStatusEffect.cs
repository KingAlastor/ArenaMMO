using SharedLibrary;

namespace GameServer
{
    internal struct ActiveStatusEffect
    {
        public int                    SourceEntityId;
        public int                    RemainingTicks;
        public int                    TickIntervalTicks;
        public int                    TicksUntilNextTick;
        public int                    PeriodicDamagePerTick;
        public float                  SourceHealPercentPerTick;
        public int                    Stacks;
        public StatusEffectVisibility  Visibility;

        /// <summary>
        /// Additive stat modifier applied to the target for the duration of this effect.
        /// Non-zero only for buff/debuff effects (e.g. "Battle Stance: +20 AttackPower for 10 s").
        /// Standard DoT/HoT effects leave this as <see cref="StatModifier.Zero"/>.
        ///
        /// <see cref="PlayerSession.RecomputeStats"/> is called on both application and expiry
        /// of any effect whose <see cref="StatModifier.HasAnyValue"/> is true, keeping
        /// authoritative stats in sync with the live set of active modifiers.
        /// </summary>
        public StatModifier StatMod;
    }
}
