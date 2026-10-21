using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoxelRush.Networking.Vehicles
{
    /// <summary>
    /// Server-confirmed paint of every car this client sees, own and remote (COSM-001), for presentation code that
    /// must not depend on Netcode types. An empty SKU means the default colour.
    /// </summary>
    public static class CarPaints
    {
        private static readonly Dictionary<Transform, string> s_current = new Dictionary<Transform, string>();

        public static IReadOnlyDictionary<Transform, string> Current => s_current;

        /// <summary>Raised with the car and its new paint SKU.</summary>
        public static event Action<Transform, string>? Changed;

        internal static void Set(Transform car, string sku)
        {
            s_current[car] = sku;
            Changed?.Invoke(car, sku);
        }

        internal static void Remove(Transform car) => s_current.Remove(car);
    }
}
