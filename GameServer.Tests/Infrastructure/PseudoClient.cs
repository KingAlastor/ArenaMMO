using LiteNetLib;
using LiteNetLib.Utils;
using SharedLibrary;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GameServer.Tests.Infrastructure
{
    /// <summary>
    /// Mock network client that simulates a connected player without a Unity client.
    /// Handles packet serialization/deserialization and maintains a simulated connection
    /// to the test server.
    /// 
    /// Thread-safe design allows test threads to send intents asynchronously while the
    /// game loop thread processes them synchronously.
    /// </summary>
    public sealed class PseudoClient
    {
        private readonly int _clientId;
        private readonly string _playerName;
        private readonly byte _faction;
        private int _clientTick = 0;
        private int _nextActionSequenceId = 1;
        private int _currentEntityId = -1;
        private Vec2 _lastKnownPosition = Vec2.Zero;

        /// <summary>
        /// Inbound packets received from the server, indexed by tick.
        /// Tests read from here to validate server broadcasts.
        /// </summary>
        private readonly ConcurrentDictionary<int, List<object>> _inboundPacketsByTick = new();

        /// <summary>
        /// Thread-safe queue for packets awaiting serialization and network transmission.
        /// The test host drains this periodically to feed the arena's input queues.
        /// </summary>
        private readonly ConcurrentQueue<object> _outboundPackets = new();

        /// <summary>
        /// Synchronization point for tests waiting on specific state updates.
        /// E.g. wait for EntityPositionPacket after sending a move intent.
        /// </summary>
        private readonly SemaphoreSlim _positionUpdateSemaphore = new(0);
        private EntityPositionPacket? _lastPositionUpdate;

        // Track client-side prediction state for reconciliation validation
        private readonly List<PlayerInputPacket> _predictedInputs = new();
        private int _lastAcknowledgedTick = 0;

        public PseudoClient(int clientId, string playerName, byte faction)
        {
            _clientId = clientId;
            _playerName = playerName;
            _faction = faction;
        }

        #region Connection Lifecycle

        /// <summary>
        /// Initializes client state after successful authentication on the server.
        /// Called by GameServerTestHost after TryAuthenticatePeer succeeds.
        /// </summary>
        public void OnAuthenticated(int entityId, Vec2 spawnPosition)
        {
            _currentEntityId = entityId;
            _lastKnownPosition = spawnPosition;
            _clientTick = 0;
        }

        #endregion

        #region Intent Queue (Client → Server)

        /// <summary>
        /// Queues a movement intent to be sent next frame.
        /// The quantized input (-127..127) represents the normalized direction vector.
        /// </summary>
        public void SendMovementIntent(sbyte inputX, sbyte inputY)
        {
            var packet = new PlayerInputPacket
            {
                TickNumber = _clientTick,
                InputX = inputX,
                InputY = inputY
            };
            _outboundPackets.Enqueue(packet);
            _predictedInputs.Add(packet);
            _clientTick++;
        }

        /// <summary>
        /// Queues an attack intent against a target entity.
        /// </summary>
        public void SendAttackIntent(int targetEntityId)
        {
            var packet = new AttackRequestPacket
            {
                TickNumber = _clientTick,
                ActionSequenceId = _nextActionSequenceId++,
                TargetEntityId = targetEntityId
            };
            _outboundPackets.Enqueue(packet);
            _clientTick++;
        }

        /// <summary>
        /// Queues a spell cast intent.
        /// For single-target spells, set targetEntityId.
        /// For ground-targeted AoE, set aoeCenterX and aoeCenterY.
        /// </summary>
        public void SendSpellCastIntent(int spellId, int targetEntityId = 0, float aoeCenterX = 0f, float aoeCenterY = 0f)
        {
            var packet = new SpellCastRequestPacket
            {
                TickNumber = _clientTick,
                ActionSequenceId = _nextActionSequenceId++,
                SpellId = spellId,
                TargetEntityId = targetEntityId,
                AoECenterX = aoeCenterX,
                AoECenterY = aoeCenterY
            };
            _outboundPackets.Enqueue(packet);
            _clientTick++;
        }

        #endregion

        #region Outbound Packet Drain (for test host)

        /// <summary>
        /// Drains all queued outbound packets for transmission to the server.
        /// Called by GameServerTestHost on each frame before ProcessTick.
        /// </summary>
        public IReadOnlyList<object> DrainOutboundPackets()
        {
            var packets = new List<object>();
            while (_outboundPackets.TryDequeue(out var packet))
                packets.Add(packet);
            return packets;
        }

        #endregion

        #region Inbound Packet Reception (Server → Client)

        /// <summary>
        /// Records an inbound packet from the server (called by the test host).
        /// Tests retrieve and validate these packets after server ticks.
        /// </summary>
        public void OnPacketReceived(int serverTick, object packet)
        {
            _inboundPacketsByTick.AddOrUpdate(
                serverTick,
                new List<object> { packet },
                (_, packets) =>
                {
                    packets.Add(packet);
                    return packets;
                }
            );

            // Update client state tracking based on packet type
            if (packet is EntityPositionPacket posPacket && posPacket.EntityId == _currentEntityId)
            {
                _lastPositionUpdate = posPacket;
                _lastKnownPosition = new Vec2(posPacket.X, posPacket.Y);
                _lastAcknowledgedTick = posPacket.AcknowledgedTick;

                // Discard predicted inputs that have been acknowledged
                while (_predictedInputs.Count > 0 && _predictedInputs[0].TickNumber <= _lastAcknowledgedTick)
                    _predictedInputs.RemoveAt(0);

                _positionUpdateSemaphore.Release();
            }
        }

        #endregion

        #region State Query & Validation

        /// <summary>
        /// Waits for the next position update from the server with a timeout.
        /// Used to synchronize test execution with server broadcasts.
        /// </summary>
        public async Task<EntityPositionPacket> WaitForPositionUpdate(TimeSpan timeout)
        {
            if (!await _positionUpdateSemaphore.WaitAsync(timeout))
                throw new TimeoutException($"Client {_playerName} did not receive position update within {timeout.TotalMilliseconds}ms");

            if (_lastPositionUpdate == null)
                throw new InvalidOperationException("Position update should have been set");

            return _lastPositionUpdate;
        }

        /// <summary>
        /// Retrieves all packets received for a specific server tick.
        /// </summary>
        public IReadOnlyList<object> GetPacketsForTick(int serverTick)
        {
            if (_inboundPacketsByTick.TryGetValue(serverTick, out var packets))
                return packets;
            return new List<object>();
        }

        /// <summary>
        /// Returns the current position as known by this client from the last EntityPositionPacket.
        /// </summary>
        public Vec2 CurrentPosition => _lastKnownPosition;

        /// <summary>
        /// Returns the client's view of the entity ID assigned to this player.
        /// </summary>
        public int CurrentEntityId => _currentEntityId;

        /// <summary>
        /// Returns all packets ever received, flattened.
        /// </summary>
        public IEnumerable<object> AllReceivedPackets
        {
            get
            {
                foreach (var tickPackets in _inboundPacketsByTick.Values)
                    foreach (var packet in tickPackets)
                        yield return packet;
            }
        }

        #endregion
    }
}
