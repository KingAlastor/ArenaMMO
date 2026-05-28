using SharedLibrary;
using System;
using System.Collections.Generic;

namespace GameServer.Systems
{
    /// <summary>
    /// Server-authoritative combat resolution for melee attacks, single-target spells,
    /// and ground-targeted AoE spells.
    ///
    /// All range, cooldown, and validity checks are performed here on the server.
    /// The client's input is only a request — it is never trusted directly.
    /// </summary>
    public static class CombatSystem
    {
        private const int BasicAttackCooldownTicks = 15;  // 0.5 s at 30 Hz
        private const int BasicAttackBaseDamage    = 10;
        // Maximum ticks to rewind for hit-detection lag compensation (~333 ms at 30 Hz).
        private const int MaxRewindTicks            = 10;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves a basic melee auto-attack request.
        /// Returns a <see cref="CombatEventPacket"/> to broadcast, or null if the
        /// attack was rejected (dead, out of range, or on cooldown).
        /// Spell slot 0 is reserved for the auto-attack cooldown.
        /// </summary>
        public static CombatEventPacket? ProcessMeleeAttack(
            PlayerSession attacker,
            PlayerSession target,
            int currentTick,
            int clientAttackTick,
            List<StatusEffectAppliedPacket>? statusEffects = null)
        {
            if (!attacker.IsAlive || !target.IsAlive)
                return null;

            if (attacker.EntityId == target.EntityId)
                return null;

            if (target.Faction == attacker.Faction)
                return null;

            if (attacker.IsOnCooldown(0, currentTick, BasicAttackCooldownTicks))
                return null;

            // Validate range against the target's historical position at the tick the client attacked.
            // This prevents penalising high-latency players whose hit was valid on their screen.
            Vec2 targetHistoricalPos = target.GetHistoricalPosition(
                clientAttackTick, currentTick, MaxRewindTicks);
            if (!CombatMath.IsInRange(attacker.Position, targetHistoricalPos, CombatMath.MeleeRange))
                return null;

            int  damage = CombatMath.CalculateDamage(BasicAttackBaseDamage, attacker.AttackPower, target.Armor);
            bool isCrit = CombatMath.IsCriticalHit(Random.Shared.NextDouble(), attacker.CritChance);
            if (isCrit) damage *= 2;

            target.ApplyDamage(damage);
            ApplyLifeSteal(attacker, damage, attacker.MeleeLifeStealPercent);
            ApplyWeaponStatusEffect(attacker, target, statusEffects);
            attacker.SetCooldown(0, currentTick);

            return new CombatEventPacket
            {
                AttackerId = attacker.EntityId,
                TargetId   = target.EntityId,
                Damage     = damage,
                IsCritical = isCrit,
            };
        }

        /// <summary>
        /// Resolves a spell cast request.
        /// Returns one <see cref="CombatEventPacket"/> per player hit (may be empty if rejected).
        /// Routing (single-target vs AoE) is determined from the server's SpellDefinition,
        /// never from anything the client sends.
        /// </summary>
        public static void ProcessSpellCast(
            PlayerSession                caster,
            SpellCastRequestPacket       request,
            SpellDefinition              spell,
            IReadOnlyList<PlayerSession> allPlayers,
            int                          currentTick,
            List<CombatEventPacket>      results,
            List<StatusEffectAppliedPacket>? statusEffects = null)
        {
            if (!caster.IsAlive)
                return;

            if (caster.IsOnCooldown(spell.SpellId, currentTick, spell.CooldownTicks))
                return;

            switch (spell.TargetType)
            {
                case SpellTargetType.SingleTarget:
                    ProcessSingleTarget(caster, request, spell, allPlayers, currentTick, results, statusEffects);
                    break;

                case SpellTargetType.AoE:
                    ProcessAoE(caster, request, spell, allPlayers, results, statusEffects);
                    break;

                case SpellTargetType.MeleeSplash:
                    // Caster-centred AoE — client input position is irrelevant and ignored.
                    // The server uses the authoritative caster position as the blast origin.
                    ProcessMeleeSplash(caster, spell, allPlayers, results, statusEffects);
                    break;

                case SpellTargetType.Projectile:
                    // Projectile spells are fired via ShootRequestPacket and
                    // simulated by ProjectileSystem — they never enter the spell queue.
                    return;
            }

            // Cooldown is always consumed once the cast passes the alive + cooldown gate,
            // regardless of whether any target was in range. Prevents infinite spam into empty space.
            caster.SetCooldown(spell.SpellId, currentTick);
        }

        // ── Private Helpers ───────────────────────────────────────────────────

        private static void ProcessSingleTarget(
            PlayerSession                caster,
            SpellCastRequestPacket       request,
            SpellDefinition              spell,
            IReadOnlyList<PlayerSession> allPlayers,
            int                          currentTick,
            List<CombatEventPacket>      results,
            List<StatusEffectAppliedPacket>? statusEffects)
        {
            PlayerSession? target = FindById(allPlayers, request.TargetEntityId);
            if (target == null || !target.IsAlive)
                return;

            if (!MatchesFactionFilter(caster, target, spell.TargetFactionFilter))
                return;

            // Validate range against the target's historical position at the tick the client cast.
            Vec2 targetHistoricalPos = target.GetHistoricalPosition(
                request.TickNumber, currentTick, MaxRewindTicks);
            if (!CombatMath.IsInRange(caster.Position, targetHistoricalPos, spell.Range))
                return;

            ResolveHit(caster, target, spell, results, statusEffects);
        }

        private static void ProcessAoE(
            PlayerSession                caster,
            SpellCastRequestPacket       request,
            SpellDefinition              spell,
            IReadOnlyList<PlayerSession> allPlayers,
            List<CombatEventPacket>      results,
            List<StatusEffectAppliedPacket>? statusEffects)
        {
            var aoECenter = new Vec2(request.AoECenterX, request.AoECenterY);

            // The server validates that the AoE origin is within the caster's cast range.
            // A cheating client cannot move the center beyond this boundary.
            if (!CombatMath.IsInRange(caster.Position, aoECenter, spell.Range))
                return;

            // Each player is independently checked against the AoE circle.
            // The client cannot claim or deny any specific player being hit.
            for (int i = 0; i < allPlayers.Count; i++)
            {
                PlayerSession target = allPlayers[i];
                if (!target.IsAlive)
                    continue;

                if (!MatchesFactionFilter(caster, target, spell.TargetFactionFilter))
                    continue;

                if (CombatMath.IsInAoE(aoECenter, spell.AoERadius, target.Position))
                    ResolveHit(caster, target, spell, results, statusEffects);
            }
        }

        private static void ProcessMeleeSplash(
            PlayerSession                caster,
            SpellDefinition              spell,
            IReadOnlyList<PlayerSession> allPlayers,
            List<CombatEventPacket>      results,
            List<StatusEffectAppliedPacket>? statusEffects)
        {
            // The AoE origin is always the caster's authoritative server position.
            // AoERadius defines the cleave / whirlwind reach — no ground-target input needed.
            for (int i = 0; i < allPlayers.Count; i++)
            {
                PlayerSession target = allPlayers[i];
                if (!target.IsAlive)
                    continue;

                if (!MatchesFactionFilter(caster, target, spell.TargetFactionFilter))
                    continue;

                if (CombatMath.IsInAoE(caster.Position, spell.AoERadius, target.Position))
                    ResolveHit(caster, target, spell, results, statusEffects);
            }
        }

        private static void ResolveHit(
            PlayerSession           attacker,
            PlayerSession           target,
            SpellDefinition         spell,
            List<CombatEventPacket> results,
            List<StatusEffectAppliedPacket>? statusEffects)
        {
            int  damage = 0;
            bool isCrit = false;

            if (spell.BaseDamage > 0)
            {
                damage = CombatMath.CalculateDamage(spell.BaseDamage, attacker.AttackPower, target.Armor);
                isCrit = CombatMath.IsCriticalHit(Random.Shared.NextDouble(), attacker.CritChance);
                if (isCrit) damage *= 2;
            }

            if (damage > 0)
            {
                target.ApplyDamage(damage);
                ApplyLifeSteal(attacker, damage, spell.LifeStealPercent);
            }
            ApplySpellStatusEffect(attacker, target, spell, statusEffects);

            results.Add(new CombatEventPacket
            {
                AttackerId = attacker.EntityId,
                TargetId   = target.EntityId,
                Damage     = damage,
                IsCritical = isCrit,
            });
        }

        private static PlayerSession? FindById(IReadOnlyList<PlayerSession> players, int entityId)
        {
            for (int i = 0; i < players.Count; i++)
                if (players[i].EntityId == entityId) return players[i];
            return null;
        }

        private static void ApplyWeaponStatusEffect(
            PlayerSession attacker,
            PlayerSession target,
            List<StatusEffectAppliedPacket>? statusEffects)
        {
            if (attacker.MeleeStatusEffectId <= 0)
                return;

            if (attacker.MeleeStatusEffectChance <= 0f)
                return;

            if (Random.Shared.NextDouble() > attacker.MeleeStatusEffectChance)
                return;

            if (!target.TryApplyStatusEffect(
                    attacker.MeleeStatusEffectId,
                    attacker.EntityId,
                    attacker.MeleeStatusEffectDurationTicks,
                    attacker.MeleeStatusEffectStacks,
                    attacker.MeleeStatusEffectTickDamage,
                    attacker.MeleeStatusEffectTickIntervalTicks,
                    attacker.MeleeStatusEffectOwnerHealPercentPerTick,
                    attacker.MeleeStatusEffectVisibility,
                    out StatusEffectAppliedPacket packet))
            {
                return;
            }

            statusEffects?.Add(packet);
        }

        private static void ApplySpellStatusEffect(
            PlayerSession attacker,
            PlayerSession target,
            SpellDefinition spell,
            List<StatusEffectAppliedPacket>? statusEffects)
        {
            if (spell.StatusEffectId <= 0)
                return;

            if (spell.StatusEffectChance <= 0f)
                return;

            if (Random.Shared.NextDouble() > spell.StatusEffectChance)
                return;

            if (!target.TryApplyStatusEffect(
                    spell.StatusEffectId,
                    attacker.EntityId,
                    spell.StatusEffectDurationTicks,
                    spell.StatusEffectStacks,
                    spell.StatusEffectTickDamage,
                    spell.StatusEffectTickIntervalTicks,
                    spell.StatusEffectOwnerHealPercentPerTick,
                    spell.StatusEffectVisibility,
                    out StatusEffectAppliedPacket packet))
            {
                return;
            }

            statusEffects?.Add(packet);
        }

        private static void ApplyLifeSteal(PlayerSession attacker, int damage, float lifeStealPercent)
        {
            if (damage <= 0 || lifeStealPercent <= 0f)
                return;

            float heal = damage * lifeStealPercent;
            if (heal > 0f)
                attacker.RestoreHealth(heal);
        }

        private static bool MatchesFactionFilter(
            PlayerSession caster,
            PlayerSession target,
            TargetFactionFilter filter)
        {
            return filter switch
            {
                TargetFactionFilter.Any => true,
                TargetFactionFilter.AlliesOnly => target.Faction == caster.Faction,
                _ => target.Faction != caster.Faction,
            };
        }
    }
}
