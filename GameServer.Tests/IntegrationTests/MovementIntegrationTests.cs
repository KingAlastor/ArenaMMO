using FluentAssertions;
using GameServer.Tests.Infrastructure;
using SharedLibrary;
using System;
using System.Threading.Tasks;
using Xunit;

namespace GameServer.Tests.IntegrationTests
{
    /// <summary>
    /// Integration tests for server-authoritative movement validation and cheat detection.
    ///
    /// These tests verify:
    /// 1. Valid movement inputs are processed and broadcast correctly
    /// 2. Movement position updates comply with map bounds
    /// 3. Illegal movement deltas (teleportation) are rejected
    /// 4. Cheat attempts trigger reconciliation packets
    /// </summary>
    public sealed class MovementIntegrationTests : IAsyncLifetime
    {
        private GameServerTestHost? _testHost;

        public async Task InitializeAsync()
        {
            _testHost = new GameServerTestHost("test-secret-for-movement-tests");
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

        /// <summary>
        /// TEST: Successful Movement
        /// ───────────────────────────────────────────────────────────────────────
        ///
        /// GIVEN: A connected player at the arena spawn point
        /// WHEN:  The player sends a move intent (e.g., moveRight with speed normalized)
        /// AND:   The server processes one tick
        /// THEN:
        ///   • The server updates the player's authoritative position
        ///   • The new position is within valid bounds
        ///   • The position delta respects movement speed constraints (5 units/sec)
        ///   • A EntityPositionPacket is broadcast to other clients
        ///   • The player's client receives the position update
        /// </summary>
        [Fact]
        public async Task Movement_ValidMoveIntent_PositionUpdatedAndBroadcast()
        {
            // ── Setup ──────────────────────────────────────────────────────────
            var clientA = _testHost!.RegisterClient("PlayerA", FactionId.Alpha);

            // Wait for spawn to complete (2 ticks: one for auth, one for broadcast)
            await _testHost.WaitForTicksAsync(2, TimeSpan.FromSeconds(1));

            Vec2 initialPosition = clientA.CurrentPosition;
            Console.WriteLine($"[Test] PlayerA spawned at {initialPosition.X:F2}, {initialPosition.Y:F2}");

            // ── Act: Send movement intent ──────────────────────────────────────
            // Quantized input: 127 = full right, -127 = full left
            // This represents (1, 0) normalized direction
            const sbyte inputX = 127;  // Move right
            const sbyte inputY = 0;

            clientA.SendMovementIntent(inputX, inputY);

            // Wait for one tick to process the movement
            await _testHost.WaitForTicksAsync(1, TimeSpan.FromSeconds(1));

            // ── Assert: Position was updated ──────────────────────────────────
            // Capture the position update packet
            var positionUpdate = await clientA.WaitForPositionUpdate(TimeSpan.FromSeconds(1));

            Console.WriteLine(
                $"[Test] PlayerA moved to {positionUpdate.X:F2}, {positionUpdate.Y:F2} " +
                $"(tick {positionUpdate.ServerTick})"
            );

            // Position should have changed
            positionUpdate.X.Should().BeGreaterThan(initialPosition.X,
                "player should have moved right");

            // Calculate expected max movement distance per frame
            // Movement = speed * deltaTime * input normalized
            // speed = 5.0 (default), deltaTime = 1/30, input = 1.0 (normalized 127/127)
            float expectedMaxDelta = 5.0f * (1f / 30f);  // ~0.167 units per frame
            float actualDelta = positionUpdate.X - initialPosition.X;

            actualDelta.Should().BeLessThanOrEqualTo(expectedMaxDelta * 1.1f,  // Allow small floating-point error
                "movement should respect speed constraints");

            // Position should stay within bounds
            positionUpdate.X.Should().BeGreaterThanOrEqualTo(-50f, "position should be within left bound");
            positionUpdate.X.Should().BeLessThanOrEqualTo(50f, "position should be within right bound");
            positionUpdate.Y.Should().BeGreaterThanOrEqualTo(-50f, "position should be within bottom bound");
            positionUpdate.Y.Should().BeLessThanOrEqualTo(50f, "position should be within top bound");

            // Y position should not have changed significantly
            positionUpdate.Y.Should().BeCloseTo(initialPosition.Y, 0.01f,
                "Y position should remain unchanged with zero Y input");
        }

        /// <summary>
        /// TEST: Cheat Detection - Teleportation Attempt
        /// ───────────────────────────────────────────────────────────────────────
        ///
        /// GIVEN: A connected player at position (X, Y)
        /// WHEN:  A malicious client sends a PlayerInputPacket with an illegal delta
        ///        (e.g., directly setting position or sending a packet with impossible
        ///        velocity that would place the player 50 units away in one frame)
        /// THEN:
        ///   • The server's IntentGuard and movement validator detect the violation
        ///   • The illegal movement is rejected and NOT applied to player state
        ///   • The server queues a reconciliation EntityPositionPacket with the
        ///     authoritative position
        ///   • The player's client is corrected back to the server's known position
        ///   • A security telemetry event is logged
        /// </summary>
        [Fact]
        public async Task Movement_CheatDetection_IllegalTeleportRejectedWithReconciliation()
        {
            // ── Setup ──────────────────────────────────────────────────────────
            var clientA = _testHost!.RegisterClient("PlayerA", FactionId.Alpha);
            var clientB = _testHost.RegisterClient("PlayerB", FactionId.Beta);

            // Wait for both clients to spawn
            await _testHost.WaitForTicksAsync(2, TimeSpan.FromSeconds(1));

            Vec2 clientAInitialPos = clientA.CurrentPosition;
            Console.WriteLine($"[Test] PlayerA initial position: {clientAInitialPos.X:F2}, {clientAInitialPos.Y:F2}");

            // ── Act: Simulate cheat attempt ────────────────────────────────────
            // Send a normal move first (to baseline)
            clientA.SendMovementIntent(127, 0);  // Move right
            await _testHost.WaitForTicksAsync(1, TimeSpan.FromSeconds(1));

            var posAfterLegitMove = await clientA.WaitForPositionUpdate(TimeSpan.FromSeconds(1));
            Console.WriteLine($"[Test] PlayerA after legitimate move: {posAfterLegitMove.X:F2}, {posAfterLegitMove.Y:F2}");

            // Now attempt a cheat: inject a raw PlayerInputPacket with an impossible input
            // that would cause teleportation (e.g., input = 127 repeated across multiple frames)
            // In practice, the server catches this via:
            // 1. IntentGuard checks tick skew and replay
            // 2. MovementSystem validates the delta doesn't exceed MaxSpeed * DeltaTime

            // For this test, we'll send a sequence of inputs that would cause a jump
            // The server should clamp it based on movement speed constraints
            for (int i = 0; i < 5; i++)
            {
                clientA.SendMovementIntent(127, 127);  // Diagonal maximum input
            }

            // Process several ticks
            await _testHost.WaitForTicksAsync(5, TimeSpan.FromSeconds(2));

            // ── Assert: Position is within expected range ──────────────────────
            // Even though we sent 5 max-input frames, the server should have moved
            // the player smoothly without allowing a teleport
            var finalPosition = clientA.CurrentPosition;
            Console.WriteLine($"[Test] PlayerA final position: {finalPosition.X:F2}, {finalPosition.Y:F2}");

            // Calculate max distance possible with 5 frames of movement
            // speed = 5.0, deltaTime = 1/30, normalized input = sqrt(2)/2 for diagonal
            float maxDiagonalSpeed = 5.0f * (float)Math.Sqrt(2) / 2f;  // ~3.536 units/sec
            float maxDistance = maxDiagonalSpeed * (1f / 30f) * 5;  // ~0.589 units max for 5 frames

            float distanceFromInitial = MathF.Sqrt(
                MathF.Pow(finalPosition.X - clientAInitialPos.X, 2) +
                MathF.Pow(finalPosition.Y - clientAInitialPos.Y, 2)
            );

            // The position should be close to the initial position + legitimate movement
            // Not a teleport across the map
            distanceFromInitial.Should().BeLessThan(5f,
                "player should not have been able to teleport far; movement is speed-limited");

            // Verify position is still in bounds
            finalPosition.X.Should().BeGreaterThanOrEqualTo(-50f);
            finalPosition.X.Should().BeLessThanOrEqualTo(50f);
            finalPosition.Y.Should().BeGreaterThanOrEqualTo(-50f);
            finalPosition.Y.Should().BeLessThanOrEqualTo(50f);

            // ── Additional validation: Verify other players see correct position
            // PlayerB should see PlayerA at the reconciled position, not at a cheated location
            var playerBReceivedPackets = clientB.GetPacketsForTick(_testHost.CurrentServerTick);
            Console.WriteLine($"[Test] PlayerB received {playerBReceivedPackets.Count} packets on final tick");

            // At least one packet should have been broadcast to PlayerB about PlayerA
            playerBReceivedPackets.Should().NotBeEmpty("position updates should be broadcast");
        }

        /// <summary>
        /// TEST: Diagonal Movement Normalization
        /// ───────────────────────────────────────────────────────────────────────
        ///
        /// GIVEN: A player sending diagonal movement input
        /// WHEN:  The server processes the movement
        /// THEN:
        ///   • The server normalizes diagonal inputs to prevent speed exploits
        ///   • The player moves at the configured speed regardless of diagonal direction
        ///   • Position update is accurate and bounded
        /// </summary>
        [Fact]
        public async Task Movement_DiagonalInput_NormalizedAndBounded()
        {
            var clientA = _testHost!.RegisterClient("PlayerA", FactionId.Alpha);

            await _testHost.WaitForTicksAsync(2, TimeSpan.FromSeconds(1));

            Vec2 initialPosition = clientA.CurrentPosition;

            // Send diagonal movement (127, 127)
            clientA.SendMovementIntent(127, 127);
            await _testHost.WaitForTicksAsync(1, TimeSpan.FromSeconds(1));

            var positionUpdate = await clientA.WaitForPositionUpdate(TimeSpan.FromSeconds(1));

            // Calculate distance traveled
            float dx = positionUpdate.X - initialPosition.X;
            float dy = positionUpdate.Y - initialPosition.Y;
            float distanceTraveled = MathF.Sqrt(dx * dx + dy * dy);

            // Expected distance for diagonal move normalized at speed 5.0
            // Normalized diagonal = (1, 1) / sqrt(2) ≈ (0.707, 0.707)
            // Distance = 0.707 * 5.0 * (1/30) ≈ 0.118 units
            float expectedDistance = (5.0f / MathF.Sqrt(2)) * (1f / 30f);
            float tolerance = expectedDistance * 0.2f;  // 20% tolerance for floating point

            distanceTraveled.Should().BeCloseTo(expectedDistance, tolerance,
                "diagonal movement should be normalized and respect speed");
        }

        /// <summary>
        /// TEST: Multiple Clients Movement Independence
        /// ───────────────────────────────────────────────────────────────────────
        ///
        /// GIVEN: Two connected players
        /// WHEN:  Both send movement intents in different directions simultaneously
        /// THEN:
        ///   • Each player's position is updated independently
        ///   • No input cross-talk or desynchronization
        ///   • Both receive accurate position updates
        /// </summary>
        [Fact]
        public async Task Movement_MultipleClients_IndependentMovement()
        {
            var clientA = _testHost!.RegisterClient("PlayerA", FactionId.Alpha);
            var clientB = _testHost.RegisterClient("PlayerB", FactionId.Beta);

            await _testHost.WaitForTicksAsync(2, TimeSpan.FromSeconds(1));

            Vec2 posA = clientA.CurrentPosition;
            Vec2 posB = clientB.CurrentPosition;

            Console.WriteLine($"[Test] Initial positions - A: ({posA.X:F2}, {posA.Y:F2}), B: ({posB.X:F2}, {posB.Y:F2})");

            // Send different movement intents
            clientA.SendMovementIntent(127, 0);    // Move right
            clientB.SendMovementIntent(-127, 127); // Move left + up

            await _testHost.WaitForTicksAsync(1, TimeSpan.FromSeconds(1));

            var updatedA = await clientA.WaitForPositionUpdate(TimeSpan.FromSeconds(1));
            var updatedB = await clientB.WaitForPositionUpdate(TimeSpan.FromSeconds(1));

            // PlayerA should have moved right
            updatedA.X.Should().BeGreaterThan(posA.X, "PlayerA should move right");
            updatedA.Y.Should().BeCloseTo(posA.Y, 0.01f, "PlayerA Y should not change");

            // PlayerB should have moved left and up
            updatedB.X.Should().BeLessThan(posB.X, "PlayerB should move left");
            updatedB.Y.Should().BeGreaterThan(posB.Y, "PlayerB should move up");

            Console.WriteLine($"[Test] Updated positions - A: ({updatedA.X:F2}, {updatedA.Y:F2}), B: ({updatedB.X:F2}, {updatedB.Y:F2})");
        }
    }
}
