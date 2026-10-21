using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace VoxelRush.Core.Cosmetics
{
    /// <summary>The loadout slots a car has; one item of each kind can be shown at a time (COSM-002).</summary>
    public enum CosmeticKind : byte
    {
        Paint = 0,
        Decal = 1,
        Trail = 2,
        Horn = 3,
        Flag = 4,
    }

    /// <summary>
    /// Cosmetic SKUs the game can show on a car, per kind (COSM-001, COSM-002). Mirrors the backend catalog
    /// (<c>backend/VoxelRush.Api/catalog.json</c>); an EditMode test fails on drift.
    /// </summary>
    public static class CosmeticCatalog
    {
        public const int MaxSkuLength = 48;

        /// <summary>Number of <see cref="CosmeticKind"/> slots.</summary>
        public const int KindCount = 5;

        // Bounds what the server parses from the API, whatever the response says.
        public const int MaxOwnedSkus = 256;

        private static readonly string[] s_prefixes = { "paint.", "decal.", "trail.", "horn.", "flag." };
        private static readonly string[] s_names = { "paint", "decal", "trail", "horn", "flag" };

        private static readonly string[][] s_skus =
        {
            new[] { "paint.racing-red", "paint.ocean-blue", "paint.sunset-stripes", "paint.chrome" },
            new[] { "decal.lightning" },
            new[] { "trail.sparks" },
            new[] { "horn.retro" },
            new[] { "flag.checkered-gold" },
        };

        private static readonly Regex s_skusArray = new Regex("\"skus\"\\s*:\\s*\\[([^\\]]*)\\]", RegexOptions.CultureInvariant);
        private static readonly Regex s_quoted = new Regex("\"([^\"\\\\]*)\"", RegexOptions.CultureInvariant);

        public static bool IsValidKind(CosmeticKind kind) => (uint)kind < KindCount;

        public static IReadOnlyList<string> SkusOf(CosmeticKind kind) => IsValidKind(kind) ? s_skus[(int)kind] : Array.Empty<string>();

        /// <summary>Lower-case kind name, as the backend catalog and logs spell it.</summary>
        public static string NameOf(CosmeticKind kind) => IsValidKind(kind) ? s_names[(int)kind] : "invalid";

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

        /// <summary>A well-formed SKU of this kind (prefix such as <c>paint.</c>), known or not.</summary>
        public static bool IsWellFormed(CosmeticKind kind, string? sku)
        {
            if (!IsValidKind(kind) || !IsWellFormedSku(sku))
            {
                return false;
            }

            string prefix = s_prefixes[(int)kind];
            return sku!.StartsWith(prefix, StringComparison.Ordinal) && sku.Length > prefix.Length;
        }

        public static bool IsKnown(CosmeticKind kind, string? sku) => IsWellFormed(kind, sku) && Array.IndexOf(s_skus[(int)kind], sku) >= 0;

        /// <summary>The kind of a known SKU (the garage and bots equip by SKU alone).</summary>
        public static bool TryKindOf(string? sku, out CosmeticKind kind)
        {
            for (int i = 0; i < KindCount; i++)
            {
                if (IsKnown((CosmeticKind)i, sku))
                {
                    kind = (CosmeticKind)i;
                    return true;
                }
            }

            kind = default;
            return false;
        }

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
