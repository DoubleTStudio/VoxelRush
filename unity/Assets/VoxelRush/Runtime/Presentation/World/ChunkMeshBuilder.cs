using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using VoxelRush.World.Meshing;

namespace VoxelRush.Presentation.World
{
    /// <summary>Uploads engine-free <see cref="ChunkMeshData"/> into a Unity mesh; buffers are reused across chunks.</summary>
    public sealed class ChunkMeshBuilder
    {
        private readonly List<Vector3> _positions = new List<Vector3>();
        private readonly List<Vector3> _normals = new List<Vector3>();
        private readonly List<Vector2> _uvs = new List<Vector2>();
        private readonly List<Vector2> _tiles = new List<Vector2>();

        public void Upload(ChunkMeshData data, Mesh mesh)
        {
            mesh.Clear();
            if (data.IsEmpty)
            {
                return;
            }

            _positions.Clear();
            _normals.Clear();
            _uvs.Clear();
            _tiles.Clear();
            for (int v = 0; v < data.VertexCount; v++)
            {
                _positions.Add(new Vector3(data.Positions[v * 3], data.Positions[v * 3 + 1], data.Positions[v * 3 + 2]));
                _normals.Add(new Vector3(data.Normals[v * 3], data.Normals[v * 3 + 1], data.Normals[v * 3 + 2]));
                _uvs.Add(new Vector2(data.Uvs[v * 2], data.Uvs[v * 2 + 1]));
                _tiles.Add(new Vector2(data.Tiles[v], 0f));
            }

            // 16-bit indices hold any chunk except pathological checkerboards; switch only when needed.
            mesh.indexFormat = data.VertexCount > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(_positions);
            mesh.SetNormals(_normals);
            mesh.SetUVs(0, _uvs);
            mesh.SetUVs(1, _tiles);
            mesh.SetTriangles(data.Indices, 0, calculateBounds: true);
        }
    }
}
