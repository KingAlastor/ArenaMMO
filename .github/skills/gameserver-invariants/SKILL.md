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

## Simulation Model
- Fixed tick loop at 30 Hz in ArenaInstance.
- Tick order matters and should stay stable:
  1. Movement input (consume latest PlayerInputPacket per peer, normalize, apply)
  2. Record position history — call `PlayerSession.RecordPositionHistory(_tick)` on every player immediately after movement
  3. Melee attacks
  4. Spell casts
  5. Shoot/projectile spawn
  6. Projectile tick and resolution
  7. Status effect tick processing (periodic effects)
  8. Broadcast snapshots/events
- Maintain deterministic, server-first sequencing wherever possible.

## Tick-Order Contract
If a change affects one phase, validate the neighboring phases still operate correctly:
- Movement before position history snapshot — history must capture the post-movement position.
- Position history snapshot before combat validations — lag-compensation rewind depends on fresh history.
- Spell/projectile spawning before projectile movement resolution.
- Status periodic ticks after direct-hit damage resolution.
- Snapshot broadcasts after authoritative state mutation is complete for the tick.

## Performance Constraints
- Keep hot paths allocation-light.
- Avoid LINQ in per-tick logic.
- Prefer for-loops and reusable lists/buffers.
- Avoid per-entity temporary object churn inside tick loops.
- Keep checks branch-light and data-oriented in CombatSystem, ProjectileSystem, and ArenaInstance.
- `ArenaInstance._entityMap` (`Dictionary<int, PlayerSession>`) provides O(1) entity lookup by EntityId.
  - Keep it in sync with `_players` and `_peerMap` at all authentication and disconnect events.
  - Replace any O(N) linear `FindById` scans with a dictionary lookup against `_entityMap`.
- Reuse pre-allocated list fields `_reusableStatusEffects` and `_reusableSpellEvents` in the tick drain loops.
  - Call `.Clear()` before each use; never allocate `new List<>()` inside per-tick or per-dequeue hot paths.
- `NetworkManager._sharedWriter` is a single pooled `NetDataWriter` for all send calls.
  - All sends occur on the single game-loop thread; no lock is needed.
  - Do not allocate `new NetDataWriter()` per send call.
- `NetworkManager._timedOutPeers` is a pre-allocated `List<NetPeer>` reused by `DisconnectAuthTimeoutPeers()`.
  - Do not replace it with `new List<NetPeer>()` inside the method body; that re-introduces per-tick heap allocation during connect floods.
- `NetworkManager._ipGuards` (`ConcurrentDictionary<IPAddress, IpGuardState>`) is periodically evicted by `EvictStaleIpGuards()`.
  - Eviction runs every `IpGuardEvictionIntervalTicks (300)` ticks (~10 s), called from `DisconnectAuthTimeoutPeers`.
  - Only entries where the IP is neither currently banned nor carrying a nonzero violation score are removed.
  - Do not remove this eviction path; without it an IPv6-spoofed unique-source-address flood grows the dictionary without bound.

## Player and Faction Rules
- Every PlayerSession has a Faction.
- Friendly/allied behavior is faction-based.
- Hostile behavior targets enemies unless explicitly configured otherwise.

## State Replication Rules
- Position and health are intentionally separated:
  - EntityPositionPacket is sent broadly.
  - EntityHealthPacket is sent only to allied viewers.
- Do not reintroduce combined position+health packets that leak hidden health data.

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
  - `IReadOnlyList<PlayerSession> allPlayers` — used for AoE and MeleeSplash iteration.
  - `IReadOnlyDictionary<int, PlayerSession> entityMap` — O(1) lookup for single-target resolution; always pass `ArenaInstance._entityMap`.
  - `List<CombatEventPacket> results` — pre-allocated `_reusableSpellEvents` list; clear it before each call.
  - Do not revert to a return-value `List<>` pattern.
  - Do not remove the `entityMap` parameter; it prevents the O(N) linear scan that a flooded spell queue can amplify into a CPU spike proportional to player count.


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

## Safety and Persistence
- Do not add direct SQL access into active match simulation code.
- Match state lives in memory during simulation.
- Preserve queue-based separation between network receive and simulation processing.
- Preserve auth-first flow: no gameplay intent should mutate state for unauthenticated peers.

## Review Checklist
- Server authority still intact for all touched mechanics.
- Faction visibility still correct for health and status effects.
- Tick order and phase boundaries unchanged or intentionally documented (movement → history snapshot → combat).
- Position history recorded every tick before combat phase.
- No new hot-path allocations or LINQ introduced.
- `_entityMap` kept in sync on connect and disconnect.
- Reusable list fields cleared before each use; no per-tick `new List<>()` allocations.
- `ProcessMeleeAttack` and `ProcessSingleTarget` still use historical position for range check.
- `EntityPositionPacket.ServerTick` and `AcknowledgedTick` still populated in BroadcastState.
- `PlayerInputPacket` axes remain `sbyte`; dequantization stays `value / 127f`.
- Packet semantics are still coherent with Unity client expectations.
- Pre-auth gating, auth timeout, and IP abuse controls still protect connection ingress.
- IntentGuard still enforces tick skew, replay resistance, and queue pressure limits.
- `GetHistoricalPosition` still uses `Math.Clamp` with both lower AND upper bounds — do not revert to `Math.Max` (lower-bound only).
- `DisconnectAuthTimeoutPeers` still uses the pre-allocated `_timedOutPeers` list — no per-call `new List<NetPeer>()`.
- `_ipGuards` eviction path (`EvictStaleIpGuards`) still wired into the tick loop via `DisconnectAuthTimeoutPeers`.
- `ProcessSpellCast` still receives `entityMap` for O(1) single-target lookup — `allPlayers` list is not the lookup path for single-target spells.
- Security telemetry still records key drop categories for incident analysis.

## PR Gate Checklist
- Build succeeds for GameServer and SharedLibrary.
- New/changed packets are justified and not leaking private data.
- All new mechanics specify faction targeting and visibility behavior.
- Life-steal and DoT interactions were sanity-checked for runaway sustain.
- Any behavior change to cooldowns, targeting, or hit resolution is explicitly called out.
- Lag-compensation rewind depth change requires review of the historical buffer size (64 slots) and `MaxRewindTicks` cap.
- Input axis type must remain `sbyte` unless a coordinated client/server/protocol migration is planned.

## If Extending Mechanics
When adding new mechanics, preserve these invariants:
- Server validates all gameplay constraints.
- Faction visibility constraints are enforced at send-time.
- New packets do not leak hidden allied-only information to enemies.
- Per-tick logic remains allocation-conscious.
- SharedLibrary stays protocol-safe and backwards-conscious for Unity client integration.
