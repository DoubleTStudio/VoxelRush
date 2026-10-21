using System;
using System.Collections.Generic;
using UnityEngine;
using VoxelRush.Core.Cosmetics;

namespace VoxelRush.Networking.Vehicles
{
    /// <summary>
    /// Server-confirmed loadout of every car this client sees, own and remote, plus its effect cues (horn presses,
    /// remote boost), for presentation code that must not depend on Netcode types (COSM-001, COSM-002).
    /// An empty SKU means nothing in that slot (paint: the default colour).
    /// </summary>
    public static class CarCosmetics
    {
        private static readonly Dictionary<Transform, string[]> s_current = new Dictionary<Transform, string[]>();
        private static readonly HashSet<Transform> s_boosting = new HashSet<Transform>();

        /// <summary>Every known car and its SKU per <see cref="CosmeticKind"/> slot (index = kind).</summary>
        public static IReadOnlyDictionary<Transform, string[]> Current => s_current;

        /// <summary>Raised with the car, the slot and its new SKU.</summary>
        public static event Action<Transform, CosmeticKind, string>? Changed;

        /// <summary>Raised when a car's horn sounds (own press, or relayed by the server).</summary>
        public static event Action<Transform>? Honked;

        /// <summary>Raised when a car leaves, so views can release what they attached.</summary>
        public static event Action<Transform>? Removed;

        public static string Get(Transform car, CosmeticKind kind) =>
            CosmeticCatalog.IsValidKind(kind) && s_current.TryGetValue(car, out string[] skus) ? skus[(int)kind] : string.Empty;

        /// <summary>Remote cars boost on the server; the snapshot says so (the own car knows from its prediction).</summary>
        public static bool IsRemoteBoosting(Transform car) => s_boosting.Contains(car);

        internal static void Set(Transform car, CosmeticKind kind, string sku)
        {
            if (!CosmeticCatalog.IsValidKind(kind))
            {
                return;
            }

            if (!s_current.TryGetValue(car, out string[] skus))
            {
                skus = new string[CosmeticCatalog.KindCount];
                for (int i = 0; i < skus.Length; i++)
                {
                    skus[i] = string.Empty;
                }

                s_current[car] = skus;
            }

            skus[(int)kind] = sku;
            Changed?.Invoke(car, kind, sku);
        }

        internal static void Honk(Transform car) => Honked?.Invoke(car);

        internal static void SetRemoteBoosting(Transform car, bool boosting)
        {
            if (boosting)
            {
                s_boosting.Add(car);
            }
            else
            {
                s_boosting.Remove(car);
            }
        }

        internal static void Remove(Transform car)
        {
            s_boosting.Remove(car);
            if (s_current.Remove(car))
            {
                Removed?.Invoke(car);
            }
        }
    }
}
