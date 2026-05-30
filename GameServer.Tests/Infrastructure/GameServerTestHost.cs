using GameServer.DataLayer;
using LiteNetLib;
using LiteNetLib.Utils;
using SharedLibrary;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GameServer.Tests.Infrastructure
{
    /// <summary>
    /// In-memory test harness for integration testing the GameServer without UDP networking.
    /// 
    /// Spins up an ArenaInstance, bypasses network I/O by directly feeding input queues,
    /// captures outbound packets, and provides synchronization points for assertions.
    ///
    /// The test host runs the 30 Hz game loop on a background thread, allowing the
    /// test thread to remain responsive for sending intents and checking state.
    /// </summary>
    public sealed class GameServerTestHost : IDisposable
    {
        private readonly ArenaInstance _arena;
        private readonly ZoneDescriptor _zoneDescriptor;
        private readonly string _ticketSecret;
        private readonly List<PseudoClient> _clients = new();
        private readonly Dictionary<int, NetPeer> _fakeNetPeers = new();
        private int _nextPeerId = 1;
        private int _fakePort = 19050;

        private Thread? _gameLoopThread;
        private volatile bool _isRunning = false;
        private int _currentServerTick = 0;

        /// <summary>
        /// Captured packets broadcast from the server this tick.
        /// Tests inspect this to validate game state updates.
        /// </summary>
        private readonly List<(int ServerTick, object Packet, int TargetEntityId)> _broadcastHistory = new();

        /// <summary>
        /// Semaphore released after each game loop tick completes.
        /// Tests use this to synchronize with server simulation.
        /// </summary>
        private readonly SemaphoreSlim _tickCompletedSemaphore = new(0);

        public GameServerTestHost(string ticketSecret = "test-secret-key")
        {
            _ticketSecret = ticketSecret;
            _zoneDescriptor = new ZoneDescriptor();
            _arena = new ArenaInstance(_ticketSecret, _zoneDescriptor);
        }

        /// <summary>
        /// Initializes the test host and starts the game loop on a background thread.
        /// Must be called before registering clients or sending intents.
        /// </summary>
        public async Task StartAsync()
        {
            if (_isRunning)
                throw new InvalidOperationException("Test host is already running");

            _isRunning = true;
            _gameLoopThread = new Thread(RunGameLoopWorker)
            {
                Name = "GameServerTestHost-GameLoop",
                IsBackground = true
            };
            _gameLoopThread.Start();

            // Give the game loop thread time to initialize
            await Task.Delay(100);
        }

        /// <summary>
        /// Gracefully shuts down the game loop and waits for the background thread to exit.
        /// </summary>
        public async Task StopAsync()
        {
            if (!_isRunning)
                return;

            _isRunning = false;
            if (_gameLoopThread != null)
            {
                await Task.Run(() => _gameLoopThread.Join(TimeSpan.FromSeconds(5)));
            }
        }

        #region Client Registration & Authentication

        /// <summary>
        /// Creates and registers a pseudo-client with the test host.
        /// The client is authenticated immediately and assigned an entity ID.
        /// </summary>
        public PseudoClient RegisterClient(string playerName, FactionId faction = FactionId.Alpha)
        {
            int clientId = _clients.Count;
            var client = new PseudoClient(clientId, playerName, (byte)faction);

            // Create a fake NetPeer (not actually connected via UDP)
            var fakePeer = new FakeNetPeer(_nextPeerId++);
            _fakeNetPeers[clientId] = fakePeer;

            // Construct auth ticket
            var ticket = new AuthTicketPacket
            {
                PlayerId = clientId,
                PlayerName = playerName,
                Faction = (byte)faction,
                AllowedSpellIdsCsv = "1,2,3,4,5",  // Placeholder
                IssuedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
                Nonce = $"test-nonce-{clientId}",
                Signature = "" // Will be computed
            };

            // Compute HMAC signature
            ticket.Signature = ComputeTicketSignature(ticket);

            // Authenticate with the arena
            if (!_arena.TryAuthenticatePeer(fakePeer, ticket, null))
                throw new InvalidOperationException($"Failed to authenticate client {playerName}");

            _clients.Add(client);
            Console.WriteLine($"[TestHost] Client registered: {playerName} (clientId={clientId})");

            // Note: We'll set the entity ID after the first tick when spawning is complete
            return client;
        }

        #endregion

        #region Input Queue Feeding

        /// <summary>
        /// Processes all outbound intents from a client and injects them into the arena's input queues.
        /// Called by the game loop thread before ProcessTick each frame.
        /// </summary>
        private void DrainClientIntentsToArena()
        {
            for (int i = 0; i < _clients.Count; i++)
            {
                PseudoClient client = _clients[i];
                var packets = client.DrainOutboundPackets();

                foreach (var packet in packets)
                {
                    var peer = _fakeNetPeers[i];

                    // Route packet to appropriate arena queue
                    switch (packet)
                    {
                        case PlayerInputPacket inputPacket:
                            _arena.EnqueueInput(peer, inputPacket);
                            break;
                        case AttackRequestPacket attackPacket:
                            _arena.EnqueueAttack(peer, attackPacket);
                            break;
                        case SpellCastRequestPacket spellPacket:
                            _arena.EnqueueSpellCast(peer, spellPacket);
                            break;
                        case ShootRequestPacket shootPacket:
                            _arena.EnqueueShoot(peer, shootPacket);
                            break;
                    }
                }
            }
        }

        #endregion

        #region Packet Capture

        /// <summary>
        /// Manually injects a simulated server broadcast to capture it for testing.
        /// In a real test, the arena's BroadcastState would call this via a hook.
        /// For now, tests construct packets and feed them here after each tick.
        /// </summary>
        public void CapturePacket(int serverTick, object packet, int? targetEntityId = null)
        {
            _broadcastHistory.Add((serverTick, packet, targetEntityId ?? -1));

            // Distribute to interested clients
            if (packet is EntityPositionPacket posPacket)
            {
                // Broadcast to all clients except the target
                foreach (var client in _clients)
                {
                    if (client.CurrentEntityId != posPacket.EntityId)
                        client.OnPacketReceived(serverTick, packet);
                }
            }
            else if (packet is EntitySpawnPacket spawnPacket)
            {
                foreach (var client in _clients)
                {
                    if (client.CurrentEntityId != spawnPacket.EntityId)
                        client.OnPacketReceived(serverTick, packet);
                }
            }
            else
            {
                // Broadcast to all
                foreach (var client in _clients)
                    client.OnPacketReceived(serverTick, packet);
            }
        }

        #endregion

        #region Game Loop Worker

        private void RunGameLoopWorker()
        {
            const int tickRateHz = 30;
            const int msPerTick = 1000 / tickRateHz;
            var sw = new Stopwatch();

            try
            {
                while (_isRunning)
                {
                    sw.Restart();

                    // ── Phase 1: Drain client intents ──────────────────────
                    DrainClientIntentsToArena();

                    // ── Phase 2: Simulate one tick (this calls arena internal methods)
                    // Note: ArenaInstance.ProcessTick() is internal, so we invoke via reflection
                    // In a production scenario, you'd expose a public method on ArenaInstance
                    SimulateOneTick();

                    _currentServerTick++;
                    _tickCompletedSemaphore.Release();

                    // ── Phase 3: Frame rate regulation ─────────────────────
                    int sleep = msPerTick - (int)sw.ElapsedMilliseconds;
                    if (sleep > 0)
                        Thread.Sleep(sleep);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TestHost] Game loop error: {ex}");
                _isRunning = false;
            }
        }

    /// <summary>
    /// Advances the arena by one tick using reflection to invoke internal methods.
    /// IMPORTANT: For this to work, ensure ArenaInstance has public EnqueueInput/Attack/Spell/Shoot methods.
    /// Consider adding a public TickForTesting() method to ArenaInstance for cleaner test integration.
    /// </summary>
    private void SimulateOneTick()
    {
        // Use reflection to call internal ProcessTick and BroadcastState
        System.Reflection.BindingFlags flags = 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        var processTickMethod = typeof(ArenaInstance).GetMethod("ProcessTick", flags);
        var broadcastStateMethod = typeof(ArenaInstance).GetMethod("BroadcastState", flags);
        var pollEventsMethod = typeof(ArenaInstance).GetMethod("PollEvents", flags);

        // Poll network events first (though our test harness doesn't use real networking)
        // This line may fail if the method doesn't exist; we'll skip it gracefully
        try { pollEventsMethod?.Invoke(_arena, null); } catch { }

        // Process game logic
        processTickMethod?.Invoke(_arena, null);
        
        // Broadcast state updates
        broadcastStateMethod?.Invoke(_arena, null);
    }        /// <summary>
        /// Waits for the specified number of game ticks to complete.
        /// Allows test code to synchronize with server simulation.
        /// </summary>
        public async Task WaitForTicksAsync(int tickCount, TimeSpan timeout = default)
        {
            if (timeout == default)
                timeout = TimeSpan.FromSeconds(5);

            var deadline = DateTime.UtcNow.Add(timeout);
            for (int i = 0; i < tickCount; i++)
            {
                if (!await _tickCompletedSemaphore.WaitAsync(
                    Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds)))
                    throw new TimeoutException($"Timeout waiting for {tickCount} ticks");
            }
        }

        #endregion

        #region State Query

        /// <summary>
        /// Returns all captured packets in broadcast history.
        /// </summary>
        public IReadOnlyList<(int ServerTick, object Packet, int TargetEntityId)> GetBroadcastHistory()
            => _broadcastHistory;

        /// <summary>
        /// Returns the current server tick count.
        /// </summary>
        public int CurrentServerTick => _currentServerTick;

        /// <summary>
        /// Returns the registered PseudoClients.
        /// </summary>
        public IReadOnlyList<PseudoClient> Clients => _clients;

        #endregion

        #region Ticket Signing

        private string ComputeTicketSignature(AuthTicketPacket ticket)
        {
            // Placeholder: In production, use HMAC-SHA256
            // For testing, we'll compute a simple signature
            using (var hmac = new System.Security.Cryptography.HMACSHA256(
                System.Text.Encoding.UTF8.GetBytes(_ticketSecret)))
            {
                var payload = System.Text.Encoding.UTF8.GetBytes(
                    $"{ticket.PlayerId}|{ticket.PlayerName}|{ticket.Faction}|" +
                    $"{ticket.IssuedAtUnixMs}|{ticket.ExpiresAtUnixMs}|{ticket.Nonce}"
                );
                var hash = hmac.ComputeHash(payload);
                return System.Convert.ToBase64String(hash);
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _isRunning = false;
            _gameLoopThread?.Join(TimeSpan.FromSeconds(2));
            _tickCompletedSemaphore?.Dispose();
        }

        #endregion
    }

    /// <summary>
    /// Minimal fake NetPeer for testing without UDP connectivity.
    /// </summary>
    internal sealed class FakeNetPeer : NetPeer
    {
        public FakeNetPeer(int id) : base(null, 0, 0, null)
        {
            // Base constructor is protected; we initialize minimal fields
        }

        public override void Send(NetDataWriter data, DeliveryMethod deliveryMethod)
        {
            // No-op: testing framework captures packets via other means
        }

        public override void Disconnect(byte[] data = null)
        {
            // No-op
        }
    }
}
