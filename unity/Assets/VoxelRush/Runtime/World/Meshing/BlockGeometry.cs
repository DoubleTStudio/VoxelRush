using System;
using System.Collections.Generic;
using VoxelRush.World.Chunks;

namespace VoxelRush.World.Meshing
{
    /// <summary>
    /// Shared face tables for both meshers (BUILD-002), so render meshes and colliders agree bit for bit.
    /// Each block face has a profile: none, full (greedy-merged, hides any neighbour face) or a partial shape that only
    /// hides an identical partial face across the border. Ramps add per-cell "templates" for their slope and partial faces.
    /// </summary>
    public static class BlockGeometry
    {
        public const byte NoFace = 0;
        public const byte FullFace = 1;
        /// <summary>A 1 × 0.5 rectangle on the lower half of the face (1:2 ramp ends).</summary>
        public const byte LowerHalf = 2;
        /// <summary>A 1 × <see cref="BlockShapes.KickerHeight"/> rectangle on the lower part of the face (kicker back).</summary>
        public const byte KickerEnd = 3;
        // Ramp side faces use the ramp's own id (≥ 13): only the same ramp at the same yaw has the same triangle.

        /// <summary>Template direction for faces inside the cell (slopes): never culled.</summary>
        public const int Inside = 6;

        private static readonly byte[] Profiles = new byte[256 * 6];
        private static readonly int[] Starts = new int[257];
        private static readonly List<int> Directions = new List<int>();
        private static readonly List<byte> TemplateProfiles = new List<byte>();
        private static readonly List<byte> Faces = new List<byte>();
        private static readonly List<byte> VertexCounts = new List<byte>();
        private static readonly List<float> Positions = new List<float>();
        private static readonly List<float> Normals = new List<float>();
        private static readonly List<float> Uvs = new List<float>();

        static BlockGeometry()
        {
            for (int id = 0; id < 256; id++)
            {
                Starts[id] = Directions.Count;
                BlockShape shape = BlockShapes.Shape((byte)id);
                for (int dir = 0; dir < 6; dir++)
                {
                    Profiles[id * 6 + dir] = shape == BlockShape.Empty ? NoFace : shape == BlockShape.Cube ? FullFace : RampProfile((byte)id, dir);
                }

                if (shape >= BlockShape.RampLow)
                {
                    AddRampTemplates((byte)id);
                }
            }

            Starts[256] = Directions.Count;
            TemplateCount = Directions.Count;
        }

        public static int TemplateCount { get; }

        /// <summary>Profile of a block's face in a direction (+X, -X, +Y, -Y, +Z, -Z).</summary>
        public static byte Profile(byte id, int direction) => Profiles[id * 6 + direction];

        /// <summary>Whether a partial or full face is covered by the neighbour's face across the border.</summary>
        public static bool IsHidden(byte profile, byte neighbourProfile) =>
            neighbourProfile == FullFace || (profile > FullFace && profile == neighbourProfile);

        public static int TemplateStart(byte id) => Starts[id];

        public static int TemplateEnd(byte id) => Starts[id + 1];

        public static int TemplateDirection(int t) => Directions[t];

        public static byte TemplateProfile(int t) => TemplateProfiles[t];

        public static BlockFace TemplateFace(int t) => (BlockFace)Faces[t];

        /// <summary>3 (triangle) or 4 (quad: triangles 0-1-2 and 0-2-3), front-facing toward the normal.</summary>
        public static int TemplateVertexCount(int t) => VertexCounts[t];

        public static float TemplatePosition(int t, int vertex, int axis) => Positions[t * 12 + vertex * 3 + axis];

        public static float TemplateUv(int t, int vertex, int axis) => Uvs[t * 8 + vertex * 2 + axis];

        public static float TemplateNormal(int t, int axis) => Normals[t * 3 + axis];

        /// <summary>Flat copies for native buffers (Burst mesher).</summary>
        public static void CopyTables(byte[] profiles, int[] starts, int[] directions, byte[] templateProfiles, byte[] faces, byte[] vertexCounts, float[] positions, float[] normals, float[] uvs)
        {
            Array.Copy(Profiles, profiles, Profiles.Length);
            Array.Copy(Starts, starts, Starts.Length);
            Directions.CopyTo(directions);
            TemplateProfiles.CopyTo(templateProfiles);
            Faces.CopyTo(faces);
            VertexCounts.CopyTo(vertexCounts);
            Positions.CopyTo(positions);
            Normals.CopyTo(normals);
            Uvs.CopyTo(uvs);
        }

        private static byte RampProfile(byte id, int direction)
        {
            BlockShapes.RampHeights(BlockShapes.Shape(id), out float low, out float high);
            int uphill = BlockShapes.UphillDirection(BlockShapes.Yaw(id));
            if (direction == BlockShapes.NegY)
            {
                return FullFace;
            }

            if (direction == BlockShapes.PosY)
            {
                return NoFace;
            }

            if (direction == uphill)
            {
                return EndProfile(high);
            }

            if (direction == BlockShapes.Opposite(uphill))
            {
                return EndProfile(low);
            }

            return id;
        }

        // A ramp end is a lower rectangle of the end's height.
        private static byte EndProfile(float height) =>
            height <= 0f ? NoFace : height >= 1f ? FullFace : height == 0.5f ? LowerHalf : height == BlockShapes.KickerHeight ? KickerEnd
            : throw new InvalidOperationException($"No face profile for a {height} m ramp end.");

        // Built uphill +Z, then turned about the cell centre; positions are multiples of 0.5, so exact in floats.
        private static void AddRampTemplates(byte id)
        {
            BlockShapes.RampHeights(BlockShapes.Shape(id), out float low, out float high);
            int yaw = BlockShapes.Yaw(id);
            float rise = high - low;
            float length = (float)Math.Sqrt(1f + rise * rise);
            Add(id, yaw, Inside, NoFace, BlockFace.Top, (0f, 1f / length, -rise / length),
                new[] { (0f, low, 0f), (1f, low, 0f), (1f, high, 1f), (0f, high, 1f) }, new[] { (0f, 0f), (1f, 0f), (1f, 1f), (0f, 1f) });

            for (int side = 0; side < 2; side++)
            {
                float x = side;
                var corners = low > 0f
                    ? new[] { (x, 0f, 0f), (x, 0f, 1f), (x, high, 1f), (x, low, 0f) }
                    : new[] { (x, 0f, 0f), (x, 0f, 1f), (x, high, 1f) };
                var uv = low > 0f ? new[] { (0f, 0f), (1f, 0f), (1f, high), (0f, low) } : new[] { (0f, 0f), (1f, 0f), (1f, high) };
                Add(id, yaw, side == 0 ? BlockShapes.NegX : BlockShapes.PosX, id, BlockFace.Side, (side == 0 ? -1f : 1f, 0f, 0f), corners, uv);
            }

            if (high < 1f)
            {
                Add(id, yaw, BlockShapes.PosZ, EndProfile(high), BlockFace.Side, (0f, 0f, 1f),
                    new[] { (0f, 0f, 1f), (1f, 0f, 1f), (1f, high, 1f), (0f, high, 1f) }, new[] { (0f, 0f), (1f, 0f), (1f, high), (0f, high) });
            }

            if (low > 0f)
            {
                Add(id, yaw, BlockShapes.NegZ, EndProfile(low), BlockFace.Side, (0f, 0f, -1f),
                    new[] { (0f, 0f, 0f), (1f, 0f, 0f), (1f, low, 0f), (0f, low, 0f) }, new[] { (0f, 0f), (1f, 0f), (1f, low), (0f, low) });
            }
        }

        private static void Add(byte id, int yaw, int direction, byte profile, BlockFace face, (float X, float Y, float Z) normal,
            (float X, float Y, float Z)[] corners, (float U, float V)[] uvs)
        {
            for (int k = 0; k < yaw; k++)
            {
                // A quarter turn maps +Z to +X: (x, z) → (z, -x) about the cell centre.
                for (int c = 0; c < corners.Length; c++)
                {
                    corners[c] = (corners[c].Z, corners[c].Y, 1f - corners[c].X);
                }

                normal = (normal.Z, normal.Y, -normal.X);
                direction = direction switch
                {
                    BlockShapes.PosZ => BlockShapes.PosX,
                    BlockShapes.PosX => BlockShapes.NegZ,
                    BlockShapes.NegZ => BlockShapes.NegX,
                    BlockShapes.NegX => BlockShapes.PosZ,
                    _ => direction,
                };
            }

            // Unity's front face: cross(b - a, c - a) points along the normal; flip the order otherwise.
            var (a, b, c2) = (corners[0], corners[1], corners[2]);
            float cx = (b.Y - a.Y) * (c2.Z - a.Z) - (b.Z - a.Z) * (c2.Y - a.Y);
            float cy = (b.Z - a.Z) * (c2.X - a.X) - (b.X - a.X) * (c2.Z - a.Z);
            float cz = (b.X - a.X) * (c2.Y - a.Y) - (b.Y - a.Y) * (c2.X - a.X);
            if (cx * normal.X + cy * normal.Y + cz * normal.Z < 0f)
            {
                Array.Reverse(corners, 1, corners.Length - 1);
                Array.Reverse(uvs, 1, uvs.Length - 1);
            }

            Directions.Add(direction);
            TemplateProfiles.Add(profile);
            Faces.Add((byte)face);
            VertexCounts.Add((byte)corners.Length);
            for (int c = 0; c < 4; c++)
            {
                (float X, float Y, float Z) p = corners[Math.Min(c, corners.Length - 1)];
                Positions.Add(p.X);
                Positions.Add(p.Y);
                Positions.Add(p.Z);
                (float U, float V) uv = uvs[Math.Min(c, uvs.Length - 1)];
                Uvs.Add(uv.U);
                Uvs.Add(uv.V);
            }

            Normals.Add(normal.X);
            Normals.Add(normal.Y);
            Normals.Add(normal.Z);
        }
    }
}
