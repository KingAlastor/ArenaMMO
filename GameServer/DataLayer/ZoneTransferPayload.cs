using System.Text.Json.Serialization;

namespace GameServer.DataLayer
{
    // ── ZoneTransferPayload ───────────────────────────────────────────────────────────────
    //
    // Published to Redis Pub/Sub channel "zone-transfer:{targetZoneId}" by the source zone
    // server when a player crosses a zone boundary.
    //
    // HANDOFF FLOW:
    //   1. Source zone: player walks into a portal / reaches a zone-boundary trigger.
    //   2. Source zone: calls PlayerStateSink.FlushAsync(player.TakeSnapshot(true)) to
    //      ensure the live-state Redis key is up to date.
    //   3. Source zone: issues a new signed AuthTicket for the player (via TicketIssuer)
    //      with the target zone's expected parameters.
    //   4. Source zone: publishes ZoneTransferPayload to
    //      Redis channel "zone-transfer:{targetZoneId}".
    //   5. Source zone: sends the client a ZoneTransferPacket (Client→Unity) containing
    //      the target server address and the new signed ticket.
    //   6. Source zone: removes the player session (they are logically on the new zone now).
    //   7. Target zone: receives the Redis message, reads State from the payload, pre-warms
    //      the session in a pending dictionary keyed by AccountId.
    //   8. Client connects to target zone and presents the signed ticket.
    //   9. Target zone: validates ticket, finds pre-warmed session, places player at
    //      State.Position with State.Health, bypassing any spawn logic.
    //
    // SECURITY:
    //   SignedTicket is HMAC-SHA256 (same secret as AuthTicket) so the target zone can
    //   verify the transfer was issued by a legitimate source zone server, not forged by a
    //   client.  The target zone still runs full AuthTicketValidator checks.
    //
    // REDIS KEY SCHEMA:
    //     Channel: zone-transfer:{targetZoneId}   → JSON(ZoneTransferPayload)
    // ──────────────────────────────────────────────────────────────────────────────────────

    public sealed class ZoneTransferPayload
    {
        /// <summary>Full live-state snapshot produced by the source zone server.</summary>
        public LivePlayerState State { get; set; } = null!;

        /// <summary>
        /// The zone the player is being transferred into.  Must match the Pub/Sub channel
        /// suffix so the target zone can verify it received the right payload.
        /// </summary>
        public string TargetZoneId { get; set; } = string.Empty;

        /// <summary>
        /// HMAC-SHA256 signed ticket issued by the source zone's TicketIssuer.
        /// The target zone calls AuthTicketValidator.TryValidate() on the client's ticket
        /// after the client connects; this field is only used for pre-warm verification.
        /// </summary>
        public string SignedTicket { get; set; } = string.Empty;

        /// <summary>Unix epoch milliseconds when this payload was published.</summary>
        public long IssuedAtMs { get; set; }
    }
}
