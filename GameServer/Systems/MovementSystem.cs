using SharedLibrary;
using System;

namespace GameServer.Systems
{
    /// <summary>
    /// Processes and validates player movement on the authoritative server.
    /// Executed once per tick per player who submitted a PlayerInputPacket.
    /// </summary>
    public static class MovementSystem
    {
        /// <summary>
        /// Validates the raw input vector, normalises it to prevent diagonal speed exploits,
        /// then writes the new authoritative position back to the player session.
        ///
        /// <paramref name="bounds"/> is passed from the zone descriptor so this method works
        /// for any map size.  The old code called CombatMath.Move without bounds, relying on a
        /// compile-time ArenaBoundsHalf constant — that would silently clamp players into a
        /// 100×100 box on any map larger than the default arena.
        /// </summary>
        public static void ProcessInput(PlayerSession player, in PlayerInputData input,
                                        float deltaTime, in WorldBounds bounds)
        {
            if (!player.IsAlive)
                return;

            // Dequantize sbyte (-127..127) to float (-1..1).
            // Both client and server apply exactly this formula, eliminating per-platform FP drift.
            float rawX = input.InputX / 127f;
            float rawY = input.InputY / 127f;

            float magSqr = rawX * rawX + rawY * rawY;
            if (magSqr <= 0f) return;

            // Normalise only when diagonal magnitude exceeds 1 to prevent diagonal speed exploits.
            if (magSqr > 1f)
            {
                float inv = 1f / MathF.Sqrt(magSqr);
                rawX *= inv;
                rawY *= inv;
            }

            player.Position = CombatMath.Move(player.Position, rawX, rawY, deltaTime, bounds);
            player.LastProcessedClientTick = input.TickNumber;
        }
    }
}
