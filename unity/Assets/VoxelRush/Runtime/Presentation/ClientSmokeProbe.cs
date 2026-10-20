using UnityEngine;
using UnityEngine.Rendering;
using VoxelRush.Core;
using VoxelRush.Core.Diagnostics;

namespace VoxelRush.Presentation
{
    /// <summary>Reports that the client scene booted with the expected render pipeline; used by smoke tests and device logs.</summary>
    [DisallowMultipleComponent]
    public sealed class ClientSmokeProbe : MonoBehaviour
    {
        public bool IsReady { get; private set; }

        public string RenderPipelineName { get; private set; } = string.Empty;

        private void Start()
        {
            RenderPipelineAsset? pipeline = GraphicsSettings.currentRenderPipeline;
            RenderPipelineName = pipeline != null ? pipeline.GetType().Name : "BuiltIn";
            Rect safeArea = Screen.safeArea;

            Debug.Log(LogEvent.Format(
                "client_smoke_ready",
                ("platform", Application.platform),
                ("unityVersion", Application.unityVersion),
                ("protocolVersion", SimulationConstants.ProtocolVersion),
                ("renderPipeline", RenderPipelineName),
                ("screen", $"{Screen.width}x{Screen.height}"),
                ("orientation", Screen.orientation),
                ("safeArea", $"{safeArea.x:0},{safeArea.y:0},{safeArea.width:0},{safeArea.height:0}")));

            IsReady = true;
        }
    }
}
