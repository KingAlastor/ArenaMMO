using LiteNetLib;
using LiteNetLib.Utils;
using SharedLibrary;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace GameServer
{
    /// <summary>
    /// Wraps LiteNetLib to accept UDP connections, route inbound packets to the arena,
    /// and broadcast authoritative state updates to all connected peers.
    /// </summary>
    public sealed class NetworkManager : INetEventListener, IDisposable
    {
        private readonly NetManager        _net;
        private readonly NetPacketProcessor _processor;
        private readonly ArenaInstance     _arena;
        // Connected peers that have not yet proven identity with AuthTicketPacket.
        private readonly ConcurrentDictionary<NetPeer, long> _pendingAuthPeers = new();
        // Lightweight per-IP abuse guard used during pre-auth connection pressure.
        private readonly ConcurrentDictionary<IPAddress, IpGuardState> _ipGuards = new();
        // Pre-allocated to avoid per-tick heap allocation inside DisconnectAuthTimeoutPeers
        // during connection floods. Cleared and reused every call.
        private readonly List<NetPeer> _timedOutPeers = new List<NetPeer>();
        // Tracks ticks between periodic _ipGuards stale-entry evictions.
        private int _ipGuardEvictionTick;
        // Reused across every send call to eliminate per-call heap allocation.
        // All sends occur on the single game-loop thread, so no lock is required.
        private readonly NetDataWriter _sharedWriter = new NetDataWriter(true, 128);

        // Must match the key sent by the Unity client in NetManager.Connect(...)
        private const string ConnectionKey = "ArenaMMO_v1";
        // Max wait time after UDP connect before auth ticket must be received.
        private const int AuthTimeoutMs = 5000;
        // Sliding-window request cap to resist trivial connect flood attempts.
        private const int MaxConnectionRequestsPerWindow = 20;
        private const int ConnectionWindowMs = 10000;
        private const int ViolationDecayPerWindow = 1;
        // Temporary IP ban duration applied after repeated violations.
        private const int BanDurationMs = 120000;
        private const int MaxIpViolationScore = 12;

        /// <summary>
        /// Mutable state for pre-auth IP abuse mitigation.
        /// </summary>
        private sealed class IpGuardState
        {
            public readonly object Gate = new object();
            public int RequestCount;
            public long WindowStartMs;
            public long BannedUntilMs;
            public int ViolationScore;

            public IpGuardState(long nowMs)
            {
                WindowStartMs = nowMs;
            }
        }

        /// <summary>
        /// Initializes LiteNetLib listeners and packet dispatch subscriptions.
        /// All packet handlers route into ArenaInstance, which remains the authority owner.
        /// </summary>
        public NetworkManager(ArenaInstance arena, int port)
        {
            _arena     = arena;
            _processor = new NetPacketProcessor();

            // Register every Client → Server packet type with its handler
            _processor.SubscribeReusable<PlayerInputPacket,         NetPeer>(OnPlayerInput);
            _processor.SubscribeReusable<AttackRequestPacket,       NetPeer>(OnAttackRequest);
            _processor.SubscribeReusable<SpellCastRequestPacket,    NetPeer>(OnSpellCastRequest);
            _processor.SubscribeReusable<ShootRequestPacket,        NetPeer>(OnShootRequest);
            _processor.SubscribeReusable<AuthTicketPacket,          NetPeer>(OnAuthTicket);
            _processor.SubscribeReusable<GearSetSwapRequestPacket,  NetPeer>(OnGearSetSwapRequest);
            _processor.SubscribeReusable<EquipItemRequestPacket,          NetPeer>(OnEquipItemRequest);
            _processor.SubscribeReusable<GroundItemPickupRequestPacket,     NetPeer>(OnGroundItemPickupRequest);

            _net = new NetManager(this) { AutoRecycle = true };
            _net.Start(port);

            Console.WriteLine($"[Network] Server listening on UDP :{port}");
        }

        /// <summary>
        /// Must be called once per game tick to dispatch all queued LiteNetLib events.
        /// All INetEventListener callbacks fire synchronously on the calling thread.
        /// </summary>
        public void PollEvents()
        {
            _net.PollEvents();
            // Keep this in PollEvents so timeout handling runs in the same thread model
            // as other network callbacks.
            DisconnectAuthTimeoutPeers();
        }

        /// <summary>Serialises and broadcasts a packet to every connected peer.</summary>
        public void SendToAll<T>(T packet, DeliveryMethod method) where T : class, new()
        {
            _sharedWriter.Reset();
            _processor.Write(_sharedWriter, packet);
            _net.SendToAll(_sharedWriter, method);
        }

        /// <summary>Serialises and sends a packet to one specific peer.</summary>
        public void SendTo<T>(NetPeer peer, T packet, DeliveryMethod method) where T : class, new()
        {
            _sharedWriter.Reset();
            _processor.Write(_sharedWriter, packet);
            peer.Send(_sharedWriter, method);
        }

        /// <summary>
        /// Serialises <paramref name="packet"/> once and sends it only to viewers whose
        /// <see cref="IInterestFilter.ShouldReceive"/> test passes.
        ///
        /// WHY THIS METHOD EXISTS:
        ///   In Arena mode every combat event is relevant to all 10–20 players, so using
        ///   SendToAll is fine.  In an open-world MMO zone with 200 players the same approach
        ///   produces O(N²) traffic — a single combat event would be sent to players on the
        ///   other side of the continent.  By routing through IInterestFilter the caller can
        ///   swap in a RadiusFilter (or a spatial-hash filter) without touching ArenaInstance.
        ///
        ///   The packet is serialised into _sharedWriter exactly once before the loop starts;
        ///   the raw bytes are then forwarded to each qualifying peer, avoiding per-peer
        ///   re-serialisation overhead at high player counts.
        ///
        ///   Peers with a null connection (grace-period disconnected players) are skipped so
        ///   no writes are attempted on dead sockets — the guard is here because ArenaInstance
        ///   iterates _players which may include ghost sessions awaiting rejoin.
        /// </summary>
        public void SendToInterested<T>(
            T packet,
            DeliveryMethod method,
            SharedLibrary.Vec2 eventOrigin,
            IInterestFilter filter,
            IReadOnlyList<PlayerSession> viewers)
            where T : class, new()
        {
            _sharedWriter.Reset();
            _processor.Write(_sharedWriter, packet);

            for (int i = 0; i < viewers.Count; i++)
            {
                PlayerSession viewer = viewers[i];
                // Skip ghost sessions whose peer was cleared on disconnect.
                if (viewer.Peer == null) continue;
                if (!filter.ShouldReceive(viewer, eventOrigin)) continue;
                viewer.Peer.Send(_sharedWriter, method);
            }
        }

        // ── INetEventListener ─────────────────────────────────────────────────

        public void OnConnectionRequest(ConnectionRequest request)
        {
            // Step 1: pre-auth IP abuse filtering.
            IPEndPoint? remote = request.RemoteEndPoint;
            if (remote != null && IsIpRejected(remote.Address, out bool isRateLimited))
            {
                if (isRateLimited)
                    SecurityTelemetry.RecordIpRateLimit(remote.Address);

                request.Reject();
                return;
            }

            // Step 2: protocol/version key check.
            request.AcceptIfKey(ConnectionKey);
        }

        public void OnPeerConnected(NetPeer peer)
        {
            Console.WriteLine($"[Network] Peer connected: {peer.Address}");
            // Connected does not mean trusted; peer remains pending until AuthTicketPacket validates.
            _pendingAuthPeers[peer] = Environment.TickCount64;
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            Console.WriteLine($"[Network] Peer disconnected: {peer.Address} ({info.Reason})");
            _pendingAuthPeers.TryRemove(peer, out _);
            _arena.OnPlayerDisconnected(peer);
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
            => _processor.ReadAllPackets(reader, peer);

        public void OnNetworkError(IPEndPoint endPoint, SocketError error)
            => Console.WriteLine($"[Network] Error from {endPoint}: {error}");

        // Required by the interface but unused at this stage
        public void OnNetworkReceiveUnconnected(IPEndPoint remote, NetPacketReader reader, UnconnectedMessageType type) { }
        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }

        // ── Inbound Packet Dispatch ───────────────────────────────────────────

        private void OnPlayerInput(PlayerInputPacket packet, NetPeer peer)
            => _arena.EnqueueInput(peer, packet);

        private void OnAttackRequest(AttackRequestPacket packet, NetPeer peer)
            => _arena.EnqueueAttack(peer, packet);

        private void OnSpellCastRequest(SpellCastRequestPacket packet, NetPeer peer)
            => _arena.EnqueueSpellCast(peer, packet);

        private void OnShootRequest(ShootRequestPacket packet, NetPeer peer)
            => _arena.EnqueueShoot(peer, packet);

        private void OnGearSetSwapRequest(GearSetSwapRequestPacket packet, NetPeer peer)
            => _arena.EnqueueGearSetSwap(peer, packet);

        private void OnEquipItemRequest(EquipItemRequestPacket packet, NetPeer peer)
            => _arena.EnqueueEquipItem(peer, packet);

        private void OnGroundItemPickupRequest(GroundItemPickupRequestPacket packet, NetPeer peer)
            => _arena.EnqueueItemPickup(peer, packet);

        private void OnAuthTicket(AuthTicketPacket packet, NetPeer peer)
        {
            // Ignore duplicate late auth packets for already-authenticated peers.
            if (!_pendingAuthPeers.ContainsKey(peer))
                return;

            if (_arena.TryAuthenticatePeer(peer, packet, peer.Address))
            {
                _pendingAuthPeers.TryRemove(peer, out _);
                return;
            }

            // Invalid ticket is treated as a high-severity pre-auth violation.
            RegisterIpViolation(peer.Address, 4);
            peer.Disconnect();
        }

        // Evict _ipGuards entries every ~10 seconds (300 ticks at 30 Hz) to prevent
        // unbounded memory growth under IPv6-spoofed unique-source-address floods.
        private const int IpGuardEvictionIntervalTicks = 300;

        /// <summary>
        /// Disconnects peers that connected but never authenticated in time.
        /// Uses a pre-allocated list to avoid per-tick heap allocations under connection floods.
        /// </summary>
        private void DisconnectAuthTimeoutPeers()
        {
            EvictStaleIpGuards();

            if (_pendingAuthPeers.Count == 0)
                return;

            long nowMs = Environment.TickCount64;
            _timedOutPeers.Clear();
            foreach (KeyValuePair<NetPeer, long> entry in _pendingAuthPeers)
            {
                if (nowMs - entry.Value >= AuthTimeoutMs)
                    _timedOutPeers.Add(entry.Key);
            }

            for (int i = 0; i < _timedOutPeers.Count; i++)
            {
                NetPeer peer = _timedOutPeers[i];
                _pendingAuthPeers.TryRemove(peer, out _);
                RegisterIpViolation(peer.Address, 2);
                SecurityTelemetry.RecordInvalidTicket("auth-timeout", peer.Address);
                peer.Disconnect();
            }
        }

        /// <summary>
        /// Removes _ipGuards entries for IPs that are neither currently banned nor recently active.
        /// Prevents unbounded map growth under IPv4/IPv6 source-address exhaustion floods.
        /// Runs at a low interval (every ~10 s) so the iteration cost is negligible.
        /// </summary>
        private void EvictStaleIpGuards()
        {
            if (++_ipGuardEvictionTick < IpGuardEvictionIntervalTicks)
                return;

            _ipGuardEvictionTick = 0;
            long nowMs = Environment.TickCount64;

            foreach (KeyValuePair<IPAddress, IpGuardState> entry in _ipGuards)
            {
                IpGuardState state = entry.Value;
                bool evict;
                lock (state.Gate)
                {
                    // Keep the entry if a ban is still active or if the IP has recent violations.
                    evict = state.BannedUntilMs <= nowMs && state.ViolationScore == 0;
                }
                if (evict)
                    _ipGuards.TryRemove(entry.Key, out _);
            }
        }

        /// <summary>
        /// Returns true when an IP should be rejected for rate-limit or active temporary ban.
        /// </summary>
        private bool IsIpRejected(IPAddress ipAddress, out bool isRateLimited)
        {
            isRateLimited = false;
            long nowMs = Environment.TickCount64;
            IpGuardState state = _ipGuards.GetOrAdd(ipAddress, _ => new IpGuardState(nowMs));

            lock (state.Gate)
            {
                if (state.BannedUntilMs > nowMs)
                    return true;

                if (nowMs - state.WindowStartMs >= ConnectionWindowMs)
                {
                    int windowsElapsed = (int)((nowMs - state.WindowStartMs) / ConnectionWindowMs);
                    if (windowsElapsed > 0 && state.ViolationScore > 0)
                    {
                        int decay = windowsElapsed * ViolationDecayPerWindow;
                        state.ViolationScore = Math.Max(0, state.ViolationScore - decay);
                    }

                    state.WindowStartMs = nowMs;
                    state.RequestCount = 0;
                }

                state.RequestCount++;
                if (state.RequestCount <= MaxConnectionRequestsPerWindow)
                    return false;

                state.ViolationScore += 2;
                isRateLimited = true;
                if (state.ViolationScore >= MaxIpViolationScore)
                    state.BannedUntilMs = nowMs + BanDurationMs;

                return true;
            }
        }

        /// <summary>
        /// Raises violation score for an IP and applies temporary ban when threshold is exceeded.
        /// </summary>
        private void RegisterIpViolation(IPAddress? address, int severity)
        {
            if (address == null)
                return;

            long nowMs = Environment.TickCount64;
            IpGuardState state = _ipGuards.GetOrAdd(address, _ => new IpGuardState(nowMs));
            lock (state.Gate)
            {
                state.ViolationScore += severity;
                if (state.ViolationScore >= MaxIpViolationScore)
                    state.BannedUntilMs = nowMs + BanDurationMs;
            }
        }

        public void Dispose() => _net.Stop();
    }
}
