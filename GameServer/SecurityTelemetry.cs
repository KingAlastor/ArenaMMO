using LiteNetLib;
using System;
using System.Net;
using System.Threading;

namespace GameServer
{
    /// <summary>
    /// Minimal in-process security telemetry.
    ///
    /// Counters are lock-free (Interlocked) and logs are structured console lines to keep
    /// overhead small while still enabling incident triage.
    /// </summary>
    internal static class SecurityTelemetry
    {
        // Aggregate counters reported in periodic snapshots.
        private static long _invalidPacketDrops;
        private static long _replayDrops;
        private static long _unauthorizedSpellDrops;
        private static long _invalidTicketDrops;
        private static long _ipRateLimitDrops;

        /// <summary>Records a malformed or out-of-policy client packet drop.</summary>
        public static void RecordInvalidPacket(string reason, NetPeer? peer = null)
        {
            Interlocked.Increment(ref _invalidPacketDrops);
            WriteAudit("invalid-packet", reason, peer, null);
        }

        /// <summary>Records a dropped replayed/out-of-order action intent.</summary>
        public static void RecordReplayDrop(string reason, NetPeer peer)
        {
            Interlocked.Increment(ref _replayDrops);
            WriteAudit("replay-drop", reason, peer, null);
        }

        /// <summary>Records attempts to cast/use spells outside server-authorized loadout.</summary>
        public static void RecordUnauthorizedSpell(NetPeer peer, int spellId)
        {
            Interlocked.Increment(ref _unauthorizedSpellDrops);
            WriteAudit("unauthorized-spell", $"spellId={spellId}", peer, null);
        }

        /// <summary>Records failed auth ticket validations and associated source IP.</summary>
        public static void RecordInvalidTicket(string reason, IPAddress? ip)
        {
            Interlocked.Increment(ref _invalidTicketDrops);
            WriteAudit("invalid-ticket", reason, null, ip);
        }

        /// <summary>Records pre-auth connection requests rejected by IP rate controls.</summary>
        public static void RecordIpRateLimit(IPAddress? ip)
        {
            Interlocked.Increment(ref _ipRateLimitDrops);
            WriteAudit("ip-rate-limit", "connection request rejected", null, ip);
        }

        /// <summary>
        /// Prints one-line snapshot suitable for periodic log scraping and dashboards.
        /// </summary>
        public static void PrintSnapshot()
        {
            Console.WriteLine(
                $"[Security][Snapshot] invalidPacketDrops={Interlocked.Read(ref _invalidPacketDrops)} " +
                $"replayDrops={Interlocked.Read(ref _replayDrops)} " +
                $"unauthorizedSpellDrops={Interlocked.Read(ref _unauthorizedSpellDrops)} " +
                $"invalidTicketDrops={Interlocked.Read(ref _invalidTicketDrops)} " +
                $"ipRateLimitDrops={Interlocked.Read(ref _ipRateLimitDrops)}");
        }

            /// <summary>
            /// Emits structured audit line with event category, peer info, source ip, and reason.
            /// </summary>
        private static void WriteAudit(string category, string reason, NetPeer? peer, IPAddress? ip)
        {
            string peerPart = peer == null ? "peer=n/a" : $"peerId={peer.Id}";
            string ipPart = ip != null ? $"ip={ip}" : peer?.Address != null ? $"ip={peer.Address}" : "ip=n/a";
            Console.WriteLine($"[Security][Audit] ts={DateTime.UtcNow:O} category={category} {peerPart} {ipPart} reason={reason}");
        }
    }
}
