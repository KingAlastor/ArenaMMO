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
        /// Calculates effective damage after armor mitigation.
        /// Formula: baseDamage × attackPower × (1 − armor / (armor + 100))
        /// Minimum result is always 1.
        /// </summary>
        public static int CalculateDamage(int baseDamage, float attackPower, float armor)
        {
            float mitigation = armor / (armor + 100f);
            float raw = baseDamage * attackPower * (1f - mitigation);
            return (int)Math.Max(1f, raw);
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
            => v < min ? min : v > max ? max : v;
    }
}
