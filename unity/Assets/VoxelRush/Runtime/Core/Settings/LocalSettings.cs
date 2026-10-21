using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using VoxelRush.Core.Input;
using VoxelRush.Core.Performance;

namespace VoxelRush.Core.Settings
{
    /// <summary>
    /// SAVE-008 device-local preferences (SAVE_GAME: control/graphics settings stay on the device; nothing
    /// authoritative). A small versioned key=value file: unknown keys are ignored, bad values fall back to defaults,
    /// and a file from a newer format is not misread (defaults, file left alone until the player saves).
    /// </summary>
    public sealed class LocalSettings
    {
        public const int FormatVersion = 1;
        public const string FileName = "settings.txt";

        public static LocalSettings Default { get; } = new LocalSettings(ControlSettings.Default, null, 1f, 1f);

        public LocalSettings(ControlSettings controls, QualityTier? qualityOverride, float musicVolume, float effectsVolume, bool vibration = true)
        {
            Controls = controls;
            QualityOverride = qualityOverride;
            MusicVolume = Clamp01(musicVolume);
            EffectsVolume = Clamp01(effectsVolume);
            Vibration = vibration;
        }

        public ControlSettings Controls { get; }

        /// <summary>Null = adaptive quality (VR-071).</summary>
        public QualityTier? QualityOverride { get; }

        public float MusicVolume { get; }

        public float EffectsVolume { get; }

        /// <summary>Haptic cues on checkpoints and finish (RACE-003 accessibility toggle).</summary>
        public bool Vibration { get; }

        /// <summary>Where the last load came from: "file", "defaults", "corrupt" or "newer_format".</summary>
        public string Source { get; private set; } = "defaults";

        public static LocalSettings Load(string directory)
        {
            string path = Path.Combine(directory, FileName);
            if (!File.Exists(path))
            {
                return Default;
            }

            Dictionary<string, string> values;
            try
            {
                values = Parse(File.ReadAllLines(path));
            }
            catch (IOException)
            {
                return WithSource(Default, "corrupt");
            }

            if (!values.TryGetValue("version", out string? v) || !int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int version) || version < 1)
            {
                return WithSource(Default, "corrupt");
            }

            if (version > FormatVersion)
            {
                return WithSource(Default, "newer_format");
            }

            float sensitivity = Float(values, "controls.sensitivity", ControlSettings.MinSensitivity, ControlSettings.MaxSensitivity, ControlSettings.Default.Sensitivity);
            float scale = Float(values, "controls.buttonScale", ControlSettings.MinButtonScale, ControlSettings.MaxButtonScale, ControlSettings.Default.ButtonScale);
            QualityTier? tier = values.TryGetValue("quality.tier", out string? t) && Enum.TryParse(t, ignoreCase: true, out QualityTier parsed)
                && Enum.IsDefined(typeof(QualityTier), parsed) ? parsed : (QualityTier?)null;
            var settings = new LocalSettings(new ControlSettings(sensitivity, scale), tier,
                Float(values, "audio.music", 0, 1, 1), Float(values, "audio.effects", 0, 1, 1), Bool(values, "accessibility.vibration", true));
            return WithSource(settings, "file");
        }

        /// <summary>Atomic write (temp + rename), so a crash mid-save leaves the previous file.</summary>
        public void Save(string directory)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, FileName);
            string temp = path + ".tmp";
            File.WriteAllLines(temp, new[]
            {
                "version=" + FormatVersion.ToString(CultureInfo.InvariantCulture),
                "controls.sensitivity=" + Controls.Sensitivity.ToString("R", CultureInfo.InvariantCulture),
                "controls.buttonScale=" + Controls.ButtonScale.ToString("R", CultureInfo.InvariantCulture),
                "quality.tier=" + (QualityOverride?.ToString() ?? "auto"),
                "audio.music=" + MusicVolume.ToString("R", CultureInfo.InvariantCulture),
                "audio.effects=" + EffectsVolume.ToString("R", CultureInfo.InvariantCulture),
                "accessibility.vibration=" + (Vibration ? "on" : "off"),
            });
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }

        private static LocalSettings WithSource(LocalSettings s, string source) =>
            new LocalSettings(s.Controls, s.QualityOverride, s.MusicVolume, s.EffectsVolume, s.Vibration) { Source = source };

        private static Dictionary<string, string> Parse(string[] lines)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in lines)
            {
                int eq = line.IndexOf('=');
                if (eq > 0 && line.Length < 256)
                {
                    values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }

            return values;
        }

        private static float Float(Dictionary<string, string> values, string key, float min, float max, float fallback) =>
            values.TryGetValue(key, out string? s) && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && f >= min && f <= max ? f : fallback;

        // A new key, not a format bump: older builds ignore it instead of discarding the whole file.
        private static bool Bool(Dictionary<string, string> values, string key, bool fallback)
        {
            if (!values.TryGetValue(key, out string? s))
            {
                return fallback;
            }

            switch (s.ToLowerInvariant())
            {
                case "on":
                case "true":
                case "1":
                    return true;
                case "off":
                case "false":
                case "0":
                    return false;
                default:
                    return fallback;
            }
        }

        private static float Clamp01(float v) => float.IsNaN(v) ? 1f : Math.Max(0f, Math.Min(1f, v));
    }
}
