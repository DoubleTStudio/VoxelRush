using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using VoxelRush.Core.Cosmetics;
using VoxelRush.Core.Diagnostics;

namespace VoxelRush.Networking.Vehicles
{
    /// <summary>
    /// The car's cosmetic loadout, one item per slot (COSM-001 paint, COSM-002 decal/trail/horn/flag). The owner asks
    /// for its equipped items; only the server writes what is shown, after <see cref="LoadoutServer"/> checked
    /// ownership. Network variables, so late joiners get the loadout with the spawn. Horn presses go owner → server
    /// → other players, rate-limited by the server.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VehicleLoadout : NetworkBehaviour
    {
        private readonly NetworkVariable<FixedString64Bytes> _paint = NewSlot();
        private readonly NetworkVariable<FixedString64Bytes> _decal = NewSlot();
        private readonly NetworkVariable<FixedString64Bytes> _trail = NewSlot();
        private readonly NetworkVariable<FixedString64Bytes> _horn = NewSlot();
        private readonly NetworkVariable<FixedString64Bytes> _flag = NewSlot();

        // A new car starts with nothing equipped, so empty slots need no request.
        private readonly string[] _sent = { string.Empty, string.Empty, string.Empty, string.Empty, string.Empty };
        private double _nextHornAt;

        /// <summary>Server-side: an owner asked for a slot (raw, unvalidated).</summary>
        internal static event Action<VehicleLoadout, CosmeticKind, string>? Requested;

        /// <summary>Server-side: an owner pressed the horn (not yet rate-limited).</summary>
        internal static event Action<VehicleLoadout>? HornPressed;

        /// <summary>The server-confirmed paint (empty = default).</summary>
        public string Paint => ShownSku(CosmeticKind.Paint);

        /// <summary>The server-confirmed item in a slot (empty = none).</summary>
        public string ShownSku(CosmeticKind kind) => Slot(kind)?.Value.ToString() ?? string.Empty;

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                return;
            }

            for (int i = 0; i < CosmeticCatalog.KindCount; i++)
            {
                var kind = (CosmeticKind)i;
                Slot(kind)!.OnValueChanged = (_, current) => Publish(kind, current.ToString());
                Publish(kind, ShownSku(kind));
            }

            if (IsOwner)
            {
                LocalPlayer.EquippedChanged += SendEquipped;
                LocalPlayer.HornPressed += Honk;
                for (int i = 0; i < CosmeticCatalog.KindCount; i++)
                {
                    SendEquipped((CosmeticKind)i);
                }
            }
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer)
            {
                return;
            }

            for (int i = 0; i < CosmeticCatalog.KindCount; i++)
            {
                // NGO types the delegate as non-nullable; this component is its only subscriber.
                Slot((CosmeticKind)i)!.OnValueChanged = null!;
            }

            LocalPlayer.EquippedChanged -= SendEquipped;
            LocalPlayer.HornPressed -= Honk;
            CarCosmetics.Remove(transform);
        }

        /// <summary>Server-side: show an already validated item.</summary>
        internal void ServerShow(CosmeticKind kind, string sku)
        {
            NetworkVariable<FixedString64Bytes>? slot = Slot(kind);
            if (!IsServer || slot == null || ShownSku(kind) == sku)
            {
                return;
            }

            slot.Value = new FixedString64Bytes(sku);
            // One-off delta to every client; metered like the other game payloads (length prefix + bytes).
            Meter(sizeof(int) + sku.Length, skipOwner: false);
        }

        /// <summary>Server-side: relay an approved horn press to every other player.</summary>
        internal void ServerRelayHorn()
        {
            if (!IsServer)
            {
                return;
            }

            HornRelayRpc();
            Meter(8, skipOwner: true);
        }

        /// <summary>Bots only (<c>-botHorn</c>): presses without the client cooldown, so the server's limit is exercised.</summary>
        internal void SendHornUnthrottled()
        {
            if (IsOwner && !IsServer)
            {
                HonkRpc();
            }
        }

        private static NetworkVariable<FixedString64Bytes> NewSlot() =>
            new NetworkVariable<FixedString64Bytes>(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private NetworkVariable<FixedString64Bytes>? Slot(CosmeticKind kind) => kind switch
        {
            CosmeticKind.Paint => _paint,
            CosmeticKind.Decal => _decal,
            CosmeticKind.Trail => _trail,
            CosmeticKind.Horn => _horn,
            CosmeticKind.Flag => _flag,
            _ => null,
        };

        private void Meter(int bytes, bool skipOwner)
        {
            foreach (ulong client in NetworkManager.ConnectedClientsIds)
            {
                if (client != NetworkManager.ServerClientId && !(skipOwner && client == OwnerClientId))
                {
                    NetworkBandwidth.Meter.Record(client, bytes);
                }
            }
        }

        private void SendEquipped(CosmeticKind kind)
        {
            string sku = LocalPlayer.Equipped(kind) ?? string.Empty;
            // Never send what the server would refuse anyway.
            if (!CosmeticCatalog.IsWellFormed(kind, sku))
            {
                sku = string.Empty;
            }

            if (sku == _sent[(int)kind])
            {
                return;
            }

            _sent[(int)kind] = sku;
            RequestCosmeticRpc(new LoadoutRequest { Kind = kind, Sku = sku });
        }

        private void Honk()
        {
            // Only a confirmed horn sounds; the local cooldown mirrors the server's so presses aren't silently dropped.
            double now = Time.realtimeSinceStartupAsDouble;
            if (ShownSku(CosmeticKind.Horn).Length == 0 || now < _nextHornAt)
            {
                return;
            }

            _nextHornAt = now + HornLimiter.MinIntervalSeconds;
            HonkRpc();
            CarCosmetics.Honk(transform);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        private void RequestCosmeticRpc(LoadoutRequest request, RpcParams rpcParams = default)
        {
            // Defence in depth on top of InvokePermission: never trust a spoofed actor.
            if (!IsServer || rpcParams.Receive.SenderClientId != OwnerClientId)
            {
                return;
            }

            Requested?.Invoke(this, request.Kind, request.Sku ?? string.Empty);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        private void HonkRpc(RpcParams rpcParams = default)
        {
            if (!IsServer || rpcParams.Receive.SenderClientId != OwnerClientId)
            {
                return;
            }

            HornPressed?.Invoke(this);
        }

        // Server-only, so a client can't relay presses past the rate limit; the owner already played its own.
        [Rpc(SendTo.NotOwner, InvokePermission = RpcInvokePermission.Server)]
        private void HornRelayRpc(RpcParams rpcParams = default)
        {
            if (IsServer || rpcParams.Receive.SenderClientId != NetworkManager.ServerClientId)
            {
                return;
            }

            string horn = ShownSku(CosmeticKind.Horn);
            if (horn.Length == 0)
            {
                return;
            }

            CarCosmetics.Honk(transform);
            Debug.Log(LogEvent.Format("vehicle_horn_heard", ("ownerClientId", OwnerClientId), ("sku", horn)));
        }

        private void Publish(CosmeticKind kind, string sku)
        {
            CarCosmetics.Set(transform, kind, sku);
            if (sku.Length > 0)
            {
                Debug.Log(LogEvent.Format("vehicle_cosmetic_seen", ("ownerClientId", OwnerClientId), ("own", IsOwner), ("kind", CosmeticCatalog.NameOf(kind)), ("sku", sku)));
            }
        }
    }
}
