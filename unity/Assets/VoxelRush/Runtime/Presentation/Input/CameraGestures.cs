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
        private readonly Dictionary<int, (bool Free, Vector2 Position)> _touches = new Dictionary<int, (bool, Vector2)>();
        private readonly List<int> _seen = new List<int>();
        private readonly List<RaycastResult> _hits = new List<RaycastResult>();
        private readonly List<(Vector2 Previous, Vector2 Current)> _free = new List<(Vector2, Vector2)>(2);
        private PointerEventData? _pointer;

        /// <summary>This frame's drag in pixels and pinch ratio (above 1 = fingers spreading); false when idle.</summary>
        public bool Read(out Vector2 drag, out float pinch)
        {
            drag = Vector2.zero;
            pinch = 1f;
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
                    if (!_touches.TryGetValue(id, out var known))
                    {
                        // Decided once at touch-down: a finger that slides off a button stays a button finger.
                        _touches[id] = (!OverHud(touch.startPosition.ReadValue()), position);
                        continue;
                    }

                    _touches[id] = (known.Free, position);
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
                        _touches.Remove(id);
                    }
                }
            }
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
