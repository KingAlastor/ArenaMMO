# ArenaMMO

A server-authoritative, real-time multiplayer arena game built with .NET 7 and Unity. The backend is composed of three independent services—**LobbyServer**, **GameServer**, and **ProfileServer**—that together handle authentication, matchmaking, live gameplay simulation, and persistent character data.

---

## Table of Contents

1. [Architecture Overview](#architecture-overview)
2. [Project Structure](#project-structure)
3. [Services In Depth](#services-in-depth)
   - [SharedLibrary](#sharedlibrary)
   - [ProfileServer](#profileserver)
   - [LobbyServer](#lobbyserver)
   - [GameServer](#gameserver)
   - [UnityClient](#unityclient)
4. [End-to-End Player Flow](#end-to-end-player-flow)
5. [Gameplay Simulation Loop](#gameplay-simulation-loop)
6. [Networking & Packet Reference](#networking--packet-reference)
7. [Security Model](#security-model)
8. [Database Schema Overview](#database-schema-overview)
9. [Configuration](#configuration)
10. [Running Locally](#running-locally)
11. [Testing](#testing)
12. [Technology Stack](#technology-stack)

---

## Architecture Overview

```
┌──────────────────────────────────────────────────────────────────────────────┐
│  Unity Client (C# / LiteNetLib)                                              │
│   • Character select (HTTP → ProfileServer)                                  │
│   • Matchmaking (TCP/UDP → LobbyServer)                                      │
│   • Live gameplay (UDP → GameServer)                                         │
└──────────┬──────────────────────┬─────────────────────────┬──────────────────┘
           │ HTTP REST            │ UDP/TCP                  │ UDP
           ▼                      ▼                          ▼
┌───────────────────┐  ┌────────────────────┐  ┌────────────────────────────┐
│  ProfileServer    │  │  LobbyServer        │  │  GameServer                │
│  :9060 (HTTP)     │  │  :9040 (LiteNetLib) │  │  :9050 (LiteNetLib)        │
│                   │  │                     │  │                            │
│ • Characters CRUD │  │ • Player auth       │  │ • 30 Hz fixed-tick loop    │
│ • Crafting        │  │ • Matchmaking queue │  │ • ArenaInstance            │
│ • PostgreSQL      │  │ • Ticket issuance   │  │ • Movement / Combat /      │
│                   │  │ • Redis pub/sub     │  │   Projectile systems       │
│                   │  │ • PostgreSQL        │  │ • Redis + PostgreSQL       │
└───────────────────┘  └──────────┬──────────┘  └────────────────────────────┘
                                   │ HMAC-signed AuthTicket
                                   └─────────────────────────────────────────►
```

All three servers share a single **PostgreSQL** database and a **Redis** instance. Communication between LobbyServer and GameServer happens via HMAC-SHA256 signed `AuthTicketPacket`s; players present their ticket directly to the GameServer when connecting.

---

## Project Structure

```
ArenaMMO/
├── SharedLibrary/          # netstandard2.1 — packets, enums, math helpers
│   ├── NetworkPackets.cs   # All C→S and S→C packet types
│   ├── CombatMath.cs
│   ├── CraftingRecipe.cs
│   ├── ItemDefinition.cs
│   ├── SpellDefinition.cs
│   └── WorldBounds.cs
│
├── GameServer/             # net7.0 — real-time arena simulation
│   ├── ArenaInstance.cs    # Core match container & tick driver
│   ├── NetworkManager.cs   # LiteNetLib glue
│   ├── PlayerSession.cs    # Per-player authoritative state
│   ├── AuthTicketValidator.cs
│   ├── IntentGuard.cs      # Rate-limiting & anti-cheat
│   ├── InputSanitizer.cs
│   ├── SecurityTelemetry.cs
│   ├── ItemDatabase.cs / SpellDatabase.cs
│   ├── ZoneDescriptor.cs
│   ├── Systems/
│   │   ├── CombatSystem.cs
│   │   ├── MovementSystem.cs
│   │   └── ProjectileSystem.cs
│   └── DataLayer/
│       ├── MatchDataService.cs
│       ├── LivePlayerState.cs
│       ├── PlayerStateSink.cs
│       └── ZoneTransferPayload.cs
│
├── LobbyServer/            # net7.0 — pre-match coordination
│   ├── LobbyNetworkManager.cs
│   ├── PlayerAuthService.cs  # BCrypt credential verification
│   ├── MatchmakingQueue.cs
│   ├── TicketIssuer.cs
│   └── MatchDataService.cs (shared Redis connection)
│
├── ProfileServer/          # net7.0 ASP.NET Core — character & crafting REST API
│   ├── CharacterService.cs
│   └── CraftingService.cs
│
├── GameServer.Tests/       # net7.0 xUnit — integration test harness
│   ├── Infrastructure/
│   │   ├── GameServerTestHost.cs
│   │   ├── PseudoClient.cs
│   │   ├── TestAssertions.cs
│   │   └── TestUtilities.cs
│   └── IntegrationTests/
│       ├── MovementIntegrationTests.cs
│       └── GameServerTestExamples.cs
│
└── UnityClient/
    └── Assets/Scripts/     # Unity C# gameplay client
```

---

## Services In Depth

### SharedLibrary

**Target:** `netstandard2.1` — consumed by every server project *and* the Unity client.

`NetworkPackets.cs` is the protocol contract. It defines every packet exchanged over the wire, typed into two groups:

| Direction | Packets |
|-----------|---------|
| Client → Server | `PlayerInputPacket`, `AttackRequestPacket`, `SpellCastRequestPacket`, `ShootRequestPacket`, `GearSetSwapRequestPacket`, `EquipItemRequestPacket`, `AuthTicketPacket`, `GroundItemPickupRequestPacket` |
| Server → Client | `EntityPositionPacket`, `EntityHealthPacket`, `CombatEventPacket`, `AoEHitEventPacket`, `StatusEffectAppliedPacket`, `StatusEffectRemovedPacket`, `ProjectileSpawnPacket`, `ProjectileDestroyPacket`, `EntitySpawnPacket`, `PlayerGraceDisconnectPacket`, `PlayerReconnectedPacket` |

Movement input uses **quantized `sbyte` axes** (`-127..127` → `-1..1`). This eliminates floating-point NaN/Inf and ensures identical dequantization on client and server.

After build, a `CopyToUnity` MSBuild target automatically copies `SharedLibrary.dll` and `LiteNetLib.dll` into `UnityClient/Assets/Plugins/` so the Unity project always stays in sync.

---

### ProfileServer

**Port:** `9060` | **Protocol:** HTTP/REST (ASP.NET Core minimal API) | **Storage:** PostgreSQL (Dapper)

Manages persistent player data outside of live matches.

#### Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET`  | `/characters/{accountId}` | List all characters for an account |
| `POST` | `/characters` | Create a character (`{ accountId, name, classId }`) |
| `DELETE` | `/characters/{accountId}/{characterId}` | Delete a character (ownership verified) |
| `GET`  | `/crafting/recipes` | Full recipe catalog |
| `POST` | `/crafting/craft` | Execute a crafting recipe (`{ accountId, recipeId }`) |

Character creation enforces:
- Name: 1–24 chars, letters/digits/spaces/hyphens only
- Max **4 characters** per account
- Globally unique names (case-insensitive)
- Transactional insert with automatic rollback on failure

> **TODO (noted in source):** JWT bearer authentication middleware is not yet wired in — all routes are currently unauthenticated.

---

### LobbyServer

**Port:** `9040` | **Protocol:** LiteNetLib (UDP) | **Storage:** PostgreSQL + Redis

Handles everything before a player enters a live match.

#### Flow

```
Client connects → LobbyNetworkManager
        │
        ▼
PlayerAuthService.TryAuthenticateAsync()
  - Queries PostgreSQL for player row
  - Runs BCrypt.Verify (constant-time even when username is not found)
  - Returns PlayerProfile on success
        │
        ▼
MatchmakingQueue.Enqueue(player)
  - FIFO, thread-safe
  - Sends periodic queue-status updates (configurable interval, default 2 s)
        │
        ▼ (when MatchSize players queued, default 2)
MatchmakingQueue.TryFormMatch()
  - Splits players: first half → FactionId.Alpha, second half → FactionId.Beta
        │
        ▼
TicketIssuer.Issue(playerId, playerName, faction, allowedSpellIdsCsv)
  - Builds canonical payload: playerId|name|faction|spells|issuedAt|expiresAt|nonce
  - Signs with HMAC-SHA256 using ARENA_TICKET_SECRET
  - Default ticket lifetime: 30 s (configurable)
        │
        ▼
AuthTicketPacket sent to each client → client presents it to GameServer
```

The lobby also broadcasts the arena IP/port to each matched player so the client knows where to connect next.

---

### GameServer

**Port:** `9050` | **Protocol:** LiteNetLib (UDP) | **Storage:** Redis + PostgreSQL

The authoritative real-time simulation. All game state mutations happen here; clients only submit *intents*.

#### ArenaInstance

`ArenaInstance` is the central container for one live match (or zone). It:

- Owns a `List<PlayerSession>` and peer/entity lookup dictionaries
- Runs the 30 Hz fixed-tick game loop on the main thread
- Drains three `ConcurrentQueue`s and one `ConcurrentDictionary` per tick:
  - `_latestInputByPeer` — latest movement intent per client (last-wins)
  - `_attackQueue` — melee attack requests
  - `_spellQueue` — spell cast requests
  - `_shootQueue` — projectile shoot requests
- Delegates physics to `MovementSystem`, `CombatSystem`, and `ProjectileSystem`
- Calls `BroadcastState()` at the end of every tick
- Supports **Dota 2-style grace-period reconnect**: disconnected sessions are kept alive as stationary ghosts for `RejoinGraceTicks` ticks; the peer is reattached on rejoin without creating a new entity

#### ZoneDescriptor

A `ZoneDescriptor` is injected at construction and acts as the single source of truth for:
- Map bounds and spawn points per faction
- View radius (interest management)
- Win condition (`IWinCondition`)
- Rejoin grace tick count

This lets the same `ArenaInstance` code host an arena match or an open-world MMO zone without any mode flags.

#### Interest Management

`BroadcastState()` uses a **view-radius filter** (`IInterestFilter`) — only packets for entities within `ViewRadius` units are sent to each client. The view radius is pre-squared (`_viewRadiusSqr`) to avoid a `sqrt` on every entity pair every tick.

#### Projectile System

Ranged attacks (`ShootRequestPacket`) spawn a `ProjectileState` on the server. Each tick `ProjectileSystem` advances all active projectiles, checks collisions, and emits `ProjectileSpawnPacket` / `ProjectileDestroyPacket`. The client interpolates visual movement independently between server ticks.

#### Grace-Period Reconnect

```
Player disconnects
       │
       ▼
session.Peer = null
_gracePeriodSessions[accountId] = (session, currentTick + RejoinGraceTicks)
PlayerGraceDisconnectPacket → all peers
       │
       ▼  (player reconnects and presents same ticket)
TryValidateForRejoin() — HMAC + expiry checked, nonce replay skipped
       │  (nonce was legitimately consumed on first connect)
       ▼
session.Peer = newPeer
PlayerReconnectedPacket → all peers
Full state-sync → rejoining player
```

---

### UnityClient

The Unity project lives in `UnityClient/Assets/Scripts/`. It consumes `SharedLibrary.dll` and `LiteNetLib.dll` directly from the `Plugins/` folder (auto-copied on every server build). The client is responsible for:

- Rendering entities and interpolating positions between server ticks
- Sending quantized movement input every frame (client-side prediction optional)
- Displaying reconciliation corrections from `EntityPositionPacket.AcknowledgedTick`
- Managing the auth flow: lobby login → ticket receipt → GameServer connection

---

## End-to-End Player Flow

```
1. Launch Unity Client
         │
         ▼
2. Character Select (HTTP GET /characters/{accountId} → ProfileServer)
         │
         ▼
3. Connect to LobbyServer (UDP :9040)
   Send credentials (playerName + bcrypt token)
         │
         ▼
4. LobbyServer authenticates via PostgreSQL (BCrypt, constant-time)
   Player enters MatchmakingQueue
         │
         ▼
5. Queue fills (MatchSize = 2 by default)
   Factions assigned: Alpha / Beta
   HMAC-signed AuthTicketPacket issued (30 s TTL)
         │
         ▼
6. Client connects to GameServer (UDP :9050)
   Sends AuthTicketPacket immediately
         │
         ▼
7. GameServer.AuthTicketValidator verifies:
   - Shape/sanity checks
   - Clock window (±5 s skew allowed)
   - HMAC-SHA256 signature
   - Nonce not previously seen (replay protection)
   - Allowed spell list parsed
         │
         ▼
8. PlayerSession created; entity spawned; EntitySpawnPacket → all peers
   Profile hydrated from PostgreSQL (stats, gear)
         │
         ▼
9. Live match (30 Hz simulation)
   Client sends intents → server validates → broadcasts authoritative state
         │
         ▼
10. Match ends (IWinCondition met)
    Results persisted via MatchDataService (Redis + PostgreSQL)
```

---

## Gameplay Simulation Loop

The game loop runs at a fixed **30 Hz** (`TickRate = 30`, `DeltaTime = 1/30 s`). Each tick:

```
RunGameLoop()
 ├── NetworkManager.PollEvents()          // drain OS receive buffer
 ├── DrainClientIntents()
 │    ├── _latestInputByPeer   → MovementSystem
 │    ├── _attackQueue         → CombatSystem
 │    ├── _spellQueue          → CombatSystem
 │    ├── _shootQueue          → ProjectileSystem
 │    └── _equipItemQueue / _pickupQueue
 ├── ProcessTick()
 │    ├── Expire grace-period sessions
 │    ├── MovementSystem.ProcessMovement()
 │    │    └── ValidateDelta, ClampToBounds, ApplyPosition
 │    ├── CombatSystem.ProcessAttacks()
 │    │    └── Melee, Spell (AoE / Single-target / MeleeSplash)
 │    ├── CombatSystem.TickStatusEffects()
 │    │    └── DoT/HoT application, expiry
 │    ├── ProjectileSystem.Tick()
 │    │    └── Advance positions, collision, destroy
 │    └── IWinCondition.Check()
 ├── BroadcastState()
 │    ├── EntityPositionPacket per visible entity
 │    ├── EntityHealthPacket (filtered by visibility)
 │    ├── CombatEventPackets
 │    ├── StatusEffect packets
 │    └── Projectile packets
 └── Frame regulation (sleep to maintain 33 ms/tick)
```

All game state is mutated **only** on this single loop thread. The `ConcurrentQueue`/`ConcurrentDictionary` input buffers are the only shared data structures between the network I/O path and the simulation.

---

## Networking & Packet Reference

All network I/O uses **LiteNetLib 2.1.4** over UDP with optional reliability layers.

### Client → Server

| Packet | Delivery | Notes |
|--------|----------|-------|
| `AuthTicketPacket` | `ReliableOrdered` | Sent once immediately after connect |
| `PlayerInputPacket` | `Unreliable` | Sent every tick; `InputX`/`InputY` are `sbyte` (-127..127) |
| `AttackRequestPacket` | `ReliableOrdered` | Carries monotonic `ActionSequenceId` |
| `SpellCastRequestPacket` | `ReliableOrdered` | Server derives `TargetType` from `SpellDatabase`; never trusts client |
| `ShootRequestPacket` | `ReliableOrdered` | Server re-normalizes aim direction |
| `GearSetSwapRequestPacket` | `ReliableOrdered` | Latest-wins per tick |
| `EquipItemRequestPacket` | `ReliableOrdered` | Queued; up to 7 drained per tick |
| `GroundItemPickupRequestPacket` | `ReliableOrdered` | Resolved after movement |

### Server → Client

| Packet | Notes |
|--------|-------|
| `EntitySpawnPacket` | Sent to all peers on join, and to joiner for existing peers |
| `EntityPositionPacket` | Every tick; includes `ServerTick` + `AcknowledgedTick` for client reconciliation |
| `EntityHealthPacket` | Filtered — allies/enemies see different data |
| `CombatEventPacket` | Melee or single-target spell hit |
| `AoEHitEventPacket` | One packet per entity hit in an AoE; client groups by `CasterId+SpellId` |
| `StatusEffectAppliedPacket` / `StatusEffectRemovedPacket` | Filtered by `StatusEffectVisibility` |
| `ProjectileSpawnPacket` | Client uses `Speed` + `DirectionX/Y` to interpolate visuals |
| `ProjectileDestroyPacket` | Includes `HitSomething` flag |
| `PlayerGraceDisconnectPacket` | Peer dropped; grace period active |
| `PlayerReconnectedPacket` | Peer rejoined |

---

## Security Model

### Ticket-Based Authentication

The lobby and game server share a single secret (`ARENA_TICKET_SECRET`). The lobby **signs** tickets; the game server **verifies** them. The secret never leaves environment variables.

```
Lobby signs:   HMAC-SHA256( "playerId|name|faction|spells|issuedAt|expiresAt|nonce" )
Arena checks:  same canonical string, same key, constant-time comparison
```

**Nonce replay protection:** Every nonce is stored in a `ConcurrentDictionary` after first use. A replayed ticket with a known nonce is rejected immediately. Nonces are purged once their ticket's expiry window passes.

**Rejoin exception:** When a player reconnects within the grace period they re-present the same ticket. The arena checks that the `AccountId` is in the grace-period set (server-side proof of prior legitimate connect) before skipping the nonce check. HMAC and expiry are still verified.

### IntentGuard — Runtime Anti-Abuse

Every intent from a client passes through `IntentGuard` before entering the simulation:

| Check | Detail |
|-------|--------|
| **Auth gate** | Unknown peers are dropped immediately |
| **Tick skew** | Rejected if `packetTick < serverTick - 2` or `> serverTick + 5` |
| **Token bucket** | Movement: 60 /s refill, 30 burst. Actions: 20 /s refill, 10 burst |
| **Monotonic sequence** | Attack/Spell/Shoot `ActionSequenceId` must strictly increase per peer |
| **Queue depth** | Per-peer max 32 queued actions; global max 1024 per action type |
| **Violation score** | Incremented on each violation; peer disconnected at score ≥ 80 |

### InputSanitizer

Before any packet enters `IntentGuard`, `InputSanitizer` validates its shape (no NaN/Inf floats, valid ranges). Malformed packets are dropped and logged via `SecurityTelemetry`.

### BCrypt Credentials (LobbyServer)

`PlayerAuthService` runs `BCrypt.Verify` even when the username is not found (using a pre-computed dummy hash). This prevents **username enumeration via timing side-channel**.

---

## Database Schema Overview

The PostgreSQL database `arenammo` is shared across all three services.

| Table | Owner | Description |
|-------|-------|-------------|
| `players` | LobbyServer | `id`, `display_name`, `allowed_spell_ids`, `password_hash` (bcrypt) |
| `characters` | ProfileServer | `character_id`, `account_id`, `name`, `class_id`, `created_at` |
| `crafting_recipes` | ProfileServer | `recipe_id`, `name`, `output_definition_id`, `ingredients_json`, `output_stats_json` |

Redis is used by the LobbyServer for pub/sub coordination and by the GameServer for pre-match player profile caching and post-match result streaming.

---

## Configuration

All secrets **must** be supplied via environment variables. Configuration files provide non-secret defaults.

### `ARENA_TICKET_SECRET` (required by LobbyServer + GameServer)

```bash
export ARENA_TICKET_SECRET="your-very-long-random-secret"
```

### GameServer — `appsettings.json`

| Key | Default | Description |
|-----|---------|-------------|
| `Arena:Port` | `9050` | UDP listen port |
| `ConnectionStrings:Redis` | `localhost:6379` | Redis connection |
| `ConnectionStrings:Postgres` | `localhost:5432/arenammo` | PostgreSQL connection |

### LobbyServer — `appsettings.json`

| Key | Default | Description |
|-----|---------|-------------|
| `Lobby:Port` | `9040` | UDP listen port |
| `Lobby:MatchSize` | `2` | Players required to start a match (must be even) |
| `Lobby:TicketLifetimeMs` | `30000` | Auth ticket validity window (ms) |
| `Lobby:QueueStatusIntervalMs` | `2000` | How often queue-position updates are pushed to clients |
| `Arena:Ip` | `127.0.0.1` | GameServer IP sent to clients on match start |
| `Arena:Port` | `9050` | GameServer port sent to clients on match start |

### ProfileServer — `appsettings.json`

| Key | Default | Description |
|-----|---------|-------------|
| `ProfileServer:Port` | `9060` | HTTP listen port |
| `ConnectionStrings:Postgres` | `localhost:5432/arenammo` | PostgreSQL connection |

---

## Running Locally

### Prerequisites

- [.NET 7 SDK](https://dotnet.microsoft.com/download/dotnet/7.0)
- PostgreSQL 14+
- Redis 7+
- (Optional) Unity 2022 LTS for the client

### 1. Set the shared secret

```bash
export ARENA_TICKET_SECRET="change-me-in-production"
```

### 2. Start dependencies

```bash
# PostgreSQL
pg_ctl start

# Redis
redis-server
```

### 3. Run each server (separate terminals)

```bash
# ProfileServer (HTTP REST)
dotnet run --project ArenaMMO/ProfileServer/ProfileServer.csproj

# LobbyServer
dotnet run --project ArenaMMO/LobbyServer/LobbyServer.csproj

# GameServer
dotnet run --project ArenaMMO/GameServer/GameServer.csproj
```

Default ports: ProfileServer `:9060`, LobbyServer `:9040`, GameServer `:9050`.

### 4. Build SharedLibrary (copies DLLs to Unity)

```bash
dotnet build ArenaMMO/SharedLibrary/SharedLibrary.csproj
```

This triggers the `CopyToUnity` MSBuild target and places `SharedLibrary.dll` and `LiteNetLib.dll` into `UnityClient/Assets/Plugins/` automatically.

---

## Testing

The `GameServer.Tests` project provides a production-grade **integration test harness** that runs the full 30 Hz game loop in-process without any UDP networking. See [`GameServer.Tests/README.md`](GameServer.Tests/README.md) and [`GameServer.Tests/ARCHITECTURE.md`](GameServer.Tests/ARCHITECTURE.md) for deep-dive documentation.

### Run all tests

```bash
dotnet test ArenaMMO/GameServer.Tests/GameServer.Tests.csproj
```

### Run a specific class

```bash
dotnet test ArenaMMO/GameServer.Tests/GameServer.Tests.csproj \
  --filter "FullyQualifiedName~MovementIntegrationTests"
```

### Watch mode

```bash
dotnet watch --project ArenaMMO/GameServer.Tests/GameServer.Tests.csproj test
```

### How the harness works

```
Test Thread (xUnit)
  │
  │  PseudoClient.SendMovementIntent(...)
  ▼
GameServerTestHost
  │  manages fake clients, drains intents,
  │  runs ArenaInstance on background thread
  ▼
ArenaInstance (30 Hz)
  └── ProcessTick() → BroadcastState()
         │
         └── Semaphore released → WaitForTicksAsync() returns
                                    [test thread resumes & asserts]
```

`PseudoClient` replaces real UDP peers; packets are stored in thread-safe collections that tests inspect directly. The tick semaphore guarantees the test thread never reads state mid-tick.

### Current test coverage

| Test | What it verifies |
|------|-----------------|
| `Movement_ValidMoveIntent_PositionUpdatedAndBroadcast` | Position advances, speed limit respected, broadcast delivered |
| `Movement_CheatDetection_IllegalTeleportRejected` | Server clamps excessive deltas; reconciliation sent to client; other players see correct position |
| `Movement_DiagonalInput_NormalizedAndBounded` | Diagonal movement doesn't exceed per-axis speed via normalization |
| `Movement_MultipleClients_IndependentMovement` | Two simultaneous clients move independently with no cross-talk |

---

## Technology Stack

| Component | Technology |
|-----------|-----------|
| All servers | .NET 7, C# 11 |
| Realtime networking | [LiteNetLib 2.1.4](https://github.com/RevenantX/LiteNetLib) (UDP) |
| ProfileServer HTTP | ASP.NET Core Minimal API |
| ORM / SQL | Dapper + Npgsql |
| Password hashing | BCrypt.Net-Next (cost ≥ 12 recommended) |
| Ticket signing | HMAC-SHA256 (System.Security.Cryptography) |
| Distributed cache | StackExchange.Redis |
| Unit / integration tests | xUnit 2.6, FluentAssertions 6.12 |
| Client engine | Unity (C#) |
| Shared protocol library | netstandard2.1 |
