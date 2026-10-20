using System.Collections.Generic;

namespace VoxelRush.World.Meshing
{
    /// <summary>
    /// Engine-free mesh buffers in chunk-local block units. Per vertex: position (xyz), normal (xyz), uv in block units
    /// (so the shader tiles across merged quads) and the atlas tile index. Two triangles per quad, counter-clockwise
    /// when seen from outside (Unity's front face).
    /// </summary>
    public sealed class ChunkMeshData
    {
        public List<float> Positions { get; } = new List<float>();

        public List<float> Normals { get; } = new List<float>();

        public List<float> Uvs { get; } = new List<float>();

        public List<float> Tiles { get; } = new List<float>();

        public List<int> Indices { get; } = new List<int>();

        public int QuadCount => Indices.Count / 6;

        public int VertexCount => Positions.Count / 3;

        public bool IsEmpty => Indices.Count == 0;

        public void Clear()
        {
            Positions.Clear();
            Normals.Clear();
            Uvs.Clear();
            Tiles.Clear();
            Indices.Clear();
        }

        internal void AddQuad(int direction, int d, int u, int v, int plane, int i, int j, int width, int height, int tile)
        {
            int start = VertexCount;
            float[] normal = { 0, 0, 0 };
            normal[d] = direction % 2 == 0 ? 1 : -1;
            for (int corner = 0; corner < 4; corner++)
            {
                int du = corner == 1 || corner == 2 ? width : 0;
                int dv = corner >= 2 ? height : 0;
                float[] position = new float[3];
                position[d] = plane;
                position[u] = i + du;
                position[v] = j + dv;
                Positions.Add(position[0]);
                Positions.Add(position[1]);
                Positions.Add(position[2]);
                Normals.Add(normal[0]);
                Normals.Add(normal[1]);
                Normals.Add(normal[2]);
                Uvs.Add(du);
                Uvs.Add(dv);
                Tiles.Add(tile);
            }

            // Front face = cross(b - a, c - a) along the normal (Unity's convention). With (d, u, v) cyclic,
            // corners 0→1→2 do that for +direction faces; -direction faces use the reverse order.
            bool positive = direction % 2 == 0;
            if (positive)
            {
                Indices.Add(start); Indices.Add(start + 1); Indices.Add(start + 2);
                Indices.Add(start); Indices.Add(start + 2); Indices.Add(start + 3);
            }
            else
            {
                Indices.Add(start); Indices.Add(start + 2); Indices.Add(start + 1);
                Indices.Add(start); Indices.Add(start + 3); Indices.Add(start + 2);
            }
        }
    }
}
