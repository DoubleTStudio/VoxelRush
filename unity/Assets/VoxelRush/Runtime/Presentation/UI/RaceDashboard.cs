using UnityEngine;
using VoxelRush.Core;
using VoxelRush.Core.Race;
using VoxelRush.Networking;
using VoxelRush.Networking.Race;
using VoxelRush.Networking.Vehicles;
using VoxelRush.Presentation.Settings;

namespace VoxelRush.Presentation.UI
{
    /// <summary>
    /// RACE-003 top-left driving panel inside the safe area: speed, an arrow and distance to the next checkpoint,
    /// local race time, and a wrong-checkpoint hint. Also plays the race haptics. Away from BUILD/HORN (top centre),
    /// the pause button (top right) and the touch clusters (bottom).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RaceDashboard : MonoBehaviour
    {
        public const float PanelWidth = 250f;
        public const float FlashSeconds = 2f;

        // Teleports (grid placement, reset when stuck) aren't drives through a gate.
        private const float MaxFrameMove = 20f;

        private RacePhase? _phase;
        private float? _raceStartedAt;
        private float? _finishedAt;
        private RaceProgressMessage? _lastProgress;
        private Vector3? _lastPosition;
        private float _flashUntil = float.NegativeInfinity;
        private Texture2D? _arrow;
        private GUIStyle? _speed;
        private GUIStyle? _text;

        /// <summary>Defaults to the live network race client; tests inject a fake and call <see cref="Step"/>.</summary>
        public IRaceView? Source { get; set; }

        public string SpeedText { get; private set; } = string.Empty;

        public string DistanceText { get; private set; } = string.Empty;

        /// <summary>Screen rotation of the arrow (0 = up/ahead, clockwise positive), or null when hidden.</summary>
        public float? ArrowDegrees { get; private set; }

        public string TimeText { get; private set; } = string.Empty;

        public string FlashText { get; private set; } = string.Empty;

        /// <summary>The last cue raised, whether or not it vibrated.</summary>
        public RaceCue LastCue { get; private set; }

        /// <summary>Updates the panel from the race and the own car. <paramref name="heading"/> is screen-forward on the ground.</summary>
        public void Step(IRaceView? view, Vector3? car, Vector3 heading, float speed, float now)
        {
            SpeedText = car.HasValue ? RaceGuide.KilometresPerHour(speed) + " km/h" : string.Empty;
            RacePhase? phase = view?.State?.State;
            if (phase != _phase)
            {
                // Only a race seen from GO gets a clock; joining mid-race has no start to measure from.
                _raceStartedAt = phase == RacePhase.Racing && _phase == RacePhase.Countdown ? now : phase == RacePhase.Finishing ? _raceStartedAt : null;
                if (phase != RacePhase.Racing && phase != RacePhase.Finishing)
                {
                    _finishedAt = null;
                }

                _phase = phase;
            }

            RaceProgressMessage? progress = view?.Progress;
            if (progress is RaceProgressMessage p && !Same(_lastProgress, p))
            {
                Raise(RaceGuide.Cue(_lastProgress, p));
            }

            _lastProgress = progress;
            bool finished = view != null && RaceGuide.HasFinished(view);
            if (finished && _finishedAt == null)
            {
                _finishedAt = now;
            }

            TimeText = _raceStartedAt is float start ? RaceHudModel.FormatTime((uint)(((_finishedAt ?? now) - start) * SimulationConstants.ServerTickRate)) : string.Empty;
            int? next = view == null ? null : RaceGuide.NextCrossing(view);
            if (view?.Track is TrackDefinition track && next is int crossing && car is Vector3 position)
            {
                bool racing = phase == RacePhase.Racing || phase == RacePhase.Finishing;
                if (racing && _lastPosition is Vector3 last && (position - last).sqrMagnitude < MaxFrameMove * MaxFrameMove
                    && RaceGuide.CrossedWrongGate(track, crossing, ToVec(last), ToVec(position)))
                {
                    _flashUntil = now + FlashSeconds;
                    Raise(RaceCue.WrongGate);
                }

                (float distance, float bearing) = RaceGuide.Direction(track.GateAt(crossing), ToVec(position), heading.x, heading.z);
                DistanceText = RaceGuide.FormatDistance(distance);
                ArrowDegrees = bearing;
            }
            else
            {
                DistanceText = string.Empty;
                ArrowDegrees = null;
            }

            _lastPosition = car;
            FlashText = now < _flashUntil ? "Wrong checkpoint" : string.Empty;
        }

        private void Update()
        {
            ReadCar(out Vector3? car, out Vector3 heading, out float speed);
            Step(Source ?? RaceClient.Current, car, heading, speed, Time.unscaledTime);
        }

        private void OnDestroy()
        {
            if (_arrow != null)
            {
                Destroy(_arrow);
            }
        }

        private void OnGUI()
        {
            ClientBootstrap? bootstrap = ClientBootstrap.Instance;
            if (Source == null && (bootstrap == null || !bootstrap.IsConnected || bootstrap.OnJoinScreen))
            {
                return;
            }

            if (SpeedText.Length == 0 && DistanceText.Length == 0)
            {
                return;
            }

            float scale = Screen.height / 720f;
            Matrix4x4 matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            GUI.matrix = matrix;
            _speed ??= new GUIStyle(GUI.skin.label) { fontSize = 40, fontStyle = FontStyle.Bold };
            _text ??= new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold };
            Rect safe = Screen.safeArea;
            float left = safe.xMin / scale + 16f;
            float y = (Screen.height - safe.yMax) / scale + 16f;
            GUI.Box(new Rect(left - 8f, y - 6f, PanelWidth, Height()), GUIContent.none);
            Shadowed(new Rect(left, y, PanelWidth, 50f), SpeedText, _speed);
            y += 52f;
            if (ArrowDegrees is float degrees)
            {
                var pivot = new Vector2(left + 28f, y + 28f);
                GUIUtility.RotateAroundPivot(degrees, pivot * scale);
                GUI.DrawTexture(new Rect(left, y, 56f, 56f), Arrow());
                GUI.matrix = matrix;
                Shadowed(new Rect(left + 68f, y + 10f, PanelWidth - 76f, 40f), DistanceText, _text);
                y += 62f;
            }

            if (TimeText.Length > 0)
            {
                Shadowed(new Rect(left, y, PanelWidth, 36f), TimeText, _text);
                y += 38f;
            }

            if (FlashText.Length > 0)
            {
                Shadowed(new Rect(left, y, PanelWidth, 36f), "! " + FlashText, _text);
            }
        }

        private float Height() => 64f + (ArrowDegrees.HasValue ? 62f : 0f) + (TimeText.Length > 0 ? 38f : 0f) + (FlashText.Length > 0 ? 38f : 0f);

        private void Raise(RaceCue cue)
        {
            if (cue == RaceCue.None)
            {
                return;
            }

            LastCue = cue;
            Haptics.Play(cue);
        }

        private static void ReadCar(out Vector3? car, out Vector3 heading, out float speed)
        {
            var vehicle = LocalPlayer.Vehicle;
            car = vehicle != null ? vehicle.transform.position : (Vector3?)null;
            speed = vehicle != null ? vehicle.ForwardSpeed : 0f;
            Camera? camera = Camera.main;
            // The arrow is drawn on screen, so "ahead" is where the camera looks.
            heading = camera != null ? camera.transform.forward : vehicle != null ? vehicle.transform.forward : Vector3.forward;
        }

        private static bool Same(RaceProgressMessage? a, RaceProgressMessage b) =>
            a is RaceProgressMessage x && x.Crossings == b.Crossings && x.TotalCrossings == b.TotalCrossings && x.LastOutcome == b.LastOutcome && x.RacerName == b.RacerName;

        private static Vec3 ToVec(Vector3 v) => new Vec3(v.x, v.y, v.z);

        private static void Shadowed(Rect rect, string text, GUIStyle style)
        {
            Color color = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), text, style);
            GUI.color = color;
            GUI.Label(rect, text, style);
        }

        // White arrow with a dark rim so it reads on sky and grass.
        private Texture2D Arrow()
        {
            if (_arrow != null)
            {
                return _arrow;
            }

            const int size = 64;
            _arrow = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "RaceArrow", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            for (int py = 0; py < size; py++)
            {
                for (int px = 0; px < size; px++)
                {
                    float inside = ArrowDistance(px + 0.5f, py + 0.5f, size);
                    _arrow.SetPixel(px, py, inside > 3f ? Color.white : inside > 0f ? Color.black : Color.clear);
                }
            }

            _arrow.Apply(false, true);
            return _arrow;
        }

        // Rough distance inside an up-pointing arrow (head on top, shaft below); negative = outside.
        private static float ArrowDistance(float x, float y, int size)
        {
            float cx = size / 2f, s = size;
            float head = y > s * 0.45f ? (s - 2f - y) - Mathf.Abs(x - cx) * 1.1f : -1f;
            float shaft = y <= s * 0.5f && y > 2f ? Mathf.Min(s * 0.17f - Mathf.Abs(x - cx), y - 2f) : -1f;
            return Mathf.Max(Mathf.Min(head, y - s * 0.45f + 4f), shaft);
        }
    }
}
