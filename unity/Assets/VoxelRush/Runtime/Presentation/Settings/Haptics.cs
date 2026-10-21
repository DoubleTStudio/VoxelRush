using UnityEngine;
using VoxelRush.Core.Race;

namespace VoxelRush.Presentation.Settings
{
    /// <summary>
    /// RACE-003 short vibrations for race cues, honouring the Vibration setting. Phones only: a no-op in the editor
    /// and batch mode, where calls are still counted so tests can see them.
    /// </summary>
    public static class Haptics
    {
        private const float MinInterval = 0.15f;

        private static float s_lastAt = float.NegativeInfinity;
#if UNITY_ANDROID && !UNITY_EDITOR
        private static AndroidJavaObject? s_vibrator;
        private static bool s_vibratorFailed;
#endif

        /// <summary>Cues that would have vibrated (enabled, not rate limited), on any platform.</summary>
        public static int Played { get; private set; }

        public static RaceCue LastCue { get; private set; }

        public static bool Enabled => DeviceSettings.Current.Vibration;

        public static bool DeviceSupported => Application.isMobilePlatform && !Application.isEditor && !Application.isBatchMode;

        /// <summary>Vibration length per cue; the finish is the longest so it reads differently.</summary>
        public static long DurationMs(RaceCue cue) => cue switch
        {
            RaceCue.CheckpointPassed => 40,
            RaceCue.NotCounted => 90,
            RaceCue.WrongGate => 120,
            RaceCue.Finished => 300,
            _ => 0,
        };

        public static bool Play(RaceCue cue) => Play(cue, Enabled, Time.unscaledTime);

        /// <summary>True when the cue vibrates (or would, off-device): enabled, a vibrating cue, not within 0.15 s of the last.</summary>
        public static bool Play(RaceCue cue, bool enabled, float now)
        {
            long ms = DurationMs(cue);
            if (ms <= 0 || !enabled || (now >= s_lastAt && now - s_lastAt < MinInterval))
            {
                return false;
            }

            s_lastAt = now;
            Played++;
            LastCue = cue;
            if (DeviceSupported)
            {
                Vibrate(ms);
            }

            return true;
        }

        private static void Vibrate(long ms)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // Handheld.Vibrate is ~0.5 s on Android; the system vibrator gives short taps. Fall back if it's missing.
            if (!s_vibratorFailed)
            {
                try
                {
                    if (s_vibrator == null)
                    {
                        using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                        using AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                        s_vibrator = activity.Call<AndroidJavaObject>("getSystemService", "vibrator");
                    }

                    s_vibrator.Call("vibrate", ms);
                    return;
                }
                catch (System.Exception e)
                {
                    s_vibratorFailed = true;
                    Debug.LogWarning(VoxelRush.Core.Diagnostics.LogEvent.Format("haptics_vibrator_unavailable", ("error", e.Message)));
                }
            }
#endif
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            // Also what makes Unity add the VIBRATE permission on Android.
            Handheld.Vibrate();
#endif
        }
    }
}
