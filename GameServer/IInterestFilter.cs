using SharedLibrary;

namespace GameServer
{
    /// <summary>
    /// Strategy interface for Network Interest Management (NIM) — deciding which connected
    /// peers receive a particular game-event packet based on its world-space origin.
    ///
    /// ─────────────────────────────────────────────────────────────────────────────────────
    /// WHY THIS EXISTS
    /// ─────────────────────────────────────────────────────────────────────────────────────
    /// In a small 10-player arena every combat event can be broadcast to every peer without
    /// measurable cost.  In an open-world zone with 200+ players, a single melee swing should
    /// NOT generate a packet send to a player standing 800 units away on the other side of the
    /// map.  Without spatial culling the per-event send count is O(N) per player, making the
    /// total per-tick cost O(N²) across all players — a classic MMO scalability cliff.
    ///
    /// ─────────────────────────────────────────────────────────────────────────────────────
    /// HOW TO USE
    /// ─────────────────────────────────────────────────────────────────────────────────────
    /// • Arena zones:      set <see cref="ZoneDescriptor.EventFilter"/> to
    ///                     <see cref="BroadcastFilter.Instance"/>.
    ///                     Zero behaviour change from the old unconditional SendToAll approach.
    ///
    /// • Large open-world: set EventFilter to a <see cref="RadiusFilter"/> whose radius matches
    ///                     the zone's expected visible distance (e.g. 80 units in a dense city,
    ///                     200 units in open terrain).
    ///
    /// • Custom rules:     implement this interface and drop it into <see cref="ZoneDescriptor"/>.
    ///                     No changes to <see cref="ArenaInstance"/> or any system code are needed.
    ///
    /// ─────────────────────────────────────────────────────────────────────────────────────
    /// UPGRADING TO A SPATIAL HASH OR QUAD-TREE LATER
    /// ─────────────────────────────────────────────────────────────────────────────────────
    /// When player counts grow further, replace <see cref="RadiusFilter"/> with a spatial-hash
    /// or quad-tree backed implementation.  The interface contract is stable — only the
    /// implementing class needs to change; all call sites in <see cref="ArenaInstance"/> and
    /// <see cref="NetworkManager"/> remain untouched.
    /// </summary>
    public interface IInterestFilter
    {
        /// <summary>
        /// Returns <c>true</c> when <paramref name="viewer"/> should receive a packet whose
        /// world-space event origin is at <paramref name="eventOrigin"/>.
        /// </summary>
        bool ShouldReceive(PlayerSession viewer, Vec2 eventOrigin);
    }

    /// <summary>
    /// Arena default: every connected peer receives every event packet regardless of distance.
    /// Equivalent to the old unconditional <c>SendToAll</c> pattern but expressed through
    /// the <see cref="IInterestFilter"/> interface so arena and MMO zones share the same
    /// broadcast call path in <see cref="NetworkManager.SendToInterested{T}"/>.
    /// </summary>
    public sealed class BroadcastFilter : IInterestFilter
    {
        /// <summary>Singleton — stateless and allocation-free.</summary>
        public static readonly BroadcastFilter Instance = new BroadcastFilter();
        private BroadcastFilter() { }

        /// <inheritdoc/>
        public bool ShouldReceive(PlayerSession viewer, Vec2 eventOrigin) => true;
    }

    /// <summary>
    /// Spatial culling filter: only peers whose current position is within
    /// <c>radius</c> world units of the event origin receive the packet.
    ///
    /// Distance comparison uses squared magnitude (no sqrt) so the check is branch-free
    /// and numerically cheap inside the per-peer broadcast loop.
    /// </summary>
    public sealed class RadiusFilter : IInterestFilter
    {
        private readonly float _radiusSqr;

        /// <param name="radius">
        /// Culling radius in world-space units (same coordinate system as <see cref="Vec2"/>).
        /// </param>
        public RadiusFilter(float radius) => _radiusSqr = radius * radius;

        /// <inheritdoc/>
        public bool ShouldReceive(PlayerSession viewer, Vec2 eventOrigin)
            => CombatMath.DistanceSqr(viewer.Position, eventOrigin) <= _radiusSqr;
    }
}
