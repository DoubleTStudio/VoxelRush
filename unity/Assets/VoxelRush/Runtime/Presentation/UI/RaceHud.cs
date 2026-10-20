using UnityEngine;
using UnityEngine.UI;
using VoxelRush.Core.Race;
using VoxelRush.Networking.Race;

namespace VoxelRush.Presentation.UI
{
    /// <summary>Top-centre race HUD: countdown, checkpoint progress, feedback and results, all from server state.</summary>
    [DisallowMultipleComponent]
    public sealed class RaceHud : MonoBehaviour
    {
        private Text? _title;
        private Text? _detail;
        private RacePhase? _phase;
        private float _phaseStartedAt;

        /// <summary>Defaults to the live network race client; tests inject a fake.</summary>
        public IRaceView? Source { get; set; }

        public string TitleText => _title != null ? _title.text : string.Empty;

        public string DetailText => _detail != null ? _detail.text : string.Empty;

        private void Awake()
        {
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _title = CreateText("Title", font, 64, new Vector2(0f, -40f), 90f);
            _detail = CreateText("Detail", font, 32, new Vector2(0f, -140f), 360f);
        }

        private void Update()
        {
            IRaceView? view = Source ?? RaceClient.Current;
            RacePhase? phase = view?.State?.State;
            if (phase != _phase)
            {
                _phase = phase;
                _phaseStartedAt = Time.unscaledTime;
            }

            (string title, string detail) = view == null ? (string.Empty, string.Empty) : RaceHudModel.Describe(view, Time.unscaledTime - _phaseStartedAt);
            _title!.text = title;
            _detail!.text = detail;
        }

        private Text CreateText(string name, Font font, int size, Vector2 offset, float height)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(transform, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = offset;
            rect.sizeDelta = new Vector2(900f, height);
            var text = go.AddComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.fontStyle = FontStyle.Bold;
            text.alignment = TextAnchor.UpperCenter;
            text.color = Color.white;
            text.raycastTarget = false;
            // Outline keeps it readable over bright sky and terrain.
            go.AddComponent<Outline>().effectColor = new Color(0f, 0f, 0f, 0.8f);
            return text;
        }
    }
}
