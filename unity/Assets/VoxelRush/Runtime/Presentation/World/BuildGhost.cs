using System.Collections.Generic;
using UnityEngine;
using VoxelRush.World.Chunks;
using VoxelRush.World.Meshing;

namespace VoxelRush.Presentation.World
{
    /// <summary>
    /// Local-only placement preview (CONTROLS_ASSETS Build mode): the exact shape the meshers will produce for a block
    /// id (cube or turned ramp), tinted valid/invalid. Never collides and never reaches the server.
    /// </summary>
    public sealed class BuildGhost
    {
        // Slightly larger than a block so it doesn't z-fight the faces it touches; lifted like the world mesh.
        private const float Grow = 0.02f;
        private const float SurfaceLift = 0.02f;

        private readonly Dictionary<byte, Mesh> _meshes = new Dictionary<byte, Mesh>();
        private readonly ChunkMesher _mesher = new ChunkMesher(BlockAtlas.Default);
        private readonly ChunkMeshData _data = new ChunkMeshData();
        private readonly ChunkMeshBuilder _builder = new ChunkMeshBuilder();
        private readonly ChunkData?[] _noNeighbours = new ChunkData?[6];
        private readonly GameObject _object;
        private readonly MeshFilter _filter;
        private readonly Renderer _renderer;
        private readonly MaterialPropertyBlock _properties = new MaterialPropertyBlock();

        public BuildGhost(Transform parent)
        {
            // A primitive's renderer brings the pipeline's default lit material, present in every client build.
            _object = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _object.name = "BuildGhost";
            // Immediately: nothing under NetworkWorld may collide, not even for a frame.
            Object.DestroyImmediate(_object.GetComponent<Collider>());
            _object.transform.SetParent(parent, false);
            _object.transform.localScale = Vector3.one * (1f + 2f * Grow);
            _filter = _object.GetComponent<MeshFilter>();
            _renderer = _object.GetComponent<Renderer>();
            _object.SetActive(false);
        }

        public bool Visible => _object != null && _object.activeSelf;

        public byte BlockType { get; private set; }

        public Color Color { get; private set; }

        public Vector3 Position => _object.transform.position;

        public void Show(BlockPos block, byte blockType, Color color)
        {
            if (!_meshes.TryGetValue(blockType, out Mesh mesh))
            {
                mesh = BuildMesh(blockType);
                _meshes[blockType] = mesh;
            }

            _filter.sharedMesh = mesh;
            BlockType = blockType;
            Color = color;
            _object.transform.position = new Vector3(block.X - Grow, block.Y - Grow + SurfaceLift, block.Z - Grow);
            _properties.SetColor("_BaseColor", color);
            _properties.SetColor("_Color", color);
            _renderer.SetPropertyBlock(_properties);
            _object.SetActive(true);
        }

        public void Hide()
        {
            if (_object != null)
            {
                _object.SetActive(false);
            }
        }

        public void Destroy()
        {
            foreach (Mesh mesh in _meshes.Values)
            {
                Object.Destroy(mesh);
            }

            _meshes.Clear();
            if (_object != null)
            {
                Object.Destroy(_object);
            }
        }

        // The block alone in an empty chunk: every face, the same geometry the world shows once it's placed.
        private Mesh BuildMesh(byte blockType)
        {
            var chunk = new ChunkData(new ChunkCoord(0, 0, 0), 1, new byte[ChunkMath.Volume]);
            chunk.Set(new LocalBlock(0, 0, 0), blockType);
            _mesher.Build(chunk, _noNeighbours, _data);
            var mesh = new Mesh { name = $"Ghost {blockType}" };
            _builder.Upload(_data, mesh);
            return mesh;
        }
    }
}
