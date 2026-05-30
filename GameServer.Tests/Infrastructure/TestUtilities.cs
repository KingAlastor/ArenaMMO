using SharedLibrary;
using System;

namespace GameServer.Tests.Infrastructure
{
    /// <summary>
    /// Test data builders for constructing test scenarios with minimal boilerplate.
    /// Provides factory methods for common test setups.
    /// </summary>
    public static class TestDataBuilders
    {
        /// <summary>
        /// Creates a standard attack intent for testing melee combat.
        /// </summary>
        public static AttackRequestPacket BuildAttackIntent(int targetEntityId, int tick = 1, int sequenceId = 1)
        {
            return new AttackRequestPacket
            {
                TickNumber = tick,
                ActionSequenceId = sequenceId,
                TargetEntityId = targetEntityId
            };
        }

        /// <summary>
        /// Creates a standard spell cast intent for single-target spells.
        /// </summary>
        public static SpellCastRequestPacket BuildSpellCastIntent(
            int spellId,
            int targetEntityId,
            int tick = 1,
            int sequenceId = 1)
        {
            return new SpellCastRequestPacket
            {
                TickNumber = tick,
                ActionSequenceId = sequenceId,
                SpellId = spellId,
                TargetEntityId = targetEntityId,
                AoECenterX = 0f,
                AoECenterY = 0f
            };
        }

        /// <summary>
        /// Creates a spell cast intent for ground-targeted AoE spells.
        /// </summary>
        public static SpellCastRequestPacket BuildAoESpellIntent(
            int spellId,
            float centerX,
            float centerY,
            int tick = 1,
            int sequenceId = 1)
        {
            return new SpellCastRequestPacket
            {
                TickNumber = tick,
                ActionSequenceId = sequenceId,
                SpellId = spellId,
                TargetEntityId = 0,
                AoECenterX = centerX,
                AoECenterY = centerY
            };
        }

        /// <summary>
        /// Creates a movement input for testing.
        /// inputX and inputY: -127 to 127 (will be clamped by server).
        /// </summary>
        public static PlayerInputPacket BuildMovementInput(sbyte inputX, sbyte inputY, int tick = 1)
        {
            return new PlayerInputPacket
            {
                TickNumber = tick,
                InputX = (sbyte)Math.Clamp(inputX, (sbyte)-127, (sbyte)127),
                InputY = (sbyte)Math.Clamp(inputY, (sbyte)-127, (sbyte)127)
            };
        }

        /// <summary>
        /// Creates standard input for rightward movement.
        /// </summary>
        public static PlayerInputPacket BuildMoveRight(int tick = 1) => BuildMovementInput(127, 0, tick);

        /// <summary>
        /// Creates standard input for leftward movement.
        /// </summary>
        public static PlayerInputPacket BuildMoveLeft(int tick = 1) => BuildMovementInput(-127, 0, tick);

        /// <summary>
        /// Creates standard input for upward movement.
        /// </summary>
        public static PlayerInputPacket BuildMoveUp(int tick = 1) => BuildMovementInput(0, 127, tick);

        /// <summary>
        /// Creates standard input for downward movement.
        /// </summary>
        public static PlayerInputPacket BuildMoveDown(int tick = 1) => BuildMovementInput(0, -127, tick);

        /// <summary>
        /// Creates standard input for diagonal movement (up-right).
        /// </summary>
        public static PlayerInputPacket BuildMoveDiagonal(int tick = 1) => BuildMovementInput(127, 127, tick);
    }

    /// <summary>
    /// Test math helpers for validating game calculations.
    /// </summary>
    public static class TestMath
    {
        /// <summary>
        /// Calculates the expected movement distance for a given input over one frame.
        /// </summary>
        public static float ExpectedMovementDistance(sbyte inputX, sbyte inputY, float speed = 5.0f, float deltaTime = 1f / 30f)
        {
            // Dequantize
            float fx = inputX / 127f;
            float fy = inputY / 127f;

            float magSqr = fx * fx + fy * fy;
            if (magSqr <= 0f) return 0f;

            // Normalize if diagonal
            if (magSqr > 1f)
            {
                float inv = 1f / MathF.Sqrt(magSqr);
                fx *= inv;
                fy *= inv;
            }

            // Apply speed
            return MathF.Sqrt(fx * fx + fy * fy) * speed * deltaTime;
        }

        /// <summary>
        /// Calculates maximum movement distance over N frames.
        /// </summary>
        public static float MaxMovementOverFrames(sbyte inputX, sbyte inputY, int frameCount, float speed = 5.0f, float deltaTime = 1f / 30f)
        {
            float perFrame = ExpectedMovementDistance(inputX, inputY, speed, deltaTime);
            return perFrame * frameCount;
        }

        /// <summary>
        /// Verifies that a movement is within physical limits.
        /// </summary>
        public static bool IsMovementPhysical(Vec2 from, Vec2 to, float maxDistance)
        {
            float dx = to.X - from.X;
            float dy = to.Y - from.Y;
            float distance = MathF.Sqrt(dx * dx + dy * dy);
            return distance <= maxDistance;
        }

        /// <summary>
        /// Calculates distance between two positions.
        /// </summary>
        public static float Distance(Vec2 a, Vec2 b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// Checks if position is within bounds.
        /// </summary>
        public static bool IsInBounds(Vec2 position, WorldBounds bounds)
        {
            return position.X >= bounds.MinX && position.X <= bounds.MaxX &&
                   position.Y >= bounds.MinY && position.Y <= bounds.MaxY;
        }
    }

    /// <summary>
    /// Test verification helpers for common game scenarios.
    /// </summary>
    public static class GameScenarioValidators
    {
        /// <summary>
        /// Validates that two clients are at a reasonable distance from each other
        /// (i.e., not overlapping at the same position).
        /// </summary>
        public static bool ArePlayersSpacedApart(Vec2 pos1, Vec2 pos2, float minDistance = 2.0f)
        {
            float distance = TestMath.Distance(pos1, pos2);
            return distance >= minDistance;
        }

        /// <summary>
        /// Validates that a player has moved within expected bounds.
        /// </summary>
        public static bool DidPlayerMoveLegally(Vec2 from, Vec2 to, float maxFrames = 1f, float speed = 5.0f, float deltaTime = 1f / 30f)
        {
            float maxDistance = speed * deltaTime * maxFrames * 1.1f;  // 10% tolerance
            float actualDistance = TestMath.Distance(from, to);
            return actualDistance <= maxDistance;
        }

        /// <summary>
        /// Validates that a player did NOT move (within tolerance).
        /// </summary>
        public static bool DidPlayerStay(Vec2 before, Vec2 after, float tolerance = 0.01f)
        {
            float distance = TestMath.Distance(before, after);
            return distance <= tolerance;
        }

        /// <summary>
        /// Validates that a player moved exactly in one direction (within tolerance).
        /// </summary>
        public static bool DidPlayerMoveInDirection(Vec2 from, Vec2 to, float expectedX, float expectedY, float tolerance = 0.1f)
        {
            float dx = to.X - from.X;
            float dy = to.Y - from.Y;

            bool xCorrect = Math.Abs(dx) <= tolerance || (expectedX > 0 && dx > 0) || (expectedX < 0 && dx < 0) || (expectedX == 0 && Math.Abs(dx) < tolerance);
            bool yCorrect = Math.Abs(dy) <= tolerance || (expectedY > 0 && dy > 0) || (expectedY < 0 && dy < 0) || (expectedY == 0 && Math.Abs(dy) < tolerance);

            return xCorrect && yCorrect;
        }
    }

    /// <summary>
    /// Constants for common test values.
    /// </summary>
    public static class TestConstants
    {
        // Movement constants
        public const float DefaultMoveSpeed = 5.0f;
        public const float DeltaTimePerFrame = 1f / 30f;
        public const float MaxExpectedDeltaPerFrame = DefaultMoveSpeed * DeltaTimePerFrame;

        // Bounds
        public static readonly WorldBounds DefaultArenaBounds = WorldBounds.DefaultArena;

        // Timeout
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(1);
        public static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(10);

        // Tick waits
        public const int SpawnWaitTicks = 2;  // Auth + broadcast
        public const int MinTickWait = 1;
        public const int StandardTickWait = 1;
        public const int LongTickWait = 5;
    }

    /// <summary>
    /// Predefined test configurations for common test setups.
    /// </summary>
    public static class TestConfigurations
    {
        /// <summary>
        /// Single player in empty arena.
        /// </summary>
        public static class SinglePlayer
        {
            public const string ClientName = "Tester";
            public static FactionId Faction => FactionId.Alpha;
        }

        /// <summary>
        /// Two opposing faction players.
        /// </summary>
        public static class TwoPlayerPvP
        {
            public const string Client1Name = "AlphaPlayer";
            public const string Client2Name = "BetaPlayer";
            public static FactionId Faction1 => FactionId.Alpha;
            public static FactionId Faction2 => FactionId.Beta;
        }

        /// <summary>
        /// Four player free-for-all.
        /// </summary>
        public static class FourPlayerFFA
        {
            public static readonly (string Name, FactionId Faction)[] Players = new[]
            {
                ("Player1", FactionId.Alpha),
                ("Player2", FactionId.Beta),
                ("Player3", FactionId.Alpha),
                ("Player4", FactionId.Beta),
            };
        }
    }

    /// <summary>
    /// Extension methods for common test operations.
    /// </summary>
    public static class TestExtensions
    {
        /// <summary>
        /// Sends N consecutive movement inputs in the same direction.
        /// </summary>
        public static void SendRepeatedMovement(this PseudoClient client, sbyte inputX, sbyte inputY, int count)
        {
            for (int i = 0; i < count; i++)
                client.SendMovementIntent(inputX, inputY);
        }

        /// <summary>
        /// Sends a sequence of directional inputs (useful for zigzag patterns).
        /// </summary>
        public static void SendMovementSequence(this PseudoClient client, params (sbyte X, sbyte Y)[] sequence)
        {
            foreach (var (x, y) in sequence)
                client.SendMovementIntent(x, y);
        }

        /// <summary>
        /// Returns the distance this client has traveled since a previous position.
        /// </summary>
        public static float GetDistanceTraveled(this PseudoClient client, Vec2 from)
        {
            return TestMath.Distance(from, client.CurrentPosition);
        }

        /// <summary>
        /// Checks if this client is approximately at the given position.
        /// </summary>
        public static bool IsAtPosition(this PseudoClient client, Vec2 position, float tolerance = 0.1f)
        {
            return TestMath.Distance(client.CurrentPosition, position) <= tolerance;
        }

        /// <summary>
        /// Checks if this client stayed still (didn't move).
        /// </summary>
        public static bool DidStay(this PseudoClient client, Vec2 previousPosition, float tolerance = 0.01f)
        {
            return GameScenarioValidators.DidPlayerStay(previousPosition, client.CurrentPosition, tolerance);
        }
    }
}
