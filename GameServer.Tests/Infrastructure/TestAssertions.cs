using SharedLibrary;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GameServer.Tests.Infrastructure
{
    /// <summary>
    /// Test assertions and utilities for validating game server behavior.
    /// Provides high-level validation helpers to reduce boilerplate in test cases.
    /// </summary>
    public static class GameServerTestAssertions
    {
        /// <summary>
        /// Verifies that a movement did not exceed the speed limit.
        /// </summary>
        public static void AssertMovementWithinSpeedLimit(
            Vec2 previousPosition,
            Vec2 currentPosition,
            float deltaTime,
            float maxSpeed = 5.0f,
            float tolerance = 1.1f)  // 10% floating-point tolerance
        {
            float dx = currentPosition.X - previousPosition.X;
            float dy = currentPosition.Y - previousPosition.Y;
            float distanceTraveled = MathF.Sqrt(dx * dx + dy * dy);
            float maxDistance = maxSpeed * deltaTime * tolerance;

            if (distanceTraveled > maxDistance)
            {
                throw new AssertionFailedException(
                    $"Movement exceeded speed limit. Distance: {distanceTraveled:F4}, " +
                    $"Max allowed: {maxDistance:F4} (speed={maxSpeed}, deltaTime={deltaTime})");
            }
        }

        /// <summary>
        /// Verifies that a position is within the specified bounds.
        /// </summary>
        public static void AssertPositionInBounds(
            Vec2 position,
            WorldBounds bounds,
            string context = "")
        {
            if (position.X < bounds.MinX || position.X > bounds.MaxX ||
                position.Y < bounds.MinY || position.Y > bounds.MaxY)
            {
                throw new AssertionFailedException(
                    $"Position ({position.X:F2}, {position.Y:F2}) is outside bounds " +
                    $"[{bounds.MinX}..{bounds.MaxX}, {bounds.MinY}..{bounds.MaxY}]. {context}");
            }
        }

        /// <summary>
        /// Verifies that exactly one packet of a specific type was broadcast.
        /// </summary>
        public static T AssertSinglePacketOfType<T>(
            IEnumerable<(int ServerTick, object Packet, int TargetEntityId)> history,
            string context = "") where T : class
        {
            var matching = history.Where(h => h.Packet is T).ToList();
            if (matching.Count != 1)
            {
                throw new AssertionFailedException(
                    $"Expected exactly 1 packet of type {typeof(T).Name}, found {matching.Count}. {context}");
            }
            return matching[0].Packet as T ?? throw new InvalidOperationException();
        }

        /// <summary>
        /// Retrieves all packets of a specific type from broadcast history.
        /// </summary>
        public static List<T> GetPacketsOfType<T>(
            IEnumerable<(int ServerTick, object Packet, int TargetEntityId)> history) where T : class
        {
            return history
                .Where(h => h.Packet is T)
                .Select(h => h.Packet as T)
                .OfType<T>()
                .ToList();
        }

        /// <summary>
        /// Verifies that a packet sequence was broadcast in strict order.
        /// </summary>
        public static void AssertPacketSequence(
            IEnumerable<(int ServerTick, object Packet, int TargetEntityId)> history,
            params Type[] expectedPacketTypes)
        {
            var relevantPackets = history
                .Select(h => h.Packet.GetType())
                .Where(t => expectedPacketTypes.Contains(t))
                .ToList();

            if (relevantPackets.Count != expectedPacketTypes.Length)
            {
                throw new AssertionFailedException(
                    $"Expected sequence of {expectedPacketTypes.Length} packets, found {relevantPackets.Count}");
            }

            for (int i = 0; i < expectedPacketTypes.Length; i++)
            {
                if (relevantPackets[i] != expectedPacketTypes[i])
                {
                    throw new AssertionFailedException(
                        $"Packet sequence mismatch at position {i}: expected {expectedPacketTypes[i].Name}, " +
                        $"got {relevantPackets[i].Name}");
                }
            }
        }
    }

    /// <summary>
    /// Custom exception for test assertion failures.
    /// </summary>
    public sealed class AssertionFailedException : Exception
    {
        public AssertionFailedException(string message) : base(message) { }
    }

    /// <summary>
    /// Builder pattern for constructing test scenarios with fluent API.
    /// Reduces boilerplate in complex multi-step test cases.
    /// </summary>
    public sealed class GameServerTestScenarioBuilder
    {
        private readonly GameServerTestHost _host;
        private readonly List<PseudoClient> _clients = new();
        private int _ticksToWait = 1;

        public GameServerTestScenarioBuilder(GameServerTestHost host)
        {
            _host = host;
        }

        /// <summary>Registers a new client with the specified faction.</summary>
        public GameServerTestScenarioBuilder WithClient(string name, FactionId faction = FactionId.Alpha)
        {
            _clients.Add(_host.RegisterClient(name, faction));
            return this;
        }

        /// <summary>Sets the default number of ticks to wait between actions.</summary>
        public GameServerTestScenarioBuilder WaitingForTicks(int count)
        {
            _ticksToWait = count;
            return this;
        }

        /// <summary>Returns the registered clients for further setup or assertions.</summary>
        public IReadOnlyList<PseudoClient> Clients => _clients;

        /// <summary>
        /// Executes a synchronous action (e.g., sending movement intents) and waits for ticks.
        /// </summary>
        public async Task<GameServerTestScenarioBuilder> ExecuteAsync(
            Func<IReadOnlyList<PseudoClient>, Task> action)
        {
            await action(_clients);
            await _host.WaitForTicksAsync(_ticksToWait, TimeSpan.FromSeconds(5));
            return this;
        }

        /// <summary>
        /// Executes an action synchronously without async.
        /// </summary>
        public async Task<GameServerTestScenarioBuilder> Execute(
            Action<IReadOnlyList<PseudoClient>> action)
        {
            await Task.Run(() => action(_clients));
            await _host.WaitForTicksAsync(_ticksToWait, TimeSpan.FromSeconds(5));
            return this;
        }
    }
}
