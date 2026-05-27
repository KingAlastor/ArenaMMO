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

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Validates the ShootRequestPacket and constructs a ProjectileState.
        /// Returns null if the direction vector is degenerate (zero-vector exploit).
        /// The caller is responsible for cooldown checks before calling this.
        /// </summary>
        public static ProjectileState? SpawnProjectile(
            PlayerSession     shooter,
            ShootRequestPacket request,
            SpellDefinition    spell,
            int                projectileId)
        {
            // Re-normalise the client-supplied direction — never trust raw client values
            float mag = MathF.Sqrt(request.DirectionX * request.DirectionX +
                                   request.DirectionY * request.DirectionY);
            if (mag < 0.001f)
                return null;

            return new ProjectileState
            {
                ProjectileId      = projectileId,
                OwnerId           = shooter.EntityId,
                SpellId           = spell.SpellId,
                Position          = shooter.Position,          // server-authoritative spawn point
                DirectionX        = request.DirectionX / mag,
                DirectionY        = request.DirectionY / mag,
                Speed             = spell.ProjectileSpeed,
                HitRadius         = spell.ProjectileHitRadius,
                // Range scales with the shooter's stat — snapshotted at launch time
                MaxRange          = spell.Range * shooter.ProjectileRangeMultiplier,
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
                TraveledDistance  = 0f,
            };
        }

        /// <summary>
        /// Advances every active projectile by one tick.
        /// Removes projectiles that collide with a player or exceed their MaxRange,
        /// and returns the corresponding events.
        /// Iterates the list in reverse to allow safe in-place removal via RemoveAt.
        /// </summary>
        public static TickResult Tick(
            List<ProjectileState>        projectiles,
            IReadOnlyList<PlayerSession> allPlayers,
            float                        deltaTime)
        {
            List<(int, CombatEventPacket)>? hits       = null;
            List<CombatEventPacket>?         pierceHits = null;
            List<StatusEffectAppliedPacket>? statusEffects = null;
            List<CombatEventPacket>?         splashHits = null;
            List<int>?                       expiredIds = null;

            for (int i = projectiles.Count - 1; i >= 0; i--)
            {
                ProjectileState proj = projectiles[i];

                // ── Move ──────────────────────────────────────────────────────
                // Direction is normalised, so distance = Speed × deltaTime exactly
                float step = proj.Speed * deltaTime;
                proj.Position = new Vec2(
                    proj.Position.X + proj.DirectionX * step,
                    proj.Position.Y + proj.DirectionY * step);
                proj.TraveledDistance += step;

                // ── Collision ─────────────────────────────────────────────────
                bool hitSomeone = false;

                for (int j = 0; j < allPlayers.Count; j++)
                {
                    PlayerSession target = allPlayers[j];
                    if (!target.IsAlive || target.EntityId == proj.OwnerId)
                        continue;

                    if (!MatchesFactionFilter(proj.TargetFactionFilter, allPlayers, proj.OwnerId, target))
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
                        int  damage = CombatMath.CalculateDamage(proj.BaseDamage, proj.AttackPower, target.Armor);
                        bool isCrit = CombatMath.IsCriticalHit(Random.Shared.NextDouble(), proj.CritChance);
                        if (isCrit) damage *= 2;

                        target.ApplyDamage(damage);
                        ApplyLifeSteal(proj, damage, allPlayers);
                        ApplyProjectileStatusEffect(proj, target, ref statusEffects);

                        var combatEv = new CombatEventPacket
                        {
                            AttackerId = proj.OwnerId,
                            TargetId   = target.EntityId,
                            Damage     = damage,
                            IsCritical = isCrit,
                        };

                        if (proj.PierceCount > 0)
                        {
                            // ── PIERCE — projectile continues flying ───────────────────────
                            // Consume one charge, broadcast damage without destroy packet.
                            // Do NOT break — continue checking remaining targets this tick.
                            proj.PierceCount--;
                            pierceHits ??= new List<CombatEventPacket>();
                            pierceHits.Add(combatEv);
                        }
                        else
                        {
                            // ── FINAL HIT — all pierce charges used ─────────────────────
                            hits ??= new List<(int, CombatEventPacket)>();
                            hits.Add((proj.ProjectileId, combatEv));

                            // Explosive detonation: splash all other players in AoE radius
                            if (proj.AoERadius > 0f)
                                ApplyExplosiveSplash(proj, target, allPlayers, ref splashHits, ref statusEffects);

                            projectiles.RemoveAt(i);
                            hitSomeone = true;
                            break;
                        }
                    }
                    else
                    {
                        // ── NEAR MISS ─────────────────────────────────────────────────
                        // Geometric overlap but deflected at distance. Near-misses always
                        // consume the projectile regardless of remaining pierce charges.
                        expiredIds ??= new List<int>();
                        expiredIds.Add(proj.ProjectileId);
                        projectiles.RemoveAt(i);
                        hitSomeone = true;
                        break;
                    }
                }

                // ── Range Expiry ──────────────────────────────────────────────
                if (!hitSomeone && proj.TraveledDistance >= proj.MaxRange)
                {
                    expiredIds ??= new List<int>();
                    expiredIds.Add(proj.ProjectileId);
                    projectiles.RemoveAt(i);
                }
            }

            return new TickResult(hits, pierceHits, statusEffects, splashHits, expiredIds);
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
            IReadOnlyList<PlayerSession> allPlayers,
            ref List<CombatEventPacket>? splashHits,
            ref List<StatusEffectAppliedPacket>? statusEffects)
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

                int  splashDmg  = CombatMath.CalculateDamage(proj.BaseDamage, proj.AttackPower, splash.Armor);
                bool splashCrit = CombatMath.IsCriticalHit(Random.Shared.NextDouble(), proj.CritChance);
                if (splashCrit) splashDmg *= 2;

                splash.ApplyDamage(splashDmg);
                ApplyLifeSteal(proj, splashDmg, allPlayers);
                ApplyProjectileStatusEffect(proj, splash, ref statusEffects);

                splashHits ??= new List<CombatEventPacket>();
                splashHits.Add(new CombatEventPacket
                {
                    AttackerId = proj.OwnerId,
                    TargetId   = splash.EntityId,
                    Damage     = splashDmg,
                    IsCritical = splashCrit,
                });
            }
        }

        private static void ApplyProjectileStatusEffect(
            ProjectileState proj,
            PlayerSession target,
            ref List<StatusEffectAppliedPacket>? statusEffects)
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

            statusEffects ??= new List<StatusEffectAppliedPacket>();
            statusEffects.Add(packet);
        }

        private static void ApplyLifeSteal(
            ProjectileState proj,
            int damage,
            IReadOnlyList<PlayerSession> allPlayers)
        {
            if (damage <= 0 || proj.LifeStealPercent <= 0f)
                return;

            for (int i = 0; i < allPlayers.Count; i++)
            {
                if (allPlayers[i].EntityId != proj.OwnerId)
                    continue;

                float heal = damage * proj.LifeStealPercent;
                if (heal > 0f)
                    allPlayers[i].RestoreHealth(heal);

                return;
            }
        }

        private static bool MatchesFactionFilter(
            TargetFactionFilter filter,
            IReadOnlyList<PlayerSession> allPlayers,
            int ownerId,
            PlayerSession target)
        {
            PlayerSession? owner = null;
            for (int i = 0; i < allPlayers.Count; i++)
            {
                if (allPlayers[i].EntityId == ownerId)
                {
                    owner = allPlayers[i];
                    break;
                }
            }

            if (owner == null)
                return false;

            return filter switch
            {
                TargetFactionFilter.Any => true,
                TargetFactionFilter.AlliesOnly => target.Faction == owner.Faction,
                _ => target.Faction != owner.Faction,
            };
        }
    }
}
