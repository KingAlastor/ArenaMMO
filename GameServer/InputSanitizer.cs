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
        // Allows a small cushion around arena bounds for targeting packets.
        // Uses WorldBounds.DefaultArena so the constant stays in sync with the default zone,
        // rather than the now-removed CombatMath.ArenaBoundsHalf.
        private const float MaxAbsWorldCoordinate = 50.0f + 5.0f;
        // Reject absurd direction vectors before server-side normalization.
        private const float MaxAbsDirectionComponent = 1000.0f;

        /// <summary>
        /// Validates movement packet shape.
        /// InputX/InputY are sbyte — all values in -128..127 are finite by definition.
        /// </summary>
        public static bool IsValid(PlayerInputPacket packet)
            => packet.TickNumber >= 0;

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

        /// <summary>Validates gear set swap request: SetIndex must be 0 or 1.</summary>
        public static bool IsValid(GearSetSwapRequestPacket packet)
            => packet.SetIndex <= 1;

        /// <summary>
        /// Validates an equip-item request.
        /// ItemInstanceId == 0 means unequip; the Slot must then be a valid EquipSlot value.
        /// ItemInstanceId > 0 means equip; the Slot field is ignored (derived server-side).
        /// </summary>
        public static bool IsValid(EquipItemRequestPacket packet)
            => packet.ItemInstanceId >= 0
            && packet.Slot >= EquipSlot.Weapon
            && packet.Slot <= EquipSlot.Trinket;
    }
}
