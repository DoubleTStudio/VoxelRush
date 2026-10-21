using UnityEngine.InputSystem;

namespace VoxelRush.Presentation.Input
{
    /// <summary>
    /// The Driving action map (CONTROLS_ASSETS). Touch on-screen controls drive the virtual gamepad paths below,
    /// so touch, gamepad and keyboard all flow through the same actions.
    /// </summary>
    public static class DrivingControls
    {
        public const string MapName = "Driving";
        public const string Steer = "Steer";
        public const string Throttle = "Throttle";
        public const string Brake = "Brake";
        public const string Boost = "Boost";
        public const string Handbrake = "Handbrake";
        public const string BuildMode = "BuildMode";
        public const string Look = "Look";

        // Control paths the on-screen touch buttons emulate.
        public const string TouchSteerLeftPath = "<Gamepad>/dpad/left";
        public const string TouchSteerRightPath = "<Gamepad>/dpad/right";
        public const string TouchThrottlePath = "<Gamepad>/rightTrigger";
        public const string TouchBrakePath = "<Gamepad>/leftTrigger";
        public const string TouchBoostPath = "<Gamepad>/buttonSouth";
        public const string TouchHandbrakePath = "<Gamepad>/buttonEast";
        public const string TouchBuildModePath = "<Gamepad>/buttonNorth";

        public static InputActionMap CreateMap()
        {
            var map = new InputActionMap(MapName);

            InputAction steer = map.AddAction(Steer, InputActionType.Value, expectedControlLayout: "Axis");
            steer.AddBinding("<Gamepad>/leftStick/x");
            steer.AddCompositeBinding("1DAxis").With("Negative", TouchSteerLeftPath).With("Positive", TouchSteerRightPath);
            steer.AddCompositeBinding("1DAxis").With("Negative", "<Keyboard>/a").With("Positive", "<Keyboard>/d");
            steer.AddCompositeBinding("1DAxis").With("Negative", "<Keyboard>/leftArrow").With("Positive", "<Keyboard>/rightArrow");

            InputAction throttle = map.AddAction(Throttle, InputActionType.Value, expectedControlLayout: "Axis");
            throttle.AddBinding(TouchThrottlePath);
            throttle.AddBinding("<Keyboard>/w");
            throttle.AddBinding("<Keyboard>/upArrow");

            InputAction brake = map.AddAction(Brake, InputActionType.Value, expectedControlLayout: "Axis");
            brake.AddBinding(TouchBrakePath);
            brake.AddBinding("<Keyboard>/s");
            brake.AddBinding("<Keyboard>/downArrow");

            map.AddAction(Boost, InputActionType.Button, TouchBoostPath).AddBinding("<Keyboard>/leftShift");
            map.AddAction(Handbrake, InputActionType.Button, TouchHandbrakePath).AddBinding("<Keyboard>/space");
            map.AddAction(BuildMode, InputActionType.Button, TouchBuildModePath).AddBinding("<Keyboard>/b");

            InputAction look = map.AddAction(Look, InputActionType.Value, "<Gamepad>/rightStick", expectedControlLayout: "Vector2");
            look.AddBinding("<Pointer>/delta");

            return map;
        }
    }
}
