using SharedLibrary;

namespace GameServer.Systems
{
    /// <summary>
    /// Shared damage-encoding helpers used by CombatSystem, ProjectileSystem, and PlayerSession.
    /// </summary>
    internal static class DamageUtils
    {
        /// <summary>
        /// Clamps <paramref name="rawDamage"/> to <see cref="CombatMath.MaxSingleHitDamage"/>
        /// and narrows to <c>ushort</c> for wire serialisation.
        ///
        /// If the raw value exceeds the cap, <see cref="SecurityTelemetry.RecordDamageCap"/>
        /// is called so the bug surfaces in telemetry and the periodic snapshot counter.
        /// Normal gameplay should never reach the cap; seeing it fire means a damage formula
        /// has a runaway multiplier or stat overflow that must be investigated.
        /// </summary>
        /// <param name="rawDamage">Authoritative computed damage before wire compression.</param>
        /// <param name="attackerId">Entity ID of the attacker, logged with the cap event.</param>
        /// <param name="context">
        /// Short label — pass a string literal ("melee", "spell", "aoe", "projectile", "splash", "dot").
        /// Declared as <see cref="System.ReadOnlySpan{T}"/> so the literal is stack-resident at the
        /// call site; no managed string is allocated or promoted to the heap on the hot path.
        /// <see cref="SecurityTelemetry.RecordDamageCap"/> receives the span and is responsible
        /// for materialising a string only if it chooses to log or store the label.
        /// </param>
        public static ushort ClampAndEncode(int rawDamage, int attackerId, System.ReadOnlySpan<char> context)
        {
            if (rawDamage > CombatMath.MaxSingleHitDamage)
                SecurityTelemetry.RecordDamageCap(attackerId, context, rawDamage);

            return (ushort)System.Math.Clamp(rawDamage, 0, CombatMath.MaxSingleHitDamage);
        }
    }
}
