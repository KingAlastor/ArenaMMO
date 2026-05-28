using Dapper;
using Npgsql;
using System;
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

        public PlayerAuthService(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Postgres connection string is required.", nameof(connectionString));

            _connectionString = connectionString;
        }

        /// <summary>
        /// Looks up the player profile by name and validates the credential token.
        /// Returns null when authentication fails (player not found or bad token).
        /// </summary>
        /// <param name="playerName">Display name submitted by the client.</param>
        /// <param name="credentialToken">
        /// Opaque token (e.g. bcrypt-hashed password, Steam session ticket, JWT sub).
        /// Replace the placeholder comparison below with your actual auth scheme.
        /// </param>
        public async Task<PlayerProfile?> TryAuthenticateAsync(string playerName, string credentialToken)
        {
            // Input guard — never send unsanitized strings into SQL params even via parameterisation.
            if (string.IsNullOrWhiteSpace(playerName) || playerName.Length > 24)
                return null;

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            // TODO: Replace the credential_token column check with your actual auth scheme.
            // Example schemes:
            //   - bcrypt: fetch password_hash, then BCrypt.Net.BCrypt.Verify(credentialToken, hash)
            //   - JWT:    validate JWT signature, extract sub == playerId
            //   - Steam:  call Steam Web API to validate the session ticket
            var profile = await conn.QueryFirstOrDefaultAsync<PlayerProfile>(
                @"SELECT id                AS AccountId,
                         display_name      AS PlayerName,
                         allowed_spell_ids AS AllowedSpellIdsCsv
                  FROM   players
                  WHERE  display_name = @name
                  LIMIT  1",
                new { name = playerName });

            if (profile is null)
                return null;

            // Placeholder token check — swap for a real scheme (see TODO above).
            if (string.IsNullOrWhiteSpace(credentialToken))
                return null;

            return profile;
        }

        public void Dispose() { /* connections are opened and closed per-call */ }
    }

    /// <summary>Immutable player profile loaded from the database after successful authentication.</summary>
    internal sealed record PlayerProfile(int AccountId, string PlayerName, string AllowedSpellIdsCsv);
}
