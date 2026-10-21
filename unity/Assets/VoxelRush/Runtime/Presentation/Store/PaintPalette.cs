using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoxelRush.Presentation.Store
{
    /// <summary>Colours of the paint SKUs (<see cref="Core.Cosmetics.PaintCatalog"/>) and how a car body is tinted.</summary>
    public static class PaintPalette
    {
        private static readonly Dictionary<string, Color> Paints = new Dictionary<string, Color>
        {
            ["paint.racing-red"] = new Color(0.85f, 0.1f, 0.1f),
            ["paint.ocean-blue"] = new Color(0.1f, 0.35f, 0.85f),
            ["paint.sunset-stripes"] = new Color(1f, 0.5f, 0.15f),
            ["paint.chrome"] = new Color(0.82f, 0.84f, 0.88f),
        };

        public static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        /// <summary>Tint for a paint SKU (null = default colour).</summary>
        public static Color? ColorOf(string? sku) => sku != null && Paints.TryGetValue(sku, out Color c) ? c : (Color?)null;

        public static bool IsPaint(string sku) => Paints.ContainsKey(sku);

        /// <summary>Tints the car's body panels (wheels keep their colour); an unknown or empty SKU restores the default.</summary>
        public static void Apply(Transform car, string? sku, MaterialPropertyBlock block)
        {
            Color? color = ColorOf(sku);
            foreach (Renderer renderer in car.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.name.StartsWith("Body", StringComparison.Ordinal) && !renderer.name.StartsWith("Cabin", StringComparison.Ordinal))
                {
                    continue;
                }

                if (color is Color c)
                {
                    renderer.GetPropertyBlock(block);
                    block.SetColor(BaseColor, c);
                    renderer.SetPropertyBlock(block);
                }
                else
                {
                    renderer.SetPropertyBlock(null);
                }
            }
        }
    }
}
