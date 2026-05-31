using SharedLibrary;
using System;
using System.Collections.Generic;

namespace GameServer.Systems
{
    /// <summary>
    /// Handles the full lifecycle of in-flight projectiles:
    ///   1. SpawnProjectile — validates a ShootRequestPacket and creates a ProjectileState.
    ///   2. Tick           — advances every active projectile, detects collisions, and
    ///                       returns hit/expiry results for the arena to broadcast.
    /// </summary>
    public static class ProjectileSystem
    {
        // ── Result Type ───────────────────────────────────────────────────────

        /// <summary>
        /// Output from one Tick call. All lists are null when nothing occurred
        /// (avoids allocating empty lists every frame).
        /// </summary>
        public readonly struct TickResult
        {
            /// <summary>
            /// Projectiles that scored a final hit (all pierce charges consumed).
            /// Broadcast a CombatEventPacket + ProjectileDestroyPacket(HitSomething=true) for each.
            /// </summary>
            public readonly List<(int ProjectileId, CombatEventPacket Hit)>? Hits;

            /// <summary>
            /// Damage events from pierce hits. The projectile is still in flight.
            /// Broadcast a CombatEventPacket only — no destroy packet.
            /// </summary>
            public readonly List<CombatEventPacket>? PierceHits;

            /// <summary>
            /// Status effects applied by projectile hits and splash.
            /// Filtered by the arena before broadcasting.
            /// </summary>
            public readonly List<StatusEffectAppliedPacket>? StatusEffects;

            /// <summary>
            /// Secondary AoE damage events caused by explosive projectiles detonating on
            /// final impact. These targets are in addition to the primary hit target.
            /// Broadcast a CombatEventPacket only — the destroy packet is sent with Hits.
            /// </summary>
            public readonly List<CombatEventPacket>? SplashHits;

            /// <summary>
            /// IDs of projectiles that exceeded MaxRange or grazed a near-miss.
            /// Send ProjectileDestroyPacket (HitSomething=false) for each.
            /// </summary>
            public readonly List<int>? ExpiredIds;

            public TickResult(
                List<(int, CombatEventPacket)>? hits,
                List<CombatEventPacket>?         pierceHits,
                List<StatusEffectAppliedPacket>? statusEffects,
                List<CombatEventPacket>?         splashHits,
                List<int>?                       expired)
            {
                Hits       = hits;
                PierceHits = pierceHits;
                StatusEffects = statusEffects;
                SplashHits = splashHits;
                ExpiredIds = expired;
            }
        }

        // ── Scratch lists (pre-allocated, cleared before each Tick call) ─────
        // The ??= new List<T>() pattern inside Tick allocates a new list on the first
        // hit every tick, causing GC pressure proportional to combat activity.
        // These static scratch lists are reused across every Tick invocation instead.
        // ProjectileSystem is always called from the single game-loop thread, so
        // no synchronisation is needed.
        private static readonly List<(int, CombatEventPacket)>    s_hits         = new(8);
        private static readonly List<CombatEventPacket>           s_pierceHits   = new(8);
        private static readonly List<StatusEffectAppliedPacket>   s_statusEffects= new(8);
        private static readonly List<CombatEventPacket>           s_splashHits   = new(8);
        private static readonly List<int>                         s_expiredIds   = new(8);

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Validates the ShootRequestPacket and writes a new ProjectileState into
        /// <paramref name="result"/> via an out parameter.
        ///
        /// Returns false if the direction vector is degenerate (zero-vector exploit) or any
        /// spell/stats field is non-finite.  The caller is responsible for cooldown checks.
        ///
        /// Zero-allocation: ProjectileState is a struct; the out parameter is written directly
        /// into the caller's pre-allocated array slot without any heap allocation.
        /// </summary>
        public static bool TrySpawnProjectile(
            PlayerSession      shooter,
            ShootRequestPacket request,
            SpellDefinition    spell,
            int                projectileId,
            out ProjectileState result)
        {
            result = default;

            if (!float.IsFinite(request.DirectionX) || !float.IsFinite(request.DirectionY))
                return false;

            if (!float.IsFinite(spell.ProjectileSpeed) || spell.ProjectileSpeed <= 0f)
                return false;

            if (!float.IsFinite(spell.ProjectileHitRadius) || spell.ProjectileHitRadius <= 0f)
                return false;

            if (!float.IsFinite(spell.Range) || spell.Range <= 0f)
                return false;

            // Re-normalise the client-supplied direction — never trust raw client values
            float mag = MathF.Sqrt(request.DirectionX * request.DirectionX +
                                   request.DirectionY * request.DirectionY);
            if (!float.IsFinite(mag) || mag < 0.001f)
                return false;

            float maxRange = spell.Range * shooter.ProjectileRangeMultiplier;
            if (!float.IsFinite(maxRange) || maxRange <= 0f)
                return false;

            // Write directly into the out parameter — the caller stores this in a pre-allocated
            // array slot, so there is zero heap allocation for this entire spawn operation.
            result = new ProjectileState
            {
                ProjectileId      = projectileId,
                OwnerId           = shooter.EntityId,
                // Snapshot owner faction at spawn time so MatchesFactionFilter resolves in O(1)
                // without scanning allPlayers on every collision check.
                OwnerFaction      = shooter.Faction,
                SpellId           = spell.SpellId,
                Position          = shooter.Position,          // server-authoritative spawn point
                DirectionX        = request.DirectionX / mag,
                DirectionY        = request.DirectionY / mag,
                Speed             = spell.ProjectileSpeed,
                HitRadius         = spell.ProjectileHitRadius,
                // Range scales with the shooter's stat — snapshotted at launch time
                MaxRange          = maxRange,
                BaseDamage        = spell.BaseDamage,
                AttackPower       = shooter.AttackPower,       // snapshot at launch time
                CritChance        = shooter.CritChance,
                LifeStealPercent  = spell.LifeStealPercent,
                TargetFactionFilter = spell.TargetFactionFilter,
                StatusEffectId    = spell.StatusEffectId,
                StatusEffectDurationTicks = spell.StatusEffectDurationTicks,
                StatusEffectChance = spell.StatusEffectChance,
                StatusEffectStacks = spell.StatusEffectStacks,
                StatusEffectTickDamage = spell.StatusEffectTickDamage,
                StatusEffectTickIntervalTicks = spell.StatusEffectTickIntervalTicks,
                StatusEffectOwnerHealPercentPerTick = spell.StatusEffectOwnerHealPercentPerTick,
                StatusEffectVisibility = spell.StatusEffectVisibility,
                // BaseHitChance <= 0 means the spell definition didn't set it; treat as 1.0
                BaseHitChance     = spell.BaseHitChance > 0f ? spell.BaseHitChance : 1.0f,
                FalloffPerUnit    = spell.HitFalloffPerUnit,
                // Snapshot pierce: base spell value + any item/skill bonus the shooter has
                PierceCount       = spell.BasePierceCount + shooter.ProjectilePierceBonus,
                // Snapshot AoE radius: > 0 means the arrow detonates on final impact
                AoERadius         = spell.AoERadius,
                // Snapshot damage type and pierce chance so stat changes mid-flight don't affect in-flight projectiles
                DamageType        = spell.DamageType,
                PierceChance      = spell.PierceChance,
                TraveledDistance  = 0f,
            };
            return true;
        }

        /// <summary>
        /// Advances every active projectile by one tick.
        /// Removes projectiles that collide with a player or exceed their MaxRange,
        /// and returns the corresponding events.
        ///
        /// Zero-allocation design:
        ///   • projectiles[] is a pre-allocated fixed array; projectileCount is passed by ref
        ///     so this method can compact the array in-place without any List overhead.
        ///   • ref ProjectileState proj = ref projectiles[i] gives a managed reference to the
        ///     array element — mutations (position, pierce count) write directly to the array
        ///     slot with no intermediate copy or heap activity.
        ///   • Removal uses an O(1) forward-iteration swap-remove: the last live element is
        ///     copied to the removed slot and projectileCount is decremented.  The loop index
        ///     is NOT advanced after a removal so the newly moved element is processed next.
        ///   • When grid != null, QueryNeighbours() narrows collision candidates from O(N) to
        ///     O(k) — critical at MMORPG scale where N = 2 000 and projectile counts are high.
        /// </summary>
        public static TickResult Tick(
            ProjectileState[]            projectiles,
            ref int                      projectileCount,
            List<PlayerSession>          allPlayers,
            System.Collections.Generic.IReadOnlyDictionary<int, PlayerSession> entityMap,
            float                        deltaTime,
            SpatialGrid?                 grid = null)
        {
            // Clear scratch lists — no allocation, just resets the Count to 0.
            s_hits.Clear();
            s_pierceHits.Clear();
            s_statusEffects.Clear();
            s_splashHits.Clear();
            s_expiredIds.Clear();

            // Forward-iteration with manual index management.
            // On removal: swap-remove the slot (copy last element to [i]) and do NOT
            // advance i — the loop will re-examine the moved element on the next iteration.
            for (int i = 0; i < projectileCount; /* advanced below */)
            {
                ref ProjectileState proj = ref projectiles[i];

                if (!float.IsFinite(proj.Position.X)
                    || !float.IsFinite(proj.Position.Y)
                    || !float.IsFinite(proj.DirectionX)
                    || !float.IsFinite(proj.DirectionY)
                    || !float.IsFinite(proj.Speed)
                    || !float.IsFinite(proj.TraveledDistance)
                    || !float.IsFinite(proj.MaxRange))
                {
                    s_expiredIds.Add(proj.ProjectileId);
                    SwapRemove(projectiles, ref projectileCount, i);
                    // Do NOT increment i — the swapped-in element must be examined next.
                    continue;
                }

                // ── Move ──────────────────────────────────────────────────────
                // Direction is normalised, so distance = Speed × deltaTime exactly.
                // Writing through the ref mutates the array element in-place — zero copy.
                float step = proj.Speed * deltaTime;
                if (!float.IsFinite(step) || step <= 0f)
                {
                    s_expiredIds.Add(proj.ProjectileId);
                    SwapRemove(projectiles, ref projectileCount, i);
                    continue;
                }

                proj.Position = new Vec2(
                    proj.Position.X + proj.DirectionX * step,
                    proj.Position.Y + proj.DirectionY * step);
                proj.TraveledDistance += step;

                // ── Collision ─────────────────────────────────────────────────
                // Narrow the candidate set with the spatial grid when available.
                // grid.QueryNeighbours() returns a pre-allocated scratch List —
                // safe to use here because we consume it fully before the next
                // QueryNeighbours call (which would overwrite the same buffer).
                IReadOnlyList<PlayerSession> candidates =
                    grid != null ? grid.QueryNeighbours(proj.Position) : allPlayers;

                bool hitSomeone = false;

                for (int j = 0; j < candidates.Count; j++)
                {
                    PlayerSession target = candidates[j];
                    if (!target.IsAlive || target.EntityId == proj.OwnerId)
                        continue;

                    // O(1) faction check — OwnerFaction was snapshotted at spawn time.
                    if (!MatchesFactionFilter(proj.TargetFactionFilter, proj.OwnerFaction, target))
                        continue;

                    if (!CombatMath.IsInAoE(proj.Position, proj.HitRadius, target.Position))
                        continue;

                    // ── First collision found — projectile is consumed here regardless ──
                    // Roll distance-based hit chance. At long range, the projectile may
                    // graze the hitbox (near-miss) and still be removed from play.
                    float hitChance = CombatMath.ProjectileHitChance(
                        proj.BaseHitChance, proj.FalloffPerUnit, proj.TraveledDistance);

                    if (Random.Shared.NextDouble() <= hitChance)
                    {
                        // ── HIT ───────────────────────────────────────────────────────
                        float absorb = proj.DamageType == DamageType.Magic
                            ? target.MagicAbsorbPercent : target.PhysicalAbsorbPercent;
                        float resist = proj.DamageType == DamageType.Magic
                            ? target.MagicResistPercent : target.PhysicalResistPercent;
                        int  damage = CombatMath.CalculateDamage(
                            proj.BaseDamage, proj.AttackPower, proj.DamageType,
                            absorb, resist, proj.PierceChance, Random.Shared.NextDouble());
                        bool isCrit = CombatMath.IsCriticalHit(Random.Shared.NextDouble(), proj.CritChance);
                        if (isCrit) damage *= 2;

                        target.ApplyDamage(damage, proj.OwnerId);
                        ApplyLifeSteal(proj, damage, entityMap);
                        ApplyProjectileStatusEffect(proj, target);

                        CombatEventPacket combatEv = new CombatEventPacket
                        {
                            PacketTypeId = PacketId.CombatEvent,
                            AttackerId   = proj.OwnerId,
                            TargetId     = target.EntityId,
                            Damage       = DamageUtils.ClampAndEncode(damage, proj.OwnerId, "projectile"),
                        };
                        combatEv.IsCritical = isCrit;

                        if (proj.PierceCount > 0)
                        {
                            // ── PIERCE — projectile continues flying ───────────────────────
                            // Consume one charge, broadcast damage without destroy packet.
                            // Do NOT break — continue checking remaining targets this tick.
                            proj.PierceCount--;
                            s_pierceHits.Add(combatEv);
                        }
                        else
                        {
                            // ── FINAL HIT — all pierce charges used ─────────────────────
                            // Capture ProjectileId BEFORE SwapRemove — after the swap, proj
                            // (ref to projectiles[i]) points to the moved element, not this one.
                            int projId = proj.ProjectileId;
                            s_hits.Add((projId, combatEv));

                            // Explosive detonation: splash all other players in AoE radius.
                            // Pass allPlayers (not grid-narrowed candidates) to ensure full AoE
                            // coverage beyond the per-projectile collision query window.
                            if (proj.AoERadius > 0f)
                                ApplyExplosiveSplash(proj, target, allPlayers, entityMap);

                            SwapRemove(projectiles, ref projectileCount, i);
                            hitSomeone = true;
                            break;
                        }
                    }
                    else
                    {
                        // ── NEAR MISS ─────────────────────────────────────────────────
                        // Geometric overlap but deflected at distance. Near-misses always
                        // consume the projectile regardless of remaining pierce charges.
                        s_expiredIds.Add(proj.ProjectileId);
                        SwapRemove(projectiles, ref projectileCount, i);
                        hitSomeone = true;
                        break;
                    }
                }

                // ── Range Expiry ──────────────────────────────────────────────
                if (!hitSomeone && proj.TraveledDistance >= proj.MaxRange)
                {
                    s_expiredIds.Add(proj.ProjectileId);
                    SwapRemove(projectiles, ref projectileCount, i);
                    // Do NOT increment i.
                    continue;
                }

                if (!hitSomeone)
                    i++; // Only advance when the slot was not replaced by a swap-remove.
            }

            // Return references to scratch lists — callers must not hold references across ticks.
            return new TickResult(
                s_hits.Count         > 0 ? s_hits         : null,
                s_pierceHits.Count   > 0 ? s_pierceHits   : null,
                s_statusEffects.Count> 0 ? s_statusEffects: null,
                s_splashHits.Count   > 0 ? s_splashHits   : null,
                s_expiredIds.Count   > 0 ? s_expiredIds   : null);
        }

        /// <summary>
        /// O(1) in-place removal: copies the last live element into slot <paramref name="index"/>
        /// and decrements the count.  The moved element will be re-examined by the caller
        /// on the next loop iteration (caller must NOT advance the index after calling this).
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static void SwapRemove(ProjectileState[] array, ref int count, int index)
        {
            count--;
            if (index < count)
                array[index] = array[count];
        }

        // ── Private Helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Deals splash damage to all living players (except the shooter and primary target)
        /// within <see cref="ProjectileState.AoERadius"/> of the detonation point.
        /// Called only on final hits where AoERadius > 0.
        /// </summary>
        private static void ApplyExplosiveSplash(
            ProjectileState              proj,
            PlayerSession                primaryTarget,
            List<PlayerSession>          allPlayers,
            System.Collections.Generic.IReadOnlyDictionary<int, PlayerSession> entityMap)
        {
            for (int k = 0; k < allPlayers.Count; k++)
            {
                PlayerSession splash = allPlayers[k];

                // Skip: dead, the shooter, or the primary target (already handled above)
                if (!splash.IsAlive
                    || splash.EntityId == proj.OwnerId
                    || splash.EntityId == primaryTarget.EntityId)
                    continue;

                if (!CombatMath.IsInAoE(proj.Position, proj.AoERadius, splash.Position))
                    continue;

                if (!MatchesFactionFilter(proj.TargetFactionFilter, proj.OwnerFaction, splash))
                    continue;

                float splashAbsorb = proj.DamageType == DamageType.Magic
                    ? splash.MagicAbsorbPercent : splash.PhysicalAbsorbPercent;
                float splashResist = proj.DamageType == DamageType.Magic
                    ? splash.MagicResistPercent : splash.PhysicalResistPercent;
                int  splashDmg  = CombatMath.CalculateDamage(
                    proj.BaseDamage, proj.AttackPower, proj.DamageType,
                    splashAbsorb, splashResist, proj.PierceChance, Random.Shared.NextDouble());
                bool splashCrit = CombatMath.IsCriticalHit(Random.Shared.NextDouble(), proj.CritChance);
                if (splashCrit) splashDmg *= 2;

                splash.ApplyDamage(splashDmg, proj.OwnerId);
                ApplyLifeSteal(proj, splashDmg, entityMap);
                ApplyProjectileStatusEffect(proj, splash);

                CombatEventPacket splashEv = new CombatEventPacket
                {
                    PacketTypeId = PacketId.CombatEvent,
                    AttackerId   = proj.OwnerId,
                    TargetId     = splash.EntityId,
                    Damage       = DamageUtils.ClampAndEncode(splashDmg, proj.OwnerId, "splash"),
                };
                splashEv.IsCritical = splashCrit;
                s_splashHits.Add(splashEv);
            }
        }

        private static void ApplyProjectileStatusEffect(
            ProjectileState proj,
            PlayerSession target)
        {
            if (proj.StatusEffectId <= 0)
                return;

            if (proj.StatusEffectChance <= 0f)
                return;

            if (Random.Shared.NextDouble() > proj.StatusEffectChance)
                return;

            if (!target.TryApplyStatusEffect(
                    proj.StatusEffectId,
                    proj.OwnerId,
                    proj.StatusEffectDurationTicks,
                    proj.StatusEffectStacks,
                    proj.StatusEffectTickDamage,
                    proj.StatusEffectTickIntervalTicks,
                    proj.StatusEffectOwnerHealPercentPerTick,
                    proj.StatusEffectVisibility,
                    out StatusEffectAppliedPacket packet))
            {
                return;
            }

            s_statusEffects.Add(packet);
        }

        // O(1) life-steal heal — resolves shooter by entity-map lookup instead of O(N) linear scan.
        // At 2 000 players with 100 active projectiles hitting per tick the old O(N) scan cost
        // 200 000 iterations/tick just for life steal; the dictionary lookup is a single hash probe.
        private static void ApplyLifeSteal(
            ProjectileState proj,
            int damage,
            System.Collections.Generic.IReadOnlyDictionary<int, PlayerSession> entityMap)
        {
            if (damage <= 0 || proj.LifeStealPercent <= 0f)
                return;

            if (!entityMap.TryGetValue(proj.OwnerId, out PlayerSession? owner))
                return;

            float heal = damage * proj.LifeStealPercent;
            if (heal > 0f)
                owner.RestoreHealth(heal);
        }

        /// <summary>
        /// O(1) faction filter.  <paramref name="ownerFaction"/> is the shooter's faction
        /// snapshotted into <see cref="ProjectileState.OwnerFaction"/> at spawn time,
        /// eliminating the O(N) allPlayers scan that would otherwise be required.
        /// </summary>
        private static bool MatchesFactionFilter(
            TargetFactionFilter filter,
            FactionId           ownerFaction,
            PlayerSession       target)
        {
            return filter switch
            {
                TargetFactionFilter.Any        => true,
                TargetFactionFilter.AlliesOnly => target.Faction == ownerFaction,
                _                              => target.Faction != ownerFaction,
            };
        }
    }
}
