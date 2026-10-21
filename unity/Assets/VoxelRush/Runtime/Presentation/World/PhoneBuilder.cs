using System;
using UnityEngine;
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
    /// Networked building from the phone (MVP-003): the build button cycles Place → Remove → Off, and a tap on the
    /// world sends that edit to the server. The client pre-checks with the server's own validator to explain
    /// failures instantly; the server's answer is authoritative.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PhoneBuilder : MonoBehaviour
    {
        // The server measures reach from the car, 1.5 m above its origin (VehicleSpawner.EyeOf).
        private static readonly Vector3 EyeOffset = Vector3.up * 1.5f;

        [SerializeField] private float _maxAimDistance = 40f;
        [SerializeField] private byte _blockType = BlockIds.Brick;

        // MVP-005 palette: building materials only (protected/reinforced blocks are server-placed).
        private static readonly (byte Id, string Name)[] Palette =
        {
            (BlockIds.Brick, "Brick"), (BlockIds.Stone, "Stone"), (BlockIds.Wood, "Wood"),
            (BlockIds.Concrete, "Concrete"), (BlockIds.Glass, "Glass"), (BlockIds.Metal, "Metal"),
        };

        private readonly CameraGestures _gestures = new CameraGestures();
        private readonly EditValidator _validator = new EditValidator(ZoneMap.Empty);
        private readonly ReplicaWorld _world = new ReplicaWorld();
        private DrivingInputReader? _input;
        private GUIStyle? _style;
        private GUIStyle? _button;
        // The status box and palette in GUI coordinates; taps there are UI, not world aims.
        private Rect _panel;

        public enum Mode
        {
            Off,
            Place,
            Remove,
        }

        public Mode Current { get; private set; }

        public string Status { get; private set; } = string.Empty;

        public long Sent { get; private set; }

        public byte BlockType => _blockType;

        public void SelectBlock(byte blockType)
        {
            foreach ((byte id, string name) in Palette)
            {
                if (id == blockType)
                {
                    _blockType = blockType;
                    Status = $"{name} selected";
                    return;
                }
            }
        }

        public void SetMode(Mode mode)
        {
            Current = mode;
            Status = mode switch
            {
                Mode.Place => "Tap near your car to place a block",
                Mode.Remove => "Tap a block near your car to remove it",
                _ => string.Empty,
            };
        }

        /// <summary>Aims through a screen point and sends the edit; returns the client-side verdict.</summary>
        public EditRejection TapAt(Vector2 screenPoint)
        {
            WorldSyncClient? sync = ClientBootstrap.Instance?.World;
            Camera? camera = Camera.main;
            if (Current == Mode.Off || sync?.Replica == null || camera == null || LocalPlayer.Vehicle == null)
            {
                return EditRejection.ChunkNotLoaded;
            }

            _world.Replica = sync.Replica;
            Ray ray = camera.ScreenPointToRay(screenPoint);
            if (!VoxelRaycast.Cast(ray.origin.x, ray.origin.y, ray.origin.z, ray.direction.x, ray.direction.y, ray.direction.z, _maxAimDistance, _world.IsSolid, out VoxelHit hit))
            {
                Status = "Tap the ground or a block";
                return EditRejection.InvalidIndex;
            }

            EditAction action = Current == Mode.Place ? EditAction.Place : EditAction.Remove;
            BlockPos target = action == EditAction.Place ? hit.Adjacent : hit.Block;
            Vector3 eye = LocalPlayer.Vehicle.transform.position + EyeOffset;
            EditRejection verdict = _validator.Validate(action, target, _blockType, eye.x, eye.y, eye.z, _world, Array.Empty<WorldBox>());
            if (verdict != EditRejection.None)
            {
                Status = EditValidator.Describe(verdict);
                return verdict;
            }

            Sent++;
            sync.SendEdit(ChunkMath.ToChunk(target), ChunkMath.ToLocal(target).Index, action, _blockType, (rejection, _) =>
                Status = rejection == EditRejection.None ? (action == EditAction.Place ? "Placed" : "Removed") : EditValidator.Describe(rejection));
            return EditRejection.None;
        }

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
            if (Current != Mode.Off && _gestures.Tap is Vector2 tap && !_panel.Contains(new Vector2(tap.x, Screen.height - tap.y)))
            {
                TapAt(tap);
            }
        }

        private void OnGUI()
        {
            if (Current == Mode.Off)
            {
                return;
            }

            _style ??= new GUIStyle(GUI.skin.box) { fontSize = Mathf.Max(18, Screen.height / 28), wordWrap = true };
            string label = (Current == Mode.Place ? "BUILD: place" : "BUILD: remove") + (Status.Length > 0 ? $"\n{Status}" : string.Empty);
            float width = Screen.width * 0.4f;
            float top = Screen.height - Screen.safeArea.yMax + 8f;
            GUI.Box(new Rect((Screen.width - width) / 2f, top, width, _style.fontSize * 3.2f), label, _style);
            _panel = new Rect((Screen.width - width) / 2f, top, width, _style.fontSize * (Current == Mode.Place ? 5.6f : 3.2f));
            if (Current != Mode.Place)
            {
                return;
            }

            // Palette row under the status box; IMGUI clicks are not world taps (OverHud only sees uGUI).
            _button ??= new GUIStyle(GUI.skin.button) { fontSize = _style.fontSize };
            float cell = width / Palette.Length;
            float y = top + _style.fontSize * 3.4f;
            for (int i = 0; i < Palette.Length; i++)
            {
                bool selected = Palette[i].Id == _blockType;
                if (GUI.Toggle(new Rect((Screen.width - width) / 2f + i * cell, y, cell - 4f, _style.fontSize * 2.2f), selected, Palette[i].Name, _button) && !selected)
                {
                    SelectBlock(Palette[i].Id);
                }
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
