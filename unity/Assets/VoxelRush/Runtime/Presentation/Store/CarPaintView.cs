using System.Collections.Generic;
using UnityEngine;
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
            CarPaints.Changed += Apply;
            foreach (KeyValuePair<Transform, string> car in CarPaints.Current)
            {
                Apply(car.Key, car.Value);
            }
        }

        private void OnDisable() => CarPaints.Changed -= Apply;

        private void Apply(Transform car, string sku)
        {
            if (car == null)
            {
                return;
            }

            _block ??= new MaterialPropertyBlock();
            PaintPalette.Apply(car, sku, _block);
        }
    }
}
