using SharedLibrary;
using System;
using System.Collections.Generic;

namespace GameServer
{
    // ─────────────────────────────────────────────────────────────────────────
    // SpatialGrid — fixed-cell 2-D spatial hash for interest management
    // ─────────────────────────────────────────────────────────────────────────
    //
    // WHY THIS EXISTS:
    //
    //   BroadcastState and SendToInterested both iterate ALL players in the
    //   zone to find who is near an event origin.  For a 10-player arena that
    //   is fine.  For a 2 000-player MMORPG zone it is O(N²) per tick:
    //
    //     2 000 players × 2 000 entities × 30 Hz = 120 000 000 distance checks/s
    //
    //   A fixed-cell spatial hash reduces the per-event query to:
    //     • O(1) to find which cell the event origin falls in
    //     • O(k) to iterate only the players in the (3×3) neighbour cells
    //       where k ≪ N in any reasonably large zone.
    //
    // DESIGN:
    //
    //   The world is divided into a uniform grid of (CellSize × CellSize) cells.
    //   A player is in exactly one cell — the one containing their position.
    //   Each cell holds a pre-allocated List<PlayerSession> to avoid per-frame
    //   allocations.  Lists are populated by RebuildEachTick() called once per
    //   tick before BroadcastState/SendToInterested.
    //
    //   CellSize should be ≥ the maximum view/interest radius so that all
    //   visible entities are guaranteed to be in the 3×3 neighbourhood of any
    //   cell.  With CellSize == ViewRadius the 3×3 neighbourhood contains all
    //   players within 3×ViewRadius, which is a safe over-approximation — the
    //   caller still performs the exact distance check.
    //
    // THREAD SAFETY:
    //   All methods must be called from the single game-loop thread.
    //   No locks are used; concurrent access will corrupt state.
    //
    // MEMORY:
    //   gridW × gridH List<PlayerSession> instances, allocated once at construction.
    //   Each List is pre-allocated with an initial capacity equal to
    //   (maxPlayers / totalCells + 4) to avoid internal resizes under normal load.
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class SpatialGrid
    {
        private readonly float _cellSize;
        private readonly float _invCellSize;   // 1/cellSize — avoids division in hot path
        private readonly int   _gridW;
        private readonly int   _gridH;
        private readonly float _worldMinX;
        private readonly float _worldMinY;

        // Flat array of cell lists.  Index = cellY * _gridW + cellX.
        private readonly List<PlayerSession>[] _cells;

        // ── Neighbour search scratch buffer ───────────────────────────────────
        // Reused across every QueryNeighbours call to avoid per-call allocation.
        private readonly List<PlayerSession> _neighbourScratch;

        /// <summary>
        /// Constructs a spatial grid covering the given world bounds.
        /// </summary>
        /// <param name="worldMinX">Left edge of the world in world units.</param>
        /// <param name="worldMinY">Bottom edge of the world in world units.</param>
        /// <param name="worldMaxX">Right edge of the world in world units.</param>
        /// <param name="worldMaxY">Top edge of the world in world units.</param>
        /// <param name="cellSize">
        /// Size of one grid cell in world units.  Should be ≥ the interest radius
        /// so a 3×3 neighbourhood covers all potentially relevant entities.
        /// </param>
        /// <param name="maxPlayers">
        /// Upper bound on player count — used to pre-size cell lists.
        /// </param>
        public SpatialGrid(
            float worldMinX, float worldMinY,
            float worldMaxX, float worldMaxY,
            float cellSize,
            int   maxPlayers = 2048)
        {
            _cellSize    = cellSize;
            _invCellSize = 1f / cellSize;
            _worldMinX   = worldMinX;
            _worldMinY   = worldMinY;

            _gridW = (int)MathF.Ceiling((worldMaxX - worldMinX) / cellSize);
            _gridH = (int)MathF.Ceiling((worldMaxY - worldMinY) / cellSize);

            int totalCells   = _gridW * _gridH;
            int cellCapacity = Math.Max(4, maxPlayers / Math.Max(1, totalCells) + 4);

            _cells = new List<PlayerSession>[totalCells];
            for (int i = 0; i < totalCells; i++)
                _cells[i] = new List<PlayerSession>(cellCapacity);

            // Scratch buffer sized for the maximum players that can fit in a 3×3 neighbourhood.
            _neighbourScratch = new List<PlayerSession>(Math.Min(maxPlayers, cellCapacity * 9));
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Clears all cells and re-inserts every player into their current cell.
        /// Must be called once per tick AFTER movement resolves and BEFORE any
        /// BroadcastState or SendToInterested calls.
        /// </summary>
        public void RebuildEachTick(IReadOnlyList<PlayerSession> players)
        {
            // Clear all cells — O(totalCells), not O(players)
            for (int i = 0; i < _cells.Length; i++)
                _cells[i].Clear();

            for (int i = 0; i < players.Count; i++)
            {
                PlayerSession p = players[i];
                int idx = CellIndex(p.Position.X, p.Position.Y);
                if (idx >= 0)
                    _cells[idx].Add(p);
            }
        }

        /// <summary>
        /// Returns a scratch list of all players whose grid cell falls within the
        /// 3×3 cell neighbourhood of <paramref name="origin"/>.
        ///
        /// ⚠️  The returned list is the internal scratch buffer and is valid only
        ///     until the next call to <see cref="QueryNeighbours"/>.  Callers must
        ///     iterate and discard — never store the reference.
        /// </summary>
        public List<PlayerSession> QueryNeighbours(Vec2 origin)
        {
            _neighbourScratch.Clear();

            int cx = WorldToCell(origin.X - _worldMinX);
            int cy = WorldToCell(origin.Y - _worldMinY);

            // 3×3 neighbourhood — clamp to grid bounds
            int x0 = Math.Max(0, cx - 1);
            int x1 = Math.Min(_gridW - 1, cx + 1);
            int y0 = Math.Max(0, cy - 1);
            int y1 = Math.Min(_gridH - 1, cy + 1);

            for (int y = y0; y <= y1; y++)
            {
                int rowBase = y * _gridW;
                for (int x = x0; x <= x1; x++)
                {
                    List<PlayerSession> cell = _cells[rowBase + x];
                    for (int k = 0; k < cell.Count; k++)
                        _neighbourScratch.Add(cell[k]);
                }
            }

            return _neighbourScratch;
        }

        // ── Private Helpers ───────────────────────────────────────────────────

        /// <summary>Converts a world-relative axis value to a grid cell index (1D).</summary>
        private int WorldToCell(float worldRelative)
            => (int)(worldRelative * _invCellSize);

        /// <summary>
        /// Returns the flat cell array index for world position (wx, wy),
        /// or -1 if the position is outside the grid bounds.
        /// </summary>
        private int CellIndex(float wx, float wy)
        {
            int cx = WorldToCell(wx - _worldMinX);
            int cy = WorldToCell(wy - _worldMinY);
            if ((uint)cx >= (uint)_gridW || (uint)cy >= (uint)_gridH)
                return -1;
            return cy * _gridW + cx;
        }
    }
}
