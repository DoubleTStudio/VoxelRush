using System.Collections.Generic;
using UnityEngine;
using VoxelRush.Core.Cosmetics;
using VoxelRush.Networking.Vehicles;

namespace VoxelRush.Presentation.Store
{
    /// <summary>Paints every car this client sees, own and remote, with the paint the server confirmed (COSM-001).</summary>
    [DisallowMultipleComponent]
    public sealed class CarPaintView : MonoBehaviour
    {
        private MaterialPropertyBlock? _block;

        private void OnEnable()
        {
            CarCosmetics.Changed += Apply;
            foreach (KeyValuePair<Transform, string[]> car in CarCosmetics.Current)
            {
                Apply(car.Key, CosmeticKind.Paint, car.Value[(int)CosmeticKind.Paint]);
            }
        }

        private void OnDisable() => CarCosmetics.Changed -= Apply;

        private void Apply(Transform car, CosmeticKind kind, string sku)
        {
            if (car == null || kind != CosmeticKind.Paint)
            {
                return;
            }

            _block ??= new MaterialPropertyBlock();
            PaintPalette.Apply(car, sku, _block);
        }
    }
}
