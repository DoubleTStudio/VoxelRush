using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;

namespace VoxelRush.Presentation.World
{
    /// <summary>
    /// Build-mode grid targeting (CONTROLS_ASSETS Build mode): aims through a screen point, shows a ghost block and
    /// explains invalid placements with the same <see cref="EditValidator"/> the server enforces. The ghost is local
    /// only; the server's result is authoritative (VR-031).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BuildTargeting : MonoBehaviour
    {
        private static readonly Color ValidColor = new Color(0.2f, 0.85f, 0.35f);
        private static readonly Color InvalidColor = new Color(0.9f, 0.25f, 0.2f);

        [SerializeField] private VoxelTerrainView _world = null!;
        [SerializeField] private Camera _camera = null!;
        [SerializeField] private Text? _feedback;
        [SerializeField] private float _maxAimDistance = 32f;
        [SerializeField] private List<Bounds> _protectedZones = new List<Bounds>();

        private EditValidator _validator = null!;
        private Transform _ghost = null!;
        private Renderer _ghostRenderer = null!;
        private MaterialPropertyBlock _properties = null!;
        private readonly IReadOnlyList<WorldBox> _occupants = Array.Empty<WorldBox>();

        public EditAction Action { get; set; } = EditAction.Place;

        public byte BlockType { get; set; } = BlockIds.Stone;

        /// <summary>Screen point to aim through; defaults to the screen centre (crosshair).</summary>
        public Vector2? AimPoint { get; set; }

        public bool HasTarget { get; private set; }

        public BlockPos Target { get; private set; }

        public EditRejection Rejection { get; private set; }

        public string FeedbackText => _feedback != null ? _feedback.text : string.Empty;

        public Color GhostColor { get; private set; }

        public bool GhostVisible => _ghost != null && _ghost.gameObject.activeSelf;

        private void Awake()
        {
            var zones = new List<WorldBox>(_protectedZones.Count);
            foreach (Bounds b in _protectedZones)
            {
                zones.Add(new WorldBox(b.min.x, b.min.y, b.min.z, b.max.x, b.max.y, b.max.z));
            }

            _validator = new EditValidator(zones);
            GameObject ghost = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ghost.name = "BuildGhost";
            Destroy(ghost.GetComponent<Collider>());
            ghost.transform.SetParent(transform, false);
            // Slightly larger than a block so it isn't z-fighting the faces it touches.
            ghost.transform.localScale = Vector3.one * 1.02f;
            _ghost = ghost.transform;
            _ghostRenderer = ghost.GetComponent<Renderer>();
            _properties = new MaterialPropertyBlock();
            ghost.SetActive(false);
        }

        private void Update() => Refresh();

        /// <summary>Re-aims and re-validates now (also called every frame).</summary>
        public void Refresh()
        {
            Vector2 point = AimPoint ?? new Vector2(Screen.width / 2f, Screen.height / 2f);
            Ray ray = _camera.ScreenPointToRay(point);
            Vector3 eye = _camera.transform.position;
            HasTarget = VoxelRaycast.Cast(ray.origin.x, ray.origin.y, ray.origin.z, ray.direction.x, ray.direction.y, ray.direction.z, _maxAimDistance, _world.IsSolid, out VoxelHit hit);
            if (!HasTarget)
            {
                _ghost.gameObject.SetActive(false);
                SetFeedback("Aim at the ground or a block");
                return;
            }

            Target = Action == EditAction.Place ? hit.Adjacent : hit.Block;
            Rejection = _validator.Validate(Action, Target, BlockType, eye.x, eye.y, eye.z, _world, _occupants);
            GhostColor = Rejection == EditRejection.None ? ValidColor : InvalidColor;
            _ghost.position = new Vector3(Target.X + 0.5f, Target.Y + 0.5f, Target.Z + 0.5f);
            _properties.SetColor("_BaseColor", GhostColor);
            _ghostRenderer.SetPropertyBlock(_properties);
            _ghost.gameObject.SetActive(true);
            SetFeedback(Rejection == EditRejection.None && Action == EditAction.Remove ? "Remove block" : EditValidator.Describe(Rejection));
        }

        private void SetFeedback(string message)
        {
            if (_feedback != null && _feedback.text != message)
            {
                _feedback.text = message;
            }
        }
    }
}
