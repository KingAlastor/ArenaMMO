---
name: lobbyserver-invariants
description: "Use when editing ArenaMMO LobbyServer authentication, matchmaking, ticket issuance, Redis pub/sub, or the Unity LobbyNetworkManager; preserves ticket protocol compatibility, pre-auth security guardrails, and the lobby-to-arena handoff contract."
---

# LobbyServer Invariants

## Purpose
This skill defines the authentication, matchmaking, and ticket-issuance invariants Copilot must preserve when editing the LobbyServer project or the Unity-side `LobbyNetworkManager`.

## Core Identity
- The LobbyServer is the only system authorized to issue signed AuthTickets.
- The lobby authenticates players and assigns factions before any arena admission.
- The arena GameServer validates but never generates tickets.
- The Unity client is a passive consumer: it receives a `MatchFoundPacket` and forwards its fields verbatim as an `AuthTicketPacket` to the arena.

---

## Ticket Issuance Contract
- `TicketIssuer` is the single source of truth for ticket signing. Do not replicate signing logic anywhere else.
- The canonical field order in `TicketIssuer.BuildCanonicalString` **must** match `AuthTicketValidator.BuildCanonicalString` in GameServer field-for-field:
  ```
  {PlayerId}|{PlayerName}|{Faction}|{AllowedSpellIdsCsv}|{IssuedAtUnixMs}|{ExpiresAtUnixMs}|{Nonce}
  ```
- Do not reorder these fields without a coordinated lobby + arena + SharedLibrary rollout.
- Nonce must always be a cryptographically random value (use `RandomNumberGenerator.GetBytes`). Never use sequential IDs or timestamps as nonces.
- `ARENA_TICKET_SECRET` must only be sourced from an environment variable. Never store it in `appsettings.json`, source control, or log output.
- Ticket lifetime is configured via `Lobby:TicketLifetimeMs` (default 30 000 ms). Keep this short — tickets are single-use.

## Ticket Fields Invariants
- `PlayerId` must be a positive integer loaded from the database. Never accept 0 or negative.
- `AllowedSpellIdsCsv` must come from the authenticated player's database record only. The client must never supply or modify this value.
- `Faction` is assigned exclusively by `MatchmakingQueue.TryFormMatch`. The client never selects its own faction.
- `Nonce` is 32 hex characters (16 random bytes). The arena's nonce replay cache enforces single-use.

---

## Authentication Contract
- `PlayerAuthService` is the only code path that touches the `players` table for credential verification.
- Input validation (name length ≤ 24, token length ≤ 512, non-empty) must happen before any database call.
- Credentials are verified with **BCrypt.Net-Next** (`BCrypt.Net.BCrypt.Verify`). The `players` table must have a `password_hash` column storing a bcrypt hash with cost ≥ 12.
- A static `_bcryptDummyHash` is pre-computed once at startup. When a username does not exist in the database, `BCrypt.Verify` is still called against the dummy hash so both paths take the same wall-clock time. **Do not remove this timing-equalization step** — it prevents username enumeration via response-time measurement.
- The `password_hash` column is selected into the private `PlayerDbRow` record, which never leaves `PlayerAuthService`. `PlayerProfile` (the public return type) does not carry any hash.
- For Steam/JWT auth: swap the Npgsql query and the `BCrypt.Verify` call; keep the dummy-hash timing equalization pattern.
- Failed authentication must:
  1. Send `LobbyLoginResponsePacket { Success = false, Error = "<reason>" }`.
  2. Immediately disconnect the peer.
  3. Never cache the unauthenticated peer as authenticated.

---

## Matchmaking Contract
- `MatchmakingQueue` is the only code that assigns factions (`FactionId.Alpha` / `FactionId.Beta`).
- Match size must be a positive even number (first half → Alpha, second half → Beta).
- A player re-queuing (e.g. after network drop) replaces their prior queue entry — not duplicated.
- Players removed from the queue (disconnect) must also be cleaned from `_authenticatedPeers`, `_peerPlayerMap`, `_profileCache`, and `_dispatchedPlayerIds` in `LobbyNetworkManager.OnPeerDisconnected`.
- `TryFormMatch` is called from the coordination loop thread; it holds the queue lock only for the atomic dequeue. Do not perform I/O or blocking calls under the lock.
- Once `TryFormAndDispatchMatch` sends a `MatchFoundPacket` for a player, their `PlayerId` is added to `_dispatchedPlayerIds`. Any subsequent `LobbyQueueJoinPacket` from that peer must be silently dropped. **Do not remove this guard** — without it, a player can re-enter the queue while simultaneously connecting to the arena.

---

## Network & Security Guardrails

### Connection Flood Defense
- `OnConnectionRequest` runs three ordered rejection checks before calling `request.Accept()`:
  1. **Protocol key** — reject wrong key immediately, before IP tracking.
  2. **IP rate limit** (`LobbyIpGuardState`) — per-IP sliding window (`MaxConnectionsPerIpWindow = 10` per 10 s); violation applies a `IpBanDurationMs = 60 000 ms` temporary ban.
  3. **Pending-auth cap** — reject if `_pendingAuth.Count >= MaxPendingConnections (512)`. This caps pre-auth RAM footprint under floods.
- Do not reorder or skip these three checks.
- `_pendingAuth` stores the connect timestamp (`long`) for each unauthenticated peer, not a sentinel byte.

### Auth Timeout (Ghost Connection Prevention)
- `DisconnectAuthTimeoutPeers()` is called every coordination tick (before `TryFormAndDispatchMatch`).
- Peers that have not completed authentication within `AuthTimeoutMs = 8 000 ms` are forcibly disconnected and removed from `_pendingAuth` and `_loginAttempts`.
- Do not remove or bypass this — without it, connection floods leave permanent ghost entries in `_pendingAuth`.

### Per-Peer Login Throttle
- `_loginAttempts` tracks the count of `LobbyLoginRequestPacket` per peer.
- After `MaxLoginAttemptsPerPeer = 3` attempts, the peer is disconnected **before** spawning a DB task.
- This prevents credential-stuffing attacks from saturating the PostgreSQL connection pool.

### Double-Login Race Condition Guard
- `OnLoginRequest` uses `_authenticatedPeers.TryAdd` (not `[]=`) as an atomic single-occupancy check after the async DB call completes.
- If `TryAdd` returns `false`, another session for the same `PlayerId` is already active; send `"already-connected"` and disconnect.
- After `TryAdd` succeeds, call `_pendingAuth.TryRemove(peer, out _)` as a **completion handshake**. If it returns `false`, the peer disconnected or timed out during the DB call; roll back `_authenticatedPeers.TryRemove` and return without writing to `_peerPlayerMap`. This prevents ghost entries from the async race.
- Do not use `_authenticatedPeers[id] = peer` (direct assignment) — it silently overwrites an existing session.

### General Packet Rules
- All inbound packets from peers not yet in `_peerPlayerMap` (i.e., unauthenticated) must be silently discarded.
- Never trust `PlayerName`, `Faction`, or `AllowedSpellIdsCsv` values from the client. Always source them from the database profile loaded during authentication.
- `OnLoginRequest` uses `Task.Run` for async DB access. The captured variables (`capturedPeer`, `capturedName`, `capturedToken`) must be immutable snapshots — never capture mutable shared state.
- `Send<T>()` allocates a new `NetDataWriter` per call. This is acceptable in the lobby (low-frequency); do not replicate this pattern in the arena's hot loop.
- Do not call `_processor.RegisterNestedType<Vec2>()` in `LobbyNetworkManager`. No lobby packet uses `Vec2`, and `Vec2` does not implement `INetSerializable` — this causes a compile error in LiteNetLib 2.x.

---

## Redis Pub/Sub Contract
- The lobby publishes to `arena:match-formed` after dispatching tickets. This is a notification channel only — the arena does not gate admission on it. Ticket HMAC is the only admission authority.
- Redis publish failures must be caught and logged but **must not** block or abort ticket dispatch to clients.
- Do not publish high-frequency positional or simulation data over Redis Pub/Sub. This channel is for orchestration events only.

---

## Lobby Coordination Loop
- The main loop runs at 20 Hz (`Thread.Sleep(50)`). This is intentional — it is a coordination loop, not a game simulation loop.
- `TryFormAndDispatchMatch()` and `BroadcastQueueStatusIfDue()` must remain lightweight. No blocking I/O, no LINQ, no allocations in steady-state when the queue is empty.
- Queue status broadcasts fire at most every `Lobby:QueueStatusIntervalMs` (default 2 000 ms) per cycle. Do not tie this to the 20 Hz loop directly.

---

## Unity LobbyNetworkManager Contract
- `LobbyNetworkManager` is a MonoBehaviour; it polls LiteNetLib via `Update()`. All packet callbacks fire on the Unity main thread — no threading concerns for UI event invocations.
- `PendingTicket` is a static nullable `MatchTicket?`. It is written once by `HandleMatchFound` and must be read and cleared by the arena's network manager on `Awake`. Do not read it from lobby UI code.
- The client must never construct or sign its own `AuthTicketPacket`. It must only forward the fields it received in `MatchFoundPacket` via `MatchTicket.ToAuthTicketPacket()`.
- `Connect()` must not be called while `_lobbyPeer != null` (guard is already in place — preserve it).
- After `HandleMatchFound` fires: disconnect from lobby, then load the arena scene. Do not load the scene while still connected to the lobby.
- `ConnectionKey` must match the string expected by `LobbyNetworkManager` on the server side: `"ArenaMMO_Lobby_v1"`. Changing it requires a coordinated client + server update.

---

## Do
- Keep `TicketIssuer` and `AuthTicketValidator` in strict canonical-order parity.
- Validate all client-supplied strings (name length ≤ 24, token length ≤ 512, non-empty) at the boundary before any downstream use.
- Source `AllowedSpellIdsCsv` exclusively from the database profile.
- Keep faction assignment inside `MatchmakingQueue.TryFormMatch` only.
- Keep Redis publish failures non-fatal to the match dispatch path.
- Keep the Unity client's role purely passive: receive ticket, forward to arena.
- Always use `_authenticatedPeers.TryAdd` (not `[]=`) when registering a new session after auth.
- Always call `_pendingAuth.TryRemove` as the completion handshake after `TryAdd` succeeds; roll back on `false`.
- Always run the BCrypt dummy-hash timing-equalization branch when a username is not found.
- Always clear `_dispatchedPlayerIds` for a player in `OnPeerDisconnected`.

## Don't
- Do not let the client supply or influence `AllowedSpellIdsCsv`, `Faction`, or `PlayerId`.
- Do not store `ARENA_TICKET_SECRET` anywhere other than an environment variable.
- Do not skip nonce generation or reuse nonces across tickets.
- Do not reorder canonical ticket fields without a simultaneous arena + lobby rollout.
- Do not add LINQ or blocking I/O to the 20 Hz coordination loop.
- Do not add SQL queries to the lobby coordination loop. DB access belongs in `PlayerAuthService` only, triggered by login.
- Do not allow unauthenticated peers to trigger matchmaking or queue operations.
- Do not have the Unity client construct, modify, or sign an `AuthTicketPacket` itself.
- Do not use `_authenticatedPeers[id] = peer` (direct assignment) — use `TryAdd` to prevent silent session takeover.
- Do not remove the `_pendingAuth.TryRemove` completion handshake — it is the rollback gate for the async double-login race.
- Do not remove `DisconnectAuthTimeoutPeers()` from the coordination loop — it is the only ghost-connection defence.
- Do not remove the `_dispatchedPlayerIds` check in `OnQueueJoin` — it prevents post-dispatch state machine re-entry.
- Do not remove the BCrypt dummy-hash branch in `PlayerAuthService` — it prevents username enumeration via timing.
- Do not call `RegisterNestedType<Vec2>()` in the lobby's `NetPacketProcessor` — no lobby packet uses `Vec2`.

---

## Project Layout
```
LobbyServer/
  Program.cs               — Entry point; reads ARENA_TICKET_SECRET from env
  appsettings.json         — Non-secret config (ports, match size, Redis/Postgres strings)
  LobbyServer.csproj       — Net7.0; refs SharedLibrary, LiteNetLib, Dapper, Npgsql, Redis, BCrypt.Net-Next
  TicketIssuer.cs          — HMAC-SHA256 signing; canonical field order is protocol contract
  PlayerAuthService.cs     — Postgres credential lookup via Dapper; BCrypt.Net-Next verification; timing-equalization dummy hash
  MatchmakingQueue.cs      — Thread-safe FIFO; faction assignment on match formation
  LobbyNetworkManager.cs   — LiteNetLib UDP listener; 20 Hz coordination loop

UnityClient/Assets/Scripts/Networking/Lobby/
  LobbyNetworkManager.cs   — MonoBehaviour; Connect/JoinQueue API; MatchTicket static store
```

## Lobby Packet Catalogue (SharedLibrary)
### Client → LobbyServer
- `LobbyLoginRequestPacket` — `PlayerName` + `CredentialToken` (opaque auth token).
- `LobbyQueueJoinPacket` — signals intent to enter matchmaking queue (no payload fields currently).

### LobbyServer → Client
- `LobbyLoginResponsePacket` — `Success`, `PlayerId`, `PlayerName`, `Error`.
- `LobbyQueueStatusPacket` — `QueuePosition`, `PlayersInQueue`, `PlayersNeeded`.
- `MatchFoundPacket` — `ArenaIp`, `ArenaPort` + all fields needed to reconstruct `AuthTicketPacket`.

### Design Rules
- `CredentialToken` is opaque; the lobby server defines its meaning. The client never interprets it.
- `MatchFoundPacket` carries the full signed ticket inline so the client requires no additional round-trip.
- Do not add client-authoritative fields to any lobby packet.
