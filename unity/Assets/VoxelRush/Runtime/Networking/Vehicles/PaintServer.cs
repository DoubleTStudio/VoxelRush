using System;
using System.Collections.Generic;
using UnityEngine;
using VoxelRush.Core;
using VoxelRush.Core.Cosmetics;
using VoxelRush.Core.Diagnostics;

namespace VoxelRush.Networking.Vehicles
{
    /// <summary>
    /// Server-side (COSM-001): turns owners' paint requests into the paint everyone sees on their car, through
    /// <see cref="PaintAuthority"/> (ownership from the API with tickets; any catalog paint on development servers).
    /// </summary>
    public sealed class PaintServer : IDisposable
    {
        private readonly PaintAuthority _authority;
        private readonly VehicleSpawner _vehicles;
        private readonly Func<ulong, Guid> _playerOf;
        private readonly List<PaintDecision> _decided = new List<PaintDecision>(SimulationConstants.MaxPlayersPerServer);
        // One warning per client for refused input, so a hostile client can't flood the log.
        private readonly HashSet<ulong> _warned = new HashSet<ulong>();

        public PaintServer(PaintAuthority authority, VehicleSpawner vehicles, Func<ulong, Guid> playerOf)
        {
            _authority = authority;
            _vehicles = vehicles;
            _playerOf = playerOf;
            VehiclePaint.Requested += OnRequested;
        }

        public PaintOwnership Mode => _authority.Mode;

        public void Tick(double now)
        {
            _decided.Clear();
            _authority.Drain(now, _decided);
            foreach (PaintDecision decision in _decided)
            {
                if (decision.Shows)
                {
                    _vehicles.PaintOf(decision.ClientId)?.ServerShow(decision.Sku);
                    Debug.Log(LogEvent.Format("vehicle_paint_applied", ("ownerClientId", decision.ClientId), ("sku", decision.Sku.Length > 0 ? decision.Sku : "default")));
                }
                else
                {
                    Debug.LogWarning(LogEvent.Format("vehicle_paint_rejected", ("ownerClientId", decision.ClientId), ("sku", decision.Sku), ("reason", ReasonOf(decision.Verdict))));
                }
            }
        }

        public void OnClientDisconnected(ulong clientId)
        {
            _authority.Forget(clientId);
            _warned.Remove(clientId);
        }

        public void Dispose()
        {
            VehiclePaint.Requested -= OnRequested;
            _authority.Dispose();
        }

        private void OnRequested(VehiclePaint car, string sku)
        {
            ulong clientId = car.OwnerClientId;
            PaintVerdict verdict = _authority.Submit(clientId, _playerOf(clientId), sku, Time.realtimeSinceStartupAsDouble);
            if (verdict != PaintVerdict.Queued && _warned.Add(clientId))
            {
                // Never log the raw text: it is attacker-controlled.
                Debug.LogWarning(LogEvent.Format("vehicle_paint_rejected", ("ownerClientId", clientId), ("length", sku.Length), ("reason", ReasonOf(verdict))));
            }
        }

        private static string ReasonOf(PaintVerdict verdict) => verdict switch
        {
            PaintVerdict.Malformed => "malformed",
            PaintVerdict.Unknown => "unknown_sku",
            PaintVerdict.NotOwned => "not_owned",
            PaintVerdict.Unverified => "unverified",
            _ => verdict.ToString().ToLowerInvariant(),
        };
    }
}
