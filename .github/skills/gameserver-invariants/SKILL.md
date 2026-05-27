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

## Simulation Model
- Fixed tick loop at 30 Hz in ArenaInstance.
- Tick order matters and should stay stable:
  1. Movement input
  2. Melee attacks
  3. Spell casts
  4. Shoot/projectile spawn
  5. Projectile tick and resolution
  6. Status effect tick processing (periodic effects)
  7. Broadcast snapshots/events
- Maintain deterministic, server-first sequencing wherever possible.

## Tick-Order Contract
If a change affects one phase, validate the neighboring phases still operate correctly:
- Movement before combat validations.
- Spell/projectile spawning before projectile movement resolution.
- Status periodic ticks after direct-hit damage resolution.
- Snapshot broadcasts after authoritative state mutation is complete for the tick.

## Performance Constraints
- Keep hot paths allocation-light.
- Avoid LINQ in per-tick logic.
- Prefer for-loops and reusable lists/buffers.
- Avoid per-entity temporary object churn inside tick loops.
- Keep checks branch-light and data-oriented in CombatSystem, ProjectileSystem, and ArenaInstance.

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

## Combat Rules to Preserve
- Melee basic attack:
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

## Review Checklist
- Server authority still intact for all touched mechanics.
- Faction visibility still correct for health and status effects.
- Tick order and phase boundaries unchanged or intentionally documented.
- No new hot-path allocations or LINQ introduced.
- Packet semantics are still coherent with Unity client expectations.

## PR Gate Checklist
- Build succeeds for GameServer and SharedLibrary.
- New/changed packets are justified and not leaking private data.
- All new mechanics specify faction targeting and visibility behavior.
- Life-steal and DoT interactions were sanity-checked for runaway sustain.
- Any behavior change to cooldowns, targeting, or hit resolution is explicitly called out.

## If Extending Mechanics
When adding new mechanics, preserve these invariants:
- Server validates all gameplay constraints.
- Faction visibility constraints are enforced at send-time.
- New packets do not leak hidden allied-only information to enemies.
- Per-tick logic remains allocation-conscious.
- SharedLibrary stays protocol-safe and backwards-conscious for Unity client integration.
