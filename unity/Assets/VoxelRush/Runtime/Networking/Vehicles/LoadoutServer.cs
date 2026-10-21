using System;
using System.Collections.Generic;
using UnityEngine;
using VoxelRush.Core;
using VoxelRush.Core.Cosmetics;
using VoxelRush.Core.Diagnostics;

namespace VoxelRush.Networking.Vehicles
{
    /// <summary>
    /// Server-side (COSM-001, COSM-002): turns owners' loadout requests into what everyone sees on their car, through
    /// <see cref="LoadoutAuthority"/> (ownership from the API with tickets; any catalog item on development servers),
    /// and relays horn presses through <see cref="HornLimiter"/>.
    /// </summary>
    public sealed class LoadoutServer : IDisposable
    {
        private readonly LoadoutAuthority _authority;
        private readonly HornLimiter _horns = new HornLimiter();
        private readonly VehicleSpawner _vehicles;
        private readonly Func<ulong, Guid> _playerOf;
        private readonly Func<double> _clock;
        private readonly List<LoadoutDecision> _decided = new List<LoadoutDecision>(SimulationConstants.MaxPlayersPerServer * CosmeticCatalog.KindCount);
        // One warning per client (and horn reason) for refused input, so a hostile client can't flood the log.
        private readonly HashSet<ulong> _warned = new HashSet<ulong>();
        private readonly HashSet<(ulong, HornVerdict)> _hornWarned = new HashSet<(ulong, HornVerdict)>();

        public LoadoutServer(LoadoutAuthority authority, VehicleSpawner vehicles, Func<ulong, Guid> playerOf, Func<double>? clock = null)
        {
            _authority = authority;
            _vehicles = vehicles;
            _playerOf = playerOf;
            _clock = clock ?? (() => Time.realtimeSinceStartupAsDouble);
            VehicleLoadout.Requested += OnRequested;
            VehicleLoadout.HornPressed += OnHornPressed;
        }

        public CosmeticOwnership Mode => _authority.Mode;

        public long HornsRelayed { get; private set; }

        public long HornsLimited => _horns.Limited;

        public void Tick(double now)
        {
            _decided.Clear();
            _authority.Drain(now, _decided);
            foreach (LoadoutDecision decision in _decided)
            {
                string kind = CosmeticCatalog.NameOf(decision.Kind);
                if (decision.Shows)
                {
                    _vehicles.LoadoutOf(decision.ClientId)?.ServerShow(decision.Kind, decision.Sku);
                    Debug.Log(LogEvent.Format("vehicle_cosmetic_applied", ("ownerClientId", decision.ClientId), ("kind", kind), ("sku", decision.Sku.Length > 0 ? decision.Sku : "none")));
                }
                else
                {
                    Debug.LogWarning(LogEvent.Format("vehicle_cosmetic_rejected", ("ownerClientId", decision.ClientId), ("kind", kind), ("sku", decision.Sku), ("reason", ReasonOf(decision.Verdict))));
                }
            }
        }

        public void OnClientDisconnected(ulong clientId)
        {
            _authority.Forget(clientId);
            _horns.Forget(clientId);
            _warned.Remove(clientId);
            _hornWarned.RemoveWhere(w => w.Item1 == clientId);
        }

        public void Dispose()
        {
            VehicleLoadout.Requested -= OnRequested;
            VehicleLoadout.HornPressed -= OnHornPressed;
            _authority.Dispose();
        }

        /// <summary>A horn press from a client's car; relayed to the others only if the server allows it.</summary>
        internal HornVerdict OnHornPressed(ulong clientId, bool hornShown, double now, Action relay)
        {
            HornVerdict verdict = _horns.Press(clientId, hornShown, now);
            if (verdict == HornVerdict.Played)
            {
                HornsRelayed++;
                relay();
                Debug.Log(LogEvent.Format("vehicle_horn_relayed", ("ownerClientId", clientId)));
            }
            else if (_hornWarned.Add((clientId, verdict)))
            {
                Debug.LogWarning(LogEvent.Format("vehicle_horn_rejected", ("ownerClientId", clientId), ("reason", verdict == HornVerdict.NoHorn ? "no_horn" : "rate_limited")));
            }

            return verdict;
        }

        private void OnHornPressed(VehicleLoadout car) =>
            OnHornPressed(car.OwnerClientId, car.ShownSku(CosmeticKind.Horn).Length > 0, _clock(), car.ServerRelayHorn);

        private void OnRequested(VehicleLoadout car, CosmeticKind kind, string sku)
        {
            ulong clientId = car.OwnerClientId;
            CosmeticVerdict verdict = _authority.Submit(clientId, _playerOf(clientId), kind, sku, _clock());
            if (verdict != CosmeticVerdict.Queued && _warned.Add(clientId))
            {
                // Never log the raw text or kind byte: they are attacker-controlled.
                Debug.LogWarning(LogEvent.Format("vehicle_cosmetic_rejected", ("ownerClientId", clientId), ("length", sku.Length), ("reason", ReasonOf(verdict))));
            }
        }

        private static string ReasonOf(CosmeticVerdict verdict) => verdict switch
        {
            CosmeticVerdict.Malformed => "malformed",
            CosmeticVerdict.Unknown => "unknown_sku",
            CosmeticVerdict.NotOwned => "not_owned",
            CosmeticVerdict.Unverified => "unverified",
            _ => verdict.ToString().ToLowerInvariant(),
        };
    }
}
