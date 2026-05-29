using SharedLibrary;
using LiteNetLib;
using GameServer.DataLayer;
using System;
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
        public int      AccountId   { get; set; }
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

        // ── Mitigation stats (0–1 fractions) ────────────────────────────────────
        // Absorb is always applied first and cannot be bypassed.
        // Resist is applied after absorb and is skipped when the hit is pierced.
        public float PhysicalAbsorbPercent { get; set; } = 0f;
        public float PhysicalResistPercent { get; set; } = 0f;
        public float MagicAbsorbPercent    { get; set; } = 0f;
        public float MagicResistPercent    { get; set; } = 0f;

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

        // ── Base stats (pre-equipment) ────────────────────────────────────────────
        // Set once from PlayerProfile at connect time; never mutated mid-match.
        // Equipment bonuses are layered on top in RecomputeStats().
        private float _baseMaxHealth;
        private float _baseAttackPower;
        private float _basePhysicalAbsorbPercent;
        private float _basePhysicalResistPercent;
        private float _baseMagicAbsorbPercent;
        private float _baseMagicResistPercent;
        private float _baseCritChance;
        private float _baseMeleeLifeStealPercent;

        // ── Inventory and equipped items ──────────────────────────────────────────
        // Inventory is the ownership source of truth; equipping an item not present
        // here is rejected server-side, preventing clients from granting themselves items.
        private readonly List<ItemInstance>                   _inventory     = new List<ItemInstance>();
        private readonly Dictionary<EquipSlot, ItemInstance>  _equippedItems = new Dictionary<EquipSlot, ItemInstance>();

        /// <summary>Up to 2 preset gear set loadouts, hydrated from Redis at connect time.</summary>
        public GearSetLoadout[] GearSets           { get; private set; } = Array.Empty<GearSetLoadout>();
        public int               ActiveGearSetIndex { get; private set; } = 0;

        /// <summary>
        /// Populates base stats, inventory, and gear set loadouts from the Redis-cached profile.
        /// Applies GearSets[0] as the starting loadout and sets Health to the resulting MaxHealth.
        /// Must be called once after the session is created, before the first game tick.
        /// </summary>
        public void HydrateFromProfile(PlayerProfile profile)
        {
            _baseMaxHealth             = profile.BaseMaxHealth;
            _baseAttackPower           = profile.BaseAttackPower;
            _basePhysicalAbsorbPercent = profile.BasePhysicalAbsorbPercent;
            _basePhysicalResistPercent = profile.BasePhysicalResistPercent;
            _baseMagicAbsorbPercent    = profile.BaseMagicAbsorbPercent;
            _baseMagicResistPercent    = profile.BaseMagicResistPercent;
            _baseCritChance            = profile.BaseCritChance;
            _baseMeleeLifeStealPercent = profile.BaseMeleeLifeStealPercent;

            _inventory.Clear();
            for (int i = 0; i < profile.Inventory.Length; i++)
                _inventory.Add(profile.Inventory[i]);

            GearSets = profile.GearSets;

            // Apply the first gear set as the starting loadout, or compute from base stats alone.
            if (GearSets.Length > 0)
                ApplyGearSetLoadout(0);
            else
                RecomputeStats();

            Health = MaxHealth;
        }

        /// <summary>
        /// Quickswap to a preset gear set. All items are validated against the player's
        /// inventory — the client cannot activate a loadout with items it doesn't own.
        /// </summary>
        public bool TryApplyGearSet(int setIndex, out PlayerStatsRefreshedPacket packet)
        {
            packet = null!;
            if (setIndex < 0 || setIndex >= GearSets.Length) return false;

            ApplyGearSetLoadout(setIndex);
            packet = BuildStatsPacket();
            return true;
        }

        /// <summary>
        /// Equips a single item from the player's inventory into its designated slot.
        /// The slot is derived from the item's definition — the client cannot reassign slots.
        /// Immediately calls <see cref="RecomputeStats"/> so the new stat total is authoritative.
        /// </summary>
        public bool TryEquipItem(int instanceId, out PlayerStatsRefreshedPacket packet)
        {
            packet = null!;
            if (instanceId <= 0) return false;

            ItemInstance? item = FindInInventory(instanceId);
            if (item == null) return false;  // not in this player's inventory — ownership check

            if (!ItemDatabase.TryGet(item.DefinitionId, out ItemDefinition def)) return false;

            _equippedItems[def.Slot] = item;
            RecomputeStats();
            packet = BuildStatsPacket();
            return true;
        }

        /// <summary>Clears the specified equipment slot and recomputes stats.</summary>
        public bool TryUnequipSlot(EquipSlot slot, out PlayerStatsRefreshedPacket packet)
        {
            packet = null!;

            _equippedItems.Remove(slot);
            RecomputeStats();
            packet = BuildStatsPacket();
            return true;
        }

        private void ApplyGearSetLoadout(int setIndex)
        {
            GearSetLoadout loadout = GearSets[setIndex];
            _equippedItems.Clear();

            foreach (KeyValuePair<int, int> entry in loadout.SlotItems)
            {
                var slot = (EquipSlot)entry.Key;
                ItemInstance? item = FindInInventory(entry.Value);
                if (item != null)
                    _equippedItems[slot] = item;
            }

            ActiveGearSetIndex = setIndex;
            RecomputeStats();
        }

        private void RecomputeStats()
        {
            // Preserve health as a percentage of the old MaxHealth so that equipping an item
            // that adds +50 MaxHP also raises current health by 50 (and removing it lowers it).
            // Only apply proportional scaling when there is a valid prior MaxHealth to divide by.
            float oldMaxHealth = MaxHealth;

            float maxHp      = _baseMaxHealth;
            float atkPower   = _baseAttackPower;
            float physAbsorb = _basePhysicalAbsorbPercent;
            float physResist = _basePhysicalResistPercent;
            float magAbsorb  = _baseMagicAbsorbPercent;
            float magResist  = _baseMagicResistPercent;
            float crit       = _baseCritChance;
            float lifeSteal  = _baseMeleeLifeStealPercent;
            float projRange  = 1.0f;
            int   projPierce = 0;

            // Layer 1: equipped item bonuses.
            foreach (KeyValuePair<EquipSlot, ItemInstance> kvp in _equippedItems)
            {
                // CraftedStats takes priority over the archetype base stats.
                // If neither is available (unknown definition), skip the item.
                ItemStatModifiers? m = kvp.Value.CraftedStats;
                if (m == null)
                {
                    if (!ItemDatabase.TryGet(kvp.Value.DefinitionId, out ItemDefinition def)) continue;
                    m = def.Stats;
                }

                maxHp      += m.MaxHealth;
                atkPower   += m.AttackPower;
                physAbsorb += m.PhysicalAbsorbPercent;
                physResist += m.PhysicalResistPercent;
                magAbsorb  += m.MagicAbsorbPercent;
                magResist  += m.MagicResistPercent;
                crit       += m.CritChance;
                lifeSteal  += m.MeleeLifeStealPercent;
                projRange  += m.ProjectileRangeBonus;
                projPierce += m.ProjectilePierceBonus;
            }

            // Layer 2: active status-effect stat modifiers (buffs / debuffs / consumables).
            // These are temporary and recalculated whenever an effect is applied or expires.
            foreach (KeyValuePair<int, ActiveStatusEffect> kvp in _statusEffects)
            {
                StatModifier m = kvp.Value.StatMod;
                if (!m.HasAnyValue) continue;
                maxHp      += m.MaxHealth;
                atkPower   += m.AttackPower;
                physAbsorb += m.PhysicalAbsorbPercent;
                physResist += m.PhysicalResistPercent;
                magAbsorb  += m.MagicAbsorbPercent;
                magResist  += m.MagicResistPercent;
                crit       += m.CritChance;
                lifeSteal  += m.MeleeLifeStealPercent;
                projRange  += m.ProjectileRangeBonus;
                projPierce += m.ProjectilePierceBonus;
            }

            // Layer 3: zone-wide stat modifier (zone aura, debuff field, environmental buff).
            // Set by ArenaInstance when a player enters or exits a zone-effect area.
            if (_zoneStatModifier.HasAnyValue)
            {
                maxHp      += _zoneStatModifier.MaxHealth;
                atkPower   += _zoneStatModifier.AttackPower;
                physAbsorb += _zoneStatModifier.PhysicalAbsorbPercent;
                physResist += _zoneStatModifier.PhysicalResistPercent;
                magAbsorb  += _zoneStatModifier.MagicAbsorbPercent;
                magResist  += _zoneStatModifier.MagicResistPercent;
                crit       += _zoneStatModifier.CritChance;
                lifeSteal  += _zoneStatModifier.MeleeLifeStealPercent;
                projRange  += _zoneStatModifier.ProjectileRangeBonus;
                projPierce += _zoneStatModifier.ProjectilePierceBonus;
            }

            MaxHealth                 = maxHp;
            AttackPower               = atkPower;
            PhysicalAbsorbPercent     = physAbsorb;
            PhysicalResistPercent     = physResist;
            MagicAbsorbPercent        = magAbsorb;
            MagicResistPercent        = magResist;
            CritChance                = crit;
            MeleeLifeStealPercent     = lifeSteal;
            ProjectileRangeMultiplier = projRange;
            ProjectilePierceBonus     = projPierce;

            // Proportional HP scaling: if MaxHealth changed (e.g. equipped/unequipped an item
            // with +HP), scale current Health by the same ratio so that equipping a +50 HP item
            // adds 50 to both MaxHealth and current Health rather than just clamping down.
            if (oldMaxHealth > 0f && MaxHealth != oldMaxHealth)
                Health = MathF.Min(Health * (MaxHealth / oldMaxHealth), MaxHealth);
            else
                Health = MathF.Min(Health, MaxHealth);
        }

        /// <summary>
        /// Builds the authoritative stats packet sent to a client after any stat change.
        /// Public so <see cref="ArenaInstance"/> can send it after resolving a
        /// stat-modifying status-effect tick.
        /// </summary>
        public PlayerStatsRefreshedPacket BuildStatsPacket() =>
            new PlayerStatsRefreshedPacket
            {
                ActiveGearSetIndex    = (byte)ActiveGearSetIndex,
                MaxHealth             = MaxHealth,
                AttackPower           = AttackPower,
                PhysicalAbsorbPercent = PhysicalAbsorbPercent,
                PhysicalResistPercent = PhysicalResistPercent,
                MagicAbsorbPercent    = MagicAbsorbPercent,
                MagicResistPercent    = MagicResistPercent,
                CritChance            = CritChance,
                MeleeLifeStealPercent = MeleeLifeStealPercent,
            };

        private ItemInstance? FindInInventory(int instanceId)
        {
            for (int i = 0; i < _inventory.Count; i++)
                if (_inventory[i].InstanceId == instanceId) return _inventory[i];
            return null;
        }

        public bool IsAlive => Health > 0f && !IsRespawning;

        // ── Respawn ───────────────────────────────────────────────────────────────────
        // 5 s at 30 Hz. Tune down per map when configurable map data is implemented.
        private const int DefaultRespawnTicks = 150;

        public bool IsRespawning { get; private set; }
        private int _respawnCountdown;

        /// <summary>
        /// Transitions the session into respawn-wait immediately after death.
        /// Called by ArenaInstance once death is detected this tick.
        /// </summary>
        public void StartRespawn()
        {
            IsRespawning      = true;
            _respawnCountdown = DefaultRespawnTicks;
        }

        /// <summary>
        /// Ticks the respawn countdown. When it expires, restores full health and
        /// moves the player to <paramref name="spawnPoint"/>.
        /// Returns true exactly once on the tick the player re-enters play.
        /// </summary>
        public bool TickRespawn(Vec2 spawnPoint)
        {
            if (!IsRespawning) return false;
            if (--_respawnCountdown > 0) return false;

            IsRespawning = false;
            Health       = MaxHealth;
            Position     = spawnPoint;
            return true;
        }

        // ── Kill attribution ─────────────────────────────────────────────────────────────
        /// <summary>EntityId of the last attacker that brought health to zero. 0 if unknown.</summary>
        public int LastKillerEntityId { get; private set; }

        /// <summary>Kills credited to this session in the current match.</summary>
        public int KillCount  { get; set; }

        /// <summary>Deaths accumulated by this session in the current match.</summary>
        public int DeathCount { get; set; }

        /// <summary>The TickNumber of the last PlayerInputPacket successfully applied to this session.
        /// Sent back to the client in EntityPositionPacket.AcknowledgedTick for reconciliation.</summary>
        public int LastProcessedClientTick { get; set; }

        private readonly Dictionary<int, ActiveStatusEffect> _statusEffects = new Dictionary<int, ActiveStatusEffect>();
        private readonly List<int> _statusEffectKeysBuffer = new List<int>();
        private readonly List<int> _expiredStatusEffectIds = new List<int>();
        private readonly HashSet<int> _allowedSpellIds = new HashSet<int>();

        // spellId (0 = basic auto-attack) → last tick it was activated
        private readonly Dictionary<int, int> _cooldowns = new Dictionary<int, int>();

        // ── Zone stat modifier ─────────────────────────────────────────────────────────────
        // Applied by ArenaInstance when a player enters a zone-effect area (e.g. a buff field,
        // a cursed zone, an environmental hazard).  Cleared when they leave.
        // Included in every RecomputeStats() call as Layer 3 on top of base + equipment + buffs.
        private StatModifier _zoneStatModifier;

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

        // ── Zone stat modifier ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Replaces the active zone-wide stat modifier and immediately recomputes authoritative
        /// stats.  Call with <see cref="StatModifier.Zero"/> to remove the zone effect.
        ///
        /// Typical use: player walks into a buff field → grant the modifier.
        ///              Player leaves the field → clear it with Zero.
        /// </summary>
        public PlayerStatsRefreshedPacket SetZoneStatModifier(StatModifier modifier)
        {
            _zoneStatModifier = modifier;
            RecomputeStats();
            return BuildStatsPacket();
        }

        // ── Stat-buffing status effects ─────────────────────────────────────────────────────
        // Standard TryApplyStatusEffect is for DoT/HoT effects (no stat change).
        // TryApplyStatBuff is for temporary pure-stat modifiers: consumables, Battle Stance, etc.

        /// <summary>
        /// Applies a temporary stat-modifier effect (buff or debuff).  Unlike regular status
        /// effects, this has no periodic damage component; it purely changes stats for the
        /// duration.
        ///
        /// <paramref name="modifier"/> is summed in <see cref="RecomputeStats"/> while the
        /// effect is active and removed when it expires via <see cref="TickStatusEffects"/>.
        /// </summary>
        public bool TryApplyStatBuff(
            int effectId,
            int sourceEntityId,
            int durationTicks,
            int stacks,
            StatModifier modifier,
            StatusEffectVisibility visibility,
            out StatusEffectAppliedPacket packet,
            out PlayerStatsRefreshedPacket? statsPacket)
        {
            statsPacket = null;

            bool applied = TryApplyStatusEffect(
                effectId, sourceEntityId, durationTicks, stacks,
                tickDamage: 0, tickIntervalTicks: 0,
                sourceHealPercentPerTick: 0f, visibility, out packet);

            if (!applied) return false;

            // Write the stat modifier into the newly stored effect.
            if (modifier.HasAnyValue && _statusEffects.TryGetValue(effectId, out ActiveStatusEffect eff))
            {
                eff.StatMod = modifier;
                _statusEffects[effectId] = eff;
                RecomputeStats();
                statsPacket = BuildStatsPacket();
            }

            return true;
        }

        // ── Inventory mutation ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Adds an item to the player's inventory.  Called when a ground item is picked up.
        /// Does NOT auto-equip the item — equipping is a separate explicit player action
        /// that routes through <see cref="TryEquipItem"/> and triggers a stat recompute.
        ///
        /// Returns <c>false</c> and discards the item when the inventory is full
        /// (<paramref name="maxInventorySize"/> enforced server-side so clients cannot bloat
        /// their inventory by racing pickups).
        /// </summary>
        public bool PickupItem(ItemInstance item, int maxInventorySize)
        {
            if (_inventory.Count >= maxInventorySize)
                return false;

            _inventory.Add(item);
            return true;
        }

        public void ApplyDamage(int damage, int killerEntityId = 0)
        {
            Health -= damage;
            if (Health <= 0f)
            {
                Health = 0f;
                if (killerEntityId != 0)
                    LastKillerEntityId = killerEntityId;
            }
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
                // Preserve the existing StatMod on refresh — the stat buff is just being extended.

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

        /// <summary>
        /// Ticks all active status effects: decrements timers, fires periodic damage/heal,
        /// and removes expired effects.
        ///
        /// Returns <c>true</c> when at least one stat-modifying effect expired this tick,
        /// indicating that <see cref="ArenaInstance"/> must send a
        /// <see cref="PlayerStatsRefreshedPacket"/> to this player's peer.
        /// </summary>
        public bool TickStatusEffects(
            IReadOnlyDictionary<int, PlayerSession> entityMap,
            List<CombatEventPacket> tickDamageEvents,
            List<StatusEffectRemovedPacket> expiredPackets)
        {
            if (_statusEffects.Count == 0)
                return false;

            bool statsDirtied = false;
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
                        entityMap.TryGetValue(effect.SourceEntityId, out PlayerSession? source);
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

                // If the expired effect carried a stat modifier, mark stats as dirty so
                // ArenaInstance sends a PlayerStatsRefreshedPacket after this call returns.
                if (effect.StatMod.HasAnyValue)
                    statsDirtied = true;

                expiredPackets.Add(new StatusEffectRemovedPacket
                {
                    TargetEntityId = EntityId,
                    EffectId       = effectId,
                    Visibility     = effect.Visibility,
                });
            }

            // Single RecomputeStats call after all expiries to avoid redundant recalculation
            // when multiple stat-modifying effects expire on the same tick.
            if (statsDirtied)
                RecomputeStats();

            return statsDirtied;
        }

        public void RestoreHealth(float amount)
        {
            Health += amount;
            if (Health > MaxHealth) Health = MaxHealth;
        }

        public void ReplaceAllowedSpells(IEnumerable<int> spellIds)
        {
            _allowedSpellIds.Clear();
            foreach (int spellId in spellIds)
            {
                if (spellId > 0)
                    _allowedSpellIds.Add(spellId);
            }
        }

        public bool IsSpellAllowed(int spellId)
            => _allowedSpellIds.Contains(spellId);

        // ── Position History (lag compensation) ─────────────────────────────────────
        // 64 slots at 30 Hz gives ~2.1 s of rewind depth — enough to cover any realistic RTT.
        private const int PositionHistorySize         = 64;
        private readonly Vec2[] _positionHistory      = new Vec2[PositionHistorySize];

        /// <summary>
        /// Snapshots the current authoritative position into the ring buffer.
        /// Called by ArenaInstance once per tick, immediately after the movement phase.
        /// </summary>
        public void RecordPositionHistory(int serverTick)
        {
            _positionHistory[serverTick % PositionHistorySize] = Position;
        }

        /// <summary>
        /// Returns the stored position at <paramref name="requestedTick"/>, clamped to the
        /// range [currentTick − maxRewindTicks, currentTick].
        ///
        /// The upper bound (currentTick) is critical: IntentGuard admits packets with up to
        /// MaxFutureTickSkew=5 ahead of the server clock. Without the upper clamp, a future
        /// requestedTick would index a ring-buffer slot that was last written ~(PositionHistorySize
        /// − futureDelta) ticks ago — returning stale 2-second-old position data and producing
        /// ghost hits or ghost misses in lag-compensation.
        /// </summary>
        public Vec2 GetHistoricalPosition(int requestedTick, int currentTick, int maxRewindTicks)
        {
            // maxRewindTicks must never exceed PositionHistorySize; the buffer only holds that many slots.
            System.Diagnostics.Debug.Assert(
                maxRewindTicks <= PositionHistorySize,
                $"maxRewindTicks ({maxRewindTicks}) exceeds PositionHistorySize ({PositionHistorySize}); history will wrap.");

            int safeTick = Math.Clamp(requestedTick, currentTick - maxRewindTicks, currentTick);
            return _positionHistory[safeTick % PositionHistorySize];
        }

        // ── State snapshot (heartbeat / zone handoff) ─────────────────────────────────────

        /// <summary>
        /// Produces a serialisable snapshot of the current authoritative session state for
        /// Redis heartbeat saves or zone-transfer handoffs.
        ///
        /// <paramref name="includeInventory"/> should be <c>true</c> for open-world MMO zones
        /// where items picked up mid-session must persist.  In Arena mode pass <c>false</c>
        /// because in-session pickups are intentionally discarded at match end; only crafting
        /// ingredient rewards (computed separately) are persisted.
        /// </summary>
        public DataLayer.LivePlayerState TakeSnapshot(bool includeInventory = true)
        {
            return new DataLayer.LivePlayerState
            {
                AccountId      = AccountId,
                PlayerName     = PlayerName,
                Position       = Position,
                Health         = Health,
                MaxHealth      = MaxHealth,
                Inventory      = includeInventory ? _inventory.ToArray()
                                                  : System.Array.Empty<DataLayer.ItemInstance>(),
                GearSets       = GearSets,
                ActiveGearSet  = ActiveGearSetIndex,
                SnapshotTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
        }
    }
}
