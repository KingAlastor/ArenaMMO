using SharedLibrary;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GameServer
{
    /// <summary>
    /// Verifies lobby-issued auth tickets for arena admission.
    ///
    /// Validation includes:
    /// - shape/sanity checks
    /// - clock-window checks
    /// - HMAC signature verification
    /// - nonce replay rejection
    /// - allowed spell list parsing
    /// </summary>
    internal sealed class AuthTicketValidator
    {
        private const int AllowedClockSkewMs = 5000;
        private const int MaxPlayerNameLength = 24;
        private const int MaxNonceLength = 128;
        private const int SignatureHexLength = 64;
        private const int MaxAllowedSpellIdsCsvLength = 768;
        private const int MaxAllowedSpellCount = 64;

        private readonly byte[] _secretBytes;
        private readonly ConcurrentDictionary<string, long> _usedNonces = new ConcurrentDictionary<string, long>();

        /// <summary>
        /// Initializes validator with server-side HMAC secret.
        /// </summary>
        public AuthTicketValidator(string secret)
        {
            if (string.IsNullOrWhiteSpace(secret))
                throw new ArgumentException("Ticket secret is required", nameof(secret));

            _secretBytes = Encoding.UTF8.GetBytes(secret);
        }

        /// <summary>
        /// Attempts to authenticate one ticket and returns normalized peer context on success.
        /// </summary>
        public bool TryValidate(AuthTicketPacket packet, out AuthenticatedPeerContext context, out string error)
        {
            context = default;
            error = string.Empty;

            if (packet.PlayerId <= 0)
            {
                error = "invalid-player-id";
                return false;
            }

            if (string.IsNullOrWhiteSpace(packet.PlayerName) || packet.PlayerName.Length > MaxPlayerNameLength)
            {
                error = "invalid-player-name";
                return false;
            }

            if (!Enum.IsDefined(typeof(FactionId), (FactionId)packet.Faction))
            {
                error = "invalid-faction";
                return false;
            }

            if (string.IsNullOrWhiteSpace(packet.Nonce) || packet.Nonce.Length > MaxNonceLength)
            {
                error = "invalid-nonce";
                return false;
            }

            if (string.IsNullOrWhiteSpace(packet.AllowedSpellIdsCsv) || packet.AllowedSpellIdsCsv.Length > MaxAllowedSpellIdsCsvLength)
            {
                error = "invalid-allowed-spells-shape";
                return false;
            }

            if (!IsValidSignatureShape(packet.Signature))
            {
                error = "invalid-signature-shape";
                return false;
            }

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (packet.IssuedAtUnixMs > nowMs + AllowedClockSkewMs)
            {
                error = "ticket-issued-in-future";
                return false;
            }

            if (packet.ExpiresAtUnixMs < nowMs - AllowedClockSkewMs)
            {
                error = "ticket-expired";
                return false;
            }

            if (packet.ExpiresAtUnixMs <= packet.IssuedAtUnixMs)
            {
                error = "invalid-ticket-times";
                return false;
            }

            // Canonical serialization must match the lobby signer exactly.
            string canonical = BuildCanonicalString(packet);
            string expectedSignature = ComputeSignature(canonical);
            if (!FixedTimeEquals(packet.Signature, expectedSignature))
            {
                error = "invalid-signature";
                return false;
            }

            // Nonce cache prevents replaying a previously valid signed ticket.
            if (!_usedNonces.TryAdd(packet.Nonce, packet.ExpiresAtUnixMs))
            {
                error = "replayed-ticket";
                return false;
            }

            CleanupExpiredNonces(nowMs);

            if (!TryParseAllowedSpells(packet.AllowedSpellIdsCsv, out HashSet<int> allowedSpells, out error))
                return false;

            context = new AuthenticatedPeerContext(
                packet.PlayerId,
                packet.PlayerName,
                (FactionId)packet.Faction,
                allowedSpells);

            return true;
        }

        /// <summary>
        /// Stable canonical payload used for signature generation and verification.
        /// Field order is part of the protocol contract.
        /// </summary>
        private static string BuildCanonicalString(AuthTicketPacket packet)
            => string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2}|{3}|{4}|{5}|{6}",
                packet.PlayerId,
                packet.PlayerName,
                packet.Faction,
                packet.AllowedSpellIdsCsv,
                packet.IssuedAtUnixMs,
                packet.ExpiresAtUnixMs,
                packet.Nonce);

        /// <summary>
        /// Computes uppercase hex HMAC-SHA256 signature for canonical payload.
        /// </summary>
        private string ComputeSignature(string canonical)
        {
            using var hmac = new HMACSHA256(_secretBytes);
            byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
            return Convert.ToHexString(hash);
        }

        /// <summary>
        /// Constant-time comparison to reduce timing side-channel leakage.
        /// </summary>
        private static bool FixedTimeEquals(string left, string right)
        {
            if (left is null || right is null)
                return false;

            if (left.Length != right.Length)
                return false;

            byte[] leftBytes = Encoding.UTF8.GetBytes(left);
            byte[] rightBytes = Encoding.UTF8.GetBytes(right);
            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }

        private static bool IsValidSignatureShape(string signature)
        {
            if (string.IsNullOrWhiteSpace(signature) || signature.Length != SignatureHexLength)
                return false;

            for (int i = 0; i < signature.Length; i++)
            {
                char c = signature[i];
                bool isHex = (c >= '0' && c <= '9')
                             || (c >= 'A' && c <= 'F')
                             || (c >= 'a' && c <= 'f');
                if (!isHex)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Parses spell entitlement CSV into a normalized positive-id hash set.
        /// </summary>
        private static bool TryParseAllowedSpells(string csv, out HashSet<int> allowedSpells, out string error)
        {
            allowedSpells = new HashSet<int>();
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(csv))
            {
                error = "empty-allowed-spells";
                return false;
            }

            string[] parts = csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts.Length > MaxAllowedSpellCount)
            {
                error = "invalid-allowed-spell-count";
                return false;
            }

            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int spellId) || spellId <= 0)
                {
                    error = "invalid-spell-id-in-ticket";
                    return false;
                }

                allowedSpells.Add(spellId);
            }

            return true;
        }

        /// <summary>
        /// Opportunistic cleanup keeps nonce cache bounded without introducing a background thread.
        /// </summary>
        private void CleanupExpiredNonces(long nowMs)
        {
            foreach (KeyValuePair<string, long> entry in _usedNonces)
            {
                if (entry.Value < nowMs - AllowedClockSkewMs)
                    _usedNonces.TryRemove(entry.Key, out _);
            }
        }
    }

    /// <summary>
    /// Trusted identity and entitlement context derived from a validated ticket.
    /// </summary>
    internal readonly record struct AuthenticatedPeerContext(
        int PlayerId,
        string PlayerName,
        FactionId Faction,
        HashSet<int> AllowedSpellIds);
}
