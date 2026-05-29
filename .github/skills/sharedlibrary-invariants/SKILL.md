---
name: sharedlibrary-invariants
description: "Use when editing ArenaMMO SharedLibrary packets, enums, SpellDefinition, or CombatMath; preserves protocol compatibility, authority boundaries, and Unity-safe shared contracts."
---

# SharedLibrary Invariants

## Purpose
This skill defines protocol and shared-combat invariants Copilot must preserve when editing SharedLibrary.

## Core Identity
- SharedLibrary is the contract between server and Unity client.
- It contains shared enums, value types, packets, and pure combat math.
- Keep it deterministic, lightweight, and serialization-friendly.

## Do
- Prefer additive, backward-aware packet and enum evolution.
- Keep shared types explicit and simple for LiteNetLib serialization.
- Preserve authority boundaries: contracts describe data, server enforces legality.
- Keep CombatMath pure and side-effect free.
- Keep new fields semantically clear and documented.

## Don't
- Do not introduce client-authoritative semantics into packet contracts.
- Do not remove or repurpose existing enum values casually.
- Do not add heavy framework dependencies incompatible with Unity/.NET Standard 2.1.
- Do not hide gameplay side effects inside SharedLibrary utilities.
- Do not couple packet definitions to server-only persistence concerns.
- Do not reorder canonical ticket fields without coordinated signer/verifier updates.
- Do not remove action sequence fields from action intent packets; replay resistance depends on them.
- Do not revert `PlayerInputPacket.InputX`/`InputY` from `sbyte` back to `float`; this would reintroduce cross-platform FP normalization divergence between Unity and .NET runtimes.
- Do not remove `EntityPositionPacket.ServerTick` or `AcknowledgedTick`; the Unity client depends on these for prediction reconciliation.

## Compatibility Target
- SharedLibrary targets .NET Standard 2.1.
- Avoid APIs that break Unity compatibility.
- Keep structs/classes simple and explicit for LiteNetLib packet serialization.

## Protocol Design Rules
- Additive changes are preferred over breaking changes.
- Preserve field intent and semantic meaning of existing packets.
- Packet-level visibility and authority assumptions must remain explicit.

## Packet Evolution Contract
- New fields should default safely when older clients/servers interoperate.
- Renames and semantic repurposes are high-risk; prefer introducing new fields/packets.
- If a packet transports private data, pair it with explicit visibility strategy in GameServer.

## Current Packet Invariants
- `PlayerInputPacket.InputX` and `InputY` are `sbyte` (range −127..127), not `float`.

## Packet Catalogue
Packets are grouped by direction and purpose.

### Client → Server (intent)
- `AuthTicketPacket` — pre-auth identity, faction, spell entitlement, HMAC signature.
- `PlayerInputPacket` — raw movement axes (sbyte) + tick number.
- `AttackRequestPacket` — melee attack intent + `ActionSequenceId`.
- `SpellCastRequestPacket` — spell cast intent + AoE center + tick number + `ActionSequenceId`.
- `ShootRequestPacket` — shoot intent + direction + `ActionSequenceId`.
- `GroundItemPickupRequestPacket` — client requests pickup of a specific ground item. Fields: `int GroundItemId`. Server validates ownership, distance, and inventory space.

### Server → Client (authoritative state)
- `EntityPositionPacket` — authoritative position + `ServerTick` + `AcknowledgedTick` for reconciliation.
- `EntityHealthPacket` — faction-gated health update (allies only).
- `EntitySpawnPacket` — broadcast when a player successfully authenticates and enters the arena. Fields: `EntityId`, `PlayerName`, `Faction`, `X`, `Y`.
- `EntityDespawnPacket` — broadcast when a player disconnects. Fields: `EntityId`.
- `CombatEventPacket` — single-target hit event (melee or single-target spell). Fields: `AttackerId`, `TargetId`, `Damage`, `IsCritical`.
- `AoEHitEventPacket` — one packet per entity hit by an AoE or MeleeSplash spell. Fields: `CasterId`, `SpellId`, `HitEntityId`, `Damage`, `IsCritical`. **Note:** the hit-target field is `HitEntityId` — there is no `TargetId` field on this packet. Do not reference `TargetId` here; that causes compile errors.
- `ProjectileSpawnPacket` — authoritative projectile creation event.
- `ProjectileDestroyPacket` — projectile removal event (hit or expiry).
- `StatusEffectAppliedPacket` — visibility-filtered status effect application.
- `StatusEffectRemovedPacket` — visibility-filtered status effect expiry.
- `PlayerDeathPacket` — broadcast on kill. Fields: `KilledEntityId`, `KillerEntityId`.
- `PlayerRespawnPacket` — broadcast when a player re-enters play after respawn timer. Fields: `EntityId`, `X`, `Y`, `Health`.
- `MatchEndPacket` — broadcast once when win condition is met. Fields: `WinnerFaction` (byte).
- `GroundItemSpawnedPacket` — sent to interested viewers when a ground item appears. Fields: `int GroundItemId, int DefinitionId, float X, float Y`.
- `GroundItemRemovedPacket` — sent to interested viewers when a ground item is picked up or despawned. Fields: `int GroundItemId`.
- `ItemAddedToInventoryPacket` — sent only to the owning client when an item enters their inventory. Fields: `int InstanceId, int DefinitionId`.
- `PlayerGraceDisconnectPacket` — sent to all peers when a player loses connection but enters the grace-period. Fields: `int EntityId`. Clients show a disconnected indicator; the ghost entity remains in world.
- `PlayerReconnectedPacket` — sent to all peers when a grace-period player successfully rejoins. Fields: `int EntityId`.
- `CraftingRewardPacket` — sent to the owning client at Arena match end to notify earned crafting ingredients. Fields: `string RewardsCsv` (format `"id:qty,id:qty,..."`). Parsing and crediting happen in ProfileServer via the Redis `crafting-reward:{accountId}` key.

### Packet Design Rules
- Do not add client-authoritative semantics to any packet contract.
- Server→Client packets describe authoritative state — the client visualizes them, not trusts them for game logic.
- `AoEHitEventPacket` is sent via `SendToInterested` (not guaranteed broadcast) in open-world zones; in Arena mode with `BroadcastFilter` all peers receive it. Do not assume global broadcast in all zone types.
- `AoEHitEventPacket.HitEntityId` is the correct field name — not `TargetId`. Do not confuse with `CombatEventPacket.TargetId`.
- `EntitySpawnPacket` and `EntityDespawnPacket` are lifecycle signals; the Unity client drives entity creation/destruction from them.
- `PlayerDeathPacket` and `PlayerRespawnPacket` drive the client respawn UI; the server's respawn timer is authoritative.
- `PlayerGraceDisconnectPacket` / `PlayerReconnectedPacket` drive disconnected-player UI indicators; they do not remove the entity from the world.
  - Both client and server dequantize via `value / 127f` — guaranteed identical math on all platforms.
  - This eliminates per-platform FP normalization drift (ARM vs x86 auto-vectorization, FMA fusing).
  - `sbyte` has no NaN/Inf; finite-value guards are unnecessary for these fields.
- `EntityPositionPacket` carries two reconciliation tick fields in addition to position:
  - `ServerTick` (int) — the server tick that produced this snapshot.
  - `AcknowledgedTick` (int) — the last `PlayerInputPacket.TickNumber` the server consumed for this entity.
  - The Unity client uses these to discard buffered inputs ≤ `AcknowledgedTick` and replay the remaining tail against the corrected position.
- `EntityPositionPacket` and `EntityHealthPacket` are intentionally split.
  - Position is broadly shareable.
  - Health is a privileged stream controlled by server faction visibility.
- Status effect lifecycle packets exist and are visibility-aware:
  - StatusEffectAppliedPacket
  - StatusEffectRemovedPacket
- AuthTicketPacket is the pre-auth identity/entitlement contract and includes:
  - player identity and faction
  - allowed spell entitlement list
  - issued/expiry timestamps
  - nonce and HMAC signature
- AttackRequestPacket, SpellCastRequestPacket, and ShootRequestPacket carry ActionSequenceId for server-side replay rejection.

## Auth and Intent Contract Rules
- AuthTicketPacket canonicalization order is part of protocol compatibility; signer and verifier must stay in lockstep.
- Keep ticket fields explicit and string/integer based for serializer stability across Unity and server runtimes.
- ActionSequenceId semantics are monotonic per action stream and validated server-side; packet contracts must preserve this field.
- TickNumber and targeting payloads are untrusted hints, not authority; contracts must not imply client final-state authority.
- AllowedSpellIdsCsv remains data-only entitlement transport; gameplay legality is still enforced in GameServer.

## Enum and Targeting Invariants
- SpellTargetType defines simulation mode (single target, AoE, projectile, melee splash).
- TargetFactionFilter defines ally/enemy/any legality checks (enforced server-side).
- StatusEffectVisibility defines who is allowed to observe status state.

## SpellDefinition Responsibilities
SpellDefinition is immutable runtime input data and includes:
- targeting and damage profile
- projectile behavior (speed, hit radius, hit chance falloff, pierce count, pierce chance)
- life-steal percentage on hit
- optional status effect application data
- optional periodic status effect tick model:
  - tick damage
  - tick interval
  - owner-heal percentage from each tick
- `PierceChance` (float 0–1) — per-hit probability that the projectile or spell bypass the target's resist mitigation (absorb is never bypassed). Added alongside `BasePierceCount`.

Do not move authoritative validation into SharedLibrary data alone; data describes, server enforces.

## SpellDefinition Change Rules
- Keep fields data-only; no hidden execution logic in SharedLibrary.
- For new mechanics, include enough parameters for server-side enforcement.
- For faction-sensitive behavior, include explicit targeting/visibility fields when needed.

## Shared Value Types

### `WorldBounds` (`readonly struct`)
- Fields: `float MinX, MaxX, MinY, MaxY`.
- Constructor: `WorldBounds(float halfX, float halfY)` — sets MinX/MinY to `-halfX`/`-halfY`, MaxX/MaxY to `+halfX`/`+halfY`.
- `static readonly DefaultArena = new WorldBounds(50f, 50f)` — 100×100 unit arena.
- Used by `CombatMath.Move` and `InputSanitizer` for out-of-bounds coordinate rejection.
- Do not replace with a mutable class or add server-only logic into this type.

### `StatModifier` (`struct`, GameServer-only but defined for cross-system use)
- Fields: `float MaxHealth, AttackPower, PhysAbsorb, PhysResist, MagAbsorb, MagResist, CritChance, MeleeLifeSteal, ProjectileRangeBonus; int ProjectilePierceBonus`.
- `bool HasAnyValue` — true if any field is non-zero; gates the `RecomputeStats` iteration check.
- `static readonly Zero` — default empty modifier.
- All fields are additive deltas, not multipliers; they are summed on top of base stats during `RecomputeStats`.
- Do not store a `StatModifier` on SharedLibrary packet types; it is a server-side accumulation structure only.

## CombatMath Rules
- CombatMath should remain pure and stateless.
- No hidden RNG state inside SharedLibrary.
- Keep methods allocation-free and side-effect-free.
- Favor methods that avoid unnecessary sqrt in hot paths where possible.
- `CombatMath.ArenaBoundsHalf` has been **removed**. Do not re-introduce this constant; bounds are now injected via `WorldBounds` at call-site.
- `CombatMath.Move(Vec2 current, float inputX, float inputY, float deltaTime, in WorldBounds bounds, float speed = DefaultMoveSpeed)` — the `in WorldBounds bounds` parameter replaces the old hardcoded arena bounds. Always pass `_zone.Bounds` from `ZoneDescriptor`.

## CombatMath.CalculateDamage Contract
Current signature:
```csharp
public static int CalculateDamage(
    int baseDamage, float attackPower, DamageType damageType,
    float absorbPercent, float resistPercent,
    float pierceChance, double pierceRoll)
```
Formula:
```
raw = baseDamage × attackPower
True  → return max(1, raw)
Physical/Magic:
  afterAbsorb = raw × (1 − clamp(absorbPercent, 0, 1))
  if (pierceRoll < pierceChance) skip resist step
  afterResist = afterAbsorb × (1 − clamp(resistPercent, 0, 1))
  return max(1, afterResist)
```
- The old `CalculateDamage(int baseDamage, float attackPower, float armor)` signature is **removed**.
  - Do not re-introduce the flat-armor formula; it ignored damage type and was vulnerable to unintended stacking.
- `pierceRoll` is provided by the caller from `Random.Shared.NextDouble()` so CombatMath stays RNG-free.
- `DamageType.True` bypasses both absorb and resist; it is intended only for effects that should always deal their full value.
- `absorbPercent` and `resistPercent` are clamped internally to [0, 1]; values outside this range are safe to pass.
- Minimum final damage is always 1 (cannot fully absorb or resist into zero damage).

## Serialization and Stability
- Keep packet fields primitive/simple.
- Prefer explicit numeric types and stable enum values.
- If adding packet fields, ensure server and client rollout compatibility.

## Numeric and Enum Stability
- Avoid changing underlying numeric values of shipped enums.
- `PlayerInputPacket` input axes are `sbyte` by design — a protocol-breaking change to any wider type requires coordinated client/server/lobby rollout.
- `EntityPositionPacket.ServerTick` and `AcknowledgedTick` are protocol fields; do not repurpose or remove them without a migration plan.
- Keep float-versus-int choices intentional (simulation precision vs payload size).
- Preserve existing packet field ordering conventions unless there is a migration plan.

## Security and Trust Model
- SharedLibrary defines data contracts, not trust.
- Do not introduce fields that imply client authority over final state.
- The server remains the source of truth for all final combat outcomes.
- Security-sensitive packet changes (auth/signature/sequence fields) require rollout compatibility notes for lobby, server, and Unity client.

## Review Checklist
- No contract change accidentally grants client authority.
- Packet/enums remain Unity-compatible and .NET Standard 2.1-safe.
- New fields have clear semantics and safe defaults.
- Shared contracts still align with faction visibility and privacy constraints.
- `PlayerInputPacket.InputX`/`InputY` remain `sbyte`; dequantization is `value / 127f`.
- `EntityPositionPacket` still carries `ServerTick` and `AcknowledgedTick`.
- CombatMath remains pure and allocation-free.
- AuthTicketPacket and action intent packet changes preserve anti-replay and auth compatibility assumptions.
- `CombatMath.CalculateDamage` signature still includes `DamageType`, `absorbPercent`, `resistPercent`, `pierceChance`, `pierceRoll` — do not collapse back to flat-armor form.
- `CombatMath.Move` takes `in WorldBounds bounds` — do not revert to hardcoded constant or remove the parameter.
- `SpellDefinition.PierceChance` (float 0–1) and `BasePierceCount` (int) both present and semantically distinct.
- New lifecycle packets (`EntitySpawnPacket`, `EntityDespawnPacket`, `PlayerDeathPacket`, `PlayerRespawnPacket`, `MatchEndPacket`) not repurposed for game-logic authority.
- `AoEHitEventPacket` references `HitEntityId`, not `TargetId`.
- New ground-item / inventory packets sent only to interested or owning-peer audiences as documented in the catalogue.

## PR Gate Checklist
- GameServer and SharedLibrary compile after contract edits.
- Any packet/enum additions include rollout compatibility notes.
- High-risk changes (renames/repurposes/removals) are explicitly justified.
- Client-impacting contract changes are documented for Unity implementation updates.
- Data-only boundaries are preserved (no server authority leakage into shared models).

## If Extending Shared Types
When adding mechanics, preserve:
- backwards-aware packet evolution
- minimal payload leakage of private information
- strong alignment with server-authoritative constraints
- Unity-friendly, .NET Standard-safe API surface
