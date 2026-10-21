using UnityEngine;
using VoxelRush.Core.Input;

namespace VoxelRush.Presentation.UI
{
    /// <summary>Keeps this RectTransform inside the device safe area (notches, home indicator); re-fits on rotation/resize.</summary>
    [RequireComponent(typeof(RectTransform))]
    [DisallowMultipleComponent]
    public sealed class SafeAreaFitter : MonoBehaviour
    {
        private RectTransform _rect = null!;
        private Rect _appliedSafeArea;
        private Vector2Int _appliedScreen;

        public SafeAreaLayout Applied { get; private set; } = SafeAreaLayout.Full;

        private void Awake() => _rect = (RectTransform)transform;

        private void OnEnable() => Fit(force: true);

        private void Update() => Fit(force: false);

        private void Fit(bool force)
        {
            Rect safe = Screen.safeArea;
            var screen = new Vector2Int(Screen.width, Screen.height);
            if (!force && safe == _appliedSafeArea && screen == _appliedScreen)
            {
                return;
            }

            _appliedSafeArea = safe;
            _appliedScreen = screen;
            Applied = SafeAreaLayout.Compute(safe.x, safe.y, safe.width, safe.height, screen.x, screen.y);
            _rect.anchorMin = new Vector2(Applied.MinX, Applied.MinY);
            _rect.anchorMax = new Vector2(Applied.MaxX, Applied.MaxY);
            _rect.offsetMin = Vector2.zero;
            _rect.offsetMax = Vector2.zero;
        }
    }
}
