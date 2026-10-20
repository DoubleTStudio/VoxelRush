using UnityEngine;

namespace VoxelRush.Gameplay.Vehicles
{
    /// <summary>Everything needed to restore a car for reconciliation replay (VEHICLE_NETWORKING VehicleSnapshot + boost).</summary>
    public readonly struct VehicleState
    {
        public VehicleState(Vector3 position, Quaternion rotation, Vector3 linearVelocity, Vector3 angularVelocity, BoostState boost)
        {
            Position = position;
            Rotation = rotation;
            LinearVelocity = linearVelocity;
            AngularVelocity = angularVelocity;
            Boost = boost;
        }

        public Vector3 Position { get; }

        public Quaternion Rotation { get; }

        public Vector3 LinearVelocity { get; }

        public Vector3 AngularVelocity { get; }

        public BoostState Boost { get; }

        public bool IsFinite =>
            Finite(Position) && Finite(LinearVelocity) && Finite(AngularVelocity)
            && !float.IsNaN(Rotation.x + Rotation.y + Rotation.z + Rotation.w) && !float.IsInfinity(Rotation.x + Rotation.y + Rotation.z + Rotation.w);

        private static bool Finite(Vector3 v) => !float.IsNaN(v.x + v.y + v.z) && !float.IsInfinity(v.x + v.y + v.z);
    }
}
