using System;
using System.Collections.Generic;

namespace VoxelRush.Core.Race
{
    /// <summary>What a client knows about the current race (implemented by the network race client; faked in tests).</summary>
    public interface IRaceView
    {
        TrackDefinition? Track { get; }

        RaceStateMessage? State { get; }

        RaceProgressMessage? Progress { get; }

        RaceResultsMessage? Results { get; }
    }

    /// <summary>Race HUD text from server state (engine-free so it is unit tested; the UI only displays it).</summary>
    public static class RaceHudModel
    {
        public static (string Title, string Detail) Describe(IRaceView view, double secondsInPhase, uint countdownTicks = 3 * SimulationConstants.ServerTickRate)
        {
            RacePhase? phase = view.State?.State;
            RaceProgressMessage? progress = view.Progress;
            switch (phase)
            {
                case RacePhase.Waiting:
                    return ("Waiting for racers", string.Empty);
                case RacePhase.Countdown:
                    double left = countdownTicks / (double)SimulationConstants.ServerTickRate - secondsInPhase;
                    return (left > 0 ? Math.Ceiling(left).ToString("0") : "GO", string.Empty);
                case RacePhase.Racing:
                case RacePhase.Finishing:
                    if (progress is not RaceProgressMessage p)
                    {
                        return ("Spectating", string.Empty);
                    }

                    if (p.Crossings >= p.TotalCrossings)
                    {
                        return ("Finished!", "Waiting for the others");
                    }

                    return ($"Checkpoint {p.Crossings + 1}/{p.TotalCrossings}", Feedback(p.LastOutcome));
                case RacePhase.Results:
                    return ("Results", string.Join("\n", ResultLines(view.Results, progress?.RacerName)));
                default:
                    return (string.Empty, string.Empty);
            }
        }

        public static IReadOnlyList<string> ResultLines(RaceResultsMessage? results, string? me)
        {
            var lines = new List<string>();
            if (results == null)
            {
                return lines;
            }

            foreach ((string name, RaceResult r) in results.Entries)
            {
                string time = r.Finished ? FormatTime(r.RaceTicks) : "DNF";
                lines.Add($"{r.Place}. {name}  {time}{(name == me ? "  < you" : string.Empty)}");
            }

            return lines;
        }

        /// <summary>m:ss.ff from server ticks.</summary>
        public static string FormatTime(uint ticks)
        {
            double seconds = ticks / (double)SimulationConstants.ServerTickRate;
            int minutes = (int)(seconds / 60);
            return $"{minutes}:{seconds - minutes * 60:00.00}";
        }

        private static string Feedback(CrossingOutcome outcome) => outcome switch
        {
            CrossingOutcome.TooFast => "Checkpoint not counted",
            CrossingOutcome.Teleported => "Checkpoint not counted",
            CrossingOutcome.Respawned => "Back on track",
            _ => string.Empty,
        };
    }
}
