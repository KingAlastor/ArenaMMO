# Integration Test Architecture Guide

## Design Principles

This test harness follows modern async/await patterns and server-authoritative game architecture principles:

### 1. **Server-Authoritative Design**
- The server owns all state mutation
- Clients submit intents, server validates and applies
- No client-side prediction validation in test (clients trust server)
- Reconciliation packets correct client state when needed

### 2. **Deterministic Simulation**
- Fixed 30 Hz tick rate eliminates floating-point drift issues
- All movement, combat, and physics use integer ticks
- Tests can inspect state at known tick boundaries
- No randomness in core movement (only cosmetic/RNG-dependent systems)
- The production game loop uses an **absolute-deadline heartbeat** (`Stopwatch.GetTimestamp` + `Thread.SpinWait`) for drift-free timing; the test harness bypasses this entirely and advances ticks on demand via the semaphore

### 3. **Asynchronous Test Coordination**
- Test thread remains responsive during server simulation
- Semaphore-based tick synchronization prevents busy-waiting
- `await WaitForPositionUpdate()` blocks only as long as needed
- Timeouts prevent hangs from logic errors

### 4. **Zero-Copy Packet Routing**
- Packets stored as-is in pseudo-client collections
- No serialization/deserialization except in real networking
- Tests inspect packets directly without marshalling overhead
- Hot-path packets (`EntityPositionPacket`, `EntityHealthPacket`, `CombatEventPacket`, `AoEHitEventPacket`) are **structs** — value types passed by value; test code must use the struct field API rather than property access that existed on the old classes
- Positions in `EntityPositionPacket` are **fixed-point `short`** fields (`X`, `Y`). Use `PacketEncoding.DecodePosition(packet.X)` to get the `float` world coordinate. `Health` in `EntityHealthPacket` is a `ushort`; use `PacketEncoding.DecodeHealth(packet.Health)` to get `float` HP.

## Implementation Patterns

### Pattern 1: Basic Intent → Assert Cycle

```csharp
[Fact]
public async Task MyTest()
{
    var client = _testHost.RegisterClient("Player", FactionId.Alpha);
    await _testHost.WaitForTicksAsync(2);  // Spawn

    var initialPos = client.CurrentPosition;
    
    // Act
    client.SendMovementIntent(127, 0);
    await _testHost.WaitForTicksAsync(1);
    
    // Assert
    var finalPos = client.CurrentPosition;
    finalPos.X.Should().BeGreaterThan(initialPos.X);
}
```

### Pattern 2: Multi-Player Synchronization

```csharp
[Fact]
public async Task MultiplayerTest()
{
    var clientA = _testHost.RegisterClient("A", FactionId.Alpha);
    var clientB = _testHost.RegisterClient("B", FactionId.Beta);
    await _testHost.WaitForTicksAsync(2);

    // Send simultaneous intents
    clientA.SendMovementIntent(127, 0);
    clientB.SendMovementIntent(-127, 0);
    
    // Tick once
    await _testHost.WaitForTicksAsync(1);
    
    // Both should receive updates
    var updateA = await clientA.WaitForPositionUpdate(TimeSpan.FromSeconds(1));
    var updateB = await clientB.WaitForPositionUpdate(TimeSpan.FromSeconds(1));
    
    updateA.Should().NotBeNull();
    updateB.Should().NotBeNull();
}
```

### Pattern 3: Fluent Builder for Complex Scenarios

```csharp
[Fact]
public async Task ComplexScenarioTest()
{
    await new GameServerTestScenarioBuilder(_testHost)
        .WithClient("A", FactionId.Alpha)
        .WithClient("B", FactionId.Beta)
        .WaitingForTicks(2)
        .Execute(clients => clients[0].SendMovementIntent(127, 0))
        .Execute(clients => clients[1].SendMovementIntent(-127, 0))
        .ExecuteAsync(async clients =>
        {
            var updateA = await clients[0].WaitForPositionUpdate(TimeSpan.FromSeconds(1));
            updateA.Should().NotBeNull();
        })
        .Clients;
}
```

### Pattern 4: State Snapshot Validation

```csharp
[Fact]
public async Task StateSnapshotTest()
{
    var client = _testHost.RegisterClient("Player", FactionId.Alpha);
    await _testHost.WaitForTicksAsync(2);
    
    for (int i = 0; i < 5; i++)
    {
        client.SendMovementIntent(127, 0);
    }
    
    await _testHost.WaitForTicksAsync(5);
    
    var finalPos = client.CurrentPosition;
    var allPackets = client.AllReceivedPackets.ToList();
    var broadcastHistory = _testHost.GetBroadcastHistory();
    
    // Validate from multiple perspectives
    finalPos.X.Should().BeGreaterThan(0);
    
    // EntityPositionPacket is now a struct with fixed-point short fields.
    // Use PacketEncoding.DecodePosition() to convert back to float world coords.
    var posPackets = allPackets.OfType<EntityPositionPacket>().ToList();
    posPackets.Should().NotBeEmpty();
    float worldX = PacketEncoding.DecodePosition(posPackets.Last().X);
    worldX.Should().BeGreaterThan(0f);
    
    // EntityHealthPacket.Health is a ushort — decode with PacketEncoding.DecodeHealth()
    var healthPackets = allPackets.OfType<EntityHealthPacket>().ToList();
    if (healthPackets.Count > 0)
    {
        float hp = PacketEncoding.DecodeHealth(healthPackets.Last().Health);
        hp.Should().BeGreaterThan(0f);
    }
}
```

## Thread Safety Guarantees

### Write Safety
```
Test Thread              Game Loop Thread
─────────────────────────────────────────
client.SendIntent()  ──► ConcurrentQueue (outbound)
                         ├─ DrainClientIntents()
                         ├─ Process into ArenaInstance
                         └─ Input Queues
```

**Guarantee:** Each outbound intent is processed exactly once, in order per client, on the game loop thread.

### Read Safety
```
Game Loop Thread              Test Thread
───────────────────────────────────────────
SpatialGrid.RebuildEachTick() // after movement, before broadcast
BroadcastState()  ► ConcurrentDict (packets)
  └─ delta check: skip pos/health if encoding unchanged
  └─ CommitBroadcastState() — update sentinels AFTER all viewers
                      ├─ Indexed by tick
                      ├─ Keyed by entity ID
                      └─ Test reads via AllReceivedPackets
```

**Guarantee:** Packets are immutable after insertion; test thread can read safely. The `SpatialGrid` scratch buffer (`_neighbourScratch`) is only mutated on the game-loop thread and is never exposed to test code.

**Delta compression note:** `EntityPositionPacket` and `EntityHealthPacket` are now suppressed when the fixed-point-encoded value is unchanged from the previous tick. Tests that assert a position packet was received should ensure the player actually moved that tick (or is the own-entity, which always receives a position packet for reconciliation). Use `WaitForPositionUpdate()` which waits for a fresh packet to arrive rather than checking `CurrentPosition` directly.

### Synchronization
```
Game Loop Thread              Test Thread
───────────────────────────────────────────
RunGameLoop()
├─ DrainIntents()
├─ ProcessTick()
├─ BroadcastState()
└─ Release(tickSemaphore) ──► WaitForTicksAsync() returns
                             [Test thread resumes]
```

**Guarantee:** Semaphore ensures test doesn't inspect state until tick is fully complete.

## Performance Optimization Tips

### 1. Batch Multiple Intents Before Ticking
```csharp
// ✓ Good: O(1) frame rate
for (int i = 0; i < 5; i++)
    client.SendMovementIntent(100, 0);
await _testHost.WaitForTicksAsync(1);

// ✗ Slow: O(N) frame rate
for (int i = 0; i < 5; i++)
{
    client.SendMovementIntent(100, 0);
    await _testHost.WaitForTicksAsync(1);  // Unnecessary wait
}
```

### 2. Register All Clients Before Waiting
```csharp
// ✓ Good: Single wait for spawn phase
var c1 = _testHost.RegisterClient("A", ...);
var c2 = _testHost.RegisterClient("B", ...);
var c3 = _testHost.RegisterClient("C", ...);
await _testHost.WaitForTicksAsync(2);

// ✗ Slow: Multiple spawn waits
var c1 = _testHost.RegisterClient("A", ...);
await _testHost.WaitForTicksAsync(2);
var c2 = _testHost.RegisterClient("B", ...);
await _testHost.WaitForTicksAsync(2);
```

### 3. Use Shorter Timeouts for CI
```csharp
// Development: generous timeout
await client.WaitForPositionUpdate(TimeSpan.FromSeconds(5));

// CI: tight timeout
await client.WaitForPositionUpdate(TimeSpan.FromSeconds(1));
```

## Debugging Strategy

### Enable Verbose Logging

The harness outputs to `Console.WriteLine()`. Capture in tests:

```csharp
[Fact]
public async Task DebugTest(ITestOutputHelper output)
{
    // xUnit captures Console output automatically
    Console.WriteLine("[Debug] Starting test...");
    
    var client = _testHost.RegisterClient("Debug", FactionId.Alpha);
    
    output.WriteLine($"Client position: {client.CurrentPosition}");
}
```

### Inspect Packet History

```csharp
var history = _testHost.GetBroadcastHistory();
foreach (var (tick, packet, targetId) in history)
{
    Console.WriteLine($"Tick {tick}: {packet.GetType().Name}");
}
```

### Breakpoint in Game Loop

Since the game loop runs on a background thread:

```csharp
private void SimulateOneTick()
{
    // ✓ Can set breakpoint here
    var processTickMethod = typeof(ArenaInstance).GetMethod(...);
    
    // Game loop will stop at breakpoint
    processTickMethod?.Invoke(_arena, null);
}
```

The test thread will wait indefinitely on the semaphore, allowing you to step through server logic.

## Common Pitfalls

### Pitfall 1: Forgetting to Wait for Spawn

```csharp
// ✗ WRONG: Client might not be fully spawned yet
var client = _testHost.RegisterClient("Player", FactionId.Alpha);
client.SendMovementIntent(127, 0);  // Entity might be -1!

// ✓ CORRECT: Wait for spawn to complete
var client = _testHost.RegisterClient("Player", FactionId.Alpha);
await _testHost.WaitForTicksAsync(2);  // Auth + Spawn
client.SendMovementIntent(127, 0);
```

### Pitfall 2: Checking Position Before Update Received

```csharp
// ✗ WRONG: No guarantee position packet arrived
client.SendMovementIntent(127, 0);
var pos = client.CurrentPosition;  // Still old value!

// ✓ CORRECT: Wait for update explicitly
client.SendMovementIntent(127, 0);
var update = await client.WaitForPositionUpdate(TimeSpan.FromSeconds(1));
var pos = update;  // Guaranteed to be fresh
```

### Pitfall 3: Race Condition with Multiple Clients

```csharp
// ✗ RISKY: Second client might not be spawned
var c1 = _testHost.RegisterClient("A", ...);
var c2 = _testHost.RegisterClient("B", ...);
await _testHost.WaitForTicksAsync(1);  // Might not be enough

// ✓ SAFE: Explicit wait
var c1 = _testHost.RegisterClient("A", ...);
var c2 = _testHost.RegisterClient("B", ...);
await _testHost.WaitForTicksAsync(2);  // Both spawned guaranteed
```

### Pitfall 4: Not Disposing Test Host

```csharp
// ✗ WRONG: Game loop thread keeps running
[Fact]
public async Task BadTest()
{
    var host = new GameServerTestHost();
    await host.StartAsync();
    // Missing: await host.StopAsync(); host.Dispose();
}

// ✓ CORRECT: Use IAsyncLifetime
public sealed class ProperTests : IAsyncLifetime
{
    private GameServerTestHost? _host;
    
    public async Task InitializeAsync()
    {
        _host = new GameServerTestHost();
        await _host.StartAsync();
    }
    
    public async Task DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
```

### Pitfall 6: Reading Tick Fields from `EntityPositionPacket` Without Decoding

`EntityPositionPacket.ServerTickLo`/`ServerTickHi` and `AcknowledgedTickLo`/`AcknowledgedTickHi` are a 24-bit split encoding. There is no longer a single `int ServerTick` field.

```csharp
// ✗ WRONG: field does not exist after round 5 refactor
posPacket.ServerTick.Should().Be(expectedTick);

// ✓ CORRECT: reconstruct with the shared helper
int serverTick = PacketEncoding.DecodeTick24(posPacket.ServerTickLo, posPacket.ServerTickHi);
serverTick.Should().Be(expectedTick);

// For long-session wrapping robustness (>154 h edge case):
// int raw = PacketEncoding.DecodeTick24(lo, hi);
// int full = (lastKnownTick & ~0xFFFFFF) | raw;
// if (full < lastKnownTick - 0x800000) full += 0x1000000;
```

### Pitfall 5: Reading Raw Struct Packet Fields Without Decoding

`EntityPositionPacket.X` and `.Y` are `short` fixed-point values (multiply by `1/16` to get world units). `EntityHealthPacket.Health` is a `ushort` raw HP integer. Comparing them directly to `float` world coordinates will produce wrong results.

```csharp
// ✗ WRONG: comparing fixed-point short to float world coordinate
var posPacket = packets.OfType<EntityPositionPacket>().Last();
posPacket.X.Should().BeApproximately(10.5f, 0.1f);  // X is a short!

// ✓ CORRECT: decode first
float worldX = PacketEncoding.DecodePosition(posPacket.X);
worldX.Should().BeApproximately(10.5f, 0.1f);

// ✗ WRONG: Health is ushort, not float
var hpPacket = packets.OfType<EntityHealthPacket>().Last();
hpPacket.Health.Should().BeApproximately(100f, 1f);  // Health is ushort!

// ✓ CORRECT
float hp = PacketEncoding.DecodeHealth(hpPacket.Health);
hp.Should().BeApproximately(100f, 1f);
```

## Extending with Custom Systems

### Adding Combat Tests

1. Extend `GameServerTestAssertions` with combat helpers:
```csharp
public static void AssertDamageApplied(
    EntityHealthPacket healthPacket,
    float expectedDamage)
{
    // Validate health delta
}
```

2. Add test cases in new `CombatIntegrationTests.cs`:
```csharp
public async Task Combat_MeleeAttack_DamageDealt()
{
    var attacker = _testHost.RegisterClient("Attacker", ...);
    var target = _testHost.RegisterClient("Target", ...);
    
    attacker.SendAttackIntent(target.CurrentEntityId);
    await _testHost.WaitForTicksAsync(1);
    
    var packets = target.AllReceivedPackets.OfType<EntityHealthPacket>();
    packets.Should().NotBeEmpty();
}
```

### Adding Projectile Tests

Similar pattern: queue projectile via `SendSpellCastIntent()`, wait for tick, inspect `ProjectileSpawnPacket` and collision results.

## Best Practices Checklist

- [ ] Always use `IAsyncLifetime` for test class lifecycle
- [ ] Wait for spawn before sending movement intents
- [ ] Use `WaitForPositionUpdate()` to synchronize with broadcasts
- [ ] Validate movement within speed constraints
- [ ] Test boundary clamping with corner positions
- [ ] Use `FluentAssertions` for readable error messages
- [ ] Batch intents before ticking when possible
- [ ] Inspect `AllReceivedPackets` for debugging
- [ ] Use custom assertions from `GameServerTestAssertions`
- [ ] Document complex test scenarios with comments
- [ ] Test both success and cheat-attempt cases
- [ ] Verify state consistency across multiple clients
- [ ] Use `PacketEncoding.DecodePosition()` when asserting on `EntityPositionPacket.X/Y`
- [ ] Use `PacketEncoding.DecodeHealth()` when asserting on `EntityHealthPacket.Health`
- [ ] Use `PacketEncoding.DecodeTick24(lo, hi)` when asserting on `EntityPositionPacket` tick fields (`ServerTickLo/Hi`, `AcknowledgedTickLo/Hi`)
- [ ] Cast `CombatEventPacket.Damage` / `AoEHitEventPacket.Damage` as `ushort` (max `CombatMath.MaxSingleHitDamage` = 9,999) — clamped via `DamageUtils.ClampAndEncode`; values above cap fire `SecurityTelemetry.RecordDamageCap` (round 6)
- [ ] Use `WaitForPositionUpdate()` rather than reading `CurrentPosition` directly — delta compression means a stationary remote entity sends no position packet; `CurrentPosition` returns the last known value, not a fresh one

---

**Last Updated:** May 31, 2026

### Recent Changes (May 31, 2026 zero-allocation audit fixes — round 9)

| Area | Change |
|------|--------|
| `ArenaInstance.BroadcastStatusEffects` | Parameter type changed from `IReadOnlyList<StatusEffectAppliedPacket>` to `List<StatusEffectAppliedPacket>`. `IReadOnlyList<T>` is an interface; every `.Count` access and `[i]` indexer call issued a vtable dispatch. In heavy AoE combat (60+ status effects/tick) this was measurable. Using the concrete type gives the JIT an inlineable, devirtualized access path. **Test impact:** none — all callers already held `List<T>` references. |
| `DamageUtils.ClampAndEncode` | `string context` parameter changed to `System.ReadOnlySpan<char> context`. String literals at call sites (`"projectile"`, `"splash"`, `"melee"`, etc.) are now zero-allocation stack-resident spans — no managed string is promoted to the GC heap on the game-loop thread even when the damage-cap path is triggered. `SecurityTelemetry.RecordDamageCap` now accepts `ReadOnlySpan<char>` and calls `span.ToString()` once before handing off to `ThreadPool.QueueUserWorkItem`, so the string allocation is entirely on the background thread. **Test impact:** none — call sites pass string literals whose type is inferred transparently by the compiler. |

### Recent Changes (May 31, 2026 delta compression broadcasting — round 8)

| Area | Change |
|------|--------|
| `PlayerSession` | Added `internal short LastBroadcastX`, `LastBroadcastY` (sentinel `short.MinValue`, outside the valid ±2048 world-unit range so the first tick always sends), and `internal ushort LastBroadcastHealth` (sentinel `ushort.MaxValue`). All three are primitives — zero GC cost. |
| `ArenaInstance.BroadcastState` | Position packet now suppressed when `EncodePosition(entity.Position.X/Y)` matches `LastBroadcastX/Y`. Exception: own-entity position always sent (client needs `AcknowledgedTick` every tick for input reconciliation). Health packet suppressed when encoded HP unchanged. `EncodeTick24` called once before the viewer loop; `serverTickLo/Hi` shared across all position packets that tick. |
| `ArenaInstance.CommitBroadcastState` | New O(N) pass after all viewers are served; writes `LastBroadcastX/Y/Health`. Deferred so every viewer in one tick sees the same changed/unchanged decision. |
| **Test impact** | Tests asserting a position packet arrived for a remote entity should verify it actually moved. `WaitForPositionUpdate()` is unaffected — it waits for a fresh packet, so it only resolves when the entity moves. Tests checking `CurrentPosition` on a stationary remote entity will return the last known value (correct; no stale-data regression since movement is still authoritative). |
| ROADMAP 2.4 | Promoted from 🔶 to ✅. |

### Recent Changes (May 31, 2026 single-thread queue & spatial-grid audit — round 7)

| Area | Change |
|------|--------|
| `ArenaInstance` — `_attackQueue`, `_spellQueue`, `_shootQueue`, `_equipItemQueue`, `_pickupQueue` | `ConcurrentQueue<T>` → `Queue<T>(initialCapacity: 16)`. All LiteNetLib callbacks fire synchronously inside `PollEvents()` on the game-loop thread; `ProcessTick` runs on the same thread. `ConcurrentQueue.TryDequeue` issues `Interlocked.CompareExchange` even under zero contention — under 30+ action events/tick at 2,000-player scale this was thousands of unnecessary interlocked ops/second. `Queue<T>` is a plain ring buffer with no locking overhead. **Test impact:** none — `TryDequeue` signature is identical on both types. |
| `ArenaInstance` — `GroundItem` nested type | `sealed class` → `struct`. Previously every `SpawnGroundItem` call (`new GroundItem { … }`) allocated a short-lived heap object — a constant GC source in farming/loot-heavy MMO zones. Storing the struct by value in `Dictionary<int, GroundItem>` eliminates the wrapper object entirely; `ItemInstance` (the managed reference inside) remains on the heap as expected. **Test impact:** `TryGetValue` out-variable type changes from `GroundItem?` to `GroundItem` (value type cannot be null). |
| `ArenaInstance` — `_spatialGrid` initialisation | Removed per-tick `if (_spatialGrid == null)` guard from `ProcessTick`. Grid now initialised eagerly in `Start()` immediately after `NetworkManager` is created, before `RunGameLoop` begins. `ProcessTick` and `BroadcastState` use `_spatialGrid!` (non-null assertion). **Test impact:** none — `GameServerTestHost` calls `StartAsync()` which calls `Start()`. |
| `ArenaInstance` — `BroadcastStatusEffect` / `BroadcastStatusEffectRemoval` (AlliesOnly path) | `_spatialGrid?.QueryNeighbours(…) ?? (List<PlayerSession>)_players` → `_spatialGrid!.QueryNeighbours(…)`. The null-conditional path forced a nullable evaluation + fallback cast on every call even though the grid is guaranteed non-null post-`Start()`. Two additional occurrences of the same pattern in `BroadcastState` were already fixed in round 6; these two in the AlliesOnly status-effect helpers are now consistent. **Test impact:** none. |
| `PlayerStateSink.FlushAsync` | Comment updated: `Task.Run(static lambda, state)` is not available — `Task.Run` has no typed state overload in any .NET version. The correct zero-closure path would require `Task.Factory.StartNew<TState>` which boxes the `LivePlayerState` struct argument to `object` — a worse trade-off. Code kept as `Task.Run(() => …)`; comment now accurately documents why the closure cannot be eliminated without a worse regression. |

### Recent Changes (May 31, 2026 concurrency audit & compile-error fixes — round 6)

| Area | Change |
|------|--------|
| `NetworkManager` — `EntityPositionPacket` serialiser | **Critical bug fixed.** `SendTo(NetPeer, in EntityPositionPacket)` was writing deleted fields `ServerTick`/`AcknowledgedTick`. Updated to write `ServerTickLo`, `ServerTickHi`, `AcknowledgedTickLo`, `AcknowledgedTickHi`. All position packets were sending corrupt tick data — lag compensation and interpolation on the Unity client were broken on every tick. |
| `NetworkManager` — `_pendingAuthPeers` / `_ipGuards` | `ConcurrentDictionary` → plain `Dictionary`. `ConcurrentDictionary.foreach` allocates a boxed `IEnumerator<KVP>` every call (no public struct enumerator). `DisconnectAuthTimeoutPeers` is called every tick via `PollEvents()` — this was a per-tick GC allocation. `Dictionary.Enumerator` is a value-type struct, zero allocation. `GetOrAdd` factory delegates also eliminated. |
| `NetworkManager` — `EvictStaleIpGuards` | Added `_staleIpAddresses` pre-allocated scratch list. Two-pass eviction (collect then remove) prevents `InvalidOperationException` from modifying a `Dictionary` during enumeration. |
| `IntentGuard` — `_peerGuards` | `ConcurrentDictionary` → plain `Dictionary` for the same single-thread reasons. `GetOrAdd` delegate replaced with `TryGetValue` + add. |
| `IntentGuard` — violation `Console.WriteLine` | Moved off game-loop thread via `ThreadPool.QueueUserWorkItem(static id => Console.WriteLine(…), peer.Id)`. `static` lambda keyword prevents closure allocation. |
| `ArenaInstance` — grace-period log | `Task.Run(() => Console.WriteLine(…))` (closure + `Task` allocation) replaced with `ThreadPool.QueueUserWorkItem(static name => Console.WriteLine(…), playerName)`. |
| `CombatSystem`, `ProjectileSystem`, `PlayerSession` | `(ushort)Math.Clamp(damage, 0, 65535)` at all six sites replaced with `DamageUtils.ClampAndEncode(damage, attackerId, context)`. Cap lowered to `CombatMath.MaxSingleHitDamage = 9_999` (~10× the design ceiling of ~1,000). Values that exceed the cap fire `SecurityTelemetry.RecordDamageCap` and increment `damageCapHits` in the snapshot counter. **Test impact:** max observable `Damage` is now `9999` (was `65535`). Use `ushort` or explicit cast when comparing; `damageCapHits > 0` in a test run indicates a formula bug. |

### Recent Changes (May 31, 2026 struct compression & allocation audit — round 5)

| Area | Change |
|------|--------|
| `EntityPositionPacket` | `ServerTick`/`AcknowledgedTick` (`int`, 4 B each) replaced with 24-bit wrapping split fields: `ushort TickLo` + `byte TickHi` (3 B each). Wire size **17 → 15 bytes**. Test code must use `PacketEncoding.DecodeTick24(lo, hi)` — direct `int` field access will not compile. |
| `CombatEventPacket.Damage` | `int` → `ushort`. Tests comparing `Damage` to an `int` literal will need an explicit `(ushort)` cast or direct comparison; `FluentAssertions` `.Be(n)` still works via implicit conversion. |
| `AoEHitEventPacket.Damage` | Same `int`→`ushort` change. |
| `ArenaInstance` reusable lists | Initial capacities added to all five per-tick scratch lists (`_reusableStatusEffects` etc.) to prevent `List<T>` internal array resize under burst AoE combat. No test impact; prevents mid-test GC noise in high-CCU stress tests. |
| `ArenaInstance._groundSpawnedPacket` | New pre-allocated field; `SpawnGroundItem` now passes it via `in`-ref. No test API change. |
| `EvictExpiredGracePeriods` | Console output moved to `Task.Run`. No test impact. |

### Recent Changes (May 31, 2026 zero-allocation audit — round 4)

| Area | Change |
|------|--------|
| `ArenaInstance._deathPacket` / `_respawnPacket` / `_groundRemovedPacket` / `_itemAddedPacket` | Pre-allocated as instance fields. Previously constructed inline (`new PlayerDeathPacket { … }`) on every event, causing a struct copy on each `SendToInterested` call. Now mutated in-place and passed by `in`-ref — same pattern as `_posPacket` and `_projSpawnPacket`. |
| `ArenaInstance` Phase 5 (projectile results) | `foreach` / `foreach var (projId, ev)` loops over `TickResult.Hits`, `PierceHits`, `SplashHits`, and `ExpiredIds` replaced with index-based `for` loops. Eliminates `List<T>.Enumerator.MoveNext`/`Current` overhead on the projectile collision hot path. |
| `ArenaInstance._pendingHydration` | `ConcurrentQueue<T>` → `Queue<T>`. All accesses are on the single game-loop thread; `ConcurrentQueue` was adding `Interlocked` / `volatile` cost with no benefit. `FinalizeHydration` updated to use `Count` / `Dequeue` / `Enqueue` (same logic, no locking needed). |
| `PlayerStateSink.FlushAsync` | `Task.Run(static lambda, state)` upgrade documented but held at `Task.Run(() => …)` due to net7.0 target. The state-passing `Task.Run` overload requires .NET 8+; upgrade path noted in code comment. |

### Recent Changes (May 31, 2026 data-layer allocation audit — round 3)

| Area | Change |
|------|--------|
| `PlayerStateSink.FlushAsync` | Replaced `async Task` body with `Task.Run(() => FlushCoreAsync(...))`. The old form ran string interpolation + `JsonSerializer.Serialize` synchronously on the game-loop thread before yielding; now all work is thread-pool-offloaded. |
| `MatchDataService.LoadPlayerProfile` | `raw.ToString()` → `(byte[])raw` + `bytes.AsSpan()` — no intermediate `string` heap allocation. |
| `MatchDataService.LoadPlayerProfileAsync` | Same `byte[]` + `Span<byte>` fix; runs on thread-pool continuation (post-`await`), reducing GC pressure at high connect rates. |

### Recent Changes (May 31, 2026)

| Area | Change |
|------|--------|
| `RunGameLoop` | Drift-free absolute-deadline heartbeat replaces `Thread.Sleep(elapsed)` |
| `BroadcastState` | Pre-allocated struct instances mutated in-place; zero per-tick allocation |
| Input drain | `_latestInputByPeer` / `_latestGearSwapByPeer` switched from `ConcurrentDictionary` (heap `IEnumerator`) to plain `Dictionary` (struct enumerator) |
| `ProjectileSystem` | `??= new List<T>()` scratch lists replaced with static pre-allocated fields cleared each tick |
| `NetworkPackets` | `EntityPositionPacket`, `EntityHealthPacket`, `CombatEventPacket`, `AoEHitEventPacket` converted to `[StructLayout] struct` with compressed fields |
| `SpatialGrid` | New `GameServer/SpatialGrid.cs` — fixed-cell 2-D hash, O(k) neighbour queries, zero allocation |
| `BroadcastState` | Now calls `SpatialGrid.QueryNeighbours(viewer)` instead of iterating all N players |

### Recent Changes (May 31, 2026 audit)

| Area | Change |
|------|--------|
| `ArenaInstance` | `ticksPerTick` now `Stopwatch.Frequency / TickRate` (integer division) — eliminates float rounding drift over millions of ticks |
| `ProjectileState` | Converted from `sealed class` to `struct`; added `OwnerFaction` field snapshotted at spawn |
| `ArenaInstance` | `List<ProjectileState> _projectiles` → `ProjectileState[512] _projectiles` + `int _projectileCount`; zero per-spawn heap allocation |
| `ProjectileSystem` | `SpawnProjectile` → `TrySpawnProjectile(..., out ProjectileState)` (zero alloc); `Tick` now accepts array + `ref int count` + `SpatialGrid?`; uses ref locals + SwapRemove |
| `ProjectileSystem` | `MatchesFactionFilter` now O(1) switch expression on `ProjectileState.OwnerFaction`; was O(N) allPlayers scan per collision pair |
| `MatchDataService` | `LoadPlayerProfileAsync` now uses `await StringGetAsync` (truly async) — was `Task.Run(blocking StringGet)` which occupied a ThreadPool thread during Redis I/O |
| `NetworkPackets` | `EntityPositionPacket` wire-size comment corrected 14 B → 17 B (`1+4+2+2+4+4`) |

### Recent Changes (May 31, 2026 performance audit — round 2)

| Area | Change |
|------|--------|
| `ProjectileSystem.Tick` | Added `IReadOnlyDictionary<int, PlayerSession> entityMap` parameter; `ApplyLifeSteal` now O(1) `TryGetValue` hash probe — was O(N) linear scan (200 000 iterations/tick at 2 000 players × 100 hits) |
| `ProjectileSystem.ApplyExplosiveSplash` | Receives `entityMap` and passes it to `ApplyLifeSteal` |
| `NetworkManager` | `CombatEventPacket` and `AoEHitEventPacket` `SendToInterested` overloads now accept `SpatialGrid? grid` and delegate to `SendWrittenToInterested` — previously hardcoded O(N) viewer loop, ignoring the grid entirely |
| `ArenaInstance.BroadcastCombatEvent` | Now passes `_spatialGrid` to `SendToInterested` |
| `ArenaInstance.BroadcastAoEHitEvent` | Now passes `_spatialGrid` to `SendToInterested` |
| `ArenaInstance` Phase 5 | Projectile final-hit `ProjectileDestroyPacket` `SendToInterested` now passes `_spatialGrid` |
| `ArenaInstance` Phase 8 | `PlayerDeathPacket` `SendToInterested` now passes `_spatialGrid` |
| `ArenaInstance` Phase 9 | `PlayerRespawnPacket` `SendToInterested` now passes `_spatialGrid` |
| `ArenaInstance` Phase 9c | `GroundItemRemovedPacket` `SendToInterested` now passes `_spatialGrid` |
| `ArenaInstance.SpawnGroundItem` | `GroundItemSpawnedPacket` `SendToInterested` now passes `_spatialGrid` |