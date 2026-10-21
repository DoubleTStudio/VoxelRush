using UnityEngine;
using VoxelRush.Gameplay.Vehicles;
using VoxelRush.Networking.Vehicles;

namespace VoxelRush.Presentation.World
{
    /// <summary>Third-person camera behind the player's own car (MVP-002); stays put while there is no car.</summary>
    [DisallowMultipleComponent]
    public sealed class ChaseCamera : MonoBehaviour
    {
        private const float SearchIntervalSeconds = 0.25f;

        [SerializeField] private float _distance = 8f;
        [SerializeField] private float _height = 3.5f;
        [SerializeField] private float _lookHeight = 1.2f;
        [SerializeField] private float _positionSharpness = 6f;
        [SerializeField] private float _yawSharpness = 4f;

        private Transform? _target;
        private bool _explicitTarget;
        private float _nextSearchAt;
        private Vector3 _heading = Vector3.forward;

        public Transform? Target => _target;

        /// <summary>Follows this transform instead of searching for the owned car.</summary>
        public void SetTarget(Transform? target)
        {
            _target = target;
            _explicitTarget = target != null;
            if (target != null)
            {
                Snap();
            }
        }

        /// <summary>Moves the camera; LateUpdate calls this after the car moved, tests call it directly.</summary>
        public void Follow(float deltaTime)
        {
            if (_target == null && !_explicitTarget && Time.unscaledTime >= _nextSearchAt)
            {
                _nextSearchAt = Time.unscaledTime + SearchIntervalSeconds;
                _target = FindOwnCar();
                if (_target != null)
                {
                    Snap();
                }
            }

            if (_target == null)
            {
                return;
            }

            // Yaw only: pitch and roll from bumps and flips would shake the view.
            Vector3 forward = Vector3.ProjectOnPlane(_target.forward, Vector3.up);
            if (forward.sqrMagnitude > 1e-4f)
            {
                _heading = Vector3.Slerp(_heading, forward.normalized, Blend(_yawSharpness, deltaTime));
            }

            Vector3 desired = DesiredPosition();
            transform.position = Vector3.Lerp(transform.position, desired, Blend(_positionSharpness, deltaTime));
            transform.rotation = Quaternion.LookRotation(_target.position + Vector3.up * _lookHeight - transform.position, Vector3.up);
        }

        private void LateUpdate() => Follow(Time.deltaTime);

        private void Snap()
        {
            Vector3 forward = Vector3.ProjectOnPlane(_target!.forward, Vector3.up);
            _heading = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
            transform.position = DesiredPosition();
            transform.rotation = Quaternion.LookRotation(_target.position + Vector3.up * _lookHeight - transform.position, Vector3.up);
        }

        private Vector3 DesiredPosition() => _target!.position - _heading * _distance + Vector3.up * _height;

        // Frame-rate independent exponential smoothing.
        private static float Blend(float sharpness, float deltaTime) => 1f - Mathf.Exp(-sharpness * deltaTime);

        private static Transform? FindOwnCar()
        {
            if (LocalPlayer.Vehicle != null)
            {
                return LocalPlayer.Vehicle.transform;
            }

            // Offline scenes drive a plain local car; networked cars turn auto-simulation off.
            foreach (ArcadeVehicle vehicle in FindObjectsByType<ArcadeVehicle>(FindObjectsSortMode.None))
            {
                if (vehicle.AutoSimulate)
                {
                    return vehicle.transform;
                }
            }

            return null;
        }
    }
}
