using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VoxelRush.Core.Cosmetics
{
    public enum PaintVerdict
    {
        /// <summary>Waiting for an ownership lookup or the change rate limit.</summary>
        Queued,
        Applied,
        /// <summary>Back to the default colour (always allowed).</summary>
        Cleared,
        Malformed,
        Unknown,
        NotOwned,
        /// <summary>Ownership could not be confirmed (no API configured, or it didn't answer in time).</summary>
        Unverified,
    }

    public enum PaintOwnership
    {
        /// <summary>Development servers: no identities, so any catalog paint is shown.</summary>
        AnyCatalogPaint,
        /// <summary>Ticketed servers with the API: only paints the player owns.</summary>
        Verified,
        /// <summary>Ticketed servers without the API: fail closed, default colour only.</summary>
        DefaultOnly,
    }

    /// <summary>A decided request: the client's car shows <see cref="Sku"/> (empty = default) when applied or cleared.</summary>
    public readonly struct PaintDecision
    {
        public PaintDecision(ulong clientId, string sku, PaintVerdict verdict)
        {
            ClientId = clientId;
            Sku = sku;
            Verdict = verdict;
        }

        public ulong ClientId { get; }

        public string Sku { get; }

        public PaintVerdict Verdict { get; }

        public bool Shows => Verdict == PaintVerdict.Applied || Verdict == PaintVerdict.Cleared;
    }

    /// <summary>
    /// Server-side (COSM-001): decides which paint each player's car shows. Clients only ask; a paint is shown when
    /// the player owns it. Ownership lookups run on the thread pool and are cached per player, so the tick never
    /// waits on IO. Main-thread only, apart from the lookup completions, which arrive through a queue.
    /// </summary>
    public sealed class PaintAuthority : IDisposable
    {
        /// <summary>At most one visible change per player per interval (each change is broadcast to every client).</summary>
        public const double MinChangeIntervalSeconds = 1;

        /// <summary>A cached miss is re-checked at most this often, so a fresh purchase shows without reconnecting.</summary>
        public const double RefreshSeconds = 10;

        public const double RetrySeconds = 5;

        /// <summary>A request whose ownership can't be confirmed within this time is dropped.</summary>
        public const double PendingTimeoutSeconds = 30;

        public delegate Task<IReadOnlyCollection<string>?> OwnershipLookup(Guid player, CancellationToken ct);

        private readonly OwnershipLookup? _lookup;
        private readonly Dictionary<ulong, Pending> _pending = new Dictionary<ulong, Pending>();
        private readonly Dictionary<ulong, double> _nextChangeAt = new Dictionary<ulong, double>();
        private readonly Dictionary<ulong, Guid> _players = new Dictionary<ulong, Guid>();
        private readonly Dictionary<Guid, Owned> _owned = new Dictionary<Guid, Owned>();
        private readonly ConcurrentQueue<Lookup> _completed = new ConcurrentQueue<Lookup>();
        private readonly List<ulong> _scratch = new List<ulong>(SimulationConstants.MaxPlayersPerServer);
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();

        private PaintAuthority(PaintOwnership mode, OwnershipLookup? lookup)
        {
            Mode = mode;
            _lookup = lookup;
        }

        public PaintOwnership Mode { get; }

        /// <summary>Ownership lookups started (for tests and metrics).</summary>
        public long Lookups { get; private set; }

        public static PaintAuthority Development() => new PaintAuthority(PaintOwnership.AnyCatalogPaint, null);

        public static PaintAuthority DefaultOnly() => new PaintAuthority(PaintOwnership.DefaultOnly, null);

        public static PaintAuthority Verified(OwnershipLookup lookup) =>
            new PaintAuthority(PaintOwnership.Verified, lookup ?? throw new ArgumentNullException(nameof(lookup)));

        /// <summary>
        /// A client asks to show <paramref name="sku"/> (empty = default). Bad input is refused at once; anything
        /// else is queued (latest wins) and decided by <see cref="Drain"/>.
        /// </summary>
        public PaintVerdict Submit(ulong clientId, Guid player, string? sku, double now)
        {
            sku ??= string.Empty;
            if (sku.Length > 0 && !PaintCatalog.IsWellFormed(sku))
            {
                return PaintVerdict.Malformed;
            }

            if (sku.Length > 0 && !PaintCatalog.IsKnown(sku))
            {
                return PaintVerdict.Unknown;
            }

            _players[clientId] = player;
            if (Mode == PaintOwnership.Verified && player != Guid.Empty && !_owned.ContainsKey(player))
            {
                _owned[player] = new Owned();
            }

            _pending[clientId] = new Pending(player, sku, now);
            return PaintVerdict.Queued;
        }

        /// <summary>Decides queued requests that are due; call once per tick. Never blocks, allocates nothing when idle.</summary>
        public void Drain(double now, List<PaintDecision> decided)
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
            foreach (ulong clientId in _pending.Keys)
            {
                _scratch.Add(clientId);
            }

            foreach (ulong clientId in _scratch)
            {
                Pending request = _pending[clientId];
                if (_nextChangeAt.TryGetValue(clientId, out double next) && now < next)
                {
                    continue;
                }

                PaintVerdict verdict = Decide(request, now);
                if (verdict == PaintVerdict.Queued)
                {
                    continue;
                }

                _pending.Remove(clientId);
                var decision = new PaintDecision(clientId, request.Sku, verdict);
                if (decision.Shows)
                {
                    _nextChangeAt[clientId] = now + MinChangeIntervalSeconds;
                }

                decided.Add(decision);
            }
        }

        /// <summary>The client left: drop its request and its player's cached ownership.</summary>
        public void Forget(ulong clientId)
        {
            _pending.Remove(clientId);
            _nextChangeAt.Remove(clientId);
            if (_players.Remove(clientId, out Guid player))
            {
                _owned.Remove(player);
            }
        }

        public void Dispose() => _stop.Cancel();

        private PaintVerdict Decide(Pending request, double now)
        {
            if (request.Sku.Length == 0)
            {
                return PaintVerdict.Cleared;
            }

            switch (Mode)
            {
                case PaintOwnership.AnyCatalogPaint:
                    return PaintVerdict.Applied;
                case PaintOwnership.DefaultOnly:
                    return PaintVerdict.Unverified;
            }

            if (request.Player == Guid.Empty || !_owned.TryGetValue(request.Player, out Owned? owned))
            {
                return PaintVerdict.Unverified;
            }

            if (owned.Skus != null && owned.Skus.Contains(request.Sku))
            {
                return PaintVerdict.Applied;
            }

            // Refuse only on an answer fetched after the request, so a purchase made just before equipping counts.
            if (owned.Skus != null && owned.FetchedAt >= request.SubmittedAt)
            {
                return PaintVerdict.NotOwned;
            }

            if (now - request.SubmittedAt >= PendingTimeoutSeconds)
            {
                return PaintVerdict.Unverified;
            }

            if (!owned.InFlight && now >= owned.NextLookupAt)
            {
                StartLookup(request.Player, owned, now);
            }

            return PaintVerdict.Queued;
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
