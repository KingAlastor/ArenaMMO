using Microsoft.AspNetCore.Mvc;
using ProfileServer;
using SharedLibrary;

// ── Configuration ─────────────────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(args);

string pgConn = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required in appsettings.json.");

// TODO: Load CraftingRecipes from PostgreSQL at startup (currently empty list as placeholder).
// SELECT recipe_id, name, output_definition_id, ingredients_json, output_stats_json FROM crafting_recipes
IEnumerable<CraftingRecipe> recipes = Array.Empty<CraftingRecipe>();

builder.Services.AddSingleton(new CraftingService(pgConn, recipes));
builder.Services.AddSingleton(new CharacterService(pgConn));

// TODO: Add authentication middleware (e.g. JWT bearer) before going to production.
// All routes below should verify the caller is the account they claim to be.

var app = builder.Build();

// ── Character routes ──────────────────────────────────────────────────────────

// GET /characters/{accountId}
// Returns all characters for the given account.
app.MapGet("/characters/{accountId:int}", async (int accountId, CharacterService svc) =>
{
    var characters = await svc.GetCharactersAsync(accountId);
    return Results.Ok(characters);
});

// POST /characters
// Body: { accountId, name, classId }
// Creates a new character. Returns 201 with { characterId } on success.
app.MapPost("/characters", async ([FromBody] CreateCharacterRequest req, CharacterService svc) =>
{
    try
    {
        int id = await svc.CreateCharacterAsync(req.AccountId, req.Name, req.ClassId);
        return Results.Created($"/characters/{req.AccountId}", new { characterId = id });
    }
    catch (CharacterCreationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// DELETE /characters/{accountId}/{characterId}
// Deletes a character. 404 when not found or not owned by accountId.
app.MapDelete("/characters/{accountId:int}/{characterId:int}",
    async (int accountId, int characterId, CharacterService svc) =>
{
    bool deleted = await svc.DeleteCharacterAsync(accountId, characterId);
    return deleted ? Results.NoContent() : Results.NotFound();
});

// ── Crafting routes ───────────────────────────────────────────────────────────

// GET /crafting/recipes
// Returns the full recipe catalog so the Unity client can render the crafting screen
// without a round-trip per recipe.
app.MapGet("/crafting/recipes", (CraftingService svc) =>
    Results.Ok(svc.GetAllRecipes()));

// POST /crafting/craft
// Body: { accountId, recipeId }
// Executes the recipe atomically. Returns 200 with { instanceId } on success.
app.MapPost("/crafting/craft", async ([FromBody] CraftRequest req, CraftingService svc) =>
{
    try
    {
        int instanceId = await svc.CraftAsync(req.AccountId, req.RecipeId);
        return Results.Ok(new { instanceId });
    }
    catch (CraftingException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.Run();

// ── Request bodies ────────────────────────────────────────────────────────────

record CreateCharacterRequest(int AccountId, string Name, int ClassId);
record CraftRequest(int AccountId, int RecipeId);
