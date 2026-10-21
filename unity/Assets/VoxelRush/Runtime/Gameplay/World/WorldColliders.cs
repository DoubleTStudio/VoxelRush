using System;
using System.Collections.Generic;
using UnityEngine;
using VoxelRush.World.Chunks;
using VoxelRush.World.Meshing;
using VoxelRush.World.Sync;

namespace VoxelRush.Gameplay.World
{
    /// <summary>
    /// Mesh colliders for a synced world region (MVP-003), rebuilt when a chunk's revision changes. The server
    /// binds its authority and predicting clients their replica, so placed blocks collide the same on both.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WorldColliders : MonoBehaviour
    {
        [SerializeField, Min(1)] private int _rebuildBudgetPerFrame = 8;

        private readonly Dictionary<ChunkCoord, MeshCollider> _colliders = new Dictionary<ChunkCoord, MeshCollider>();
        private readonly Dictionary<ChunkCoord, (ChunkData Chunk, ulong Revision)> _seen = new Dictionary<ChunkCoord, (ChunkData, ulong)>();
        private readonly List<ChunkCoord> _region = new List<ChunkCoord>();
        private readonly List<ChunkCoord> _dirtyOrder = new List<ChunkCoord>();
        private readonly HashSet<ChunkCoord> _dirty = new HashSet<ChunkCoord>();
        private readonly ChunkData?[] _neighbours = new ChunkData?[6];
        private Func<WorldInfo?> _info = () => null;
        private Func<ChunkCoord, ChunkData?> _get = _ => null;
        private WorldInfo? _boundInfo;
        private BurstChunkMesher? _mesher;

        public int ColliderCount => _colliders.Count;

        public int PendingChunks => _dirty.Count;

        /// <param name="info">The world's region; may be null until a client has joined.</param>
        public void Bind(Func<WorldInfo?> info, Func<ChunkCoord, ChunkData?> get)
        {
            _info = info;
            _get = get;
            Clear();
        }

        /// <summary>Picks up changed chunks; <paramref name="all"/> skips the budget (startup, so cars spawn on solid ground).</summary>
        public void Refresh(bool all = false)
        {
            WorldInfo? info = _info();
            if (info != _boundInfo)
            {
                Clear();
                _boundInfo = info;
                if (info != null)
                {
                    _region.AddRange(info.RegionChunks());
                }
            }

            foreach (ChunkCoord coord in _region)
            {
                ChunkData? chunk = _get(coord);
                if (chunk == null)
                {
                    continue;
                }

                if (!_seen.TryGetValue(coord, out var seen) || seen.Chunk != chunk || seen.Revision != chunk.Revision)
                {
                    _seen[coord] = (chunk, chunk.Revision);
                    MarkDirty(coord);
                    MarkDirty(coord.Offset(1, 0, 0));
                    MarkDirty(coord.Offset(-1, 0, 0));
                    MarkDirty(coord.Offset(0, 1, 0));
                    MarkDirty(coord.Offset(0, -1, 0));
                    MarkDirty(coord.Offset(0, 0, 1));
                    MarkDirty(coord.Offset(0, 0, -1));
                }
            }

            int built = 0;
            while (_dirtyOrder.Count > 0 && (all || built < _rebuildBudgetPerFrame))
            {
                ChunkCoord coord = _dirtyOrder[_dirtyOrder.Count - 1];
                _dirtyOrder.RemoveAt(_dirtyOrder.Count - 1);
                _dirty.Remove(coord);
                Rebuild(coord);
                built++;
            }
        }

        private void MarkDirty(ChunkCoord coord)
        {
            if (_get(coord) != null && _dirty.Add(coord))
            {
                _dirtyOrder.Add(coord);
            }
        }

        private void Rebuild(ChunkCoord coord)
        {
            ChunkData? chunk = _get(coord);
            if (chunk == null)
            {
                return;
            }

            _neighbours[0] = _get(coord.Offset(1, 0, 0));
            _neighbours[1] = _get(coord.Offset(-1, 0, 0));
            _neighbours[2] = _get(coord.Offset(0, 1, 0));
            _neighbours[3] = _get(coord.Offset(0, -1, 0));
            _neighbours[4] = _get(coord.Offset(0, 0, 1));
            _neighbours[5] = _get(coord.Offset(0, 0, -1));
            _mesher ??= new BurstChunkMesher(BlockAtlas.Default);
            _mesher.Build(chunk, _neighbours);
            if (_mesher.IsEmpty)
            {
                Remove(coord);
                return;
            }

            if (!_colliders.TryGetValue(coord, out MeshCollider collider))
            {
                var go = new GameObject($"WorldCollider {coord.X},{coord.Y},{coord.Z}");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = new Vector3(coord.X, coord.Y, coord.Z) * ChunkMath.Size;
                go.isStatic = true;
                collider = go.AddComponent<MeshCollider>();
                _colliders[coord] = collider;
            }

            Mesh mesh = collider.sharedMesh != null ? collider.sharedMesh : new Mesh { name = collider.name };
            mesh.Clear();
            mesh.indexFormat = _mesher.Positions.Length > ushort.MaxValue ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.SetVertices(_mesher.Positions.AsArray());
            mesh.SetIndices(_mesher.Indices.AsArray(), MeshTopology.Triangles, 0, calculateBounds: true);
            // Reassigning makes PhysX re-cook the changed mesh.
            collider.sharedMesh = null;
            collider.sharedMesh = mesh;
        }

        private void Remove(ChunkCoord coord)
        {
            if (_colliders.Remove(coord, out MeshCollider collider) && collider != null)
            {
                if (collider.sharedMesh != null)
                {
                    Destroy(collider.sharedMesh);
                }

                Destroy(collider.gameObject);
            }
        }

        private void Clear()
        {
            foreach (ChunkCoord coord in new List<ChunkCoord>(_colliders.Keys))
            {
                Remove(coord);
            }

            _seen.Clear();
            _region.Clear();
            _dirty.Clear();
            _dirtyOrder.Clear();
            _boundInfo = null;
        }

        private void Update() => Refresh();

        private void OnDestroy()
        {
            _mesher?.Dispose();
            _mesher = null;
        }
    }
}
