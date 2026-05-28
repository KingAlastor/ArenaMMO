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
- Input validation (name length ≤ 24, non-empty token) must happen before any database call.
- Credential verification is intentionally a placeholder — replace with bcrypt, JWT, or Steam session ticket verification; do not ship the placeholder in production.
- DB connections open and close per-call (no connection pooling at this layer). This is intentional for simplicity; revisit if lobby scale requires it.
- Failed authentication must:
  1. Send `LobbyLoginResponsePacket { Success = false, Error = "<reason>" }`.
  2. Immediately disconnect the peer.
  3. Never cache the unauthenticated peer as authenticated.

---

## Matchmaking Contract
- `MatchmakingQueue` is the only code that assigns factions (`FactionId.Alpha` / `FactionId.Beta`).
- Match size must be a positive even number (first half → Alpha, second half → Beta).
- A player re-queuing (e.g. after network drop) replaces their prior queue entry — not duplicated.
- Players removed from the queue (disconnect) must also be cleaned from `_authenticatedPeers`, `_peerPlayerMap`, and `_profileCache` in `LobbyNetworkManager.OnPeerDisconnected`.
- `TryFormMatch` is called from the coordination loop thread; it holds the queue lock only for the atomic dequeue. Do not perform I/O or blocking calls under the lock.

---

## Network & Security Guardrails
- Peers that connect but do not complete login within a reasonable time should be disconnected. Consider adding an auth-timeout similar to the arena's `AuthTimeoutMs`.
- All inbound packets from peers not yet in `_peerPlayerMap` (i.e., unauthenticated) must be silently discarded or rejected with disconnect.
- Never trust `PlayerName`, `Faction`, or `AllowedSpellIdsCsv` values from the client. Always source them from the database profile loaded during authentication.
- `OnLoginRequest` uses `Task.Run` for async DB access. The captured variables (`capturedPeer`, `capturedName`, `capturedToken`) must be immutable snapshots — never capture mutable shared state.
- `Send<T>()` allocates a new `NetDataWriter` per call. This is acceptable in the lobby (low-frequency); do not replicate this pattern in the arena's hot loop.

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
- Validate all client-supplied strings (name length, non-empty token) at the boundary before any downstream use.
- Source `AllowedSpellIdsCsv` exclusively from the database profile.
- Keep faction assignment inside `MatchmakingQueue.TryFormMatch` only.
- Keep Redis publish failures non-fatal to the match dispatch path.
- Keep the Unity client's role purely passive: receive ticket, forward to arena.

## Don't
- Do not let the client supply or influence `AllowedSpellIdsCsv`, `Faction`, or `PlayerId`.
- Do not store `ARENA_TICKET_SECRET` anywhere other than an environment variable.
- Do not skip nonce generation or reuse nonces across tickets.
- Do not reorder canonical ticket fields without a simultaneous arena + lobby rollout.
- Do not add LINQ or blocking I/O to the 20 Hz coordination loop.
- Do not add SQL queries to the lobby coordination loop. DB access belongs in `PlayerAuthService` only, triggered by login.
- Do not allow unauthenticated peers to trigger matchmaking or queue operations.
- Do not have the Unity client construct, modify, or sign an `AuthTicketPacket` itself.

---

## Project Layout
```
LobbyServer/
  Program.cs               — Entry point; reads ARENA_TICKET_SECRET from env
  appsettings.json         — Non-secret config (ports, match size, Redis/Postgres strings)
  LobbyServer.csproj       — Net7.0; refs SharedLibrary, LiteNetLib, Dapper, Npgsql, Redis
  TicketIssuer.cs          — HMAC-SHA256 signing; canonical field order is protocol contract
  PlayerAuthService.cs     — Postgres credential lookup via Dapper
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
