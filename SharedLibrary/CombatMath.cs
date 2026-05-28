using System;

namespace SharedLibrary
{
    /// <summary>
    /// Pure, stateless math shared between the server and the Unity client.
    /// No allocations — all methods operate on primitives or structs.
    /// </summary>
    public static class CombatMath
    {
        public const float DefaultMoveSpeed = 5.0f;
        public const float MeleeRange       = 1.5f;
        public const float ArenaBoundsHalf  = 50.0f;   // world extends ±50 units

        // ── Movement ──────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the new authoritative position after applying a normalised input vector.
        /// Arena bounds are enforced here on the server to prevent out-of-bounds exploits.
        /// </summary>
        public static Vec2 Move(Vec2 current, float inputX, float inputY, float deltaTime,
                                float speed = DefaultMoveSpeed)
        {
            if (!float.IsFinite(current.X) || !float.IsFinite(current.Y))
                current = Vec2.Zero;

            if (!float.IsFinite(inputX) || !float.IsFinite(inputY))
                return current;

            if (!float.IsFinite(deltaTime) || deltaTime <= 0f)
                return current;

            if (!float.IsFinite(speed) || speed <= 0f)
                speed = DefaultMoveSpeed;

            float newX = Clamp(current.X + inputX * speed * deltaTime, -ArenaBoundsHalf, ArenaBoundsHalf);
            float newY = Clamp(current.Y + inputY * speed * deltaTime, -ArenaBoundsHalf, ArenaBoundsHalf);
            return new Vec2(newX, newY);
        }

        // ── Range & AoE ───────────────────────────────────────────────────────

        /// <summary>
        /// Returns the squared distance between two positions.
        /// Use this instead of Distance() wherever possible — no sqrt needed.
        /// </summary>
        public static float DistanceSqr(Vec2 a, Vec2 b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        /// <summary>Returns true if <paramref name="target"/> is within <paramref name="range"/> of <paramref name="origin"/>.</summary>
        public static bool IsInRange(Vec2 origin, Vec2 target, float range)
            => DistanceSqr(origin, target) <= range * range;

        /// <summary>
        /// Returns true if <paramref name="entityPos"/> lies within the AoE circle.
        /// Used by the server to independently verify every potentially-hit player.
        /// </summary>
        public static bool IsInAoE(Vec2 center, float radius, Vec2 entityPos)
            => DistanceSqr(center, entityPos) <= radius * radius;

        // ── Damage Formulas ───────────────────────────────────────────────────

        /// <summary>
        /// Calculates final damage after absorb and (optionally) resist mitigation.
        ///
        /// Pipeline for Physical and Magic damage:
        ///   1. raw          = baseDamage × attackPower
        ///   2. afterAbsorb  = raw × (1 − absorbPercent)           — always applied; cannot be bypassed
        ///   3. pierce check: if pierceRoll &lt; pierceChance → resistance step is skipped
        ///   4. afterResist  = afterAbsorb × (1 − resistPercent)   — skipped when pierced
        ///   5. result       = max(1, afterResist)
        ///
        /// True damage ignores both absorb and resist: result = max(1, baseDamage × attackPower).
        ///
        /// <paramref name="pierceRoll"/> must be a pre-generated value in [0, 1) from the call site
        /// so that CombatMath remains stateless and independently testable.
        /// </summary>
        public static int CalculateDamage(
            int        baseDamage,
            float      attackPower,
            DamageType damageType,
            float      absorbPercent,
            float      resistPercent,
            float      pierceChance,
            double     pierceRoll)
        {
            if (!float.IsFinite(attackPower) || attackPower <= 0f)
                attackPower = 1f;

            float raw = baseDamage * attackPower;

            if (damageType == DamageType.True)
                return (int)Math.Max(1f, raw);

            float afterAbsorb = raw * (1f - Clamp(absorbPercent, 0f, 1f));

            bool pierced = pierceChance > 0f && pierceRoll < pierceChance;
            if (!pierced)
                afterAbsorb *= (1f - Clamp(resistPercent, 0f, 1f));

            return (int)Math.Max(1f, afterAbsorb);
        }

        /// <summary>
        /// Pure critical-hit check. The caller provides a pre-generated [0, 1) random value
        /// so SharedLibrary remains free of any RNG state.
        /// </summary>
        public static bool IsCriticalHit(double randomRoll, float critChance)
            => randomRoll < critChance;
        /// <summary>
        /// Computes the effective hit probability for a projectile at its current travel distance.
        /// Hit chance decreases linearly as the projectile travels further from its origin.
        /// The result is clamped to <paramref name="minHitChance"/> so a shot can never be
        /// guaranteed to miss — useful for UI crosshair feedback in Unity as well as
        /// server-side hit rolls.
        /// Formula: clamp(baseHitChance − falloffPerUnit × distanceTraveled, min, 1)
        /// </summary>
        public static float ProjectileHitChance(
            float baseHitChance,
            float falloffPerUnit,
            float distanceTraveled,
            float minHitChance = 0.05f)
        {
            float chance = baseHitChance - falloffPerUnit * distanceTraveled;
            return chance < minHitChance ? minHitChance : chance;
        }
        // ── Helpers ───────────────────────────────────────────────────────────

        private static float Clamp(float v, float min, float max)
            => !float.IsFinite(v) ? 0f : v < min ? min : v > max ? max : v;
    }
}
