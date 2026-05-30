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

### 3. **Asynchronous Test Coordination**
- Test thread remains responsive during server simulation
- Semaphore-based tick synchronization prevents busy-waiting
- `await WaitForPositionUpdate()` blocks only as long as needed
- Timeouts prevent hangs from logic errors

### 4. **Zero-Copy Packet Routing**
- Packets stored as-is in pseudo-client collections
- No serialization/deserialization except in real networking
- Tests inspect packets directly without marshalling overhead

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
    allPackets.OfType<EntityPositionPacket>().Should().NotBeEmpty();
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
BroadcastState()  ──► ConcurrentDict (packets)
                      ├─ Indexed by tick
                      ├─ Keyed by entity ID
                      └─ Test reads via AllReceivedPackets
```

**Guarantee:** Packets are immutable after insertion; test thread can read safely.

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

---

**Last Updated:** May 29, 2026
