using UnityEngine;

namespace VoxelRush.Presentation.Store
{
    /// <summary>
    /// Procedural meshes, texture and sound for the non-paint cosmetics (COSM-002): no imported assets, built once
    /// and shared by every car.
    /// </summary>
    public static class CosmeticAssets
    {
        public const int HornSampleRate = 22050;

        public static readonly Color DecalColor = new Color(1f, 0.84f, 0.1f);
        public static readonly Color SparkColor = new Color(1f, 0.55f, 0.1f);
        public static readonly Color FlagGold = new Color(1f, 0.78f, 0.2f);
        public static readonly Color FlagDark = new Color(0.12f, 0.1f, 0.05f);

        private static Mesh? s_lightning;
        private static Mesh? s_flagCloth;
        private static Mesh? s_box;
        private static Texture2D? s_checkered;
        private static AudioClip? s_horn;

        /// <summary>A lightning bolt in the XZ plane (unit square, both faces), for the hood.</summary>
        public static Mesh Lightning => s_lightning != null ? s_lightning : s_lightning = BuildLightning();

        /// <summary>A unit flag cloth in the YZ plane, hanging back (-Z) from its pole at the origin (both faces).</summary>
        public static Mesh FlagCloth => s_flagCloth != null ? s_flagCloth : s_flagCloth = BuildFlagCloth();

        /// <summary>A unit cube (pole and spark particles).</summary>
        public static Mesh Box => s_box != null ? s_box : s_box = BuildBox();

        /// <summary>Gold and dark checks, 8 × 4, point-filtered so they stay crisp.</summary>
        public static Texture2D Checkered => s_checkered != null ? s_checkered : s_checkered = BuildCheckered();

        /// <summary>A short two-tone square-wave "beep-beep".</summary>
        public static AudioClip Horn => s_horn != null ? s_horn : s_horn = BuildHorn();

        private static Mesh BuildLightning()
        {
            // Two overlapping wedges read as a bolt at a glance.
            Vector3[] bolt =
            {
                new Vector3(0.2f, 0f, 0.5f), new Vector3(-0.25f, 0f, -0.06f), new Vector3(0.1f, 0f, 0.06f),
                new Vector3(-0.2f, 0f, -0.5f), new Vector3(0.25f, 0f, 0.06f), new Vector3(-0.1f, 0f, -0.06f),
            };

            return DoubleSided("CosmeticLightning", bolt, new[] { 0, 1, 2, 3, 4, 5 }, null);
        }

        private static Mesh BuildFlagCloth()
        {
            Vector3[] quad = { new Vector3(0f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 1f, -1f), new Vector3(0f, 0f, -1f) };
            Vector2[] uv = { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) };
            return DoubleSided("CosmeticFlagCloth", quad, new[] { 0, 1, 2, 0, 2, 3 }, uv);
        }

        // Front and back copies with their own normals, so the shape lights the same from either side.
        private static Mesh DoubleSided(string name, Vector3[] points, int[] triangles, Vector2[]? uv)
        {
            int n = points.Length;
            var vertices = new Vector3[n * 2];
            var uvs = new Vector2[n * 2];
            var tris = new int[triangles.Length * 2];
            for (int i = 0; i < n; i++)
            {
                vertices[i] = vertices[i + n] = points[i];
                uvs[i] = uvs[i + n] = uv != null ? uv[i] : new Vector2(points[i].x + 0.5f, points[i].z + 0.5f);
            }

            for (int t = 0; t < triangles.Length; t += 3)
            {
                tris[t] = triangles[t];
                tris[t + 1] = triangles[t + 1];
                tris[t + 2] = triangles[t + 2];
                tris[triangles.Length + t] = triangles[t] + n;
                tris[triangles.Length + t + 1] = triangles[t + 2] + n;
                tris[triangles.Length + t + 2] = triangles[t + 1] + n;
            }

            var mesh = new Mesh { name = name, vertices = vertices, uv = uvs, triangles = tris };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh BuildBox()
        {
            var vertices = new Vector3[24];
            var normals = new Vector3[24];
            var uv = new Vector2[24];
            var tris = new int[36];
            Vector3[] axes = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            for (int f = 0; f < 6; f++)
            {
                Vector3 n = axes[f];
                Vector3 u = Mathf.Abs(n.y) > 0.5f ? Vector3.right : Vector3.up;
                Vector3 v = Vector3.Cross(n, u);
                for (int c = 0; c < 4; c++)
                {
                    float su = c == 1 || c == 2 ? 0.5f : -0.5f;
                    float sv = c >= 2 ? 0.5f : -0.5f;
                    vertices[f * 4 + c] = (n * 0.5f) + (u * su) + (v * sv);
                    normals[f * 4 + c] = n;
                    uv[f * 4 + c] = new Vector2(su + 0.5f, sv + 0.5f);
                }

                int b = f * 4;
                int[] quad = { b, b + 1, b + 2, b, b + 2, b + 3 };
                // Wind each face so it faces outwards (Unity front faces are clockwise).
                Vector3 normal = Vector3.Cross(vertices[b + 1] - vertices[b], vertices[b + 2] - vertices[b]);
                if (Vector3.Dot(normal, n) < 0)
                {
                    quad = new[] { b, b + 2, b + 1, b, b + 3, b + 2 };
                }

                quad.CopyTo(tris, f * 6);
            }

            var mesh = new Mesh { name = "CosmeticBox", vertices = vertices, normals = normals, uv = uv, triangles = tris };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Texture2D BuildCheckered()
        {
            const int cell = 4;
            var texture = new Texture2D(8 * cell, 4 * cell, TextureFormat.RGBA32, false)
            {
                name = "CosmeticCheckeredGold",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            var pixels = new Color[texture.width * texture.height];
            for (int y = 0; y < texture.height; y++)
            {
                for (int x = 0; x < texture.width; x++)
                {
                    pixels[(y * texture.width) + x] = ((x / cell) + (y / cell)) % 2 == 0 ? FlagGold : FlagDark;
                }
            }

            texture.SetPixels(pixels);
            texture.Apply(false, false);
            return texture;
        }

        private static AudioClip BuildHorn()
        {
            const float beep = 0.16f;
            const float gap = 0.06f;
            int samples = Mathf.CeilToInt((beep * 2f + gap) * HornSampleRate);
            var data = new float[samples];
            for (int i = 0; i < samples; i++)
            {
                float t = i / (float)HornSampleRate;
                float local = t < beep ? t : t - beep - gap;
                if (local < 0f || local > beep)
                {
                    continue;
                }

                // Two square waves a major third apart, the classic toy-car honk; short ramps avoid clicks.
                float a = Mathf.Sign(Mathf.Sin(2f * Mathf.PI * 440f * t));
                float b = Mathf.Sign(Mathf.Sin(2f * Mathf.PI * 554.37f * t));
                float envelope = Mathf.Clamp01(local / 0.01f) * Mathf.Clamp01((beep - local) / 0.02f);
                data[i] = (a + b) * 0.2f * envelope;
            }

            AudioClip clip = AudioClip.Create("CosmeticRetroHorn", samples, 1, HornSampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
