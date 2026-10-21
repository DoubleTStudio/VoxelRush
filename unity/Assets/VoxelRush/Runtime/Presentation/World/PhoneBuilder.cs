using System;
using UnityEngine;
using UnityEngine.InputSystem;
using VoxelRush.Networking;
using VoxelRush.Networking.Vehicles;
using VoxelRush.Networking.World;
using VoxelRush.Presentation.Input;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;
using VoxelRush.World.Sync;

namespace VoxelRush.Presentation.World
{
    /// <summary>
    /// Networked building from the phone (MVP-003, BUILD-002): the build button cycles Place → Remove → Off. A tap on
    /// the world aims: a ghost of the selected block (or the block to remove) appears there, green when the server's
    /// own validator would accept it, red with the reason otherwise. PLACE/REMOVE (or tapping the ghost again) sends
    /// the edit, ROTATE turns a ramp 90°, CANCEL drops the aim. Drags still orbit the camera. The server is authoritative.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PhoneBuilder : MonoBehaviour
    {
        // The server measures reach from the car, 1.5 m above its origin (VehicleSpawner.EyeOf).
        private static readonly Vector3 EyeOffset = Vector3.up * 1.5f;
        private static readonly Color ValidColor = new Color(0.2f, 0.85f, 0.35f);
        private static readonly Color RemoveColor = new Color(1f, 0.6f, 0.15f);
        private static readonly Color InvalidColor = new Color(0.9f, 0.25f, 0.2f);

        [SerializeField] private float _maxAimDistance = 40f;
        [SerializeField] private byte _blockType = BlockIds.Brick;

        // MVP-005 palette: building materials only (protected/reinforced blocks are server-placed); ramps turn in place.
        private static readonly (byte Id, string Name)[] Blocks =
        {
            (BlockIds.Brick, "Brick"), (BlockIds.Stone, "Stone"), (BlockIds.Wood, "Wood"),
            (BlockIds.Concrete, "Concrete"), (BlockIds.Glass, "Glass"), (BlockIds.Metal, "Metal"),
        };

        private static readonly (byte Id, string Name)[] Ramps =
        {
            (BlockIds.RampLow, "Low ramp"), (BlockIds.RampHigh, "High ramp"), (BlockIds.Kicker, "Kicker"),
        };

        private readonly CameraGestures _gestures = new CameraGestures();
        private readonly EditValidator _validator = new EditValidator(ZoneMap.Empty);
        private readonly ReplicaWorld _world = new ReplicaWorld();
        private DrivingInputReader? _input;
        private BuildGhost? _ghost;
        private int _yaw;
        private bool _previewing;
        private ClientWorldReplica? _previewReplica;
        private Transform? _previewCar;
        private GUIStyle? _style;
        private GUIStyle? _button;
        // The status/palette box and the action row in GUI coordinates; taps there are UI, not world aims.
        private Rect _panel;
        private Rect _actions;

        public enum Mode
        {
            Off,
            Place,
            Remove,
        }

        public Mode Current { get; private set; }

        public string Status { get; private set; } = string.Empty;

        public long Sent { get; private set; }

        /// <summary>The id a placement would send: the selected block, turned by <see cref="Yaw"/> when it's a ramp.</summary>
        public byte BlockType => BlockShapes.Rotate(_blockType, _yaw);

        public int Yaw => _yaw;

        /// <summary>The aimed cell waiting for PLACE/REMOVE, if any.</summary>
        public BlockPos? Target { get; private set; }

        /// <summary>The validator's verdict for <see cref="Target"/>, refreshed every frame.</summary>
        public EditRejection Verdict { get; private set; }

        public bool GhostVisible => _ghost != null && _ghost.Visible;

        public Color GhostColor => _ghost != null ? _ghost.Color : Color.clear;

        public byte GhostBlockType => _ghost != null ? _ghost.BlockType : BlockIds.Air;

        public Vector3 GhostPosition => _ghost != null ? _ghost.Position : Vector3.zero;

        /// <summary>Selects a palette block; a turned ramp id also sets the yaw. Anything off the palette is ignored.</summary>
        public void SelectBlock(byte blockType)
        {
            byte variant = BlockShapes.Variant(blockType);
            string? name = NameOf(variant);
            if (name == null)
            {
                return;
            }

            _blockType = variant;
            if (BlockShapes.IsRamp(blockType))
            {
                _yaw = BlockShapes.Yaw(blockType);
            }

            Status = $"{name} selected";
            Refresh();
        }

        /// <summary>Turns the selected ramp a quarter turn (clockwise seen from above).</summary>
        public void Rotate()
        {
            if (!BlockShapes.IsRamp(_blockType))
            {
                return;
            }

            _yaw = (_yaw + 1) % BlockShapes.Yaws;
            Refresh();
        }

        public void SetMode(Mode mode)
        {
            Current = mode;
            Cancel();
            Status = mode switch
            {
                Mode.Place => "Tap near your car to aim, then PLACE",
                Mode.Remove => "Tap a block near your car, then REMOVE",
                _ => string.Empty,
            };
        }

        /// <summary>Uses this replica and car instead of the connected client's (tests, offline previews); nothing is sent.</summary>
        public void Preview(ClientWorldReplica? replica, Transform? car)
        {
            _previewing = replica != null;
            _previewReplica = replica;
            _previewCar = car;
            Cancel();
        }

        /// <summary>
        /// Aims through a screen point: shows the ghost there and returns the client-side verdict. Tapping the aimed
        /// cell again confirms it.
        /// </summary>
        public EditRejection TapAt(Vector2 screenPoint)
        {
            Camera? camera = Camera.main;
            if (Current == Mode.Off || !TryBindWorld(out _) || camera == null)
            {
                return EditRejection.ChunkNotLoaded;
            }

            Ray ray = camera.ScreenPointToRay(screenPoint);
            if (!VoxelRaycast.Cast(ray.origin.x, ray.origin.y, ray.origin.z, ray.direction.x, ray.direction.y, ray.direction.z, _maxAimDistance, _world.IsSolid, out VoxelHit hit))
            {
                Status = "Tap the ground or a block";
                return EditRejection.InvalidIndex;
            }

            BlockPos target = Current == Mode.Place ? hit.Adjacent : hit.Block;
            if (Target is BlockPos aimed && aimed.Equals(target))
            {
                return Confirm();
            }

            return AimAt(target);
        }

        /// <summary>Puts the ghost on a cell and validates it (Place: the empty cell; Remove: the block).</summary>
        public EditRejection AimAt(BlockPos target)
        {
            if (Current == Mode.Off)
            {
                return EditRejection.UnsupportedAction;
            }

            Target = target;
            Refresh();
            return Verdict;
        }

        /// <summary>Sends the aimed edit when the validator agrees; returns the verdict.</summary>
        public EditRejection Confirm()
        {
            if (Target is not BlockPos target)
            {
                return EditRejection.InvalidIndex;
            }

            EditRejection verdict = Validate(target, out EditAction action);
            if (verdict != EditRejection.None)
            {
                Status = EditValidator.Describe(verdict);
                return verdict;
            }

            WorldSyncClient? sync = _previewing ? null : ClientBootstrap.Instance?.World;
            byte blockType = BlockType;
            Cancel();
            if (sync == null)
            {
                Status = "Offline preview: nothing sent";
                return EditRejection.None;
            }

            Sent++;
            Status = "Sending...";
            sync.SendEdit(ChunkMath.ToChunk(target), ChunkMath.ToLocal(target).Index, action, blockType, (rejection, _) =>
                Status = rejection == EditRejection.None ? (action == EditAction.Place ? "Placed" : "Removed") : EditValidator.Describe(rejection));
            return EditRejection.None;
        }

        public void Cancel()
        {
            Target = null;
            Verdict = EditRejection.None;
            _ghost?.Hide();
        }

        private void Awake() => _ghost = new BuildGhost(transform);

        private void OnDestroy() => _ghost?.Destroy();

        private void Update()
        {
            if (_input == null)
            {
                _input = FindAnyObjectByType<DrivingInputReader>();
            }

            if (_input != null && _input.BuildModePressedThisFrame)
            {
                SetMode(Current switch { Mode.Off => Mode.Place, Mode.Place => Mode.Remove, _ => Mode.Off });
            }

            _gestures.Read(out _, out _);
            if (UI.GameMenu.IsOpen || Current == Mode.Off)
            {
                return;
            }

            if (_gestures.Tap is Vector2 tap)
            {
                var gui = new Vector2(tap.x, Screen.height - tap.y);
                if (!_panel.Contains(gui) && !(Target != null && _actions.Contains(gui)))
                {
                    TapAt(tap);
                }
            }

            ReadKeys();
            Refresh();
        }

        // Desktop/editor shortcuts mirroring the action row.
        private void ReadKeys()
        {
            Keyboard? keys = Keyboard.current;
            if (keys == null || Target == null)
            {
                return;
            }

            if (keys.rKey.wasPressedThisFrame)
            {
                Rotate();
            }
            else if (keys.enterKey.wasPressedThisFrame)
            {
                Confirm();
            }
            else if (keys.escapeKey.wasPressedThisFrame)
            {
                Cancel();
            }
        }

        // Re-validates the aim every frame: the car moves and the world changes under the ghost.
        private void Refresh()
        {
            if (Target is not BlockPos target || Current == Mode.Off)
            {
                _ghost?.Hide();
                return;
            }

            Verdict = Validate(target, out EditAction action);
            byte shown = action == EditAction.Place ? BlockType : _world.GetBlock(target);
            if (shown == BlockIds.Air)
            {
                shown = BlockIds.Stone;
            }

            Color color = Verdict != EditRejection.None ? InvalidColor : action == EditAction.Place ? ValidColor : RemoveColor;
            _ghost?.Show(target, shown, color);
            Status = Verdict != EditRejection.None ? EditValidator.Describe(Verdict)
                : action == EditAction.Place ? $"{NameOf(_blockType)} here: tap PLACE" : "Remove this block: tap REMOVE";
        }

        private EditRejection Validate(BlockPos target, out EditAction action)
        {
            action = Current == Mode.Remove ? EditAction.Remove : EditAction.Place;
            if (!TryBindWorld(out Vector3 eye))
            {
                return EditRejection.ChunkNotLoaded;
            }

            return _validator.Validate(action, target, BlockType, eye.x, eye.y, eye.z, _world, Array.Empty<WorldBox>());
        }

        private bool TryBindWorld(out Vector3 eye)
        {
            ClientWorldReplica? replica = _previewing ? _previewReplica : ClientBootstrap.Instance?.World?.Replica;
            Transform? car = _previewing ? _previewCar : LocalPlayer.Vehicle != null ? LocalPlayer.Vehicle.transform : null;
            _world.Replica = replica;
            eye = car != null ? car.position + EyeOffset : Vector3.zero;
            return replica != null && car != null;
        }

        private int YawAwayFromCamera()
        {
            Camera? camera = Camera.main;
            if (camera == null)
            {
                return _yaw;
            }

            Vector3 f = camera.transform.forward;
            return Mathf.Abs(f.z) >= Mathf.Abs(f.x) ? (f.z >= 0f ? 0 : 2) : (f.x >= 0f ? 1 : 3);
        }

        private static string? NameOf(byte variant)
        {
            foreach ((byte id, string name) in Blocks)
            {
                if (id == variant)
                {
                    return name;
                }
            }

            foreach ((byte id, string name) in Ramps)
            {
                if (id == variant)
                {
                    return name;
                }
            }

            return null;
        }

        private void OnGUI()
        {
            if (Current == Mode.Off)
            {
                return;
            }

            _style ??= new GUIStyle(GUI.skin.box) { fontSize = Mathf.Max(18, Screen.height / 28), wordWrap = true };
            _button ??= new GUIStyle(GUI.skin.button) { fontSize = _style.fontSize };
            float font = _style.fontSize;
            string label = (Current == Mode.Place ? "BUILD: place" : "BUILD: remove") + (Status.Length > 0 ? $"\n{Status}" : string.Empty);
            float width = Screen.width * 0.4f;
            float left = (Screen.width - width) / 2f;
            float top = Screen.height - Screen.safeArea.yMax + 8f;
            GUI.Box(new Rect(left, top, width, font * 3.2f), label, _style);
            _panel = new Rect(left, top, width, font * (Current == Mode.Place ? 8f : 3.2f));
            if (Current == Mode.Place)
            {
                // Two palette rows under the status box; IMGUI clicks are not world taps (OverHud only sees uGUI).
                PaletteRow(Blocks, left, top + font * 3.4f, width, font);
                PaletteRow(Ramps, left, top + font * 5.7f, width, font);
            }

            DrawActions(font);
        }

        private void PaletteRow((byte Id, string Name)[] row, float left, float y, float width, float font)
        {
            float cell = width / row.Length;
            for (int i = 0; i < row.Length; i++)
            {
                bool selected = row[i].Id == _blockType;
                if (GUI.Toggle(new Rect(left + i * cell, y, cell - 4f, font * 2.2f), selected, row[i].Name, _button) && !selected)
                {
                    // A freshly picked ramp climbs away from the camera; ROTATE turns it from there.
                    SelectBlock(BlockShapes.Rotate(row[i].Id, YawAwayFromCamera()));
                }
            }
        }

        // PLACE/REMOVE, ROTATE (ramps), CANCEL at the bottom centre, between the steering and pedal clusters.
        private void DrawActions(float font)
        {
            if (Target == null)
            {
                _actions = Rect.zero;
                return;
            }

            bool rotate = Current == Mode.Place && BlockShapes.IsRamp(_blockType);
            int count = rotate ? 3 : 2;
            float width = Screen.width * 0.34f;
            float height = font * 2.6f;
            float left = (Screen.width - width) / 2f;
            float top = Screen.height - Screen.safeArea.yMin - height - 12f;
            _actions = new Rect(left, top, width, height);
            float cell = width / count;
            bool enabled = GUI.enabled;
            GUI.enabled = Verdict == EditRejection.None;
            if (GUI.Button(new Rect(left, top, cell - 6f, height), Current == Mode.Place ? "PLACE" : "REMOVE", _button))
            {
                Confirm();
            }

            GUI.enabled = enabled;
            if (rotate && GUI.Button(new Rect(left + cell, top, cell - 6f, height), "ROTATE", _button))
            {
                Rotate();
            }

            if (GUI.Button(new Rect(left + cell * (count - 1), top, cell - 6f, height), "CANCEL", _button))
            {
                Cancel();
            }
        }

        // Validator and raycast view of the synced replica.
        private sealed class ReplicaWorld : IEditWorld
        {
            public ClientWorldReplica? Replica { get; set; }

            public bool IsLoaded(BlockPos block) => Replica?.Get(ChunkMath.ToChunk(block)) != null;

            public byte GetBlock(BlockPos block) => Replica?.Get(ChunkMath.ToChunk(block))?.Get(ChunkMath.ToLocal(block)) ?? BlockIds.Air;

            public bool IsSolid(BlockPos block) => GetBlock(block) != BlockIds.Air;
        }
    }
}
