---
name: gameserver-invariants
description: "Use when editing ArenaMMO GameServer combat, networking, factions, replication visibility, life-steal, status effects, or tick-loop logic; preserves server-authoritative and performance invariants."
---

# GameServer Invariants

## Purpose
This skill defines the gameplay and networking invariants Copilot must preserve when editing the GameServer project.

## Core Identity
- The server is fully authoritative.
- Clients submit intent only (input, cast requests, shoot requests).
- The server validates all range, cooldown, targeting, and faction rules before mutating state.
- Never trust client position, damage, hit claims, or target claims.

## Do
- Enforce gameplay legality server-side before mutating state.
- Keep fixed-tick order stable unless the user explicitly requests an architecture change.
- Preserve faction visibility gating at send-time for private data (health, allied-only effects).
- Keep combat resolution inside server systems (CombatSystem and ProjectileSystem), not packet handlers.
- Rebuild after edits that touch simulation, packets, or system signatures.

## Don't
- Do not grant client authority over hit results, final position, or damage outcomes.
- Do not merge position and health replication into a single packet stream.
- Do not add SQL or external I/O into per-tick simulation paths.
- Do not introduce LINQ or high-allocation patterns in hot loops.
- Do not reorder tick phases casually; this can introduce subtle behavior regressions.

## Ingress Security Guardrails
- Treat pre-auth peers as untrusted until AuthTicketPacket validation succeeds.
- Preserve the pending-auth timeout path; peers that connect but do not authenticate in time must be disconnected.
- Keep pre-auth IP abuse controls in place:
  - sliding-window request limiting
  - violation score tracking
  - temporary IP bans after repeated violations
- Keep packet shape/finite-value sanitization before intents enter simulation queues.
- Keep intent admission centralized through IntentGuard:
  - packet tick skew checks
  - per-intent token-bucket rate limiting
  - monotonic action sequence replay checks
  - per-peer and global queue pressure limits
- Keep spell entitlement checks server-side (authenticated loadout only).
- Keep invalid ticket, replay, unauthorized spell, and rate-limit telemetry emitted for operational triage.

## Ticket Validation Contract
- Ticket validation must include all of: shape checks, clock-window checks, HMAC verification, nonce replay protection, and allowed-spell parsing.
- Canonical ticket serialization field order is protocol-critical; do not reorder fields without coordinated lobby/client/server rollout.
- Nonce replay protection must remain server-side and enforced before peer becomes authoritative in match state.
- `TryValidateForRejoin` is a secondary validation path that skips **only** nonce replay. It is called exclusively after `ArenaInstance` confirms the AccountId is in the grace-period set — a server-side check. HMAC and clock-window are still verified. Do not call it for first-time connections; that would defeat replay protection entirely.

## Simulation Model
- Fixed tick loop at 30 Hz in ArenaInstance.
- Tick order matters and should stay stable:
  0. `FinalizeHydration()` — drains completed async Redis profile reads from `_pendingHydration`; applies profiles to waiting sessions; re-enqueues incomplete tasks. Never blocks.
  1. Movement input (consume latest `PlayerInputData` struct per peer from `_latestInputByPeer`; normalize; apply via `MovementSystem.ProcessInput(player, in input, DeltaTime, _zone.Bounds)`). **`_latestInputByPeer` is `Dictionary<NetPeer, PlayerInputData>` — the value type is `PlayerInputData` (struct), not `PlayerInputPacket` (class).** `EnqueueInput` copies the three scalar fields immediately to prevent LiteNetLib reusable-instance aliasing.
  2. Record position history — call `PlayerSession.RecordPositionHistory(_tick)` on every player immediately after movement
  3. Melee attacks
  4. Spell casts — drains spell queue; emits `CombatEventPacket` (via `_reusableSpellEvents`) and `AoEHitEventPacket` (via `_reusableAoEHitEvents`)
  5. Shoot/projectile spawn — mutates pre-allocated `_projSpawnPacket` struct, sends via `SendToInterested(in _projSpawnPacket, ...)`
  6. Projectile tick and resolution — mutates pre-allocated `_projDestPacket` struct on hit/expiry
  7. Status effect tick processing — `TickStatusEffects` returns `bool statsChanged`; if `true` AND `player.Peer != null`, send `BuildStatsPacket()` to that peer
  8. Death detection — set `IsRespawning`, increment `DeathCount`/`KillCount`, broadcast `PlayerDeathPacket` via `SendToInterested`
  9. Respawn countdown — call `TickRespawn` per player; broadcast `PlayerRespawnPacket` via `SendToInterested` on return `true`
  10. Equip / gear-set swap — no respawn-window gate; always permitted; ownership validation inside `TryEquipItem`/`TryUnequipSlot`/`TryApplyGearSet`
  11. Ground item pickups — distance check ≤ 2 units, inventory size cap, `PickupItem`, `GroundItemRemovedPacket` via `SendToInterested`
  12. Win-condition check — delegates to `_zone.WinCondition.Evaluate(_players, _tick)` (returns `FactionId?`); guarded by `_matchEnded`
  13. Broadcast snapshots/events
- Maintain deterministic, server-first sequencing wherever possible.

## Tick-Order Contract
If a change affects one phase, validate the neighboring phases still operate correctly:
- Movement before position history snapshot — history must capture the post-movement position.
- Position history snapshot before combat validations — lag-compensation rewind depends on fresh history.
- Spell/projectile spawning before projectile movement resolution.
- Status periodic ticks after direct-hit damage resolution.
- Equip/swap after respawn countdown — stat change takes effect at the correct health baseline.
- Ground item pickups after equip — ensures inventory-size cap reflects any just-equipped items.
- Snapshot broadcasts after authoritative state mutation is complete for the tick.

## Equip & Gear System
- `TryEquipItem`, `TryUnequipSlot`, and `TryApplyGearSet` no longer accept an `isPermitted` parameter. Gear changes are always permitted; the old respawn-window gate has been removed.
- Ownership validation (item must exist in `_inventory`) is the only gate inside these methods.
- All three methods call `RecomputeStats()` and return a `PlayerStatsRefreshedPacket` via `BuildStatsPacket()`. Always send that packet to the owning peer after a successful call.
- Do not re-add a respawn-window gate. If you need a timed restriction, use a `ZoneDescriptor` strategy.

## Stat Computation Model (`RecomputeStats`)
- `RecomputeStats` is called on all stat-affecting mutations: equip, unequip, gear-set swap, stat-buff apply, stat-buff expiry, zone modifier change.
- Three layers are summed in order:
  1. **Base stats** — set once from `HydrateFromProfile`.
  2. **Equipped items** — iterate `_equippedItems`; prefer `ItemInstance.CraftedStats` over archetype definition stats.
  3. **Active status-effect stat modifiers** — iterate `_statusEffects`; sum only those where `StatMod.HasAnyValue == true`.
  4. **Zone stat modifier** — single `_zoneStatModifier` field; set via `SetZoneStatModifier(modifier)` (pass `StatModifier.Zero` to clear).
- **Proportional HP scaling**: when `MaxHealth` changes, current `Health` is scaled by `Health * (newMaxHealth / oldMaxHealth)` so equipping a +50 HP item also adds 50 to current health. This is intentional; do not revert to a simple clamp.
- `BuildStatsPacket()` (public) constructs the authoritative `PlayerStatsRefreshedPacket`. Call it after any `RecomputeStats()` invocation to send the updated stats to the player's peer.

## Status Effects
- `TryApplyStatusEffect` handles DoT/HoT effects with no stat change. Its signature is unchanged.
- `TryApplyStatBuff` handles temporary pure-stat modifiers (consumables, spell buffs, passive auras). It calls `TryApplyStatusEffect` internally and then writes `StatMod` into the effect, then calls `RecomputeStats()`.
- `TickStatusEffects` returns `bool statsChanged`. If `true`, `ArenaInstance` must send `BuildStatsPacket()` to `player.Peer` (if non-null).
- `ActiveStatusEffect.StatMod` is `StatModifier.Zero` for all standard DoT/HoT effects — no behavior change for existing combat system calls.
- Do not change `TryApplyStatusEffect`'s signature; `CombatSystem` calls it without stat modifiers.

## Inventory & Ground Items
- `PlayerSession.PickupItem(item, maxInventorySize)` adds an item to `_inventory` if there is space. Returns `false` if full. The size cap is always passed from `_zone.MaxInventorySize` — clients cannot bypass it.
- Ground items live in `ArenaInstance._groundItems` (`Dictionary<int, GroundItem>`). Keys are server-assigned IDs; clients can only reference items by these IDs.
- Pickup resolution: distance check ≤ 2 units squared, inventory-space check, remove from `_groundItems`, call `PickupItem`, send `GroundItemRemovedPacket` via `SendToInterested`, send `ItemAddedToInventoryPacket` to owning peer.
- In Arena mode (`_zone.IsArenaMode == true`), items picked up during the match are **not persisted**. Only crafting ingredient rewards (computed in `ComputeCraftingRewards`) persist at match end.
- `SpawnGroundItem(Vec2, ItemInstance)` is the public API for placing loot on the ground.

## Grace-Period Rejoin (Dota2-style Reconnect)
- When a player disconnects, `OnPlayerDisconnected` sets `session.Peer = null` and stores the session in `_gracePeriodSessions[accountId] = (session, _tick + _zone.RejoinGraceTicks)`.
- The session remains in `_players` and `_entityMap` as a stationary ghost. Other players can still attack it.
- `TryAuthenticatePeer` checks `_gracePeriodSessions` first. If the AccountId is in the grace set and the window has not expired, it calls `TryValidateForRejoin` (HMAC + expiry, skips nonce replay) and then `OnPlayerRejoined`.
- `OnPlayerRejoined` reattaches the peer, re-registers in `_peerMap`, removes from `_gracePeriodSessions`, and sends `PlayerReconnectedPacket` to all peers.
- `EvictExpiredGracePeriods()` runs every `TickRate` ticks. It removes expired sessions from `_players`, `_entityMap`, and `_gracePeriodSessions`, then sends `EntityDespawnPacket`.
- `PlayerGraceDisconnectPacket` is sent to all peers on disconnect (client shows disconnected indicator).
- `PlayerReconnectedPacket` is sent to all peers on successful rejoin.
- Do not remove the grace-period logic — it is an explicit product requirement.

## Heartbeat & Zone Handoff (DataLayer)
- `MatchDataService.Sink` (`PlayerStateSink`) writes `live-state:{accountId}` to Redis with a 2-hour TTL.
- `FlushAllPlayerStates()` fires every 60 s (every `TickRate * 60` ticks) from `RunGameLoop`. It is fire-and-forget — `_ = Sink.FlushAsync(...)` — and never blocks the tick loop.
- In Arena mode, `TakeSnapshot(includeInventory: false)` is passed because Arena pickups are match-scoped.
- In MMO zones, `TakeSnapshot(includeInventory: true)` includes the full inventory.
- Zone handoffs publish a `ZoneTransferPayload` to Redis Pub/Sub channel `zone-transfer:{targetZoneId}`. The target zone pre-warms the session from the payload's `LivePlayerState`.
- `CraftingIngredientReward[]` is written to Redis key `crafting-reward:{accountId}` by `SaveMatchResultAsync` at Arena match end. The ProfileServer claims this key to credit the player's ingredient pouch.
- Do not add SQL calls to any path that runs during the tick loop.
- Keep hot paths allocation-light.
- Avoid LINQ in per-tick logic.
- Prefer for-loops and reusable lists/buffers.
- Avoid per-entity temporary object churn inside tick loops.
- Keep checks branch-light and data-oriented in CombatSystem, ProjectileSystem, and ArenaInstance.
- **`CombatSystem.ProcessSpellCast`, `ProcessAoE`, `ProcessMeleeSplash`, and `ProjectileSystem.Tick` / `ApplyExplosiveSplash` accept `List<PlayerSession>` (not `IReadOnlyList<PlayerSession>`).** Every `[i]` and `.Count` access in the AoE and projectile collision inner loops must be a direct array read (no vtable dispatch). Do not widen these parameters back to `IReadOnlyList` — at 2,000-player MMO scale that re-introduces ~12B virtual dispatch calls/second.
- **`SecurityTelemetry.WriteAudit`, `PrintSnapshot`, and `RecordUnauthorizedSpell` must remain fully off-thread.** All string interpolation and `Console.WriteLine` I/O must be performed inside `ThreadPool.QueueUserWorkItem<TState>` callbacks with `static` lambdas and value-tuple `TState` arguments. Do not move these back onto the game-loop thread — under adversarial cheat floods these paths fire hundreds of times per tick.
- **`NetworkManager.OnPeerConnected`, `OnPeerDisconnected`, and `OnNetworkError` must remain off-thread.** Log lines must be emitted via `ThreadPool.QueueUserWorkItem<TState>` with static lambdas. The address/endpoint string must be snapshotted before hand-off. Do not call `Console.WriteLine` directly in these event handlers.
- `ArenaInstance._entityMap` (`Dictionary<int, PlayerSession>`) provides O(1) entity lookup by EntityId.
  - Keep it in sync with `_players` and `_peerMap` at all authentication and disconnect events.
  - Replace any O(N) linear `FindById` scans with a dictionary lookup against `_entityMap`.
  - `PlayerSession.TickStatusEffects` accepts `IReadOnlyDictionary<int, PlayerSession>` (not a list); always pass `_entityMap`.
  - `PlayerSession` no longer contains a private static `FindById` helper; it was removed as O(N) debt.
- Reuse pre-allocated list fields `_reusableStatusEffects`, `_reusableSpellEvents`, and `_reusableAoEHitEvents` in the tick drain loops.
  - Call `.Clear()` before each use; never allocate `new List<>()` inside per-tick or per-dequeue hot paths.
- `RemovePlayerFromList(session)` in ArenaInstance uses an O(1) swap-remove (replace slot with last element, then `RemoveAt` tail).
  - Do not replace it with `_players.Remove(session)` which is O(N) and shifts the entire list.
- `NetworkManager._sharedWriter` is a single pooled `NetDataWriter` for all send calls.
  - All sends occur on the single game-loop thread; no lock is needed.
  - Do not allocate `new NetDataWriter()` per send call.
- `NetworkManager._timedOutPeers` is a pre-allocated `List<NetPeer>` reused by `DisconnectAuthTimeoutPeers()`.
  - Do not replace it with `new List<NetPeer>()` inside the method body; that re-introduces per-tick heap allocation during connect floods.
- `NetworkManager._ipGuards` (`ConcurrentDictionary<IPAddress, IpGuardState>`) is periodically evicted by `EvictStaleIpGuards()`.
  - Eviction runs every `IpGuardEvictionIntervalTicks (300)` ticks (~10 s), called from `DisconnectAuthTimeoutPeers`.
  - Only entries where the IP is neither currently banned nor carrying a nonzero violation score are removed.
  - Do not remove this eviction path; without it an IPv6-spoofed unique-source-address flood grows the dictionary without bound.

## Zone Architecture
- `ZoneDescriptor` is injected into `ArenaInstance` at construction. It is the single source of truth for:
  - `Bounds` (`WorldBounds`) — used by `MovementSystem.ProcessInput` for clamping.
  - `ViewRadius` / `ViewRadiusSqr` — used by `BroadcastState` for AoI culling.
  - `FactionSpawnPoints` — replaces the old hardcoded `SpawnAlpha`/`SpawnBeta` statics.
  - `WinCondition` (`IWinCondition`) — `EliminationWinCondition` for Arena; `NoWinCondition.Instance` for MMO zones.
  - `EventFilter` (`IInterestFilter`) — `BroadcastFilter.Instance` for Arena; `RadiusFilter(r)` for large zones.
  - `MaxInventorySize` — enforced server-side on ground-item pickup via `PickupItem(item, _zone.MaxInventorySize)`.
  - `IsArenaMode` — controls whether `TakeSnapshot` includes inventory (`false` in Arena, `true` in MMO).
  - `RejoinGraceTicks` — how long a disconnected ghost session is preserved (default 9000 = 5 min at 30 Hz).
- Do not add new mode branches to `ArenaInstance` for Arena vs. MMO logic. Use `ZoneDescriptor` fields and strategy interfaces instead.
- Pass `new ZoneDescriptor()` in `Program.cs` for the default Arena configuration.

## Player and Faction Rules
- Every PlayerSession has a Faction.
- Friendly/allied behavior is faction-based.
- Hostile behavior targets enemies unless explicitly configured otherwise.
- `PlayerSession.Peer` may be `null` for grace-period ghost sessions. Always null-check `Peer` before calling `SendTo`. `BroadcastState` and `SendToInterested` already guard for this — do not bypass them.

## State Replication Rules
- Position and health are intentionally separated:
  - EntityPositionPacket is sent broadly.
  - EntityHealthPacket is sent only to allied viewers.
- Do not reintroduce combined position+health packets that leak hidden health data.
- `BroadcastState` skips viewers with `Peer == null` (grace-period disconnected players) to avoid null-socket writes.
- All combat-event and state-update broadcasts route through `NetworkManager.SendToInterested<T>` with the zone's `EventFilter`. In Arena mode `BroadcastFilter.Instance` passes all peers (identical behavior to `SendToAll`). In open-world zones a `RadiusFilter` limits traffic. Do not call `SendToAll` for combat events — use `SendToInterested`.

## Visibility Contract
- Allied-only visibility must be enforced by recipient filtering, not by "masked values" when possible.
- Hostile/public effects can be broadcast broadly only when intended by visibility enum.
- New private gameplay fields must be evaluated for faction leakage risk before adding packets.

## Input Quantization Contract
- `PlayerInputPacket.InputX` and `InputY` are `sbyte` (-127..127), not `float`.
- Both client and server dequantize via `value / 127f` — identical math on all platforms, eliminating cross-platform FP drift.
- Do not revert input axes to `float`; this would reintroduce normalization divergence across Unity runtimes.
- `InputSanitizer.IsValid(PlayerInputPacket)` only checks `TickNumber >= 0`; finite-value guards are unnecessary for `sbyte`.

## Reconciliation Contract
- `EntityPositionPacket` carries two tick fields for client-side prediction reconciliation:
  - `ServerTick` — the server tick that produced this snapshot.
  - `AcknowledgedTick` — the last `PlayerInputPacket.TickNumber` consumed for this entity.
- Both fields are populated in `ArenaInstance.BroadcastState` from `_tick` and `entity.LastProcessedClientTick`.
- `PlayerSession.LastProcessedClientTick` is set by `MovementSystem.ProcessInput` on every successfully applied input.
- Do not remove these fields; the Unity client uses them to discard stale predicted inputs and replay only the unacknowledged tail.

## Lag Compensation Contract
- `PlayerSession` holds a 64-slot `Vec2[]` position ring buffer (`_positionHistory`).
  - 64 slots at 30 Hz ≈ 2.1 s of rewind depth — sufficient for any realistic RTT.
  - Populated by `RecordPositionHistory(serverTick)` immediately after the movement phase each tick.
- `GetHistoricalPosition(requestedTick, currentTick, maxRewindTicks)` clamps `requestedTick` to **both** bounds:
  - Lower bound: `currentTick - maxRewindTicks` — prevents rewind beyond the history buffer.
  - Upper bound: `currentTick` — **critical security invariant**: IntentGuard admits packets up to `MaxFutureTickSkew (5)` ticks ahead. Without the upper clamp, a future `requestedTick` indexes a ring-buffer slot written ~`(PositionHistorySize - delta)` ticks ago, producing ghost hits/misses from 2-second-old stale position data.
  - Implementation uses `Math.Clamp(requestedTick, currentTick - maxRewindTicks, currentTick)`.
  - A `Debug.Assert(maxRewindTicks <= PositionHistorySize)` guards against a future constant change silently wrapping the ring buffer.
  - Do not revert this to `Math.Max` (lower-bound only); that re-opens the future-tick exploit.
- `CombatSystem.MaxRewindTicks = 10` (~333 ms) caps the rewind depth for melee and single-target spells.
- Lag compensation applies to **melee attacks** and **single-target spells** only:
  - `ProcessMeleeAttack` accepts `int clientAttackTick` and rewinds the target before the range check.
  - `ProcessSingleTarget` rewinds the target using `request.TickNumber`.
- **AoE, MeleeSplash, and projectile collision use current-tick positions** — these are spatially resolved, not instant casts.
- Do not apply lag compensation to projectile collision; projectiles travel physically through space each tick.

## CombatSystem API Contract
- `ProcessMeleeAttack` signature includes `int clientAttackTick` — always pass `entry.Packet.TickNumber` from the attack queue.
- `ProcessSpellCast` returns `void` and accepts:
  - `List<PlayerSession> allPlayers` — used for AoE and MeleeSplash iteration. **Must be `List<T>`, not `IReadOnlyList<T>`** — eliminates vtable dispatch on every `[i]` and `.Count` in the collision loop.
  - `IReadOnlyDictionary<int, PlayerSession> entityMap` — O(1) lookup for single-target resolution; always pass `ArenaInstance._entityMap`.
  - `List<CombatEventPacket> results` — pre-allocated `_reusableSpellEvents` list; clear it before each call.
  - `List<AoEHitEventPacket> aoeResults` — pre-allocated `_reusableAoEHitEvents` list; clear it before each call. Used by `ProcessAoE` and `ProcessMeleeSplash`.
  - Do not revert to a return-value `List<>` pattern.
  - Do not remove the `entityMap` parameter; it prevents the O(N) linear scan that a flooded spell queue can amplify into a CPU spike proportional to player count.
  - Do not collapse `results` and `aoeResults` into one list; single-target events and AoE events are different packet types consumed differently by clients.


  - Must be enemy-only.
  - Enforces cooldown and range server-side.
- Spell casts:
  - Routing based on SpellDefinition.TargetType, not client claims.
  - Cooldown is consumed after passing cast gate (alive and not on cooldown), even if no hit lands.
- Projectiles:
  - Spawn from authoritative shooter position.
  - Direction is normalized server-side.
  - Range, hit chance falloff, pierce, and AoE detonation are server-resolved.

## Life-Steal Rules
- Life-steal can come from:
  - melee weapon stats on attacker
  - spell definition
  - projectile snapshot from spell at spawn
- Life-steal heals the attacker/owner based on dealt damage.
- Keep life-steal application in server-side damage resolution only.

## Status Effect Rules
- Status effects can be applied by:
  - melee weapons
  - spells
  - projectiles
- Visibility is explicit:
  - AlliesOnly effects are shown only to allied viewers of the target.
  - Everyone effects are shown to all viewers.
- Status effects support periodic ticking:
  - damage per tick
  - tick interval
  - owner heal percentage per tick (vampiric DoT model)
- DoT tick processing remains server-side and emits CombatEventPacket tick events.

## Life-Steal and DoT Contract
- Direct-hit life-steal heals from actual dealt damage only.
- DoT source-heal uses per-effect configuration and is processed on tick, server-side.
- Periodic effect ticking must not mutate collection state unsafely during enumeration.
- Effect refresh should preserve clear semantics (refresh duration/stacks/source by explicit rules).

## Networking and Event Delivery
- Combat and status events are generated from server simulation results only.
- Keep reliable/unreliable channel intent:
  - state snapshots use Unreliable latest-state style
  - combat/status lifecycle events use ReliableOrdered
  - lifecycle events (`EntitySpawnPacket`, `EntityDespawnPacket`, `PlayerDeathPacket`, `PlayerRespawnPacket`, `MatchEndPacket`) use ReliableOrdered
- `AoEHitEventPacket` is broadcast to all peers as ReliableOrdered, not AoI-filtered;
  clients may be beyond view radius but still want death/SFX feedback for AoE spells.
- `StatusEffectAppliedPacket` and `StatusEffectRemovedPacket` are now **structs**. Always pass them by `in` reference to `NetworkManager.SendToInterested` / `SendTo` overloads. Do not store them as class references or pass them to the generic `SendToInterested<T>` path.
- `ProjectileSpawnPacket` and `ProjectileDestroyPacket` are now **structs**. Use the pre-allocated `_projSpawnPacket` / `_projDestPacket` instance fields on `ArenaInstance`; mutate them before each send. Do not use `new ProjectileSpawnPacket` or `new ProjectileDestroyPacket` inside the tick loop.
- `BroadcastStatusEffect` and `BroadcastStatusEffectRemoval` for `AlliesOnly` visibility now call `_spatialGrid.QueryNeighbours(target.Position)` to reduce the inner loop from O(N) to O(k). This is the same grid used by `BroadcastState`.

## Safety and Persistence
- Do not add direct SQL access into active match simulation code.
- Match state lives in memory during simulation.
- Preserve queue-based separation between network receive and simulation processing.
- Preserve auth-first flow: no gameplay intent should mutate state for unauthenticated peers.
- `MatchDataService` (in `GameServer.DataLayer`) is the only data-access layer in GameServer.
  - `LoadPlayerProfileAsync(accountId)` uses `await _redis.StringGetAsync(...).ConfigureAwait(false)` — **truly non-blocking**, zero ThreadPool thread occupation during the Redis round-trip. It **must** be called in `OnPlayerAuthenticated` (on the game-loop thread via `PollEvents`) — never the synchronous `LoadPlayerProfile`. The returned `Task<PlayerProfile?>` is enqueued into `_pendingHydration` alongside the session.
  - `FinalizeHydration()` drains the `_pendingHydration` `ConcurrentQueue` at the **top of every `ProcessTick`**, applying profiles from completed tasks only. Pending tasks are re-enqueued. No blocking ever occurs on the tick thread.
  - `LoadPlayerProfile(accountId)` (synchronous) still exists but must **only** be called from true off-tick contexts (tests, one-off tooling). Never call it from inside `OnPlayerAuthenticated`, `PollEvents`, `ProcessTick`, or `BroadcastState`. Calling it from `OnPlayerAuthenticated` blocks the game-loop thread for a full Redis round-trip (1–15 ms) and directly erodes the 33.33 ms tick budget.
  - `SaveMatchResultAsync(result)` is fire-and-forget; called once from `EndMatch` after `_isRunning = false`.
  - Postgres upsert is delegated to a background `Task.Run`; the tick loop never waits for it.
  - Do not call any `MatchDataService` method synchronously from inside `ProcessTick` or `BroadcastState`.
- Avoid string interpolation (`$"...{variable}..."`) in methods that run on the game-loop thread, including `EvictExpiredGracePeriods`. Each interpolated string heap-allocates a new `string` object. Use separate `Console.Write` / `Console.WriteLine` calls with literal string arguments instead — literals are interned and zero-allocation.
- `SecurityTelemetry.WriteAudit`, `PrintSnapshot`, and `RecordUnauthorizedSpell` are fully off-thread: they must use `ThreadPool.QueueUserWorkItem<TState>` with `static` lambdas and value-tuple `TState`. Do not call `Console.WriteLine` directly from these methods on the game-loop thread.
- `NetworkManager.OnPeerConnected`, `OnPeerDisconnected`, and `OnNetworkError` must log via `ThreadPool.QueueUserWorkItem<TState>`. Snapping `peer.Address.ToString()` / `endPoint.ToString()` before hand-off is required because the address may become stale; the static lambda must capture nothing from the live LiteNetLib objects.
- Configuration is loaded from `appsettings.json` + environment variables at startup via `Microsoft.Extensions.Configuration`.
  - `ARENA_TICKET_SECRET` must remain in an environment variable; do not move it into `appsettings.json`.
  - Redis and Postgres connection strings live in `appsettings.json:ConnectionStrings` and are overridable via env vars.
  - Arena port lives in `appsettings.json:Arena:Port` (default 9050).

## Damage Pipeline Contract
All damage resolution follows a three-stage pipeline:
```
raw = baseDamage × attackPower
│
├─ True damage → max(1, raw)  [skip absorb and resist]
│
└─ Physical / Magic:
    afterAbsorb = raw × (1 − clamp(absorbPercent, 0, 1))    [absorb: always applied]
    if (pierceRoll < pierceChance) skip resist                 [pierce: prob-based]
    afterResist = afterAbsorb × (1 − clamp(resistPercent, 0, 1))
    result = max(1, afterResist)
```
- `absorbPercent` is always applied; it cannot be bypassed (comes from armor/equipment).
- `resistPercent` is bypassed when the pierce check succeeds (comes from stats/resistances).
- `pierceChance` comes from `SpellDefinition.PierceChance` or `ProjectileState.PierceChance`.
- Melee basic attacks always use `DamageType.Physical` with `pierceChance = 0f`.
- `CombatMath.CalculateDamage` is the single authoritative implementation; do not fork it.
- Do not mix absorb and resist semantics; they are intentionally different mitigation tiers.

## PlayerSession Combat Stats Contract
- `Armor` property was **removed**; do not re-introduce it.
- Mitigation is now expressed as four separate float fractions (0–1):
  - `PhysicalAbsorbPercent` — always applied to Physical hits
  - `PhysicalResistPercent` — applied unless pierced
  - `MagicAbsorbPercent`    — always applied to Magic hits
  - `MagicResistPercent`    — applied unless pierced
- These are hydrated from `PlayerProfile` (Redis) at connection time in `OnPlayerAuthenticated`.
- Server systems select the correct pair per damage type before calling `CombatMath.CalculateDamage`.
- `KillCount` and `DeathCount` are tracked on `PlayerSession`; incremented by ArenaInstance in phase 8.
  - `KillCount`: credited to the entity whose `EntityId == LastKillerEntityId` on the victim.
  - `DeathCount`: incremented on the dying session when `StartRespawn()` is called.
  - Both are written to `MatchResult` by `EndMatch` for persistence.

## Respawn Contract
- `PlayerSession.StartRespawn()` transitions the session to respawning state.
  - Sets `IsRespawning = true` and starts a `DefaultRespawnTicks = 150` (5 s at 30 Hz) countdown.
  - Must only be called once per death; the phase-8 guard is `p.Health <= 0f && !p.IsRespawning`.
- `PlayerSession.TickRespawn(Vec2 spawnPoint)` decrements the countdown each tick.
  - Returns `true` exactly once on the tick the player re-enters play.
  - When returning `true`, the caller broadcasts `PlayerRespawnPacket` via `SendToInterested`.
- `IsAlive` is now `Health > 0f && !IsRespawning`.
  - Combat systems check `IsAlive` before resolving hits; a respawning player cannot be targeted.
  - Do not revert `IsAlive` to `Health > 0f` alone; that allows combat resolution against ghosts.
- Spawn points come from `_zone.GetSpawnPoint(faction)` (injected `ZoneDescriptor`).
  - Do not hardcode `SpawnAlpha`/`SpawnBeta` — those static constants have been removed.

## Match Lifecycle Contract
- `ArenaInstance` constructor signature is `ArenaInstance(string ticketSecret, ZoneDescriptor zone, MatchDataService? dataService = null)`.
  - `zone` configures all map topology and rules; pass `new ZoneDescriptor()` for default Arena mode.
  - `dataService` is optional for unit-test contexts that don't need persistence.
- `_matchEnded` flag gates `CheckWinCondition()` in phase 12; once true it prevents repeat end-match broadcasts.
- `EndMatch(FactionId winner)` must:
  1. Set `_matchEnded = true` and `_isRunning = false` (stops the tick loop).
  2. Broadcast `MatchEndPacket` to all peers.
  3. Compute `CraftingIngredientReward[]` per player via `ComputeCraftingRewards`.
  4. Send `CraftingRewardPacket` to each non-null peer that earned rewards.
  5. Fire `SaveMatchResultAsync` for each session (fire-and-forget); `MatchResult.CraftingRewards` carries the rewards so the ProfileServer can credit them.
- `ComputeCraftingRewards` must use the pre-allocated `_rewardScratch = new CraftingIngredientReward[1]` instance field on `ArenaInstance`. Mutate `_rewardScratch[0]` in-place and return the field reference. Do **not** use `new[] { ... }` — that heap-allocates on every call, which fires on the game-loop thread before `_isRunning` is fully observable as `false`. Return `Array.Empty<CraftingIngredientReward>()` for zero-reward cases (already heap-free via the shared empty singleton).
- `CheckWinCondition()` delegates to `_zone.WinCondition.Evaluate(_players, _tick)` which returns `FactionId?`.
  - `EliminationWinCondition` (default): all surviving players belong to one faction.
  - `NoWinCondition.Instance`: always returns `null` (for MMO open-world zones).
- Entity lifecycle:
  - `OnPlayerAuthenticated`: broadcast `EntitySpawnPacket` to all connected peers; back-fill all existing entities to the new peer.
  - `OnPlayerDisconnected`: set `Peer = null`, store in `_gracePeriodSessions`; send `PlayerGraceDisconnectPacket` to all. **Do not** call `RemovePlayerFromList` here — the ghost session must remain in `_players`.
  - Grace expiry (`EvictExpiredGracePeriods`): removes session from `_players`, `_entityMap`, `_gracePeriodSessions`; broadcasts `EntityDespawnPacket`.
  - Rejoin (`OnPlayerRejoined`): reattaches peer; removes from `_gracePeriodSessions`; sends `PlayerReconnectedPacket` to all.

## Equip & Gear System
- `TryEquipItem`, `TryUnequipSlot`, and `TryApplyGearSet` no longer accept an `isPermitted` parameter. Gear changes are always permitted.
- Ownership validation (item must exist in `_inventory`) is the only gate inside these methods.
- All three return a `PlayerStatsRefreshedPacket` via the renamed public `BuildStatsPacket()`. Always send that packet to the owning peer after a successful call (guard `Peer != null`).
- Do not re-add a respawn-window gate. If timed restrictions are needed, use a `ZoneDescriptor` strategy.

## Stat Computation Model (`RecomputeStats`)
- `RecomputeStats` is called on all stat-affecting mutations: equip, unequip, gear-set swap, stat-buff apply/expiry, zone modifier change.
- Three layers are summed in order:
  1. **Base stats** — set once from `HydrateFromProfile`.
  2. **Equipped items** — iterate `_equippedItems`; prefer `ItemInstance.CraftedStats` over archetype definition stats.
  3. **Active status-effect stat modifiers** — iterate `_statusEffects`; sum only those where `StatMod.HasAnyValue == true`.
  4. **Zone stat modifier** — single `_zoneStatModifier` field; set via `SetZoneStatModifier(modifier)` (pass `StatModifier.Zero` to clear).
- **Proportional HP scaling**: when `MaxHealth` changes, current `Health` is scaled by `Health * (newMaxHealth / oldMaxHealth)`. Do not revert to a simple clamp.
- `BuildStatsPacket()` (public) constructs the `PlayerStatsRefreshedPacket`. The old `BuildStatsRefreshedPacket(byte gearSetIndex)` was removed — use `BuildStatsPacket()` instead.

## Status Effects (extended)
- `TryApplyStatusEffect` handles DoT/HoT effects with no stat change. Signature unchanged.
- `TryApplyStatBuff` handles temporary pure-stat modifiers. It calls `TryApplyStatusEffect` internally, writes `StatMod` into the effect, then calls `RecomputeStats()`. Returns `PlayerStatsRefreshedPacket?` as an `out` param.
- `TickStatusEffects` now returns `bool statsChanged`. If `true` AND `player.Peer != null`, `ArenaInstance` sends `BuildStatsPacket()` to that peer after the loop.
- `ActiveStatusEffect.StatMod` is `StatModifier.Zero` for all standard DoT/HoT effects — no behavior change for existing combat system calls.
- Do not change `TryApplyStatusEffect`'s signature; `CombatSystem` calls it without stat modifiers.

## Inventory & Ground Items
- `PlayerSession.PickupItem(item, maxInventorySize)` adds an item to `_inventory`. Returns `false` if full. Always pass `_zone.MaxInventorySize`.
- Ground items live in `ArenaInstance._groundItems` (`Dictionary<int, GroundItem>`), keyed by server-assigned IDs.
- Pickup resolution: distance ≤ 2 units (squared 4f), inventory-space check, remove from `_groundItems`, `PickupItem`, `GroundItemRemovedPacket` via `SendToInterested`, `ItemAddedToInventoryPacket` to owning peer.
- In Arena mode, items picked up during the match are **not persisted**. Only `CraftingIngredientReward[]` written by `SaveMatchResultAsync` persists.
- `SpawnGroundItem(Vec2, ItemInstance)` is the public API for placing loot on the ground.

## Grace-Period Rejoin
- Disconnect: `session.Peer = null`, stored in `_gracePeriodSessions`, `PlayerGraceDisconnectPacket` sent to all.
- Rejoin: `TryAuthenticatePeer` checks grace set; calls `TryValidateForRejoin` (HMAC + expiry, skips nonce replay); then `OnPlayerRejoined`.
- `OnPlayerRejoined`: reattaches peer, removes from grace set, sends full entity-list sync + `PlayerReconnectedPacket`.
- Eviction: `EvictExpiredGracePeriods()` runs every `TickRate` ticks; removes expired sessions; sends `EntityDespawnPacket`.
- `TryValidateForRejoin` must only be called after confirming the AccountId is in the grace set. Never call it for first-time connections.

## Heartbeat & DataLayer
- `MatchDataService.Sink` (`PlayerStateSink`) writes `live-state:{accountId}` to Redis (2h TTL).
- `FlushAllPlayerStates()` fires every 60 s in `RunGameLoop` — fire-and-forget, never blocks the tick loop.
- Arena mode: `TakeSnapshot(includeInventory: false)`. MMO zones: `TakeSnapshot(includeInventory: true)`.
- Zone handoff: publish `ZoneTransferPayload` to `zone-transfer:{targetZoneId}` after flushing state.
- `CraftingIngredientReward[]` written to `crafting-reward:{accountId}` at Arena match end by `SaveMatchResultAsync`.
- Do not call any `MatchDataService` or `PlayerStateSink` methods from inside `ProcessTick`.
- **Async profile hydration pipeline (fully wired as of May 2026 audit):**
  1. `OnPlayerAuthenticated` sets `session.Health = session.MaxHealth` immediately as a safe default, then calls `_dataService.LoadPlayerProfileAsync(accountId)` — returns a `Task<PlayerProfile?>` immediately without blocking. Internally uses `StringGetAsync` (truly async, zero ThreadPool occupation during wait).
  2. The `(PlayerSession, Task<PlayerProfile?>)` pair is enqueued into `_pendingHydration` (`ConcurrentQueue`).
  3. `FinalizeHydration()` (called as phase 0 of `ProcessTick`) dequeues entries, skips incomplete tasks (re-enqueues them), and applies completed profiles via `HydrateFromProfile`.
  4. The tick thread is **never blocked** waiting for Redis. Players enter the match with base stats and receive their full profile within 1–10 ticks (typically < 1 tick on local Redis).
  5. Do not regress this to `LoadPlayerProfile` (synchronous); that blocked the game-loop thread for the full Redis round-trip on every player connect event.

## ProjectileState Snapshot Contract
- `ProjectileState` is a **struct** (not a class). Do not convert it back to a class; that would reintroduce per-spawn heap allocation on the game-loop thread.
- `ArenaInstance` stores projectiles in `ProjectileState[] _projectiles` (length 512) with a companion `int _projectileCount`. Do not replace this with `List<ProjectileState>` or any heap-backed collection.
- `ProjectileState` snapshots `DamageType`, `PierceChance`, and **`OwnerFaction`** from the spell/shooter at spawn time.
- These snapshots are immutable for the lifetime of the projectile.
- Do not re-read spell stats from `SpellDatabase` during projectile tick resolution; use the snapshot.
- `OwnerFaction` is snapshotted at spawn so that `MatchesFactionFilter` is O(1) — it uses a switch expression over `(filter, ownerFaction, candidate.Faction)`. Do **not** revert to scanning `allPlayers` to look up the owner's faction; that is O(N) per `(projectile × candidate)` pair.
- This prevents a future in-flight mutation window if `SpellDatabase` ever becomes hot-reloadable.

## ProjectileSystem API Contract
- `TrySpawnProjectile(..., out ProjectileState result)` is the spawn API. It returns `bool` and writes the new projectile to the `out` parameter (stack-allocated, zero heap alloc). The caller stores it in `_projectiles[_projectileCount++]`.
- Do not call `new ProjectileState { ... }` directly at the call site; always go through `TrySpawnProjectile`.
- `ProjectileSystem.Tick(ProjectileState[] projectiles, ref int projectileCount, List<PlayerSession> players, float delta, SpatialGrid? grid)` — uses `ref ProjectileState proj = ref projectiles[i]` for in-place mutation (zero struct copy), and `SwapRemove` for O(1) removal. **`players` must be `List<PlayerSession>`, not `IReadOnlyList<PlayerSession>`** — same vtable-elimination reason as `ProcessSpellCast`.
- `SwapRemove` replaces the removed slot with the last element and decrements the count — forward iteration with `i--` after removal preserves visit correctness.
- Pass `_spatialGrid` to `Tick` and `SendToInterested` inside the projectile broadcast path. Do not remove the grid parameter.

## ticksPerTick Invariant
- `ticksPerTick` (the `Stopwatch` tick count per game tick) must be computed as `Stopwatch.Frequency / TickRate` (integer division).
- Do **not** use `(long)(Stopwatch.Frequency * DeltaTime)` or any float-multiplication path. `1f/30f` is not exactly representable in IEEE 754; compounding rounding error causes phase drift over millions of ticks.
- `DeltaTime` (`1f / TickRate`) is still used as a `float` for physics integration (movement, projectile position). Only the heartbeat deadline uses the integer path.


- Server authority still intact for all touched mechanics.
- Faction visibility still correct for health and status effects.
- Tick order and phase boundaries unchanged or intentionally documented (movement → history snapshot → combat → status ticks → death → respawn → equip → pickup → win-check → broadcast).
- Position history recorded every tick before combat phase.
- No new hot-path allocations or LINQ introduced.
- `_entityMap` kept in sync on connect, disconnect, and grace-period eviction.
- Reusable list fields cleared before each use; no per-tick `new List<>()` allocations.
- `ProcessMeleeAttack` and `ProcessSingleTarget` still use historical position for range check.
- `EntityPositionPacket.ServerTick` and `AcknowledgedTick` still populated in BroadcastState.
- `PlayerInputPacket` axes remain `sbyte`; dequantization stays `value / 127f`.
- `_latestInputByPeer` stores `PlayerInputData` **structs**, not `PlayerInputPacket` class references. `EnqueueInput` must copy the scalar fields into a `PlayerInputData` struct immediately on receipt — never store the raw class reference. LiteNetLib reusable instances are overwritten by the next inbound packet of the same type, which would silently alias multiple peers to the same data.
- `ticksPerTick` must be `Stopwatch.Frequency / TickRate` (integer division). Do not use float multiplication.
- `ProjectileState` must remain a **struct**. `_projectiles` must remain a fixed `ProjectileState[512]` array with a companion `_projectileCount` int.
- `MatchesFactionFilter` must use the `OwnerFaction` field on `ProjectileState` (O(1) switch). Do not revert to an O(N) allPlayers scan.
- `ProjectileSystem.TrySpawnProjectile` must write to an `out ProjectileState` — never `return new ProjectileState`.
- `_projSpawnPacket` and `_projDestPacket` are pre-allocated struct instance fields on `ArenaInstance`. Mutate them in-place before each `SendToInterested` call inside the tick loop. Do not use `new ProjectileSpawnPacket` or `new ProjectileDestroyPacket` inside `ProcessTick`.
- `StatusEffectAppliedPacket` and `StatusEffectRemovedPacket` are now structs. `TryApplyStatusEffect` writes them via `out` (stack-allocated). `TickStatusEffects` adds them to `List<struct>` (value storage). Always pass them by `in` reference to `NetworkManager` overloads.
- `BroadcastStatusEffect`/`BroadcastStatusEffectRemoval` for `AlliesOnly` use `_spatialGrid.QueryNeighbours` — do not revert to iterating all `_players`.
- `FinalizeHydration()` called as phase 0 of `ProcessTick` — do not remove this call or replace it with a synchronous `LoadPlayerProfile` call inside `OnPlayerAuthenticated`.
- Packet semantics are still coherent with Unity client expectations.
- Pre-auth gating, auth timeout, and IP abuse controls still protect connection ingress.
- IntentGuard still enforces tick skew, replay resistance, and queue pressure limits.
- `GetHistoricalPosition` still uses `Math.Clamp` with both lower AND upper bounds — do not revert to `Math.Max`.
- `DisconnectAuthTimeoutPeers` still uses the pre-allocated `_timedOutPeers` list.
- `_ipGuards` eviction path (`EvictStaleIpGuards`) still wired into the tick loop.
- `ProcessSpellCast` still receives `entityMap` for O(1) single-target lookup.
- `ProcessSpellCast` receives both `_reusableSpellEvents` and `_reusableAoEHitEvents`.
- Security telemetry still records key drop categories.
- `PlayerSession.Armor` is gone; new mitigation stats must follow the absorb/resist/pierce model.
- `IsAlive` still includes `&& !IsRespawning`.
- `TickStatusEffects` receives `_entityMap` (`IReadOnlyDictionary<int, PlayerSession>`) — do not pass `_players`.
- `RemovePlayerFromList` is used for grace-period eviction, not on disconnect (ghost must remain).
- No `MatchDataService`/`PlayerStateSink` methods called from inside `ProcessTick` or `BroadcastState`.
- `ARENA_TICKET_SECRET` is read from environment, not from `appsettings.json`.
- `BroadcastState` skips `Peer == null` ghost sessions.
- All combat events use `SendToInterested` with `_zone.EventFilter`; do not use `SendToAll` for events.
- `TryEquipItem`/`TryUnequipSlot`/`TryApplyGearSet` called without `isPermitted` parameter.
- `BuildStatsPacket()` used instead of removed `BuildStatsRefreshedPacket(byte gearSetIndex)`.
- `TickStatusEffects` return value (`bool statsChanged`) checked; if `true`, `BuildStatsPacket()` sent to peer.

## PR Gate Checklist
- Build succeeds for GameServer and SharedLibrary.
- New/changed packets are justified and not leaking private data.
- All new mechanics specify faction targeting and visibility behavior.
- Life-steal and DoT interactions were sanity-checked for runaway sustain.
- Any behavior change to cooldowns, targeting, or hit resolution is explicitly called out.
- Lag-compensation rewind depth change requires review of the historical buffer size (64 slots) and `MaxRewindTicks` cap.
- Input axis type must remain `sbyte` unless a coordinated client/server/protocol migration is planned.
- Damage pipeline changes must go through `CombatMath.CalculateDamage` — no inline damage formulas in combat systems.
- New mitigation mechanics must follow the absorb→resist→pierce ordering — do not add a fourth stage without documenting it here.
- Match lifecycle changes (new end conditions, respawn rules) must update this skill file.
- New stat modifiers must use `StatModifier` struct and flow through `RecomputeStats()` layers.
- New zone behaviors must be expressed as `ZoneDescriptor` fields or strategies, not `ArenaInstance` mode branches.
- Any new `PlayerSession` method that changes stats must call `RecomputeStats()` and return `BuildStatsPacket()` to the caller.
- Grace-period logic must not be bypassed. `TryValidateForRejoin` is only valid after grace-set membership is confirmed.
- `OnPlayerAuthenticated` must use `LoadPlayerProfileAsync` + `_pendingHydration` enqueue. Never reintroduce `LoadPlayerProfile` (synchronous) at this call site. `LoadPlayerProfileAsync` must use `StringGetAsync` internally — never `Task.Run(blocking)` around `StringGet`.
- `ticksPerTick` must use integer division (`Stopwatch.Frequency / TickRate`). Float-multiplication drift is a latent bug.
- `ProjectileState` must remain a struct. Revert-to-class PRs are rejected.
- `MatchesFactionFilter` must remain O(1) (switch on `OwnerFaction`). Revert-to-O(N)-scan PRs are rejected.
- `ComputeCraftingRewards` must use the pre-allocated `_rewardScratch` field — never `new[] { ... }` inside the method body.
- New game-loop-thread logging must use separate `Console.Write`/`WriteLine` literal calls, not string interpolation.
- `CombatSystem.ProcessSpellCast` / `ProcessAoE` / `ProcessMeleeSplash` and `ProjectileSystem.Tick` / `ApplyExplosiveSplash` must accept `List<PlayerSession>`, not `IReadOnlyList<PlayerSession>`. Widening to the interface re-introduces vtable dispatch on every inner-loop element access.
- `SecurityTelemetry` audit/snapshot methods must remain off-thread (`ThreadPool.QueueUserWorkItem<TState>` with static lambdas). Do not move string interpolation or `Console.WriteLine` back onto the game-loop thread.
- `NetworkManager.OnPeerConnected`, `OnPeerDisconnected`, and `OnNetworkError` must log off-thread. Do not add inline `Console.WriteLine` calls to these handlers.

## If Extending Mechanics
When adding new mechanics, preserve these invariants:
- Server validates all gameplay constraints.
- Faction visibility constraints are enforced at send-time.
- New packets do not leak hidden allied-only information to enemies.
- Per-tick logic remains allocation-conscious.
- SharedLibrary stays protocol-safe and backwards-conscious for Unity client integration.
