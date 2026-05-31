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
        private static long _damageCapHits;

        /// <summary>Records a malformed or out-of-policy client packet drop.</summary>
        public static void RecordInvalidPacket(string reason, NetPeer? peer = null)
        {
            Interlocked.Increment(ref _invalidPacketDrops);
            WriteAudit("invalid-packet", reason, peer?.Id ?? -1, peer?.Address?.ToString());
        }

        /// <summary>Records a dropped replayed/out-of-order action intent.</summary>
        public static void RecordReplayDrop(string reason, NetPeer peer)
        {
            Interlocked.Increment(ref _replayDrops);
            WriteAudit("replay-drop", reason, peer.Id, peer.Address?.ToString());
        }

        /// <summary>Records attempts to cast/use spells outside server-authorized loadout.</summary>
        // spellId is passed as a plain int — no $"spellId={spellId}" interpolation on the game-loop thread.
        // The string is materialised inside WriteAudit's ThreadPool callback.
        public static void RecordUnauthorizedSpell(NetPeer peer, int spellId)
        {
            Interlocked.Increment(ref _unauthorizedSpellDrops);
            WriteAuditWithSpell("unauthorized-spell", peer.Id, peer.Address?.ToString(), spellId);
        }

        /// <summary>Records failed auth ticket validations and associated source IP.</summary>
        public static void RecordInvalidTicket(string reason, IPAddress? ip)
        {
            Interlocked.Increment(ref _invalidTicketDrops);
            WriteAudit("invalid-ticket", reason, -1, ip?.ToString());
        }

        /// <summary>Records pre-auth connection requests rejected by IP rate controls.</summary>
        public static void RecordIpRateLimit(IPAddress? ip)
        {
            Interlocked.Increment(ref _ipRateLimitDrops);
            WriteAudit("ip-rate-limit", "connection request rejected", -1, ip?.ToString());
        }

        /// <summary>
        /// Records a raw damage value that exceeded <see cref="SharedLibrary.CombatMath.MaxSingleHitDamage"/>
        /// before being clamped to that ceiling.
        ///
        /// This should never fire in a correctly-functioning simulation.  A hit here means
        /// a damage formula has a runaway multiplier or stat overflow bug — investigate
        /// immediately.  The log line includes the attacker entity ID, the context string
        /// (e.g. "melee", "spell", "projectile", "splash", "dot"), and the raw unclamped value
        /// so the bug can be reproduced.
        ///
        /// Logging is offloaded to the ThreadPool so the game-loop thread is not stalled by
        /// Console I/O on this (rare, should-be-zero) path.
        /// </summary>
        // Accepts ReadOnlySpan<char> so call sites can pass string literals with zero heap
        // allocation.  The span is materialised into a string here — on the background
        // thread-pool path — so the managed string lifetime is entirely off the game-loop
        // thread and does not contribute to tick-time GC pressure.
        public static void RecordDamageCap(int attackerId, System.ReadOnlySpan<char> context, int rawDamage)
        {
            Interlocked.Increment(ref _damageCapHits);
            // Materialise the span to a string here, before QueueUserWorkItem, because
            // ReadOnlySpan<char> cannot safely cross async/thread boundaries (stack lifetime).
            // The string allocation happens only on this rare error path, never on the hot path.
            string contextStr = context.ToString();
            var state = (attackerId, contextStr, rawDamage);
            System.Threading.ThreadPool.QueueUserWorkItem(
                static s => Console.WriteLine(
                    $"[Security][BUG] damage-cap-hit attacker={s.attackerId} ctx={s.contextStr} " +
                    $"raw={s.rawDamage} capped={SharedLibrary.CombatMath.MaxSingleHitDamage}"),
                state,
                preferLocal: false);
        }

        /// <summary>
        /// Enqueues a periodic security snapshot to the ThreadPool.
        /// Called from the 30 Hz game loop — Console I/O and string allocation
        /// are deliberately kept off the tick thread.
        /// </summary>
        public static void PrintSnapshot()
        {
            // Snapshot all counters atomically before handing off — the ThreadPool
            // callback captures a plain value tuple (no closure object on heap).
            var snap = (
                inv:   Interlocked.Read(ref _invalidPacketDrops),
                rep:   Interlocked.Read(ref _replayDrops),
                spell: Interlocked.Read(ref _unauthorizedSpellDrops),
                tick:  Interlocked.Read(ref _invalidTicketDrops),
                ip:    Interlocked.Read(ref _ipRateLimitDrops),
                dmg:   Interlocked.Read(ref _damageCapHits));

            ThreadPool.QueueUserWorkItem(
                static s => Console.WriteLine(
                    $"[Security][Snapshot] invalidPacketDrops={s.inv} " +
                    $"replayDrops={s.rep} unauthorizedSpellDrops={s.spell} " +
                    $"invalidTicketDrops={s.tick} ipRateLimitDrops={s.ip} " +
                    $"damageCapHits={s.dmg}"),
                snap,
                preferLocal: false);
        }

        /// <summary>
        /// Offloads one audit log line to the ThreadPool.
        /// All string interpolation and Console I/O happen off the game-loop thread.
        /// <paramref name="peerId"/> is -1 when no peer is associated.
        /// <paramref name="ipStr"/> is a pre-materialised string (IPEndPoint.ToString
        /// already allocates; we snapshot it once here rather than boxing the struct
        /// repeatedly inside the lambda).
        /// </summary>
        private static void WriteAudit(string category, string reason, int peerId, string? ipStr)
        {
            var state = (category, reason, peerId, ipStr, ts: DateTime.UtcNow);
            ThreadPool.QueueUserWorkItem(
                static s =>
                {
                    string peerPart = s.peerId == -1 ? "peer=n/a" : $"peerId={s.peerId}";
                    string ipPart   = s.ipStr  != null ? $"ip={s.ipStr}" : "ip=n/a";
                    Console.WriteLine(
                        $"[Security][Audit] ts={s.ts:O} category={s.category} {peerPart} {ipPart} reason={s.reason}");
                },
                state,
                preferLocal: false);
        }

        // Separate overload for RecordUnauthorizedSpell so the spellId int
        // is carried as part of the TState value tuple — zero heap allocation
        // on the game-loop thread (no $"spellId={spellId}" string needed at the call site).
        private static void WriteAuditWithSpell(string category, int peerId, string? ipStr, int spellId)
        {
            var state = (category, peerId, ipStr, spellId, ts: DateTime.UtcNow);
            ThreadPool.QueueUserWorkItem(
                static s =>
                {
                    string peerPart = s.peerId == -1 ? "peer=n/a" : $"peerId={s.peerId}";
                    string ipPart   = s.ipStr  != null ? $"ip={s.ipStr}" : "ip=n/a";
                    Console.WriteLine(
                        $"[Security][Audit] ts={s.ts:O} category={s.category} {peerPart} {ipPart} reason=spellId={s.spellId}");
                },
                state,
                preferLocal: false);
        }
    }
}
