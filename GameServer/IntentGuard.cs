using LiteNetLib;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace GameServer
{
    internal enum IntentKind : byte
    {
        Input,
        Attack,
        Spell,
        Shoot,
    }

    internal sealed class IntentGuard
    {
        private const int MaxPastTickSkew   = 2;
        private const int MaxFutureTickSkew = 5;

        private const double InputRatePerSecond  = 60.0;
        private const double ActionRatePerSecond = 20.0;
        private const double InputBurstTokens    = 30.0;
        private const double ActionBurstTokens   = 10.0;

        private const int MaxPerPeerQueuedActions = 32;
        private const int MaxAttackQueueDepth     = 1024;
        private const int MaxSpellQueueDepth      = 1024;
        private const int MaxShootQueueDepth      = 1024;

        private const int DisconnectViolationScore = 80;

        private sealed class PeerGuardState
        {
            public readonly object Gate = new object();

            public double InputTokens;
            public double AttackTokens;
            public double SpellTokens;
            public double ShootTokens;

            public long LastRefillMs;
            public int ViolationScore;

            public int LastAcceptedInputTick;

            public int PendingAttack;
            public int PendingSpell;
            public int PendingShoot;

            public PeerGuardState(long nowMs)
            {
                InputTokens           = InputBurstTokens;
                AttackTokens          = ActionBurstTokens;
                SpellTokens           = ActionBurstTokens;
                ShootTokens           = ActionBurstTokens;
                LastRefillMs          = nowMs;
                LastAcceptedInputTick = int.MinValue;
            }
        }

        private readonly ConcurrentDictionary<NetPeer, PeerGuardState> _peerGuards = new();
        private int _attackQueueDepth;
        private int _spellQueueDepth;
        private int _shootQueueDepth;

        public void OnPeerConnected(NetPeer peer)
            => _peerGuards[peer] = new PeerGuardState(Environment.TickCount64);

        public void OnPeerDisconnected(NetPeer peer)
            => _peerGuards.TryRemove(peer, out _);

        public bool TryAcceptIntent(NetPeer peer, int packetTick, IntentKind kind, int currentTick, bool isKnownPeer)
        {
            if (!isKnownPeer)
                return false;

            PeerGuardState guard = _peerGuards.GetOrAdd(peer, _ => new PeerGuardState(Environment.TickCount64));
            bool disconnect = false;

            lock (guard.Gate)
            {
                RefillTokens(guard, Environment.TickCount64);

                if (packetTick < currentTick - MaxPastTickSkew || packetTick > currentTick + MaxFutureTickSkew)
                {
                    disconnect = RegisterViolationLocked(guard, 3);
                    goto Finalize;
                }

                if (kind == IntentKind.Input && packetTick < guard.LastAcceptedInputTick)
                    goto Finalize;

                ref double tokens = ref GetTokenBucketRef(guard, kind);
                if (tokens < 1.0)
                {
                    disconnect = RegisterViolationLocked(guard, 1);
                    goto Finalize;
                }

                tokens -= 1.0;
                if (kind == IntentKind.Input)
                    guard.LastAcceptedInputTick = packetTick;

                if (guard.ViolationScore > 0)
                    guard.ViolationScore--;

                return true;
            }

        Finalize:
            if (disconnect)
            {
                Console.WriteLine($"[Guard] Disconnecting peer {peer.Id} for intent abuse");
                peer.Disconnect();
            }

            return false;
        }

        public bool TryReserveActionSlot(NetPeer peer, IntentKind kind)
        {
            if (!_peerGuards.TryGetValue(peer, out PeerGuardState? guard))
                return false;

            bool disconnect = false;

            lock (guard.Gate)
            {
                ref int pending = ref GetPendingRef(guard, kind);
                if (pending >= MaxPerPeerQueuedActions)
                    disconnect = RegisterViolationLocked(guard, 2);
                else
                    pending++;
            }

            if (disconnect)
            {
                Console.WriteLine($"[Guard] Disconnecting peer {peer.Id} for queue abuse");
                peer.Disconnect();
                return false;
            }

            ref int depthRef = ref GetQueueDepthRef(kind);
            int depth = Interlocked.Increment(ref depthRef);
            if (depth <= GetQueueLimit(kind))
                return true;

            Interlocked.Decrement(ref depthRef);
            lock (guard.Gate)
            {
                ref int pending = ref GetPendingRef(guard, kind);
                if (pending > 0) pending--;
                disconnect = RegisterViolationLocked(guard, 2);
            }

            if (disconnect)
            {
                Console.WriteLine($"[Guard] Disconnecting peer {peer.Id} for global queue abuse");
                peer.Disconnect();
            }

            return false;
        }

        public void ReleaseActionSlot(NetPeer peer, IntentKind kind)
        {
            ref int depthRef = ref GetQueueDepthRef(kind);
            Interlocked.Decrement(ref depthRef);

            if (!_peerGuards.TryGetValue(peer, out PeerGuardState? guard))
                return;

            lock (guard.Gate)
            {
                ref int pending = ref GetPendingRef(guard, kind);
                if (pending > 0) pending--;
            }
        }

        private static bool RegisterViolationLocked(PeerGuardState guard, int amount)
        {
            guard.ViolationScore += amount;
            return guard.ViolationScore >= DisconnectViolationScore;
        }

        private static void RefillTokens(PeerGuardState guard, long nowMs)
        {
            long elapsedMs = nowMs - guard.LastRefillMs;
            if (elapsedMs <= 0)
                return;

            double elapsedSeconds = elapsedMs / 1000.0;
            guard.LastRefillMs = nowMs;

            guard.InputTokens  = Math.Min(InputBurstTokens,  guard.InputTokens  + InputRatePerSecond * elapsedSeconds);
            guard.AttackTokens = Math.Min(ActionBurstTokens, guard.AttackTokens + ActionRatePerSecond * elapsedSeconds);
            guard.SpellTokens  = Math.Min(ActionBurstTokens, guard.SpellTokens  + ActionRatePerSecond * elapsedSeconds);
            guard.ShootTokens  = Math.Min(ActionBurstTokens, guard.ShootTokens  + ActionRatePerSecond * elapsedSeconds);
        }

        private static ref double GetTokenBucketRef(PeerGuardState guard, IntentKind kind)
        {
            switch (kind)
            {
                case IntentKind.Input:
                    return ref guard.InputTokens;
                case IntentKind.Attack:
                    return ref guard.AttackTokens;
                case IntentKind.Spell:
                    return ref guard.SpellTokens;
                default:
                    return ref guard.ShootTokens;
            }
        }

        private static ref int GetPendingRef(PeerGuardState guard, IntentKind kind)
        {
            switch (kind)
            {
                case IntentKind.Attack:
                    return ref guard.PendingAttack;
                case IntentKind.Spell:
                    return ref guard.PendingSpell;
                default:
                    return ref guard.PendingShoot;
            }
        }

        private ref int GetQueueDepthRef(IntentKind kind)
        {
            switch (kind)
            {
                case IntentKind.Attack:
                    return ref _attackQueueDepth;
                case IntentKind.Spell:
                    return ref _spellQueueDepth;
                default:
                    return ref _shootQueueDepth;
            }
        }

        private static int GetQueueLimit(IntentKind kind)
        {
            switch (kind)
            {
                case IntentKind.Attack:
                    return MaxAttackQueueDepth;
                case IntentKind.Spell:
                    return MaxSpellQueueDepth;
                default:
                    return MaxShootQueueDepth;
            }
        }
    }
}
