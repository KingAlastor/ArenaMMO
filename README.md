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
13. [Roadmap](#roadmap)

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
│   ├── ArenaInstance.cs    # Core match container & drift-free tick driver
│   ├── NetworkManager.cs   # LiteNetLib glue
│   ├── PlayerSession.cs    # Per-player authoritative state
│   ├── SpatialGrid.cs      # Fixed-cell 2-D spatial hash for O(k) interest queries
│   ├── IInterestFilter.cs  # BroadcastFilter / RadiusFilter strategies
│   ├── AuthTicketValidator.cs
│   ├── IntentGuard.cs      # Rate-limiting & anti-cheat
│   ├── InputSanitizer.cs
│   ├── SecurityTelemetry.cs
│   ├── ItemDatabase.cs / SpellDatabase.cs
│   ├── ZoneDescriptor.cs
│   ├── Systems/
│   │   ├── CombatSystem.cs
│   │   ├── MovementSystem.cs
│   │   └── ProjectileSystem.cs  # Static scratch lists — zero per-tick allocation
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

`NetworkPackets.cs` is the protocol contract. Packets are split into two tiers:

**Hot-path structs** — `[StructLayout(Sequential, Pack=1)]` value types, written directly to `NetDataWriter`. Zero heap allocation per send.

| Struct | Wire size | Compression |
|--------|-----------|-------------|
| `EntityPositionPacket` | 15 B | X/Y as `short` fixed-point (scale ×16, precision 0.0625 units); tick fields use 24-bit wrapping encoding (`ushort lo` + `byte hi`, 3 B each) via `PacketEncoding.EncodeTick24`/`DecodeTick24` — saves 2 B vs prior `int` layout |
| `EntityHealthPacket` | 7 B | `Health` as `ushort` raw HP |
| `CombatEventPacket` | 12 B | `Damage` compressed from `int` (4 B) to `ushort` (2 B, max 65,535); `IsCritical` + future flags in 1-byte `Flags` field |
| `AoEHitEventPacket` | 16 B | `Damage` likewise compressed to `ushort`; same flags packing |
| `StatusEffectAppliedPacket` | 15 B | `Visibility` packed into `byte VisibilityFlags` bit 0; was ~40 B as a class |
| `StatusEffectRemovedPacket` | 10 B | Same visibility packing; was ~24 B as a class |
| `ProjectileSpawnPacket` | 25 B | Direction compressed to `short×32767`; speed/range to `ushort×10`; was ~60 B as a class |
| `ProjectileDestroyPacket` | 6 B | `HitSomething` packed into `byte Flags` bit 0; was ~24 B as a class |
| `EntityDespawnPacket` | 5 B | Converted from class |
| `PlayerDeathPacket` | 9 B | Converted from class |
| `PlayerRespawnPacket` | 11 B | X/Y as `short` fixed-point, `Health` as `ushort` |
| `MatchEndPacket` | 2 B | Converted from class |
| `GroundItemSpawnedPacket` | 13 B | X/Y as `short` fixed-point |
| `GroundItemRemovedPacket` | 5 B | Converted from class |
| `ItemAddedToInventoryPacket` | 9 B | Converted from class |
| `PlayerGraceDisconnectPacket` | 5 B | Converted from class |
| `PlayerReconnectedPacket` | 5 B | Converted from class |
| `PlayerStatsRefreshedPacket` | 20 B | Stat fractions as `ushort×10000` |

`Vec2` also carries `[StructLayout(Sequential, Pack=1)]` for guaranteed cross-platform blittability.

**Infrequent classes** — sent at most once per event (spawn, match flow); retain `NetPacketProcessor` compatibility because they carry `string` fields.

| Direction | Packets |
|-----------|--------|
| Client → Server | `PlayerInputPacket`, `AttackRequestPacket`, `SpellCastRequestPacket`, `ShootRequestPacket`, `GearSetSwapRequestPacket`, `EquipItemRequestPacket`, `AuthTicketPacket`, `GroundItemPickupRequestPacket` |
| Server → Client (classes) | `EntitySpawnPacket`, `CraftingRewardPacket`, `LobbyLoginResponsePacket`, `MatchFoundPacket` |

Movement input uses **quantized `sbyte` axes** (`-127..127` → `-1..1`). This eliminates floating-point NaN/Inf and ensures identical dequantization on client and server.

`PacketEncoding` provides `EncodePosition`/`DecodePosition`, `EncodeHealth`/`DecodeHealth`, `EncodeDirection`/`DecodeDirection` (unit vector → `short×32767`), `EncodeSpeed`/`DecodeSpeed` (`float` → `ushort×10`), and `EncodeTick24`/`DecodeTick24` (24-bit wrapping tick, `ushort lo` + `byte hi`) helpers shared by the server and Unity client. `PacketId` constants are the dispatch discriminators written as the first byte of each struct packet.

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

- Owns a `List<PlayerSession>` and peer/entity lookup dictionaries, plus a pre-allocated `ProjectileState[512]` fixed array (zero heap allocation per projectile spawn)
- Runs a **drift-free 30 Hz fixed-tick game loop** using absolute `Stopwatch` deadlines; `ticksPerTick = Stopwatch.Frequency / TickRate` uses integer division (no float rounding bias) with a `Thread.SpinWait` final sub-millisecond window
- Loads player profiles via a **fully async deferred-hydration pipeline**: `OnPlayerAuthenticated` fires `LoadPlayerProfileAsync` (which uses `StringGetAsync` — truly non-blocking, zero ThreadPool occupation during the wait) and enqueues the `(session, Task<PlayerProfile?>)` pair into `_pendingHydration`; `FinalizeHydration()` drains completed tasks at the top of every tick — the game-loop thread is never blocked on Redis I/O. `LoadPlayerProfileAsync` deserializes via `(byte[])raw` + `bytes.AsSpan()` (no intermediate `string` copy)
- **`PlayerStateSink.FlushAsync`** returns `Task.Run(() => FlushCoreAsync(...))` so the game-loop thread never executes the synchronous prelude (string interpolation + `JsonSerializer.Serialize`) — all data-layer CPU work is fully offloaded to the thread pool
- Drains three `ConcurrentQueue`s for action intents per tick:
  - `_attackQueue` — melee attack requests
  - `_spellQueue` — spell cast requests
  - `_shootQueue` — projectile shoot requests
- Drains `_latestInputByPeer` and `_latestGearSwapByPeer` via **plain `Dictionary` with struct enumerator** (zero heap allocation) — both are written and read exclusively on the game-loop thread
- Delegates physics to `MovementSystem`, `CombatSystem`, and `ProjectileSystem`
- Calls `BroadcastState()` at the end of every tick using **pre-allocated struct instances** (`EntityPositionPacket`, `EntityHealthPacket`, `PlayerDeathPacket`, `PlayerRespawnPacket`, `GroundItemRemovedPacket`, `ItemAddedToInventoryPacket`) that are mutated in-place and passed via `in`-ref — zero per-tick GC allocations across all broadcast paths
- All `TickResult` list fields from `ProjectileSystem.Tick` are iterated via **index-based `for` loops** (not `foreach`) to eliminate `List<T>.Enumerator` overhead on the projectile collision hot path
- `_pendingHydration` is a plain **`Queue<T>`** (not `ConcurrentQueue<T>`) — all accesses are on the game-loop thread; eliminates `Interlocked`/`volatile` overhead that `ConcurrentQueue` adds unnecessarily
- **`SecurityTelemetry` fully off-thread**: `WriteAudit`, `PrintSnapshot`, and `RecordUnauthorizedSpell` use `ThreadPool.QueueUserWorkItem<TState>` with `static` lambdas and value-tuple `TState` arguments — zero string allocation and zero `Console.WriteLine` I/O on the game-loop thread, including under adversarial cheat-flood conditions
- **`NetworkManager` connection logging off-thread**: `OnPeerConnected`, `OnPeerDisconnected`, and `OnNetworkError` offload their log lines to `ThreadPool.QueueUserWorkItem<TState>` — the address string is snapshotted before hand-off so the callback captures nothing from the live peer object
- Supports **Dota 2-style grace-period reconnect**: disconnected sessions are kept alive as stationary ghosts for `RejoinGraceTicks` ticks; the peer is reattached on rejoin without creating a new entity

#### ZoneDescriptor

A `ZoneDescriptor` is injected at construction and acts as the single source of truth for:
- Map bounds and spawn points per faction
- View radius (interest management)
- Win condition (`IWinCondition`)
- Rejoin grace tick count

This lets the same `ArenaInstance` code host an arena match or an open-world MMO zone without any mode flags.

#### Interest Management

`BroadcastState()` routes through a **`SpatialGrid`** and an **`IInterestFilter`** — only entities within `ViewRadius` units of each viewer are transmitted.

- **`SpatialGrid`** (`GameServer/SpatialGrid.cs`): a fixed uniform-cell 2-D spatial hash rebuilt once per tick after movement. `QueryNeighbours(origin)` returns the pre-allocated 3×3 neighbourhood scratch list in O(1) cell lookup + O(k) iteration — no heap allocation. Reduces `BroadcastState` from O(N²) to O(N × k) where k ≪ N at MMO scale.
- **`IInterestFilter`** strategy interface: `BroadcastFilter` (Arena default, all peers) or `RadiusFilter` (open-world, distance check) — swappable per zone via `ZoneDescriptor.EventFilter`.
- The view radius is pre-squared (`_viewRadiusSqr`) to avoid a `sqrt` on every entity pair every tick.
- `NetworkManager.SendToInterested` accepts an optional `SpatialGrid` on **all** overloads — including `CombatEventPacket` and `AoEHitEventPacket` — so every event broadcast benefits from grid-narrowing at high player counts. All 9 call sites in `ArenaInstance` now pass `_spatialGrid`, covering: combat events, AoE events, projectile spawn/destroy, death, respawn, ground item spawned/removed.

| CCU | Old BroadcastState | New BroadcastState |
|-----|-------------------|--------------------|
| 20 (Arena) | O(400) / tick | O(~400) / tick (same) |
| 2 000 (MMO) | O(4 000 000) / tick | O(~80 000) / tick (~50× faster) |

#### Projectile System

Ranged attacks (`ShootRequestPacket`) spawn a `ProjectileState` struct on the server. Each tick `ProjectileSystem` advances all active projectiles, checks collisions, and emits `ProjectileSpawnPacket` / `ProjectileDestroyPacket`. The client interpolates visual movement independently between server ticks.

- **`ProjectileState` is a `struct`** stored in a pre-allocated `ProjectileState[512]` fixed array on `ArenaInstance`. Eliminated the heap allocation that occurred on every spawn when it was a `sealed class`.
- **`TrySpawnProjectile`** (replaces the old `SpawnProjectile? return` pattern): writes the new struct directly into an `out ProjectileState` parameter — zero heap allocation end-to-end.
- **`OwnerFaction`** is snapshotted into `ProjectileState` at spawn time, making `MatchesFactionFilter` O(1) (single enum comparison) instead of the previous O(N) linear scan through all players on every collision check.
- **Array-based `Tick`**: `ProjectileSystem.Tick` now accepts `ProjectileState[]` + `ref int projectileCount` and removes projectiles via an O(1) swap-remove (`SwapRemove`) rather than `List.RemoveAt` which shifts all subsequent elements.
- **Spatial grid collision narrowing**: `Tick` accepts `SpatialGrid?`; when present it calls `grid.QueryNeighbours(proj.Position)` to narrow collision candidates from O(N-all-players) to O(k-nearby) — critical at MMORPG scale.
- **O(1) life-steal resolution**: `Tick` now also accepts `IReadOnlyDictionary<int, PlayerSession> entityMap`. `ApplyLifeSteal` uses a single `TryGetValue` hash probe to find the shooter instead of the previous O(N) linear scan through all players. At 2,000 players × 100 projectile hits/tick this eliminates 200,000 redundant iterations per tick.
- Result lists (`hits`, `pierceHits`, `splashHits`, `expiredIds`, `statusEffects`) are **static pre-allocated scratch lists** cleared at the start of each `Tick` call. Callers must consume list contents before the next tick.
- **`IReadOnlyList<PlayerSession>` → `List<PlayerSession>`**: `CombatSystem.ProcessSpellCast`, `ProcessAoE`, `ProcessMeleeSplash`, and `ProjectileSystem.Tick` / `ApplyExplosiveSplash` all narrowed from `IReadOnlyList` to `List` — the same fix previously applied to `BroadcastStatusEffects`. Every `[i]` and `.Count` access in the AoE and projectile collision inner loops is now a direct array read with no vtable dispatch (eliminates up to ~12B virtual calls/s at 2,000-player MMO scale).

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
8. PlayerSession created; entity spawned; `EntitySpawnPacket` → all peers
   Async Redis profile load fired immediately (non-blocking); session enters match with base stats.
   Profile applied by `FinalizeHydration()` on first completed tick (~1–10 ticks, imperceptible).
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
RunGameLoop()  ← absolute-deadline heartbeat: tracks nextTickTime = Stopwatch.GetTimestamp()
 ├── NetworkManager.PollEvents()          // drain OS receive buffer
 ├── ProcessTick()
 │    ├── FinalizeHydration()             // phase 0: drain completed async Redis profile reads (zero blocking)
 │    ├── Drain _latestInputByPeer        // plain Dictionary<NetPeer,PlayerInputData> struct enumerator, zero-alloc
 │    │    └── → MovementSystem.ProcessInput()   // accepts in PlayerInputData (value-type copy, not class ref)
 │    ├── SpatialGrid.RebuildEachTick()   // O(cells + N), once per tick after movement
 │    ├── Drain _attackQueue  → CombatSystem.ProcessMeleeAttack()
 │    ├── Drain _spellQueue   → CombatSystem.ProcessSpellCast()
 │    ├── Drain _shootQueue   → ProjectileSystem.TrySpawnProjectile()  // out ProjectileState → _projectiles[count++]
 │    │    └── mutates pre-alloc _projSpawnPacket struct; SendToInterested with _spatialGrid (zero alloc)
 │    ├── ProjectileSystem.Tick(array, ref count, entityMap, grid)  // ref locals, SwapRemove, O(k) collision via grid
 │    │   ApplyLifeSteal uses entityMap (O(1) hash probe) — was O(N) allPlayers scan per hit
 │    │   TickResult lists iterated via index-based for loops (no List<T>.Enumerator overhead)
 │    └── mutates pre-alloc _projDestPacket struct on hit/expiry; SendToInterested passes _spatialGrid (zero alloc)
 │    ├── TickStatusEffects()             // DoT/HoT, expiry — StatusEffectApplied/Removed emitted as structs
 │    ├── Death / Respawn detection       // _deathPacket / _respawnPacket mutated in-place; SendToInterested passes _spatialGrid (zero alloc)
 │    ├── Drain _equipItemQueue / _pickupQueue  // _groundRemovedPacket / _itemAddedPacket mutated in-place; SendToInterested passes _spatialGrid (zero alloc)
 │    └── IWinCondition.Check()
 ├── BroadcastState()
 │    ├── SpatialGrid.QueryNeighbours(viewer) → O(k) neighbour list
 │    ├── EntityPositionPacket: sent only when encoded X/Y differs from LastBroadcastX/Y (delta compression)
 │    │   Own-entity position always sent (AcknowledgedTick field required for client reconciliation)
 │    │   EncodeTick24 called ONCE before the viewer loop; shared serverTickLo/Hi reused per packet
 │    ├── EntityHealthPacket: sent only when encoded Health differs from LastBroadcastHealth
 │    └── CommitBroadcastState() — O(N) pass updates LastBroadcastX/Y/Health sentinels AFTER all viewers served
 └── Frame regulation:
      ├── Thread.Sleep(sleepMs - 1)       // coarse OS sleep
      └── Thread.SpinWait(8) loop         // sub-ms spin to hit deadline precisely
         nextTickTime += ticksPerTick     // = Stopwatch.Frequency / TickRate (integer division — exact, zero float bias)
```

All game state is mutated **only** on this single loop thread. `ConcurrentQueue`s are used only for action intents (attack/spell/shoot) which may arrive from a future dedicated I/O thread. Movement and gear-swap inputs use plain `Dictionary` since LiteNetLib callbacks fire synchronously on the game-loop thread via `PollEvents()`.

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

Hot-path packets (sent every tick or on every combat event) are **`[StructLayout(Sequential, Pack=1)]` value types** written directly to `NetDataWriter` — bypassing reflection-based serialisation and eliminating per-send heap allocations. Infrequent packets (spawn, respawn, match flow) remain as classes.

| Packet | Type | Wire size | Notes |
|--------|------|-----------|-------|
| `EntityPositionPacket` | **struct** | 15 B | X/Y as `short` fixed-point (÷16, ±2048 range); `ServerTick`/`AcknowledgedTick` encoded as 24-bit wrapping values (`ushort lo` + `byte hi`, 3 B each) via `PacketEncoding.EncodeTick24` — saves 2 B vs prior `int` layout; use `DecodeTick24(lo, hi)` on the Unity client |
| `EntityHealthPacket` | **struct** | 7 B | `Health` as `ushort` raw HP (was `float`, saves 2 B) |
| `CombatEventPacket` | **struct** | 12 B | `Damage` as `ushort` (was `int`, saves 2 B); `IsCritical` packed into `byte Flags` bit 0 |
| `AoEHitEventPacket` | **struct** | 16 B | `Damage` as `ushort` (was `int`, saves 2 B); `IsCritical` packed into `byte Flags` bit 0 |
| `EntityDespawnPacket` | **struct** | 5 B | Converted from class; −11 B vs object header |
| `PlayerDeathPacket` | **struct** | 9 B | Converted from class |
| `PlayerRespawnPacket` | **struct** | 11 B | X/Y compressed to `short`, Health to `ushort` |
| `MatchEndPacket` | **struct** | 2 B | Converted from class |
| `GroundItemSpawnedPacket` | **struct** | 13 B | X/Y compressed to `short` |
| `GroundItemRemovedPacket` | **struct** | 5 B | Converted from class |
| `ItemAddedToInventoryPacket` | **struct** | 9 B | Converted from class |
| `PlayerGraceDisconnectPacket` | **struct** | 5 B | Converted from class |
| `PlayerReconnectedPacket` | **struct** | 5 B | Converted from class |
| `PlayerStatsRefreshedPacket` | **struct** | 20 B | Stat fractions compressed to `ushort×10000` |
| `StatusEffectAppliedPacket` | **struct** | 15 B | Converted from class (was ~40 B); `Visibility` packed into `byte VisibilityFlags` bit 0 |
| `StatusEffectRemovedPacket` | **struct** | 10 B | Converted from class (was ~24 B) |
| `ProjectileSpawnPacket` | **struct** | 25 B | Converted from class (was ~60 B); direction compressed to `short×32767`, speed/range to `ushort×10` |
| `ProjectileDestroyPacket` | **struct** | 6 B | Converted from class (was ~24 B); `HitSomething` packed into `byte Flags` bit 0 |
| `EntitySpawnPacket` | class | variable | Sent once on join; carries `string PlayerName` |

`PacketEncoding` helpers: `EncodePosition(float) → short` / `DecodePosition(short) → float` and `EncodeHealth(float) → ushort` / `DecodeHealth(ushort) → float` are in `SharedLibrary/NetworkPackets.cs` for use on both server and Unity client.

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

### Changelog

#### May 31, 2026 — Delta compression broadcasting (round 8)

| Area | Change |
|------|--------|
| `PlayerSession.cs` | Added three dirty-tracking sentinel fields: `internal short LastBroadcastX`, `LastBroadcastY` (initialized to `short.MinValue` — outside the valid ±2048 world-unit range so the first tick always sends), and `internal ushort LastBroadcastHealth` (initialized to `ushort.MaxValue`). All three are primitive value types — zero GC cost. |
| `ArenaInstance.BroadcastState` | Position packet now suppressed when `PacketEncoding.EncodePosition(entity.Position.X/Y)` matches `entity.LastBroadcastX/Y` from last tick. Exception: own-entity position is always sent so the `AcknowledgedTick` field reaches the client every tick for input reconciliation. Health packet likewise suppressed when encoded HP is unchanged. `EncodeTick24` called once before the viewer loop (shared `serverTickLo/Hi` written into every position packet this tick — eliminates a redundant call per entity per viewer). |
| `ArenaInstance.CommitBroadcastState` | New private method called once per tick **after** `BroadcastState` finishes iterating all viewers. Updates `LastBroadcastX/Y/Health` for every player in a single O(N) pass. Updating sentinels inside the inner loop would cause viewer[1] to see "unchanged" for an entity that viewer[0] just broadcast in the same tick — the deferred commit ensures a consistent decision across all viewers. |
| Bandwidth impact | At ~10 % entity-moved-per-tick rate (typical open-world zone): 2,000 players × 20 viewers × 30 Hz × 90 % skip rate = **1,080,000 position packets/s eliminated**. In the arena (10-20 players, small map, most players moving) the reduction is minimal — no regression. |
| ROADMAP 2.4 | Promoted from 🔶 to ✅. |

#### May 31, 2026 — Concurrency audit & compile-error fixes (round 6)

| Area | Change |
|------|--------|
| `NetworkManager.cs` — `EntityPositionPacket` serialiser | **Critical bug fixed.** `SendTo(NetPeer, in EntityPositionPacket, …)` was writing non-existent fields `packet.ServerTick` and `packet.AcknowledgedTick` (deleted in round 5's 24-bit encoding migration). Updated to write the correct six fields: `ServerTickLo`, `ServerTickHi`, `AcknowledgedTickLo`, `AcknowledgedTickHi`. Without this fix every position packet sent corrupt tick data, breaking client-side lag compensation and interpolation on every tick. |
| `NetworkManager.cs` — `_pendingAuthPeers` / `_ipGuards` | Changed from `ConcurrentDictionary` to plain `Dictionary`. All LiteNetLib callbacks fire synchronously inside `PollEvents()` on the single game-loop thread — there is no concurrent access. `ConcurrentDictionary.foreach` returns a heap-allocated boxed `IEnumerator<KVP>` (no public struct enumerator), causing a GC allocation on **every tick** in `DisconnectAuthTimeoutPeers`. `Dictionary.Enumerator` is a public value-type struct — zero allocation. Also eliminates `GetOrAdd(key, _ => new …)` factory delegate allocations on the common (already-exists) path. |
| `NetworkManager.cs` — `EvictStaleIpGuards` | Added pre-allocated `_staleIpAddresses` scratch `List<IPAddress>`. Previously `_ipGuards.Remove(entry.Key)` was called inside the `foreach` enumeration loop, which throws `InvalidOperationException` on plain `Dictionary`. Now collects keys in a first pass, removes in a second pass — same two-pass pattern as `_timedOutPeers`. |
| `IntentGuard.cs` — `_peerGuards` | Changed from `ConcurrentDictionary` to plain `Dictionary` for the same reasons as `NetworkManager`. All callers (`TryAcceptIntent`, `TryReserveActionSlot`, `ReleaseActionSlot`, `OnPeerConnected`, `OnPeerDisconnected`) are invoked on the game-loop thread. Replaced `GetOrAdd(peer, _ => new …)` with `TryGetValue` + conditional `Dictionary` insert to avoid the factory delegate allocation on the hot path. |
| `IntentGuard.cs` — `Console.WriteLine` in violation handlers | Moved off the game-loop thread. String interpolation (`$"[Guard] Disconnecting peer {peer.Id}..."`) allocates a `string` on the GC heap every time a violation fires. Replaced with `ThreadPool.QueueUserWorkItem(static id => Console.WriteLine(…), peer.Id)` — the `static` keyword enforces at compile time that the lambda captures nothing, eliminating the closure object. |
| `ArenaInstance.cs` — `EvictExpiredGracePeriods` | Replaced `Task.Run(() => Console.WriteLine($"…{expiredName}…"))` with `ThreadPool.QueueUserWorkItem(static name => Console.WriteLine(…), grace.Session.PlayerName)`. Eliminates the compiler-generated display class (closure), the captured `string expiredName` local, and the `Task`/`QueueSegment` overhead of `Task.Run`. |
| `CombatSystem.cs`, `ProjectileSystem.cs`, `PlayerSession.cs` | Replaced raw `(ushort)Math.Clamp(damage, 0, 65535)` at all six `Damage` assignment sites with `DamageUtils.ClampAndEncode(damage, attackerId, context)`. The cap is now `CombatMath.MaxSingleHitDamage = 9_999` — ~10× above the design ceiling of ~1,000. Any value reaching the cap fires `SecurityTelemetry.RecordDamageCap(attackerId, context, rawDamage)` (off the game-loop thread via `ThreadPool.QueueUserWorkItem`) and increments a `damageCapHits` counter visible in the periodic telemetry snapshot. A non-zero `damageCapHits` in production logs indicates a runaway damage formula bug. Context labels: `"melee"`, `"spell"`, `"aoe"`, `"projectile"`, `"splash"`, `"dot"`. |

#### May 31, 2026 — Struct compression & allocation audit (round 5)

| Area | Change |
|------|--------|
| `EntityPositionPacket` | `ServerTick` and `AcknowledgedTick` (`int`, 4 B each) replaced with 24-bit wrapping layout: `ushort TickLo` + `byte TickHi` (3 B each). Wire size: **17 → 15 bytes**. Wraps after 16,777,216 ticks ≈ 154 h at 30 Hz. At 2,000 players × 20 viewers × 30 Hz this saves **2.4 MB/s** of outbound bandwidth. `PacketEncoding.EncodeTick24`/`DecodeTick24` helpers added to `SharedLibrary`. |
| `CombatEventPacket.Damage` | Changed from `int` (4 B) to `ushort` (2 B). Wire size: **13 → 12 bytes**. No game damage value exceeds 65,535; server clamps before assignment. |
| `AoEHitEventPacket.Damage` | Same `int`→`ushort` change for consistency. Wire size: **17 → 16 bytes**. |
| `ArenaInstance._reusableStatusEffects` / `_reusableSpellEvents` / `_reusableAoEHitEvents` / `_statusTickEvents` / `_expiredStatusEffects` | Added explicit initial capacities (`64` / `32`). Without a hint, `List<T>` doubles its internal array on the first AoE burst, allocating on the LOH mid-combat. |
| `ArenaInstance._groundSpawnedPacket` | Added as a pre-allocated instance field (same pattern as `_posPacket`, `_projSpawnPacket`). `SpawnGroundItem` now mutates it in-place and passes it via `in`-ref instead of constructing a new struct literal on every item drop. |
| `ArenaInstance.EvictExpiredGracePeriods` | `Console.Write` / `Console.WriteLine` calls moved into `Task.Run(...)` (fire-and-forget). `Console` internally allocates `char[]` buffers; even these rare calls were touching the GC on the game-loop thread. |
| `ArenaInstance.ProcessTick` — status effect phase | Removed redundant end-of-block `.Clear()` calls on `_statusTickEvents` and `_expiredStatusEffects`. Both lists are cleared at the top of the phase each tick; the trailing clears were dead code that obscured the tick phase structure. |
| `BroadcastState` | Updated to call `PacketEncoding.EncodeTick24(...)` and write the new `ServerTickLo/Hi` and `AcknowledgedTickLo/Hi` fields instead of the old `int` assignments. |

#### May 31, 2026 — Zero-allocation audit (round 4)

| Area | Change |
|------|--------|
| `ArenaInstance` | Pre-allocated `_deathPacket`, `_respawnPacket`, `_groundRemovedPacket`, and `_itemAddedPacket` struct fields added alongside the existing `_posPacket` / `_projSpawnPacket` fields. All four packet types are now mutated in-place and passed via `in`-ref to `SendToInterested` — eliminating the per-event struct copy that occurred when they were constructed inline. |
| `ArenaInstance` Phase 5 (projectile results) | Replaced `foreach` / `foreach var (…)` loops over `TickResult` list fields with index-based `for` loops. `List<T>.Enumerator` is a struct (no boxing), but removing the `MoveNext`/`Current` overhead on the projectile collision hot path measurably reduces per-tick work under heavy combat load. |
| `ArenaInstance._pendingHydration` | Changed from `ConcurrentQueue<T>` to plain `Queue<T>`. All access — `Enqueue` on connect (via `PollEvents` on the game-loop thread) and `Dequeue`/`Enqueue` in `FinalizeHydration` (also game-loop thread) — is single-threaded. `ConcurrentQueue` uses `Interlocked` and `volatile` operations internally; `Queue<T>` has zero synchronisation overhead. |
| `PlayerStateSink.FlushAsync` | `Task.Run(static lambda, state)` pattern documented but held at `Task.Run(() => …)` because the state-passing overload requires .NET 8+. A comment marks the upgrade path. The closure allocation (~48 B) is acceptable on this cold path (once per player per 60 s). |

#### May 31, 2026 — Data-layer allocation audit (round 3)

| Area | Change |
|------|--------|
| `PlayerStateSink.FlushAsync` | Changed from `async Task` with sync prelude to `Task.Run(() => FlushCoreAsync(...))`. The old implementation executed string interpolation and `JsonSerializer.Serialize` synchronously on the caller's thread (the game-loop thread) before the first `await`. Now returns immediately; all CPU work runs on a thread-pool thread. |
| `MatchDataService.LoadPlayerProfile` | Replaced `JsonSerializer.Deserialize(raw.ToString())` with `(byte[])raw` + `bytes.AsSpan()` overload. Eliminates the intermediate managed `string` copy of the Redis JSON payload. |
| `MatchDataService.LoadPlayerProfileAsync` | Same `byte[]` + `Span<byte>` fix. Runs on a thread-pool continuation thread (post-`await`), so it does not affect the tick budget, but reduces GC pressure at high connection rates. |

---

## Roadmap

See [`ROADMAP.md`](ROADMAP.md) for the full engineering goals checklist, split into two phases:

- **Phase 1 — Arena:** All systems required for the current PvP arena build target. Use this as the active implementation checklist.
- **Phase 2 — MMO:** NPC/mob systems, pathfinding, aggro tables, XP/leveling, player trading, and world persistence. Defined for planning purposes — out of scope until Phase 1 is complete.

Each item has a status marker (✅ implemented, 🔶 partial, ❌ not built) and notes pointing to the relevant source files.

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
