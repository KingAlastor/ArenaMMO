using GameServer.DataLayer;
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

        // ── Zone descriptor ───────────────────────────────────────────────────
        // All zone topology and rules (bounds, view radius, spawn points, win condition, etc.)
        // are injected via ZoneDescriptor so the same ArenaInstance code can host an Arena
        // match or an open-world MMO zone without branching on a hard-coded mode flag.
        private readonly ZoneDescriptor _zone;
        // Pre-computed from _zone.ViewRadius to avoid the multiply on every BroadcastState tick.
        private readonly float _viewRadiusSqr;

        // ── Player State ──────────────────────────────────────────────────────

        private readonly List<PlayerSession>                _players   = new List<PlayerSession>();
        private readonly Dictionary<NetPeer, PlayerSession> _peerMap   = new Dictionary<NetPeer, PlayerSession>();
        private readonly Dictionary<int, PlayerSession>     _entityMap = new Dictionary<int, PlayerSession>();
        private int _nextEntityId = 1;

        // Reusable per-tick lists — allocated once, cleared before each use to avoid GC churn.
        private readonly List<StatusEffectAppliedPacket> _reusableStatusEffects = new List<StatusEffectAppliedPacket>();
        private readonly List<CombatEventPacket>          _reusableSpellEvents   = new List<CombatEventPacket>();
        private readonly List<AoEHitEventPacket>          _reusableAoEHitEvents  = new List<AoEHitEventPacket>();

        // ── Input Queues ──────────────────────────────────────────────────────
        // Filled by network callbacks each tick; drained by ProcessTick().

        private readonly ConcurrentDictionary<NetPeer, PlayerInputPacket> _latestInputByPeer = new();
        private readonly ConcurrentQueue<(NetPeer Peer, AttackRequestPacket    Packet)> _attackQueue    = new();
        private readonly ConcurrentQueue<(NetPeer Peer, SpellCastRequestPacket Packet)> _spellQueue     = new();
        private readonly ConcurrentQueue<(NetPeer Peer, ShootRequestPacket     Packet)> _shootQueue     = new();
        // Latest-wins: only the most recent swap request per peer is processed each tick,
        // matching the movement input model to avoid per-peer allocation churn.
        private readonly ConcurrentDictionary<NetPeer, GearSetSwapRequestPacket> _latestGearSwapByPeer = new();
        // Individual item equip/unequip requests — queued so multiple fast requests in one
        // tick are not silently dropped (player may swap several slots quickly).
        private readonly ConcurrentQueue<(NetPeer Peer, EquipItemRequestPacket Packet)> _equipItemQueue = new();
        // Ground item pickup requests — queued for phase-ordered resolution after movement.
        private readonly ConcurrentQueue<(NetPeer Peer, GroundItemPickupRequestPacket Packet)> _pickupQueue = new();
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
        private bool _matchEnded = false;

        // ── Grace-period reconnect (Dota2-style rejoin) ───────────────────────
        // When a player drops, their session is kept alive as a stationary ghost for
        // RejoinGraceTicks ticks (default 5 min at 30 Hz = 9000 ticks). If they reconnect
        // within the window their peer is reattached and they resume normally.
        // If the window expires their entity is fully despawned.
        private readonly Dictionary<int, (PlayerSession Session, int ExpiryTick)> _gracePeriodSessions = new();
        private readonly List<int> _expiredGraceAccountIds = new();

        // ── Ground items ──────────────────────────────────────────────────────
        private readonly Dictionary<int, GroundItem> _groundItems = new();
        private int _nextGroundItemId = 1;

        private readonly MatchDataService? _dataService;

        // Max individual equip requests drained per tick — prevents a burst of requests from
        // hogging tick time; each request is O(inventory size) which is bounded but non-zero.
        private const int MaxEquipDrainPerTick = 7;

        // ── Entry Point ───────────────────────────────────────────────────────

        /// <summary>
        /// Creates one arena runtime with a ticket validator bound to the configured signing secret.
        /// The secret is never rotated in-process; restart the server after secret changes.
        /// <paramref name="restrictEquipToRespawnWindow"/> restricts all gear changes to the
        /// respawn window when true (default/arena). Pass false for MMO instances.
        /// </summary>
        /// <summary>
        /// Creates one arena/zone runtime bound to the provided zone descriptor.
        /// The zone descriptor is the single source of truth for map topology, rules, and
        /// routing policy — pass a default <see cref="ZoneDescriptor"/> for Arena mode.
        /// </summary>
        public ArenaInstance(string ticketSecret, ZoneDescriptor zone, MatchDataService? dataService = null)
        {
            _zone             = zone;
            _viewRadiusSqr    = zone.ViewRadius * zone.ViewRadius;
            _ticketValidator  = new AuthTicketValidator(ticketSecret);
            _dataService      = dataService;
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
            // ── Rejoin path (grace-period reconnect) ───────────────────────────────
            // If the AccountId is already in the grace-period set, skip nonce replay
            // (the nonce was consumed on first connect) but still verify the HMAC and
            // clock window to prevent ticket forgery.  The grace-period membership check
            // is the server-side proof that the nonce was legitimately used before.
            if (_gracePeriodSessions.TryGetValue(ticket.PlayerId, out var grace) &&
                grace.ExpiryTick > _tick)
            {
                if (_ticketValidator.TryValidateForRejoin(ticket, out AuthenticatedPeerContext ctx, out string rejoinError))
                {
                    OnPlayerRejoined(peer, grace.Session, ctx);
                    return true;
                }
                SecurityTelemetry.RecordInvalidTicket(rejoinError, ip);
                return false;
            }

            // ── Normal first-time connect path ─────────────────────────────────────
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
                Position   = _zone.GetSpawnPoint(context.Faction),
            };
            session.ReplaceAllowedSpells(context.AllowedSpellIds);

            PlayerProfile? profile = _dataService?.LoadPlayerProfile(context.PlayerId);
            if (profile != null)
                session.HydrateFromProfile(profile);
            else
                session.Health = session.MaxHealth;

            _players.Add(session);
            _peerMap[peer]               = session;
            _entityMap[session.EntityId] = session;
            _intentGuard.OnPeerConnected(peer);

            _network?.SendToAll(new EntitySpawnPacket
            {
                EntityId   = session.EntityId,
                PlayerName = session.PlayerName,
                Faction    = (byte)session.Faction,
                X          = session.Position.X,
                Y          = session.Position.Y,
            }, DeliveryMethod.ReliableOrdered);

            for (int i = 0; i < _players.Count - 1; i++)
            {
                PlayerSession existing = _players[i];
                _network?.SendTo(peer, new EntitySpawnPacket
                {
                    EntityId   = existing.EntityId,
                    PlayerName = existing.PlayerName,
                    Faction    = (byte)existing.Faction,
                    X          = existing.Position.X,
                    Y          = existing.Position.Y,
                }, DeliveryMethod.ReliableOrdered);
            }

            Console.WriteLine($"[Arena] Authenticated {session.PlayerName} (account={session.AccountId}, entity={session.EntityId})");
        }

        /// <summary>
        /// Reattaches a reconnecting peer to their existing session that was preserved in the
        /// grace-period set.  The session was kept alive as a stationary ghost; this call
        /// restores their live peer reference and resumes normal simulation.
        /// </summary>
        private void OnPlayerRejoined(NetPeer peer, PlayerSession session, AuthenticatedPeerContext context)
        {
            session.Peer = peer;
            _peerMap[peer] = session;
            _gracePeriodSessions.Remove(session.AccountId);
            _intentGuard.OnPeerConnected(peer);

            // Send the rejoining player a full state-sync so their client can restore all
            // existing entities, ground items, and their own current health/stats.
            for (int i = 0; i < _players.Count; i++)
            {
                PlayerSession existing = _players[i];
                _network?.SendTo(peer, new EntitySpawnPacket
                {
                    EntityId   = existing.EntityId,
                    PlayerName = existing.PlayerName,
                    Faction    = (byte)existing.Faction,
                    X          = existing.Position.X,
                    Y          = existing.Position.Y,
                }, DeliveryMethod.ReliableOrdered);
            }

            // Notify all other players that this entity has reconnected.
            _network?.SendToAll(new PlayerReconnectedPacket
            {
                EntityId = session.EntityId,
            }, DeliveryMethod.ReliableOrdered);

            Console.WriteLine($"[Arena] {session.PlayerName} rejoined (account={session.AccountId}, entity={session.EntityId})");
        }

        public void OnPlayerDisconnected(NetPeer peer)
        {
            if (!_peerMap.TryGetValue(peer, out PlayerSession? session))
                return;

            _peerMap.Remove(peer);
            _latestInputByPeer.TryRemove(peer, out _);
            _intentGuard.OnPeerDisconnected(peer);

            // Keep the session alive as a stationary ghost for RejoinGraceTicks ticks.
            // The peer reference is cleared so BroadcastState and SendToInterested skip
            // this ghost without crashing.  The session remains in _players and _entityMap
            // so it still participates in combat (other players can attack it).
            session.Peer = null;
            _gracePeriodSessions[session.AccountId] = (session, _tick + _zone.RejoinGraceTicks);

            _network?.SendToAll(new PlayerGraceDisconnectPacket
            {
                EntityId = session.EntityId,
            }, DeliveryMethod.ReliableOrdered);

            Console.WriteLine($"[Arena] {session.PlayerName} disconnected — grace period active ({_zone.RejoinGraceTicks} ticks)");
        }

        /// <summary>
        /// O(1) swap-remove: replaces the slot with the last element, then shrinks the list by one.
        /// Avoids the O(N) element-shift cost of <see cref="List{T}.Remove"/> for large rosters.
        /// </summary>
        private void RemovePlayerFromList(PlayerSession session)
        {
            int idx  = _players.IndexOf(session);
            if (idx < 0) return;

            int last = _players.Count - 1;
            if (idx != last)
                _players[idx] = _players[last];
            _players.RemoveAt(last);
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

        public void EnqueueGearSetSwap(NetPeer peer, GearSetSwapRequestPacket packet)
        {
            if (!InputSanitizer.IsValid(packet))
            {
                SecurityTelemetry.RecordInvalidPacket("invalid-gearswap-packet", peer);
                return;
            }

            // Latest-wins: overwrite any earlier pending request for this peer.
            // A player can only have 2 gear sets so the valid state space is bounded.
            _latestGearSwapByPeer[peer] = packet;
        }

        public void EnqueueItemPickup(NetPeer peer, GroundItemPickupRequestPacket packet)
        {
            if (packet.GroundItemId <= 0) return;
            _pickupQueue.Enqueue((peer, packet));
        }

        public void EnqueueEquipItem(NetPeer peer, EquipItemRequestPacket packet)
        {
            if (!InputSanitizer.IsValid(packet))
            {
                SecurityTelemetry.RecordInvalidPacket("invalid-equipitem-packet", peer);
                return;
            }

            _equipItemQueue.Enqueue((peer, packet));
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

                _network!.PollEvents();
                ProcessTick();
                BroadcastState();

                // Heartbeat: flush all player states to Redis every 60 s for crash recovery
                // and zone-handoff readiness.  Fire-and-forget — does not block the tick loop.
                if ((_tick % (TickRate * 60)) == 0 && _tick > 0)
                    FlushAllPlayerStates();

                // Grace-period eviction: remove sessions whose reconnect window has expired
                // (checked every second to avoid O(N) iteration every tick at high CCU).
                if ((_tick % TickRate) == 0)
                    EvictExpiredGracePeriods();

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
                    MovementSystem.ProcessInput(player, latestInput, DeltaTime, _zone.Bounds);
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
                _reusableAoEHitEvents.Clear();
                CombatSystem.ProcessSpellCast(
                    caster, entry.Packet, spell, _players, _entityMap, _tick,
                    _reusableSpellEvents, _reusableAoEHitEvents, _reusableStatusEffects);
                for (int evIdx = 0; evIdx < _reusableSpellEvents.Count; evIdx++)
                    BroadcastCombatEvent(_reusableSpellEvents[evIdx]);
                for (int evIdx = 0; evIdx < _reusableAoEHitEvents.Count; evIdx++)
                    BroadcastAoEHitEvent(_reusableAoEHitEvents[evIdx]);

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

                _network?.SendToInterested(new ProjectileSpawnPacket
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
                }, DeliveryMethod.ReliableOrdered, proj.Position, _zone.EventFilter, _players);
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
                        Vec2 hitOrigin = FindById(ev.TargetId)?.Position ?? Vec2.Zero;
                        _network?.SendToInterested(new ProjectileDestroyPacket
                        {
                            ProjectileId = projId,
                            HitSomething = true,
                        }, DeliveryMethod.ReliableOrdered, hitOrigin, _zone.EventFilter, _players);
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
            {
                bool statsDirty = _players[i].TickStatusEffects(_entityMap, _statusTickEvents, _expiredStatusEffects);
                // If a stat-modifying effect expired, send the updated stats to the player's peer.
                if (statsDirty && _players[i].Peer != null)
                    _network?.SendTo(_players[i].Peer!, _players[i].BuildStatsPacket(), DeliveryMethod.ReliableOrdered);
            }

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

            // ── Phase 8: Death detection ──────────────────────────────────────────
            for (int i = 0; i < _players.Count; i++)
            {
                PlayerSession p = _players[i];
                if (p.Health <= 0f && !p.IsRespawning)
                {
                    p.DeathCount++;
                    p.StartRespawn();

                    if (p.LastKillerEntityId != 0 &&
                        _entityMap.TryGetValue(p.LastKillerEntityId, out PlayerSession? killer))
                    {
                        killer.KillCount++;
                    }

                    Vec2 deathPos = p.Position;
                    _network?.SendToInterested(new PlayerDeathPacket
                    {
                        KilledEntityId = p.EntityId,
                        KillerEntityId = p.LastKillerEntityId,
                    }, DeliveryMethod.ReliableOrdered, deathPos, _zone.EventFilter, _players);
                }
            }

            // ── Phase 9: Respawn countdown ────────────────────────────────────────
            for (int i = 0; i < _players.Count; i++)
            {
                PlayerSession p = _players[i];
                Vec2 spawnPoint = _zone.GetSpawnPoint(p.Faction);
                if (p.TickRespawn(spawnPoint))
                {
                    _network?.SendToInterested(new PlayerRespawnPacket
                    {
                        EntityId = p.EntityId,
                        X        = p.Position.X,
                        Y        = p.Position.Y,
                        Health   = p.Health,
                    }, DeliveryMethod.ReliableOrdered, spawnPoint, _zone.EventFilter, _players);
                }
            }

            // ── Phase 9b: Equip / gear-set swap ──────────────────────────────────
            // Gear changes are always permitted — the old respawn-window gate has been removed.
            // isPermitted was replaced by ownership-only validation inside TryEquipItem/
            // TryUnequipSlot/TryApplyGearSet, which check that the item is actually in the
            // player's inventory before accepting the request.

            // --- Part A: individual item equips/unequips ---
            int equipDrained = 0;
            while (equipDrained < MaxEquipDrainPerTick && _equipItemQueue.TryDequeue(out (NetPeer Peer, EquipItemRequestPacket Packet) eq))
            {
                equipDrained++;
                if (!_peerMap.TryGetValue(eq.Peer, out PlayerSession? eqSession)) continue;

                bool success = eq.Packet.ItemInstanceId == 0
                    ? eqSession.TryUnequipSlot(eq.Packet.Slot, out PlayerStatsRefreshedPacket eqPkt)
                    : eqSession.TryEquipItem(eq.Packet.ItemInstanceId, out eqPkt);

                if (success && eqSession.Peer != null)
                    _network?.SendTo(eqSession.Peer, eqPkt, DeliveryMethod.ReliableOrdered);
            }

            // --- Part B: preset gear-set quickswaps (latest-wins) ---
            foreach (KeyValuePair<NetPeer, GearSetSwapRequestPacket> entry in _latestGearSwapByPeer)
            {
                if (!_latestGearSwapByPeer.TryRemove(entry.Key, out GearSetSwapRequestPacket? swapReq))
                    continue;

                if (!_peerMap.TryGetValue(entry.Key, out PlayerSession? swapSession))
                    continue;

                if (swapSession.TryApplyGearSet(swapReq.SetIndex, out PlayerStatsRefreshedPacket refreshPkt))
                {
                    if (swapSession.Peer != null)
                        _network?.SendTo(swapSession.Peer, refreshPkt, DeliveryMethod.ReliableOrdered);
                    Console.WriteLine($"[Arena] {swapSession.PlayerName} swapped to gear set {swapReq.SetIndex}");
                }
            }

            // ── Phase 9c: Ground item pickups ─────────────────────────────────────
            while (_pickupQueue.TryDequeue(out (NetPeer Peer, GroundItemPickupRequestPacket Packet) pickup))
            {
                if (!_peerMap.TryGetValue(pickup.Peer, out PlayerSession? picker)) continue;
                if (!_groundItems.TryGetValue(pickup.Packet.GroundItemId, out GroundItem? groundItem)) continue;

                // Server-side distance check: player must be within 2 units of the ground item.
                if (CombatMath.DistanceSqr(picker.Position, groundItem.Position) > 4f) continue;

                // Ownership enforcement: inventory size is capped server-side, not client-side.
                if (!picker.PickupItem(groundItem.Item, _zone.MaxInventorySize)) continue;

                _groundItems.Remove(pickup.Packet.GroundItemId);

                // Tell everyone nearby the item is gone.
                _network?.SendToInterested(new GroundItemRemovedPacket
                {
                    GroundItemId = groundItem.GroundItemId,
                }, DeliveryMethod.ReliableOrdered, groundItem.Position, _zone.EventFilter, _players);

                // Confirm to the picking player that the item was added to their inventory.
                if (picker.Peer != null)
                    _network?.SendTo(picker.Peer, new ItemAddedToInventoryPacket
                    {
                        DefinitionId = groundItem.Item.DefinitionId,
                        InstanceId   = groundItem.Item.InstanceId,
                    }, DeliveryMethod.ReliableOrdered);
            }

            // ── Phase 10: Win-condition check ─────────────────────────────────────
            if (!_matchEnded)
                CheckWinCondition();
        }

        // ── Broadcast ─────────────────────────────────────────────────────────

        private void BroadcastState()
        {
            if (_network == null) return;

            for (int viewerIndex = 0; viewerIndex < _players.Count; viewerIndex++)
            {
                PlayerSession viewer = _players[viewerIndex];
                // Skip ghost sessions whose peer was cleared on disconnect.
                // They have no live socket to receive state updates.
                if (viewer.Peer == null) continue;

                for (int entityIndex = 0; entityIndex < _players.Count; entityIndex++)
                {
                    PlayerSession entity = _players[entityIndex];

                    if (entity.EntityId != viewer.EntityId &&
                        CombatMath.DistanceSqr(viewer.Position, entity.Position) > _viewRadiusSqr)
                        continue;

                    _network.SendTo(viewer.Peer, new EntityPositionPacket
                    {
                        EntityId         = entity.EntityId,
                        X                = entity.Position.X,
                        Y                = entity.Position.Y,
                        ServerTick       = _tick,
                        AcknowledgedTick = entity.LastProcessedClientTick,
                    }, DeliveryMethod.Unreliable);

                    if (entity.Faction == viewer.Faction)
                    {
                        _network.SendTo(viewer.Peer, new EntityHealthPacket
                        {
                            EntityId = entity.EntityId,
                            Health   = entity.Health,
                        }, DeliveryMethod.Unreliable);
                    }
                }
            }
        }

        private void BroadcastCombatEvent(CombatEventPacket ev)
        {
            // Route through interest filter so only nearby players receive this event.
            // In Arena mode EventFilter is BroadcastFilter (all players); in open-world zones
            // it is a RadiusFilter so distant players are not spammed with irrelevant events.
            Vec2 origin = FindById(ev.TargetId)?.Position ?? Vec2.Zero;
            _network?.SendToInterested(ev, DeliveryMethod.ReliableOrdered, origin, _zone.EventFilter, _players);
        }

        private void BroadcastAoEHitEvent(AoEHitEventPacket ev)
        {
            Vec2 origin = FindById(ev.HitEntityId)?.Position ?? Vec2.Zero;
            _network?.SendToInterested(ev, DeliveryMethod.ReliableOrdered, origin, _zone.EventFilter, _players);
        }

        // ── Win Condition ─────────────────────────────────────────────────────

        /// <summary>
        /// Checks whether all living players belong to a single faction, meaning the opposing
        /// faction has been fully eliminated.
        /// TODO: Replace elimination check with objective-based win logic when map data is implemented.
        /// </summary>
        private void CheckWinCondition()
        {
            bool alphaAlive = false;
            bool betaAlive  = false;

            for (int i = 0; i < _players.Count; i++)
            {
                if (!_players[i].IsAlive) continue;
                if (_players[i].Faction == FactionId.Alpha) alphaAlive = true;
                else                                          betaAlive  = true;
            }

            // Wait until there is at least one dead player on a side to avoid triggering on
            // an empty match or before anyone has died.
            if (alphaAlive && betaAlive) return;
            if (alphaAlive)  { EndMatch(FactionId.Alpha); return; }
            if (betaAlive)   { EndMatch(FactionId.Beta);  return; }
            // Everyone is dead \u2014 declare a draw by whichever faction had the last kill.
            // For now, default to Beta winning the draw; adjust when scoring is implemented.
            EndMatch(FactionId.Beta);
        }

        private void EndMatch(FactionId winner)
        {
            _matchEnded = true;
            _isRunning  = false;

            Console.WriteLine($"[Arena] Match ended. Winner: {winner}");

            _network?.SendToAll(new MatchEndPacket
            {
                WinnerFaction = (byte)winner,
            }, DeliveryMethod.ReliableOrdered);

            // Persist results asynchronously \u2014 fire-and-forget; the tick loop has already stopped.
            if (_dataService != null)
            {
                for (int i = 0; i < _players.Count; i++)
                {
                    PlayerSession p = _players[i];
                    _ = _dataService.SaveMatchResultAsync(new MatchResult
                    {
                        AccountId  = p.AccountId,
                        Won        = p.Faction == winner,
                        KillCount  = p.KillCount,
                        DeathCount = p.DeathCount,
                    });
                }
            }
        }

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
                _network.SendToInterested(packet, DeliveryMethod.ReliableOrdered,
                    target.Position, _zone.EventFilter, _players);
                return;
            }

            for (int i = 0; i < _players.Count; i++)
            {
                PlayerSession viewer = _players[i];
                // Skip ghost sessions and enemy players.
                if (viewer.Peer == null) continue;
                if (viewer.Faction != target.Faction) continue;
                if (!_zone.EventFilter.ShouldReceive(viewer, target.Position)) continue;
                _network.SendTo(viewer.Peer, packet, DeliveryMethod.ReliableOrdered);
            }
        }

        private void BroadcastStatusEffectRemoval(StatusEffectRemovedPacket packet)
        {
            PlayerSession? target = FindById(packet.TargetEntityId);
            if (target == null || _network == null)
                return;

            if (packet.Visibility == StatusEffectVisibility.Everyone)
            {
                _network.SendToInterested(packet, DeliveryMethod.ReliableOrdered,
                    target.Position, _zone.EventFilter, _players);
                return;
            }

            for (int i = 0; i < _players.Count; i++)
            {
                PlayerSession viewer = _players[i];
                if (viewer.Peer == null) continue;
                if (viewer.Faction != target.Faction) continue;
                if (!_zone.EventFilter.ShouldReceive(viewer, target.Position)) continue;
                _network.SendTo(viewer.Peer, packet, DeliveryMethod.ReliableOrdered);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private PlayerSession? FindById(int entityId)
            => _entityMap.TryGetValue(entityId, out PlayerSession? s) ? s : null;

        // ── Heartbeat / grace-period maintenance ──────────────────────────────

        /// <summary>
        /// Fire-and-forget: writes every active player's state to Redis so a crash loses at
        /// most 60 s of progress.  Does not block the game loop; awaited on a thread-pool thread.
        /// In Arena mode inventories are NOT included (pickups are match-scoped).
        /// In open-world zones inventories ARE included (items picked up must persist).
        /// </summary>
        private void FlushAllPlayerStates()
        {
            if (_dataService?.Sink == null) return;

            bool includeInventory = !_zone.IsArenaMode;
            for (int i = 0; i < _players.Count; i++)
                _ = _dataService.Sink.FlushAsync(_players[i].TakeSnapshot(includeInventory));
        }

        /// <summary>
        /// Removes players whose reconnect grace period has expired from the simulation.
        /// Called once per second (every TickRate ticks) to avoid O(N) work every tick.
        /// </summary>
        private void EvictExpiredGracePeriods()
        {
            if (_gracePeriodSessions.Count == 0) return;

            _expiredGraceAccountIds.Clear();
            foreach (KeyValuePair<int, (PlayerSession Session, int ExpiryTick)> kvp in _gracePeriodSessions)
            {
                if (kvp.Value.ExpiryTick <= _tick)
                    _expiredGraceAccountIds.Add(kvp.Key);
            }

            for (int i = 0; i < _expiredGraceAccountIds.Count; i++)
            {
                int accountId = _expiredGraceAccountIds[i];
                if (!_gracePeriodSessions.TryGetValue(accountId, out var grace)) continue;

                _gracePeriodSessions.Remove(accountId);
                _entityMap.Remove(grace.Session.EntityId);
                RemovePlayerFromList(grace.Session);

                // Notify clients that this entity is permanently gone.
                _network?.SendToAll(new EntityDespawnPacket
                {
                    EntityId = grace.Session.EntityId,
                }, DeliveryMethod.ReliableOrdered);

                Console.WriteLine($"[Arena] Grace period expired for {grace.Session.PlayerName} — entity despawned.");
            }
        }

        /// <summary>
        /// Computes crafting ingredient rewards earned by <paramref name="player"/> in this match.
        /// Rewards are the only durable progression output from an Arena match (in-session pickups
        /// are discarded by design so Arena balance is not affected by loot variance).
        /// </summary>
        private CraftingIngredientReward[] ComputeCraftingRewards(PlayerSession player, FactionId winner)
        {
            // Base participation reward: ingredient 1 (generic "arena shard"), quantity = 1.
            // Kill bonus: +1 per kill.  Win bonus: +2 extra shards.
            // TODO: Replace with a configurable reward table once designer tooling is available.
            int total = 1 + player.KillCount + (player.Faction == winner ? 2 : 0);
            if (total <= 0) return System.Array.Empty<CraftingIngredientReward>();
            return new[] { new CraftingIngredientReward { IngredientId = 1, Quantity = total } };
        }

        // ── Ground item helpers ───────────────────────────────────────────────

        /// <summary>
        /// Spawns a lootable item on the ground and notifies all interested players.
        /// Called by CombatSystem or future drop-table logic when an entity is slain.
        /// </summary>
        public void SpawnGroundItem(Vec2 position, ItemInstance item)
        {
            int id = _nextGroundItemId++;
            _groundItems[id] = new GroundItem { GroundItemId = id, Position = position, Item = item };
            _network?.SendToInterested(new GroundItemSpawnedPacket
            {
                GroundItemId = id,
                DefinitionId = item.DefinitionId,
                X            = position.X,
                Y            = position.Y,
            }, DeliveryMethod.ReliableOrdered, position, _zone.EventFilter, _players);
        }

        /// <summary>Lightweight container for a lootable item that has landed on the ground.</summary>
        private sealed class GroundItem
        {
            public int          GroundItemId;
            public Vec2         Position;
            public ItemInstance Item = null!;
        }
    }
}
