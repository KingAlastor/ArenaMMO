using Dapper;
using Npgsql;
using System;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace LobbyServer
{
    /// <summary>
    /// Authenticates players against the persistent database.
    /// Only called during the pre-match lobby flow — never inside any game simulation loop.
    /// </summary>
    internal sealed class PlayerAuthService : IDisposable
    {
        private readonly string _connectionString;

        // Pre-computed dummy hash used in timing-equalization when the queried username does not
        // exist. Without this, an attacker can distinguish valid vs. invalid usernames by
        // measuring how long the server takes to respond (no BCrypt work vs. full BCrypt work).
        private static readonly string _bcryptDummyHash =
            BCrypt.Net.BCrypt.HashPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));

        public PlayerAuthService(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Postgres connection string is required.", nameof(connectionString));

            _connectionString = connectionString;
        }

        /// <summary>
        /// Looks up the player by display name and verifies the bcrypt credential.
        /// Returns null when authentication fails (player not found or wrong password).
        /// Runs constant-time BCrypt.Verify even when the username is not found to prevent
        /// timing-based username enumeration.
        /// </summary>
        public async Task<PlayerProfile?> TryAuthenticateAsync(string playerName, string credentialToken)
        {
            if (string.IsNullOrWhiteSpace(playerName) || playerName.Length > 24
                || string.IsNullOrWhiteSpace(credentialToken) || credentialToken.Length > 512)
                return null;

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            // Fetch the raw DB row including the stored bcrypt hash.
            // NOTE: The players table must have a password_hash column (bcrypt, cost ≥ 12).
            //       For Steam/JWT auth: swap this query and the Verify call below accordingly.
            var row = await conn.QueryFirstOrDefaultAsync<PlayerDbRow>(
                @"SELECT id                AS AccountId,
                         display_name      AS PlayerName,
                         allowed_spell_ids AS AllowedSpellIdsCsv,
                         password_hash     AS PasswordHash
                  FROM   players
                  WHERE  display_name = @name
                  LIMIT  1",
                new { name = playerName });

            // Always call BCrypt.Verify regardless of whether the row was found.
            // When the row is missing we verify against _bcryptDummyHash so the call
            // takes the same wall-clock time, preventing username enumeration.
            string hashToVerify    = row?.PasswordHash ?? _bcryptDummyHash;
            bool   credentialValid = BCrypt.Net.BCrypt.Verify(credentialToken, hashToVerify);

            if (row is null || !credentialValid)
                return null;

            return new PlayerProfile(row.AccountId, row.PlayerName, row.AllowedSpellIdsCsv);
        }

        public void Dispose() { /* connections are opened and closed per-call */ }

        // Raw DB row — includes the bcrypt hash, which must never leave this class.
        private sealed record PlayerDbRow(
            int    AccountId,
            string PlayerName,
            string AllowedSpellIdsCsv,
            string PasswordHash);
    }

    /// <summary>Immutable player profile loaded from the database after successful authentication.</summary>
    internal sealed record PlayerProfile(int AccountId, string PlayerName, string AllowedSpellIdsCsv);
}
