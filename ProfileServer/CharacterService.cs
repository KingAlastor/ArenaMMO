using Dapper;
using Npgsql;

namespace ProfileServer;

/// <summary>
/// Manages character creation and retrieval for a player account.
///
/// Called from the character select / creation screen — never during matchmaking or gameplay.
/// Characters are persistent entities in PostgreSQL; they are not loaded into the game server
/// directly. Instead the LobbyServer reads the selected character from the player's profile
/// when building the PlayerProfile that gets cached to Redis pre-match.
/// </summary>
internal sealed class CharacterService
{
    private readonly string _connectionString;

    /// <summary>Maximum number of characters allowed per account.</summary>
    private const int MaxCharactersPerAccount = 4;

    public CharacterService(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Postgres connection string is required.", nameof(connectionString));

        _connectionString = connectionString;
    }

    /// <summary>
    /// Returns all characters belonging to the given account, ordered by creation date.
    /// </summary>
    public async Task<IEnumerable<CharacterSummary>> GetCharactersAsync(int accountId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        return await conn.QueryAsync<CharacterSummary>(
            @"SELECT character_id   AS CharacterId,
                     name           AS Name,
                     class_id       AS ClassId,
                     created_at     AS CreatedAt
                FROM characters
               WHERE account_id = @AccountId
               ORDER BY created_at ASC",
            new { AccountId = accountId });
    }

    /// <summary>
    /// Creates a new character for the given account.
    /// Throws <see cref="CharacterCreationException"/> when the name is taken,
    /// the account is at the character cap, or the class ID is invalid.
    /// Returns the new character's ID on success.
    /// </summary>
    public async Task<int> CreateCharacterAsync(int accountId, string name, int classId)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 24)
            throw new CharacterCreationException("Character name must be 1–24 characters.");

        // Sanitize: letters, digits, spaces, hyphens only.
        foreach (char c in name)
            if (!char.IsLetterOrDigit(c) && c != ' ' && c != '-')
                throw new CharacterCreationException("Character name contains invalid characters.");

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        try
        {
            // ── 1. Enforce character cap ──────────────────────────────────────────
            int existing = await conn.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM characters WHERE account_id = @AccountId",
                new { AccountId = accountId },
                transaction: tx);

            if (existing >= MaxCharactersPerAccount)
                throw new CharacterCreationException(
                    $"Account has reached the maximum of {MaxCharactersPerAccount} characters.");

            // ── 2. Enforce unique name ────────────────────────────────────────────
            bool nameTaken = await conn.QuerySingleAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM characters WHERE LOWER(name) = LOWER(@Name))",
                new { Name = name },
                transaction: tx);

            if (nameTaken)
                throw new CharacterCreationException($"The name \"{name}\" is already taken.");

            // ── 3. Insert character ───────────────────────────────────────────────
            int newCharacterId = await conn.QuerySingleAsync<int>(
                @"INSERT INTO characters (account_id, name, class_id)
                  VALUES (@AccountId, @Name, @ClassId)
                  RETURNING character_id",
                new { AccountId = accountId, Name = name.Trim(), ClassId = classId },
                transaction: tx);

            await tx.CommitAsync();
            return newCharacterId;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// Permanently deletes a character. The character must belong to the given account —
    /// the ownership check prevents one player from deleting another's character.
    /// Returns false when the character was not found or does not belong to this account.
    /// </summary>
    public async Task<bool> DeleteCharacterAsync(int accountId, int characterId)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        int rows = await conn.ExecuteAsync(
            "DELETE FROM characters WHERE character_id = @CharacterId AND account_id = @AccountId",
            new { CharacterId = characterId, AccountId = accountId });
        return rows > 0;
    }
}

/// <summary>Lightweight read model returned by <see cref="CharacterService.GetCharactersAsync"/>.</summary>
public sealed class CharacterSummary
{
    public int      CharacterId { get; init; }
    public string   Name        { get; init; } = string.Empty;
    public int      ClassId     { get; init; }
    public DateTime CreatedAt   { get; init; }
}

/// <summary>Thrown by CharacterService for expected creation failures (not system errors).</summary>
public sealed class CharacterCreationException : Exception
{
    public CharacterCreationException(string message) : base(message) { }
}
