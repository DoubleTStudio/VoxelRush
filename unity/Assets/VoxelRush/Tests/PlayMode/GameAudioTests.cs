using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using VoxelRush.Core.Input;
using VoxelRush.Core.Settings;
using VoxelRush.Destruction;
using VoxelRush.Networking.Vehicles;
using VoxelRush.Presentation.Audio;
using VoxelRush.Presentation.UI;
using VoxelRush.Presentation.World;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;
using VoxelRush.World.Generation;
using VoxelRush.World.Sync;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace VoxelRush.Tests.PlayMode
{
    /// <summary>AUDIO-001: synthesised SFX, engine mapping, voice and rate limits, build/crunch hooks, volume, no GC.</summary>
    public sealed class GameAudioTests
    {
        private const float Dt = 1f / 60f;

        private readonly List<GameObject> _objects = new List<GameObject>();
        private float _now;

        [SetUp]
        public void SetUp()
        {
            AudioListener.volume = 1f;
            AudioListener.pause = false;
            _now = 100f;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _objects)
            {
                if (go != null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }

            _objects.Clear();
            AudioListener.volume = 1f;
            AudioListener.pause = false;
        }

        private static IEnumerable<TestCaseData> Clips()
        {
            yield return new TestCaseData((Func<float[]>)SfxSynth.EngineLoop, SfxSynth.EngineLoopSeconds).SetName("Engine");
            yield return new TestCaseData((Func<float[]>)SfxSynth.BoostLoop, SfxSynth.BoostLoopSeconds).SetName("Boost");
            yield return new TestCaseData((Func<float[]>)SfxSynth.Impact, SfxSynth.ImpactSeconds).SetName("Impact");
            yield return new TestCaseData((Func<float[]>)SfxSynth.Place, SfxSynth.PlaceSeconds).SetName("Place");
            yield return new TestCaseData((Func<float[]>)SfxSynth.Remove, SfxSynth.RemoveSeconds).SetName("Remove");
            yield return new TestCaseData((Func<float[]>)SfxSynth.Nope, SfxSynth.NopeSeconds).SetName("Nope");
            yield return new TestCaseData((Func<float[]>)SfxSynth.Crunch, SfxSynth.CrunchSeconds).SetName("Crunch");
        }

        [TestCaseSource(nameof(Clips))]
        public void Synthesis_IsAudibleBoundedDeterministicAndOfExpectedLength(Func<float[]> synth, float seconds)
        {
            float[] samples = synth();

            Assert.That(samples.Length, Is.EqualTo(Mathf.RoundToInt(seconds * SfxSynth.SampleRate)));
            Assert.That(Array.TrueForAll(samples, v => !float.IsNaN(v) && !float.IsInfinity(v)), "finite");
            Assert.That(SfxSynth.Peak(samples), Is.LessThanOrEqualTo(1f), "no clipping");
            Assert.That(SfxSynth.Rms(samples), Is.GreaterThan(0.05f), "not silent");
            Assert.That(synth(), Is.EqualTo(samples), "same sound on every device");

            AudioClip clip = SfxSynth.ToClip("Test", samples);
            try
            {
                Assert.That(clip.samples, Is.EqualTo(samples.Length));
                Assert.That(clip.channels, Is.EqualTo(1));
                Assert.That(clip.length, Is.EqualTo(seconds).Within(1e-3f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void Loops_WrapWithoutAClick()
        {
            foreach (float[] loop in new[] { SfxSynth.EngineLoop(), SfxSynth.BoostLoop() })
            {
                float maxStep = 0f;
                for (int i = 1; i < loop.Length; i++)
                {
                    maxStep = Mathf.Max(maxStep, Mathf.Abs(loop[i] - loop[i - 1]));
                }

                Assert.That(Mathf.Abs(loop[0] - loop[loop.Length - 1]), Is.LessThanOrEqualTo(maxStep * 1.5f));
            }
        }

        [Test]
        public void EnginePitch_RisesMonotonicallyWithSpeed_AndThrottleLiftsIt()
        {
            const float max = 28f;
            foreach (float throttle in new[] { 0f, 0.5f, 1f })
            {
                float previous = float.NegativeInfinity;
                for (float speed = 0f; speed <= 45f; speed += 0.25f)
                {
                    float pitch = EngineSound.Pitch(speed, max, throttle);
                    Assert.That(pitch, Is.GreaterThanOrEqualTo(previous), $"speed {speed} throttle {throttle}");
                    Assert.That(EngineSound.Pitch(-speed, max, throttle), Is.EqualTo(pitch), "reverse sounds the same");
                    previous = pitch;
                }

                Assert.That(EngineSound.Pitch(max, max, throttle), Is.GreaterThan(EngineSound.Pitch(max / 2f, max, throttle)));
            }

            Assert.That(EngineSound.Pitch(0f, max, 0f), Is.EqualTo(EngineSound.IdlePitch));
            Assert.That(EngineSound.Pitch(10f, max, 1f), Is.GreaterThan(EngineSound.Pitch(10f, max, 0f)));
            Assert.That(EngineSound.Pitch(float.NaN, max, 0f), Is.EqualTo(EngineSound.IdlePitch));
        }

        [Test]
        public void OwnEngine_PitchFollowsTheCarsSpeed()
        {
            GameAudio audio = Create(out List<ClientCar> cars);
            Transform own = Car(cars, Vector3.zero, isLocal: true);
            float last = 0f;
            for (int frame = 0; frame < 120; frame++)
            {
                float speed = frame * 0.25f;   // accelerating to 30 m/s
                own.position += Vector3.forward * speed * Dt;
                Tick(audio);
                if (frame % 20 == 19)
                {
                    float pitch = audio.EnginePitch(0);
                    Assert.That(pitch, Is.GreaterThan(last), $"frame {frame}");
                    last = pitch;
                }
            }

            Assert.That(audio.EngineCar(0)?.Transform, Is.SameAs(own));
        }

        [Test]
        public void VoicePicker_PicksNearestFirst()
        {
            float[] distances = { 25f, 4f, float.PositiveInfinity, 9f, 1f, 16f, 4f };
            var picked = new int[3];

            Assert.That(VoicePicker.PickNearest(distances, distances.Length, picked, 3), Is.EqualTo(3));
            Assert.That(picked, Is.EqualTo(new[] { 4, 1, 6 }), "ties keep the lower index");
            Assert.That(VoicePicker.PickNearest(distances, 3, picked, 3), Is.EqualTo(2), "unreachable candidates are skipped");
            Assert.That(new[] { picked[0], picked[1] }, Is.EqualTo(new[] { 1, 0 }));
            Assert.That(VoicePicker.PickNearest(distances, distances.Length, picked, 0), Is.Zero);
        }

        [Test]
        public void RemoteEngines_AreLimitedToTheNearestCars_AndStickWhileNearest()
        {
            GameAudio audio = Create(out List<ClientCar> cars);
            Car(cars, Vector3.zero, isLocal: true);
            var remotes = new List<Transform>();
            foreach (float z in new[] { 50f, 10f, 30f, 5f, 40f, 20f, 200f })
            {
                remotes.Add(Car(cars, new Vector3(3f, 0f, z), isLocal: false));
            }

            Tick(audio);
            Assert.That(audio.EngineVoices, Is.EqualTo(5), "own car + 4 remote");
            Assert.That(audio.EngineCar(0)!.IsLocal, Is.True);
            // Compared by name: NUnit treats a Transform as the (empty) collection of its children.
            Assert.That(Voiced(audio), Is.EquivalentTo(Names(remotes[1], remotes[2], remotes[3], remotes[5])));
            Transform? firstVoice = audio.EngineCar(1)?.Transform;

            // The far car drives past the listener: it takes the voice of the car that is now fifth.
            remotes[6].position = new Vector3(3f, 0f, 1f);
            Tick(audio);
            Assert.That(Voiced(audio), Is.EquivalentTo(Names(remotes[1], remotes[3], remotes[5], remotes[6])));
            Assert.That(audio.EngineCar(1)?.Transform, Is.SameAs(firstVoice == remotes[2] ? remotes[6] : firstVoice), "kept voices don't move");

            cars.RemoveAt(cars.Count - 1);
            Tick(audio);
            Assert.That(Voiced(audio), Has.No.Member(remotes[6].name), "a despawned car frees its voice");
            Assert.That(Voiced(audio), Has.Count.EqualTo(4), "the next nearest takes the voice");
        }

        [Test]
        public void ImpactDetector_IgnoresDrivingAndRateLimitsHits()
        {
            var detector = new ImpactDetector();
            float now = 0f;
            float Step(Vector3 v) => detector.Step(v, Dt, now += Dt);

            // Hard braking (12 m/s²) and boost (13 m/s²) never sound like a crash.
            var v = new Vector3(0f, 0f, 30f);
            for (int i = 0; i < 120; i++)
            {
                v.z = Mathf.Max(0f, v.z - 12f * Dt);
                Assert.That(Step(v), Is.Zero, "braking");
            }

            for (int i = 0; i < 120; i++)
            {
                v.z += 13f * Dt;
                Assert.That(Step(v), Is.Zero, "accelerating");
            }

            float hard = Step(new Vector3(0f, 0f, 2f));
            Assert.That(hard, Is.GreaterThan(0.5f), "wall hit");
            Assert.That(Step(new Vector3(0f, 0f, 20f)), Is.Zero, "cooldown");
            for (int i = 0; i < 30; i++)
            {
                Step(new Vector3(0f, 0f, 20f));
            }

            Assert.That(Step(new Vector3(0f, 0f, 14f)), Is.GreaterThan(0f).And.LessThan(hard), "a softer hit is quieter");
            for (int i = 0; i < 30; i++)
            {
                Step(new Vector3(0f, 0f, 14f));
            }

            Assert.That(Step(new Vector3(0f, 0f, 14f + ImpactDetector.TeleportChange + 1f)), Is.Zero, "respawn/teleport");

            var limiter = new RateLimiter(perSecond: 4f, burst: 2f);
            Assert.That(limiter.TryAcquire(0f) && limiter.TryAcquire(0f), Is.True);
            Assert.That(limiter.TryAcquire(0.1f), Is.False, "burst spent");
            Assert.That(limiter.TryAcquire(0.3f), Is.True, "refilled");
        }

        [Test]
        public void RemoteCrash_PlaysOneImpact_AndSimultaneousCrashesAreRateLimited()
        {
            GameAudio audio = Create(out List<ClientCar> cars);
            var remotes = new List<Transform>();
            for (int i = 0; i < 5; i++)
            {
                remotes.Add(Car(cars, new Vector3(i * 4f, 0f, 0f), isLocal: false));
            }

            void Drive(int frames, Func<int, float> speed)
            {
                for (int f = 0; f < frames; f++)
                {
                    foreach (Transform t in remotes)
                    {
                        t.position += Vector3.forward * speed(f) * Dt;
                    }

                    Tick(audio);
                }
            }

            Drive(90, _ => 20f);
            Assert.That(audio.ImpactsPlayed, Is.Zero, "cruising");
            // Interpolated wall hit: 20 m/s to rest within ~4 frames, then the snapshots stop moving.
            Drive(4, f => 20f - 5f * (f + 1));
            Drive(30, _ => 0f);
            Assert.That(audio.ImpactsPlayed, Is.EqualTo(2), "5 simultaneous crashes, burst of 2");

            // A held snapshot (no movement for a few frames) then catch-up is not a crash.
            remotes.RemoveRange(1, 4);
            Drive(120, _ => 20f);
            long before = audio.ImpactsPlayed;
            Drive(5, _ => 0f);
            remotes[0].position += Vector3.forward * 20f * 5 * Dt;
            Drive(60, _ => 20f);
            Assert.That(audio.ImpactsPlayed, Is.EqualTo(before));
        }

        [Test]
        public void BuildResults_MapToConfirmOrNope()
        {
            Assert.That(BuildSounds.For(EditAction.Place, EditRejection.None), Is.EqualTo(BuildSound.Place));
            Assert.That(BuildSounds.For(EditAction.Remove, EditRejection.None), Is.EqualTo(BuildSound.Remove));
            foreach (EditRejection rejection in Enum.GetValues(typeof(EditRejection)))
            {
                if (rejection != EditRejection.None)
                {
                    Assert.That(BuildSounds.For(EditAction.Place, rejection), Is.EqualTo(BuildSound.Nope), rejection.ToString());
                    Assert.That(BuildSounds.For(EditAction.Remove, rejection), Is.EqualTo(BuildSound.Nope), rejection.ToString());
                }
            }

            GameAudio audio = Create(out _);
            Tick(audio);
            audio.PlayBuildResult(EditAction.Remove, EditRejection.None);
            Assert.That(audio.LastBuildSound, Is.EqualTo(BuildSound.Remove));
            audio.PlayBuildResult(EditAction.Place, EditRejection.OutOfReach);
            Assert.That(audio.LastBuildSound, Is.EqualTo(BuildSound.Nope));
            audio.PlayBuildResult(EditAction.Place, EditRejection.None);
            Assert.That(audio.LastBuildSound, Is.EqualTo(BuildSound.Place));
            Assert.That(audio.BuildSoundsPlayed, Is.EqualTo(3));
        }

        [Test]
        public void Blast_PlaysCappedCrunches_AlongsideDebris()
        {
            GameAudio audio = Create(out _);
            var debris = audio.gameObject.AddComponent<DebrisEffects>();
            debris.Configure(64, new DebrisPlanner(piecesPerBlock: 3, maxPiecesPerFrame: 96));
            ClientWorldReplica replica = Blastable(out WorldAuthority server, out _);
            debris.Attach(replica);
            audio.Listen(replica);

            ExplosionOutcome blast = new ExplosionResolver(new ExplosionModel(), ZoneMap.Empty, new DamageLedger(MaterialCatalog.Default))
                .Explode(server, Guid.NewGuid(), "x", 0f, -1f, 0f, 4f, 400, 1);
            foreach (ChunkMutationBatch b in blast.Commit.Batches)
            {
                replica.ApplyDelta(b);
            }

            Tick(audio);
            Assert.That(blast.Destroyed, Is.GreaterThan(20));
            Assert.That(debris.Spawned, Is.GreaterThan(0), "debris still gets the hook");
            Assert.That(audio.CrunchesPlayed, Is.EqualTo(2), "per-frame cap, not one per block");
            Tick(audio);
            Assert.That(audio.CrunchesPlayed, Is.EqualTo(2), "the queue is drained");

            debris.Detach();
            Assert.That(replica.BlockChanged, Is.Not.Null, "detaching debris leaves audio subscribed");
            audio.Listen(null);
            Assert.That(replica.BlockChanged, Is.Null);
        }

        [Test]
        public void EffectsVolume_FromGameMenu_MutesAndUnmutes_AndPauseSilences()
        {
            GameAudio audio = Create(out List<ClientCar> cars);
            Car(cars, Vector3.zero, isLocal: true);

            GameMenu.Apply(new LocalSettings(ControlSettings.Default, null, 1f, 0f));
            Tick(audio);
            Assert.That(audio.Muted, Is.True, "zero effects volume");
            audio.PlayBuildResult(EditAction.Place, EditRejection.None);
            Assert.That(audio.BuildSoundsPlayed, Is.Zero);

            GameMenu.Apply(new LocalSettings(ControlSettings.Default, null, 1f, 0.6f));
            Tick(audio);
            Assert.That(AudioListener.volume, Is.EqualTo(0.6f).Within(1e-4f), "effects volume is the listener volume");
            Assert.That(audio.Muted, Is.False);
            audio.PlayBuildResult(EditAction.Place, EditRejection.None);
            Assert.That(audio.BuildSoundsPlayed, Is.EqualTo(1));

            audio.SetAppPaused(true);
            Tick(audio);
            Assert.That(AudioListener.pause, Is.True);
            Assert.That(audio.Muted, Is.True, "backgrounded");
            audio.SetAppPaused(false);
            Tick(audio);
            Assert.That(AudioListener.pause, Is.False);
            Assert.That(audio.Muted, Is.False);
        }

        [Test]
        public void SteadyState_Frames_DoNotAllocate()
        {
            GameAudio audio = Create(out List<ClientCar> cars);
            ClientWorldReplica replica = Blastable(out _, out ChunkCoord chunk);
            audio.Listen(replica);
            Transform own = Car(cars, Vector3.zero, isLocal: true);
            var remotes = new List<Transform>();
            for (int i = 0; i < 6; i++)
            {
                remotes.Add(Car(cars, new Vector3(i * 6f, 0f, 10f), isLocal: false));
            }

            ushort index = (ushort)new LocalBlock(1, 15, 1).Index;
            void Frames()
            {
                for (int f = 0; f < 60; f++)
                {
                    // Cruise, then a sharp stop half way: impact, engine and crunch paths all run.
                    float speed = f < 30 ? 20f : f < 33 ? 20f - 6f * (f - 29) : 0.5f;
                    own.position += Vector3.forward * speed * Dt;
                    for (int r = 0; r < remotes.Count; r++)
                    {
                        remotes[r].position += Vector3.forward * speed * Dt;
                    }

                    if (f % 10 == 0)
                    {
                        replica.BlockChanged!.Invoke(chunk, index, BlockIds.Stone, BlockIds.Air);
                        audio.PlayBuildResult(EditAction.Place, f % 20 == 0 ? EditRejection.None : EditRejection.OutOfReach);
                    }

                    Tick(audio);
                }
            }

            Frames();   // warm up pools and first-use paths
            _now += 5f;
            long impacts = audio.ImpactsPlayed;
            long crunches = audio.CrunchesPlayed;

            Assert.That(() => Frames(), Is.Not.AllocatingGCMemory());
            Assert.That(audio.ImpactsPlayed, Is.GreaterThan(impacts), "the impact path ran");
            Assert.That(audio.CrunchesPlayed, Is.GreaterThan(crunches), "the crunch path ran");
        }

        private GameAudio Create(out List<ClientCar> cars)
        {
            var go = new GameObject("AudioTest");
            _objects.Add(go);
            var listener = new GameObject("Listener").transform;
            _objects.Add(listener.gameObject);
            GameAudio audio = go.AddComponent<GameAudio>();
            cars = new List<ClientCar>();
            audio.CarsOverride = cars;
            audio.ListenerOverride = listener;
            audio.Listen(null);
            return audio;
        }

        private Transform Car(List<ClientCar> cars, Vector3 position, bool isLocal)
        {
            var go = new GameObject((isLocal ? "Own " : "Remote ") + cars.Count);
            _objects.Add(go);
            go.transform.position = position;
            cars.Add(new ClientCar(go.transform, null, isLocal));
            return go.transform;
        }

        private void Tick(GameAudio audio)
        {
            _now += Dt;
            audio.Tick(Dt, _now);
        }

        private static List<string> Voiced(GameAudio audio)
        {
            var voiced = new List<string>();
            for (int v = 1; v < audio.EngineVoices; v++)
            {
                if (audio.EngineCar(v) is ClientCar car)
                {
                    voiced.Add(car.Transform.name);
                }
            }

            return voiced;
        }

        private static string[] Names(params Transform[] cars) => Array.ConvertAll(cars, t => t.name);

        private static ClientWorldReplica Blastable(out WorldAuthority server, out ChunkCoord first)
        {
            var gen = new FlatPlotGenerator();
            var coords = new[] { new ChunkCoord(-1, -1, -1), new ChunkCoord(-1, -1, 0), new ChunkCoord(0, -1, -1), new ChunkCoord(0, -1, 0) };
            server = new WorldAuthority(new EditValidator(ZoneMap.Empty));
            var replica = new ClientWorldReplica(new WorldInfo(Guid.NewGuid(), GeneratorKind.FlatPlot, 0, FlatPlotGenerator.Version, 1, coords[0], coords[3]));
            foreach (ChunkCoord c in coords)
            {
                server.AddChunk(new ChunkData(c, gen.GeneratorVersion, gen.GenerateBlocks(c)));
                replica.ApplySnapshot(ChunkSnapshotMessage.From(server.GetChunk(c)!));
            }

            first = coords[3];
            return replica;
        }
    }
}
