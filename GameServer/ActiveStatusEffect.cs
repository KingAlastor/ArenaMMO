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
    }
}