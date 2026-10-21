using System.Collections.Generic;
using UnityEngine;
using VoxelRush.Gameplay.Vehicles;
using VoxelRush.Networking;
using VoxelRush.Networking.Vehicles;
using VoxelRush.Presentation.World;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;
using VoxelRush.World.Sync;

namespace VoxelRush.Presentation.Audio
{
    /// <summary>
    /// Client sound effects (AUDIO-001): engines for the own car and the nearest remote cars, hard-contact impacts,
    /// build confirm/refuse from <see cref="PhoneBuilder"/> results and crunches for blocks a replica delta removes.
    /// Clips are synthesised (<see cref="SfxSynth"/>), sources are created once, steady-state frames allocate nothing.
    /// Loudness follows the effects volume (GameMenu sets <see cref="AudioListener.volume"/>); paused apps are silent.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GameAudio : MonoBehaviour
    {
        private const int CrunchQueue = 32;
        private const float LocalEngineSpatial = 0.2f;
        private const float EngineMinDistance = 4f;
        private const float EngineMaxDistance = 70f;
        private const float PitchSeconds = 0.08f;
        private const float FadeSeconds = 0.15f;
        private const float VelocitySeconds = 0.05f;
        private const float AccelSeconds = 0.2f;
        // An interpolated car that hasn't moved for this long has stopped; shorter is a snapshot hold.
        private const float HoldSeconds = 0.15f;
        // Faster than any car drives: snapshot catch-up after a hold, not motion.
        private const float GlitchSpeed = 50f;

        private static readonly float s_defaultMaxSpeed = new VehicleTuning().MaxSpeed;

        [SerializeField] private PhoneBuilder? _builder;
        [SerializeField, Range(0, 8)] private int _remoteEngines = 4;
        [SerializeField, Min(1)] private int _oneShotVoices = 8;
        [SerializeField, Min(1)] private int _oneShotsPerFrame = 3;
        [SerializeField, Min(1)] private int _crunchesPerFrame = 2;
        [SerializeField, Range(0f, 1f)] private float _engineVolume = 0.55f;
        [SerializeField, Range(0f, 1f)] private float _impactVolume = 0.9f;
        [SerializeField, Range(0f, 1f)] private float _buildVolume = 0.7f;
        [SerializeField, Range(0f, 1f)] private float _crunchVolume = 0.8f;

        private readonly List<CarTrack> _tracks = new List<CarTrack>(Core.SimulationConstants.MaxPlayersPerServer);
        private readonly Stack<CarTrack> _spareTracks = new Stack<CarTrack>();
        private readonly RateLimiter _impactLimiter = new RateLimiter(perSecond: 4f, burst: 2f);
        private readonly RateLimiter _crunchLimiter = new RateLimiter(perSecond: 10f, burst: 3f);
        private readonly Vector3[] _crunchAt = new Vector3[CrunchQueue];
        private readonly byte[] _crunchType = new byte[CrunchQueue];
        private readonly float[] _crunchDistance = new float[CrunchQueue];
        private readonly int[] _crunchPicked = new int[CrunchQueue];
        private float[] _remoteDistance = new float[16];
        private int[] _remoteTrack = new int[16];
        private int[] _picked = System.Array.Empty<int>();
        private EngineVoice[] _engines = System.Array.Empty<EngineVoice>();
        private SfxPool? _pool;
        private AudioClip? _engineClip;
        private AudioClip? _boostClip;
        private AudioClip? _impactClip;
        private AudioClip? _placeClip;
        private AudioClip? _removeClip;
        private AudioClip? _nopeClip;
        private AudioClip? _crunchClip;
        private ClientWorldReplica? _replica;
        private ClientWorldReplica? _replicaOverride;
        private bool _hasReplicaOverride;
        private System.Action<ChunkCoord, ushort, byte, byte>? _onBlockChanged;
        private System.Action<EditAction, EditRejection>? _onEdit;
        private PhoneBuilder? _subscribed;
        private Transform? _listener;
        private float _nextListenerSearch;
        private int _crunchCount;
        private int _crunchTotal;
        private bool _appPaused;
        private bool _muted;
        private int _stamp;
        private float _now;
        private uint _random = 0x9E3779B9u;

        public long ImpactsPlayed { get; private set; }

        public long CrunchesPlayed { get; private set; }

        public long BuildSoundsPlayed { get; private set; }

        public BuildSound? LastBuildSound { get; private set; }

        /// <summary>Paused, backgrounded or effects volume at zero: nothing plays or is tracked.</summary>
        public bool Muted => _muted;

        /// <summary>Voice 0 is the own car; the rest go to the nearest remote cars.</summary>
        public int EngineVoices => _engines.Length;

        public SfxPool? Pool => _pool;

        /// <summary>Cars to voice instead of <see cref="ClientCars.All"/> (tests).</summary>
        internal IReadOnlyList<ClientCar>? CarsOverride { get; set; }

        /// <summary>Distance reference instead of the scene's AudioListener (tests).</summary>
        internal Transform? ListenerOverride { get; set; }

        public ClientCar? EngineCar(int voice) => _engines[voice].Track?.Car;

        public float EnginePitch(int voice) => _engines[voice].TargetPitch;

        /// <summary>Listens to this replica instead of the connected client's (tests, offline previews).</summary>
        public void Listen(ClientWorldReplica? replica)
        {
            _hasReplicaOverride = true;
            _replicaOverride = replica;
            BindReplica(replica);
        }

        public void SetAppPaused(bool paused)
        {
            if (_appPaused == paused)
            {
                return;
            }

            _appPaused = paused;
            AudioListener.pause = paused;
        }

        /// <summary>Confirm or refuse sound for one edit verdict; PhoneBuilder raises this.</summary>
        public void PlayBuildResult(EditAction action, EditRejection rejection)
        {
            BuildSound sound = BuildSounds.For(action, rejection);
            LastBuildSound = sound;
            AudioClip? clip = sound switch { BuildSound.Place => _placeClip, BuildSound.Remove => _removeClip, _ => _nopeClip };
            if (_muted || _pool == null || clip == null)
            {
                return;
            }

            float pitch = sound == BuildSound.Nope ? 1f : Vary(0.04f);
            if (_pool.Play(clip, Vector3.zero, _buildVolume, pitch, spatial: false, _now))
            {
                BuildSoundsPlayed++;
            }
        }

        /// <summary>Advances all sounds by one frame; LateUpdate calls this, tests call it directly.</summary>
        public void Tick(float deltaTime, float now)
        {
            _now = now;
            BindReplica(_hasReplicaOverride ? _replicaOverride : ClientBootstrap.Instance?.World?.Replica);
            bool muted = _appPaused || AudioListener.volume <= 0f;
            if (muted != _muted)
            {
                _muted = muted;
                if (muted)
                {
                    StopAll();
                }
            }

            if (muted || _pool == null)
            {
                _crunchCount = 0;
                _crunchTotal = 0;
                return;
            }

            _pool.BeginFrame();
            Vector3 listener = ListenerPosition(now);
            UpdateTracks(deltaTime, now);
            UpdateEngines(listener, deltaTime);
            FlushCrunches(listener, now);
        }

        private void Awake()
        {
            _engineClip = SfxSynth.ToClip("Engine", SfxSynth.EngineLoop());
            _boostClip = SfxSynth.ToClip("Boost", SfxSynth.BoostLoop());
            _impactClip = SfxSynth.ToClip("Impact", SfxSynth.Impact());
            _placeClip = SfxSynth.ToClip("Place", SfxSynth.Place());
            _removeClip = SfxSynth.ToClip("Remove", SfxSynth.Remove());
            _nopeClip = SfxSynth.ToClip("Nope", SfxSynth.Nope());
            _crunchClip = SfxSynth.ToClip("Crunch", SfxSynth.Crunch());

            var root = new GameObject("Audio").transform;
            root.SetParent(transform, false);
            _pool = new SfxPool(root, _oneShotVoices, _oneShotsPerFrame);
            _engines = new EngineVoice[1 + _remoteEngines];
            for (int i = 0; i < _engines.Length; i++)
            {
                _engines[i] = new EngineVoice(root, i, _engineClip, _boostClip);
            }

            _picked = new int[_remoteEngines];
            _onBlockChanged = OnBlockChanged;
            _onEdit = PlayBuildResult;
        }

        private void OnEnable()
        {
            if (_builder == null)
            {
                _builder = GetComponent<PhoneBuilder>();
            }

            if (_builder != null)
            {
                _builder.EditResolved += _onEdit;
                _subscribed = _builder;
            }
        }

        private void OnDisable()
        {
            if (_subscribed != null)
            {
                _subscribed.EditResolved -= _onEdit;
                _subscribed = null;
            }

            BindReplica(null);
            StopAll();
            // Never leave the global pause behind if this goes away while backgrounded.
            SetAppPaused(false);
        }

        private void OnDestroy()
        {
            foreach (AudioClip? clip in new[] { _engineClip, _boostClip, _impactClip, _placeClip, _removeClip, _nopeClip, _crunchClip })
            {
                if (clip != null)
                {
                    Destroy(clip);
                }
            }
        }

        private void LateUpdate() => Tick(Time.deltaTime, Time.time);

        private void OnApplicationPause(bool paused) => SetAppPaused(paused);

        // Phones lose focus to the notification shade and incoming calls; desktop focus changes keep playing.
        private void OnApplicationFocus(bool focused)
        {
            if (Application.isMobilePlatform)
            {
                SetAppPaused(!focused);
            }
        }

        private void BindReplica(ClientWorldReplica? replica)
        {
            if (replica == _replica)
            {
                return;
            }

            if (_replica != null)
            {
                _replica.BlockChanged -= _onBlockChanged;
            }

            _replica = replica;
            if (replica != null)
            {
                // Combined with the debris hook rather than replacing it.
                replica.BlockChanged += _onBlockChanged;
            }
        }

        private void OnBlockChanged(ChunkCoord chunk, ushort index, byte oldType, byte newType)
        {
            if (_muted || newType != BlockIds.Air || oldType == BlockIds.Air)
            {
                return;
            }

            _crunchTotal++;
            if (_crunchCount < CrunchQueue)
            {
                BlockPos block = ChunkMath.ToWorld(chunk, LocalBlock.FromIndex(index));
                _crunchAt[_crunchCount] = new Vector3(block.X + 0.5f, block.Y + 0.5f, block.Z + 0.5f);
                _crunchType[_crunchCount] = oldType;
                _crunchCount++;
            }
        }

        private Vector3 ListenerPosition(float now)
        {
            if (ListenerOverride != null)
            {
                return ListenerOverride.position;
            }

            if (_listener == null && now >= _nextListenerSearch)
            {
                _nextListenerSearch = now + 1f;
                AudioListener found = FindAnyObjectByType<AudioListener>();
                _listener = found != null ? found.transform : null;
            }

            return _listener != null ? _listener.position : Vector3.zero;
        }

        private void UpdateTracks(float deltaTime, float now)
        {
            IReadOnlyList<ClientCar> cars = CarsOverride ?? ClientCars.All;
            _stamp++;
            for (int i = 0; i < cars.Count; i++)
            {
                ClientCar car = cars[i];
                if (car.Transform == null)
                {
                    continue;
                }

                CarTrack track = TrackOf(car);
                track.Stamp = _stamp;
                Step(track, deltaTime, now);
            }

            for (int i = _tracks.Count - 1; i >= 0; i--)
            {
                CarTrack track = _tracks[i];
                if (track.Stamp == _stamp)
                {
                    continue;
                }

                foreach (EngineVoice voice in _engines)
                {
                    if (voice.Track == track)
                    {
                        voice.Assign(null);
                    }
                }

                _tracks.RemoveAt(i);
                track.Car = null!;
                _spareTracks.Push(track);
            }
        }

        private CarTrack TrackOf(ClientCar car)
        {
            for (int i = 0; i < _tracks.Count; i++)
            {
                if (_tracks[i].Car == car)
                {
                    return _tracks[i];
                }
            }

            CarTrack track = _spareTracks.Count > 0 ? _spareTracks.Pop() : new CarTrack();
            track.Begin(car);
            _tracks.Add(track);
            return track;
        }

        private void Step(CarTrack track, float deltaTime, float now)
        {
            Transform t = track.Car.Transform;
            Vector3 position = t.position;
            ArcadeVehicle? vehicle = track.Car.Vehicle;
            Rigidbody? body = track.Car.IsLocal && vehicle != null && vehicle.enabled ? vehicle.Body : null;
            bool simulated = body != null && !body.isKinematic;
            track.MaxSpeed = vehicle != null ? vehicle.Tuning.MaxSpeed : s_defaultMaxSpeed;
            track.Boosting = track.Car.IsBoosting;
            if (!track.HasLast || !(deltaTime > 0f))
            {
                track.HasLast = true;
                track.Position = position;
                track.Detector.Reset();
                return;
            }

            bool detect = true;
            if (simulated)
            {
                track.Velocity = body!.linearVelocity;
                track.ForwardSpeed = vehicle!.ForwardSpeed;
                track.Throttle = vehicle.Input.Throttle;
                if (vehicle.Resets != track.Resets)
                {
                    track.Resets = vehicle.Resets;
                    track.Detector.Reset();
                }
            }
            else
            {
                Vector3 moved = position - track.Position;
                if (moved.sqrMagnitude < 1e-10f)
                {
                    // A snapshot hold looks like a wall stop; only accept "stopped" once it lasts.
                    track.HeldFor += deltaTime;
                    detect = false;
                    if (track.HeldFor >= HoldSeconds)
                    {
                        track.Velocity = Vector3.zero;
                        track.HasVelocity = false;
                        track.Detector.Reset();
                    }
                }
                else
                {
                    track.HeldFor = 0f;
                    Vector3 raw = moved / deltaTime;
                    if (raw.sqrMagnitude > GlitchSpeed * GlitchSpeed)
                    {
                        detect = false;
                        track.Detector.Reset();
                    }
                    else if (!track.HasVelocity)
                    {
                        // First motion after spawn or a stop: take it as is rather than ramping up from zero.
                        track.Velocity = raw;
                        track.HasVelocity = true;
                        detect = false;
                        track.Detector.Reset();
                    }
                    else
                    {
                        track.Velocity = Vector3.Lerp(track.Velocity, raw, 1f - Mathf.Exp(-deltaTime / VelocitySeconds));
                    }
                }

                float forward = Vector3.Dot(track.Velocity, t.forward);
                float accel = (forward - track.ForwardSpeed) / deltaTime;
                track.Accel = Mathf.Lerp(track.Accel, accel, 1f - Mathf.Exp(-deltaTime / AccelSeconds));
                track.ForwardSpeed = forward;
                track.Throttle = EngineSound.EstimateThrottle(track.Accel, forward);
            }

            track.Position = position;
            float hit = detect ? track.Detector.Step(track.Velocity, deltaTime, now) : 0f;
            if (hit > 0f && _impactClip != null && _impactLimiter.TryAcquire(now))
            {
                // Harder hits sound heavier.
                float pitch = (1.1f - 0.3f * hit) * Vary(0.06f);
                if (_pool!.Play(_impactClip, position, _impactVolume * hit, pitch, spatial: !track.Car.IsLocal, now))
                {
                    ImpactsPlayed++;
                }
            }
        }

        private void UpdateEngines(Vector3 listener, float deltaTime)
        {
            if (_remoteDistance.Length < _tracks.Count)
            {
                _remoteDistance = new float[_tracks.Count * 2];
                _remoteTrack = new int[_tracks.Count * 2];
            }

            CarTrack? local = null;
            int remotes = 0;
            for (int i = 0; i < _tracks.Count; i++)
            {
                CarTrack track = _tracks[i];
                if (track.Car.IsLocal && local == null)
                {
                    local = track;
                    continue;
                }

                float d = (track.Position - listener).sqrMagnitude;
                _remoteDistance[remotes] = d <= EngineMaxDistance * EngineMaxDistance ? d : float.PositiveInfinity;
                _remoteTrack[remotes] = i;
                remotes++;
            }

            _engines[0].Assign(local);
            int picked = VoicePicker.PickNearest(_remoteDistance, remotes, _picked, _picked.Length);

            // Voices stick to their car while it stays among the nearest, so pitch doesn't jump between cars.
            for (int v = 1; v < _engines.Length; v++)
            {
                CarTrack? track = _engines[v].Track;
                if (track != null && !IsPicked(track, picked))
                {
                    _engines[v].Assign(null);
                }
            }

            for (int p = 0; p < picked; p++)
            {
                CarTrack track = _tracks[_remoteTrack[_picked[p]]];
                if (HasVoice(track))
                {
                    continue;
                }

                for (int v = 1; v < _engines.Length; v++)
                {
                    if (_engines[v].Track == null)
                    {
                        _engines[v].Assign(track);
                        break;
                    }
                }
            }

            foreach (EngineVoice voice in _engines)
            {
                voice.Update(deltaTime, _engineVolume);
            }
        }

        private bool IsPicked(CarTrack track, int picked)
        {
            for (int p = 0; p < picked; p++)
            {
                if (_tracks[_remoteTrack[_picked[p]]] == track)
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasVoice(CarTrack track)
        {
            for (int v = 1; v < _engines.Length; v++)
            {
                if (_engines[v].Track == track)
                {
                    return true;
                }
            }

            return false;
        }

        private void FlushCrunches(Vector3 listener, float now)
        {
            if (_crunchTotal == 0 || _crunchClip == null)
            {
                return;
            }

            for (int i = 0; i < _crunchCount; i++)
            {
                float d = (_crunchAt[i] - listener).sqrMagnitude;
                _crunchDistance[i] = d <= SfxPool.MaxDistance * SfxPool.MaxDistance ? d : float.PositiveInfinity;
            }

            int picked = VoicePicker.PickNearest(_crunchDistance, _crunchCount, _crunchPicked, _crunchesPerFrame);
            // A big blast plays a few loud crunches, not hundreds.
            float volume = _crunchVolume * (0.55f + 0.45f * Mathf.Clamp01(_crunchTotal / 12f));
            for (int p = 0; p < picked && _crunchLimiter.TryAcquire(now); p++)
            {
                int i = _crunchPicked[p];
                if (_pool!.Play(_crunchClip, _crunchAt[i], volume, CrunchPitch(_crunchType[i]) * Vary(0.06f), spatial: true, now))
                {
                    CrunchesPlayed++;
                }
            }

            _crunchCount = 0;
            _crunchTotal = 0;
        }

        private void StopAll()
        {
            _pool?.StopAll();
            foreach (EngineVoice voice in _engines)
            {
                voice.Silence();
            }

            // Velocity history is stale after a gap; restart it so resuming isn't heard as a crash.
            foreach (CarTrack track in _tracks)
            {
                track.HasLast = false;
            }
        }

        private float Vary(float amount)
        {
            _random ^= _random << 13;
            _random ^= _random >> 17;
            _random ^= _random << 5;
            return 1f + (_random / (float)uint.MaxValue * 2f - 1f) * amount;
        }

        private static float CrunchPitch(byte blockType) => blockType switch
        {
            BlockIds.Glass => 1.5f,
            BlockIds.Metal => 1.25f,
            BlockIds.Wood => 0.95f,
            BlockIds.Grass or BlockIds.Dirt or BlockIds.Sand => 0.8f,
            _ => 0.9f,
        };

        private sealed class CarTrack
        {
            public readonly ImpactDetector Detector = new ImpactDetector();
            public ClientCar Car = null!;
            public Vector3 Position;
            public Vector3 Velocity;
            public float ForwardSpeed;
            public float Accel;
            public float Throttle;
            public float MaxSpeed;
            public float HeldFor;
            public bool Boosting;
            public bool HasLast;
            public bool HasVelocity;
            public int Resets;
            public int Stamp;

            public void Begin(ClientCar car)
            {
                Car = car;
                Velocity = Vector3.zero;
                ForwardSpeed = 0f;
                Accel = 0f;
                Throttle = 0f;
                HeldFor = 0f;
                HasLast = false;
                HasVelocity = false;
                Resets = car.Vehicle != null ? car.Vehicle.Resets : 0;
            }
        }

        private sealed class EngineVoice
        {
            private readonly AudioSource _engine;
            private readonly AudioSource _boost;
            private readonly int _loopOffset;
            private float _volume;
            private float _pitch = EngineSound.IdlePitch;
            private float _boostVolume;

            public EngineVoice(Transform parent, int index, AudioClip engine, AudioClip boost)
            {
                bool local = index == 0;
                var go = new GameObject(local ? "Engine own" : "Engine " + index);
                go.transform.SetParent(parent, false);
                _engine = Create(go, engine, local, local ? 0 : 64);
                _boost = Create(go, boost, local, local ? 1 : 96);
                // Different loop phases keep several identical engines from phasing into one drone.
                _loopOffset = index * 2711 % engine.samples;
            }

            public CarTrack? Track { get; private set; }

            public float TargetPitch { get; private set; } = EngineSound.IdlePitch;

            public void Assign(CarTrack? track)
            {
                if (track == Track)
                {
                    return;
                }

                Track = track;
                if (track != null)
                {
                    // A new car starts at its own pitch and fades in.
                    _pitch = EngineSound.Pitch(track.ForwardSpeed, track.MaxSpeed, track.Throttle);
                    _volume = 0f;
                    _boostVolume = 0f;
                }
            }

            public void Update(float deltaTime, float gain)
            {
                float targetVolume = 0f;
                float targetBoost = 0f;
                CarTrack? track = Track;
                if (track != null)
                {
                    _engine.transform.position = track.Position;
                    TargetPitch = EngineSound.Pitch(track.ForwardSpeed, track.MaxSpeed, track.Throttle) * (track.Boosting ? 1.06f : 1f);
                    targetVolume = gain * EngineSound.Volume(track.ForwardSpeed, track.MaxSpeed, track.Throttle);
                    targetBoost = track.Boosting ? gain * 0.6f : 0f;
                }

                _pitch += (TargetPitch - _pitch) * (1f - Mathf.Exp(-deltaTime / PitchSeconds));
                float fade = 1f - Mathf.Exp(-deltaTime / FadeSeconds);
                _volume += (targetVolume - _volume) * fade;
                _boostVolume += (targetBoost - _boostVolume) * fade;
                Drive(_engine, _volume, targetVolume, _pitch);
                Drive(_boost, _boostVolume, targetBoost, 0.8f + 0.2f * _pitch);
            }

            public void Silence()
            {
                _engine.Stop();
                _boost.Stop();
                _volume = 0f;
                _boostVolume = 0f;
            }

            private void Drive(AudioSource source, float volume, float target, float pitch)
            {
                if (volume < 0.005f && target <= 0f)
                {
                    if (source.isPlaying)
                    {
                        source.Stop();
                    }

                    return;
                }

                source.volume = volume;
                source.pitch = pitch;
                if (!source.isPlaying)
                {
                    source.Play();
                    source.timeSamples = _loopOffset;
                }
            }

            private static AudioSource Create(GameObject go, AudioClip clip, bool local, int priority)
            {
                AudioSource source = go.AddComponent<AudioSource>();
                source.clip = clip;
                source.loop = true;
                source.playOnAwake = false;
                source.dopplerLevel = 0f;
                source.priority = priority;
                // The own car is just ahead of the chase camera: mostly 2D so it stays steady while turning.
                source.spatialBlend = local ? LocalEngineSpatial : 1f;
                source.rolloffMode = AudioRolloffMode.Linear;
                source.minDistance = EngineMinDistance;
                source.maxDistance = EngineMaxDistance;
                source.volume = 0f;
                return source;
            }
        }
    }
}
