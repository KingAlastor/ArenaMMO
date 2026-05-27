using SharedLibrary;
using LiteNetLib;
using System.Collections.Generic;

namespace GameServer
{
    /// <summary>
    /// Authoritative, server-only representation of a connected player.
    /// Lives entirely in server RAM for the duration of the match.
    ///
    /// Lifecycle: loaded from Redis at match start → mutated each tick → written back
    /// to Redis at match end → flushed to PostgreSQL asynchronously.
    /// Never queried from PostgreSQL during active gameplay.
    /// </summary>
    public sealed class PlayerSession
    {
        public int      EntityId    { get; set; }
        public string   PlayerName  { get; set; } = string.Empty;
        public NetPeer? Peer        { get; set; }
        public FactionId Faction    { get; set; }

        // ── Authoritative game state ───────────────────────────────────────────
        // Only the server writes these values. Client predictions are reconciled
        // against these on each EntityStatePacket broadcast.
        public Vec2  Position    { get; set; }
        public float Health      { get; set; } = 100f;
        public float MaxHealth   { get; set; } = 100f;
        public float AttackPower { get; set; } = 1.0f;  // damage multiplier
        public float Armor       { get; set; } = 0f;    // flat mitigation input
        public float CritChance  { get; set; } = 0.05f; // 0–1  (5% default)
        public float MeleeLifeStealPercent { get; set; } = 0f;

        // ── Weapon status effects ───────────────────────────────────────────
        /// <summary>
        /// Optional status effect attached to the player's basic melee weapon.
        /// 0 = no effect. This lets melee weapons apply ally-only buffs or public debuffs.
        /// </summary>
        public int                    MeleeStatusEffectId             { get; set; } = 0;
        public int                    MeleeStatusEffectDurationTicks  { get; set; } = 0;
        public StatusEffectVisibility  MeleeStatusEffectVisibility     { get; set; } = StatusEffectVisibility.Everyone;
        public float                  MeleeStatusEffectChance         { get; set; } = 0f;
        public int                    MeleeStatusEffectStacks         { get; set; } = 1;
        public int                    MeleeStatusEffectTickDamage     { get; set; } = 0;
        public int                    MeleeStatusEffectTickIntervalTicks { get; set; } = 30;
        public float                  MeleeStatusEffectOwnerHealPercentPerTick { get; set; } = 0f;

        /// <summary>
        /// Multiplier applied to a spell's base Range when a projectile spawns.
        /// 1.0 = default. Increase via archery-focused stat builds or equipment.
        /// Snapshotted into ProjectileState.MaxRange at launch time.
        /// </summary>
        public float ProjectileRangeMultiplier { get; set; } = 1.0f;

        /// <summary>
        /// Flat bonus pierce charges added on top of the spell's BasePierceCount at spawn time.
        /// Grant via item affixes ("Piercing Arrows") or active skills ("Piercing Shot").
        /// 0 = no bonus (default). Each point lets the projectile pass through one extra target.
        /// </summary>
        public int   ProjectilePierceBonus     { get; set; } = 0;

        public bool IsAlive => Health > 0f;

        private readonly Dictionary<int, ActiveStatusEffect> _statusEffects = new Dictionary<int, ActiveStatusEffect>();
        private readonly List<int> _statusEffectKeysBuffer = new List<int>();
        private readonly List<int> _expiredStatusEffectIds = new List<int>();

        // spellId (0 = basic auto-attack) → last tick it was activated
        private readonly Dictionary<int, int> _cooldowns = new Dictionary<int, int>();

        /// <summary>Returns true when the ability is not yet available this tick.</summary>
        public bool IsOnCooldown(int spellId, int currentTick, int cooldownTicks)
        {
            if (!_cooldowns.TryGetValue(spellId, out int lastUsedTick))
                return false;
            return (currentTick - lastUsedTick) < cooldownTicks;
        }

        /// <summary>Records that the ability was just used on this tick.</summary>
        public void SetCooldown(int spellId, int currentTick)
            => _cooldowns[spellId] = currentTick;

        public void ApplyDamage(int damage)
        {
            Health -= damage;
            if (Health < 0f) Health = 0f;
        }

        public bool TryApplyStatusEffect(
            int effectId,
            int sourceEntityId,
            int durationTicks,
            int stacks,
            int tickDamage,
            int tickIntervalTicks,
            float sourceHealPercentPerTick,
            StatusEffectVisibility visibility,
            out StatusEffectAppliedPacket packet)
        {
            packet = null!;

            if (effectId <= 0 || durationTicks <= 0)
                return false;

            if (_statusEffects.TryGetValue(effectId, out ActiveStatusEffect existing))
            {
                existing.SourceEntityId = sourceEntityId;
                existing.RemainingTicks = durationTicks;
                existing.TickIntervalTicks = tickIntervalTicks > 0 ? tickIntervalTicks : 30;
                existing.TicksUntilNextTick = existing.TickIntervalTicks;
                existing.PeriodicDamagePerTick = tickDamage;
                existing.SourceHealPercentPerTick = sourceHealPercentPerTick;
                existing.Stacks         = stacks;
                existing.Visibility     = visibility;

                _statusEffects[effectId] = existing;

                packet = new StatusEffectAppliedPacket
                {
                    TargetEntityId = EntityId,
                    SourceEntityId = sourceEntityId,
                    EffectId       = effectId,
                    RemainingTicks = existing.RemainingTicks,
                    Stacks         = existing.Stacks,
                    Visibility     = existing.Visibility,
                };

                return true;
            }

            var active = new ActiveStatusEffect
            {
                SourceEntityId = sourceEntityId,
                RemainingTicks = durationTicks,
                TickIntervalTicks = tickIntervalTicks > 0 ? tickIntervalTicks : 30,
                TicksUntilNextTick = tickIntervalTicks > 0 ? tickIntervalTicks : 30,
                PeriodicDamagePerTick = tickDamage,
                SourceHealPercentPerTick = sourceHealPercentPerTick,
                Stacks         = stacks,
                Visibility     = visibility,
            };

            _statusEffects[effectId] = active;

            packet = new StatusEffectAppliedPacket
            {
                TargetEntityId = EntityId,
                SourceEntityId = sourceEntityId,
                EffectId       = effectId,
                RemainingTicks = durationTicks,
                Stacks         = stacks,
                Visibility     = visibility,
            };

            return true;
        }

        public void TickStatusEffects(
            IReadOnlyList<PlayerSession> allPlayers,
            List<CombatEventPacket> tickDamageEvents,
            List<StatusEffectRemovedPacket> expiredPackets)
        {
            if (_statusEffects.Count == 0)
                return;

            _expiredStatusEffectIds.Clear();
            _statusEffectKeysBuffer.Clear();

            foreach (var pair in _statusEffects)
                _statusEffectKeysBuffer.Add(pair.Key);

            for (int i = 0; i < _statusEffectKeysBuffer.Count; i++)
            {
                int effectId = _statusEffectKeysBuffer[i];
                ActiveStatusEffect effect = _statusEffects[effectId];
                effect.RemainingTicks--;
                effect.TicksUntilNextTick--;

                if (effect.PeriodicDamagePerTick > 0 && effect.TicksUntilNextTick <= 0 && IsAlive)
                {
                    int dotDamage = effect.PeriodicDamagePerTick * (effect.Stacks > 0 ? effect.Stacks : 1);
                    ApplyDamage(dotDamage);

                    tickDamageEvents.Add(new CombatEventPacket
                    {
                        AttackerId = effect.SourceEntityId,
                        TargetId   = EntityId,
                        Damage     = dotDamage,
                        IsCritical = false,
                    });

                    if (effect.SourceHealPercentPerTick > 0f)
                    {
                        PlayerSession? source = FindById(allPlayers, effect.SourceEntityId);
                        if (source != null)
                        {
                            float healAmount = dotDamage * effect.SourceHealPercentPerTick;
                            if (healAmount > 0f)
                                source.RestoreHealth(healAmount);
                        }
                    }

                    effect.TicksUntilNextTick = effect.TickIntervalTicks > 0 ? effect.TickIntervalTicks : 30;
                }

                if (effect.RemainingTicks <= 0)
                    _expiredStatusEffectIds.Add(effectId);
                else
                    _statusEffects[effectId] = effect;
            }

            for (int i = 0; i < _expiredStatusEffectIds.Count; i++)
            {
                int effectId = _expiredStatusEffectIds[i];
                ActiveStatusEffect effect = _statusEffects[effectId];
                _statusEffects.Remove(effectId);

                expiredPackets.Add(new StatusEffectRemovedPacket
                {
                    TargetEntityId = EntityId,
                    EffectId       = effectId,
                    Visibility     = effect.Visibility,
                });
            }
        }

        public void RestoreHealth(float amount)
        {
            Health += amount;
            if (Health > MaxHealth) Health = MaxHealth;
        }

        private static PlayerSession? FindById(IReadOnlyList<PlayerSession> players, int entityId)
        {
            for (int i = 0; i < players.Count; i++)
                if (players[i].EntityId == entityId) return players[i];
            return null;
        }
    }
}
