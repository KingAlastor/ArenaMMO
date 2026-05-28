using SharedLibrary;
using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LobbyServer
{
    /// <summary>
    /// Issues HMAC-SHA256 signed AuthTicketPackets that the arena's AuthTicketValidator will accept.
    ///
    /// The canonical field order and HMAC algorithm are part of the server-to-server protocol
    /// contract. Any change here must be rolled out to the arena server simultaneously.
    /// </summary>
    internal sealed class TicketIssuer
    {
        private readonly byte[] _secretBytes;
        private readonly int    _lifetimeMs;

        public TicketIssuer(string secret, int lifetimeMs)
        {
            if (string.IsNullOrWhiteSpace(secret))
                throw new ArgumentException("Ticket secret is required.", nameof(secret));

            _secretBytes = Encoding.UTF8.GetBytes(secret);
            _lifetimeMs  = lifetimeMs;
        }

        /// <summary>
        /// Builds and signs a ticket for a player who has been assigned to a match.
        /// </summary>
        public AuthTicketPacket Issue(int playerId, string playerName, byte faction, string allowedSpellIdsCsv)
        {
            long   nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

            var packet = new AuthTicketPacket
            {
                PlayerId           = playerId,
                PlayerName         = playerName,
                Faction            = faction,
                AllowedSpellIdsCsv = allowedSpellIdsCsv,
                IssuedAtUnixMs     = nowMs,
                ExpiresAtUnixMs    = nowMs + _lifetimeMs,
                Nonce              = nonce,
            };

            string canonical = BuildCanonicalString(packet);
            packet.Signature  = ComputeSignature(canonical);
            return packet;
        }

        // ── Private ───────────────────────────────────────────────────────────

        /// <summary>
        /// Canonical payload string — must match AuthTicketValidator.BuildCanonicalString
        /// field-for-field. Protocol contract: do not reorder fields without a coordinated
        /// lobby + arena rollout.
        /// </summary>
        private static string BuildCanonicalString(AuthTicketPacket p)
            => string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2}|{3}|{4}|{5}|{6}",
                p.PlayerId,
                p.PlayerName,
                p.Faction,
                p.AllowedSpellIdsCsv,
                p.IssuedAtUnixMs,
                p.ExpiresAtUnixMs,
                p.Nonce);

        private string ComputeSignature(string canonical)
        {
            using var hmac = new HMACSHA256(_secretBytes);
            return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
        }
    }
}
