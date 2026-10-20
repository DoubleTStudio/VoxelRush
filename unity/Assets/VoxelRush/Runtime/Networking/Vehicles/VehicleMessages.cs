using Unity.Netcode;
using UnityEngine;
using VoxelRush.Core.Input;
using VoxelRush.Core.Simulation;

namespace VoxelRush.Networking.Vehicles
{
    /// <summary>Redundant input batch (CONTRACTS ClientInput ×N): the newest commands, oldest first.</summary>
    public struct InputBatch : INetworkSerializable
    {
        public const int MaxCommands = 4;

        public InputCommand[] Commands;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            byte count = (byte)(Commands?.Length ?? 0);
            serializer.SerializeValue(ref count);
            if (serializer.IsReader)
            {
                // Never allocate from an untrusted length beyond the contract.
                if (count > MaxCommands)
                {
                    count = 0;
                    Commands = System.Array.Empty<InputCommand>();
                    return;
                }

                Commands = new InputCommand[count];
            }

            for (int i = 0; i < count; i++)
            {
                uint sequence = serializer.IsWriter ? Commands![i].Sequence : 0;
                uint tick = serializer.IsWriter ? Commands![i].ClientTick : 0;
                short steering = serializer.IsWriter ? Commands![i].Input.Steering : (short)0;
                ushort throttle = serializer.IsWriter ? Commands![i].Input.Throttle : (ushort)0;
                ushort brake = serializer.IsWriter ? Commands![i].Input.Brake : (ushort)0;
                byte flags = serializer.IsWriter ? Commands![i].Input.Flags : (byte)0;
                serializer.SerializeValue(ref sequence);
                serializer.SerializeValue(ref tick);
                serializer.SerializeValue(ref steering);
                serializer.SerializeValue(ref throttle);
                serializer.SerializeValue(ref brake);
                serializer.SerializeValue(ref flags);
                if (serializer.IsReader)
                {
                    Commands![i] = new InputCommand(sequence, tick, new QuantizedDrivingInput(steering, throttle, brake, flags));
                }
            }
        }
    }

    /// <summary>CONTRACTS VehicleSnapshot plus boost state for replay.</summary>
    public struct VehicleSnapshot : INetworkSerializable
    {
        public uint ServerTick;
        public uint LastProcessedInput;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 LinearVelocity;
        public Vector3 AngularVelocity;
        public float BoostEnergy;
        public float BoostCooldown;
        public bool Boosting;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref ServerTick);
            serializer.SerializeValue(ref LastProcessedInput);
            serializer.SerializeValue(ref Position);
            serializer.SerializeValue(ref Rotation);
            serializer.SerializeValue(ref LinearVelocity);
            serializer.SerializeValue(ref AngularVelocity);
            serializer.SerializeValue(ref BoostEnergy);
            serializer.SerializeValue(ref BoostCooldown);
            serializer.SerializeValue(ref Boosting);
        }
    }
}
