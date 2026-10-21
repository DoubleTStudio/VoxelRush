using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using VoxelRush.Core.Cosmetics;
using VoxelRush.Core.Diagnostics;

namespace VoxelRush.Networking.Vehicles
{
    /// <summary>
    /// The car's paint (COSM-001). The owner asks for its equipped paint; only the server writes the shown value,
    /// after <see cref="PaintServer"/> checked ownership. A network variable, so late joiners get it with the spawn.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VehiclePaint : NetworkBehaviour
    {
        private readonly NetworkVariable<FixedString64Bytes> _shown = new NetworkVariable<FixedString64Bytes>(
            default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // A new car starts on the default colour, so the default needs no request.
        private string _sent = string.Empty;

        /// <summary>Server-side: an owner asked for a paint (raw, unvalidated).</summary>
        internal static event Action<VehiclePaint, string>? Requested;

        /// <summary>The server-confirmed paint (empty = default).</summary>
        public string Shown => _shown.Value.ToString();

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                return;
            }

            _shown.OnValueChanged += OnShownChanged;
            Publish(Shown);
            if (IsOwner)
            {
                LocalPlayer.EquippedPaintChanged += SendEquipped;
                SendEquipped();
            }
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer)
            {
                return;
            }

            // NGO types the delegate as non-nullable; this component is its only subscriber.
            _shown.OnValueChanged = null!;
            LocalPlayer.EquippedPaintChanged -= SendEquipped;
            CarPaints.Remove(transform);
        }

        /// <summary>Server-side: show an already validated paint.</summary>
        internal void ServerShow(string sku)
        {
            if (!IsServer || Shown == sku)
            {
                return;
            }

            _shown.Value = new FixedString64Bytes(sku);
            // One-off delta to every client; metered like the other game payloads (length prefix + bytes).
            foreach (ulong client in NetworkManager.ConnectedClientsIds)
            {
                if (client != NetworkManager.ServerClientId)
                {
                    NetworkBandwidth.Meter.Record(client, sizeof(int) + sku.Length);
                }
            }
        }

        private void SendEquipped()
        {
            string sku = LocalPlayer.EquippedPaint ?? string.Empty;
            // Never send what the server would refuse anyway.
            if (!PaintCatalog.IsWellFormed(sku))
            {
                sku = string.Empty;
            }

            if (sku == _sent)
            {
                return;
            }

            _sent = sku;
            RequestPaintRpc(new PaintRequest { Sku = sku });
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        private void RequestPaintRpc(PaintRequest request, RpcParams rpcParams = default)
        {
            // Defence in depth on top of InvokePermission: never trust a spoofed actor.
            if (!IsServer || rpcParams.Receive.SenderClientId != OwnerClientId)
            {
                return;
            }

            Requested?.Invoke(this, request.Sku ?? string.Empty);
        }

        private void OnShownChanged(FixedString64Bytes previous, FixedString64Bytes current) => Publish(current.ToString());

        private void Publish(string sku)
        {
            CarPaints.Set(transform, sku);
            if (sku.Length > 0)
            {
                Debug.Log(LogEvent.Format("vehicle_paint_seen", ("ownerClientId", OwnerClientId), ("own", IsOwner), ("sku", sku)));
            }
        }
    }
}
