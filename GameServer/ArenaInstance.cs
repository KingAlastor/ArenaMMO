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
    // ── Value-type snapshot of one client's movement input ───────────────────
    // LiteNetLib's SubscribeReusable<T> allocates ONE T instance globally and
    // overwrites its fields for every inbound packet of that type.  Storing the
    // class reference in a Dictionary means every dictionary entry points at the
    // SAME object — the last peer to send input overwrites all earlier entries.
    //
    // Fix: copy the three relevant scalar fields into this plain struct immediately
    // inside EnqueueInput (on the callback thread, before PollEvents returns).
    // The dictionary then holds independent per-peer value copies, and
    // MovementSystem.ProcessInput works from those copies each tick.
    public struct PlayerInputData
    {
        public int   TickNumber;
        public sbyte InputX;
        public sbyte InputY;
    }

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
        // Initial capacity is set to a conservative upper bound for a 20-player AoE fight:
        //   20 targets × 3 effects each = 60 status effects/tick is the realistic burst ceiling.
        //   Sizing at 64 (next power of two) ensures the internal array never resizes in practice.
        private readonly List<StatusEffectAppliedPacket> _reusableStatusEffects = new List<StatusEffectAppliedPacket>(64);
        private readonly List<CombatEventPacket>          _reusableSpellEvents   = new List<CombatEventPacket>(32);
        private readonly List<AoEHitEventPacket>          _reusableAoEHitEvents  = new List<AoEHitEventPacket>(32);

        // ── Input Queues ──────────────────────────────────────────────────────
        // Filled by network callbacks each tick; drained by ProcessTick().

        // Latest-wins: only the most recent input per peer is processed each tick.
        // LiteNetLib callbacks all fire on the game-loop thread (via PollEvents), so a
        // plain Dictionary is sufficient here — no concurrent access occurs.
        // Stored as PlayerInputData (struct) — NOT as the PlayerInputPacket class reference.
        // See PlayerInputData comment above for why the class reference must NOT be stored.
        private readonly Dictionary<NetPeer, PlayerInputData> _latestInputByPeer = new();
        // ── Action queues — plain Queue<T>, NOT ConcurrentQueue<T> ───────────────────────
        // All LiteNetLib callbacks (Enqueue* methods) fire synchronously inside PollEvents(),
        // which is called from the game-loop thread.  ProcessTick() also runs on the game-loop
        // thread.  There is therefore zero concurrent access — ConcurrentQueue<T> would only
        // add interlocked/volatile overhead (an Interlocked.CompareExchange per TryDequeue)
        // with no correctness benefit.
        // If packet I/O is ever moved to a dedicated I/O thread, switch back to ConcurrentQueue
        // or add a spinlock and use a pre-sized ring buffer.
        private readonly Queue<(NetPeer Peer, AttackRequestPacket    Packet)> _attackQueue    = new(16);
        private readonly Queue<(NetPeer Peer, SpellCastRequestPacket Packet)> _spellQueue     = new(16);
        private readonly Queue<(NetPeer Peer, ShootRequestPacket     Packet)> _shootQueue     = new(16);
        // Latest-wins: same single-thread reasoning as above.
        private readonly Dictionary<NetPeer, GearSetSwapRequestPacket> _latestGearSwapByPeer = new();
        // Individual item equip/unequip requests — queued so multiple fast requests in one
        // tick are not silently dropped (player may swap several slots quickly).
        private readonly Queue<(NetPeer Peer, EquipItemRequestPacket Packet)> _equipItemQueue = new(16);
        // Ground item pickup requests — queued for phase-ordered resolution after movement.
        private readonly Queue<(NetPeer Peer, GroundItemPickupRequestPacket Packet)> _pickupQueue = new(16);
        // IntentGuard enforces anti-spam, tick skew, and replay rules before intents enter simulation.
        private readonly IntentGuard _intentGuard = new();
        // Ticket validator is the trust boundary between lobby-issued identity and live arena authority.
        private readonly AuthTicketValidator _ticketValidator;

        // ── Projectile State ───────────────────────────────────────────
        //
        // ProjectileState is a struct stored in a fixed-capacity array.  This eliminates
        // the heap allocation that occurred on every SpawnProjectile call when it was a class.
        // _projectileCount tracks the live-projectile window; slots beyond it are dead.
        // Mutations inside ProjectileSystem.Tick use ref locals for zero-copy in-place updates.
        // MaxProjectiles = 512 supports ~17 volleys of 30 arrows simultaneously — tune upward
        // for denser MMORPG fights; at ~100 B/struct the array occupies ≈ 50 KB on the heap.
        private const int MaxProjectiles = 512;
        private readonly ProjectileState[] _projectiles    = new ProjectileState[MaxProjectiles];
        private int                        _projectileCount = 0;
        private int                        _nextProjectileId = 1;

        // Sized for a 20-player fight where every player has 3 active DoT ticks firing simultaneously.
        private readonly List<CombatEventPacket>         _statusTickEvents     = new List<CombatEventPacket>(64);
        private readonly List<StatusEffectRemovedPacket> _expiredStatusEffects = new List<StatusEffectRemovedPacket>(32);

        // ── Reusable broadcast packet instances (zero-allocation BroadcastState) ──────
        // Pre-allocated once and mutated before every SendTo call.  All sends occur on the
        // single game-loop thread, so no synchronisation is required.
        private EntityPositionPacket       _posPacket          = new EntityPositionPacket       { PacketTypeId = PacketId.EntityPosition       };
        private EntityHealthPacket         _healthPacket       = new EntityHealthPacket         { PacketTypeId = PacketId.EntityHealth         };
        // Pre-allocated for zero-GC projectile lifecycle broadcasts inside ProcessTick.
        private ProjectileSpawnPacket      _projSpawnPacket    = new ProjectileSpawnPacket      { PacketTypeId = PacketId.ProjectileSpawn      };
        private ProjectileDestroyPacket    _projDestPacket     = new ProjectileDestroyPacket    { PacketTypeId = PacketId.ProjectileDestroy    };
        // Pre-allocated for zero-GC event broadcasts inside ProcessTick.
        // Mutation + in-ref pattern mirrors _posPacket/_projSpawnPacket above.
        private PlayerDeathPacket          _deathPacket        = new PlayerDeathPacket          { PacketTypeId = PacketId.PlayerDeath          };
        private PlayerRespawnPacket        _respawnPacket      = new PlayerRespawnPacket        { PacketTypeId = PacketId.PlayerRespawn        };
        private GroundItemRemovedPacket    _groundRemovedPacket  = new GroundItemRemovedPacket    { PacketTypeId = PacketId.GroundItemRemoved    };
        private GroundItemSpawnedPacket   _groundSpawnedPacket  = new GroundItemSpawnedPacket   { PacketTypeId = PacketId.GroundItemSpawned    };
        private ItemAddedToInventoryPacket _itemAddedPacket     = new ItemAddedToInventoryPacket { PacketTypeId = PacketId.ItemAddedToInventory };

        // ── Spatial grid ──────────────────────────────────────────────────────
        // Lazily created from zone bounds on the first ProcessTick call so the
        // ZoneDescriptor bounds are guaranteed to be populated.  The grid cell
        // size is set to the view radius so a 3×3 neighbourhood always covers the
        // full interest window without iterating all players.
        private SpatialGrid? _spatialGrid;

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

        // ── Deferred profile hydration ──────────────────────────────────────────
        // Redis I/O for player profiles must NEVER block the game-loop tick thread.
        // OnPlayerAuthenticated fires a background Task immediately and enqueues the
        // (session, task) pair here.  ProcessTick drains completed tasks each tick,
        // applying the loaded profile with zero blocking latency.
        // Players use base/default stats for the 1–10 ms it normally takes Redis to
        // respond — this is imperceptible and vastly preferable to stalling the tick.
        //
        // Plain Queue<T> is intentional: all access is on the single game-loop thread
        // (OnPlayerAuthenticated fires from PollEvents, FinalizeHydration from ProcessTick —
        // both on the same thread).  ConcurrentQueue<T> would add unnecessary
        // interlocked/volatile overhead with zero benefit here.
        private readonly Queue<(PlayerSession Session, System.Threading.Tasks.Task<DataLayer.PlayerProfile?> ProfileTask)>
            _pendingHydration = new();

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

            // Initialise the spatial grid eagerly before the loop starts so ProcessTick never
            // needs a null check.  ZoneDescriptor.Bounds must be set before calling Start().
            WorldBounds wb       = _zone.Bounds;
            float       cellSize = MathF.Max(_zone.ViewRadius, 16f);
            _spatialGrid = new SpatialGrid(wb.MinX, wb.MinY, wb.MaxX, wb.MaxY, cellSize);
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

            // ── Deferred Redis hydration (NEVER block the game-loop thread on I/O) ───────
            // Fire the async Redis read immediately so it starts in the background.
            // The session enters the match with base/default stats for the 1–10 ms it
            // normally takes Redis to respond — this is imperceptible to the player.
            // FinalizeHydration() drains completed tasks each tick with zero blocking.
            session.Health = session.MaxHealth;   // safe default until profile arrives
            if (_dataService != null)
            {
                System.Threading.Tasks.Task<DataLayer.PlayerProfile?> profileTask =
                    _dataService.LoadPlayerProfileAsync(context.PlayerId);
                _pendingHydration.Enqueue((session, profileTask));
            }

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
            _latestInputByPeer.Remove(peer);
            _latestGearSwapByPeer.Remove(peer);
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

            // Copy the three scalar fields into a value-type struct immediately.
            // DO NOT store 'packet' directly — it is the single LiteNetLib reusable instance
            // and its fields will be overwritten by the next inbound PlayerInputPacket from
            // any peer.  By the time ProcessTick runs, a stored reference would read stale data.
            _latestInputByPeer[peer] = new PlayerInputData
            {
                TickNumber = packet.TickNumber,
                InputX     = packet.InputX,
                InputY     = packet.InputY,
            };
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
            // ── Drift-free absolute-deadline heartbeat ────────────────────────────────
            // Rather than sleeping (MsPerTick - elapsed), we track the absolute Stopwatch
            // timestamp at which each tick SHOULD fire.  If a tick runs long, the next
            // deadline is still computed from the original baseline, so overruns self-correct
            // instead of compounding.  SpinWait is used for the final sub-millisecond window
            // to avoid the 15.6 ms OS timer-resolution floor of Thread.Sleep.
            //
            // NOTE: Use integer division (Frequency / TickRate) rather than
            //   (long)(Stopwatch.Frequency * DeltaTime) where DeltaTime is float.
            //   1f/30f is not exactly representable in IEEE 754 single precision;
            //   the float path introduces a consistent per-tick rounding bias that
            //   compounds into measurable phase drift over millions of ticks.
            //   Integer division is exact (same truncation, zero bias).
            long ticksPerTick = Stopwatch.Frequency / TickRate;
            long nextTickTime  = Stopwatch.GetTimestamp();

            while (_isRunning)
            {
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

                // Advance deadline by exactly one tick interval, regardless of how long this
                // tick took.  This keeps the heartbeat phase-locked to the original start time.
                nextTickTime += ticksPerTick;

                long remaining = nextTickTime - Stopwatch.GetTimestamp();
                if (remaining > 0)
                {
                    // Convert to milliseconds for the coarse Sleep, keeping 1 ms in reserve
                    // for the SpinWait to fill the sub-millisecond gap without over-sleeping.
                    long sleepMs = (remaining * 1000L / Stopwatch.Frequency) - 1;
                    if (sleepMs > 0)
                        Thread.Sleep((int)sleepMs);

                    // Spin the final sub-millisecond window with zero OS scheduler involvement.
                    while (Stopwatch.GetTimestamp() < nextTickTime)
                        Thread.SpinWait(8);
                }
                // If remaining <= 0 the tick ran over-budget; no sleep, fire next tick immediately.
                // The deadline still advances by ticksPerTick, so the loop self-corrects.
            }
        }

        private void ProcessTick()
        {
            // Tick order is intentionally fixed. Reordering phases can change gameplay semantics
            // (for example, movement-before-combat range checks and projectile-before-DoT timing).

            // ── 0. Deferred profile hydration ────────────────────────────────────────
            // Drain completed background Redis reads.  Zero blocking — we only process
            // tasks that are already finished.  Pending tasks are left in the queue.
            FinalizeHydration();

            // ── 1. Movement ───────────────────────────────────────────────────
            // _latestInputByPeer is a plain Dictionary — foreach uses its struct enumerator,
            // which is a value type and does NOT allocate on the heap.  Each value is a
            // PlayerInputData struct copy, independent of the LiteNetLib reusable packet object.
            foreach (KeyValuePair<NetPeer, PlayerInputData> entry in _latestInputByPeer)
            {
                if (_peerMap.TryGetValue(entry.Key, out PlayerSession? player))
                    MovementSystem.ProcessInput(player, entry.Value, DeltaTime, _zone.Bounds);
            }
            _latestInputByPeer.Clear();
            // Snapshot authoritative positions after movement for lag-compensation rewind.
            for (int i = 0; i < _players.Count; i++)
                _players[i].RecordPositionHistory(_tick);

            // Rebuild spatial grid once per tick after movement resolves.
            // _spatialGrid is guaranteed non-null here — initialised eagerly in Start() before
            // the game loop begins, so the null check that used to appear here is gone.
            _spatialGrid!.RebuildEachTick(_players);
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
                if (ev.HasValue) BroadcastCombatEvent(ev.Value);
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

                // Guard: silently drop the shot if the projectile pool is full rather than
                // growing unbounded.  At 512 slots this is a hard ceiling, not a normal path.
                if (_projectileCount >= MaxProjectiles) continue;

                // TrySpawnProjectile writes the new struct directly into the out parameter —
                // zero heap allocation (struct return, stored in the fixed array below).
                if (!ProjectileSystem.TrySpawnProjectile(
                        shooter, entry.Packet, spell, _nextProjectileId, out ProjectileState newProj))
                    continue;

                _projectiles[_projectileCount++] = newProj;
                _nextProjectileId++;
                shooter.SetCooldown(spell.SpellId, _tick);

                // Mutate the pre-allocated broadcast struct — zero heap allocation.
                // Direction and speed are compressed to halve wire size vs raw floats.
                _projSpawnPacket.ProjectileId = newProj.ProjectileId;
                _projSpawnPacket.OwnerId      = newProj.OwnerId;
                _projSpawnPacket.SpellId      = newProj.SpellId;
                _projSpawnPacket.StartX       = PacketEncoding.EncodePosition(newProj.Position.X);
                _projSpawnPacket.StartY       = PacketEncoding.EncodePosition(newProj.Position.Y);
                _projSpawnPacket.DirectionX   = PacketEncoding.EncodeDirection(newProj.DirectionX);
                _projSpawnPacket.DirectionY   = PacketEncoding.EncodeDirection(newProj.DirectionY);
                _projSpawnPacket.Speed        = PacketEncoding.EncodeSpeed(newProj.Speed);
                _projSpawnPacket.MaxRange     = PacketEncoding.EncodeSpeed(newProj.MaxRange);
                // Pass the spatial grid so only nearby clients receive the spawn packet.
                _network?.SendToInterested(in _projSpawnPacket, DeliveryMethod.ReliableOrdered,
                    newProj.Position, _zone.EventFilter, _players, _spatialGrid);
            }

            // ── 5. Tick active projectiles (move + collision) ─────────────────────
            if (_projectileCount > 0)
            {
                // Pass the spatial grid so ProjectileSystem narrows collision candidates
                // from O(N-all-players) to O(k-nearby) for each projectile — critical at
                // MMORPG scale where N can be 2 000 and projectile counts reach hundreds.
                ProjectileSystem.TickResult result =
                    ProjectileSystem.Tick(_projectiles, ref _projectileCount, _players, _entityMap, DeltaTime, _spatialGrid);

                // Pierce hits — damage lands but projectile keeps flying (no destroy packet).
                // Index-based for loops avoid List<T>.Enumerator overhead on the hot path.
                if (result.PierceHits != null)
                {
                    for (int pi = 0; pi < result.PierceHits.Count; pi++)
                        BroadcastCombatEvent(result.PierceHits[pi]);
                }

                if (result.StatusEffects != null)
                    BroadcastStatusEffects(result.StatusEffects);

                // Splash hits from explosive detonations — extra targets hit by AoE on impact
                if (result.SplashHits != null)
                {
                    for (int pi = 0; pi < result.SplashHits.Count; pi++)
                        BroadcastCombatEvent(result.SplashHits[pi]);
                }

                // Final hits — projectile consumed after landing
                if (result.Hits != null)
                {
                    for (int pi = 0; pi < result.Hits.Count; pi++)
                    {
                        (int projId, CombatEventPacket ev) = result.Hits[pi];
                        BroadcastCombatEvent(ev);
                        Vec2 hitOrigin = FindById(ev.TargetId)?.Position ?? Vec2.Zero;
                        // Mutate pre-allocated struct — zero heap allocation.
                        _projDestPacket.ProjectileId = projId;
                        _projDestPacket.HitSomething = true;
                        _network?.SendToInterested(in _projDestPacket, DeliveryMethod.ReliableOrdered, hitOrigin, _zone.EventFilter, _players, _spatialGrid);
                    }
                }

                if (result.ExpiredIds != null)
                {
                    for (int pi = 0; pi < result.ExpiredIds.Count; pi++)
                    {
                        // Mutate pre-allocated struct — zero heap allocation.
                        _projDestPacket.ProjectileId = result.ExpiredIds[pi];
                        _projDestPacket.HitSomething = false;
                        _network?.SendToAll(in _projDestPacket, DeliveryMethod.ReliableOrdered);
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

            // Broadcast DoT damage events accumulated during TickStatusEffects.
            for (int i = 0; i < _statusTickEvents.Count; i++)
                BroadcastCombatEvent(_statusTickEvents[i]);

            // Broadcast status-effect expiry notifications.
            for (int i = 0; i < _expiredStatusEffects.Count; i++)
            {
                // Local copy is stack-allocated (struct); `in` avoids a second copy on the call.
                StatusEffectRemovedPacket expired = _expiredStatusEffects[i];
                BroadcastStatusEffectRemoval(in expired);
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
                    // Mutate pre-allocated struct — zero heap allocation, zero copy (in-ref).
                    _deathPacket.KilledEntityId = p.EntityId;
                    _deathPacket.KillerEntityId = p.LastKillerEntityId;
                    _network?.SendToInterested(in _deathPacket, DeliveryMethod.ReliableOrdered, deathPos, _zone.EventFilter, _players, _spatialGrid);
                }
            }

            // ── Phase 9: Respawn countdown ────────────────────────────────────────
            for (int i = 0; i < _players.Count; i++)
            {
                PlayerSession p = _players[i];
                Vec2 spawnPoint = _zone.GetSpawnPoint(p.Faction);
                if (p.TickRespawn(spawnPoint))
                {
                    // Mutate pre-allocated struct — zero heap allocation, zero copy (in-ref).
                    _respawnPacket.EntityId = p.EntityId;
                    _respawnPacket.X        = PacketEncoding.EncodePosition(p.Position.X);
                    _respawnPacket.Y        = PacketEncoding.EncodePosition(p.Position.Y);
                    _respawnPacket.Health   = PacketEncoding.EncodeHealth(p.Health);
                    _network?.SendToInterested(in _respawnPacket, DeliveryMethod.ReliableOrdered, spawnPoint, _zone.EventFilter, _players, _spatialGrid);
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
            // Plain Dictionary foreach uses a struct enumerator — zero allocation.
            foreach (KeyValuePair<NetPeer, GearSetSwapRequestPacket> entry in _latestGearSwapByPeer)
            {
                if (!_peerMap.TryGetValue(entry.Key, out PlayerSession? swapSession))
                    continue;

                if (swapSession.TryApplyGearSet(entry.Value.SetIndex, out PlayerStatsRefreshedPacket refreshPkt))
                {
                    if (swapSession.Peer != null)
                        _network?.SendTo(swapSession.Peer, refreshPkt, DeliveryMethod.ReliableOrdered);
                    // NOTE: Console.WriteLine with string interpolation allocates — omit from hot path.
                    // Uncomment only during debug builds:
                    // Console.WriteLine($"[Arena] {swapSession.PlayerName} swapped to gear set {entry.Value.SetIndex}");
                }
            }
            _latestGearSwapByPeer.Clear();

            // ── Phase 9c: Ground item pickups ─────────────────────────────────────
            while (_pickupQueue.TryDequeue(out (NetPeer Peer, GroundItemPickupRequestPacket Packet) pickup))
            {
                if (!_peerMap.TryGetValue(pickup.Peer, out PlayerSession? picker)) continue;
                if (!_groundItems.TryGetValue(pickup.Packet.GroundItemId, out GroundItem groundItem)) continue;

                // Server-side distance check: player must be within 2 units of the ground item.
                if (CombatMath.DistanceSqr(picker.Position, groundItem.Position) > 4f) continue;

                // Ownership enforcement: inventory size is capped server-side, not client-side.
                if (!picker.PickupItem(groundItem.Item, _zone.MaxInventorySize)) continue;

                _groundItems.Remove(pickup.Packet.GroundItemId);

                // Mutate pre-allocated structs — zero heap allocation, zero copy (in-ref).
                // Tell everyone nearby the item is gone.
                _groundRemovedPacket.GroundItemId = groundItem.GroundItemId;
                _network?.SendToInterested(in _groundRemovedPacket, DeliveryMethod.ReliableOrdered, groundItem.Position, _zone.EventFilter, _players, _spatialGrid);

                // Confirm to the picking player that the item was added to their inventory.
                if (picker.Peer != null)
                {
                    _itemAddedPacket.DefinitionId = groundItem.Item.DefinitionId;
                    _itemAddedPacket.InstanceId   = groundItem.Item.InstanceId;
                    _network?.SendTo(picker.Peer, in _itemAddedPacket, DeliveryMethod.ReliableOrdered);
                }
            }

            // ── Phase 10: Win-condition check ─────────────────────────────────────
            if (!_matchEnded)
                CheckWinCondition();
        }

        // ── Broadcast ─────────────────────────────────────────────────────────

        private void BroadcastState()
        {
            if (_network == null) return;

            // ── Pre-encode tick fields once for the entire broadcast pass ─────────────
            // EncodeTick24 is called here rather than inside the inner loop so the same
            // three bytes are reused across all position packets this tick.
            PacketEncoding.EncodeTick24(_tick, out ushort serverTickLo, out byte serverTickHi);

            for (int viewerIndex = 0; viewerIndex < _players.Count; viewerIndex++)
            {
                PlayerSession viewer = _players[viewerIndex];
                if (viewer.Peer == null) continue;

                // Query the spatial grid for the 3×3 cell neighbourhood around this viewer.
                // _spatialGrid is guaranteed non-null here (initialised in Start, rebuilt each tick).
                // For a 2 000-player zone this reduces the inner loop from O(N) to O(k)
                // where k = players in adjacent cells, typically single digits to low hundreds.
                // In Arena mode (10-20 players, small map) all players fit in a few cells —
                // the overhead of the grid is negligible and the code path is identical.
                System.Collections.Generic.List<PlayerSession> nearby =
                    _spatialGrid!.QueryNeighbours(viewer.Position);

                for (int ni = 0; ni < nearby.Count; ni++)
                {
                    PlayerSession entity = nearby[ni];
                    bool isSelf = entity.EntityId == viewer.EntityId;

                    if (!isSelf &&
                        CombatMath.DistanceSqr(viewer.Position, entity.Position) > _viewRadiusSqr)
                        continue;

                    // ── Delta-compression position check ─────────────────────────────
                    // Encode position once; compare against the value broadcast last tick.
                    // LastBroadcastX/Y are updated in CommitBroadcastState() AFTER all
                    // viewers are processed, so every viewer in the same tick sees the
                    // same "changed" / "unchanged" decision for a given entity.
                    //
                    // Own entity: always send regardless of movement — the client needs
                    // the AcknowledgedTick field for input reconciliation every tick.
                    // Other entities: skip if position encoding hasn't changed.
                    short encX = PacketEncoding.EncodePosition(entity.Position.X);
                    short encY = PacketEncoding.EncodePosition(entity.Position.Y);

                    if (isSelf || encX != entity.LastBroadcastX || encY != entity.LastBroadcastY)
                    {
                        // Mutate pre-allocated struct — zero heap allocation.
                        // Ticks use 24-bit wrapping encoding: 3 bytes each instead of 4.
                        _posPacket.EntityId = entity.EntityId;
                        _posPacket.X        = encX;
                        _posPacket.Y        = encY;
                        _posPacket.ServerTickLo      = serverTickLo;
                        _posPacket.ServerTickHi      = serverTickHi;
                        PacketEncoding.EncodeTick24(entity.LastProcessedClientTick,
                            out _posPacket.AcknowledgedTickLo, out _posPacket.AcknowledgedTickHi);
                        _network.SendTo(viewer.Peer, in _posPacket, DeliveryMethod.Unreliable);
                    }

                    // ── Delta-compression health check ────────────────────────────────
                    // Health is faction-gated (allies only). Skip if HP encoding unchanged.
                    if (entity.Faction == viewer.Faction)
                    {
                        ushort encHealth = PacketEncoding.EncodeHealth(entity.Health);
                        if (encHealth != entity.LastBroadcastHealth)
                        {
                            _healthPacket.EntityId = entity.EntityId;
                            _healthPacket.Health   = encHealth;
                            _network.SendTo(viewer.Peer, in _healthPacket, DeliveryMethod.Unreliable);
                        }
                    }
                }
            }

            // ── Commit broadcast state after ALL viewers have been served ─────────────
            // Updating LastBroadcast* inside the inner loop would cause viewer[1] to see
            // "unchanged" for an entity that viewer[0] just broadcast — incorrect.
            // One O(N) pass here keeps the per-viewer logic clean and allocation-free.
            CommitBroadcastState();
        }

        /// <summary>
        /// Updates each player's delta-compression sentinels to reflect the encoded values
        /// that were eligible for broadcast this tick.  Must be called exactly once per tick,
        /// after BroadcastState() finishes iterating all viewers.
        /// </summary>
        private void CommitBroadcastState()
        {
            for (int i = 0; i < _players.Count; i++)
            {
                PlayerSession e = _players[i];
                e.LastBroadcastX      = PacketEncoding.EncodePosition(e.Position.X);
                e.LastBroadcastY      = PacketEncoding.EncodePosition(e.Position.Y);
                e.LastBroadcastHealth = PacketEncoding.EncodeHealth(e.Health);
            }
        }

        private void BroadcastCombatEvent(CombatEventPacket ev)
        {
            Vec2 origin = FindById(ev.TargetId)?.Position ?? Vec2.Zero;
            _network?.SendToInterested(in ev, DeliveryMethod.ReliableOrdered, origin, _zone.EventFilter, _players, _spatialGrid);
        }

        private void BroadcastAoEHitEvent(AoEHitEventPacket ev)
        {
            Vec2 origin = FindById(ev.HitEntityId)?.Position ?? Vec2.Zero;
            _network?.SendToInterested(in ev, DeliveryMethod.ReliableOrdered, origin, _zone.EventFilter, _players, _spatialGrid);
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

        // Accepts the concrete List<T> type rather than IReadOnlyList<T> so every
        // .Count access and [i] indexer call is a direct array read (no vtable dispatch).
        // All callers already hold List<StatusEffectAppliedPacket> references; the interface
        // parameter provided zero abstraction benefit at measurable per-call cost in AoE combat.
        private void BroadcastStatusEffects(List<StatusEffectAppliedPacket> statusEffects)
        {
            for (int i = 0; i < statusEffects.Count; i++)
            {
                // Copy to a local so we can pass by 'in' reference cleanly.
                // The copy is stack-allocated (struct); no heap allocation occurs.
                StatusEffectAppliedPacket p = statusEffects[i];
                BroadcastStatusEffect(in p);
            }
        }

        private void BroadcastStatusEffect(in StatusEffectAppliedPacket packet)
        {
            PlayerSession? target = FindById(packet.TargetEntityId);
            if (target == null || _network == null)
                return;

            if (packet.Visibility == StatusEffectVisibility.Everyone)
            {
                _network.SendToInterested(in packet, DeliveryMethod.ReliableOrdered,
                    target.Position, _zone.EventFilter, _players, _spatialGrid);
                return;
            }

            // AlliesOnly: use the spatial grid neighbourhood to avoid iterating all N players.
            // _spatialGrid is guaranteed non-null (initialised in Start, rebuilt each tick).
            System.Collections.Generic.List<PlayerSession> nearby =
                _spatialGrid!.QueryNeighbours(target.Position);
            for (int i = 0; i < nearby.Count; i++)
            {
                PlayerSession viewer = nearby[i];
                // Skip ghost sessions and enemy players.
                if (viewer.Peer == null) continue;
                if (viewer.Faction != target.Faction) continue;
                if (!_zone.EventFilter.ShouldReceive(viewer, target.Position)) continue;
                _network.SendTo(viewer.Peer, in packet, DeliveryMethod.ReliableOrdered);
            }
        }

        private void BroadcastStatusEffectRemoval(in StatusEffectRemovedPacket packet)
        {
            PlayerSession? target = FindById(packet.TargetEntityId);
            if (target == null || _network == null)
                return;

            if (packet.Visibility == StatusEffectVisibility.Everyone)
            {
                _network.SendToInterested(in packet, DeliveryMethod.ReliableOrdered,
                    target.Position, _zone.EventFilter, _players, _spatialGrid);
                return;
            }

            // AlliesOnly: spatial grid narrows the viewer set from O(N) to O(k).
            // _spatialGrid is guaranteed non-null (initialised in Start, rebuilt each tick).
            System.Collections.Generic.List<PlayerSession> nearby =
                _spatialGrid!.QueryNeighbours(target.Position);
            for (int i = 0; i < nearby.Count; i++)
            {
                PlayerSession viewer = nearby[i];
                if (viewer.Peer == null) continue;
                if (viewer.Faction != target.Faction) continue;
                if (!_zone.EventFilter.ShouldReceive(viewer, target.Position)) continue;
                _network.SendTo(viewer.Peer, in packet, DeliveryMethod.ReliableOrdered);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private PlayerSession? FindById(int entityId)
            => _entityMap.TryGetValue(entityId, out PlayerSession? s) ? s : null;

        // ── Heartbeat / grace-period maintenance ──────────────────────────────

        /// <summary>
        /// Drains the deferred-hydration queue, applying completed Redis profile reads to
        /// their sessions.  Incomplete tasks are skipped and remain in the queue.
        /// Called once per tick from ProcessTick — never blocks the tick thread.
        /// </summary>
        private void FinalizeHydration()
        {
            if (_pendingHydration.Count == 0) return;

            // Drain the queue: dequeue everything, re-enqueue incomplete tasks, apply completed ones.
            // All access is on the single game-loop thread — plain Queue<T>, no locks needed.
            // Under normal conditions the queue has at most a handful of entries (one per
            // connecting player), so this O(n) pass is negligible.
            int count = _pendingHydration.Count;
            for (int i = 0; i < count; i++)
            {
                var entry = _pendingHydration.Dequeue();

                if (!entry.ProfileTask.IsCompleted)
                {
                    // Still waiting for Redis — put it back and try next tick.
                    _pendingHydration.Enqueue(entry);
                    continue;
                }

                DataLayer.PlayerProfile? profile = null;
                if (!entry.ProfileTask.IsFaulted)
                    profile = entry.ProfileTask.Result;

                if (profile != null)
                    entry.Session.HydrateFromProfile(profile);
                else
                    entry.Session.Health = entry.Session.MaxHealth;
            }
        }

        /// <summary>  Does not block the game loop; awaited on a thread-pool thread.
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

                // Task.Run with a capturing lambda allocates a closure object on the GC heap.
                // ThreadPool.QueueUserWorkItem<TState> with a static lambda eliminates the closure:
                // the player name is passed as the TState argument rather than being captured.
                // 'static' forces a compile-time guarantee that the lambda captures nothing.
                ThreadPool.QueueUserWorkItem(
                    static name => Console.WriteLine($"[Arena] Grace period expired for {name} — entity despawned."),
                    grace.Session.PlayerName,
                    preferLocal: false);
            }
        }

        /// <summary>
        /// Computes crafting ingredient rewards earned by <paramref name="player"/> in this match.
        /// Rewards are the only durable progression output from an Arena match (in-session pickups
        /// are discarded by design so Arena balance is not affected by loot variance).
        /// </summary>
        // Pre-allocated single-element reward array — reused across all end-of-match reward
        // computations. Avoids the 'new[]' heap allocation that would occur inside ProcessTick
        // when EndMatch fires. Safe because EndMatch sets _isRunning = false before this is
        // called, so no subsequent tick will observe a stale value.
        private readonly CraftingIngredientReward[] _rewardScratch = new CraftingIngredientReward[1];

        private CraftingIngredientReward[] ComputeCraftingRewards(PlayerSession player, FactionId winner)
        {
            // Base participation reward: ingredient 1 (generic "arena shard"), quantity = 1.
            // Kill bonus: +1 per kill.  Win bonus: +2 extra shards.
            // TODO: Replace with a configurable reward table once designer tooling is available.
            int total = 1 + player.KillCount + (player.Faction == winner ? 2 : 0);
            if (total <= 0) return System.Array.Empty<CraftingIngredientReward>();

            // Mutate the pre-allocated scratch slot instead of allocating 'new[]'.
            _rewardScratch[0] = new CraftingIngredientReward { IngredientId = 1, Quantity = total };
            return _rewardScratch;
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
            // Mutate pre-allocated struct — zero heap allocation, passed by `in` to avoid copy.
            _groundSpawnedPacket.GroundItemId = id;
            _groundSpawnedPacket.DefinitionId = item.DefinitionId;
            _groundSpawnedPacket.X            = PacketEncoding.EncodePosition(position.X);
            _groundSpawnedPacket.Y            = PacketEncoding.EncodePosition(position.Y);
            _network?.SendToInterested(in _groundSpawnedPacket, DeliveryMethod.ReliableOrdered, position, _zone.EventFilter, _players, _spatialGrid);
        }

        /// <summary>
        /// Value-type container for a lootable item that has landed on the ground.
        /// Stored directly in the Dictionary<int, GroundItem> value slots — no heap allocation
        /// per drop event.  Item is a managed reference; making GroundItem a struct eliminates
        /// the extra wrapper object while keeping the ItemInstance reference itself on the heap
        /// (which is correct — ItemInstance has crafted-stat data that must outlive the drop).
        /// </summary>
        private struct GroundItem
        {
            public int          GroundItemId;
            public Vec2         Position;
            public ItemInstance Item;
        }
    }
}
