using SharedLibrary;

namespace GameServer
{
    /// <summary>
    /// Strategy interface for zone-specific win conditions.
    ///
    /// WHY THIS IS SEPARATE:
    /// The two-faction design is permanent; this interface is not about abstracting factions.
    /// It exists because the word "win" only applies to Arena matches — open-world MMO zones
    /// run indefinitely.  Keeping win-condition logic out of the simulation loop means:
    ///
    ///   • Arena maps plug in <see cref="EliminationWinCondition"/> to shut down the instance
    ///     when one faction is fully eliminated.
    ///   • MMO zones plug in <see cref="NoWinCondition"/> so the same simulation loop code
    ///     runs forever without any if/else guard.
    ///   • Future Arena modes (first-to-X-kills, timed, capture-flag) each get their own
    ///     implementation without any changes to <see cref="ArenaInstance"/>.
    /// </summary>
    public interface IWinCondition
    {
        /// <summary>
        /// Evaluated once per tick after all combat resolution completes.
        /// Returns the winning <see cref="FactionId"/> when the condition is satisfied,
        /// or <c>null</c> to continue the simulation for at least one more tick.
        /// </summary>
        FactionId? Evaluate(System.Collections.Generic.IReadOnlyList<PlayerSession> players, int currentTick);
    }

    /// <summary>
    /// Arena deathmatch: the match ends when every living player belongs to the same faction,
    /// meaning the opposing faction has been fully eliminated.
    ///
    /// The two-faction assumption is intentional and will not change.  The interface exists
    /// purely so MMO zone servers can opt out of win-condition evaluation with
    /// <see cref="NoWinCondition"/> rather than special-casing in the loop body.
    /// </summary>
    public sealed class EliminationWinCondition : IWinCondition
    {
        public FactionId? Evaluate(
            System.Collections.Generic.IReadOnlyList<PlayerSession> players,
            int currentTick)
        {
            bool alphaAlive = false;
            bool betaAlive  = false;
            bool anyDead    = false;

            for (int i = 0; i < players.Count; i++)
            {
                PlayerSession p = players[i];
                if (!p.IsAlive) { anyDead = true; continue; }
                if (p.Faction == FactionId.Alpha) alphaAlive = true;
                else                              betaAlive  = true;
            }

            // Wait until at least one player has died before declaring a winner.
            // This prevents the condition firing on an empty or freshly-spawned match.
            if (!anyDead) return null;

            if ( alphaAlive && !betaAlive) return FactionId.Alpha;
            if (!alphaAlive &&  betaAlive) return FactionId.Beta;
            if (!alphaAlive && !betaAlive) return FactionId.Beta; // mutual elimination → Beta by convention

            return null;
        }
    }

    /// <summary>
    /// Open-world MMO zones never "end".  Players leave via zone-transfer handoffs, not a
    /// match-end event.  This implementation always returns null so the simulation loop in
    /// <see cref="ArenaInstance"/> simply skips the EndMatch call every tick.
    /// </summary>
    public sealed class NoWinCondition : IWinCondition
    {
        /// <summary>Singleton — stateless and allocation-free.</summary>
        public static readonly NoWinCondition Instance = new NoWinCondition();
        private NoWinCondition() { }
        public FactionId? Evaluate(
            System.Collections.Generic.IReadOnlyList<PlayerSession> players,
            int currentTick) => null;
    }
}
