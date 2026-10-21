using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using VoxelRush.Core.Diagnostics;
using VoxelRush.Core.Performance;
using VoxelRush.Presentation.World;

namespace VoxelRush.Presentation.Quality
{
    /// <summary>
    /// Picks a starting tier from the device (or <c>-qualityTier Low|Medium|High</c>), applies its preset to a
    /// runtime copy of the URP asset (the project asset is never modified), and adapts: steps down on over-budget
    /// frames or thermal headroom, back up after sustained headroom (VR-071).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class QualityController : MonoBehaviour
    {
        public const string TierArgument = "-qualityTier";

        [SerializeField] private bool _adaptive = true;

        private RenderPipelineAsset? _original;
        private UniversalRenderPipelineAsset? _asset;
        private AdaptiveQuality? _controller;
        private float _thermal = float.NaN;
        private float _nextThermalAt;

        public static QualityController? Instance { get; private set; }

        public QualityTier Tier => _controller?.Tier ?? QualityTier.High;

        public QualityTier ClassifiedTier { get; private set; }

        public float LastThermalHeadroom => _thermal;

        public int Changes => _controller?.Changes ?? 0;

        public UniversalRenderPipelineAsset? RuntimeAsset => _asset;

        /// <summary>Tests and the benchmark can switch adaptation off to measure one tier.</summary>
        public bool Adaptive
        {
            get => _adaptive;
            set => _adaptive = value;
        }

        public void ForceTier(QualityTier tier)
        {
            _controller = new AdaptiveQuality(tier);
            Apply(QualityPresets.For(tier));
        }

        private void Awake()
        {
            Instance = this;
            ClassifiedTier = new DeviceProfile(Application.isMobilePlatform, SystemInfo.systemMemorySize, SystemInfo.graphicsMemorySize, SystemInfo.processorCount).Classify();
            QualityTier start = ParseTier(Environment.GetCommandLineArgs()) ?? ClassifiedTier;
            _original = QualitySettings.renderPipeline ?? GraphicsSettings.defaultRenderPipeline;
            if (_original is UniversalRenderPipelineAsset urp)
            {
                _asset = Instantiate(urp);
                _asset.name = urp.name + " (runtime)";
                QualitySettings.renderPipeline = _asset;
            }

            ForceTier(start);
            Debug.Log(LogEvent.Format("quality_tier", ("tier", start), ("classified", ClassifiedTier), ("device", SystemInfo.deviceModel),
                ("memoryMb", SystemInfo.systemMemorySize), ("cores", SystemInfo.processorCount)));
        }

        private void Update()
        {
            if (!_adaptive || _controller == null)
            {
                return;
            }

            if (Time.unscaledTime >= _nextThermalAt)
            {
                _nextThermalAt = Time.unscaledTime + 1f;
                _thermal = ThermalProbe.Headroom();
            }

            if (_controller.Frame(Time.unscaledTimeAsDouble, Time.unscaledDeltaTime * 1000f, _thermal))
            {
                Apply(QualityPresets.For(_controller.Tier));
                Debug.Log(LogEvent.Format("quality_tier_changed", ("tier", _controller.Tier), ("thermalHeadroom", _thermal), ("changes", _controller.Changes)));
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }

            // Restore the project asset so editor sessions and tests never keep the runtime copy.
            if (_asset != null)
            {
                QualitySettings.renderPipeline = _original;
                Destroy(_asset);
            }
        }

        private void Apply(QualityPreset preset)
        {
            if (_asset != null)
            {
                _asset.renderScale = preset.RenderScale;
                _asset.shadowDistance = preset.ShadowDistance;
                _asset.msaaSampleCount = preset.Msaa;
            }

            Application.targetFrameRate = preset.TargetFrameRate;
            foreach (DebrisEffects debris in FindObjectsByType<DebrisEffects>(FindObjectsSortMode.None))
            {
                if (debris.Capacity != preset.DebrisPool)
                {
                    debris.Configure(preset.DebrisPool, new VoxelRush.Destruction.DebrisPlanner());
                }
            }
        }

        private static QualityTier? ParseTier(string[] args)
        {
            int i = Array.IndexOf(args, TierArgument);
            return i >= 0 && i + 1 < args.Length && Enum.TryParse(args[i + 1], true, out QualityTier tier) ? tier : (QualityTier?)null;
        }
    }
}
