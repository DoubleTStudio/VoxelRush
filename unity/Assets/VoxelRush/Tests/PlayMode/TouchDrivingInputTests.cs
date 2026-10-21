using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using VoxelRush.Presentation.Input;
using VoxelRush.Presentation.UI;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;

namespace VoxelRush.Tests.PlayMode
{
    /// <summary>
    /// VR-010 DoD: steer and gas held simultaneously via multi-touch on the landscape HUD.
    /// Drives the real input system with device state events (InputTestFixture conflicts with the HUD's on-screen devices).
    /// </summary>
    public sealed class TouchDrivingInputTests
    {
        // Steering ramps over 0.25 s at default sensitivity; headless frames are ~1 ms, so wait in real time.
        private const float SettleSeconds = 0.6f;

        private readonly List<InputDevice> _devices = new List<InputDevice>();
        private InputSettings.BackgroundBehavior _previousBackgroundBehavior;
#if UNITY_EDITOR
        private InputSettings.EditorInputBehaviorInPlayMode _previousEditorBehavior;
#endif

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            // A batch-mode editor never has app or Game View focus; the UI module and the editor's play-mode
            // routing both drop pointer/touch input without it (gamepads are exempt), so route everything.
            _previousBackgroundBehavior = InputSystem.settings.backgroundBehavior;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
            _previousEditorBehavior = InputSystem.settings.editorInputBehaviorInPlayMode;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
            yield return TestScenes.UnloadAll();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            yield return TestScenes.UnloadAll();
            foreach (InputDevice device in _devices)
            {
                if (device.added)
                {
                    InputSystem.RemoveDevice(device);
                }
            }

            _devices.Clear();
            InputSystem.settings.backgroundBehavior = _previousBackgroundBehavior;
#if UNITY_EDITOR
            InputSystem.settings.editorInputBehaviorInPlayMode = _previousEditorBehavior;
#endif
        }

        [UnityTest]
        public IEnumerator TwoSimultaneousTouches_SteerLeftAndGas_BothRegister()
        {
            var touchscreen = Add(InputSystem.AddDevice<Touchscreen>());
            yield return LoadClientScene();
            var reader = Object.FindAnyObjectByType<DrivingInputReader>();
            Assert.That(reader, Is.Not.Null);

            Vector2 steer = ScreenCenterOf("SteerLeft");
            Vector2 gas = ScreenCenterOf("Gas");
            Touch(touchscreen, 1, TouchPhase.Began, steer);
            Touch(touchscreen, 2, TouchPhase.Began, gas);
            yield return new WaitForSecondsRealtime(SettleSeconds);

            Assert.That(reader!.RawSteer, Is.EqualTo(-1f), "steer-left touch not registered");
            Assert.That(reader.Current.Steer, Is.EqualTo(-1f).Within(1e-3f), "steering did not ramp to full lock");
            Assert.That(reader.Current.Throttle, Is.EqualTo(1f), "gas touch not registered while steering");

            Touch(touchscreen, 1, TouchPhase.Ended, steer);
            yield return new WaitForSecondsRealtime(SettleSeconds);

            Assert.That(reader.Current.Steer, Is.EqualTo(0f).Within(1e-3f), "steering did not return to centre");
            Assert.That(reader.Current.Throttle, Is.EqualTo(1f), "releasing steer must not release gas");

            Touch(touchscreen, 2, TouchPhase.Ended, gas);
            yield return new WaitForSecondsRealtime(0.1f);

            Assert.That(reader.Current.Throttle, Is.EqualTo(0f));
        }

        [UnityTest]
        public IEnumerator CameraDrag_OffTheHudOrbits_ButSteeringFingerDoesNot()
        {
            var touchscreen = Add(InputSystem.AddDevice<Touchscreen>());
            yield return LoadClientScene();
            var camera = Camera.main!.GetComponent<VoxelRush.Presentation.World.ChaseCamera>();
            var car = new GameObject("GestureCar");
            try
            {
                camera.SetTarget(car.transform);

                // A finger on the steer button sliding sideways must not turn the camera.
                Vector2 steer = ScreenCenterOf("SteerLeft");
                Touch(touchscreen, 1, TouchPhase.Began, steer);
                yield return null;
                yield return null;
                Touch(touchscreen, 1, TouchPhase.Moved, steer + new Vector2(200f, 0f));
                yield return null;
                yield return null;
                Assert.That(camera.YawOffset, Is.EqualTo(0f), "steering finger turned the camera");
                Touch(touchscreen, 1, TouchPhase.Ended, steer + new Vector2(200f, 0f));
                yield return null;

                // A drag across empty sky orbits.
                var sky = new Vector2(Screen.width * 0.5f, Screen.height * 0.55f);
                Touch(touchscreen, 2, TouchPhase.Began, sky);
                yield return null;
                yield return null;
                Touch(touchscreen, 2, TouchPhase.Moved, sky + new Vector2(Screen.width * 0.2f, 0f));
                yield return null;
                yield return null;
                Assert.That(camera.YawOffset, Is.GreaterThan(20f), "drag off the HUD did not orbit");
                Touch(touchscreen, 2, TouchPhase.Ended, sky + new Vector2(Screen.width * 0.2f, 0f));
                yield return null;
            }
            finally
            {
                Object.Destroy(car);
            }
        }

        [UnityTest]
        public IEnumerator Gamepad_SteerAndTriggerTogether_BothRegister()
        {
            var gamepad = Add(InputSystem.AddDevice<Gamepad>());
            yield return LoadClientScene();
            var reader = Object.FindAnyObjectByType<DrivingInputReader>();

            InputSystem.QueueStateEvent(gamepad, new GamepadState { leftStick = new Vector2(0.5f, 0f), rightTrigger = 1f }.WithButton(GamepadButton.South));
            yield return new WaitForSecondsRealtime(SettleSeconds);

            Assert.That(reader!.Current.Steer, Is.EqualTo(0.5f).Within(0.05f));
            Assert.That(reader.Current.Throttle, Is.EqualTo(1f));
            Assert.That(reader.Current.Boost, Is.True);
        }

        [UnityTest]
        public IEnumerator Hud_IsInsideSafeAreaAndLandscapeAnchored()
        {
            yield return LoadClientScene();
            Assert.That(Object.FindAnyObjectByType<SafeAreaFitter>(), Is.Not.Null);

            Rect safe = Screen.safeArea;
            foreach (string button in new[] { "SteerLeft", "SteerRight", "Gas", "Brake", "Boost", "Handbrake", "BuildMode" })
            {
                Assert.That(safe.Contains(ScreenCenterOf(button)), Is.True, $"{button} centre outside safe area {safe}");
            }

            Assert.That(ScreenCenterOf("SteerLeft").x, Is.LessThan(Screen.width / 2f), "steering belongs on the left");
            Assert.That(ScreenCenterOf("Gas").x, Is.GreaterThan(Screen.width / 2f), "gas belongs on the right");
        }

        private T Add<T>(T device) where T : InputDevice
        {
            _devices.Add(device);
            return device;
        }

        private static void Touch(Touchscreen screen, int touchId, TouchPhase phase, Vector2 position) =>
            InputSystem.QueueStateEvent(screen, new TouchState { touchId = touchId, phase = phase, position = position });

        private static IEnumerator LoadClientScene()
        {
            yield return SceneManager.LoadSceneAsync("Client", LoadSceneMode.Single);
            yield return null;
        }

        private static Vector2 ScreenCenterOf(string name)
        {
            var rect = GameObject.Find(name)?.GetComponent<RectTransform>();
            Assert.That(rect, Is.Not.Null, $"HUD element '{name}' missing");
            // Overlay canvas: world space is screen space.
            return RectTransformUtility.WorldToScreenPoint(null, rect!.TransformPoint(rect.rect.center));
        }
    }
}
