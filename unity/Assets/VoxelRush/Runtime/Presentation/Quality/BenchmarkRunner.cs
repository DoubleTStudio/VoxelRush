using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Profiling;
using VoxelRush.Core.Diagnostics;
using VoxelRush.Core.Performance;
using VoxelRush.Presentation.World;
using VoxelRush.World.Chunks;

namespace VoxelRush.Presentation.Quality
{
    /// <summary>
    /// Device benchmark (<c>-benchmarkSeconds N</c>, optional <c>-benchmarkWarmup S</c>): renders the voxel terrain
    /// from a camera orbiting at driving height, bursts debris every two seconds, records frame times after a warm-up
    /// and writes a JSON result (frames p50/p95/p99, fps, over-budget %, tier changes, thermal, memory, GC) to the
    /// persistent data path, then quits. Thermal soak = a long run (e.g. 1800 s) with adaptation on.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BenchmarkRunner : MonoBehaviour
    {
        public const string SecondsArgument = "-benchmarkSeconds";
        public const string WarmupArgument = "-benchmarkWarmup";

        [SerializeField] private VoxelTerrainView? _terrain;
        [SerializeField] private DebrisEffects? _debris;
        [SerializeField] private Camera? _camera;

        public string? LastResultJson { get; private set; }

        public string? LastResultPath { get; private set; }

        public void Configure(VoxelTerrainView terrain, DebrisEffects? debris, Camera camera)
        {
            _terrain = terrain;
            _debris = debris;
            _camera = camera;
        }

        private void Start()
        {
            string[] args = Environment.GetCommandLineArgs();
            if (TryArg(args, SecondsArgument, out float seconds))
            {
                TryArg(args, WarmupArgument, out float warmup);
                StartCoroutine(Run(seconds, warmup > 0 ? warmup : 5f, quit: !Application.isEditor));
            }
        }

        public IEnumerator Run(float seconds, float warmup, bool quit = false)
        {
            if (_terrain == null || _camera == null)
            {
                Debug.LogError(LogEvent.Format("benchmark_misconfigured"));
                yield break;
            }

            _terrain.gameObject.SetActive(true);
            _terrain.Build();
            QualityController? quality = QualityController.Instance;
            QualityTier startTier = quality?.Tier ?? QualityTier.High;
            float thermalStart = ThermalProbe.Headroom();
            int gcStart = GC.CollectionCount(0);
            var frames = new List<float>(Mathf.CeilToInt(seconds * 120));
            float started = Time.unscaledTime;
            float nextBurst = started;
            var rng = new System.Random(1);
            Debug.Log(LogEvent.Format("benchmark_started", ("seconds", seconds), ("warmup", warmup), ("tier", startTier), ("device", SystemInfo.deviceModel)));
            while (Time.unscaledTime - started < warmup + seconds)
            {
                float t = Time.unscaledTime - started;
                // Orbit the terrain at driving height, looking slightly down at the ground ahead.
                var center = new Vector3(0f, 0f, 0f);
                var eye = center + new Vector3(Mathf.Cos(t * 0.15f) * 24f, 12f, Mathf.Sin(t * 0.15f) * 24f);
                _camera.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(center + Vector3.up * 2f - eye));
                if (_debris != null && Time.unscaledTime >= nextBurst)
                {
                    nextBurst = Time.unscaledTime + 2f;
                    for (int i = 0; i < 24; i++)
                    {
                        _debris.SpawnFor(new BlockPos(rng.Next(-16, 16), rng.Next(2, 8), rng.Next(-16, 16)), BlockIds.Stone);
                    }
                }

                yield return null;
                if (t >= warmup)
                {
                    frames.Add(Time.unscaledDeltaTime * 1000f);
                }
            }

            QualityTier endTier = quality?.Tier ?? startTier;
            FrameStats stats = FrameStats.Summarize(frames, QualityPresets.For(endTier).FrameBudgetMs);
            LastResultJson = ToJson(stats, startTier, endTier, quality?.Changes ?? 0, thermalStart, ThermalProbe.Headroom(), GC.CollectionCount(0) - gcStart, seconds);
            LastResultPath = Path.Combine(Application.persistentDataPath, $"benchmark-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(LastResultPath, LastResultJson);
            Debug.Log(LogEvent.Format("benchmark_result", ("p50Ms", stats.P50), ("p95Ms", stats.P95), ("p99Ms", stats.P99), ("fps", Math.Round(stats.MeanFps, 1)),
                ("overBudgetPercent", stats.OverBudgetPercent), ("tierStart", startTier), ("tierEnd", endTier), ("path", LastResultPath)));
            if (quit)
            {
                Application.Quit(0);
            }
        }

        private static string ToJson(FrameStats s, QualityTier start, QualityTier end, int changes, float thermalStart, float thermalEnd, int gcCollections, float seconds)
        {
            string F(double v) => double.IsNaN(v) ? "null" : v.ToString("0.###", CultureInfo.InvariantCulture);
            return "{\n" +
                $"  \"device\": \"{Escape(SystemInfo.deviceModel)}\",\n  \"os\": \"{Escape(SystemInfo.operatingSystem)}\",\n" +
                $"  \"gpu\": \"{Escape(SystemInfo.graphicsDeviceName)}\",\n  \"memoryMb\": {SystemInfo.systemMemorySize},\n  \"cores\": {SystemInfo.processorCount},\n" +
                $"  \"unity\": \"{Application.unityVersion}\",\n  \"seconds\": {F(seconds)},\n  \"frames\": {s.Frames},\n" +
                $"  \"p50Ms\": {F(s.P50)},\n  \"p95Ms\": {F(s.P95)},\n  \"p99Ms\": {F(s.P99)},\n  \"maxMs\": {F(s.Max)},\n" +
                $"  \"meanFps\": {F(s.MeanFps)},\n  \"overBudgetPercent\": {F(s.OverBudgetPercent)},\n" +
                $"  \"tierStart\": \"{start}\",\n  \"tierEnd\": \"{end}\",\n  \"tierChanges\": {changes},\n" +
                $"  \"thermalHeadroomStart\": {F(thermalStart)},\n  \"thermalHeadroomEnd\": {F(thermalEnd)},\n" +
                $"  \"gcCollections\": {gcCollections},\n  \"allocatedMb\": {F(Profiler.GetTotalAllocatedMemoryLong() / 1048576.0)}\n}}\n";
        }

        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static bool TryArg(string[] args, string name, out float value)
        {
            value = 0;
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0;
        }
    }
}
