using System;
using UnityEngine;
using VoxelRush.Core.Input;
using VoxelRush.Core.Race;
using VoxelRush.Networking.Race;
using VoxelRush.Networking.Vehicles;

namespace VoxelRush.Networking.Bots
{
    /// <summary>
    /// Headless bot driving. <c>-botDrive</c>: full throttle with a slow weave, deterministic per bot seed.
    /// <c>-botRace</c>: waits for the start, follows its lane through the server's gates, coasts after finishing;
    /// <c>-botReconnectAfterGate K</c> additionally drops and resumes the connection after its K-th validated gate.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BotInputSource : MonoBehaviour, IDrivingInputSource
    {
        public const string DriveArgument = "-botDrive";
        public const string RaceArgument = "-botRace";
        public const string ReconnectArgument = "-botReconnectAfterGate";
        // RACE-002: ready up once this many racers are connected (no server auto-start).
        public const string ReadyArgument = "-botReady";

        private float _phase;
        private bool _drive;
        private bool _race;
        private int _reconnectAfter = -1;
        private int _readyAt = -1;
        private float? _lane;

        public DrivingInput ReadDrivingInput()
        {
            if (_race)
            {
                return RaceInput();
            }

            if (!_drive)
            {
                return DrivingInput.Neutral;
            }

            float t = Time.realtimeSinceStartup;
            return new DrivingInput(Mathf.Sin(t * 0.6f + _phase) * 0.5f, 1f, 0f, DrivingButtons.None);
        }

        private void Awake()
        {
            string[] args = Environment.GetCommandLineArgs();
            _drive = Array.IndexOf(args, DriveArgument) >= 0;
            _race = Array.IndexOf(args, RaceArgument) >= 0;
            int r = Array.IndexOf(args, ReconnectArgument);
            if (r >= 0 && r + 1 < args.Length && int.TryParse(args[r + 1], out int gate))
            {
                _reconnectAfter = gate;
            }

            int ready = Array.IndexOf(args, ReadyArgument);
            if (ready >= 0 && ready + 1 < args.Length && int.TryParse(args[ready + 1], out int racers))
            {
                _readyAt = racers;
            }

            // Different bots weave out of phase so they spread over the arena (process ids repeat across containers).
            _phase = (Guid.NewGuid().GetHashCode() & 0x7fffffff) % 7 * 0.9f;
        }

        private void Update()
        {
            RaceClient? race = RaceClient.Current;
            if (_readyAt > 0 && race?.State?.State == RacePhase.Waiting && !race.WantsReady && race.Lobby is RaceLobbyMessage lobby && lobby.Racers >= _readyAt)
            {
                race.SetReady(true);
            }

            if (_reconnectAfter > 0 && race?.Progress is RaceProgressMessage progress && progress.Crossings >= _reconnectAfter
                && ClientBootstrap.Instance is ClientBootstrap bootstrap && bootstrap.Reconnects == 0)
            {
                _reconnectAfter = -1;
                bootstrap.Reconnect();
            }
        }

        private DrivingInput RaceInput()
        {
            RaceClient? race = RaceClient.Current;
            VehicleNetworkDriver? car = OwnCar();
            if (race?.Track == null || race.State == null || car == null)
            {
                return DrivingInput.Neutral;
            }

            RaceProgressMessage? progress = race.Progress;
            bool racing = race.State.State == RacePhase.Racing || race.State.State == RacePhase.Finishing;
            bool finished = progress.HasValue && progress.Value.Crossings >= progress.Value.TotalCrossings;
            if (!racing || finished)
            {
                // Coast, don't brake: a held brake at a standstill turns into reverse (VR-011).
                return DrivingInput.Neutral;
            }

            Vector3 p = car.transform.position;
            Vector3 f = car.transform.forward;
            var position = new Vec3(p.x, p.y, p.z);
            Gate next = race.Track.GateAt(Math.Min(progress?.Crossings ?? 0, race.Track.TotalCrossings - 1));
            // Keep the lane we started in (fixed per bot) so the grid doesn't converge into one pile.
            _lane ??= RaceAutopilot.LaneOf(position, race.Track.Gates[0]);
            return RaceAutopilot.Drive(position, f.x, f.z, next, _lane.Value);
        }

        private static VehicleNetworkDriver? OwnCar()
        {
            foreach (VehicleNetworkDriver driver in FindObjectsByType<VehicleNetworkDriver>(FindObjectsSortMode.None))
            {
                if (driver.IsOwner)
                {
                    return driver;
                }
            }

            return null;
        }
    }
}
