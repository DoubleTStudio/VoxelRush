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
            float nx = 0, ny = 0, nz = 0;
            Assign(d, direction % 2 == 0 ? 1 : -1, ref nx, ref ny, ref nz);
            for (int corner = 0; corner < 4; corner++)
            {
                int du = corner == 1 || corner == 2 ? width : 0;
                int dv = corner >= 2 ? height : 0;
                float x = 0, y = 0, z = 0;
                Assign(d, plane, ref x, ref y, ref z);
                Assign(u, i + du, ref x, ref y, ref z);
                Assign(v, j + dv, ref x, ref y, ref z);
                Positions.Add(x);
                Positions.Add(y);
                Positions.Add(z);
                Normals.Add(nx);
                Normals.Add(ny);
                Normals.Add(nz);
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

        // Writes one axis of a vector without allocating (the old per-quad float[] showed up as GC churn, VR-070).
        private static void Assign(int axis, float value, ref float x, ref float y, ref float z)
        {
            switch (axis)
            {
                case 0: x = value; break;
                case 1: y = value; break;
                default: z = value; break;
            }
        }
    }
}
