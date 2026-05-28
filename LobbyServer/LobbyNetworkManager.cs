using LiteNetLib;
using LiteNetLib.Utils;
using SharedLibrary;
using StackExchange.Redis;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LobbyServer
{
    /// <summary>
    /// Accepts UDP connections from Unity clients via LiteNetLib, routes lobby packets,
    /// drives matchmaking, and sends MatchFoundPackets once teams are formed.
    ///
    /// Threading model:
    ///   The main loop thread calls PollEvents() → all LiteNetLib callbacks fire synchronously
    ///   on that thread. OnLoginRequest spawns a Task for async DB access; that Task then calls
    ///   Send() on a background thread. NetPeer.Send() is thread-safe in LiteNetLib, so no lock
    ///   is needed — each Send() call allocates its own NetDataWriter (lobby traffic is low frequency).
    /// </summary>
    internal sealed class LobbyNetworkManager : INetEventListener, IDisposable
    {
        private readonly NetManager         _net;
        private readonly NetPacketProcessor _processor;
        private readonly PlayerAuthService  _authService;
        private readonly MatchmakingQueue   _queue;
        private readonly TicketIssuer       _ticketIssuer;
        private readonly ISubscriber        _redis;
        private readonly string             _arenaIp;
        private readonly int                _arenaPort;
        private readonly int                _queueStatusIntervalMs;

        // PlayerId → NetPeer (post-authentication only)
        private readonly ConcurrentDictionary<int, NetPeer>        _authenticatedPeers = new();
        // NetPeer → PlayerId (reverse map for disconnect cleanup)
        private readonly ConcurrentDictionary<NetPeer, int>        _peerPlayerMap      = new();
        // Cached player profile keyed by PlayerId — needed when the queue-join packet arrives
        // after the async auth task completes.
        private readonly ConcurrentDictionary<int, PlayerProfile>  _profileCache       = new();
        // Tracks peers that have connected but not yet authenticated.
        private readonly ConcurrentDictionary<NetPeer, byte>       _pendingAuth        = new();

        private long _lastQueueBroadcastMs;

        private const string ConnectionKey = "ArenaMMO_Lobby_v1";

        public LobbyNetworkManager(
            PlayerAuthService authService,
            MatchmakingQueue  queue,
            TicketIssuer      ticketIssuer,
            ISubscriber       redis,
            string            arenaIp,
            int               arenaPort,
            int               queueStatusIntervalMs)
        {
            _authService           = authService;
            _queue                 = queue;
            _ticketIssuer          = ticketIssuer;
            _redis                 = redis;
            _arenaIp               = arenaIp;
            _arenaPort             = arenaPort;
            _queueStatusIntervalMs = queueStatusIntervalMs;

            _net       = new NetManager(this) { AutoRecycle = true };
            _processor = new NetPacketProcessor();
            _processor.RegisterNestedType<Vec2>();
            _processor.SubscribeReusable<LobbyLoginRequestPacket, NetPeer>(OnLoginRequest);
            _processor.SubscribeReusable<LobbyQueueJoinPacket,    NetPeer>(OnQueueJoin);
        }

        // ── Entry point ───────────────────────────────────────────────────────

        public void Start(int port)
        {
            if (!_net.Start(port))
                throw new InvalidOperationException($"Failed to bind UDP socket on port {port}.");

            Console.WriteLine($"[LobbyServer] Listening on UDP :{port}");

            while (true)
            {
                _net.PollEvents();
                TryFormAndDispatchMatch();
                BroadcastQueueStatusIfDue();
                Thread.Sleep(50);   // 20 Hz coordination loop — no game simulation here
            }
        }

        // ── INetEventListener ─────────────────────────────────────────────────

        public void OnConnectionRequest(ConnectionRequest request)
        {
            if (request.Data.TryGetString(out string? key) && key == ConnectionKey)
            {
                NetPeer peer = request.Accept();
                _pendingAuth.TryAdd(peer, 0);
            }
            else
            {
                request.Reject();
            }
        }

        public void OnPeerConnected(NetPeer peer)
            => Console.WriteLine($"[LobbyServer] Peer connected: {peer.Address}");

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            Console.WriteLine($"[LobbyServer] Peer disconnected: {peer.Address} ({info.Reason})");
            _pendingAuth.TryRemove(peer, out _);

            if (_peerPlayerMap.TryRemove(peer, out int playerId))
            {
                _authenticatedPeers.TryRemove(playerId, out _);
                _profileCache.TryRemove(playerId, out _);
                _queue.Remove(playerId);
            }
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod method)
        {
            try
            {
                _processor.ReadAllPackets(reader, peer);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LobbyServer] Packet parse error from {peer.Address}: {ex.Message}");
                peer.Disconnect();
            }
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
            => Console.WriteLine($"[LobbyServer] Socket error from {endPoint}: {socketError}");

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }

        // ── Packet handlers ───────────────────────────────────────────────────

        private void OnLoginRequest(LobbyLoginRequestPacket packet, NetPeer peer)
        {
            if (!_pendingAuth.ContainsKey(peer))
                return;

            if (string.IsNullOrWhiteSpace(packet.PlayerName) || packet.PlayerName.Length > 24
                || string.IsNullOrWhiteSpace(packet.CredentialToken))
            {
                Send(peer, new LobbyLoginResponsePacket { Success = false, Error = "invalid-request-shape" });
                peer.Disconnect();
                return;
            }

            // DB access is async — capture values before leaving the LiteNetLib callback thread.
            NetPeer capturedPeer  = peer;
            string  capturedName  = packet.PlayerName;
            string  capturedToken = packet.CredentialToken;

            Task.Run(async () =>
            {
                PlayerProfile? profile = await _authService.TryAuthenticateAsync(capturedName, capturedToken);

                if (profile is null)
                {
                    Send(capturedPeer, new LobbyLoginResponsePacket { Success = false, Error = "auth-failed" });
                    capturedPeer.Disconnect();
                    return;
                }

                _pendingAuth.TryRemove(capturedPeer, out _);
                _profileCache[profile.AccountId]     = profile;
                _authenticatedPeers[profile.AccountId] = capturedPeer;
                _peerPlayerMap[capturedPeer]           = profile.AccountId;

                Send(capturedPeer, new LobbyLoginResponsePacket
                {
                    Success    = true,
                    PlayerId   = profile.AccountId,
                    PlayerName = profile.PlayerName,
                });

                Console.WriteLine($"[LobbyServer] Authenticated: {profile.PlayerName} (id={profile.AccountId})");
            });
        }

        private void OnQueueJoin(LobbyQueueJoinPacket packet, NetPeer peer)
        {
            if (!_peerPlayerMap.TryGetValue(peer, out int playerId)
                || !_profileCache.TryGetValue(playerId, out PlayerProfile? profile))
            {
                // Not authenticated yet — reject silently.
                return;
            }

            _queue.Enqueue(new QueuedPlayer(playerId, profile.PlayerName, profile.AllowedSpellIdsCsv, peer));

            var (pos, total, needed) = _queue.GetStatus(playerId);
            Send(peer, new LobbyQueueStatusPacket
            {
                QueuePosition  = pos,
                PlayersInQueue = total,
                PlayersNeeded  = needed,
            });

            Console.WriteLine($"[LobbyServer] {profile.PlayerName} joined queue. ({total}/{needed})");
        }

        // ── Match formation ───────────────────────────────────────────────────

        private void TryFormAndDispatchMatch()
        {
            MatchGroup? match = _queue.TryFormMatch();
            if (match is null)
                return;

            Console.WriteLine($"[LobbyServer] Match formed — {match.Players.Count} players.");

            var playerIds = new List<int>(match.Players.Count);

            foreach (QueuedPlayer player in match.Players)
            {
                AuthTicketPacket ticket = _ticketIssuer.Issue(
                    player.PlayerId,
                    player.PlayerName,
                    (byte)player.Faction,
                    player.AllowedSpellIdsCsv);

                Send(player.Peer, new MatchFoundPacket
                {
                    ArenaIp            = _arenaIp,
                    ArenaPort          = _arenaPort,
                    PlayerId           = ticket.PlayerId,
                    PlayerName         = ticket.PlayerName,
                    Faction            = ticket.Faction,
                    AllowedSpellIdsCsv = ticket.AllowedSpellIdsCsv,
                    IssuedAtUnixMs     = ticket.IssuedAtUnixMs,
                    ExpiresAtUnixMs    = ticket.ExpiresAtUnixMs,
                    Nonce              = ticket.Nonce,
                    Signature          = ticket.Signature,
                });

                playerIds.Add(player.PlayerId);
                Console.WriteLine($"  → Ticket issued: {player.PlayerName} (faction={player.Faction})");
            }

            // Notify the arena server so it can pre-log expected players, emit metrics, etc.
            // The arena does not gate admission on this message — ticket HMAC is the authority.
            PublishMatchFormedEvent(playerIds);
        }

        private void PublishMatchFormedEvent(List<int> playerIds)
        {
            try
            {
                string payload = JsonSerializer.Serialize(new
                {
                    PlayerIds  = playerIds,
                    ArenaPort  = _arenaPort,
                    FormedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                });

                _redis.Publish(RedisChannel.Literal("arena:match-formed"), payload);
            }
            catch (Exception ex)
            {
                // Redis failure must not break match dispatch — clients already have their tickets.
                Console.WriteLine($"[LobbyServer] Redis publish failed: {ex.Message}");
            }
        }

        // ── Periodic queue status ─────────────────────────────────────────────

        private void BroadcastQueueStatusIfDue()
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (nowMs - _lastQueueBroadcastMs < _queueStatusIntervalMs)
                return;

            _lastQueueBroadcastMs = nowMs;

            foreach (KeyValuePair<NetPeer, int> kv in _peerPlayerMap)
            {
                var (pos, total, needed) = _queue.GetStatus(kv.Value);
                if (pos < 0)
                    continue;   // not in queue (authenticated but hasn't joined yet)

                Send(kv.Key, new LobbyQueueStatusPacket
                {
                    QueuePosition  = pos,
                    PlayersInQueue = total,
                    PlayersNeeded  = needed,
                });
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Serializes and sends a packet to one peer.
        /// Each call uses its own NetDataWriter — safe to call from any thread.
        /// </summary>
        private void Send<T>(NetPeer peer, T packet) where T : class, new()
        {
            var writer = new NetDataWriter();
            _processor.Write(writer, packet);
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        public void Dispose() => _net.Stop();
    }
}
