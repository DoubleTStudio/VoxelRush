using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace VoxelRush.Presentation.Input
{
    /// <summary>
    /// Camera gestures from touches that start off the HUD (MVP-002): one finger drags, two fingers pinch.
    /// Fingers on the driving buttons are ignored, so steering never turns the camera. Mouse: right-drag and wheel.
    /// </summary>
    public sealed class CameraGestures
    {
        // A short touch that barely moved is a tap (build), not a drag (camera).
        private const float TapMaxSeconds = 0.35f;
        private const float TapMaxTravelPixels = 25f;

        private readonly Dictionary<int, Tracked> _touches = new Dictionary<int, Tracked>();
        private readonly List<int> _seen = new List<int>();
        private readonly List<RaycastResult> _hits = new List<RaycastResult>();
        private readonly List<(Vector2 Previous, Vector2 Current)> _free = new List<(Vector2, Vector2)>(2);
        private PointerEventData? _pointer;

        /// <summary>Screen position of a tap off the HUD that ended this frame (set by <see cref="Read"/>).</summary>
        public Vector2? Tap { get; private set; }

        /// <summary>This frame's drag in pixels and pinch ratio (above 1 = fingers spreading); false when idle.</summary>
        public bool Read(out Vector2 drag, out float pinch)
        {
            drag = Vector2.zero;
            pinch = 1f;
            Tap = null;
            ReadTouches();
            if (_free.Count == 1)
            {
                drag = _free[0].Current - _free[0].Previous;
            }
            else if (_free.Count >= 2)
            {
                float before = Vector2.Distance(_free[0].Previous, _free[1].Previous);
                float now = Vector2.Distance(_free[0].Current, _free[1].Current);
                pinch = before > 1f ? now / before : 1f;
            }

            Mouse? mouse = Mouse.current;
            if (mouse != null)
            {
                if (mouse.rightButton.isPressed)
                {
                    drag += mouse.delta.ReadValue();
                }

                if (mouse.leftButton.wasPressedThisFrame && !OverHud(mouse.position.ReadValue()))
                {
                    Tap = mouse.position.ReadValue();
                }

                float scroll = mouse.scroll.ReadValue().y;
                if (scroll != 0f)
                {
                    pinch *= 1f + Mathf.Clamp(scroll, -120f, 120f) * 0.001f;
                }
            }

            return drag != Vector2.zero || pinch != 1f;
        }

        private void ReadTouches()
        {
            _free.Clear();
            _seen.Clear();
            Touchscreen? screen = Touchscreen.current;
            if (screen != null)
            {
                foreach (TouchControl touch in screen.touches)
                {
                    if (!touch.press.isPressed)
                    {
                        continue;
                    }

                    int id = touch.touchId.ReadValue();
                    Vector2 position = touch.position.ReadValue();
                    _seen.Add(id);
                    if (!_touches.TryGetValue(id, out Tracked known))
                    {
                        // Decided once at touch-down: a finger that slides off a button stays a button finger.
                        Vector2 start = touch.startPosition.ReadValue();
                        _touches[id] = new Tracked(!OverHud(start), start, position, Time.unscaledTime, 0f);
                        continue;
                    }

                    _touches[id] = new Tracked(known.Free, known.Start, position, known.StartedAt, Mathf.Max(known.Travel, Vector2.Distance(known.Start, position)));
                    if (known.Free && _free.Count < 2)
                    {
                        _free.Add((known.Position, position));
                    }
                }
            }

            if (_touches.Count > _seen.Count)
            {
                foreach (int id in new List<int>(_touches.Keys))
                {
                    if (!_seen.Contains(id))
                    {
                        Tracked ended = _touches[id];
                        if (ended.Free && ended.Travel <= TapMaxTravelPixels && Time.unscaledTime - ended.StartedAt <= TapMaxSeconds)
                        {
                            Tap = ended.Position;
                        }

                        _touches.Remove(id);
                    }
                }
            }
        }

        private readonly struct Tracked
        {
            public Tracked(bool free, Vector2 start, Vector2 position, float startedAt, float travel)
            {
                Free = free;
                Start = start;
                Position = position;
                StartedAt = startedAt;
                Travel = travel;
            }

            public bool Free { get; }

            public Vector2 Start { get; }

            public Vector2 Position { get; }

            public float StartedAt { get; }

            public float Travel { get; }
        }

        private bool OverHud(Vector2 screenPosition)
        {
            EventSystem? events = EventSystem.current;
            if (events == null)
            {
                return false;
            }

            _pointer ??= new PointerEventData(events);
            _pointer.position = screenPosition;
            _hits.Clear();
            events.RaycastAll(_pointer, _hits);
            return _hits.Count > 0;
        }
    }
}
