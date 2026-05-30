# Integration Test Harness - Quick Start

Get up and running with the GameServer integration test suite in 5 minutes.

## Prerequisites

- .NET 7.0 SDK
- xUnit test runner (installed via NuGet)
- ArenaMMO project cloned

## Installation

The test project is already created. Just restore dependencies:

```bash
cd /home/taavi/Coding/ArenaMMO/GameServer.Tests
dotnet restore
```

## Your First Test (2 minutes)

Create a new file `IntegrationTests/MyFirstTest.cs`:

```csharp
using FluentAssertions;
using GameServer.Tests.Infrastructure;
using SharedLibrary;
using System;
using System.Threading.Tasks;
using Xunit;

namespace GameServer.Tests.IntegrationTests
{
    public sealed class MyFirstTest : IAsyncLifetime
    {
        private GameServerTestHost? _testHost;

        public async Task InitializeAsync()
        {
            _testHost = new GameServerTestHost("test-secret");
            await _testHost.StartAsync();
        }

        public async Task DisposeAsync()
        {
            if (_testHost != null)
            {
                await _testHost.StopAsync();
                _testHost.Dispose();
            }
        }

        [Fact]
        public async Task PlayerCanMove()
        {
            // 1. Create a client
            var player = _testHost!.RegisterClient("Hero", FactionId.Alpha);

            // 2. Wait for spawn (2 ticks: auth + broadcast)
            await _testHost.WaitForTicksAsync(2);

            // 3. Get initial position
            var initial = player.CurrentPosition;

            // 4. Send movement intent (right)
            player.SendMovementIntent(127, 0);

            // 5. Wait for one tick
            await _testHost.WaitForTicksAsync(1);

            // 6. Get updated position
            var updated = player.CurrentPosition;

            // 7. Assert
            updated.X.Should().BeGreaterThan(initial.X, "player should move right");
        }
    }
}
```

## Run Your Test

```bash
# Run all tests
dotnet test GameServer.Tests

# Run your new test only
dotnet test GameServer.Tests --filter "MyFirstTest"

# Verbose output
dotnet test GameServer.Tests --verbosity=detailed
```

Output:
```
Test Failures (0)
Test Passed (1)
Test Skipped (0)
```

## Core Concepts (3 minutes)

### 1. Register a Client
```csharp
var player = _testHost.RegisterClient("PlayerName", FactionId.Alpha);
```
- Creates a pseudo-client (no UDP)
- Authenticates with server
- Returns immediately

### 2. Wait for Spawn
```csharp
await _testHost.WaitForTicksAsync(2);
```
- Blocks until 2 game ticks complete
- Tick 1: Authentication
- Tick 2: Broadcast spawn packet
- After this, `player.CurrentPosition` is valid

### 3. Send Intent
```csharp
player.SendMovementIntent(127, 0);   // Full right, no vertical
player.SendMovementIntent(-127, 127); // Full left + up (diagonal)
player.SendAttackIntent(targetEntityId);
player.SendSpellCastIntent(spellId, targetEntityId);
```
- Queues intent (immediate, non-blocking)
- Intent processed on next tick

### 4. Wait for Tick
```csharp
await _testHost.WaitForTicksAsync(1);
```
- Blocks until 1 tick processes
- Meanwhile, game loop thread simulates
- Returns when tick complete

### 5. Assert State
```csharp
var pos = player.CurrentPosition;
var packets = player.AllReceivedPackets;
var update = await player.WaitForPositionUpdate(TimeSpan.FromSeconds(1));

pos.X.Should().BeGreaterThan(0);
```

## Common Patterns

### Pattern A: Simple Movement
```csharp
var player = _testHost.RegisterClient("Player", FactionId.Alpha);
await _testHost.WaitForTicksAsync(2);

player.SendMovementIntent(127, 0);
await _testHost.WaitForTicksAsync(1);

player.CurrentPosition.X.Should().BeGreaterThan(0);
```

### Pattern B: Multiple Clients
```csharp
var p1 = _testHost.RegisterClient("A", FactionId.Alpha);
var p2 = _testHost.RegisterClient("B", FactionId.Beta);
await _testHost.WaitForTicksAsync(2);

p1.SendMovementIntent(127, 0);
p2.SendMovementIntent(-127, 0);
await _testHost.WaitForTicksAsync(1);

p1.CurrentPosition.X.Should().BeGreaterThan(0);
p2.CurrentPosition.X.Should().BeLessThan(0);
```

### Pattern C: Fluent Builder
```csharp
var clients = await new GameServerTestScenarioBuilder(_testHost)
    .WithClient("A", FactionId.Alpha)
    .WithClient("B", FactionId.Beta)
    .WaitingForTicks(2)
    .Clients;
```

## Debugging

### View Console Output
```csharp
Console.WriteLine($"[Test] Player at {player.CurrentPosition.X}");
// xUnit captures and displays on test failure
```

### View All Packets
```csharp
foreach (var packet in player.AllReceivedPackets)
{
    Console.WriteLine($"Received: {packet.GetType().Name}");
}
```

### Inspect Broadcast History
```csharp
var history = _testHost.GetBroadcastHistory();
foreach (var (tick, packet, targetId) in history)
{
    Console.WriteLine($"Tick {tick}: {packet.GetType().Name}");
}
```

## What's Included

### Files
| File | Purpose |
|------|---------|
| `GameServer.Tests.csproj` | Project file with dependencies |
| `Infrastructure/PseudoClient.cs` | Mock player simulation |
| `Infrastructure/GameServerTestHost.cs` | Server harness |
| `Infrastructure/TestAssertions.cs` | Validation helpers |
| `IntegrationTests/MovementIntegrationTests.cs` | Example tests |
| `IntegrationTests/GameServerTestExamples.cs` | Advanced examples |
| `README.md` | Full documentation |
| `ARCHITECTURE.md` | Deep dive on design |
| `QUICKSTART.md` | This file |

### Key Classes
- **PseudoClient**: Mock player
- **GameServerTestHost**: Server manager
- **GameServerTestAssertions**: Assertion helpers
- **GameServerTestScenarioBuilder**: Fluent builder
- **FakeNetPeer**: Mock network peer

## Next Steps

1. **Run Existing Tests**
   ```bash
   dotnet test GameServer.Tests --filter "Movement"
   ```

2. **Read Example Tests**
   - `MovementIntegrationTests.cs` - Basic movement validation
   - `GameServerTestExamples.cs` - Advanced patterns

3. **Write Your First Test**
   - Copy `MyFirstTest` above
   - Modify to test your game mechanics

4. **Extend Framework**
   - Add combat tests
   - Add projectile tests
   - Add spell casting tests

## Troubleshooting

### Test Hangs
- Usually: Forgetting to wait for ticks
- Fix: Add `await _testHost.WaitForTicksAsync(2)` after RegisterClient

### Position Not Updating
- Usually: Checking position before tick completes
- Fix: Use `await client.WaitForPositionUpdate()`

### "AuthTicketPacket signature invalid"
- Usually: Ticket secret mismatch
- Fix: Ensure `new GameServerTestHost("correct-secret")`

### NullReferenceException on `CurrentPosition`
- Usually: Client not fully spawned
- Fix: Add `await _testHost.WaitForTicksAsync(2)` before using position

## Performance

| Operation | Time |
|-----------|------|
| Test startup | ~500ms |
| One tick | ~1-2ms |
| Test teardown | ~100ms |
| Full suite (5 tests) | ~10s |

## Tips for Performance

```csharp
// ✓ Fast: Batch intents, one wait
client1.SendMovementIntent(100, 0);
client2.SendMovementIntent(-100, 0);
client3.SendAttackIntent(targetId);
await _testHost.WaitForTicksAsync(1);  // One wait

// ✗ Slow: Multiple waits
client1.SendMovementIntent(100, 0);
await _testHost.WaitForTicksAsync(1);
client2.SendMovementIntent(-100, 0);
await _testHost.WaitForTicksAsync(1);
```

## Further Reading

- **ARCHITECTURE.md** - Design decisions and threading model
- **README.md** - Complete API reference
- **MovementIntegrationTests.cs** - Real test examples
- **GameServerTestExamples.cs** - Advanced patterns

---

**Ready to test?** Start with:
```bash
dotnet test GameServer.Tests --filter "Movement_ValidMoveIntent"
```

**Questions?** Check the full docs:
```bash
cat README.md
cat ARCHITECTURE.md
```
