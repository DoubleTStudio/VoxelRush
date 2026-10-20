using System;
using Unity.Netcode;
using UnityEngine;
using VoxelRush.Core.Diagnostics;
using VoxelRush.Core.Race;
using VoxelRush.Networking.World;

namespace VoxelRush.Networking.Race
{
    /// <summary>Client side of races: the latest track, state, own progress and results from the server (display only).</summary>
    public sealed class RaceClient : IRaceView
    {
        public RaceClient(NetworkManager manager)
        {
            manager.CustomMessagingManager.RegisterNamedMessageHandler(RaceMessages.Track, OnTrack);
            manager.CustomMessagingManager.RegisterNamedMessageHandler(RaceMessages.State, OnState);
            manager.CustomMessagingManager.RegisterNamedMessageHandler(RaceMessages.Progress, OnProgress);
            manager.CustomMessagingManager.RegisterNamedMessageHandler(RaceMessages.Results, OnResults);
        }

        /// <summary>The current connection's race client (HUD, bots).</summary>
        public static RaceClient? Current { get; set; }

        public TrackDefinition? Track { get; private set; }

        public RaceStateMessage? State { get; private set; }

        public RaceProgressMessage? Progress { get; private set; }

        public RaceResultsMessage? Results { get; private set; }

        public event Action? Changed;

        private void OnTrack(ulong sender, FastBufferReader reader)
        {
            if (WorldMessages.TryRead(reader, out byte[] payload) && TrackMessage.TryDecode(payload, out TrackDefinition? track))
            {
                Track = track;
                Changed?.Invoke();
            }
        }

        private void OnState(ulong sender, FastBufferReader reader)
        {
            if (WorldMessages.TryRead(reader, out byte[] payload) && RaceStateMessage.TryDecode(payload, out RaceStateMessage? state))
            {
                if (state!.State == RacePhase.Countdown)
                {
                    Results = null;
                }

                State = state;
                Debug.Log(LogEvent.Format("race_client_state", ("phase", state.State), ("startTick", state.ServerStartTick)));
                Changed?.Invoke();
            }
        }

        private void OnProgress(ulong sender, FastBufferReader reader)
        {
            if (WorldMessages.TryRead(reader, out byte[] payload) && RaceProgressMessage.TryDecode(payload, out RaceProgressMessage progress))
            {
                Progress = progress;
                Debug.Log(LogEvent.Format("race_client_progress", ("racer", progress.RacerName), ("crossings", progress.Crossings), ("total", progress.TotalCrossings), ("outcome", progress.LastOutcome)));
                Changed?.Invoke();
            }
        }

        private void OnResults(ulong sender, FastBufferReader reader)
        {
            if (!WorldMessages.TryRead(reader, out byte[] payload) || !RaceResultsMessage.TryDecode(payload, out RaceResultsMessage? results))
            {
                return;
            }

            Results = results;
            foreach ((string name, RaceResult r) in results!.Entries)
            {
                Debug.Log(LogEvent.Format("race_client_result", ("place", r.Place), ("racer", name), ("finished", r.Finished), ("raceTicks", r.RaceTicks), ("me", Progress?.RacerName == name)));
            }

            Changed?.Invoke();
        }
    }
}
