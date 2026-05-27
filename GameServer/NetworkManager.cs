using LiteNetLib;
using LiteNetLib.Utils;
using SharedLibrary;
using System;
using System.Net;
using System.Net.Sockets;

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

        // Must match the key sent by the Unity client in NetManager.Connect(...)
        private const string ConnectionKey = "ArenaMMO_v1";

        public NetworkManager(ArenaInstance arena, int port)
        {
            _arena     = arena;
            _processor = new NetPacketProcessor();

            // Register every Client → Server packet type with its handler
            _processor.SubscribeReusable<PlayerInputPacket,      NetPeer>(OnPlayerInput);
            _processor.SubscribeReusable<AttackRequestPacket,    NetPeer>(OnAttackRequest);
            _processor.SubscribeReusable<SpellCastRequestPacket, NetPeer>(OnSpellCastRequest);
            _processor.SubscribeReusable<ShootRequestPacket,     NetPeer>(OnShootRequest);  

            _net = new NetManager(this) { AutoRecycle = true };
            _net.Start(port);

            Console.WriteLine($"[Network] Server listening on UDP :{port}");
        }

        /// <summary>
        /// Must be called once per game tick to dispatch all queued LiteNetLib events.
        /// All INetEventListener callbacks fire synchronously on the calling thread.
        /// </summary>
        public void PollEvents() => _net.PollEvents();

        /// <summary>Serialises and broadcasts a packet to every connected peer.</summary>
        public void SendToAll<T>(T packet, DeliveryMethod method) where T : class, new()
        {
            // TODO: pool NetDataWriter instances to eliminate per-call allocation at high CCU
            var writer = new NetDataWriter();
            _processor.Write(writer, packet);
            _net.SendToAll(writer, method);
        }

        /// <summary>Serialises and sends a packet to one specific peer.</summary>
        public void SendTo<T>(NetPeer peer, T packet, DeliveryMethod method) where T : class, new()
        {
            var writer = new NetDataWriter();
            _processor.Write(writer, packet);
            peer.Send(writer, method);
        }

        // ── INetEventListener ─────────────────────────────────────────────────

        public void OnConnectionRequest(ConnectionRequest request)
        {
            // Reject connections that do not supply the correct version key
            request.AcceptIfKey(ConnectionKey);
        }

        public void OnPeerConnected(NetPeer peer)
        {
            Console.WriteLine($"[Network] Peer connected: {peer.Address}");
            _arena.OnPlayerConnected(peer);
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            Console.WriteLine($"[Network] Peer disconnected: {peer.Address} ({info.Reason})");
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

        public void Dispose() => _net.Stop();
    }
}
