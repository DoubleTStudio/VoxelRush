using System;
using System.Globalization;

namespace VoxelRush.Core.Race
{
    /// <summary>How a checkpoint is drawn for the local racer.</summary>
    public enum GateMarker : byte
    {
        Upcoming,
        Next,
        Passed,
    }

    /// <summary>A moment the local racer should feel (haptics) or see.</summary>
    public enum RaceCue : byte
    {
        None,
        CheckpointPassed,
        Finished,
        /// <summary>The server refused a crossing (too fast, teleport, out of bounds).</summary>
        NotCounted,
        /// <summary>The own car went through a gate that isn't the next one, or the next one backwards.</summary>
        WrongGate,
    }

    /// <summary>
    /// RACE-003 client-side race guidance from server state: checkpoint markers, the direction and distance to the
    /// next gate, and feedback cues. Display only; the server alone decides crossings (VR-060).
    /// </summary>
    public static class RaceGuide
    {
        /// <summary>The crossing index the local racer is heading for, or null when there is nothing to aim at.</summary>
        public static int? NextCrossing(IRaceView view)
        {
            TrackDefinition? track = view.Track;
            RacePhase? phase = view.State?.State;
            if (track == null || !(view.Progress is RaceProgressMessage p)
                || (phase != RacePhase.Countdown && phase != RacePhase.Racing && phase != RacePhase.Finishing))
            {
                return null;
            }

            return p.Crossings < track.TotalCrossings ? p.Crossings : (int?)null;
        }

        /// <summary>True once the local racer has crossed every gate of this race.</summary>
        public static bool HasFinished(IRaceView view) =>
            view.Track is TrackDefinition track && view.Progress is RaceProgressMessage p && p.Crossings >= track.TotalCrossings
            && (view.State?.State == RacePhase.Racing || view.State?.State == RacePhase.Finishing);

        /// <summary>Marker of <paramref name="gateIndex"/> (index into <see cref="TrackDefinition.Gates"/>) on the current lap.</summary>
        public static GateMarker MarkerOf(IRaceView view, int gateIndex)
        {
            if (HasFinished(view))
            {
                return GateMarker.Passed;
            }

            if (!(NextCrossing(view) is int next) || view.Track == null)
            {
                return GateMarker.Upcoming;
            }

            int nextGate = next % view.Track.Gates.Count;
            return gateIndex == nextGate ? GateMarker.Next : gateIndex < nextGate ? GateMarker.Passed : GateMarker.Upcoming;
        }

        /// <summary>The last gate is the finish (and lap) line.</summary>
        public static bool IsFinish(TrackDefinition track, int gateIndex) => gateIndex == track.Gates.Count - 1;

        /// <summary>Where to steer for: the closest point of the gate's span, kept inside its posts.</summary>
        public static Vec3 AimPoint(Gate gate, Vec3 from)
        {
            float tx = -gate.NormalZ, tz = gate.NormalX;
            float lateral = (from.X - gate.X) * tx + (from.Z - gate.Z) * tz;
            float margin = Math.Max(0f, gate.HalfWidth * 0.8f);
            float clamped = Math.Max(-margin, Math.Min(margin, lateral));
            return new Vec3(gate.X + tx * clamped, gate.Y, gate.Z + tz * clamped);
        }

        /// <summary>
        /// Horizontal distance (m) to the gate's aim point and its bearing in degrees relative to the heading:
        /// 0 = straight ahead, positive = to the right (clockwise from above), ±180 = behind.
        /// </summary>
        public static (float Distance, float BearingDegrees) Direction(Gate gate, Vec3 from, float headingX, float headingZ)
        {
            Vec3 aim = AimPoint(gate, from);
            float dx = aim.X - from.X, dz = aim.Z - from.Z;
            float distance = (float)Math.Sqrt(dx * dx + dz * dz);
            if (distance < 1e-4f || (headingX == 0f && headingZ == 0f))
            {
                return (distance, 0f);
            }

            double bearing = Math.Atan2(headingZ * dx - headingX * dz, headingX * dx + headingZ * dz) * 180.0 / Math.PI;
            return (distance, (float)bearing);
        }

        /// <summary>What changed between two progress messages from the server.</summary>
        public static RaceCue Cue(RaceProgressMessage? before, RaceProgressMessage after)
        {
            if (!(before is RaceProgressMessage b))
            {
                return RaceCue.None;
            }

            if (after.Crossings > b.Crossings)
            {
                return after.Crossings >= after.TotalCrossings ? RaceCue.Finished : RaceCue.CheckpointPassed;
            }

            bool refused = after.LastOutcome == CrossingOutcome.TooFast || after.LastOutcome == CrossingOutcome.Teleported
                || after.LastOutcome == CrossingOutcome.InvalidPosition;
            bool repeat = after.Crossings == b.Crossings && after.LastOutcome == b.LastOutcome;
            return refused && !repeat ? RaceCue.NotCounted : RaceCue.None;
        }

        /// <summary>
        /// True when the own car's move from <paramref name="from"/> to <paramref name="to"/> went forwards through a
        /// gate other than the next one, or backwards through the next one. Client hint only; nothing is sent.
        /// </summary>
        public static bool CrossedWrongGate(TrackDefinition track, int crossings, Vec3 from, Vec3 to)
        {
            if (crossings < 0 || crossings >= track.TotalCrossings)
            {
                return false;
            }

            int next = crossings % track.Gates.Count;
            for (int i = 0; i < track.Gates.Count; i++)
            {
                Gate gate = track.Gates[i];
                if (i == next ? gate.CrossedForward(to, from) : gate.CrossedForward(from, to))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>A new race starts from zero; the server only resends progress on the next crossing.</summary>
        public static RaceProgressMessage? AtCountdown(RaceProgressMessage? progress) =>
            progress is RaceProgressMessage p ? new RaceProgressMessage(p.RacerName, 0, p.TotalCrossings, CrossingOutcome.None) : (RaceProgressMessage?)null;

        public static int KilometresPerHour(float metresPerSecond) =>
            float.IsNaN(metresPerSecond) || float.IsInfinity(metresPerSecond) ? 0 : (int)Math.Round(Math.Abs(metresPerSecond) * 3.6f);

        public static string FormatDistance(float metres)
        {
            if (float.IsNaN(metres) || metres < 0f)
            {
                return string.Empty;
            }

            return metres < 1000f
                ? ((int)Math.Round(metres)).ToString(CultureInfo.InvariantCulture) + " m"
                : (metres / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + " km";
        }
    }
}
