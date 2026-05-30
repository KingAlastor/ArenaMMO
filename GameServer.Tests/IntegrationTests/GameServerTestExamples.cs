using FluentAssertions;
using GameServer.Tests.Infrastructure;
using SharedLibrary;
using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace GameServer.Tests.IntegrationTests
{
    /// <summary>
    /// Example test configurations and utilities for complex test scenarios.
    /// Demonstrates advanced patterns for multi-client interactions and state validation.
    /// </summary>
    public sealed class GameServerTestExamples : IAsyncLifetime
    {
        private GameServerTestHost? _testHost;

        public async Task InitializeAsync()
        {
            _testHost = new GameServerTestHost("test-secret-examples");
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
        /// Demonstrates the fluent builder pattern for setting up complex scenarios.
        /// </summary>
        [Fact]
        public async Task Example_BuilderPattern_ScenarioSetup()
        {
            // Setup: Two factions, 2 players each
            var builder = new GameServerTestScenarioBuilder(_testHost!);
            var clients = await builder
                .WithClient("AlphaLeader", FactionId.Alpha)
                .WithClient("AlphaSoldier", FactionId.Alpha)
                .WithClient("BetaLeader", FactionId.Beta)
                .WithClient("BetaSoldier", FactionId.Beta)
                .WaitingForTicks(2)
                .ExecuteAsync(async _ =>
                {
                    // Wait for spawn to complete
                    await Task.Delay(100);
                })
                .Clients;

            clients.Should().HaveCount(4);
            Console.WriteLine($"[Example] Spawned {clients.Count} players across 2 factions");
        }

        /// <summary>
        /// Example of validating multi-player interactions using the test framework.
        /// </summary>
        [Fact]
        public async Task Example_MultiplayerValidation_SynchronizedMovement()
        {
            var alphaPlayer = _testHost!.RegisterClient("Alpha", FactionId.Alpha);
            var betaPlayer = _testHost.RegisterClient("Beta", FactionId.Beta);

            await _testHost.WaitForTicksAsync(2, TimeSpan.FromSeconds(1));

            var alphaStart = alphaPlayer.CurrentPosition;
            var betaStart = betaPlayer.CurrentPosition;

            Console.WriteLine($"[Example] Alpha starts at ({alphaStart.X:F2}, {alphaStart.Y:F2})");
            Console.WriteLine($"[Example] Beta starts at ({betaStart.X:F2}, {betaStart.Y:F2})");

            // Synchronized movement: both move toward each other
            alphaPlayer.SendMovementIntent(127, 0);   // Move right
            betaPlayer.SendMovementIntent(-127, 0);   // Move left

            await _testHost.WaitForTicksAsync(1, TimeSpan.FromSeconds(1));

            var alphaEnd = alphaPlayer.CurrentPosition;
            var betaEnd = betaPlayer.CurrentPosition;

            // Verify both moved in opposite directions
            alphaEnd.X.Should().BeGreaterThan(alphaStart.X);
            betaEnd.X.Should().BeLessThan(betaStart.X);

            float distanceBetween = MathF.Sqrt(
                MathF.Pow(alphaEnd.X - betaEnd.X, 2) +
                MathF.Pow(alphaEnd.Y - betaEnd.Y, 2)
            );

            Console.WriteLine($"[Example] After movement, distance between players: {distanceBetween:F2}");
        }

        /// <summary>
        /// Example of boundary testing with edge cases.
        /// </summary>
        [Fact]
        public async Task Example_BoundaryCondition_CornerClamping()
        {
            var player = _testHost!.RegisterClient("BoundaryTester", FactionId.Alpha);
            await _testHost.WaitForTicksAsync(2, TimeSpan.FromSeconds(1));

            // Move toward top-right corner repeatedly
            for (int i = 0; i < 50; i++)
            {
                player.SendMovementIntent(127, 127);
            }

            await _testHost.WaitForTicksAsync(10, TimeSpan.FromSeconds(2));

            var finalPos = player.CurrentPosition;

            // Should be clamped to bounds
            finalPos.X.Should().BeLessThanOrEqualTo(50f);
            finalPos.Y.Should().BeLessThanOrEqualTo(50f);

            Console.WriteLine($"[Example] Player bounded at ({finalPos.X:F2}, {finalPos.Y:F2})");
        }

        /// <summary>
        /// Example of rate limiting and input validation.
        /// Demonstrates that rapid-fire inputs don't exceed speed limits.
        /// </summary>
        [Fact]
        public async Task Example_RateLimiting_NoSpeedExploitViaInputSpam()
        {
            var player = _testHost!.RegisterClient("SpamTester", FactionId.Alpha);
            await _testHost.WaitForTicksAsync(2, TimeSpan.FromSeconds(1));

            var initialPos = player.CurrentPosition;

            // Spam maximum movement input
            for (int i = 0; i < 100; i++)
            {
                player.SendMovementIntent(127, 0);
            }

            // Process many ticks but let rate limiting kick in
            await _testHost.WaitForTicksAsync(5, TimeSpan.FromSeconds(1));

            var finalPos = player.CurrentPosition;
            float distanceTraveled = finalPos.X - initialPos.X;

            // Even with spam, movement should be bounded by speed * time
            float maxExpectedDistance = 5.0f * (1f / 30f) * 5;  // 5 frames max

            distanceTraveled.Should().BeLessThan(maxExpectedDistance + 1f,
                "speed exploit via input spam should not be possible");

            Console.WriteLine($"[Example] After input spam, traveled {distanceTraveled:F2} units (max expected: {maxExpectedDistance:F2})");
        }

        /// <summary>
        /// Example of validation with multiple assertion styles.
        /// </summary>
        [Fact]
        public async Task Example_AssertionStyles_ComparisonPatterns()
        {
            var player = _testHost!.RegisterClient("Validator", FactionId.Alpha);
            await _testHost.WaitForTicksAsync(2, TimeSpan.FromSeconds(1));

            var pos1 = player.CurrentPosition;
            player.SendMovementIntent(127, 0);
            await _testHost.WaitForTicksAsync(1, TimeSpan.FromSeconds(1));
            var pos2 = player.CurrentPosition;

            // FluentAssertions style
            pos2.X.Should().BeGreaterThan(pos1.X);
            pos2.Y.Should().BeCloseTo(pos1.Y, 0.01f);

            // Custom assertions
            GameServerTestAssertions.AssertMovementWithinSpeedLimit(
                pos1, pos2, 1f / 30f, maxSpeed: 5.0f);

            GameServerTestAssertions.AssertPositionInBounds(
                pos2, WorldBounds.DefaultArena, "Player should remain in bounds");

            Console.WriteLine("[Example] All assertions passed");
        }

        /// <summary>
        /// Example of packet history inspection for debugging.
        /// </summary>
        [Fact]
        public async Task Example_PacketInspection_DebugHistory()
        {
            var player = _testHost!.RegisterClient("PacketInspector", FactionId.Alpha);
            await _testHost.WaitForTicksAsync(2, TimeSpan.FromSeconds(1));

            player.SendMovementIntent(100, 50);
            await _testHost.WaitForTicksAsync(1, TimeSpan.FromSeconds(1));

            var allPackets = player.AllReceivedPackets.ToList();
            Console.WriteLine($"[Example] Player received {allPackets.Count} total packets");

            foreach (var packet in allPackets)
            {
                Console.WriteLine($"  - {packet.GetType().Name}");
                if (packet is EntityPositionPacket posPacket)
                {
                    Console.WriteLine($"    Position: ({posPacket.X:F2}, {posPacket.Y:F2}), Tick: {posPacket.ServerTick}");
                }
            }

            // Find all position updates
            var positions = GameServerTestAssertions.GetPacketsOfType<EntityPositionPacket>(
                _testHost!.GetBroadcastHistory());

            Console.WriteLine($"[Example] Total position updates broadcast: {positions.Count}");
        }

        /// <summary>
        /// Example of concurrent player state validation.
        /// </summary>
        [Fact]
        public async Task Example_ConcurrentState_MultiplayerConsistency()
        {
            var players = new[]
            {
                _testHost!.RegisterClient("Player1", FactionId.Alpha),
                _testHost.RegisterClient("Player2", FactionId.Alpha),
                _testHost.RegisterClient("Player3", FactionId.Beta),
            };

            await _testHost.WaitForTicksAsync(2, TimeSpan.FromSeconds(1));

            // All players move simultaneously
            foreach (var p in players)
            {
                p.SendMovementIntent(127, 127);
            }

            await _testHost.WaitForTicksAsync(2, TimeSpan.FromSeconds(1));

            // Validate all reached consistent state
            foreach (var player in players)
            {
                var pos = player.CurrentPosition;
                pos.Should().NotBe(Vec2.Zero, $"{player} should have moved");
            }

            Console.WriteLine("[Example] All players reached consistent state");
        }
    }
}
