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
- projectile behavior (speed, hit radius, hit chance falloff, pierce)
- life-steal percentage on hit
- optional status effect application data
- optional periodic status effect tick model:
  - tick damage
  - tick interval
  - owner-heal percentage from each tick

Do not move authoritative validation into SharedLibrary data alone; data describes, server enforces.

## SpellDefinition Change Rules
- Keep fields data-only; no hidden execution logic in SharedLibrary.
- For new mechanics, include enough parameters for server-side enforcement.
- For faction-sensitive behavior, include explicit targeting/visibility fields when needed.

## CombatMath Rules
- CombatMath should remain pure and stateless.
- No hidden RNG state inside SharedLibrary.
- Keep methods allocation-free and side-effect-free.
- Favor methods that avoid unnecessary sqrt in hot paths where possible.

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
