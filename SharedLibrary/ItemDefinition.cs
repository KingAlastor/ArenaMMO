namespace SharedLibrary
{
    /// <summary>
    /// Equipment slots available on a player character.
    /// Used as the key when mapping equipped items on the server and in the Unity character sheet.
    /// </summary>
    public enum EquipSlot : byte
    {
        Weapon  = 0,
        Offhand = 1,
        Helm    = 2,
        Chest   = 3,
        Legs    = 4,
        Boots   = 5,
        Trinket = 6,
    }

    /// <summary>
    /// Additive stat bonuses applied to a PlayerSession when this item is equipped.
    /// All values default to zero (no contribution).
    /// Stats are summed across all equipped items in RecomputeStats() each time loadout changes.
    /// </summary>
    public class ItemStatModifiers
    {
        public float MaxHealth             { get; set; } = 0f;
        public float AttackPower           { get; set; } = 0f;
        public float PhysicalAbsorbPercent { get; set; } = 0f;
        public float PhysicalResistPercent { get; set; } = 0f;
        public float MagicAbsorbPercent    { get; set; } = 0f;
        public float MagicResistPercent    { get; set; } = 0f;
        public float CritChance            { get; set; } = 0f;
        public float MeleeLifeStealPercent { get; set; } = 0f;
        /// <summary>Added on top of the base ProjectileRangeMultiplier (1.0). E.g. 0.2 → 1.2× range.</summary>
        public float ProjectileRangeBonus  { get; set; } = 0f;
        /// <summary>Additional pierce charges stacked on top of the spell's BasePierceCount.</summary>
        public int   ProjectilePierceBonus { get; set; } = 0;
    }

    /// <summary>
    /// Immutable definition of an equippable item.
    /// Loaded once at server startup from ItemDatabase; never mutated during a match.
    /// Shared with Unity for tooltips, character sheet display, and item comparison UI.
    /// </summary>
    public class ItemDefinition
    {
        public int               ItemId { get; set; }
        public string            Name   { get; set; } = string.Empty;
        public EquipSlot         Slot   { get; set; }
        public ItemStatModifiers Stats  { get; set; } = new ItemStatModifiers();
    }
}
