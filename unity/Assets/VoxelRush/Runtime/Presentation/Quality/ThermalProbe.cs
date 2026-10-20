using System;
using UnityEngine;

namespace VoxelRush.Presentation.Quality
{
    /// <summary>
    /// Platform thermal headroom (0 = cool, 1 = severe throttling). Android 11+ via PowerManager.getThermalHeadroom;
    /// NaN elsewhere (iOS needs a native plugin; the controller then relies on frame times alone).
    /// </summary>
    public static class ThermalProbe
    {
        private static bool _unsupported;

        public static float Headroom()
        {
            if (_unsupported)
            {
                return float.NaN;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using AndroidJavaObject power = activity.Call<AndroidJavaObject>("getSystemService", "power");
                // Forecast 10 s ahead; NaN when the device has no thermal HAL.
                return power.Call<float>("getThermalHeadroom", 10);
            }
            catch (Exception)
            {
                _unsupported = true;
                return float.NaN;
            }
#else
            _unsupported = true;
            return float.NaN;
#endif
        }
    }
}
