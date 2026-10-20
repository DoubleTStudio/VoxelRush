using System.Collections.Generic;
using UnityEngine;
using VoxelRush.Core.Simulation;

namespace VoxelRush.Networking.Vehicles
{
    /// <summary>Per-process 30 Hz simulation loop: physics is stepped by script so the server and predicting clients tick identically.</summary>
    public interface ITickParticipant
    {
        void OnPreTick(uint tick, float deltaTime);

        void OnPostTick(uint tick, float deltaTime);
    }

    [DisallowMultipleComponent]
    public sealed class NetworkSimulation : MonoBehaviour
    {
        private static NetworkSimulation? s_Instance;
        private readonly List<ITickParticipant> _participants = new List<ITickParticipant>();
        private readonly TickClock _clock = new TickClock();
        private SimulationMode _previousMode;

        public static NetworkSimulation Instance
        {
            get
            {
                if (s_Instance == null)
                {
                    var go = new GameObject(nameof(NetworkSimulation));
                    DontDestroyOnLoad(go);
                    s_Instance = go.AddComponent<NetworkSimulation>();
                }

                return s_Instance;
            }
        }

        public uint Tick => _clock.Tick;

        public float TickSeconds => (float)_clock.TickSeconds;

        public long DroppedTicks => _clock.DroppedTicks;

        public void Register(ITickParticipant participant)
        {
            if (!_participants.Contains(participant))
            {
                _participants.Add(participant);
            }
        }

        public void Unregister(ITickParticipant participant) => _participants.Remove(participant);

        private void Awake()
        {
            _previousMode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
        }

        private void OnDestroy()
        {
            Physics.simulationMode = _previousMode;
            if (s_Instance == this)
            {
                s_Instance = null;
            }
        }

        private void Update()
        {
            int ticks = _clock.Advance(Time.unscaledDeltaTime);
            float dt = TickSeconds;
            for (int t = 0; t < ticks; t++)
            {
                uint tick = _clock.Tick - (uint)(ticks - 1 - t);
                for (int i = 0; i < _participants.Count; i++)
                {
                    _participants[i].OnPreTick(tick, dt);
                }

                Physics.Simulate(dt);
                for (int i = 0; i < _participants.Count; i++)
                {
                    _participants[i].OnPostTick(tick, dt);
                }
            }
        }
    }
}
