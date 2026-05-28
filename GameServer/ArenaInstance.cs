using GameServer.Systems;
using LiteNetLib;
using SharedLibrary;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;

namespace GameServer
{
    /// <summary>
    /// Manages one self-contained arena match.
    /// Owns the fixed-tick simulation loop, all PlayerSessions, the NetworkManager,
    /// and the three input queues that decouple packet receipt from game simulation.
    ///
    /// Threading model:
    ///   PollEvents() is called from the game loop thread, so all LiteNetLib callbacks
    ///   currently fire on the same thread. The ConcurrentQueues are ready for a future
    ///   design where packet I/O runs on a dedicated thread.
    /// </summary>
    public sealed class ArenaInstance
    {
        // Simulation cadence. Keep these aligned with client prediction/interpolation assumptions.
        private const int   TickRate  = 30;
        private const float DeltaTime = 1f / TickRate;
        private const int   MsPerTick = 1000 / TickRate;

        // ── Player State ──────────────────────────────────────────────────────

        private readonly List<PlayerSession>                _players   = new List<PlayerSession>();
        private readonly Dictionary<NetPeer, PlayerSession> _peerMap   = new Dictionary<NetPeer, PlayerSession>();
        private readonly Dictionary<int, PlayerSession>     _entityMap = new Dictionary<int, PlayerSession>();
        private int _nextEntityId = 1;

        // Reusable per-tick lists — allocated once, cleared before each use to avoid GC churn.
        private readonly List<StatusEffectAppliedPacket> _reusableStatusEffects = new List<StatusEffectAppliedPacket>();
        private readonly List<CombatEventPacket>          _reusableSpellEvents   = new List<CombatEventPacket>();

        // ── Input Queues ──────────────────────────────────────────────────────
        // Filled by network callbacks each tick; drained by ProcessTick().

        private readonly ConcurrentDictionary<NetPeer, PlayerInputPacket> _latestInputByPeer = new();
        private readonly ConcurrentQueue<(NetPeer Peer, AttackRequestPacket    Packet)> _attackQueue = new();
        private readonly ConcurrentQueue<(NetPeer Peer, SpellCastRequestPacket Packet)> _spellQueue  = new();
        private readonly ConcurrentQueue<(NetPeer Peer, ShootRequestPacket     Packet)> _shootQueue  = new();
        // IntentGuard enforces anti-spam, tick skew, and replay rules before intents enter simulation.
        private readonly IntentGuard _intentGuard = new();
        // Ticket validator is the trust boundary between lobby-issued identity and live arena authority.
        private readonly AuthTicketValidator _ticketValidator;

        // ── Projectile State ───────────────────────────────────────────

        private readonly List<ProjectileState> _projectiles    = new List<ProjectileState>();
        private int                            _nextProjectileId = 1;

        private readonly List<CombatEventPacket> _statusTickEvents = new List<CombatEventPacket>();
        private readonly List<StatusEffectRemovedPacket> _expiredStatusEffects = new List<StatusEffectRemovedPacket>();

        // ── Internals ─────────────────────────────────────────────────────────

        private NetworkManager? _network;
        private int  _tick      = 0;
        private bool _isRunning = false;

        // ── Entry Point ───────────────────────────────────────────────────────

        /// <summary>
        /// Creates one arena runtime with a ticket validator bound to the configured signing secret.
        /// The secret is never rotated in-process; restart the server after secret changes.
        /// </summary>
        public ArenaInstance(string ticketSecret)
        {
            _ticketValidator = new AuthTicketValidator(ticketSecret);
        }

        /// <summary>Starts the network listener and blocks on the game loop until shutdown.</summary>
        public void Start(int port)
        {
            _network   = new NetworkManager(this, port);
            _isRunning = true;
            Console.WriteLine($"[Arena] Instance running at {TickRate} Hz");
            RunGameLoop();
        }

        // ── Connection Lifecycle ──────────────────────────────────────────────

        /// <summary>
        /// Validates a lobby-issued auth ticket and materializes a PlayerSession only on success.
        /// This is the only legal path that inserts a peer into authoritative player collections.
        /// </summary>
        public bool TryAuthenticatePeer(NetPeer peer, AuthTicketPacket ticket, IPAddress? ip)
        {
            if (!_ticketValidator.TryValidate(ticket, out AuthenticatedPeerContext context, out string error))
            {
                SecurityTelemetry.RecordInvalidTicket(error, ip);
                return false;
            }

            OnPlayerAuthenticated(peer, context);
            return true;
        }

        /// <summary>
        /// Initializes authoritative in-match state from authenticated context.
        /// Note: combat stats are still placeholder defaults until profile hydration is integrated.
        /// </summary>
        private void OnPlayerAuthenticated(NetPeer peer, AuthenticatedPeerContext context)
        {
            var session = new PlayerSession
            {
                AccountId  = context.PlayerId,
                EntityId   = _nextEntityId++,
                PlayerName = context.PlayerName,
                Peer       = peer,
                Faction    = context.Faction,
                Position   = Vec2.Zero,
                Health     = 100f,
                MaxHealth  = 100f,
            };
            session.ReplaceAllowedSpells(context.AllowedSpellIds);

            _players.Add(session);
            _peerMap[peer]             = session;
            _entityMap[session.EntityId] = session;
            _intentGuard.OnPeerConnected(peer);
            Console.WriteLine($"[Arena] Authenticated {session.PlayerName} (account={session.AccountId}, entity={session.EntityId})");
        }

        public void OnPlayerDisconnected(NetPeer peer)
        {
            if (_peerMap.TryGetValue(peer, out PlayerSession? session))
            {
                _players.Remove(session);
                _peerMap.Remove(peer);
                _entityMap.Remove(session.EntityId);
                _latestInputByPeer.TryRemove(peer, out _);
                _intentGuard.OnPeerDisconnected(peer);
                Console.WriteLine($"[Arena] Removed {session.PlayerName} (id={session.EntityId})");
            }
        }

        // ── Queue Entry Points (called from network callbacks) ────────────────

        public void EnqueueInput(NetPeer peer, PlayerInputPacket packet)
        {
            // Packet shape/float sanity gate. This blocks NaN/Inf poisoning and malformed payloads
            // before any per-tick movement math runs.
            if (!InputSanitizer.IsValid(packet))
            {
                SecurityTelemetry.RecordInvalidPacket("invalid-input-packet", peer);
                return;
            }

            if (!_intentGuard.TryAcceptIntent(peer, packet.TickNumber, IntentKind.Input, _tick, _peerMap.ContainsKey(peer)))
                return;

            // Keep only the latest input per peer to prevent scheduler spam from growing work.
            _latestInputByPeer[peer] = packet;
        }

        public void EnqueueAttack(NetPeer peer, AttackRequestPacket packet)
        {
            // Action packets must carry monotonic sequence IDs for replay resistance.
            if (!InputSanitizer.IsValid(packet))
            {
                SecurityTelemetry.RecordInvalidPacket("invalid-attack-packet", peer);
                return;
            }

            if (!_intentGuard.TryAcceptIntent(peer, packet.TickNumber, IntentKind.Attack, _tick, _peerMap.ContainsKey(peer), packet.ActionSequenceId))
            {
                SecurityTelemetry.RecordReplayDrop("attack-sequence-or-rate-rejected", peer);
                return;
            }

            if (!_intentGuard.TryReserveActionSlot(peer, IntentKind.Attack))
                return;

            _attackQueue.Enqueue((peer, packet));
        }

        public void EnqueueSpellCast(NetPeer peer, SpellCastRequestPacket packet)
        {
            if (!InputSanitizer.IsValid(packet))
            {
                SecurityTelemetry.RecordInvalidPacket("invalid-spell-packet", peer);
                return;
            }

            // Gate by authenticated loadout entitlement. Clients can request any spellId,
            // but only server-authorized spell IDs are admitted.
            if (!_peerMap.TryGetValue(peer, out PlayerSession? caster))
                return;

            if (!caster.IsSpellAllowed(packet.SpellId))
            {
                SecurityTelemetry.RecordUnauthorizedSpell(peer, packet.SpellId);
                return;
            }

            if (!_intentGuard.TryAcceptIntent(peer, packet.TickNumber, IntentKind.Spell, _tick, true, packet.ActionSequenceId))
            {
                SecurityTelemetry.RecordReplayDrop("spell-sequence-or-rate-rejected", peer);
                return;
            }

            if (!_intentGuard.TryReserveActionSlot(peer, IntentKind.Spell))
                return;

            _spellQueue.Enqueue((peer, packet));
        }

        public void EnqueueShoot(NetPeer peer, ShootRequestPacket packet)
        {
            if (!InputSanitizer.IsValid(packet))
            {
                SecurityTelemetry.RecordInvalidPacket("invalid-shoot-packet", peer);
                return;
            }

            // Shoot requests share the same entitlement model as spell casts.
            if (!_peerMap.TryGetValue(peer, out PlayerSession? shooter))
                return;

            if (!shooter.IsSpellAllowed(packet.SpellId))
            {
                SecurityTelemetry.RecordUnauthorizedSpell(peer, packet.SpellId);
                return;
            }

            if (!_intentGuard.TryAcceptIntent(peer, packet.TickNumber, IntentKind.Shoot, _tick, true, packet.ActionSequenceId))
            {
                SecurityTelemetry.RecordReplayDrop("shoot-sequence-or-rate-rejected", peer);
                return;
            }

            if (!_intentGuard.TryReserveActionSlot(peer, IntentKind.Shoot))
                return;

            _shootQueue.Enqueue((peer, packet));
        }

        // ── Game Loop ─────────────────────────────────────────────────────────

        private void RunGameLoop()
        {
            var sw = new Stopwatch();

            while (_isRunning)
            {
                sw.Restart();

                _network!.PollEvents();   // fires queued callbacks → fills the input queues
                ProcessTick();             // drain queues & run authoritative simulation
                BroadcastState();          // push authoritative positions + health to all peers

                // Emit periodic security counters to provide low-cost operational observability.
                if ((_tick % (TickRate * 10)) == 0)
                    SecurityTelemetry.PrintSnapshot();

                _tick++;

                int sleep = MsPerTick - (int)sw.ElapsedMilliseconds;
                if (sleep > 0) Thread.Sleep(sleep);
            }
        }

        private void ProcessTick()
        {
            // Tick order is intentionally fixed. Reordering phases can change gameplay semantics
            // (for example, movement-before-combat range checks and projectile-before-DoT timing).

            // ── 1. Movement ───────────────────────────────────────────────────
            foreach (KeyValuePair<NetPeer, PlayerInputPacket> entry in _latestInputByPeer)
            {
                if (!_latestInputByPeer.TryRemove(entry.Key, out PlayerInputPacket? latestInput))
                    continue;

                if (_peerMap.TryGetValue(entry.Key, out PlayerSession? player))
                    MovementSystem.ProcessInput(player, latestInput, DeltaTime);
            }
            // Snapshot authoritative positions after movement for lag-compensation rewind.
            for (int i = 0; i < _players.Count; i++)
                _players[i].RecordPositionHistory(_tick);
            // ── 2. Melee auto-attacks ─────────────────────────────────────────
            while (_attackQueue.TryDequeue(out var entry))
            {
                _intentGuard.ReleaseActionSlot(entry.Peer, IntentKind.Attack);
                if (!_peerMap.TryGetValue(entry.Peer, out PlayerSession? attacker)) continue;

                PlayerSession? target = FindById(entry.Packet.TargetEntityId);
                if (target == null) continue;

                _reusableStatusEffects.Clear();
                CombatEventPacket? ev = CombatSystem.ProcessMeleeAttack(
                    attacker, target, _tick, entry.Packet.TickNumber, _reusableStatusEffects);
                if (ev != null) BroadcastCombatEvent(ev);
                BroadcastStatusEffects(_reusableStatusEffects);
            }

            // ── 3. Spell casts ────────────────────────────────────────────────
            while (_spellQueue.TryDequeue(out var entry))
            {
                _intentGuard.ReleaseActionSlot(entry.Peer, IntentKind.Spell);
                if (!_peerMap.TryGetValue(entry.Peer, out PlayerSession? caster)) continue;
                if (!caster.IsSpellAllowed(entry.Packet.SpellId)) continue;
                if (!SpellDatabase.TryGet(entry.Packet.SpellId, out SpellDefinition spell)) continue;

                _reusableStatusEffects.Clear();
                _reusableSpellEvents.Clear();
                CombatSystem.ProcessSpellCast(
                    caster, entry.Packet, spell, _players, _tick,
                    _reusableSpellEvents, _reusableStatusEffects);
                for (int evIdx = 0; evIdx < _reusableSpellEvents.Count; evIdx++)
                    BroadcastCombatEvent(_reusableSpellEvents[evIdx]);

                BroadcastStatusEffects(_reusableStatusEffects);
            }

            // ── 4. Shoot requests (spawn projectiles) ─────────────────────────────
            while (_shootQueue.TryDequeue(out var entry))
            {
                _intentGuard.ReleaseActionSlot(entry.Peer, IntentKind.Shoot);
                if (!_peerMap.TryGetValue(entry.Peer, out PlayerSession? shooter)) continue;
                if (!shooter.IsSpellAllowed(entry.Packet.SpellId)) continue;
                if (!SpellDatabase.TryGet(entry.Packet.SpellId, out SpellDefinition spell)) continue;
                if (spell.TargetType != SpellTargetType.Projectile) continue;
                if (!shooter.IsAlive) continue;
                if (shooter.IsOnCooldown(spell.SpellId, _tick, spell.CooldownTicks)) continue;

                ProjectileState? proj = ProjectileSystem.SpawnProjectile(
                    shooter, entry.Packet, spell, _nextProjectileId++);

                if (proj == null) continue;

                _projectiles.Add(proj);
                shooter.SetCooldown(spell.SpellId, _tick);

                _network?.SendToAll(new ProjectileSpawnPacket
                {
                    ProjectileId = proj.ProjectileId,
                    OwnerId      = proj.OwnerId,
                    SpellId      = proj.SpellId,
                    StartX       = proj.Position.X,
                    StartY       = proj.Position.Y,
                    DirectionX   = proj.DirectionX,
                    DirectionY   = proj.DirectionY,
                    Speed        = proj.Speed,
                    MaxRange     = proj.MaxRange,
                }, DeliveryMethod.ReliableOrdered);
            }

            // ── 5. Tick active projectiles (move + collision) ─────────────────────
            if (_projectiles.Count > 0)
            {
                ProjectileSystem.TickResult result =
                    ProjectileSystem.Tick(_projectiles, _players, DeltaTime);

                // Pierce hits — damage lands but projectile keeps flying (no destroy packet)
                if (result.PierceHits != null)
                {
                    foreach (CombatEventPacket ev in result.PierceHits)
                        BroadcastCombatEvent(ev);
                }

                if (result.StatusEffects != null)
                    BroadcastStatusEffects(result.StatusEffects);

                // Splash hits from explosive detonations — extra targets hit by AoE on impact
                if (result.SplashHits != null)
                {
                    foreach (CombatEventPacket ev in result.SplashHits)
                        BroadcastCombatEvent(ev);
                }

                // Final hits — projectile consumed after landing
                if (result.Hits != null)
                {
                    foreach (var (projId, ev) in result.Hits)
                    {
                        BroadcastCombatEvent(ev);
                        _network?.SendToAll(new ProjectileDestroyPacket
                        {
                            ProjectileId = projId,
                            HitSomething = true,
                        }, DeliveryMethod.ReliableOrdered);
                    }
                }

                if (result.ExpiredIds != null)
                {
                    foreach (int projId in result.ExpiredIds)
                    {
                        _network?.SendToAll(new ProjectileDestroyPacket
                        {
                            ProjectileId = projId,
                            HitSomething = false,
                        }, DeliveryMethod.ReliableOrdered);
                    }
                }
            }

            _statusTickEvents.Clear();
            _expiredStatusEffects.Clear();
            for (int i = 0; i < _players.Count; i++)
                _players[i].TickStatusEffects(_players, _statusTickEvents, _expiredStatusEffects);

            if (_statusTickEvents.Count > 0)
            {
                for (int i = 0; i < _statusTickEvents.Count; i++)
                    BroadcastCombatEvent(_statusTickEvents[i]);

                _statusTickEvents.Clear();
            }

            if (_expiredStatusEffects.Count > 0)
            {
                for (int i = 0; i < _expiredStatusEffects.Count; i++)
                    BroadcastStatusEffectRemoval(_expiredStatusEffects[i]);

                _expiredStatusEffects.Clear();
            }
        }

        // ── Broadcast ─────────────────────────────────────────────────────────

        private void BroadcastState()
        {
            if (_network == null) return;

            // Positions are public. Health is sent separately only to allied recipients.
            // TODO: only send to peers within a relevant area at high player counts.
            for (int viewerIndex = 0; viewerIndex < _players.Count; viewerIndex++)
            {
                PlayerSession viewer = _players[viewerIndex];

                for (int entityIndex = 0; entityIndex < _players.Count; entityIndex++)
                {
                    PlayerSession entity = _players[entityIndex];

                    _network.SendTo(viewer.Peer!, new EntityPositionPacket
                    {
                        EntityId         = entity.EntityId,
                        X                = entity.Position.X,
                        Y                = entity.Position.Y,
                        ServerTick       = _tick,
                        AcknowledgedTick = entity.LastProcessedClientTick,
                    }, DeliveryMethod.Unreliable);

                    if (entity.Faction == viewer.Faction)
                    {
                        _network.SendTo(viewer.Peer!, new EntityHealthPacket
                        {
                            EntityId = entity.EntityId,
                            Health   = entity.Health,
                        }, DeliveryMethod.Unreliable);
                    }
                }
            }
        }

        private void BroadcastCombatEvent(CombatEventPacket ev)
            => _network?.SendToAll(ev, DeliveryMethod.ReliableOrdered);

        private void BroadcastStatusEffects(IReadOnlyList<StatusEffectAppliedPacket> statusEffects)
        {
            for (int i = 0; i < statusEffects.Count; i++)
                BroadcastStatusEffect(statusEffects[i]);
        }

        private void BroadcastStatusEffect(StatusEffectAppliedPacket packet)
        {
            PlayerSession? target = FindById(packet.TargetEntityId);
            if (target == null || _network == null)
                return;

            if (packet.Visibility == StatusEffectVisibility.Everyone)
            {
                _network.SendToAll(packet, DeliveryMethod.ReliableOrdered);
                return;
            }

            for (int i = 0; i < _players.Count; i++)
            {
                PlayerSession viewer = _players[i];
                if (viewer.Faction != target.Faction)
                    continue;

                _network.SendTo(viewer.Peer!, packet, DeliveryMethod.ReliableOrdered);
            }
        }

        private void BroadcastStatusEffectRemoval(StatusEffectRemovedPacket packet)
        {
            PlayerSession? target = FindById(packet.TargetEntityId);
            if (target == null || _network == null)
                return;

            if (packet.Visibility == StatusEffectVisibility.Everyone)
            {
                _network.SendToAll(packet, DeliveryMethod.ReliableOrdered);
                return;
            }

            for (int i = 0; i < _players.Count; i++)
            {
                PlayerSession viewer = _players[i];
                if (viewer.Faction != target.Faction)
                    continue;

                _network.SendTo(viewer.Peer!, packet, DeliveryMethod.ReliableOrdered);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private PlayerSession? FindById(int entityId)
            => _entityMap.TryGetValue(entityId, out PlayerSession? s) ? s : null;
    }
}
