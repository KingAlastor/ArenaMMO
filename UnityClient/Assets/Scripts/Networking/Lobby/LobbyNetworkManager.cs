using LiteNetLib;
using LiteNetLib.Utils;
using SharedLibrary;
using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace ArenaMMO.Networking
{
    /// <summary>
    /// MonoBehaviour that connects to the LobbyServer, handles login, matchmaking queue,
    /// and transitions to the arena scene when a match is found.
    ///
    /// Usage:
    ///   1. Attach to a persistent GameObject in your Lobby scene.
    ///   2. Set LobbyHost / LobbyPort in the Inspector.
    ///   3. Wire the UnityEvents to your UI (login panel, queue panel, etc.).
    ///   4. Call Connect(playerName, credentialToken) when the player clicks "Play".
    ///   5. Call JoinQueue() once OnLoginSuccess fires.
    ///
    /// After OnMatchFound fires the component auto-loads ArenaSceneName.
    /// The arena's network manager reads LobbyNetworkManager.PendingTicket on Awake to authenticate.
    /// </summary>
    public sealed class LobbyNetworkManager : MonoBehaviour, INetEventListener
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Lobby Server")]
        [SerializeField] private string _lobbyHost = "127.0.0.1";
        [SerializeField] private int    _lobbyPort = 9040;

        [Header("Arena Scene")]
        [Tooltip("Exact scene name to load when a match is found.")]
        [SerializeField] private string _arenaSceneName = "Arena";

        // ── Events ────────────────────────────────────────────────────────────

        [Header("Events")]
        [Tooltip("Fired on successful login. Args: (playerId, playerName)")]
        public UnityEvent<int, string>   OnLoginSuccess    = new();

        [Tooltip("Fired when login is rejected. Arg: error code string")]
        public UnityEvent<string>        OnLoginFailed     = new();

        [Tooltip("Periodic queue update. Args: (position, playersInQueue, playersNeeded)")]
        public UnityEvent<int, int, int> OnQueueStatusUpdate = new();

        [Tooltip("Fired just before scene transition. Arg: the pending MatchTicket.")]
        public UnityEvent<MatchTicket>   OnMatchFound      = new();

        // ── Static cross-scene ticket storage ────────────────────────────────

        /// <summary>
        /// Set when MatchFoundPacket is received. The arena's network manager reads this
        /// on Awake to build and send the AuthTicketPacket to the arena server.
        /// Cleared to null after the arena consumes it.
        /// </summary>
        public static MatchTicket? PendingTicket { get; private set; }

        // ── Private ───────────────────────────────────────────────────────────

        private NetManager?         _net;
        private NetPacketProcessor? _processor;
        private NetPeer?            _lobbyPeer;

        private string _pendingName  = string.Empty;
        private string _pendingToken = string.Empty;

        private const string ConnectionKey = "ArenaMMO_Lobby_v1";

        // ── Unity lifecycle ───────────────────────────────────────────────────

        private void Awake()
        {
            _processor = new NetPacketProcessor();
            _processor.RegisterNestedType<Vec2>();

            _processor.SubscribeReusable<LobbyLoginResponsePacket>(HandleLoginResponse);
            _processor.SubscribeReusable<LobbyQueueStatusPacket>(HandleQueueStatus);
            _processor.SubscribeReusable<MatchFoundPacket>(HandleMatchFound);

            _net = new NetManager(this) { AutoRecycle = true };
            _net.Start();
        }

        private void Update()
        {
            _net?.PollEvents();
        }

        private void OnDestroy()
        {
            _lobbyPeer?.Disconnect();
            _net?.Stop();
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Initiates the connection to the lobby server and queues a login request
        /// to be sent once the UDP handshake completes.
        /// </summary>
        /// <param name="playerName">Display name of the player.</param>
        /// <param name="credentialToken">
        /// Opaque auth token (e.g. hashed password, JWT, Steam session ticket).
        /// Must match whatever scheme PlayerAuthService.TryAuthenticateAsync expects.
        /// </param>
        public void Connect(string playerName, string credentialToken)
        {
            if (_net == null || _lobbyPeer != null)
                return;

            _pendingName  = playerName;
            _pendingToken = credentialToken;

            var writer = new NetDataWriter();
            writer.Put(ConnectionKey);
            _lobbyPeer = _net.Connect(_lobbyHost, _lobbyPort, writer);
        }

        /// <summary>
        /// Sends a queue-join request. Call this after OnLoginSuccess has fired.
        /// </summary>
        public void JoinQueue()
        {
            if (_lobbyPeer == null || _lobbyPeer.ConnectionState != ConnectionState.Connected)
            {
                Debug.LogWarning("[LobbyClient] JoinQueue called before connection was ready.");
                return;
            }

            SendPacket(new LobbyQueueJoinPacket());
        }

        /// <summary>Manually disconnect from the lobby (e.g. player presses Cancel).</summary>
        public void Disconnect()
        {
            _lobbyPeer?.Disconnect();
            _lobbyPeer = null;
        }

        // ── INetEventListener ─────────────────────────────────────────────────

        public void OnPeerConnected(NetPeer peer)
        {
            Debug.Log("[LobbyClient] Connected to lobby server. Sending login...");
            SendPacket(new LobbyLoginRequestPacket
            {
                PlayerName      = _pendingName,
                CredentialToken = _pendingToken,
            });
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            Debug.Log($"[LobbyClient] Disconnected from lobby: {info.Reason}");
            _lobbyPeer = null;
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod method)
        {
            try
            {
                _processor?.ReadAllPackets(reader);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LobbyClient] Packet parse error: {ex.Message}");
            }
        }

        // Lobby client never accepts incoming connections from others.
        public void OnConnectionRequest(ConnectionRequest request) => request.Reject();

        public void OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError)
            => Debug.LogError($"[LobbyClient] Network error from {endPoint}: {socketError}");

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
        public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }

        // ── Packet handlers (called from Update via PollEvents) ───────────────

        private void HandleLoginResponse(LobbyLoginResponsePacket packet)
        {
            if (packet.Success)
            {
                Debug.Log($"[LobbyClient] Login OK — {packet.PlayerName} (id={packet.PlayerId})");
                OnLoginSuccess.Invoke(packet.PlayerId, packet.PlayerName);
            }
            else
            {
                Debug.LogWarning($"[LobbyClient] Login failed: {packet.Error}");
                OnLoginFailed.Invoke(packet.Error);
            }
        }

        private void HandleQueueStatus(LobbyQueueStatusPacket packet)
        {
            Debug.Log($"[LobbyClient] Queue position {packet.QueuePosition}/{packet.PlayersNeeded}");
            OnQueueStatusUpdate.Invoke(packet.QueuePosition, packet.PlayersInQueue, packet.PlayersNeeded);
        }

        private void HandleMatchFound(MatchFoundPacket packet)
        {
            Debug.Log($"[LobbyClient] Match found — arena at {packet.ArenaIp}:{packet.ArenaPort}");

            PendingTicket = new MatchTicket(
                arenaIp:            packet.ArenaIp,
                arenaPort:          packet.ArenaPort,
                playerId:           packet.PlayerId,
                playerName:         packet.PlayerName,
                faction:            (FactionId)packet.Faction,
                allowedSpellIdsCsv: packet.AllowedSpellIdsCsv,
                issuedAtUnixMs:     packet.IssuedAtUnixMs,
                expiresAtUnixMs:    packet.ExpiresAtUnixMs,
                nonce:              packet.Nonce,
                signature:          packet.Signature);

            OnMatchFound.Invoke(PendingTicket.Value);

            // Disconnect from lobby cleanly before loading the arena scene.
            Disconnect();
            SceneManager.LoadScene(_arenaSceneName);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void SendPacket<T>(T packet) where T : class, new()
        {
            if (_lobbyPeer == null || _processor == null)
                return;

            var writer = new NetDataWriter();
            _processor.Write(writer, packet);
            _lobbyPeer.Send(writer, DeliveryMethod.ReliableOrdered);
        }
    }

    // ── MatchTicket ───────────────────────────────────────────────────────────

    /// <summary>
    /// Immutable snapshot of the arena ticket received from the lobby.
    /// Stored in LobbyNetworkManager.PendingTicket and read by the arena's
    /// network manager on Awake to authenticate with the arena server.
    /// </summary>
    public readonly struct MatchTicket
    {
        public readonly string    ArenaIp;
        public readonly int       ArenaPort;
        public readonly int       PlayerId;
        public readonly string    PlayerName;
        public readonly FactionId Faction;
        public readonly string    AllowedSpellIdsCsv;
        public readonly long      IssuedAtUnixMs;
        public readonly long      ExpiresAtUnixMs;
        public readonly string    Nonce;
        public readonly string    Signature;

        public MatchTicket(
            string    arenaIp,
            int       arenaPort,
            int       playerId,
            string    playerName,
            FactionId faction,
            string    allowedSpellIdsCsv,
            long      issuedAtUnixMs,
            long      expiresAtUnixMs,
            string    nonce,
            string    signature)
        {
            ArenaIp            = arenaIp;
            ArenaPort          = arenaPort;
            PlayerId           = playerId;
            PlayerName         = playerName;
            Faction            = faction;
            AllowedSpellIdsCsv = allowedSpellIdsCsv;
            IssuedAtUnixMs     = issuedAtUnixMs;
            ExpiresAtUnixMs    = expiresAtUnixMs;
            Nonce              = nonce;
            Signature          = signature;
        }

        /// <summary>
        /// Converts this ticket into the wire packet format expected by the arena server.
        /// Call this on Awake in your arena network manager and send it immediately after connecting.
        /// </summary>
        public AuthTicketPacket ToAuthTicketPacket() => new AuthTicketPacket
        {
            PlayerId           = PlayerId,
            PlayerName         = PlayerName,
            Faction            = (byte)Faction,
            AllowedSpellIdsCsv = AllowedSpellIdsCsv,
            IssuedAtUnixMs     = IssuedAtUnixMs,
            ExpiresAtUnixMs    = ExpiresAtUnixMs,
            Nonce              = Nonce,
            Signature          = Signature,
        };
    }
}
