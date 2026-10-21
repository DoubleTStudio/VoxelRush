using System.Collections.Generic;
using UnityEngine;
using VoxelRush.Gameplay.Vehicles;
using VoxelRush.World.Chunks;
using VoxelRush.World.Generation;
using VoxelRush.World.Meshing;
using VoxelRush.World.Streaming;

namespace VoxelRush.Gameplay.World
{
    /// <summary>
    /// Streams collidable voxel chunks around registered vehicles with per-step budgets (VR-023) and guarantees
    /// they never drive into a chunk whose collider isn't ready: spawn gating + <see cref="TravelGuard"/> before physics.
    /// Runs on the server (authority) and on predicting clients (same collision).
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public sealed class VoxelWorldStreamer : MonoBehaviour, IChunkBackend
    {
        // Half a car length: checked beyond this step's travel so the bumper never crosses into an unready column.
        private const float GuardMargin = 2.5f;

        [SerializeField] private long _seed = 12345;
        [SerializeField] private int _radiusChunks = 3;
        [SerializeField] private int _minChunkY = -1;
        [SerializeField] private int _maxChunkY = 2;
        [SerializeField] private int _generateBudget = 4;
        [SerializeField] private int _colliderBudget = 2;
        [SerializeField] private bool _autoStep = true;

        private readonly List<ArcadeVehicle> _vehicles = new List<ArcadeVehicle>();
        private readonly List<StreamingObserver> _observers = new List<StreamingObserver>();
        private readonly Dictionary<ChunkCoord, GameObject> _colliders = new Dictionary<ChunkCoord, GameObject>();
        private readonly HashSet<ArcadeVehicle> _waitingToSpawn = new HashSet<ArcadeVehicle>();
        private readonly ChunkMesher _mesher = new ChunkMesher(BlockAtlas.Default);
        private readonly ChunkMeshData _meshData = new ChunkMeshData();
        private readonly List<Vector3> _vertices = new List<Vector3>();
        private ChunkStreamer? _streamer;
        private IChunkSource? _sourceOverride;

        public ChunkStreamer Streamer => _streamer ??= CreateStreamer();

        public long GuardClamps { get; private set; }

        public int ColliderObjects => _colliders.Count;

        /// <summary>Overrides the terrain source (tests, and snapshot-backed worlds later). Call before the first step.</summary>
        public void Configure(IChunkSource source, int minChunkY, int maxChunkY, int generateBudget, int colliderBudget, bool autoStep = true)
        {
            _sourceOverride = source;
            _minChunkY = minChunkY;
            _maxChunkY = maxChunkY;
            _generateBudget = generateBudget;
            _colliderBudget = colliderBudget;
            _autoStep = autoStep;
            _streamer = null;
        }

        /// <summary>Tracks a vehicle; it stays kinematic (held in place) until its own column is collidable.</summary>
        public void Register(ArcadeVehicle vehicle)
        {
            if (_vehicles.Contains(vehicle))
            {
                return;
            }

            _vehicles.Add(vehicle);
            if (!IsReadyAt(vehicle.transform.position))
            {
                vehicle.Body.isKinematic = true;
                _waitingToSpawn.Add(vehicle);
            }
        }

        public void Unregister(ArcadeVehicle vehicle)
        {
            _vehicles.Remove(vehicle);
            _waitingToSpawn.Remove(vehicle);
        }

        public bool IsReadyAt(Vector3 position) => Streamer.IsBlockColumnReady(position.x, position.z);

        /// <summary>Streaming plus guards; the network tick loop calls this before physics when not auto-stepping.</summary>
        public void Step(float deltaTime)
        {
            StreamStep();
            GuardStep(deltaTime);
        }

        /// <summary>Budgeted streaming work; may run less often than physics under load.</summary>
        public void StreamStep()
        {
            _observers.Clear();
            foreach (ArcadeVehicle vehicle in _vehicles)
            {
                if (vehicle == null)
                {
                    continue;
                }

                Vector3 p = vehicle.transform.position;
                Vector3 v = vehicle.Body.isKinematic ? Vector3.zero : vehicle.Body.linearVelocity;
                _observers.Add(new StreamingObserver(p.x, p.y, p.z, v.x, v.z));
            }

            Streamer.Step(_observers);
            Streamer.RefreshStaleColliders();
            ReleaseSpawnedVehicles();
        }

        /// <summary>
        /// Safety invariant: must run before every physics step, independent of how often streaming runs
        /// (a guard tied to a throttled streaming step let a car cross a boundary between checks).
        /// </summary>
        public void GuardStep(float deltaTime) => GuardVehicles(deltaTime);

        void IChunkBackend.BuildCollider(ChunkData chunk, ChunkData?[] neighbours)
        {
            _mesher.Build(chunk, neighbours, _meshData);
            if (!_colliders.TryGetValue(chunk.Coord, out GameObject go))
            {
                go = new GameObject($"ChunkCollider {chunk.Coord.X},{chunk.Coord.Y},{chunk.Coord.Z}");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = new Vector3(chunk.Coord.X, chunk.Coord.Y, chunk.Coord.Z) * ChunkMath.Size;
                go.isStatic = true;
                go.AddComponent<MeshCollider>();
                _colliders[chunk.Coord] = go;
            }

            var collider = go.GetComponent<MeshCollider>();
            Mesh? mesh = collider.sharedMesh;
            if (_meshData.IsEmpty)
            {
                // Air chunk: ready, but nothing to collide with.
                collider.sharedMesh = null;
                collider.enabled = false;
                return;
            }

            if (mesh == null)
            {
                mesh = new Mesh { name = go.name };
            }

            _vertices.Clear();
            for (int i = 0; i < _meshData.VertexCount; i++)
            {
                _vertices.Add(new Vector3(_meshData.Positions[i * 3], _meshData.Positions[i * 3 + 1], _meshData.Positions[i * 3 + 2]));
            }

            mesh.Clear();
            mesh.indexFormat = _meshData.VertexCount > ushort.MaxValue ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(_vertices);
            mesh.SetTriangles(_meshData.Indices, 0, calculateBounds: true);
            // Reassigning makes PhysX re-cook the changed mesh (no stale collision after edits).
            collider.sharedMesh = null;
            collider.sharedMesh = mesh;
            collider.enabled = true;
        }

        void IChunkBackend.Remove(ChunkCoord coord)
        {
            if (_colliders.Remove(coord, out GameObject go) && go != null)
            {
                Mesh? mesh = go.GetComponent<MeshCollider>().sharedMesh;
                if (mesh != null)
                {
                    Destroy(mesh);
                }

                Destroy(go);
            }
        }

        private void FixedUpdate()
        {
            if (_autoStep)
            {
                Step(Time.fixedDeltaTime);
            }
        }

        private void ReleaseSpawnedVehicles()
        {
            if (_waitingToSpawn.Count == 0)
            {
                return;
            }

            _vehicles.ForEach(vehicle =>
            {
                if (_waitingToSpawn.Contains(vehicle) && IsReadyAt(vehicle.transform.position))
                {
                    vehicle.Body.isKinematic = false;
                    _waitingToSpawn.Remove(vehicle);
                }
            });
        }

        private void GuardVehicles(float deltaTime)
        {
            foreach (ArcadeVehicle vehicle in _vehicles)
            {
                if (vehicle == null || vehicle.Body.isKinematic)
                {
                    continue;
                }

                Vector3 p = vehicle.transform.position;
                Vector3 v = vehicle.Body.linearVelocity;
                float vx = v.x;
                float vz = v.z;
                if (TravelGuard.Clamp(p.x, p.z, ref vx, ref vz, deltaTime, GuardMargin, Streamer.IsBlockColumnReady))
                {
                    vehicle.Body.linearVelocity = new Vector3(vx, v.y, vz);
                    GuardClamps++;
                }
            }
        }

        private ChunkStreamer CreateStreamer() => new ChunkStreamer(
            _sourceOverride ?? new GeneratedChunkSource(new TerrainGenerator(_seed)),
            this,
            new StreamingSettings
            {
                RadiusChunks = _radiusChunks,
                MinChunkY = _minChunkY,
                MaxChunkY = _maxChunkY,
                GenerateBudget = _generateBudget,
                ColliderBudget = _colliderBudget,
            });
    }
}
