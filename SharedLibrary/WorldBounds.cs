namespace SharedLibrary
{
    /// <summary>
    /// Describes the navigable rectangular extents of any zone or arena map.
    ///
    /// WHY THIS EXISTS:
    /// The old code used <c>const float ArenaBoundsHalf = 50f</c> baked directly into
    /// <see cref="CombatMath.Move"/>. That hardcodes a single 100×100 arena into every piece of
    /// movement math, making it impossible to reuse the same code for a 4000×2000 dungeon, an
    /// open-world continent, or any map whose size is determined at runtime from map data.
    ///
    /// By passing <see cref="WorldBounds"/> as a value type into movement math, each zone
    /// server simply constructs a descriptor at startup with its own extents.  The movement
    /// formula itself never changes, and no compile-time constant leaks into the math library.
    /// </summary>
    public readonly struct WorldBounds
    {
        public readonly float MinX;
        public readonly float MaxX;
        public readonly float MinY;
        public readonly float MaxY;

        public WorldBounds(float minX, float maxX, float minY, float maxY)
        {
            MinX = minX;
            MaxX = maxX;
            MinY = minY;
            MaxY = maxY;
        }

        /// <summary>
        /// Convenience constructor for centred symmetric maps.
        /// E.g. <c>new WorldBounds(50f, 50f)</c> → MinX=-50, MaxX=50, MinY=-50, MaxY=50.
        /// </summary>
        public WorldBounds(float halfExtentX, float halfExtentY)
            : this(-halfExtentX, halfExtentX, -halfExtentY, halfExtentY) { }

        /// <summary>Default arena bounds: ±50 units on both axes (100×100 world).</summary>
        public static readonly WorldBounds DefaultArena = new WorldBounds(50f, 50f);
    }
}
