# ArenaMMO — Server Engineering Goals & Roadmap

This document is the canonical checklist of every server-side system required for a
production-grade, server-authoritative MMORPG/Arena backend. Each item maps directly
to a specific implementation concern. Status markers reflect the current codebase.

---

## Legend
- ✅ **Implemented** — production-quality code exists in the repo
- 🔶 **Partial** — scaffolding or a first-pass implementation exists; gaps noted
- ❌ **Not yet built** — planned, no code exists

---

# Phase 1 — Arena

> Scope: fixed-size PvP matches with a defined win condition, no persistent world,
> no NPCs. This is the active development target.

---

## 1 · Network & Infrastructure Layer

> The plumbing that keeps the server running safely at 30 Hz and talking to the Unity
> DOTS client.

| # | Goal | Status | Notes |
|---|------|--------|-------|
| 1.1 | **Socket & Connection Management** — LiteNetLib lifecycle (Connect / Disconnect / Timeout detection) for thousands of concurrent connections | ✅ | `NetworkManager.cs`; `_pendingAuthPeers` + `DisconnectAuthTimeoutPeers`; plain `Dictionary` enumerator (zero-alloc). `OnPeerConnected`, `OnPeerDisconnected`, and `OnNetworkError` log lines offloaded to `ThreadPool.QueueUserWorkItem<TState>` with static lambdas — eliminates string allocation and blocking `Console.WriteLine` I/O from the tick thread during high-churn reconnect scenarios. |
| 1.2 | **High-Precision 30 Hz Heartbeat** — absolute `Stopwatch` deadline loop with `Thread.SpinWait` sub-millisecond correction; zero drift or time-lag accumulation | ✅ | `ArenaInstance.RunGameLoop`; `nextTickTime += ticksPerTick` (integer division, no float bias); `Thread.Sleep(ms-1)` + `SpinWait` final window |
| 1.3 | **Packet Serialization & Blitting Engine** — `[StructLayout(Sequential, Pack=1)]` hot-path structs written directly to `NetDataWriter`; zero GC allocation per send | ✅ | All hot-path packets in `SharedLibrary/NetworkPackets.cs`; `PacketEncoding` helpers; quantized `sbyte` axes, fixed-point positions, 24-bit tick encoding. `BroadcastStatusEffects` parameter narrowed from `IReadOnlyList<StatusEffectAppliedPacket>` to `List<StatusEffectAppliedPacket>` — eliminates vtable dispatch on every `.Count` and `[i]` access in AoE combat. `DamageUtils.ClampAndEncode` `context` parameter changed from `string` to `ReadOnlySpan<char>` — string literals at call sites are now zero-allocation stack spans; `SecurityTelemetry.RecordDamageCap` materialises the span to `string` only on the background thread-pool path. `CombatSystem.ProcessSpellCast` / `ProcessAoE` / `ProcessMeleeSplash` and `ProjectileSystem.Tick` / `ApplyExplosiveSplash` parameters narrowed from `IReadOnlyList<PlayerSession>` to `List<PlayerSession>` — eliminates vtable dispatch on every `[i]` and `.Count` access in the AoE and projectile collision inner loops (up to ~12B vtable calls/s eliminated at 2,000-player MMO scale). `SecurityTelemetry.WriteAudit`, `PrintSnapshot`, and `RecordUnauthorizedSpell` fully offloaded to `ThreadPool` via `TState` value-tuples and `static` lambdas — all string interpolation and `Console.WriteLine` I/O now happen off the game-loop thread, including during adversarial violation-flood scenarios. |
| 1.4 | **Thread-Safe Input Queue** — asynchronous client input arrival queued safely for sequential processing on the next 30 Hz tick | ✅ | `_attackQueue`, `_spellQueue`, `_shootQueue`, `_equipItemQueue`, `_pickupQueue` are plain `Queue<T>` drained on the game-loop thread (all LiteNetLib callbacks fire on that thread via `PollEvents`); movement uses `_latestInputByPeer` dictionary |
| 1.5 | **State Sync Caching (Redis / Dapper Separation)** — real-time tick changes in hot RAM; DB saves (Dapper / PostgreSQL) strictly on background thread pool | ✅ | `LivePlayerState` in-memory; `PlayerStateSink.FlushAsync` offloads via `Task.Run`; `FinalizeHydration` drains async Redis reads without blocking the loop |

---

## 2 · Interest Management & Spatial Partitioning

> Without this, large-match state broadcasts will saturate bandwidth and crash the server.

| # | Goal | Status | Notes |
|---|------|--------|-------|
| 2.1 | **Spatial Grid Segmentation** — uniform fixed-cell 2-D spatial hash dividing the world map into virtual grid cells | ✅ | `SpatialGrid.cs`; cell size configurable via `ZoneDescriptor`; rebuilt once per tick after movement (O(cells + N)) |
| 2.2 | **Entity-to-Cell Hashing** — continuous tracking and shifting of player IDs to correct grid cells as they cross world coordinates | ✅ | `SpatialGrid.RebuildEachTick`; entity IDs mapped to `(cellX, cellY)` buckets; stale entries cleared each tick |
| 2.3 | **Observer Lists (Network Bubbles)** — dynamic calculation of which clients are within viewing range (target cell + 8 neighbours) | ✅ | `SpatialGrid.QueryNeighbours(origin)` returns pre-allocated 3×3 scratch list in O(1); `IInterestFilter` strategy (`BroadcastFilter` / `RadiusFilter`) swappable per zone via `ZoneDescriptor.EventFilter`; `_viewRadiusSqr` pre-squared to avoid `sqrt` |
| 2.4 | **Delta Compression Broadcasting** — state updates sent only to relevant observers; entities with no changes filtered out | ✅ | `BroadcastState` skips position packets for entities whose fixed-point encoded X/Y has not changed since last tick; skips health packets when HP encoding is unchanged; own-entity position always sent for client reconciliation. `CommitBroadcastState()` updates `LastBroadcastX/Y/Health` sentinels in one O(N) pass after all viewers are served, ensuring every viewer in a tick sees a consistent changed/unchanged decision. |

---

## 3 · Server-Authoritative Movement & Physics

> The server owns the true position of every entity.

```
[Client Input] ──► [Server Validation] ──► [Grid Check] ──► [State Broadcast]
 "Move Forward"     Is speed legal?          Hit a wall?      "New Position is X"
                    No teleporting?          Apply physics.
```

| # | Goal | Status | Notes |
|---|------|--------|-------|
| 3.1 | **Speed & Teleport Validation** — compare requested movement against max allowed velocity per tick; flag and reject speed-hacks | ✅ | `MovementSystem.ProcessInput`; `IntentGuard` token-bucket rate limiting (60/s refill, 30 burst); tick-skew gate (`packetTick` window check); `Movement_CheatDetection_IllegalTeleportRejected` integration test |
| 3.2 | **Headless Collision / Raycasting System** — simulate solid static geometry (terrain, walls, doors) via flat mathematical structures or bounding volumes | ❌ | Not yet built. `WorldBounds.cs` provides outer map limits only. No AABB / capsule / tilemap collision against interior static geometry. |
| 3.3 | **Server-Side Kinematics & Forces** — knockback vectors, gravity falls, jump velocities simulated as math vectors on the server | ❌ | Not yet built. `MovementSystem` handles flat 2-D translation only. No vertical axis, no impulse / force accumulator. |

---

## 4 · Combat & Action Simulation

> The core engine of the Arena gameplay loop.

| # | Goal | Status | Notes |
|---|------|--------|-------|
| 4.1 | **Ability Activation & Cooldown Ledger** — exact execution timestamps for every spell, attack, and skill; prevent rapid-fire ability exploitation | ✅ | `IntentGuard` monotonic `ActionSequenceId` enforcement; per-peer token bucket for actions (20/s refill, 10 burst); `PlayerSession` tracks last-used timestamps consumed by `CombatSystem` |
| 4.2 | **Resource Validation** — verify mana/stamina, stun/silence state, weapon requirements before executing a cast sequence | 🔶 | `CombatSystem` checks mana cost and `SpellDatabase` constraints. Weapon-type gating and silence/interrupt crowd-control checks not yet implemented. |
| 4.3 | **Hit-Box & Hit-Scan Validation** — server-side distance checks, cone sweeps, or raycasts to verify projectile / melee intersection with enemy hit-boxes | 🔶 | `CombatSystem` performs distance-based melee range checks. `ProjectileSystem` uses AABB-style collision via `SpatialGrid` narrowing. Cone-sweep and true raycast hit-scan not yet implemented. |
| 4.4 | **Projectile Lifecycle Manager** — flight trajectories, velocities, and tracking loops for thousands of projectiles entirely in server memory until impact or expiration | ✅ | `ProjectileSystem.cs` + `ProjectileState` struct array (512 pre-allocated, zero alloc per spawn); `SwapRemove` O(1) removal; `SpatialGrid` collision narrowing; `TrySpawnProjectile` out-param pattern |

---

## 5 · RPG Systems & State Machines (Arena subset)

> State-tracking rules governing characters within a match.

| # | Goal | Status | Notes |
|---|------|--------|-------|
| 5.1 | **Entity Attribute Management** — HP, Mana, Attack Power, Defenses, Crit Chances factoring in gear and buffs | 🔶 | `PlayerSession` holds core stats; `StatModifier.cs` exists; `PlayerStatsRefreshedPacket` broadcasts stat fractions. Full recalculation pipeline (additive + multiplicative modifier stacking from gear sets) not yet formalized. |
| 5.2 | **Status Effect & Buff / Debuff Ticker** — tracks DoT/HoT (Poison, Bleed) and crowd-control (Stun, Slow, Root); ticks durations and applies per frame | ✅ | `ActiveStatusEffect.cs`; `TickStatusEffects()` in `ArenaInstance`; `StatusEffectAppliedPacket` / `StatusEffectRemovedPacket` as zero-alloc structs; `_reusableStatusEffects` pre-allocated scratch lists |

---

## 6 · Real-Time Networking Adjustments (Lag Compensation)

> Ensuring PvP feels fair and responsive despite players having different pings.

| # | Goal | Status | Notes |
|---|------|--------|-------|
| 6.1 | **Server Input Buffer (Jitter Buffer)** — hold incoming client commands for a micro-window to smooth erratic pings and packet clustering | ❌ | Not yet built. Inputs are processed the tick they arrive. No ring-buffer delay window or per-peer jitter measurement. |
| 6.2 | **History Rewind Engine** — rolling 200–500 ms position history buffer; rewind world state to a player's fire-time tick for hit-scan validation | ❌ | Not yet built. `EntityPositionPacket.AcknowledgedTick` (via `EncodeTick24`) is sent to the client for client-side reconciliation display, but the server does not store a rewindable snapshot ring-buffer. |
| 6.3 | **State Reconciliation Broadcasting** — authoritative correction packets sent to a client when local prediction drifts beyond threshold | 🔶 | `EntityPositionPacket` carries `AcknowledgedTick` so the client knows which input the server last processed. Server-side threshold check to trigger a forced correction broadcast (beyond the normal per-tick position packet) not yet implemented. |

---

## 7 · Security, Anti-Cheat, & Validation

> Ironclad validation because the game features enforced PvP.

| # | Goal | Status | Notes |
|---|------|--------|-------|
| 7.1 | **Packet Sanity & Size Limits** — drop malicious/corrupted packets violating size guidelines or containing nonsensical values | ✅ | `InputSanitizer.cs` validates shape (no NaN/Inf floats, valid ranges) before packets enter `IntentGuard`; malformed packets dropped and logged via `SecurityTelemetry` |
| 7.2 | **State Validation Rules** — bar impossible actions (loot through wall, open inventory while dead) | 🔶 | `IntentGuard` enforces auth gate, tick skew, sequence IDs, queue depth. Dead-state action blocking exists in `CombatSystem`. Through-wall interaction checks require collision system (see 3.2). |
| 7.3 | **Rate-Limiting (DDoS / Spam Protection)** — monitor connection request volumes and per-IP packet inputs; auto-throttle or disconnect flooding clients | ✅ | `IntentGuard` token buckets (movement 60/s, actions 20/s); violation score (disconnect at ≥ 80); `_ipGuards` per-`IPAddress` connection rate tracking in `NetworkManager`; `SecurityTelemetry` audit and snapshot logging fully off-thread: `WriteAudit`, `PrintSnapshot`, and `RecordUnauthorizedSpell` use `ThreadPool.QueueUserWorkItem<TState>` with static lambdas — zero game-loop-thread string allocation even under adversarial connection/violation floods. |

---

## 8 · Economy, Inventory, & Match Persistence

> Ensuring player data stays secure within and across matches.

| # | Goal | Status | Notes |
|---|------|--------|-------|
| 8.1 | **Atomic Inventory Transaction Engine** — multi-step validation for item moves, vendor purchases, and loot drops; items never duplicate or disappear on mid-transaction network drops | 🔶 | `_equipItemQueue` / `_pickupQueue` drained per tick; `GroundItem` struct; `ItemAddedToInventoryPacket` and `GroundItemRemovedPacket` broadcast. True atomic DB-level transaction (begin / commit / rollback with idempotency key) not yet implemented. |
| 8.2 | **Asynchronous Save Queue** — periodically offload dirty player states (inventory, gold) from RAM to Redis and Dapper/SQL on background worker threads without stuttering the 30 Hz loop | ✅ | `PlayerStateSink.FlushAsync` → `Task.Run(() => FlushCoreAsync(...))` offloads all CPU work to the thread pool; `MatchDataService` writes to Redis + PostgreSQL; game-loop thread returns in nanoseconds |

---

## Phase 1 Summary

| Category | ✅ Done | 🔶 Partial | ❌ Not Built |
|----------|---------|-----------|-------------|
| 1 · Network & Infrastructure | 5 / 5 | — | — |
| 2 · Interest Management | 4 / 4 | — | — |
| 3 · Movement & Physics | 1 / 3 | — | 2 / 3 |
| 4 · Combat & Action | 2 / 4 | 2 / 4 | — |
| 5 · RPG Systems (Arena) | 1 / 2 | 1 / 2 | — |
| 6 · Lag Compensation | — | 1 / 3 | 2 / 3 |
| 7 · Security & Anti-Cheat | 2 / 3 | 1 / 3 | — |
| 8 · Economy & Persistence | 1 / 2 | 1 / 2 | — |
| **Total** | **16 / 26** | **6 / 26** | **4 / 26** |

### Highest-Priority Phase 1 Gaps
1. **3.2 Headless Collision** — players can currently walk through walls
2. **6.2 History Rewind** — hit-scan PvP is not fair at high latency without this
3. **8.1 Atomic Inventory Transactions** — items can theoretically dupe on network drop
4. **6.1 Jitter Buffer** — inputs spike under poor network conditions

---

---

# Phase 2 — MMO

> Scope: persistent open world, NPC mobs, progression, player-to-player economy.
> **These systems are defined here for planning purposes and are out of scope until
> Phase 1 Arena is complete and stable. Do not implement yet.**

---

## 3-MMO · Server-Authoritative Movement & Physics (MMO additions)

| # | Goal | Notes |
|---|------|-------|
| 3-MMO.1 | **Pathfinding Engine (AI / NPC Navigation)** — high-performance crowd navigation for thousands of NPCs (custom 2-D grid pathfinder / flow-field) | No NPC entity type, navigation graph, or pathfinding algorithm exists. Requires NPC entity model first. |
| 3-MMO.2 | **Server-Side Kinematics & Forces** — knockback vectors, gravity falls, jump velocities simulated as math vectors | `MovementSystem` handles flat 2-D translation only. Extends Phase 1 item 3.3. |

---

## 5-MMO · RPG Systems & State Machines (MMO additions)

| # | Goal | Notes |
|---|------|-------|
| 5-MMO.1 | **Aggro & Threat Table Engine** — NPC threat levels based on damage dealt / healing output; updating active chase target per NPC | No NPC entity type exists. Requires pathfinding (3-MMO.1). Threat table data structure and aggro decay logic not designed yet. |
| 5-MMO.2 | **Experience & Leveling Logic** — kill credit distribution, XP calculations, stat increases on level-up | No XP field in `PlayerSession` or `LivePlayerState`. No leveling formula or kill-credit attribution system. |

---

## 8-MMO · Economy, Inventory, & World Persistence (MMO additions)

| # | Goal | Notes |
|---|------|-------|
| 8-MMO.1 | **Player-to-Player Secure Trading** — synchronized state lock requiring both players to lock their trade windows and explicitly confirm before data is transferred | No trade session state machine, mutual-lock protocol, or trade packet types defined. |
| 8-MMO.2 | **Full World Persistence** — zone state, respawn timers, resource nodes, and NPC positions persisted across server restarts | Not designed. Requires NPC entity model and open-world zone architecture. |
