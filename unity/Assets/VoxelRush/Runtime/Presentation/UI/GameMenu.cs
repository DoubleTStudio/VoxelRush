using UnityEngine;
using VoxelRush.Core.Input;
using VoxelRush.Core.Performance;
using VoxelRush.Core.Settings;
using VoxelRush.Networking;
using VoxelRush.Presentation.Input;
using VoxelRush.Presentation.Quality;
using VoxelRush.Presentation.Settings;

namespace VoxelRush.Presentation.UI
{
    /// <summary>
    /// In-game menu (UI-001): a pause button while connected opens settings (steering sensitivity, button size,
    /// graphics, effects volume) and Leave. Settings apply immediately and are saved on close (SAVE-008 LocalSettings).
    /// The game keeps running underneath: it's multiplayer, nothing pauses.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GameMenu : MonoBehaviour
    {
        private static readonly string[] QualityNames = { "Auto", "Low", "Medium", "High" };

        private float _sensitivity;
        private float _buttonScale;
        private int _quality;
        private float _effects;
        private GUIStyle? _button;
        private GUIStyle? _label;

        /// <summary>True while the panel is open; camera gestures ignore the screen meanwhile.</summary>
        public static bool IsOpen { get; private set; }

        public void Open()
        {
            LocalSettings current = DeviceSettings.Current;
            _sensitivity = current.Controls.Sensitivity;
            _buttonScale = current.Controls.ButtonScale;
            _quality = current.QualityOverride is QualityTier tier ? (int)tier + 1 : 0;
            _effects = current.EffectsVolume;
            IsOpen = true;
        }

        /// <summary>Saves and applies the edited settings.</summary>
        public void Close()
        {
            if (!IsOpen)
            {
                return;
            }

            IsOpen = false;
            LocalSettings current = DeviceSettings.Current;
            var controls = new ControlSettings(_sensitivity, _buttonScale);
            QualityTier? tier = _quality == 0 ? (QualityTier?)null : (QualityTier)(_quality - 1);
            var updated = new LocalSettings(controls, tier, current.MusicVolume, _effects);
            DeviceSettings.Save(updated);
            Apply(updated);
        }

        /// <summary>Pushes settings to the live input, HUD, quality and audio.</summary>
        public static void Apply(LocalSettings settings)
        {
            foreach (DrivingInputReader reader in FindObjectsByType<DrivingInputReader>(FindObjectsSortMode.None))
            {
                reader.Apply(settings.Controls);
            }

            foreach (TouchControlsScaler scaler in FindObjectsByType<TouchControlsScaler>(FindObjectsSortMode.None))
            {
                scaler.Apply(settings.Controls);
            }

            QualityController? quality = QualityController.Instance;
            if (quality != null)
            {
                // A fixed choice turns adaptation off; Auto hands control back to it.
                quality.Adaptive = settings.QualityOverride == null;
                quality.ForceTier(settings.QualityOverride ?? quality.ClassifiedTier);
            }

            AudioListener.volume = Mathf.Clamp01(settings.EffectsVolume);
        }

        internal void SetForTest(float sensitivity, float buttonScale, int quality, float effects)
        {
            _sensitivity = sensitivity;
            _buttonScale = buttonScale;
            _quality = quality;
            _effects = effects;
        }

        private void Start() => AudioListener.volume = Mathf.Clamp01(DeviceSettings.Current.EffectsVolume);

        private void OnDisable() => IsOpen = false;

        private void OnGUI()
        {
            ClientBootstrap? bootstrap = ClientBootstrap.Instance;
            if (bootstrap == null || !bootstrap.IsConnected || bootstrap.OnJoinScreen)
            {
                IsOpen = false;
                return;
            }

            float scale = Screen.height / 720f;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float width = Screen.width / scale;
            _button ??= new GUIStyle(GUI.skin.button) { fontSize = 28 };
            _label ??= new GUIStyle(GUI.skin.label) { fontSize = 24 };
            float right = width - (Screen.width - Screen.safeArea.xMax) / scale;
            if (!IsOpen)
            {
                if (GUI.Button(new Rect(right - 96f, 16f, 80f, 64f), "II", _button))
                {
                    Open();
                }

                return;
            }

            GUILayout.BeginArea(new Rect((width - 620f) / 2f, 60f, 620f, 600f), GUI.skin.box);
            GUILayout.Label($"Steering sensitivity  {_sensitivity:0.00}", _label);
            _sensitivity = GUILayout.HorizontalSlider(_sensitivity, ControlSettings.MinSensitivity, ControlSettings.MaxSensitivity, GUILayout.Height(40));
            GUILayout.Label($"Button size  {_buttonScale:0.00}", _label);
            _buttonScale = GUILayout.HorizontalSlider(_buttonScale, ControlSettings.MinButtonScale, ControlSettings.MaxButtonScale, GUILayout.Height(40));
            GUILayout.Label("Graphics", _label);
            _quality = GUILayout.Toolbar(_quality, QualityNames, _button, GUILayout.Height(56));
            GUILayout.Label($"Effects volume  {Mathf.RoundToInt(_effects * 100)}%", _label);
            _effects = GUILayout.HorizontalSlider(_effects, 0f, 1f, GUILayout.Height(40));
            GUILayout.Space(16);
            if (GUILayout.Button("Resume", _button, GUILayout.Height(64)))
            {
                Close();
            }

            if (GUILayout.Button("Leave world", _button, GUILayout.Height(64)))
            {
                Close();
                bootstrap.Leave();
            }

            GUILayout.EndArea();
        }
    }
}
