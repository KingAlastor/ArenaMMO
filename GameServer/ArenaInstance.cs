using GameServer.Systems;
using LiteNetLib;
using SharedLibrary;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
        private const int   TickRate  = 30;
        private const float DeltaTime = 1f / TickRate;
        private const int   MsPerTick = 1000 / TickRate;

        // ── Player State ──────────────────────────────────────────────────────

        private readonly List<PlayerSession>                _players = new List<PlayerSession>();
        private readonly Dictionary<NetPeer, PlayerSession> _peerMap = new Dictionary<NetPeer, PlayerSession>();
        private int _nextEntityId = 1;

        // ── Input Queues ──────────────────────────────────────────────────────
        // Filled by network callbacks each tick; drained by ProcessTick().

        private readonly ConcurrentQueue<(NetPeer Peer, PlayerInputPacket      Packet)> _inputQueue  = new();
        private readonly ConcurrentQueue<(NetPeer Peer, AttackRequestPacket    Packet)> _attackQueue = new();
        private readonly ConcurrentQueue<(NetPeer Peer, SpellCastRequestPacket Packet)> _spellQueue  = new();
        private readonly ConcurrentQueue<(NetPeer Peer, ShootRequestPacket     Packet)> _shootQueue  = new();

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

        /// <summary>Starts the network listener and blocks on the game loop until shutdown.</summary>
        public void Start(int port)
        {
            _network   = new NetworkManager(this, port);
            _isRunning = true;
            Console.WriteLine($"[Arena] Instance running at {TickRate} Hz");
            RunGameLoop();
        }

        // ── Connection Lifecycle ──────────────────────────────────────────────

        public void OnPlayerConnected(NetPeer peer)
        {
            var session = new PlayerSession
            {
                EntityId   = _nextEntityId++,
                PlayerName = $"Player_{peer.Id}",
                Peer       = peer,
                Faction    = (_players.Count & 1) == 0 ? FactionId.Alpha : FactionId.Beta,
                Position   = Vec2.Zero,
                Health     = 100f,
                MaxHealth  = 100f,
            };

            _players.Add(session);
            _peerMap[peer] = session;
            Console.WriteLine($"[Arena] Spawned {session.PlayerName} (id={session.EntityId})");
        }

        public void OnPlayerDisconnected(NetPeer peer)
        {
            if (_peerMap.TryGetValue(peer, out PlayerSession? session))
            {
                _players.Remove(session);
                _peerMap.Remove(peer);
                Console.WriteLine($"[Arena] Removed {session.PlayerName} (id={session.EntityId})");
            }
        }

        // ── Queue Entry Points (called from network callbacks) ────────────────

        public void EnqueueInput(NetPeer peer, PlayerInputPacket packet)
            => _inputQueue.Enqueue((peer, packet));

        public void EnqueueAttack(NetPeer peer, AttackRequestPacket packet)
            => _attackQueue.Enqueue((peer, packet));

        public void EnqueueSpellCast(NetPeer peer, SpellCastRequestPacket packet)
            => _spellQueue.Enqueue((peer, packet));

        public void EnqueueShoot(NetPeer peer, ShootRequestPacket packet)
            => _shootQueue.Enqueue((peer, packet));

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

                _tick++;

                int sleep = MsPerTick - (int)sw.ElapsedMilliseconds;
                if (sleep > 0) Thread.Sleep(sleep);
            }
        }

        private void ProcessTick()
        {
            // ── 1. Movement ───────────────────────────────────────────────────
            while (_inputQueue.TryDequeue(out var entry))
            {
                if (_peerMap.TryGetValue(entry.Peer, out PlayerSession? player))
                    MovementSystem.ProcessInput(player, entry.Packet, DeltaTime);
            }

            // ── 2. Melee auto-attacks ─────────────────────────────────────────
            while (_attackQueue.TryDequeue(out var entry))
            {
                if (!_peerMap.TryGetValue(entry.Peer, out PlayerSession? attacker)) continue;

                PlayerSession? target = FindById(entry.Packet.TargetEntityId);
                if (target == null) continue;

                var statusEffects = new List<StatusEffectAppliedPacket>();
                CombatEventPacket? ev = CombatSystem.ProcessMeleeAttack(attacker, target, _tick, statusEffects);
                if (ev != null) BroadcastCombatEvent(ev);
                BroadcastStatusEffects(statusEffects);
            }

            // ── 3. Spell casts ────────────────────────────────────────────────
            while (_spellQueue.TryDequeue(out var entry))
            {
                if (!_peerMap.TryGetValue(entry.Peer, out PlayerSession? caster)) continue;
                if (!SpellDatabase.TryGet(entry.Packet.SpellId, out SpellDefinition spell)) continue;

                var statusEffects = new List<StatusEffectAppliedPacket>();
                List<CombatEventPacket> events =
                    CombatSystem.ProcessSpellCast(caster, entry.Packet, spell, _players, _tick, statusEffects);

                foreach (CombatEventPacket ev in events)
                    BroadcastCombatEvent(ev);

                BroadcastStatusEffects(statusEffects);
            }

            // ── 4. Shoot requests (spawn projectiles) ─────────────────────────────
            while (_shootQueue.TryDequeue(out var entry))
            {
                if (!_peerMap.TryGetValue(entry.Peer, out PlayerSession? shooter)) continue;
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
                        EntityId = entity.EntityId,
                        X        = entity.Position.X,
                        Y        = entity.Position.Y,
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
        {
            for (int i = 0; i < _players.Count; i++)
                if (_players[i].EntityId == entityId) return _players[i];
            return null;
        }
    }
}
