using System;
using Unity.Collections;
using Unity.Netcode;

namespace VoxelRush.Networking.World
{
    /// <summary>Named messages for voxel world sync and building; payloads are the engine-free binary codecs.</summary>
    internal static class WorldMessages
    {
        public const string Info = "vr/world-info";
        public const string Snapshot = "vr/chunk-snapshot";
        public const string Delta = "vr/chunk-delta";
        public const string SnapshotRequest = "vr/snapshot-request";
        public const string Edit = "vr/edit";
        public const string EditResult = "vr/edit-result";

        // Upper bound for any world message; untrusted lengths beyond it are dropped before allocating.
        public const int MaxPayloadBytes = 64 * 1024;

        public static void Send(NetworkManager manager, string name, ulong clientId, byte[] payload, NetworkDelivery delivery)
        {
            using var writer = new FastBufferWriter(payload.Length + sizeof(int), Allocator.Temp, payload.Length + sizeof(int));
            writer.WriteValueSafe(payload.Length);
            writer.WriteBytesSafe(payload);
            manager.CustomMessagingManager.SendNamedMessage(name, clientId, writer, delivery);
            if (manager.IsServer)
            {
                NetworkBandwidth.Meter.Record(clientId, payload.Length + sizeof(int));
            }
        }

        public static void SendToAll(NetworkManager manager, string name, byte[] payload, NetworkDelivery delivery)
        {
            using var writer = new FastBufferWriter(payload.Length + sizeof(int), Allocator.Temp, payload.Length + sizeof(int));
            writer.WriteValueSafe(payload.Length);
            writer.WriteBytesSafe(payload);
            manager.CustomMessagingManager.SendNamedMessageToAll(name, writer, delivery);
            foreach (ulong client in manager.ConnectedClientsIds)
            {
                if (client != NetworkManager.ServerClientId)
                {
                    NetworkBandwidth.Meter.Record(client, payload.Length + sizeof(int));
                }
            }
        }

        public static bool TryRead(FastBufferReader reader, out byte[] payload)
        {
            payload = Array.Empty<byte>();
            if (!reader.TryBeginRead(sizeof(int)))
            {
                return false;
            }

            reader.ReadValue(out int length);
            if (length <= 0 || length > MaxPayloadBytes || !reader.TryBeginRead(length))
            {
                return false;
            }

            payload = new byte[length];
            reader.ReadBytes(ref payload, length);
            return true;
        }
    }
}
