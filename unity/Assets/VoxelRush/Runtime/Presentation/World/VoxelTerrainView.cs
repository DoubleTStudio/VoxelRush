using System.Collections.Generic;
using UnityEngine;
using VoxelRush.World.Chunks;
using VoxelRush.World.Generation;
using VoxelRush.World.Meshing;

namespace VoxelRush.Presentation.World
{
    /// <summary>
    /// Renders a fixed region of generated chunks: one GameObject + Mesh per non-empty chunk (never per voxel).
    /// Streaming, budgets and colliders arrive in VR-023.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VoxelTerrainView : MonoBehaviour
    {
        [SerializeField] private long _seed = 12345;
        [SerializeField] private Vector3Int _minChunk = new Vector3Int(-2, -1, -2);
        [SerializeField] private Vector3Int _maxChunk = new Vector3Int(1, 2, 1);
        [SerializeField] private Material? _material;

        private readonly Dictionary<ChunkCoord, GameObject> _chunkObjects = new Dictionary<ChunkCoord, GameObject>();

        public IReadOnlyDictionary<ChunkCoord, GameObject> ChunkObjects => _chunkObjects;

        public int ChunksGenerated { get; private set; }

        public void Build()
        {
            Clear();
            var generator = new TerrainGenerator(_seed);
            var chunks = new Dictionary<ChunkCoord, ChunkData>();
            for (int x = _minChunk.x; x <= _maxChunk.x; x++)
            {
                for (int y = _minChunk.y; y <= _maxChunk.y; y++)
                {
                    for (int z = _minChunk.z; z <= _maxChunk.z; z++)
                    {
                        var coord = new ChunkCoord(x, y, z);
                        chunks[coord] = generator.Generate(coord);
                    }
                }
            }

            ChunksGenerated = chunks.Count;
            var mesher = new ChunkMesher(BlockAtlas.Default);
            var meshData = new ChunkMeshData();
            var builder = new ChunkMeshBuilder();
            var neighbours = new ChunkData?[6];
            foreach (KeyValuePair<ChunkCoord, ChunkData> entry in chunks)
            {
                ChunkCoord c = entry.Key;
                neighbours[0] = Lookup(chunks, c.Offset(1, 0, 0));
                neighbours[1] = Lookup(chunks, c.Offset(-1, 0, 0));
                neighbours[2] = Lookup(chunks, c.Offset(0, 1, 0));
                neighbours[3] = Lookup(chunks, c.Offset(0, -1, 0));
                neighbours[4] = Lookup(chunks, c.Offset(0, 0, 1));
                neighbours[5] = Lookup(chunks, c.Offset(0, 0, -1));
                mesher.Build(entry.Value, neighbours, meshData);
                if (meshData.IsEmpty)
                {
                    continue;
                }

                var go = new GameObject($"Chunk {c.X},{c.Y},{c.Z}");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = new Vector3(c.X, c.Y, c.Z) * ChunkMath.Size;
                var mesh = new Mesh { name = go.name };
                builder.Upload(meshData, mesh);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = _material;
                _chunkObjects[c] = go;
            }
        }

        public void Clear()
        {
            foreach (GameObject go in _chunkObjects.Values)
            {
                if (go != null)
                {
                    DestroyAny(go.GetComponent<MeshFilter>().sharedMesh);
                    DestroyAny(go);
                }
            }

            _chunkObjects.Clear();
        }

        // Edit-mode tools and previews build outside play mode, where Destroy isn't allowed.
        private static void DestroyAny(Object target)
        {
            if (Application.isPlaying)
            {
                Destroy(target);
            }
            else
            {
                DestroyImmediate(target);
            }
        }

        private static ChunkData? Lookup(Dictionary<ChunkCoord, ChunkData> chunks, ChunkCoord coord) =>
            chunks.TryGetValue(coord, out ChunkData chunk) ? chunk : null;

        private void Start() => Build();

        private void OnDestroy() => Clear();
    }
}
