using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using VoxelRush.Core.Diagnostics;
using VoxelRush.Core.Race;
using VoxelRush.Gameplay.Vehicles;
using VoxelRush.Networking.Vehicles;
using VoxelRush.Networking.World;

namespace VoxelRush.Networking.Race
{
    /// <summary>
    /// Server side of races (VR-060..062): lobby by session id, optional auto-start, samples every racer's server car
    /// each tick into <see cref="RaceAuthority"/>, broadcasts RaceState and results, and resumes reconnecting racers
    /// at their last validated gate. Session ids never leave the server; players see display names.
    /// </summary>
    public sealed class RaceServer
    {
        private readonly NetworkManager _manager;
        private readonly VehicleSpawner _vehicles;
        private readonly int? _autoStart;
        private readonly Dictionary<ulong, string> _keys = new Dictionary<ulong, string>();
        private readonly Dictionary<string, string> _names = new Dictionary<string, string>();
        private readonly Dictionary<ulong, (Vector3 Position, int Resets)> _last = new Dictionary<ulong, (Vector3, int)>();
        private readonly HashSet<ulong> _respawned = new HashSet<ulong>();
        private readonly byte[] _trackPayload;
        private RacePhase _phase;
        private readonly Action<IReadOnlyList<RewardGrant>>? _rewards;

        /// <param name="rewards">Receives XP grants for server-validated results (ticketed servers only: racer keys are player ids).</param>
        public RaceServer(NetworkManager manager, TrackDefinition track, VehicleSpawner vehicles, int? autoStart, Action<IReadOnlyList<RewardGrant>>? rewards = null)
        {
            _rewards = rewards;
            _manager = manager;
            _vehicles = vehicles;
            _autoStart = autoStart;
            Race = new RaceAuthority(track);
            _trackPayload = TrackMessage.Encode(track);
            _phase = Race.Phase;
        }

        public RaceAuthority Race { get; }

        /// <summary>Registers the player; returns a spawn pose when a reconnecting racer must resume mid-course.</summary>
        public (Vector3 Position, Quaternion Rotation)? OnClientConnected(ulong clientId, Guid session, uint tick)
        {
            string key = session.ToString("N");
            _keys[clientId] = key;
            if (!_names.TryGetValue(key, out string? name))
            {
                name = $"P{_names.Count + 1}";
                _names[key] = name;
            }

            (Vector3, Quaternion)? pose = null;
            if (Race.IsRacer(key) && Race.TryReconnect(key, tick))
            {
                (Vec3 p, float hx, float hz) = Race.RespawnPose(key);
                pose = (new Vector3(p.X, p.Y + 0.5f, p.Z), Quaternion.LookRotation(new Vector3(hx, 0f, hz)));
                _respawned.Add(clientId);
                Debug.Log(LogEvent.Format("race_reconnected", ("racer", name), ("clientId", clientId), ("crossings", Race.CrossingsOf(key))));
            }
            else if (Race.Phase == RacePhase.Waiting && Race.AddRacer(key))
            {
                Debug.Log(LogEvent.Format("race_joined", ("racer", name), ("clientId", clientId), ("racers", Race.RacerCount)));
            }

            WorldMessages.Send(_manager, RaceMessages.Track, clientId, _trackPayload, NetworkDelivery.ReliableSequenced);
            WorldMessages.Send(_manager, RaceMessages.State, clientId, Race.ToMessage().Encode(), NetworkDelivery.ReliableSequenced);
            SendProgress(clientId, key, CrossingOutcome.None);
            return pose;
        }

        public void OnClientDisconnected(ulong clientId, uint tick)
        {
            if (_keys.Remove(clientId, out string? key))
            {
                Race.Disconnect(key, tick);
                if (Race.IsRacer(key))
                {
                    Debug.Log(LogEvent.Format("race_racer_away", ("racer", _names[key]), ("crossings", Race.CrossingsOf(key))));
                }
            }

            _last.Remove(clientId);
            _respawned.Remove(clientId);
        }

        public void Tick(uint tick)
        {
            if (Race.Phase == RacePhase.Waiting && _autoStart is int needed && Race.RacerCount >= needed && Race.StartCountdown(tick))
            {
                Debug.Log(LogEvent.Format("race_countdown", ("raceId", Race.RaceId), ("racers", Race.RacerCount), ("startTick", Race.StartTick)));
                PlaceOnGrid();
            }

            foreach (KeyValuePair<ulong, string> entry in _keys)
            {
                Sample(entry.Key, entry.Value, tick);
            }

            Race.Tick(tick);
            if (Race.Phase != _phase)
            {
                _phase = Race.Phase;
                Debug.Log(LogEvent.Format("race_phase", ("phase", _phase), ("tick", tick)));
                WorldMessages.SendToAll(_manager, RaceMessages.State, Race.ToMessage().Encode(), NetworkDelivery.ReliableSequenced);
                if (_phase == RacePhase.Results)
                {
                    PublishResults();
                }
            }
        }

        // Countdown: every racer back on the start grid (cars may be anywhere after free roam or the last race).
        private void PlaceOnGrid()
        {
            int slot = 0;
            foreach (KeyValuePair<ulong, string> entry in _keys)
            {
                ArcadeVehicle? car = _vehicles.VehicleOf(entry.Key);
                if (car == null || !Race.IsRacer(entry.Value))
                {
                    continue;
                }

                (Vector3 position, Quaternion rotation) = _vehicles.GridPose(slot++);
                car.ResetTo(position, rotation);
                _respawned.Add(entry.Key);
            }
        }

        private void Sample(ulong clientId, string key, uint tick)
        {
            ArcadeVehicle? car = _vehicles.VehicleOf(clientId);
            if (car == null || !Race.IsRacer(key))
            {
                return;
            }

            Vector3 position = car.transform.position;
            int resets = car.Resets;
            (Vector3 Position, int Resets) last = _last.TryGetValue(clientId, out var l) ? l : (position, resets);
            _last[clientId] = (position, resets);
            // The server moved the car (spawn at a gate on reconnect, reset when stuck): nothing it passed counts.
            bool respawned = _respawned.Remove(clientId) || resets != last.Resets;
            CrossingOutcome outcome = Race.ReportMovement(key, ToVec(last.Position), ToVec(position), tick, respawned);
            if (outcome == CrossingOutcome.None || outcome == CrossingOutcome.NotRacing || outcome == CrossingOutcome.UnknownRacer)
            {
                return;
            }

            Debug.Log(LogEvent.Format("race_crossing", ("racer", _names[key]), ("outcome", outcome), ("crossings", Race.CrossingsOf(key)), ("tick", tick)));
            SendProgress(clientId, key, outcome);
        }

        private void SendProgress(ulong clientId, string key, CrossingOutcome outcome)
        {
            int crossings = Math.Max(0, Race.CrossingsOf(key));
            var message = new RaceProgressMessage(_names[key], crossings, Race.Track.TotalCrossings, outcome);
            WorldMessages.Send(_manager, RaceMessages.Progress, clientId, message.Encode(), NetworkDelivery.ReliableSequenced);
        }

        private void PublishResults()
        {
            var entries = new List<(string, RaceResult)>(Race.Results.Count);
            foreach (RaceResult r in Race.Results)
            {
                string name = _names.TryGetValue(r.RacerId, out string? n) ? n : "?";
                entries.Add((name, r));
                Debug.Log(LogEvent.Format("race_result", ("place", r.Place), ("racer", name), ("finished", r.Finished), ("raceTicks", r.RaceTicks), ("crossings", r.Crossings)));
            }

            WorldMessages.SendToAll(_manager, RaceMessages.Results, new RaceResultsMessage(Race.RaceId, entries).Encode(), NetworkDelivery.ReliableSequenced);
            if (_rewards != null)
            {
                IReadOnlyList<RewardGrant> grants = RaceRewards.For(Race.RaceId, Race.Results, key => Guid.TryParseExact(key, "N", out Guid player) ? player : (Guid?)null);
                _rewards(grants);
                Debug.Log(LogEvent.Format("race_rewards_queued", ("raceId", Race.RaceId), ("grants", grants.Count)));
            }
        }

        private static Vec3 ToVec(Vector3 v) => new Vec3(v.x, v.y, v.z);
    }

    internal static class RaceMessages
    {
        public const string Track = "vr/race-track";
        public const string State = "vr/race-state";
        public const string Progress = "vr/race-progress";
        public const string Results = "vr/race-results";
    }
}
