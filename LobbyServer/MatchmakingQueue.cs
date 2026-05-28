using LiteNetLib;
using SharedLibrary;
using System.Collections.Generic;

namespace LobbyServer
{
    /// <summary>
    /// Thread-safe FIFO matchmaking queue.
    /// Once enough players have queued, TryFormMatch splits them evenly across
    /// Alpha and Beta factions (first half → Alpha, second half → Beta).
    /// </summary>
    internal sealed class MatchmakingQueue
    {
        private readonly int    _matchSize;   // total players required (must be even)
        private readonly object _lock = new();
        private readonly List<QueuedPlayer> _queue = new();

        /// <param name="matchSize">Total players required for one match. Must be a positive even number.</param>
        public MatchmakingQueue(int matchSize)
        {
            _matchSize = matchSize;
        }

        /// <summary>
        /// Adds a player to the back of the queue.
        /// If the player was already queued (e.g. reconnect), their entry is replaced.
        /// </summary>
        public void Enqueue(QueuedPlayer player)
        {
            lock (_lock)
            {
                _queue.RemoveAll(p => p.PlayerId == player.PlayerId);
                _queue.Add(player);
            }
        }

        /// <summary>Removes a player from the queue (e.g. on disconnect).</summary>
        public void Remove(int playerId)
        {
            lock (_lock)
            {
                _queue.RemoveAll(p => p.PlayerId == playerId);
            }
        }

        /// <summary>
        /// Returns the 1-based queue position, total players currently queued,
        /// and how many are needed to form a match.
        /// Position is -1 when the player is not in the queue.
        /// </summary>
        public (int Position, int Total, int Needed) GetStatus(int playerId)
        {
            lock (_lock)
            {
                int idx = _queue.FindIndex(p => p.PlayerId == playerId);
                return (idx >= 0 ? idx + 1 : -1, _queue.Count, _matchSize);
            }
        }

        /// <summary>
        /// Attempts to dequeue exactly <c>MatchSize</c> players and assign them factions.
        /// Returns null when not enough players are queued yet.
        /// </summary>
        public MatchGroup? TryFormMatch()
        {
            lock (_lock)
            {
                if (_queue.Count < _matchSize)
                    return null;

                int half    = _matchSize / 2;
                var players = new List<QueuedPlayer>(_matchSize);

                for (int i = 0; i < _matchSize; i++)
                {
                    FactionId faction = i < half ? FactionId.Alpha : FactionId.Beta;
                    players.Add(_queue[i] with { Faction = faction });
                }

                _queue.RemoveRange(0, _matchSize);
                return new MatchGroup(players);
            }
        }
    }

    /// <summary>
    /// Immutable snapshot of a queued player.
    /// Faction is set to Alpha by default and updated by TryFormMatch.
    /// </summary>
    internal sealed record QueuedPlayer(
        int      PlayerId,
        string   PlayerName,
        string   AllowedSpellIdsCsv,
        NetPeer  Peer)
    {
        public FactionId Faction { get; init; } = FactionId.Alpha;
    }

    /// <summary>A set of players assigned to one match, with factions already decided.</summary>
    internal sealed record MatchGroup(List<QueuedPlayer> Players);
}
