using System.Collections.Generic;
using UnityEngine;
using VoxelRush.Core.Race;
using VoxelRush.Networking.Race;

namespace VoxelRush.Presentation.World
{
    /// <summary>
    /// RACE-003: draws every checkpoint of the current track as two posts and a banner sized from the gate.
    /// States read without colour: next = bright, chevron banner and a bobbing V marker; passed = dark, lowered posts,
    /// no banner; finish = checkered banner. Pure presentation (no colliders), refreshed on RaceClient.Changed.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CheckpointView : MonoBehaviour
    {
        public const float PostWidth = 1.2f;
        public const float BannerHeight = 1.6f;
        public const float PassedHeightFactor = 0.3f;

        // Luminance-separated so states differ in brightness, not only hue.
        public static readonly Color NextColor = new Color(1f, 0.92f, 0.25f);
        public static readonly Color UpcomingColor = new Color(0.3f, 0.55f, 0.95f);
        public static readonly Color PassedColor = new Color(0.16f, 0.16f, 0.18f);

        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
        private static readonly int BaseMapST = Shader.PropertyToID("_BaseMap_ST");

        [SerializeField] private Material? _material;

        private readonly List<GateParts> _gates = new List<GateParts>();
        private Transform? _root;
        private Transform? _marker;
        private Mesh? _cube;
        private Texture2D? _checker;
        private Texture2D? _chevrons;
        private MaterialPropertyBlock? _block;
        private TrackDefinition? _shownTrack;
        private RaceClient? _subscribed;
        private bool _dirty = true;
        private int _next = -1;

        /// <summary>Defaults to the live network race client; tests inject a fake and call <see cref="Refresh"/>.</summary>
        public IRaceView? Source { get; set; }

        public int GateCount => _gates.Count;

        public GateMarker MarkerAt(int index) => _gates[index].Marker;

        public bool IsCheckered(int index) => _gates[index].Finish;

        public Transform GateAt(int index) => _gates[index].Root;

        /// <summary>The bobbing V above the next gate, or null when there is no next gate.</summary>
        public Transform? NextMarker => _marker != null && _marker.gameObject.activeSelf ? _marker : null;

        public Color ColorOf(int index, string part)
        {
            Renderer? renderer = part switch
            {
                "post" => _gates[index].LeftPost,
                "banner" => _gates[index].Banner,
                _ => null,
            };
            if (renderer == null)
            {
                return Color.clear;
            }

            _block ??= new MaterialPropertyBlock();
            renderer.GetPropertyBlock(_block);
            return _block.GetColor(BaseColor);
        }

        /// <summary>Rebuilds for a new track and re-marks every gate from the current race state.</summary>
        public void Refresh()
        {
            _dirty = false;
            IRaceView? view = Source ?? RaceClient.Current;
            TrackDefinition? track = view?.Track;
            if (track != _shownTrack)
            {
                Build(track);
            }

            if (view == null || track == null)
            {
                _next = -1;
                return;
            }

            _next = -1;
            for (int i = 0; i < _gates.Count; i++)
            {
                GateMarker marker = RaceGuide.MarkerOf(view, i);
                if (marker == GateMarker.Next)
                {
                    _next = i;
                }

                Apply(_gates[i], marker);
            }

            PlaceMarker(Time.unscaledTime);
        }

        private void Update()
        {
            RaceClient? live = Source == null ? RaceClient.Current : null;
            if (live != _subscribed)
            {
                Unsubscribe();
                if (live != null)
                {
                    live.Changed += MarkDirty;
                }

                _subscribed = live;
                _dirty = true;
            }

            if (_dirty && Source == null)
            {
                Refresh();
            }

            Animate(Time.unscaledTime);
        }

        private void OnDestroy()
        {
            Unsubscribe();
            Build(null);
            if (_checker != null)
            {
                Destroy(_checker);
            }

            if (_chevrons != null)
            {
                Destroy(_chevrons);
            }
        }

        private void MarkDirty() => _dirty = true;

        private void Unsubscribe()
        {
            if (_subscribed != null)
            {
                _subscribed.Changed -= MarkDirty;
                _subscribed = null;
            }
        }

        private void Build(TrackDefinition? track)
        {
            _shownTrack = track;
            if (_root != null)
            {
                Destroy(_root.gameObject);
            }

            _root = null;
            _marker = null;
            _gates.Clear();
            if (track == null)
            {
                return;
            }

            _root = new GameObject("Checkpoints").transform;
            _root.SetParent(transform, false);
            for (int i = 0; i < track.Gates.Count; i++)
            {
                _gates.Add(BuildGate(track.Gates[i], RaceGuide.IsFinish(track, i)));
            }

            _marker = BuildMarker();
        }

        private GateParts BuildGate(Gate gate, bool finish)
        {
            var root = new GameObject($"Gate{gate.Id}").transform;
            root.SetParent(_root, false);
            // Posts stand on the gate's floor; the banner spans its top edge.
            root.position = new Vector3(gate.X, gate.Y - gate.HalfHeight, gate.Z);
            root.rotation = Quaternion.LookRotation(new Vector3(gate.NormalX, 0f, gate.NormalZ));
            float height = gate.HalfHeight * 2f;
            var parts = new GateParts
            {
                Root = root,
                Finish = finish,
                Height = height,
                LeftPost = Part("PostL", root, new Vector3(-gate.HalfWidth, height / 2f, 0f), new Vector3(PostWidth, height, PostWidth)),
                RightPost = Part("PostR", root, new Vector3(gate.HalfWidth, height / 2f, 0f), new Vector3(PostWidth, height, PostWidth)),
                Banner = Part("Banner", root, new Vector3(0f, height - BannerHeight / 2f, 0f), new Vector3(gate.HalfWidth * 2f, BannerHeight, 0.3f)),
                Cells = Mathf.Clamp(Mathf.RoundToInt(gate.HalfWidth * 2f / BannerHeight), 4, 48),
            };
            return parts;
        }

        private Transform BuildMarker()
        {
            var marker = new GameObject("NextMarker").transform;
            marker.SetParent(_root, false);
            // Two stacked V chevrons pointing down into the gate.
            for (int row = 0; row < 2; row++)
            {
                float y = row * 1.6f;
                foreach (float side in new[] { -1f, 1f })
                {
                    MeshRenderer arm = Part(row == 0 ? "ChevronLow" : "ChevronHigh", marker, new Vector3(side * 0.9f, y + 0.9f, 0f), new Vector3(0.6f, 2.4f, 0.3f));
                    arm.transform.localRotation = Quaternion.Euler(0f, 0f, side * 40f);
                    Tint(arm, NextColor, null, Vector2.one);
                }
            }

            marker.gameObject.SetActive(false);
            return marker;
        }

        private void Apply(GateParts gate, GateMarker marker)
        {
            gate.Marker = marker;
            float postHeight = marker == GateMarker.Passed ? gate.Height * PassedHeightFactor : gate.Height;
            foreach (MeshRenderer post in new[] { gate.LeftPost, gate.RightPost })
            {
                Transform t = post.transform;
                t.localPosition = new Vector3(t.localPosition.x, postHeight / 2f, 0f);
                t.localScale = new Vector3(marker == GateMarker.Next ? PostWidth * 1.5f : PostWidth, postHeight, marker == GateMarker.Next ? PostWidth * 1.5f : PostWidth);
                Color postColor = marker switch
                {
                    GateMarker.Next => NextColor,
                    GateMarker.Passed => PassedColor,
                    // The finish keeps upcoming posts so the next gate stays the brightest; its banner marks it.
                    _ => UpcomingColor,
                };
                Tint(post, postColor, null, Vector2.one);
            }

            gate.Banner.gameObject.SetActive(marker != GateMarker.Passed);
            if (gate.Finish)
            {
                Tint(gate.Banner, marker == GateMarker.Next ? Color.white : new Color(0.8f, 0.8f, 0.8f), Checker(), new Vector2(gate.Cells, 1f));
            }
            else if (marker == GateMarker.Next)
            {
                Tint(gate.Banner, NextColor, Chevrons(), new Vector2(gate.Cells, 1f));
            }
            else
            {
                Tint(gate.Banner, UpcomingColor, null, Vector2.one);
            }
        }

        private void Animate(float time)
        {
            if (_next < 0 || _next >= _gates.Count)
            {
                return;
            }

            PlaceMarker(time);
            GateParts gate = _gates[_next];
            if (!gate.Finish && gate.Banner.gameObject.activeSelf)
            {
                // Scrolls the chevrons downwards and pulses brightness, so the next gate draws the eye.
                float pulse = 0.8f + 0.2f * Mathf.Sin(time * 6f);
                _block ??= new MaterialPropertyBlock();
                gate.Banner.GetPropertyBlock(_block);
                _block.SetColor(BaseColor, NextColor * pulse);
                _block.SetVector(BaseMapST, new Vector4(gate.Cells, 1f, 0f, time * 0.8f % 1f));
                gate.Banner.SetPropertyBlock(_block);
            }
        }

        private void PlaceMarker(float time)
        {
            if (_marker == null)
            {
                return;
            }

            bool show = _next >= 0 && _next < _gates.Count;
            if (_marker.gameObject.activeSelf != show)
            {
                _marker.gameObject.SetActive(show);
            }

            if (show)
            {
                GateParts gate = _gates[_next];
                _marker.SetPositionAndRotation(gate.Root.position + Vector3.up * (gate.Height + 1.5f + 0.6f * Mathf.Sin(time * 3f)), gate.Root.rotation);
                _marker.localScale = Vector3.one * Mathf.Clamp(gate.Height / 6f, 1f, 3f);
            }
        }

        private MeshRenderer Part(string name, Transform parent, Vector3 position, Vector3 scale)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = Cube();
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = Material();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return renderer;
        }

        private void Tint(Renderer renderer, Color color, Texture? pattern, Vector2 tiling)
        {
            _block ??= new MaterialPropertyBlock();
            _block.Clear();
            _block.SetColor(BaseColor, color);
            if (pattern != null)
            {
                _block.SetTexture(BaseMap, pattern);
            }

            _block.SetVector(BaseMapST, new Vector4(tiling.x, tiling.y, 0f, 0f));
            renderer.SetPropertyBlock(_block);
        }

        private Mesh Cube() => _cube ??= Resources.GetBuiltinResource<Mesh>("Cube.fbx");

        private Material Material()
        {
            if (_material == null)
            {
                // Tests build the view without the scene's material: borrow the pipeline default.
                GameObject probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _material = probe.GetComponent<Renderer>().sharedMaterial;
                Destroy(probe);
            }

            return _material;
        }

        private Texture2D Checker() => _checker ??= Pattern("Checker", 2, 2, (x, y) => (x + y) % 2 == 0 ? Color.white : Color.black);

        // One V per cell, black on white; the tint makes it black on yellow.
        private Texture2D Chevrons() => _chevrons ??= Pattern("Chevrons", 16, 16, (x, y) =>
        {
            float d = Mathf.Abs(x - 7.5f) - (y - 2f);
            return d > -3f && d < 0f ? Color.black : Color.white;
        });

        private static Texture2D Pattern(string name, int width, int height, System.Func<int, int, Color> pixel)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
            };
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    texture.SetPixel(x, y, pixel(x, y));
                }
            }

            texture.Apply(false, true);
            return texture;
        }

        private sealed class GateParts
        {
            public Transform Root = null!;
            public MeshRenderer LeftPost = null!;
            public MeshRenderer RightPost = null!;
            public MeshRenderer Banner = null!;
            public bool Finish;
            public float Height;
            public int Cells;
            public GateMarker Marker;
        }
    }
}
