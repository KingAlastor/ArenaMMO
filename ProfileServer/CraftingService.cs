using Dapper;
using Npgsql;
using SharedLibrary;

namespace ProfileServer;

/// <summary>
/// Executes crafting recipes for a player.
///
/// Called from the loadout/crafting screen — never from the matchmaking or game paths.
///
/// Responsibilities:
///   1. Validate the player owns the required ingredients in sufficient quantity.
///   2. Consume (delete) the ingredient item instances from the database in a
///      single transaction so partial states are impossible.
///   3. Insert the new crafted ItemInstance row with its CraftedStats JSON blob.
///   4. Return the new InstanceId so the client can update its local state.
///
/// Thread safety: each call opens its own connection; safe to call concurrently
/// for different accounts. Do not call this during a match — item state is cached
/// in Redis and PostgreSQL writes mid-match would be invisible to the running GameServer.
/// </summary>
internal sealed class CraftingService
{
    private readonly string _connectionString;

    // Loaded once at server startup; recipe catalog never changes at runtime.
    private readonly IReadOnlyDictionary<int, CraftingRecipe> _recipes;

    public CraftingService(string connectionString, IEnumerable<CraftingRecipe> recipes)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Postgres connection string is required.", nameof(connectionString));

        _connectionString = connectionString;
        _recipes = recipes.ToDictionary(r => r.RecipeId);
    }

    /// <summary>
    /// Attempts to execute a recipe for the given account.
    /// Returns the new item's InstanceId on success, or throws <see cref="CraftingException"/>
    /// when the recipe does not exist, the player lacks ingredients, or the transaction fails.
    /// </summary>
    public async Task<int> CraftAsync(int accountId, int recipeId)
    {
        if (!_recipes.TryGetValue(recipeId, out CraftingRecipe? recipe))
            throw new CraftingException($"Recipe {recipeId} does not exist.");

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        try
        {
            // ── 1. Verify ingredient ownership ───────────────────────────────────
            // For each required ingredient type, find instance IDs the player owns.
            // We take exactly the required quantity and consume them; any extras are kept.
            var toConsume = new List<int>();  // InstanceIds to delete

            foreach (CraftingIngredient ingredient in recipe.Ingredients)
            {
                List<int> owned = (await conn.QueryAsync<int>(
                    @"SELECT instance_id
                        FROM player_items
                       WHERE account_id         = @AccountId
                         AND definition_id      = @DefinitionId
                         AND crafted_stats_json IS NULL
                       ORDER BY instance_id
                       LIMIT @Quantity",
                    new { AccountId = accountId, DefinitionId = ingredient.ItemDefinitionId, ingredient.Quantity },
                    transaction: tx)).AsList();

                if (owned.Count < ingredient.Quantity)
                    throw new CraftingException(
                        $"Insufficient ingredients: need {ingredient.Quantity}× definition {ingredient.ItemDefinitionId}, " +
                        $"have {owned.Count}.");

                toConsume.AddRange(owned);
            }

            // ── 2. Consume ingredient instances ──────────────────────────────────
            await conn.ExecuteAsync(
                "DELETE FROM player_items WHERE instance_id = ANY(@Ids)",
                new { Ids = toConsume.ToArray() },
                transaction: tx);

            // ── 3. Insert the crafted output ──────────────────────────────────────
            // CraftedStats is serialized as JSON; null when the recipe produces a
            // plain (unmodified) copy of the archetype.
            string? craftedStatsJson = recipe.OutputStats == null
                ? null
                : System.Text.Json.JsonSerializer.Serialize(recipe.OutputStats);

            int newInstanceId = await conn.QuerySingleAsync<int>(
                @"INSERT INTO player_items (account_id, definition_id, crafted_stats_json)
                  VALUES (@AccountId, @DefinitionId, @CraftedStatsJson::jsonb)
                  RETURNING instance_id",
                new
                {
                    AccountId        = accountId,
                    DefinitionId     = recipe.OutputDefinitionId,
                    CraftedStatsJson = craftedStatsJson,
                },
                transaction: tx);

            await tx.CommitAsync();
            return newInstanceId;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// Returns every recipe available in the catalog.
    /// Used to send the full recipe list to the Unity client on crafting screen entry.
    /// </summary>
    public IEnumerable<CraftingRecipe> GetAllRecipes() => _recipes.Values;
}

/// <summary>Thrown by CraftingService for expected crafting failures (not system errors).</summary>
public sealed class CraftingException : Exception
{
    public CraftingException(string message) : base(message) { }
}
