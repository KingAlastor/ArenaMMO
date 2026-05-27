using SharedLibrary;
using System;

namespace GameServer
{
    /// <summary>
    /// Stateless packet-level sanity checks for untrusted client payloads.
    ///
    /// These checks are intentionally cheap and conservative. They are not gameplay
    /// legality checks; those remain in simulation systems.
    /// </summary>
    internal static class InputSanitizer
    {
        // Allows slight analog overshoot while still rejecting clearly invalid ranges.
        private const float MaxRawInput = 2.0f;
        // Allows a small cushion around arena bounds for targeting packets.
        private const float MaxAbsWorldCoordinate = CombatMath.ArenaBoundsHalf + 5.0f;
        // Reject absurd direction vectors before server-side normalization.
        private const float MaxAbsDirectionComponent = 1000.0f;

        /// <summary>Validates movement packet shape and finite axis values.</summary>
        public static bool IsValid(PlayerInputPacket packet)
            => packet.TickNumber >= 0
               && float.IsFinite(packet.InputX)
               && float.IsFinite(packet.InputY)
               && MathF.Abs(packet.InputX) <= MaxRawInput
               && MathF.Abs(packet.InputY) <= MaxRawInput;

        /// <summary>Validates melee action packet shape and replay sequence field.</summary>
        public static bool IsValid(AttackRequestPacket packet)
            => packet.TickNumber >= 0
               && packet.ActionSequenceId > 0
               && packet.TargetEntityId > 0;

        /// <summary>Validates spell packet fields and finite AoE coordinates.</summary>
        public static bool IsValid(SpellCastRequestPacket packet)
            => packet.TickNumber >= 0
               && packet.ActionSequenceId > 0
               && packet.SpellId > 0
               && packet.TargetEntityId >= 0
               && float.IsFinite(packet.AoECenterX)
               && float.IsFinite(packet.AoECenterY)
               && MathF.Abs(packet.AoECenterX) <= MaxAbsWorldCoordinate
               && MathF.Abs(packet.AoECenterY) <= MaxAbsWorldCoordinate;

         /// <summary>Validates projectile fire packet fields and finite direction vector.</summary>
        public static bool IsValid(ShootRequestPacket packet)
            => packet.TickNumber >= 0
               && packet.ActionSequenceId > 0
               && packet.SpellId > 0
               && float.IsFinite(packet.DirectionX)
               && float.IsFinite(packet.DirectionY)
               && MathF.Abs(packet.DirectionX) <= MaxAbsDirectionComponent
               && MathF.Abs(packet.DirectionY) <= MaxAbsDirectionComponent;
    }
}
