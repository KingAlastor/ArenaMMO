---
name: profileserver-invariants
description: "Use when editing ArenaMMO ProfileServer character management, crafting execution, or HTTP endpoints; preserves account ownership enforcement, transactional item integrity, and the separation boundary between pre-match player management and the LobbyServer/GameServer runtime."
---

# ProfileServer Invariants

## Purpose
This skill defines the invariants Copilot must preserve when editing the ProfileServer project — the ASP.NET Core HTTP service that handles pre-match player management (character creation, crafting) outside of the UDP game and lobby servers.

## Core Identity
- ProfileServer is an **HTTP REST service** (ASP.NET Core minimal API), not a UDP game server.
- It is the only system authorized to create, delete, or modify characters and crafted items in PostgreSQL.
- It runs completely outside the game runtime path. It has no knowledge of active matches, ticket issuance, or Redis player profiles during play.
- The LobbyServer reads characters and items from PostgreSQL when building a `PlayerProfile` for Redis. ProfileServer never writes to Redis during normal operation.
- The GameServer has no connection to ProfileServer whatsoever. It only sees the finished `ItemInstance` data that the LobbyServer already serialized into Redis.
- **Exception — crafting rewards:** At Arena match end, the GameServer writes `crafting-reward:{accountId}` to Redis. ProfileServer must read and claim this key on the player's next login/lobby entry to credit the earned ingredients into PostgreSQL before deleting the key. This is the only data flow from GameServer that ProfileServer must process.

---

## Separation of Concerns Boundary

```
[Unity Client]
      │  HTTP (REST)             UDP (LiteNetLib)        UDP (LiteNetLib)
      ├──────────────►  ProfileServer  ──PostgreSQL──►  LobbyServer  ──Redis──►  GameServer
      │  Character/Crafting UI        (reads char + items)            (reads profile)
      └──────────────────────────────────────────────────────────────────────────────────────
```

- **ProfileServer** = character creation, character deletion, character listing, crafting. Pure CRUD against PostgreSQL.
- **LobbyServer** = authentication, matchmaking, ticket issuance, writing `PlayerProfile` to Redis.
- **GameServer** = runtime simulation only. Never calls ProfileServer or writes characters/items.

### Rules enforced by this boundary
- Do not add matchmaking, ticket issuance, or Redis access to ProfileServer (except the crafting-reward pickup flow — see below).
- Do not add character creation or crafting to LobbyServer or GameServer.
- Do not add UDP networking (LiteNetLib) to ProfileServer — HTTP only.
- ProfileServer must never write to Redis. It writes only to PostgreSQL and reads one Redis key (`crafting-reward:{accountId}`) as part of the post-match reward flow.

### Arena Crafting Reward Pickup (GameServer → Redis → ProfileServer)
- At Arena match end, `MatchDataService.SaveMatchResultAsync` writes `crafting-reward:{accountId}` to Redis (24-hour TTL). The value is a JSON array of `{ IngredientId, Quantity }` objects.
- ProfileServer must read this key on the player's next session (login or lobby entry) and, inside a transaction, INSERT the ingredient rows into `player_items` and then DELETE the Redis key.
- If the Redis key is absent, no-op — the player earned no rewards or they were already claimed.
- The Redis DELETE must happen **after** the PostgreSQL commit, not before. If the DB commit fails, the key remains for retry.
- Do not have ProfileServer poll for this key continuously. It should only be checked at a defined player session entry point.
- The `crafting-reward:{accountId}` key is exclusive to this flow. Do not write to it from ProfileServer; GameServer is the sole writer.

---

## Authentication Requirement (TODO — must be completed before production)
- **All routes are currently unauthenticated.** The `Program.cs` contains a `TODO` comment marking where JWT bearer middleware must be added.
- Before going to production, every route must verify the caller is the account they claim to be. The `accountId` in the URL/body must be validated against the authenticated identity in the token — a player must not be able to pass another player's `accountId`.
- The identity token should come from the login response issued by `PlayerAuthService` in LobbyServer (or a dedicated auth service).
- Do not remove the `TODO` comment until JWT middleware is in place and tested.

---

## CharacterService Invariants

### Character Cap
- `MaxCharactersPerAccount = 4`. Do not raise this without considering the lobby's character-select UI and the database query load.
- The cap check runs **inside the transaction**, before the INSERT. Do not move it outside the transaction — a race between two simultaneous create requests must not bypass the cap.

### Name Validation
- Name must be 1–24 characters. Do not widen this without updating the database column width.
- Allowed characters: letters, digits, spaces, hyphens only. This is enforced by character-by-character validation in `CreateCharacterAsync` before any database call.
- Name uniqueness is case-insensitive: `LOWER(name) = LOWER(@Name)`. Do not change to case-sensitive matching — it would allow `"Player"` and `"player"` to coexist.
- The name is stored trimmed (`name.Trim()`). Do not store leading/trailing whitespace.

### Ownership Enforcement
- `DeleteCharacterAsync` always includes `AND account_id = @AccountId` in its WHERE clause. This is the ownership guard — without it, any player could delete any character by guessing an ID. **Do not remove this clause.**
- `GetCharactersAsync` filters by `account_id`. Never return characters across accounts.

### Transaction Contract
- `CreateCharacterAsync` opens an explicit transaction. The cap check, uniqueness check, and INSERT all run within the same transaction. Rolling back on any exception is mandatory.
- Do not refactor these three steps to run outside the transaction or as separate requests.

---

## CraftingService Invariants

### Atomicity Contract
- The crafting transaction covers three operations: ingredient ownership verification, ingredient consumption (DELETE), and output creation (INSERT). **All three must run inside a single transaction.**
- If ingredient verification passes but the DELETE fails, the transaction rolls back — no partial crafts possible.
- Do not split these into separate database round-trips outside a transaction.

### Ingredient Ownership Check
- Ingredients are verified by querying `player_items WHERE account_id = @AccountId AND definition_id = @DefinitionId`. The `account_id` filter is the ownership guard. **Do not remove it.**
- Only uncrafted ingredients (`crafted_stats_json IS NULL`) are considered consumable inputs. Crafted items cannot be used as raw materials for other recipes (they have unique stats; consuming them would destroy player customization silently).
- The query uses `LIMIT @Quantity` with `ORDER BY instance_id` (deterministic oldest-first). This is intentional to avoid consuming a specifically-selected item when the player has multiples.

### Recipe Catalog
- Recipes are loaded once at server startup into `_recipes` (`IReadOnlyDictionary<int, CraftingRecipe>`). They never change at runtime.
- Recipe IDs come from the database; `CraftingRecipe` is the SharedLibrary contract. Do not introduce a mutable recipe store.
- `GetAllRecipes()` is the endpoint used to send the full catalog to Unity on crafting screen entry — Unity renders requirements from this, not from a separate API call per recipe.

### CraftedStats Serialization
- `recipe.OutputStats == null` → `crafted_stats_json` column is NULL → GameServer uses the archetype's default stats from `ItemDatabase`.
- `recipe.OutputStats != null` → serialized as JSON into `crafted_stats_json` → GameServer reads it as `ItemStatModifiers?` on `ItemInstance.CraftedStats`, taking full priority over the archetype stats.
- Do not change the serialization format without a corresponding change to how `MatchDataService` / `PlayerSession.RecomputeStats` reads `CraftedStats`.

---

## HTTP API Contract

### Endpoint Map
| Method | Path | Handler |
|--------|------|---------|
| `GET`  | `/characters/{accountId}` | List characters |
| `POST` | `/characters` | Create character |
| `DELETE` | `/characters/{accountId}/{characterId}` | Delete character |
| `GET`  | `/crafting/recipes` | Get full recipe catalog |
| `POST` | `/crafting/craft` | Execute craft |

### Response Conventions
- Expected validation failures (`CraftingException`, `CharacterCreationException`) → `400 Bad Request` with `{ "error": "<message>" }`.
- Not found / not owned → `404 Not Found`.
- System errors (DB down, unexpected exception) → let ASP.NET Core's default exception handler return `500`.
- Successful character creation → `201 Created` with `{ "characterId": <id> }`.
- Successful craft → `200 OK` with `{ "instanceId": <id> }`.
- Do not return stack traces or internal exception details in error responses.

### Input Bodies
- `POST /characters` body: `CreateCharacterRequest(int AccountId, string Name, int ClassId)`.
- `POST /crafting/craft` body: `CraftRequest(int AccountId, int RecipeId)`.
- Both are `record` types declared at the bottom of `Program.cs`. Do not move them to separate files without reason — they are request-scoped contracts only.

---

## Database Schema Dependencies

ProfileServer assumes the following PostgreSQL tables:

```sql
-- Characters
CREATE TABLE characters (
    character_id  SERIAL PRIMARY KEY,
    account_id    INT  NOT NULL REFERENCES accounts(id),
    name          VARCHAR(24) NOT NULL,
    class_id      INT  NOT NULL,
    created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (LOWER(name))
);

-- Player items (owned item instances)
CREATE TABLE player_items (
    instance_id        SERIAL PRIMARY KEY,
    account_id         INT  NOT NULL REFERENCES accounts(id),
    definition_id      INT  NOT NULL,
    crafted_stats_json JSONB NULL  -- NULL = unmodified archetype
);
```

- Do not rename columns without updating the Dapper column-alias queries in `CharacterService`.
- `crafted_stats_json` must be `JSONB` (not `TEXT`) so the `::jsonb` cast in the INSERT does not fail.
- The `UNIQUE (LOWER(name))` index enforces the case-insensitive uniqueness rule at the database level in addition to the application-level check.

---

## Do
- Keep all three crafting steps (verify, consume, insert) in one transaction.
- Include `AND account_id = @AccountId` in every query that reads or mutates a specific player's data.
- Validate character names (length + allowed characters) before opening a database connection.
- Keep `MaxCharactersPerAccount` enforced inside the transaction.
- Return `CraftingException` / `CharacterCreationException` as `400 Bad Request`, not `500`.
- Keep the recipe catalog immutable after startup.
- Keep ProfileServer HTTP-only. No UDP, no LiteNetLib.
- On player session entry (login/lobby), check Redis for `crafting-reward:{accountId}`. If present, INSERT the ingredient rows in PostgreSQL, commit, then DELETE the Redis key.
- Always commit to PostgreSQL before deleting the `crafting-reward` Redis key. Failure to commit leaves the key for retry.

## Don't
- Do not add matchmaking, ticket issuance, or session tracking to ProfileServer.
- Do not write to Redis from ProfileServer (the `crafting-reward` key is write-only by GameServer; ProfileServer only reads and deletes it).
- Do not add direct GameServer communication to ProfileServer.
- Do not allow a player to craft using ingredients they don't own (always filter by `account_id`).
- Do not allow crafted items (`crafted_stats_json IS NOT NULL`) to be used as crafting ingredients.
- Do not split the crafting transaction into separate round-trips.
- Do not remove the `AND account_id = @AccountId` ownership guard from `DeleteCharacterAsync`.
- Do not store leading/trailing whitespace in character names.
- Do not skip character name validation before the database call.
- Do not expose raw exception messages or stack traces in HTTP error responses.
- Do not skip the `TODO` auth middleware — all routes must be authenticated before production.
- Do not delete `crafting-reward:{accountId}` from Redis before the PostgreSQL transaction commits.

---

## Project Layout
```
ProfileServer/
  Program.cs               — Entry point; minimal API route registration; request record types
  appsettings.json         — Port (9060) and Postgres connection string
  ProfileServer.csproj     — net7.0 Web SDK; refs SharedLibrary, Dapper, Npgsql
  CharacterService.cs      — Character CRUD; enforces cap (4), name rules, ownership
  CraftingService.cs       — Atomic recipe execution; ingredient ownership check + consume + insert
```
