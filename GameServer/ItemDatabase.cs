using SharedLibrary;
using System.Collections.Generic;

namespace GameServer
{
    /// <summary>
    /// Immutable lookup table of every item definition, populated once at server startup.
    ///
    /// Production path: hydrate from PostgreSQL via Dapper at startup (same pattern as SpellDatabase).
    /// During a match the game loop reads this dictionary — no DB round-trips.
    /// </summary>
    public static class ItemDatabase
    {
        private static readonly Dictionary<int, ItemDefinition> _items =
            new Dictionary<int, ItemDefinition>
            {
                [1001] = new ItemDefinition
                {
                    ItemId = 1001,
                    Name   = "Iron Sword",
                    Slot   = EquipSlot.Weapon,
                    Stats  = new ItemStatModifiers { AttackPower = 0.25f },
                },
                [1002] = new ItemDefinition
                {
                    ItemId = 1002,
                    Name   = "Steel Sword",
                    Slot   = EquipSlot.Weapon,
                    Stats  = new ItemStatModifiers { AttackPower = 0.50f, CritChance = 0.03f },
                },
                [1003] = new ItemDefinition
                {
                    ItemId = 1003,
                    Name   = "Wooden Shield",
                    Slot   = EquipSlot.Offhand,
                    Stats  = new ItemStatModifiers { PhysicalAbsorbPercent = 0.05f },
                },
                [1004] = new ItemDefinition
                {
                    ItemId = 1004,
                    Name   = "Iron Shield",
                    Slot   = EquipSlot.Offhand,
                    Stats  = new ItemStatModifiers { PhysicalAbsorbPercent = 0.10f, MagicAbsorbPercent = 0.03f },
                },
                [1005] = new ItemDefinition
                {
                    ItemId = 1005,
                    Name   = "Leather Helm",
                    Slot   = EquipSlot.Helm,
                    Stats  = new ItemStatModifiers { MaxHealth = 15f },
                },
                [1006] = new ItemDefinition
                {
                    ItemId = 1006,
                    Name   = "Chainmail Chest",
                    Slot   = EquipSlot.Chest,
                    Stats  = new ItemStatModifiers { MaxHealth = 25f, PhysicalResistPercent = 0.05f },
                },
                [1007] = new ItemDefinition
                {
                    ItemId = 1007,
                    Name   = "Vampiric Ring",
                    Slot   = EquipSlot.Trinket,
                    Stats  = new ItemStatModifiers { MeleeLifeStealPercent = 0.08f, AttackPower = 0.10f },
                },
            };

        /// <summary>Looks up an item definition by ID. Returns false when the ID is not registered.</summary>
        public static bool TryGet(int itemId, out ItemDefinition definition)
            => _items.TryGetValue(itemId, out definition!);
    }
}
