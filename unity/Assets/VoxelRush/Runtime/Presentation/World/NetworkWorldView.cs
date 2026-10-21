using System.Collections.Generic;
using UnityEngine;
using VoxelRush.Networking;
using VoxelRush.World.Chunks;
using VoxelRush.World.Meshing;
using VoxelRush.World.Sync;

namespace VoxelRush.Presentation.World
{
    /// <summary>
    /// Renders the server-synced world replica (MVP-002): one mesh per non-empty chunk, remeshed when a snapshot
    /// replaces a chunk or a delta bumps its revision, within a per-frame budget. Also feeds debris from deltas.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NetworkWorldView : MonoBehaviour
    {
        // Lifts the voxel surface off the arena ground box (both top out at y = 0) to avoid z-fighting.
        private const float SurfaceLift = 0.02f;

        [SerializeField] private Material? _material;
        [SerializeField] private DebrisEffects? _debris;
        [SerializeField, Min(1)] private int _meshBudgetPerFrame = 12;

        private readonly Dictionary<ChunkCoord, GameObject> _chunkObjects = new Dictionary<ChunkCoord, GameObject>();
        private readonly Dictionary<ChunkCoord, (ChunkData Chunk, ulong Revision)> _seen = new Dictionary<ChunkCoord, (ChunkData, ulong)>();
        private readonly List<ChunkCoord> _region = new List<ChunkCoord>();
        private readonly List<ChunkCoord> _dirtyOrder = new List<ChunkCoord>();
        private readonly HashSet<ChunkCoord> _dirty = new HashSet<ChunkCoord>();
        private readonly ChunkData?[] _neighbours = new ChunkData?[6];
        private readonly ChunkMesher _mesher = new ChunkMesher(BlockAtlas.Default);
        private readonly ChunkMeshData _meshData = new ChunkMeshData();
        private readonly ChunkMeshBuilder _builder = new ChunkMeshBuilder();
        private ClientWorldReplica? _replica;
        private ClientWorldReplica? _override;

        public IReadOnlyDictionary<ChunkCoord, GameObject> ChunkObjects => _chunkObjects;

        public int PendingChunks => _dirty.Count;

        /// <summary>Renders this replica instead of the connected client's (tests, offline previews).</summary>
        public void Show(ClientWorldReplica? replica) => _override = replica;

        /// <summary>Picks up replica changes and remeshes; Update calls this, tests call it directly.</summary>
        public void Tick()
        {
            ClientWorldReplica? replica = _override ?? ClientBootstrap.Instance?.World?.Replica;
            if (replica != _replica)
            {
                Bind(replica);
            }

            if (_replica == null)
            {
                return;
            }

            foreach (ChunkCoord coord in _region)
            {
                ChunkData? chunk = _replica.Get(coord);
                if (chunk == null)
                {
                    continue;
                }

                // Snapshots replace the chunk object; deltas bump its revision.
                if (!_seen.TryGetValue(coord, out var seen) || seen.Chunk != chunk || seen.Revision != chunk.Revision)
                {
                    _seen[coord] = (chunk, chunk.Revision);
                    MarkDirty(coord);
                    // Faces on shared borders depend on the neighbour too.
                    MarkDirty(coord.Offset(1, 0, 0));
                    MarkDirty(coord.Offset(-1, 0, 0));
                    MarkDirty(coord.Offset(0, 1, 0));
                    MarkDirty(coord.Offset(0, -1, 0));
                    MarkDirty(coord.Offset(0, 0, 1));
                    MarkDirty(coord.Offset(0, 0, -1));
                }
            }

            int built = 0;
            while (_dirtyOrder.Count > 0 && built < _meshBudgetPerFrame)
            {
                ChunkCoord coord = _dirtyOrder[_dirtyOrder.Count - 1];
                _dirtyOrder.RemoveAt(_dirtyOrder.Count - 1);
                _dirty.Remove(coord);
                if (Rebuild(coord))
                {
                    built++;
                }
            }
        }

        private void Bind(ClientWorldReplica? replica)
        {
            Clear();
            _replica = replica;
            if (replica == null)
            {
                _debris?.Detach();
                return;
            }

            _region.AddRange(replica.Info.RegionChunks());
            _debris?.Attach(replica);
        }

        private void MarkDirty(ChunkCoord coord)
        {
            if (_replica?.Get(coord) != null && _dirty.Add(coord))
            {
                _dirtyOrder.Add(coord);
            }
        }

        // Returns false for chunks that were skipped without meshing work.
        private bool Rebuild(ChunkCoord coord)
        {
            ChunkData? chunk = _replica!.Get(coord);
            if (chunk == null)
            {
                return false;
            }

            _neighbours[0] = _replica.Get(coord.Offset(1, 0, 0));
            _neighbours[1] = _replica.Get(coord.Offset(-1, 0, 0));
            _neighbours[2] = _replica.Get(coord.Offset(0, 1, 0));
            _neighbours[3] = _replica.Get(coord.Offset(0, -1, 0));
            _neighbours[4] = _replica.Get(coord.Offset(0, 0, 1));
            _neighbours[5] = _replica.Get(coord.Offset(0, 0, -1));
            _mesher.Build(chunk, _neighbours, _meshData);
            if (_meshData.IsEmpty)
            {
                Remove(coord);
                return true;
            }

            if (!_chunkObjects.TryGetValue(coord, out GameObject go))
            {
                go = new GameObject($"NetChunk {coord.X},{coord.Y},{coord.Z}");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = new Vector3(coord.X, coord.Y, coord.Z) * ChunkMath.Size + Vector3.up * SurfaceLift;
                go.AddComponent<MeshFilter>().sharedMesh = new Mesh { name = go.name };
                go.AddComponent<MeshRenderer>().sharedMaterial = _material;
                _chunkObjects[coord] = go;
            }

            _builder.Upload(_meshData, go.GetComponent<MeshFilter>().sharedMesh);
            return true;
        }

        private void Remove(ChunkCoord coord)
        {
            if (_chunkObjects.Remove(coord, out GameObject go) && go != null)
            {
                Destroy(go.GetComponent<MeshFilter>().sharedMesh);
                Destroy(go);
            }
        }

        private void Clear()
        {
            foreach (ChunkCoord coord in new List<ChunkCoord>(_chunkObjects.Keys))
            {
                Remove(coord);
            }

            _seen.Clear();
            _region.Clear();
            _dirty.Clear();
            _dirtyOrder.Clear();
        }

        private void Update() => Tick();

        private void OnDestroy()
        {
            _debris?.Detach();
            Clear();
        }
    }
}
