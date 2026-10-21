using System;
using UnityEngine;
using VoxelRush.Core.Input;

namespace VoxelRush.Networking.Bots
{
    /// <summary>Headless bot driving (<c>-botDrive</c>): full throttle with a slow weave, deterministic per bot seed.</summary>
    [DisallowMultipleComponent]
    public sealed class BotInputSource : MonoBehaviour, IDrivingInputSource
    {
        public const string DriveArgument = "-botDrive";

        private float _phase;
        private bool _drive;

        public DrivingInput ReadDrivingInput()
        {
            if (!_drive)
            {
                return DrivingInput.Neutral;
            }

            float t = Time.realtimeSinceStartup;
            return new DrivingInput(Mathf.Sin(t * 0.6f + _phase) * 0.5f, 1f, 0f, DrivingButtons.None);
        }

        private void Awake()
        {
            _drive = Array.IndexOf(Environment.GetCommandLineArgs(), DriveArgument) >= 0;
            // Different bots weave out of phase so they spread over the arena.
            _phase = (System.Diagnostics.Process.GetCurrentProcess().Id % 7) * 0.9f;
        }
    }
}
