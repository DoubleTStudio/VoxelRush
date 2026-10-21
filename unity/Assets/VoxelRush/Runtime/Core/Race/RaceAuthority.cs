using System;
using System.Collections.Generic;

namespace VoxelRush.Core.Race
{
    /// <summary>GAMEPLAY_RULES race states; only the server changes them.</summary>
    public enum RacePhase : byte
    {
        Waiting = 0,
        Countdown = 1,
        Racing = 2,
        Finishing = 3,
        Results = 4,
        Reset = 5,
    }

    public enum CrossingOutcome : byte
    {
        /// <summary>No gate crossed this sample.</summary>
        None,
        Accepted,
        Finished,
        /// <summary>The next gate was crossed sooner than a legitimate car could get there.</summary>
        TooFast,
        /// <summary>The car moved further in one tick than it physically can (and the server didn't move it).</summary>
        Teleported,
        /// <summary>A server respawn/reset moved the car; nothing it passed through counts.</summary>
        Respawned,
        NotRacing,
        UnknownRacer,
        InvalidPosition,
    }

    public sealed class RaceTimings
    {
        public uint CountdownTicks { get; set; } = 3 * SimulationConstants.ServerTickRate;

        /// <summary>After the first finisher, how long the others get before results.</summary>
        public uint FinishingTicks { get; set; } = 30 * SimulationConstants.ServerTickRate;

        public uint ResultsTicks { get; set; } = 10 * SimulationConstants.ServerTickRate;

        /// <summary>How long a disconnected racer keeps its progress for a reconnect.</summary>
        public uint ReconnectGraceTicks { get; set; } = 30 * SimulationConstants.ServerTickRate;

        /// <summary>A race nobody finishes still ends.</summary>
        public uint MaxRaceTicks { get; set; } = 10 * 60 * SimulationConstants.ServerTickRate;
    }

    public readonly struct RaceResult
    {
        public RaceResult(string racerId, int place, bool finished, uint raceTicks, int crossings)
        {
            RacerId = racerId;
            Place = place;
            Finished = finished;
            RaceTicks = raceTicks;
            Crossings = crossings;
        }

        public string RacerId { get; }

        public int Place { get; }

        public bool Finished { get; }

        /// <summary>Ticks from the start signal to the finish (0 for DNF).</summary>
        public uint RaceTicks { get; }

        public int Crossings { get; }
    }

    /// <summary>
    /// Server race state and ordered checkpoint validation (GAMEPLAY_RULES Race states/Checkpoints, VEHICLE_NETWORKING
    /// authoritative crossings). Progress comes only from server physics samples: ordered gate-plane crossings inside
    /// gate bounds, no faster than physically possible, never during teleports or respawns. Clients can't claim anything.
    /// </summary>
    public sealed class RaceAuthority
    {
        private readonly Dictionary<string, Racer> _racers = new Dictionary<string, Racer>();
        private readonly List<RaceResult> _results = new List<RaceResult>();
        private uint _phaseTick;

        public RaceAuthority(TrackDefinition track, RaceTimings? timings = null)
        {
            Track = track;
            Timings = timings ?? new RaceTimings();
        }

        public TrackDefinition Track { get; }

        public RaceTimings Timings { get; }

        public RacePhase Phase { get; private set; } = RacePhase.Waiting;

        public Guid RaceId { get; private set; }

        /// <summary>Track revision locked at countdown (ADR-004: race on an immutable revision).</summary>
        public ulong LockedTrackRevision { get; private set; }

        /// <summary>Tick of the start signal (end of countdown).</summary>
        public uint StartTick { get; private set; }

        public int RacerCount => _racers.Count;

        public long RejectedClaims { get; private set; }

        public long SuspiciousSamples { get; private set; }

        public IReadOnlyList<RaceResult> Results => _results;

        public bool AddRacer(string racerId)
        {
            if (Phase != RacePhase.Waiting || string.IsNullOrEmpty(racerId) || _racers.Count >= SimulationConstants.MaxPlayersPerServer)
            {
                return false;
            }

            if (!_racers.ContainsKey(racerId))
            {
                _racers[racerId] = new Racer(racerId);
            }

            return true;
        }

        /// <summary>
        /// Before the countdown a disconnect frees the slot; during a race the racer keeps its progress for
        /// <see cref="RaceTimings.ReconnectGraceTicks"/> and stops being sampled.
        /// </summary>
        public void Disconnect(string racerId, uint tick)
        {
            if (Phase == RacePhase.Waiting)
            {
                _racers.Remove(racerId);
                return;
            }

            if (_racers.TryGetValue(racerId, out Racer? racer) && racer.Connected)
            {
                racer.Connected = false;
                racer.DisconnectedTick = tick;
            }
        }

        /// <summary>Resumes a disconnected racer within the grace period (same session). False otherwise.</summary>
        public bool TryReconnect(string racerId, uint tick)
        {
            if (!_racers.TryGetValue(racerId, out Racer? racer) || racer.Connected
                || (Phase != RacePhase.Countdown && Phase != RacePhase.Racing && Phase != RacePhase.Finishing)
                || tick - racer.DisconnectedTick > Timings.ReconnectGraceTicks)
            {
                return false;
            }

            racer.Connected = true;
            return true;
        }

        public bool IsRacer(string racerId) => _racers.ContainsKey(racerId);

        public bool IsConnected(string racerId) => _racers.TryGetValue(racerId, out Racer? r) && r.Connected;

        /// <summary>
        /// Where a (re)spawned racer's car goes: 4 m past its last validated gate facing along the gate, or the start.
        /// Heading is a unit XZ direction.
        /// </summary>
        public (Vec3 Position, float HeadingX, float HeadingZ) RespawnPose(string racerId)
        {
            int crossings = _racers.TryGetValue(racerId, out Racer? r) ? r.Crossings : 0;
            Gate next = Track.GateAt(Math.Min(crossings, Track.TotalCrossings - 1));
            if (crossings == 0)
            {
                return (Track.Start, next.NormalX, next.NormalZ);
            }

            Gate last = Track.GateAt(crossings - 1);
            return (new Vec3(last.X + last.NormalX * 4f, last.Y, last.Z + last.NormalZ * 4f), last.NormalX, last.NormalZ);
        }

        public bool StartCountdown(uint tick)
        {
            if (Phase != RacePhase.Waiting || _racers.Count == 0)
            {
                return false;
            }

            RaceId = Guid.NewGuid();
            LockedTrackRevision = Track.TrackRevision;
            Enter(RacePhase.Countdown, tick);
            StartTick = tick + Timings.CountdownTicks;
            return true;
        }

        public void Tick(uint tick)
        {
            uint elapsed = tick - _phaseTick;
            switch (Phase)
            {
                case RacePhase.Countdown when elapsed >= Timings.CountdownTicks:
                    Enter(RacePhase.Racing, tick);
                    break;
                case RacePhase.Racing when ConnectedCount() == 0 || elapsed >= Timings.MaxRaceTicks:
                case RacePhase.Finishing when elapsed >= Timings.FinishingTicks || AllFinished():
                    EnterResults(tick);
                    break;
                case RacePhase.Results when elapsed >= Timings.ResultsTicks:
                    Enter(RacePhase.Reset, tick);
                    break;
                case RacePhase.Reset:
                    // Reset: discard progress (and, with VR-061, the match overlay), keep the racers for the next round.
                    foreach (Racer r in _racers.Values)
                    {
                        r.Clear();
                    }

                    _results.Clear();
                    Enter(RacePhase.Waiting, tick);
                    break;
            }
        }

        /// <summary>
        /// Feeds one server physics sample of a racer's car (previous and current position this tick).
        /// <paramref name="respawned"/> is true when the server itself moved the car (reset when stuck, respawn).
        /// </summary>
        public CrossingOutcome ReportMovement(string racerId, Vec3 from, Vec3 to, uint tick, bool respawned = false)
        {
            if (!_racers.TryGetValue(racerId, out Racer? racer))
            {
                return CrossingOutcome.UnknownRacer;
            }

            if (Phase != RacePhase.Racing && Phase != RacePhase.Finishing || racer.FinishTick.HasValue || !racer.Connected)
            {
                return CrossingOutcome.NotRacing;
            }

            if (!from.IsFinite || !to.IsFinite)
            {
                SuspiciousSamples++;
                return CrossingOutcome.InvalidPosition;
            }

            // A server catching up after a stall samples once for several physics ticks: allow distance for the time
            // that actually passed (capped at one second), so legitimate cars aren't flagged as teleporting.
            uint elapsed = racer.LastSampleTick is uint last && tick > last ? Math.Min(tick - last, (uint)SimulationConstants.ServerTickRate) : 1;
            racer.LastSampleTick = tick;
            if (respawned)
            {
                racer.Respawns++;
                return CrossingOutcome.Respawned;
            }

            float maxStep = Track.MaxPlausibleSpeed / SimulationConstants.ServerTickRate * 1.5f * elapsed + 1f;
            if (from.HorizontalDistance(to) > maxStep)
            {
                SuspiciousSamples++;
                return CrossingOutcome.Teleported;
            }

            // Only the next gate in order counts; crossing any other gate (skipping ahead, going back) is ignored.
            Gate next = Track.GateAt(racer.Crossings);
            if (!next.CrossedForward(from, to))
            {
                return CrossingOutcome.None;
            }

            uint since = tick - (racer.Crossings == 0 ? StartTick : racer.LastCrossingTick);
            if (since < Track.MinTicksTo(racer.Crossings, SimulationConstants.ServerTickRate))
            {
                SuspiciousSamples++;
                return CrossingOutcome.TooFast;
            }

            racer.Crossings++;
            racer.LastCrossingTick = tick;
            if (racer.Crossings < Track.TotalCrossings)
            {
                return CrossingOutcome.Accepted;
            }

            racer.FinishTick = tick;
            if (Phase == RacePhase.Racing)
            {
                Enter(RacePhase.Finishing, tick);
            }

            return CrossingOutcome.Finished;
        }

        /// <summary>Clients may report "I finished" (e.g. UI); it never changes the race. Counted for abuse monitoring.</summary>
        public bool ClaimFinishFromClient(string racerId)
        {
            RejectedClaims++;
            return false;
        }

        public int CrossingsOf(string racerId) => _racers.TryGetValue(racerId, out Racer? r) ? r.Crossings : -1;

        public uint? FinishTickOf(string racerId) => _racers.TryGetValue(racerId, out Racer? r) ? r.FinishTick : null;

        public int RespawnsOf(string racerId) => _racers.TryGetValue(racerId, out Racer? r) ? r.Respawns : 0;

        public RaceStateMessage ToMessage()
        {
            var ids = new int[Track.Gates.Count];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = Track.Gates[i].Id;
            }

            return new RaceStateMessage(RaceId, LockedTrackRevision, Phase, StartTick, ids);
        }

        // Disconnected racers don't hold the race open; they finish as DNF unless they come back and finish.
        private bool AllFinished()
        {
            foreach (Racer r in _racers.Values)
            {
                if (r.Connected && !r.FinishTick.HasValue)
                {
                    return false;
                }
            }

            return true;
        }

        private int ConnectedCount()
        {
            int n = 0;
            foreach (Racer r in _racers.Values)
            {
                if (r.Connected)
                {
                    n++;
                }
            }

            return n;
        }

        private void EnterResults(uint tick)
        {
            _results.Clear();
            var ordered = new List<Racer>(_racers.Values);
            // Finishers by time; then the rest by progress (crossings, then who got there first). Ids break ties.
            ordered.Sort((a, b) =>
            {
                if (a.FinishTick.HasValue != b.FinishTick.HasValue)
                {
                    return a.FinishTick.HasValue ? -1 : 1;
                }

                int c = a.FinishTick.HasValue ? a.FinishTick!.Value.CompareTo(b.FinishTick!.Value) : b.Crossings.CompareTo(a.Crossings);
                if (c == 0 && !a.FinishTick.HasValue)
                {
                    c = a.LastCrossingTick.CompareTo(b.LastCrossingTick);
                }

                return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
            });

            for (int i = 0; i < ordered.Count; i++)
            {
                Racer r = ordered[i];
                _results.Add(new RaceResult(r.Id, i + 1, r.FinishTick.HasValue, r.FinishTick.HasValue ? r.FinishTick.Value - StartTick : 0, r.Crossings));
            }

            Enter(RacePhase.Results, tick);
        }

        private void Enter(RacePhase phase, uint tick)
        {
            Phase = phase;
            _phaseTick = tick;
        }

        private sealed class Racer
        {
            public Racer(string id) => Id = id;

            public string Id { get; }

            public int Crossings { get; set; }

            public uint LastCrossingTick { get; set; }

            public uint? LastSampleTick { get; set; }

            public uint? FinishTick { get; set; }

            public int Respawns { get; set; }

            public bool Connected { get; set; } = true;

            public uint DisconnectedTick { get; set; }

            public void Clear()
            {
                Crossings = 0;
                LastCrossingTick = 0;
                FinishTick = null;
                Respawns = 0;
            }
        }
    }
}
