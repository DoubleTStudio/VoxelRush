using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using VoxelRush.Core.Diagnostics;
using VoxelRush.Core.Hosting;
using VoxelRush.Core.Input;
using VoxelRush.Core.Simulation;
using VoxelRush.Gameplay.Vehicles;

namespace VoxelRush.Networking.Vehicles
{
    /// <summary>
    /// Server-authoritative networked car (ADR-002, VEHICLE_NETWORKING). Server: consumes validated owner input per tick
    /// and publishes snapshots. Owner: predicts with the same quantized input, reconciles by restore + replay.
    /// Others: kinematic, interpolated from snapshots.
    /// </summary>
    [RequireComponent(typeof(ArcadeVehicle))]
    public sealed class VehicleNetworkDriver : NetworkBehaviour, ITickParticipant
    {
        private const int RedundantInputs = 3;
        private const int SnapshotEveryTicks = 2;
        private const int PredictionRing = 128;
        private const float BlendFactor = 0.5f;
        private const double StatsIntervalSeconds = 5;
        // A hostile client is cut off after this many out-of-contract commands. Lag-induced rejections (too far
        // ahead, queue full, rate limited) don't count: a slow server must not kick honest clients (VR-062 CI).
        private const long MaxMalformedInputs = 600;

        private readonly CorrectionPolicy _policy = new CorrectionPolicy();
        private readonly List<InputCommand> _batchScratch = new List<InputCommand>(RedundantInputs);
        private readonly List<InputBatch> _outboundReady = new List<InputBatch>();
        private readonly List<VehicleSnapshot> _inboundReady = new List<VehicleSnapshot>();
        private readonly (uint Sequence, VehicleState State, double SentAt)[] _predicted = new (uint, VehicleState, double)[PredictionRing];
        private readonly List<ulong> _targets = new List<ulong>(Core.SimulationConstants.MaxPlayersPerServer);

        // Server: cars by owner, for per-client snapshot interest (distance from each client's own car).
        private static readonly Dictionary<ulong, VehicleNetworkDriver> s_byOwner = new Dictionary<ulong, VehicleNetworkDriver>();

        private ArcadeVehicle _vehicle = null!;
        private Role _role;
        private ServerInputQueue? _serverQueue;
        private ClientInputHistory? _history;
        private SnapshotInterpolator? _interpolator;
        private IDrivingInputSource? _source;
        private DelayLine<InputBatch>? _outbound;
        private DelayLine<VehicleSnapshot>? _inbound;
        private uint _lastSnapshotTick;
        private double _nextStatsAt;
        private Stats _stats;

        private enum Role
        {
            None,
            Server,
            Owner,
            Remote,
        }

        /// <summary>Server-wide count of snapshot RPCs sent (each goes to the clients interested in that car this tick).</summary>
        public static long SnapshotsSent { get; private set; }

        /// <summary>Server-wide count of per-client snapshot deliveries skipped by interest management.</summary>
        public static long SnapshotsSkipped { get; private set; }

        public bool IsPredicting => _role == Role.Owner;

        public ArcadeVehicle Vehicle => _vehicle;

        public override void OnNetworkSpawn()
        {
            _vehicle = GetComponent<ArcadeVehicle>();
            _vehicle.AutoSimulate = false;
            // The object is instantiated at the prefab origin and then moved by NGO; with autoSyncTransforms off the
            // Rigidbody would still be at the origin and the first Physics.Simulate would teleport the car there.
            _vehicle.ResetTo(transform.position, transform.rotation);
            _nextStatsAt = Time.realtimeSinceStartupAsDouble + StatsIntervalSeconds;

            if (IsServer)
            {
                _role = Role.Server;
                _serverQueue = new ServerInputQueue();
                _stats.StartPosition = transform.position;
                s_byOwner[OwnerClientId] = this;
                NetworkSimulation.Instance.Register(this);
                Debug.Log(LogEvent.Format("vehicle_spawned", ("ownerClientId", OwnerClientId), ("networkObjectId", NetworkObjectId), ("position", transform.position)));
            }
            else if (IsOwner)
            {
                _role = Role.Owner;
                _history = new ClientInputHistory(PredictionRing);
                _source = FindInputSource();
                ConfigureConditions();
                NetworkSimulation.Instance.Register(this);
                LocalPlayer.Vehicle = _vehicle;
                Debug.Log(LogEvent.Format("vehicle_owned", ("networkObjectId", NetworkObjectId), ("inputSource", _source?.GetType().Name ?? "none")));
            }
            else
            {
                _role = Role.Remote;
                _interpolator = new SnapshotInterpolator();
                ConfigureConditions();
                _vehicle.Body.isKinematic = true;
                _vehicle.Body.interpolation = RigidbodyInterpolation.None;
            }
        }

        public override void OnNetworkDespawn()
        {
            if (_role == Role.Server && s_byOwner.TryGetValue(OwnerClientId, out VehicleNetworkDriver? mine) && mine == this)
            {
                s_byOwner.Remove(OwnerClientId);
            }

            if (_role == Role.Owner && LocalPlayer.Vehicle == _vehicle)
            {
                LocalPlayer.Vehicle = null;
            }

            LogStats();
            if (_role == Role.Server || _role == Role.Owner)
            {
                NetworkSimulation.Instance.Unregister(this);
            }

            _role = Role.None;
        }

        public void OnPreTick(uint tick, float deltaTime)
        {
            if (_role == Role.Server)
            {
                _vehicle.SetInput(_serverQueue!.Consume());
                _vehicle.Simulate(deltaTime);
            }
            else if (_role == Role.Owner)
            {
                DrivingInput raw = _source?.ReadDrivingInput() ?? DrivingInput.Neutral;
                QuantizedDrivingInput quantized = QuantizedDrivingInput.From(raw);
                InputCommand command = _history!.Record(tick, quantized);
                // Predict with exactly what the server will decode, not the raw float input.
                quantized.TryToInput(out DrivingInput applied);
                _vehicle.SetInput(applied);
                _vehicle.Simulate(deltaTime);

                _history.CopyNewest(_batchScratch, RedundantInputs);
                Send(new InputBatch { Commands = _batchScratch.ToArray() });
                _predicted[command.Sequence % PredictionRing] = (command.Sequence, default, Time.realtimeSinceStartupAsDouble);
            }
        }

        public void OnPostTick(uint tick, float deltaTime)
        {
            if (_role == Role.Server)
            {
                TrackServerStats();
                if (tick % SnapshotEveryTicks == 0)
                {
                    SendSnapshot(tick);
                }
            }
            else if (_role == Role.Owner)
            {
                uint sequence = _history!.NextSequence - 1;
                ref var slot = ref _predicted[sequence % PredictionRing];
                slot = (sequence, _vehicle.CaptureState(), slot.SentAt);
            }
        }

        // Owner-only, unreliable: a lost batch is covered by the redundancy of the next one.
        [Rpc(SendTo.Server, Delivery = RpcDelivery.Unreliable, InvokePermission = RpcInvokePermission.Owner)]
        internal void SubmitInputsRpc(InputBatch batch, RpcParams rpcParams = default)
        {
            // Defence in depth on top of InvokePermission: never trust a spoofed actor.
            if (_role != Role.Server || rpcParams.Receive.SenderClientId != OwnerClientId || batch.Commands == null)
            {
                _stats.RejectedSpoofed++;
                return;
            }

            uint serverTick = NetworkSimulation.Instance.Tick;
            foreach (InputCommand command in batch.Commands)
            {
                InputRejection result = _serverQueue!.Enqueue(command, serverTick);
                if (result == InputRejection.Malformed)
                {
                    _stats.RejectedMalformed++;
                }
                else if (result != InputRejection.None && result != InputRejection.Duplicate)
                {
                    _stats.RejectedOther++;
                }
            }

            if (_stats.RejectedMalformed > MaxMalformedInputs)
            {
                Debug.LogWarning(LogEvent.Format("vehicle_input_abuse_disconnect", ("ownerClientId", OwnerClientId), ("malformed", _stats.RejectedMalformed)));
                NetworkManager.DisconnectClient(OwnerClientId, "input_abuse");
            }
        }

        // Interest management (VR-072): each client gets this car at a rate set by its distance from that client's car.
        private void SendSnapshot(uint tick)
        {
            _targets.Clear();
            Vector3 here = transform.position;
            foreach (ulong client in NetworkManager.ConnectedClientsIds)
            {
                if (client == NetworkManager.ServerClientId)
                {
                    continue;
                }

                bool owner = client == OwnerClientId;
                float distance = !owner && s_byOwner.TryGetValue(client, out VehicleNetworkDriver? theirs) && theirs != null
                    ? Vector3.Distance(here, theirs.transform.position)
                    : 0f;
                if (Core.Networking.SnapshotInterest.ShouldSend(tick, distance, owner))
                {
                    _targets.Add(client);
                    NetworkBandwidth.Meter.Record(client, NetworkBandwidth.VehicleSnapshotBytes);
                }
                else
                {
                    SnapshotsSkipped++;
                }
            }

            if (_targets.Count > 0)
            {
                SnapshotRpc(MakeSnapshot(tick), RpcTarget.Group(_targets, RpcTargetUse.Temp));
                SnapshotsSent++;
            }
        }

        [Rpc(SendTo.SpecifiedInParams, Delivery = RpcDelivery.Unreliable)]
        private void SnapshotRpc(VehicleSnapshot snapshot, RpcParams rpcParams = default)
        {
            if (_inbound != null)
            {
                _inbound.Send(snapshot, Time.realtimeSinceStartupAsDouble);
            }
            else
            {
                Apply(snapshot);
            }
        }

        private void Update()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (_outbound != null && _outbound.Receive(now, _outboundReady) > 0)
            {
                foreach (InputBatch batch in _outboundReady)
                {
                    SubmitInputsRpc(batch);
                }
            }

            if (_inbound != null && _inbound.Receive(now, _inboundReady) > 0)
            {
                foreach (VehicleSnapshot snapshot in _inboundReady)
                {
                    Apply(snapshot);
                }
            }

            if (_role == Role.Remote && _interpolator!.TrySample(now, out Vector3 position, out Quaternion rotation))
            {
                _vehicle.Body.position = position;
                _vehicle.Body.rotation = rotation;
                transform.SetPositionAndRotation(position, rotation);
            }

            if (now >= _nextStatsAt)
            {
                _nextStatsAt = now + StatsIntervalSeconds;
                LogStats();
            }
        }

        private void Send(InputBatch batch)
        {
            if (_outbound != null)
            {
                _outbound.Send(batch, Time.realtimeSinceStartupAsDouble);
            }
            else
            {
                SubmitInputsRpc(batch);
            }
        }

        private void Apply(VehicleSnapshot snapshot)
        {
            if (snapshot.ServerTick <= _lastSnapshotTick)
            {
                _stats.SnapshotsOutOfOrder++;
                return;
            }

            _lastSnapshotTick = snapshot.ServerTick;
            _stats.Snapshots++;
            double now = Time.realtimeSinceStartupAsDouble;
            if (_role == Role.Remote)
            {
                _interpolator!.Add(snapshot.ServerTick * (double)NetworkSimulation.Instance.TickSeconds, snapshot.Position, snapshot.Rotation, now);
            }
            else if (_role == Role.Owner)
            {
                Reconcile(snapshot, now);
            }
        }

        private void Reconcile(VehicleSnapshot snapshot, double now)
        {
            var authoritative = new VehicleState(
                snapshot.Position, snapshot.Rotation, snapshot.LinearVelocity, snapshot.AngularVelocity,
                new BoostState(snapshot.BoostEnergy, snapshot.BoostCooldown, snapshot.Boosting));
            if (!authoritative.IsFinite)
            {
                return;
            }

            uint acked = snapshot.LastProcessedInput;
            var predicted = _predicted[acked % PredictionRing];
            bool havePrediction = acked > 0 && predicted.Sequence == acked && predicted.State.IsFinite;
            if (havePrediction && predicted.SentAt > 0)
            {
                _stats.RttSum += now - predicted.SentAt;
                _stats.RttSamples++;
                _predicted[acked % PredictionRing].SentAt = 0;
            }

            _history!.Acknowledge(acked);
            float positionError = havePrediction ? Vector3.Distance(predicted.State.Position, authoritative.Position) : float.NaN;
            float rotationError = havePrediction ? Quaternion.Angle(predicted.State.Rotation, authoritative.Rotation) : float.NaN;
            // Before the server has applied any input its state is the spawn pose: re-sync and replay everything.
            // That's initial synchronisation, not a misprediction, so it isn't counted as a snap.
            CorrectionKind kind;
            if (acked == 0)
            {
                kind = CorrectionKind.Snap;
                _stats.Syncs++;
            }
            else
            {
                kind = _policy.Decide(positionError, rotationError);
                _stats.Record(kind, positionError);
            }
            if (kind == CorrectionKind.None)
            {
                return;
            }

            VehicleState beforeCorrection = _vehicle.CaptureState();
            _vehicle.RestoreState(authoritative);
            float dt = NetworkSimulation.Instance.TickSeconds;
            for (int i = 0; i < _history.Count; i++)
            {
                InputCommand pending = _history[i];
                pending.Input.TryToInput(out DrivingInput input);
                _vehicle.SetInput(input);
                _vehicle.Simulate(dt);
                Physics.Simulate(dt);
                ref var slot = ref _predicted[pending.Sequence % PredictionRing];
                slot = (pending.Sequence, _vehicle.CaptureState(), slot.SentAt);
                _stats.Replayed++;
            }

            if (kind == CorrectionKind.Blend)
            {
                VehicleState corrected = _vehicle.CaptureState();
                _vehicle.RestoreState(new VehicleState(
                    Vector3.Lerp(beforeCorrection.Position, corrected.Position, BlendFactor),
                    Quaternion.Slerp(beforeCorrection.Rotation, corrected.Rotation, BlendFactor),
                    corrected.LinearVelocity, corrected.AngularVelocity, corrected.Boost));
            }
        }

        private VehicleSnapshot MakeSnapshot(uint tick)
        {
            VehicleState state = _vehicle.CaptureState();
            return new VehicleSnapshot
            {
                ServerTick = tick,
                LastProcessedInput = _serverQueue!.LastProcessed,
                Position = state.Position,
                Rotation = state.Rotation,
                LinearVelocity = state.LinearVelocity,
                AngularVelocity = state.AngularVelocity,
                BoostEnergy = state.Boost.Energy,
                BoostCooldown = state.Boost.CooldownRemaining,
                Boosting = state.Boost.IsBoosting,
            };
        }

        private void TrackServerStats()
        {
            Vector3 v = _vehicle.Body.linearVelocity;
            float speed = new Vector2(v.x, v.z).magnitude;
            _stats.MaxSpeed = Mathf.Max(_stats.MaxSpeed, speed);
            // Driven distance; displacement alone undercounts a car that weaves back toward its start.
            _stats.PathLength += speed * NetworkSimulation.Instance.TickSeconds;
        }

        private void ConfigureConditions()
        {
            // Development-only latency/loss simulation of gameplay messages; never active in release builds.
            if (!Debug.isDebugBuild || !NetworkConditionOptions.TryParse(Environment.GetCommandLineArgs(), out var conditions, out _) || !conditions.IsActive)
            {
                return;
            }

            int seed = (int)NetworkObjectId;
            _outbound = new DelayLine<InputBatch>(conditions.OneWaySeconds, conditions.OneWayJitterSeconds, conditions.LossPercent, seed);
            _inbound = new DelayLine<VehicleSnapshot>(conditions.OneWaySeconds, conditions.OneWayJitterSeconds, conditions.LossPercent, seed + 1);
            Debug.Log(LogEvent.Format("network_conditions", ("rttMs", conditions.RttMs), ("jitterMs", conditions.JitterMs), ("lossPercent", conditions.LossPercent)));
        }

        private static IDrivingInputSource? FindInputSource()
        {
            foreach (MonoBehaviour behaviour in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (behaviour is IDrivingInputSource source && behaviour.isActiveAndEnabled)
                {
                    return source;
                }
            }

            return null;
        }

        private void LogStats()
        {
            switch (_role)
            {
                case Role.Server:
                    Debug.Log(LogEvent.Format("vehicle_server_stats",
                        ("ownerClientId", OwnerClientId),
                        ("distance", Vector3.Distance(_stats.StartPosition, transform.position)),
                        ("pathLength", _stats.PathLength),
                        ("maxSpeed", _stats.MaxSpeed),
                        ("lastProcessedInput", _serverQueue!.LastProcessed),
                        ("rejectedMalformed", _stats.RejectedMalformed),
                        ("rejectedOther", _stats.RejectedOther),
                        ("rejectedSpoofed", _stats.RejectedSpoofed)));
                    break;
                case Role.Owner:
                    Debug.Log(LogEvent.Format("vehicle_prediction_stats",
                        ("rttMs", _stats.RttSamples > 0 ? _stats.RttSum / _stats.RttSamples * 1000 : -1),
                        ("snapshots", _stats.Snapshots),
                        ("none", _stats.CorrectionsNone),
                        ("blend", _stats.CorrectionsBlend),
                        ("snap", _stats.CorrectionsSnap),
                        ("sync", _stats.Syncs),
                        ("meanErrorM", _stats.ErrorSamples > 0 ? _stats.ErrorSum / _stats.ErrorSamples : -1),
                        ("maxErrorM", _stats.MaxError),
                        ("replayedTicks", _stats.Replayed)));
                    break;
                case Role.Remote:
                    Debug.Log(LogEvent.Format("vehicle_remote_stats",
                        ("ownerClientId", OwnerClientId),
                        ("snapshots", _stats.Snapshots),
                        ("outOfOrder", _stats.SnapshotsOutOfOrder)));
                    break;
            }
        }

        private struct Stats
        {
            public Vector3 StartPosition;
            public float MaxSpeed;
            public float PathLength;
            public long RejectedMalformed;
            public long RejectedOther;
            public long RejectedSpoofed;
            public long Snapshots;
            public long SnapshotsOutOfOrder;
            public long CorrectionsNone;
            public long CorrectionsBlend;
            public long CorrectionsSnap;
            public long Syncs;
            public long Replayed;
            public double RttSum;
            public long RttSamples;
            public double ErrorSum;
            public long ErrorSamples;
            public float MaxError;

            public void Record(CorrectionKind kind, float error)
            {
                switch (kind)
                {
                    case CorrectionKind.None: CorrectionsNone++; break;
                    case CorrectionKind.Blend: CorrectionsBlend++; break;
                    default: CorrectionsSnap++; break;
                }

                if (!float.IsNaN(error))
                {
                    ErrorSum += error;
                    ErrorSamples++;
                    MaxError = Mathf.Max(MaxError, error);
                }
            }
        }
    }
}
