using LiteNetLib;
using LiteNetLib.Utils;
using SharedLibrary;
using System;
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
        // Plain Dictionary is correct here: ALL LiteNetLib callbacks (OnConnectionRequest,
        // OnPeerConnected, OnPeerDisconnected, OnNetworkReceive) fire synchronously inside
        // PollEvents(), which is called from the single game-loop thread.  There is no
        // concurrent access — ConcurrentDictionary would only add interlocked overhead and
        // a heap-allocated boxed IEnumerator<KVP> every time foreach iterates it.
        private readonly Dictionary<NetPeer, long> _pendingAuthPeers = new();
        // Same single-thread reasoning as _pendingAuthPeers — all IP guard access flows
        // through OnConnectionRequest which also fires inside PollEvents().
        private readonly Dictionary<IPAddress, IpGuardState> _ipGuards = new();
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
        // ── IpGuardState ───────────────────────────────────────────────────────
        // All access to _ipGuards and its IpGuardState entries flows through
        // OnConnectionRequest, which fires synchronously inside PollEvents() on the
        // game-loop thread.  The 'Gate' lock is retained for defense-in-depth in case
        // a future design moves connection handling to a dedicated I/O thread.
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

        // ── Zero-allocation hot-path struct sends ─────────────────────────────
        // These overloads write the blittable hot-path structs (EntityPositionPacket,
        // EntityHealthPacket, CombatEventPacket, AoEHitEventPacket) directly into the
        // shared writer field-by-field, bypassing the reflection-based NetPacketProcessor.
        // Using struct + direct write eliminates both the heap allocation (no 'new') and
        // the reflection overhead of the processor on the most frequently sent packet types.

        /// <summary>Writes an EntityPositionPacket into the shared buffer and sends to peer.</summary>
        /// <remarks>
        /// Tick fields use 24-bit split encoding: ushort (low 16 bits) + byte (high 8 bits).
        /// Decode on the client with: int tick = tickLo | (tickHi &lt;&lt; 16).
        /// Wire layout matches EntityPositionPacket field order (Sequential, Pack=1): 15 bytes total.
        /// </remarks>
        public void SendTo(NetPeer peer, in SharedLibrary.EntityPositionPacket packet, DeliveryMethod method)
        {
            _sharedWriter.Reset();
            _sharedWriter.Put(packet.PacketTypeId);
            _sharedWriter.Put(packet.EntityId);
            _sharedWriter.Put(packet.X);
            _sharedWriter.Put(packet.Y);
            // 24-bit server tick: low ushort then high byte (matches struct field order).
            _sharedWriter.Put(packet.ServerTickLo);
            _sharedWriter.Put(packet.ServerTickHi);
            // 24-bit acknowledged client tick: same split encoding.
            _sharedWriter.Put(packet.AcknowledgedTickLo);
            _sharedWriter.Put(packet.AcknowledgedTickHi);
            peer.Send(_sharedWriter, method);
        }

        /// <summary>Writes an EntityHealthPacket into the shared buffer and sends to peer.</summary>
        public void SendTo(NetPeer peer, in SharedLibrary.EntityHealthPacket packet, DeliveryMethod method)
        {
            _sharedWriter.Reset();
            _sharedWriter.Put(packet.PacketTypeId);
            _sharedWriter.Put(packet.EntityId);
            _sharedWriter.Put(packet.Health);
            peer.Send(_sharedWriter, method);
        }

        /// <summary>Writes a CombatEventPacket into the shared buffer and broadcasts to all interested peers.</summary>
        public void SendToInterested(
            in SharedLibrary.CombatEventPacket packet,
            DeliveryMethod method,
            SharedLibrary.Vec2 eventOrigin,
            IInterestFilter filter,
            IReadOnlyList<PlayerSession> viewers,
            SpatialGrid? grid = null)
        {
            _sharedWriter.Reset();
            _sharedWriter.Put(packet.PacketTypeId);
            _sharedWriter.Put(packet.AttackerId);
            _sharedWriter.Put(packet.TargetId);
            _sharedWriter.Put(packet.Damage);
            _sharedWriter.Put(packet.Flags);
            SendWrittenToInterested(method, eventOrigin, filter, viewers, grid);
        }

        /// <summary>Writes an AoEHitEventPacket into the shared buffer and broadcasts to all interested peers.</summary>
        public void SendToInterested(
            in SharedLibrary.AoEHitEventPacket packet,
            DeliveryMethod method,
            SharedLibrary.Vec2 eventOrigin,
            IInterestFilter filter,
            IReadOnlyList<PlayerSession> viewers,
            SpatialGrid? grid = null)
        {
            _sharedWriter.Reset();
            _sharedWriter.Put(packet.PacketTypeId);
            _sharedWriter.Put(packet.CasterId);
            _sharedWriter.Put(packet.SpellId);
            _sharedWriter.Put(packet.HitEntityId);
            _sharedWriter.Put(packet.Damage);
            _sharedWriter.Put(packet.Flags);
            SendWrittenToInterested(method, eventOrigin, filter, viewers, grid);
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
        ///
        ///   When <paramref name="grid"/> is provided the viewer list is narrowed to the
        ///   3×3 spatial grid neighbourhood of <paramref name="eventOrigin"/> first, reducing
        ///   the iteration from O(N) to O(k) — essential at 2 000-player MMO scale.
        /// </summary>
        public void SendToInterested<T>(
            T packet,
            DeliveryMethod method,
            SharedLibrary.Vec2 eventOrigin,
            IInterestFilter filter,
            IReadOnlyList<PlayerSession> viewers,
            SpatialGrid? grid = null)
            where T : class, new()
        {
            _sharedWriter.Reset();
            _processor.Write(_sharedWriter, packet);

            // If a spatial grid is available, restrict to the neighbourhood of the event origin.
            // Otherwise fall back to iterating all viewers (Arena mode, no grid).
            System.Collections.Generic.List<PlayerSession>? nearby =
                grid?.QueryNeighbours(eventOrigin);

            int count = nearby?.Count ?? viewers.Count;
            for (int i = 0; i < count; i++)
            {
                PlayerSession viewer = nearby != null ? nearby[i] : viewers[i];
                if (viewer.Peer == null) continue;
                if (!filter.ShouldReceive(viewer, eventOrigin)) continue;
                viewer.Peer.Send(_sharedWriter, method);
            }
        }

        // ── Zero-allocation event struct overloads ───────────────────────────
        // One overload per converted event struct.  Each writes the struct
        // fields directly to _sharedWriter — no reflection, no heap allocation.

        private void WriteStruct(in SharedLibrary.EntityDespawnPacket p)
        { _sharedWriter.Reset(); _sharedWriter.Put(p.PacketTypeId); _sharedWriter.Put(p.EntityId); }

        private void WriteStruct(in SharedLibrary.PlayerDeathPacket p)
        { _sharedWriter.Reset(); _sharedWriter.Put(p.PacketTypeId); _sharedWriter.Put(p.KilledEntityId); _sharedWriter.Put(p.KillerEntityId); }

        private void WriteStruct(in SharedLibrary.PlayerRespawnPacket p)
        { _sharedWriter.Reset(); _sharedWriter.Put(p.PacketTypeId); _sharedWriter.Put(p.EntityId); _sharedWriter.Put(p.X); _sharedWriter.Put(p.Y); _sharedWriter.Put(p.Health); }

        private void WriteStruct(in SharedLibrary.MatchEndPacket p)
        { _sharedWriter.Reset(); _sharedWriter.Put(p.PacketTypeId); _sharedWriter.Put(p.WinnerFaction); }

        private void WriteStruct(in SharedLibrary.GroundItemSpawnedPacket p)
        { _sharedWriter.Reset(); _sharedWriter.Put(p.PacketTypeId); _sharedWriter.Put(p.GroundItemId); _sharedWriter.Put(p.DefinitionId); _sharedWriter.Put(p.X); _sharedWriter.Put(p.Y); }

        private void WriteStruct(in SharedLibrary.GroundItemRemovedPacket p)
        { _sharedWriter.Reset(); _sharedWriter.Put(p.PacketTypeId); _sharedWriter.Put(p.GroundItemId); }

        private void WriteStruct(in SharedLibrary.ItemAddedToInventoryPacket p)
        { _sharedWriter.Reset(); _sharedWriter.Put(p.PacketTypeId); _sharedWriter.Put(p.DefinitionId); _sharedWriter.Put(p.InstanceId); }

        private void WriteStruct(in SharedLibrary.PlayerGraceDisconnectPacket p)
        { _sharedWriter.Reset(); _sharedWriter.Put(p.PacketTypeId); _sharedWriter.Put(p.EntityId); }

        private void WriteStruct(in SharedLibrary.PlayerReconnectedPacket p)
        { _sharedWriter.Reset(); _sharedWriter.Put(p.PacketTypeId); _sharedWriter.Put(p.EntityId); }

        private void WriteStruct(in SharedLibrary.PlayerStatsRefreshedPacket p)
        {
            _sharedWriter.Reset();
            _sharedWriter.Put(p.PacketTypeId); _sharedWriter.Put(p.ActiveGearSetIndex);
            _sharedWriter.Put(p.MaxHealth);    _sharedWriter.Put(p.AttackPower);
            _sharedWriter.Put(p.PhysicalAbsorbPercent); _sharedWriter.Put(p.PhysicalResistPercent);
            _sharedWriter.Put(p.MagicAbsorbPercent);    _sharedWriter.Put(p.MagicResistPercent);
            _sharedWriter.Put(p.CritChance);   _sharedWriter.Put(p.MeleeLifeStealPercent);
        }

        // Reusable interest-filter loop — called after WriteStruct sets up _sharedWriter.
        private void SendWrittenToInterested(
            DeliveryMethod method,
            SharedLibrary.Vec2 eventOrigin,
            IInterestFilter filter,
            IReadOnlyList<PlayerSession> viewers,
            SpatialGrid? grid = null)
        {
            System.Collections.Generic.List<PlayerSession>? nearby = grid?.QueryNeighbours(eventOrigin);
            int count = nearby?.Count ?? viewers.Count;
            for (int i = 0; i < count; i++)
            {
                PlayerSession viewer = nearby != null ? nearby[i] : viewers[i];
                if (viewer.Peer == null) continue;
                if (!filter.ShouldReceive(viewer, eventOrigin)) continue;
                viewer.Peer.Send(_sharedWriter, method);
            }
        }

        public void SendToAll(in SharedLibrary.EntityDespawnPacket p, DeliveryMethod m)
        { WriteStruct(in p); _net.SendToAll(_sharedWriter, m); }

        public void SendToAll(in SharedLibrary.MatchEndPacket p, DeliveryMethod m)
        { WriteStruct(in p); _net.SendToAll(_sharedWriter, m); }

        public void SendToAll(in SharedLibrary.PlayerGraceDisconnectPacket p, DeliveryMethod m)
        { WriteStruct(in p); _net.SendToAll(_sharedWriter, m); }

        public void SendToAll(in SharedLibrary.PlayerReconnectedPacket p, DeliveryMethod m)
        { WriteStruct(in p); _net.SendToAll(_sharedWriter, m); }

        public void SendTo(NetPeer peer, in SharedLibrary.PlayerStatsRefreshedPacket p, DeliveryMethod m)
        { WriteStruct(in p); peer.Send(_sharedWriter, m); }

        public void SendTo(NetPeer peer, in SharedLibrary.ItemAddedToInventoryPacket p, DeliveryMethod m)
        { WriteStruct(in p); peer.Send(_sharedWriter, m); }

        public void SendToInterested(in SharedLibrary.PlayerDeathPacket p, DeliveryMethod m,
            SharedLibrary.Vec2 origin, IInterestFilter f, IReadOnlyList<PlayerSession> viewers, SpatialGrid? grid = null)
        { WriteStruct(in p); SendWrittenToInterested(m, origin, f, viewers, grid); }

        public void SendToInterested(in SharedLibrary.PlayerRespawnPacket p, DeliveryMethod m,
            SharedLibrary.Vec2 origin, IInterestFilter f, IReadOnlyList<PlayerSession> viewers, SpatialGrid? grid = null)
        { WriteStruct(in p); SendWrittenToInterested(m, origin, f, viewers, grid); }

        public void SendToInterested(in SharedLibrary.GroundItemSpawnedPacket p, DeliveryMethod m,
            SharedLibrary.Vec2 origin, IInterestFilter f, IReadOnlyList<PlayerSession> viewers, SpatialGrid? grid = null)
        { WriteStruct(in p); SendWrittenToInterested(m, origin, f, viewers, grid); }

        public void SendToInterested(in SharedLibrary.GroundItemRemovedPacket p, DeliveryMethod m,
            SharedLibrary.Vec2 origin, IInterestFilter f, IReadOnlyList<PlayerSession> viewers, SpatialGrid? grid = null)
        { WriteStruct(in p); SendWrittenToInterested(m, origin, f, viewers, grid); }

        // \u2500\u2500 Zero-allocation projectile lifecycle struct overloads \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500

        private void WriteStruct(in SharedLibrary.ProjectileSpawnPacket p)
        {
            _sharedWriter.Reset();
            _sharedWriter.Put(p.PacketTypeId);
            _sharedWriter.Put(p.ProjectileId);
            _sharedWriter.Put(p.OwnerId);
            _sharedWriter.Put(p.SpellId);
            _sharedWriter.Put(p.StartX);
            _sharedWriter.Put(p.StartY);
            _sharedWriter.Put(p.DirectionX);
            _sharedWriter.Put(p.DirectionY);
            _sharedWriter.Put(p.Speed);
            _sharedWriter.Put(p.MaxRange);
        }

        private void WriteStruct(in SharedLibrary.ProjectileDestroyPacket p)
        {
            _sharedWriter.Reset();
            _sharedWriter.Put(p.PacketTypeId);
            _sharedWriter.Put(p.ProjectileId);
            _sharedWriter.Put(p.Flags);
        }

        /// <summary>Writes a StatusEffectAppliedPacket struct to the shared buffer.</summary>
        private void WriteStruct(in SharedLibrary.StatusEffectAppliedPacket p)
        {
            _sharedWriter.Reset();
            _sharedWriter.Put(p.PacketTypeId);
            _sharedWriter.Put(p.TargetEntityId);
            _sharedWriter.Put(p.SourceEntityId);
            _sharedWriter.Put(p.EffectId);
            _sharedWriter.Put(p.RemainingTicks);
            _sharedWriter.Put(p.VisibilityFlags);
        }

        /// <summary>Writes a StatusEffectRemovedPacket struct to the shared buffer.</summary>
        private void WriteStruct(in SharedLibrary.StatusEffectRemovedPacket p)
        {
            _sharedWriter.Reset();
            _sharedWriter.Put(p.PacketTypeId);
            _sharedWriter.Put(p.TargetEntityId);
            _sharedWriter.Put(p.EffectId);
            _sharedWriter.Put(p.VisibilityFlags);
        }

        public void SendToInterested(in SharedLibrary.ProjectileSpawnPacket p, DeliveryMethod m,
            SharedLibrary.Vec2 origin, IInterestFilter f, IReadOnlyList<PlayerSession> viewers, SpatialGrid? grid = null)
        { WriteStruct(in p); SendWrittenToInterested(m, origin, f, viewers, grid); }

        public void SendToAll(in SharedLibrary.ProjectileDestroyPacket p, DeliveryMethod m)
        { WriteStruct(in p); _net.SendToAll(_sharedWriter, m); }

        public void SendToInterested(in SharedLibrary.ProjectileDestroyPacket p, DeliveryMethod m,
            SharedLibrary.Vec2 origin, IInterestFilter f, IReadOnlyList<PlayerSession> viewers, SpatialGrid? grid = null)
        { WriteStruct(in p); SendWrittenToInterested(m, origin, f, viewers, grid); }

        /// <summary>Serialises once and sends to all interested peers (spatial + visibility filtered).</summary>
        public void SendToInterested(in SharedLibrary.StatusEffectAppliedPacket p, DeliveryMethod m,
            SharedLibrary.Vec2 origin, IInterestFilter f, IReadOnlyList<PlayerSession> viewers, SpatialGrid? grid = null)
        { WriteStruct(in p); SendWrittenToInterested(m, origin, f, viewers, grid); }

        /// <summary>Serialises and sends a StatusEffectAppliedPacket to one specific peer.</summary>
        public void SendTo(NetPeer peer, in SharedLibrary.StatusEffectAppliedPacket p, DeliveryMethod m)
        { WriteStruct(in p); peer.Send(_sharedWriter, m); }

        /// <summary>Serialises once and sends a StatusEffectRemovedPacket to all interested peers.</summary>
        public void SendToInterested(in SharedLibrary.StatusEffectRemovedPacket p, DeliveryMethod m,
            SharedLibrary.Vec2 origin, IInterestFilter f, IReadOnlyList<PlayerSession> viewers, SpatialGrid? grid = null)
        { WriteStruct(in p); SendWrittenToInterested(m, origin, f, viewers, grid); }

        /// <summary>Serialises and sends a StatusEffectRemovedPacket to one specific peer.</summary>
        public void SendTo(NetPeer peer, in SharedLibrary.StatusEffectRemovedPacket p, DeliveryMethod m)
        { WriteStruct(in p); peer.Send(_sharedWriter, m); }

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
            // Snapshot address string before handing to ThreadPool — IPEndPoint.ToString()
            // allocates; doing it here (once) is no worse than doing it in the lambda,
            // and the static lambda + TState pattern avoids a closure object on the heap.
            string addrStr = peer.Address?.ToString() ?? "unknown";
            ThreadPool.QueueUserWorkItem(
                static s => Console.WriteLine($"[Network] Peer connected: {s}"),
                addrStr, preferLocal: false);
            // Connected does not mean trusted; peer remains pending until AuthTicketPacket validates.
            _pendingAuthPeers[peer] = Environment.TickCount64; // game-loop thread only
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            string addrStr = peer.Address?.ToString() ?? "unknown";
            var logState = (addrStr, reason: info.Reason);
            ThreadPool.QueueUserWorkItem(
                static s => Console.WriteLine($"[Network] Peer disconnected: {s.addrStr} ({s.reason})"),
                logState, preferLocal: false);
            _pendingAuthPeers.Remove(peer);
            _arena.OnPlayerDisconnected(peer);
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
            => _processor.ReadAllPackets(reader, peer);

        public void OnNetworkError(IPEndPoint endPoint, SocketError error)
        {
            string epStr = endPoint?.ToString() ?? "unknown";
            var logState = (epStr, error);
            ThreadPool.QueueUserWorkItem(
                static s => Console.WriteLine($"[Network] Error from {s.epStr}: {s.error}"),
                logState, preferLocal: false);
        }

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
                _pendingAuthPeers.Remove(peer);
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
        /// Iterates _pendingAuthPeers using a for-loop over _timedOutPeers to avoid the
        /// boxed IEnumerator that foreach over ConcurrentDictionary produces.  The two-pass
        /// approach (collect then remove) also prevents a dictionary-modified-during-enumeration
        /// exception if a peer disconnects concurrently — not currently possible (single-thread
        /// model) but safe defensively.
        /// </summary>
        private void DisconnectAuthTimeoutPeers()
        {
            EvictStaleIpGuards();

            if (_pendingAuthPeers.Count == 0)
                return;

            long nowMs = Environment.TickCount64;
            _timedOutPeers.Clear();

            // Dictionary<K,V>.Enumerator is a public value-type struct — foreach uses it
            // directly without boxing, unlike ConcurrentDictionary which only exposes
            // IEnumerator<KVP> (heap-allocated object).
            foreach (KeyValuePair<NetPeer, long> entry in _pendingAuthPeers)
            {
                if (nowMs - entry.Value >= AuthTimeoutMs)
                    _timedOutPeers.Add(entry.Key);
            }

            for (int i = 0; i < _timedOutPeers.Count; i++)
            {
                NetPeer peer = _timedOutPeers[i];
                _pendingAuthPeers.Remove(peer);
                RegisterIpViolation(peer.Address, 2);
                SecurityTelemetry.RecordInvalidTicket("auth-timeout", peer.Address);
                peer.Disconnect();
            }
        }

        // Pre-allocated list for stale IP guard eviction — same pattern as _timedOutPeers.
        // Collecting keys to remove in a first pass avoids modifying the Dictionary while
        // iterating it (which throws InvalidOperationException).
        private readonly List<IPAddress> _staleIpAddresses = new List<IPAddress>();

        /// <summary>
        /// Removes _ipGuards entries for IPs that are neither currently banned nor recently active.
        /// Prevents unbounded map growth under IPv4/IPv6 source-address exhaustion floods.
        /// Runs at a low interval (every ~10 s) so the iteration cost is negligible.
        ///
        /// Two-pass pattern (collect then remove) is required because modifying a Dictionary
        /// while enumerating it throws InvalidOperationException.  Dictionary.Enumerator is a
        /// public value-type struct — the foreach here does NOT box, unlike ConcurrentDictionary.
        /// </summary>
        private void EvictStaleIpGuards()
        {
            if (++_ipGuardEvictionTick < IpGuardEvictionIntervalTicks)
                return;

            _ipGuardEvictionTick = 0;
            long nowMs = Environment.TickCount64;

            // Pass 1: collect stale keys into the pre-allocated scratch list.
            _staleIpAddresses.Clear();
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
                    _staleIpAddresses.Add(entry.Key);
            }

            // Pass 2: remove outside the enumeration loop.
            for (int i = 0; i < _staleIpAddresses.Count; i++)
                _ipGuards.Remove(_staleIpAddresses[i]);
        }

        /// <summary>
        /// Returns true when an IP should be rejected for rate-limit or active temporary ban.
        /// </summary>
        private bool IsIpRejected(IPAddress ipAddress, out bool isRateLimited)
        {
            isRateLimited = false;
            long nowMs = Environment.TickCount64;
            // TryGetValue + conditional Add eliminates the GetOrAdd factory delegate alloc.
            if (!_ipGuards.TryGetValue(ipAddress, out IpGuardState? state))
            {
                state = new IpGuardState(nowMs);
                _ipGuards[ipAddress] = state;
            }

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
            if (!_ipGuards.TryGetValue(address, out IpGuardState? state))
            {
                state = new IpGuardState(nowMs);
                _ipGuards[address] = state;
            }
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
