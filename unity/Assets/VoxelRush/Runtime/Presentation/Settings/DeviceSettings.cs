using UnityEngine;
using VoxelRush.Core.Diagnostics;
using VoxelRush.Core.Settings;

namespace VoxelRush.Presentation.Settings
{
    /// <summary>SAVE-008: the device's <see cref="LocalSettings"/>, loaded once from persistentDataPath (defaults in batch mode).</summary>
    public static class DeviceSettings
    {
        private static LocalSettings? _current;

        public static LocalSettings Current
        {
            get
            {
                if (_current == null)
                {
                    // Headless bots and servers stay deterministic: no per-machine preferences.
                    _current = Application.isBatchMode ? LocalSettings.Default : LocalSettings.Load(Application.persistentDataPath);
                    Debug.Log(LogEvent.Format("settings_loaded", ("source", _current.Source), ("quality", _current.QualityOverride?.ToString() ?? "auto")));
                }

                return _current;
            }
        }

        public static void Save(LocalSettings settings)
        {
            _current = settings;
            try
            {
                settings.Save(Application.persistentDataPath);
            }
            catch (System.IO.IOException e)
            {
                Debug.LogWarning(LogEvent.Format("settings_save_failed", ("error", e.Message)));
            }
        }
    }
}
