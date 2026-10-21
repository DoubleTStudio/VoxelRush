using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace VoxelRush.Core.Cosmetics
{
    /// <summary>
    /// Paint SKUs the game can show on a car (COSM-001). Mirrors the paint items of the backend catalog
    /// (<c>backend/VoxelRush.Api/catalog.json</c>); an EditMode test fails on drift.
    /// </summary>
    public static class PaintCatalog
    {
        public const int MaxSkuLength = 48;
        public const string Prefix = "paint.";

        // Bounds what the server parses from the API, whatever the response says.
        public const int MaxOwnedSkus = 256;

        private static readonly string[] s_skus = { "paint.racing-red", "paint.ocean-blue", "paint.sunset-stripes", "paint.chrome" };

        private static readonly Regex s_skusArray = new Regex("\"skus\"\\s*:\\s*\\[([^\\]]*)\\]", RegexOptions.CultureInvariant);
        private static readonly Regex s_quoted = new Regex("\"([^\"\\\\]*)\"", RegexOptions.CultureInvariant);

        public static IReadOnlyList<string> Skus => s_skus;

        /// <summary>A cosmetic SKU shape: 1..48 characters of a-z, 0-9, '.' and '-' (no spaces, quotes or control characters).</summary>
        public static bool IsWellFormedSku(string? sku)
        {
            if (sku == null || sku.Length == 0 || sku.Length > MaxSkuLength)
            {
                return false;
            }

            foreach (char c in sku)
            {
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '-'))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>A well-formed paint SKU (kind prefix <c>paint.</c>), known or not.</summary>
        public static bool IsWellFormed(string? sku) =>
            IsWellFormedSku(sku) && sku!.StartsWith(Prefix, StringComparison.Ordinal) && sku.Length > Prefix.Length;

        public static bool IsKnown(string? sku) => IsWellFormed(sku) && Array.IndexOf(s_skus, sku) >= 0;

        /// <summary>
        /// The SKUs in the API's <c>{"skus":[...]}</c> answer, dropping anything that isn't a well-formed SKU;
        /// null when the body has no such array.
        /// </summary>
        public static IReadOnlyCollection<string>? ParseOwned(string? json)
        {
            if (json == null)
            {
                return null;
            }

            Match array = s_skusArray.Match(json);
            if (!array.Success)
            {
                return null;
            }

            var owned = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in s_quoted.Matches(array.Groups[1].Value))
            {
                string sku = m.Groups[1].Value;
                if (IsWellFormedSku(sku) && owned.Count < MaxOwnedSkus)
                {
                    owned.Add(sku);
                }
            }

            return owned;
        }
    }
}
