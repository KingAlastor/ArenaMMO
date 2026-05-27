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
        /// </summary>
        public static void ProcessInput(PlayerSession player, PlayerInputPacket input, float deltaTime)
        {
            if (!player.IsAlive)
                return;

            if (!float.IsFinite(input.InputX) || !float.IsFinite(input.InputY))
                return;

            // Normalise so diagonal movement (|input| ≈ 1.41) cannot exceed base speed.
            // If magnitude <= 1 (cardinal or idle), use the raw values as-is.
            float magnitude = MathF.Sqrt(input.InputX * input.InputX + input.InputY * input.InputY);
            if (!float.IsFinite(magnitude) || magnitude <= 0f)
                return;

            float normX = magnitude > 1f ? input.InputX / magnitude : input.InputX;
            float normY = magnitude > 1f ? input.InputY / magnitude : input.InputY;

            player.Position = CombatMath.Move(player.Position, normX, normY, deltaTime);
        }
    }
}
