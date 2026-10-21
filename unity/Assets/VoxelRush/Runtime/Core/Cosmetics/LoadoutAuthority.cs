using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VoxelRush.Core.Cosmetics
{
    public enum CosmeticVerdict
    {
        /// <summary>Waiting for an ownership lookup or the change rate limit.</summary>
        Queued,
        Applied,
        /// <summary>Back to nothing (paint: the default colour); always allowed.</summary>
        Cleared,
        Malformed,
        Unknown,
        NotOwned,
        /// <summary>Ownership could not be confirmed (no API configured, or it didn't answer in time).</summary>
        Unverified,
    }

    public enum CosmeticOwnership
    {
        /// <summary>Development servers: no identities, so any catalog item is shown.</summary>
        AnyCatalogItem,
        /// <summary>Ticketed servers with the API: only items the player owns.</summary>
        Verified,
        /// <summary>Ticketed servers without the API: fail closed, nothing equipped.</summary>
        DefaultOnly,
    }

    /// <summary>A decided request: the client's car shows <see cref="Sku"/> in slot <see cref="Kind"/> (empty = none) when applied or cleared.</summary>
    public readonly struct LoadoutDecision
    {
        public LoadoutDecision(ulong clientId, CosmeticKind kind, string sku, CosmeticVerdict verdict)
        {
            ClientId = clientId;
            Kind = kind;
            Sku = sku;
            Verdict = verdict;
        }

        public ulong ClientId { get; }

        public CosmeticKind Kind { get; }

        public string Sku { get; }

        public CosmeticVerdict Verdict { get; }

        public bool Shows => Verdict == CosmeticVerdict.Applied || Verdict == CosmeticVerdict.Cleared;
    }

    /// <summary>
    /// Server-side (COSM-001, COSM-002): decides what each player's car shows in every loadout slot (paint, decal,
    /// trail, horn, flag). Clients only ask; an item is shown when the player owns it. Ownership lookups run on the
    /// thread pool and are cached per player (shared by all slots), so the tick never waits on IO. Main-thread only,
    /// apart from the lookup completions, which arrive through a queue.
    /// </summary>
    public sealed class LoadoutAuthority : IDisposable
    {
        /// <summary>At most one visible change per player and slot per interval (each change is broadcast to every client).</summary>
        public const double MinChangeIntervalSeconds = 1;

        /// <summary>A cached miss is re-checked at most this often, so a fresh purchase shows without reconnecting.</summary>
        public const double RefreshSeconds = 10;

        public const double RetrySeconds = 5;

        /// <summary>A request whose ownership can't be confirmed within this time is dropped.</summary>
        public const double PendingTimeoutSeconds = 30;

        public delegate Task<IReadOnlyCollection<string>?> OwnershipLookup(Guid player, CancellationToken ct);

        private readonly OwnershipLookup? _lookup;
        private readonly Dictionary<Slot, Pending> _pending = new Dictionary<Slot, Pending>();
        private readonly Dictionary<Slot, double> _nextChangeAt = new Dictionary<Slot, double>();
        private readonly Dictionary<ulong, Guid> _players = new Dictionary<ulong, Guid>();
        private readonly Dictionary<Guid, Owned> _owned = new Dictionary<Guid, Owned>();
        private readonly ConcurrentQueue<Lookup> _completed = new ConcurrentQueue<Lookup>();
        private readonly List<Slot> _scratch = new List<Slot>(SimulationConstants.MaxPlayersPerServer * CosmeticCatalog.KindCount);
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();

        private LoadoutAuthority(CosmeticOwnership mode, OwnershipLookup? lookup)
        {
            Mode = mode;
            _lookup = lookup;
        }

        public CosmeticOwnership Mode { get; }

        /// <summary>Ownership lookups started (for tests and metrics).</summary>
        public long Lookups { get; private set; }

        public static LoadoutAuthority Development() => new LoadoutAuthority(CosmeticOwnership.AnyCatalogItem, null);

        public static LoadoutAuthority DefaultOnly() => new LoadoutAuthority(CosmeticOwnership.DefaultOnly, null);

        public static LoadoutAuthority Verified(OwnershipLookup lookup) =>
            new LoadoutAuthority(CosmeticOwnership.Verified, lookup ?? throw new ArgumentNullException(nameof(lookup)));

        /// <summary>
        /// A client asks to show <paramref name="sku"/> in slot <paramref name="kind"/> (empty = none). Bad input is
        /// refused at once; anything else is queued (latest per slot wins) and decided by <see cref="Drain"/>.
        /// </summary>
        public CosmeticVerdict Submit(ulong clientId, Guid player, CosmeticKind kind, string? sku, double now)
        {
            sku ??= string.Empty;
            if (!CosmeticCatalog.IsValidKind(kind) || (sku.Length > 0 && !CosmeticCatalog.IsWellFormed(kind, sku)))
            {
                return CosmeticVerdict.Malformed;
            }

            if (sku.Length > 0 && !CosmeticCatalog.IsKnown(kind, sku))
            {
                return CosmeticVerdict.Unknown;
            }

            _players[clientId] = player;
            if (Mode == CosmeticOwnership.Verified && player != Guid.Empty && !_owned.ContainsKey(player))
            {
                _owned[player] = new Owned();
            }

            _pending[new Slot(clientId, kind)] = new Pending(player, sku, now);
            return CosmeticVerdict.Queued;
        }

        /// <summary>Decides queued requests that are due; call once per tick. Never blocks, allocates nothing when idle.</summary>
        public void Drain(double now, List<LoadoutDecision> decided)
        {
            while (_completed.TryDequeue(out Lookup done))
            {
                // A player who left meanwhile is dropped, so late answers can't grow the cache.
                if (_owned.TryGetValue(done.Player, out Owned? owned))
                {
                    owned.InFlight = false;
                    if (done.Skus != null)
                    {
                        owned.Skus = done.Skus;
                        owned.FetchedAt = done.StartedAt;
                    }
                    else
                    {
                        owned.NextLookupAt = now + RetrySeconds;
                    }
                }
            }

            if (_pending.Count == 0)
            {
                return;
            }

            _scratch.Clear();
            foreach (Slot slot in _pending.Keys)
            {
                _scratch.Add(slot);
            }

            foreach (Slot slot in _scratch)
            {
                Pending request = _pending[slot];
                if (_nextChangeAt.TryGetValue(slot, out double next) && now < next)
                {
                    continue;
                }

                CosmeticVerdict verdict = Decide(request, now);
                if (verdict == CosmeticVerdict.Queued)
                {
                    continue;
                }

                _pending.Remove(slot);
                var decision = new LoadoutDecision(slot.ClientId, slot.Kind, request.Sku, verdict);
                if (decision.Shows)
                {
                    _nextChangeAt[slot] = now + MinChangeIntervalSeconds;
                }

                decided.Add(decision);
            }
        }

        /// <summary>The client left: drop its requests and its player's cached ownership.</summary>
        public void Forget(ulong clientId)
        {
            for (int i = 0; i < CosmeticCatalog.KindCount; i++)
            {
                var slot = new Slot(clientId, (CosmeticKind)i);
                _pending.Remove(slot);
                _nextChangeAt.Remove(slot);
            }

            if (_players.Remove(clientId, out Guid player))
            {
                _owned.Remove(player);
            }
        }

        public void Dispose() => _stop.Cancel();

        private CosmeticVerdict Decide(Pending request, double now)
        {
            if (request.Sku.Length == 0)
            {
                return CosmeticVerdict.Cleared;
            }

            switch (Mode)
            {
                case CosmeticOwnership.AnyCatalogItem:
                    return CosmeticVerdict.Applied;
                case CosmeticOwnership.DefaultOnly:
                    return CosmeticVerdict.Unverified;
            }

            if (request.Player == Guid.Empty || !_owned.TryGetValue(request.Player, out Owned? owned))
            {
                return CosmeticVerdict.Unverified;
            }

            if (owned.Skus != null && owned.Skus.Contains(request.Sku))
            {
                return CosmeticVerdict.Applied;
            }

            // Refuse only on an answer fetched after the request, so a purchase made just before equipping counts.
            if (owned.Skus != null && owned.FetchedAt >= request.SubmittedAt)
            {
                return CosmeticVerdict.NotOwned;
            }

            if (now - request.SubmittedAt >= PendingTimeoutSeconds)
            {
                return CosmeticVerdict.Unverified;
            }

            if (!owned.InFlight && now >= owned.NextLookupAt)
            {
                StartLookup(request.Player, owned, now);
            }

            return CosmeticVerdict.Queued;
        }

        private void StartLookup(Guid player, Owned owned, double now)
        {
            owned.InFlight = true;
            owned.NextLookupAt = now + RefreshSeconds;
            Lookups++;
            Task<IReadOnlyCollection<string>?> task;
            try
            {
                task = _lookup!(player, _stop.Token);
            }
            catch (Exception)
            {
                _completed.Enqueue(new Lookup(player, null, now));
                return;
            }

            task.ContinueWith(t =>
            {
                // Observed, so a failed lookup is a retry rather than an unobserved-exception report.
                _ = t.Exception;
                bool ok = t.Status == TaskStatus.RanToCompletion && t.Result != null;
                _completed.Enqueue(new Lookup(player, ok ? new HashSet<string>(t.Result!, StringComparer.Ordinal) : null, now));
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        private readonly struct Slot : IEquatable<Slot>
        {
            public Slot(ulong clientId, CosmeticKind kind)
            {
                ClientId = clientId;
                Kind = kind;
            }

            public ulong ClientId { get; }

            public CosmeticKind Kind { get; }

            public bool Equals(Slot other) => ClientId == other.ClientId && Kind == other.Kind;

            public override bool Equals(object? obj) => obj is Slot other && Equals(other);

            public override int GetHashCode() => (ClientId.GetHashCode() * 31) + (int)Kind;
        }

        private readonly struct Pending
        {
            public Pending(Guid player, string sku, double submittedAt)
            {
                Player = player;
                Sku = sku;
                SubmittedAt = submittedAt;
            }

            public Guid Player { get; }

            public string Sku { get; }

            public double SubmittedAt { get; }
        }

        private readonly struct Lookup
        {
            public Lookup(Guid player, HashSet<string>? skus, double startedAt)
            {
                Player = player;
                Skus = skus;
                StartedAt = startedAt;
            }

            public Guid Player { get; }

            public HashSet<string>? Skus { get; }

            public double StartedAt { get; }
        }

        private sealed class Owned
        {
            public HashSet<string>? Skus;
            public double FetchedAt = double.NegativeInfinity;
            public double NextLookupAt = double.NegativeInfinity;
            public bool InFlight;
        }
    }
}
