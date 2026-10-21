using System.Collections.Generic;
using UnityEngine;
using VoxelRush.Destruction;
using VoxelRush.World.Chunks;
using VoxelRush.World.Sync;

namespace VoxelRush.Presentation.World
{
    /// <summary>
    /// Client-only pooled debris for removed blocks: plain renderers moved ballistically in Update, no Rigidbody,
    /// Collider or NetworkObject (DESTRUCTION Cosmetic debris). When the pool is full the oldest piece is reused.
    /// </summary>
    public sealed class DebrisEffects : MonoBehaviour
    {
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        [SerializeField, Min(1)] private int capacity = 192;
        [SerializeField, Min(0.1f)] private float lifetime = 1.2f;
        [SerializeField] private float pieceSize = 0.3f;
        [SerializeField] private float gravity = -18f;
        [SerializeField] private Material? material;

        private readonly List<DebrisSpawn> _planned = new List<DebrisSpawn>(128);
        private DebrisPlanner _planner = new DebrisPlanner();
        private Piece[] _pieces = System.Array.Empty<Piece>();
        private MaterialPropertyBlock? _props;
        private Mesh? _mesh;
        private int _next;
        private ClientWorldReplica? _replica;
        private System.Action<ChunkCoord, ushort, byte, byte>? _onBlockChanged;

        public int Capacity => capacity;

        public int ActiveCount { get; private set; }

        public long Spawned { get; private set; }

        public long Recycled { get; private set; }

        public IReadOnlyList<Transform> PooledTransforms
        {
            get
            {
                EnsurePool();
                var list = new List<Transform>(_pieces.Length);
                foreach (Piece p in _pieces)
                {
                    list.Add(p.Transform);
                }

                return list;
            }
        }

        public void Configure(int poolCapacity, DebrisPlanner planner)
        {
            capacity = Mathf.Max(1, poolCapacity);
            _planner = planner;
            DestroyPool();
        }

        /// <summary>Shows debris for every block a replica delta removes.</summary>
        public void Attach(ClientWorldReplica replica)
        {
            Detach();
            _replica = replica;
            // Combined, not assigned: audio listens to the same hook.
            _onBlockChanged ??= OnBlockChanged;
            replica.BlockChanged += _onBlockChanged;
        }

        public void Detach()
        {
            if (_replica != null && _onBlockChanged != null)
            {
                _replica.BlockChanged -= _onBlockChanged;
            }

            _replica = null;
        }

        public void SpawnFor(BlockPos block, byte blockType)
        {
            EnsurePool();
            _planned.Clear();
            _planner.Plan(block, blockType, _planned);
            foreach (DebrisSpawn spawn in _planned)
            {
                Activate(spawn);
            }
        }

        /// <summary>Advances all pieces; Update calls this, tests call it directly.</summary>
        public void Tick(float deltaTime)
        {
            _planner.BeginFrame();
            for (int i = 0; i < _pieces.Length; i++)
            {
                ref Piece p = ref _pieces[i];
                if (!p.Active)
                {
                    continue;
                }

                p.Age += deltaTime;
                if (p.Age >= lifetime)
                {
                    p.Active = false;
                    p.Transform.gameObject.SetActive(false);
                    ActiveCount--;
                    continue;
                }

                p.Velocity.y += gravity * deltaTime;
                p.Position += p.Velocity * deltaTime;
                p.Transform.SetPositionAndRotation(p.Position, Quaternion.Euler(p.Age * 360f, p.Age * 240f, 0f));
                p.Transform.localScale = Vector3.one * (pieceSize * (1f - p.Age / lifetime));
            }
        }

        private void Update() => Tick(Time.deltaTime);

        private void OnDestroy()
        {
            Detach();
            DestroyPool();
        }

        private void OnBlockChanged(ChunkCoord chunk, ushort index, byte oldType, byte newType)
        {
            if (newType == BlockIds.Air && oldType != BlockIds.Air)
            {
                SpawnFor(ChunkMath.ToWorld(chunk, LocalBlock.FromIndex(index)), oldType);
            }
        }

        private void Activate(DebrisSpawn spawn)
        {
            ref Piece p = ref _pieces[_next];
            _next = (_next + 1) % _pieces.Length;
            if (p.Active)
            {
                Recycled++;
            }
            else
            {
                ActiveCount++;
            }

            p.Active = true;
            p.Age = 0f;
            p.Position = new Vector3(spawn.X, spawn.Y, spawn.Z);
            p.Velocity = new Vector3(spawn.Vx, spawn.Vy, spawn.Vz);
            p.Transform.position = p.Position;
            p.Transform.localScale = Vector3.one * pieceSize;
            _props!.SetColor(BaseColor, ColorOf(spawn.BlockType));
            p.Renderer.SetPropertyBlock(_props);
            p.Transform.gameObject.SetActive(true);
            Spawned++;
        }

        private void EnsurePool()
        {
            if (_pieces.Length == capacity)
            {
                return;
            }

            DestroyPool();
            _props = new MaterialPropertyBlock();
            _mesh = CreateCubeMesh();
            _pieces = new Piece[capacity];
            for (int i = 0; i < capacity; i++)
            {
                var go = new GameObject("Debris");
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = _mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                go.SetActive(false);
                _pieces[i] = new Piece { Transform = go.transform, Renderer = renderer };
            }
        }

        private void DestroyPool()
        {
            foreach (Piece p in _pieces)
            {
                if (p.Transform != null)
                {
                    DestroyImmediateOrLater(p.Transform.gameObject);
                }
            }

            if (_mesh != null)
            {
                DestroyImmediateOrLater(_mesh);
            }

            _pieces = System.Array.Empty<Piece>();
            _mesh = null;
            _next = 0;
            ActiveCount = 0;
        }

        private static void DestroyImmediateOrLater(Object obj)
        {
            if (Application.isPlaying)
            {
                Destroy(obj);
            }
            else
            {
                DestroyImmediate(obj);
            }
        }

        // Placeholder tints until debris uses the voxel atlas.
        private static Color ColorOf(byte blockType) => blockType switch
        {
            BlockIds.Grass => new Color(0.36f, 0.62f, 0.25f),
            BlockIds.Dirt => new Color(0.47f, 0.33f, 0.2f),
            BlockIds.Wood => new Color(0.6f, 0.42f, 0.22f),
            BlockIds.Brick => new Color(0.66f, 0.27f, 0.2f),
            BlockIds.Sand => new Color(0.86f, 0.8f, 0.55f),
            BlockIds.Glass => new Color(0.7f, 0.9f, 1f),
            BlockIds.Metal => new Color(0.65f, 0.67f, 0.7f),
            BlockIds.Asphalt => new Color(0.2f, 0.2f, 0.22f),
            _ => new Color(0.5f, 0.5f, 0.52f),
        };

        private static Mesh CreateCubeMesh()
        {
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh mesh = Instantiate(primitive.GetComponent<MeshFilter>().sharedMesh);
            mesh.name = "DebrisCube";
            DestroyImmediateOrLater(primitive);
            return mesh;
        }

        private struct Piece
        {
            public Transform Transform;
            public MeshRenderer Renderer;
            public Vector3 Position;
            public Vector3 Velocity;
            public float Age;
            public bool Active;
        }
    }
}
