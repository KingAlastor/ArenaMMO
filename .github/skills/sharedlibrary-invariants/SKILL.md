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
- EntityPositionPacket and EntityHealthPacket are intentionally split.
  - Position is broadly shareable.
  - Health is a privileged stream controlled by server faction visibility.
- Status effect lifecycle packets exist and are visibility-aware:
  - StatusEffectAppliedPacket
  - StatusEffectRemovedPacket

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
- Keep float-versus-int choices intentional (simulation precision vs payload size).
- Preserve existing packet field ordering conventions unless there is a migration plan.

## Security and Trust Model
- SharedLibrary defines data contracts, not trust.
- Do not introduce fields that imply client authority over final state.
- The server remains the source of truth for all final combat outcomes.

## Review Checklist
- No contract change accidentally grants client authority.
- Packet/enums remain Unity-compatible and .NET Standard 2.1-safe.
- New fields have clear semantics and safe defaults.
- Shared contracts still align with faction visibility and privacy constraints.
- CombatMath remains pure and allocation-free.

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
