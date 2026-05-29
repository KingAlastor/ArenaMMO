namespace SharedLibrary
{
    /// <summary>
    /// One ingredient line in a crafting recipe.
    /// </summary>
    public class CraftingIngredient
    {
        /// <summary>DefinitionId of the item archetype required as an ingredient.</summary>
        public int ItemDefinitionId { get; set; }
        /// <summary>How many of this ingredient are consumed per craft.</summary>
        public int Quantity         { get; set; }
    }

    /// <summary>
    /// Defines how one item archetype can be crafted or upgraded.
    ///
    /// The recipe catalog is stored in the database and loaded by the LobbyServer at startup.
    /// It is also shared with Unity so the crafting screen can display requirements and
    /// stat previews without a server round-trip.
    ///
    /// A recipe produces one ItemInstance whose CraftedStats are determined by the
    /// crafting inputs — the LobbyServer's CraftingService owns that computation.
    /// </summary>
    public class CraftingRecipe
    {
        /// <summary>Unique recipe identifier (matches the database primary key).</summary>
        public int                   RecipeId           { get; set; }
        /// <summary>Human-readable name displayed in the crafting UI.</summary>
        public string                Name               { get; set; } = string.Empty;
        /// <summary>DefinitionId of the item this recipe produces.</summary>
        public int                   OutputDefinitionId { get; set; }
        /// <summary>Ingredients consumed when the recipe is executed.</summary>
        public CraftingIngredient[]  Ingredients        { get; set; } = System.Array.Empty<CraftingIngredient>();
        /// <summary>
        /// Stat modifiers applied to the crafted ItemInstance (CraftedStats).
        /// Null means the output item uses the archetype's default stats — useful for
        /// simple "combine X to make Y" recipes that produce unmodified items.
        /// </summary>
        public ItemStatModifiers?    OutputStats        { get; set; }
    }
}
