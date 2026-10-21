using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using VoxelRush.Core;
using VoxelRush.Core.Cosmetics;
using VoxelRush.Core.Performance;
using VoxelRush.Gameplay.Vehicles;
using VoxelRush.Networking.Vehicles;
using VoxelRush.Presentation.Quality;

namespace VoxelRush.Presentation.Store
{
    /// <summary>
    /// Shows the server-confirmed non-paint cosmetics on every car this client sees (COSM-002): a lightning decal on
    /// the hood, a gold checkered flag at the rear, spark trails while fast or boosting (pooled, off at the low
    /// quality tier) and the retro horn, spatialised at the car. Client-only presentation; nothing is networked here.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CarCosmeticsView : MonoBehaviour
    {
        /// <summary>Sparks fly above this speed (m/s) even without boost; road speed tops out at 28.</summary>
        public const float TrailSpeed = 18f;

        /// <summary>At most one trail per player on a server.</summary>
        public const int MaxTrails = SimulationConstants.MaxPlayersPerServer;

        public const string DecalName = "CosmeticDecal";
        public const string FlagName = "CosmeticFlag";
        public const string TrailName = "CosmeticTrail";
        public const string HornName = "CosmeticHorn";

        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int BaseMap = Shader.PropertyToID("_BaseMap");

        private readonly Dictionary<Transform, Attached> _cars = new Dictionary<Transform, Attached>();
        private readonly Stack<ParticleSystem> _pool = new Stack<ParticleSystem>(MaxTrails);
        private readonly List<Transform> _scratch = new List<Transform>(MaxTrails);
        private MaterialPropertyBlock? _block;
        private int _trailBudget = -1;

        /// <summary>Spark particles per trail at the current quality tier (0 = trails off).</summary>
        public int TrailBudget => QualityPresets.For(QualityController.Instance != null ? QualityController.Instance.Tier : QualityTier.High).TrailParticles;

        public int ActiveTrails { get; private set; }

        public long HornPlays { get; private set; }

        public ParticleSystem? TrailOf(Transform car) => _cars.TryGetValue(car, out Attached a) ? a.Trail : null;

        public bool IsEmitting(Transform car) => _cars.TryGetValue(car, out Attached a) && a.Trail != null && a.Trail.emission.enabled;

        public AudioSource? HornOf(Transform car) => _cars.TryGetValue(car, out Attached a) ? a.Horn : null;

        private void OnEnable()
        {
            CarCosmetics.Changed += OnChanged;
            CarCosmetics.Honked += OnHonked;
            CarCosmetics.Removed += Release;
            foreach (KeyValuePair<Transform, string[]> car in CarCosmetics.Current)
            {
                for (int i = 0; i < CosmeticCatalog.KindCount; i++)
                {
                    OnChanged(car.Key, (CosmeticKind)i, car.Value[i]);
                }
            }
        }

        private void OnDisable()
        {
            CarCosmetics.Changed -= OnChanged;
            CarCosmetics.Honked -= OnHonked;
            CarCosmetics.Removed -= Release;
            _scratch.Clear();
            _scratch.AddRange(_cars.Keys);
            foreach (Transform car in _scratch)
            {
                Release(car);
            }
        }

        private void Update()
        {
            int budget = TrailBudget;
            bool budgetChanged = budget != _trailBudget;
            _trailBudget = budget;
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            float sway = Mathf.Sin(Time.time * 6f) * 12f;

            _scratch.Clear();
            _scratch.AddRange(_cars.Keys);
            foreach (Transform car in _scratch)
            {
                Attached a = _cars[car];
                if (car == null)
                {
                    // Destroyed without a despawn (scene unload): its children went with it.
                    ReleaseTrail(a);
                    _cars.Remove(car!);
                    continue;
                }

                Vector3 position = car.position;
                a.Speed = Mathf.Lerp(a.Speed, (position - a.LastPosition).magnitude / dt, 0.2f);
                a.LastPosition = position;
                if (a.Cloth != null)
                {
                    a.Cloth.localRotation = Quaternion.Euler(0f, sway, 0f);
                }

                if (budgetChanged)
                {
                    RefreshTrail(car, a);
                }

                if (a.Trail != null)
                {
                    ArcadeVehicle? own = LocalPlayer.Vehicle;
                    bool boosting = own != null && own.transform == car ? own.IsBoosting : CarCosmetics.IsRemoteBoosting(car);
                    bool emit = boosting || a.Speed >= TrailSpeed;
                    ParticleSystem.EmissionModule emission = a.Trail.emission;
                    if (emission.enabled != emit)
                    {
                        emission.enabled = emit;
                    }
                }
            }
        }

        private void OnChanged(Transform car, CosmeticKind kind, string sku)
        {
            if (car == null || kind == CosmeticKind.Paint)
            {
                return;
            }

            Attached a = AttachedOf(car);
            bool on = sku.Length > 0;
            switch (kind)
            {
                case CosmeticKind.Decal:
                    SetDecal(car, a, on);
                    break;
                case CosmeticKind.Flag:
                    SetFlag(car, a, on);
                    break;
                case CosmeticKind.Trail:
                    a.WantsTrail = on;
                    RefreshTrail(car, a);
                    break;
                case CosmeticKind.Horn:
                    SetHorn(car, a, on);
                    break;
            }
        }

        private void OnHonked(Transform car)
        {
            if (car != null && _cars.TryGetValue(car, out Attached a) && a.Horn != null)
            {
                a.Horn.Play();
                HornPlays++;
            }
        }

        private Attached AttachedOf(Transform car)
        {
            if (!_cars.TryGetValue(car, out Attached a))
            {
                a = new Attached { Body = BodyBounds(car), Material = MaterialOf(car), LastPosition = car.position };
                _cars[car] = a;
            }

            return a;
        }

        private void SetDecal(Transform car, Attached a, bool on)
        {
            if (!on || a.Material == null)
            {
                DestroyChild(ref a.Decal);
                return;
            }

            if (a.Decal != null)
            {
                return;
            }

            Bounds body = a.Body;
            // Just above the hood (front of the body), clear of z-fighting.
            a.Decal = Part(DecalName, car, CosmeticAssets.Lightning, a.Material,
                new Vector3(body.center.x, body.max.y + 0.005f, body.center.z + (body.extents.z * 0.6f)),
                new Vector3(body.size.x * 0.55f, 1f, body.size.z * 0.3f));
            Tint(a.Decal.GetComponent<Renderer>(), CosmeticAssets.DecalColor, null);
        }

        private void SetFlag(Transform car, Attached a, bool on)
        {
            if (!on || a.Material == null)
            {
                DestroyChild(ref a.Flag);
                a.Cloth = null;
                return;
            }

            if (a.Flag != null)
            {
                return;
            }

            Bounds body = a.Body;
            a.Flag = new GameObject(FlagName);
            a.Flag.transform.SetParent(car, false);
            a.Flag.transform.localPosition = new Vector3(body.center.x + (body.extents.x * 0.6f), body.max.y, body.min.z + 0.15f);
            GameObject pole = Part("Pole", a.Flag.transform, CosmeticAssets.Box, a.Material, new Vector3(0f, 0.45f, 0f), new Vector3(0.04f, 0.9f, 0.04f));
            Tint(pole.GetComponent<Renderer>(), new Color(0.25f, 0.25f, 0.28f), null);
            GameObject cloth = Part("Cloth", a.Flag.transform, CosmeticAssets.FlagCloth, a.Material, new Vector3(0f, 0.55f, 0f), new Vector3(1f, 0.35f, 0.55f));
            Tint(cloth.GetComponent<Renderer>(), Color.white, CosmeticAssets.Checkered);
            a.Cloth = cloth.transform;
        }

        private void SetHorn(Transform car, Attached a, bool on)
        {
            if (!on)
            {
                if (a.Horn != null)
                {
                    Destroy(a.Horn.gameObject);
                    a.Horn = null;
                }

                return;
            }

            if (a.Horn != null)
            {
                return;
            }

            var go = new GameObject(HornName);
            go.transform.SetParent(car, false);
            go.transform.localPosition = new Vector3(0f, 0.3f, a.Body.max.z);
            AudioSource source = go.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.clip = CosmeticAssets.Horn;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 4f;
            source.maxDistance = 80f;
            source.dopplerLevel = 0f;
            a.Horn = source;
        }

        private void RefreshTrail(Transform car, Attached a)
        {
            int budget = TrailBudget;
            bool show = a.WantsTrail && budget > 0 && a.Material != null;
            if (!show)
            {
                ReleaseTrail(a);
                return;
            }

            if (a.Trail == null)
            {
                if (ActiveTrails >= MaxTrails)
                {
                    return;
                }

                a.Trail = _pool.Count > 0 ? _pool.Pop() : CreateTrail();
                a.HadTrail = true;
                ActiveTrails++;
                Transform t = a.Trail.transform;
                t.SetParent(car, false);
                Bounds body = a.Body;
                t.localPosition = new Vector3(body.center.x, body.min.y + 0.15f, body.min.z);
                t.localRotation = Quaternion.LookRotation(Vector3.back);
                a.Trail.gameObject.SetActive(true);
                ParticleSystemRenderer renderer = a.Trail.GetComponent<ParticleSystemRenderer>();
                renderer.sharedMaterial = a.Material;
                Tint(renderer, CosmeticAssets.SparkColor, null);
                ParticleSystem.EmissionModule emission = a.Trail.emission;
                emission.enabled = false;
                a.Trail.Play();
            }

            ParticleSystem.MainModule main = a.Trail.main;
            main.maxParticles = budget;
        }

        private void ReleaseTrail(Attached a)
        {
            if (!a.HadTrail)
            {
                return;
            }

            a.HadTrail = false;
            ActiveTrails--;
            // A trail destroyed with its car can't be reused.
            if (a.Trail != null)
            {
                a.Trail.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                a.Trail.gameObject.SetActive(false);
                a.Trail.transform.SetParent(transform, false);
                _pool.Push(a.Trail);
            }

            a.Trail = null;
        }

        private void Release(Transform car)
        {
            if (!_cars.TryGetValue(car, out Attached a))
            {
                return;
            }

            // Back to the pool before the car (and its children) is destroyed.
            ReleaseTrail(a);
            if (car != null)
            {
                DestroyChild(ref a.Decal);
                DestroyChild(ref a.Flag);
                if (a.Horn != null)
                {
                    Destroy(a.Horn.gameObject);
                }
            }

            _cars.Remove(car);
        }

        private ParticleSystem CreateTrail()
        {
            var go = new GameObject(TrailName);
            go.SetActive(false);
            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = ps.main;
            main.playOnAwake = false;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.5f, 4f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.12f);
            main.gravityModifier = 0.8f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTime = 60f;
            emission.enabled = false;
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 25f;
            shape.radius = 0.25f;
            ParticleSystemRenderer renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Mesh;
            renderer.mesh = CosmeticAssets.Box;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return ps;
        }

        private static GameObject Part(string name, Transform parent, Mesh mesh, Material material, Vector3 localPosition, Vector3 localScale)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = localScale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            return go;
        }

        private void Tint(Renderer renderer, Color color, Texture? texture)
        {
            _block ??= new MaterialPropertyBlock();
            _block.Clear();
            _block.SetColor(BaseColor, color);
            if (texture != null)
            {
                _block.SetTexture(BaseMap, texture);
            }

            renderer.SetPropertyBlock(_block);
        }

        private static void DestroyChild(ref GameObject? child)
        {
            if (child != null)
            {
                Destroy(child);
            }

            child = null;
        }

        // The cars' own lit material, so the cosmetics need no extra shader in the build.
        private static Material? MaterialOf(Transform car)
        {
            Material? fallback = null;
            foreach (Renderer renderer in car.GetComponentsInChildren<Renderer>())
            {
                if (renderer.name.StartsWith("Body", StringComparison.Ordinal) && renderer.sharedMaterial != null)
                {
                    return renderer.sharedMaterial;
                }

                fallback ??= renderer.sharedMaterial;
            }

            return fallback;
        }

        // The body box in car space, so the cosmetics sit on any car shape.
        private static Bounds BodyBounds(Transform car)
        {
            foreach (MeshFilter filter in car.GetComponentsInChildren<MeshFilter>())
            {
                if (!filter.name.StartsWith("Body", StringComparison.Ordinal) || filter.sharedMesh == null)
                {
                    continue;
                }

                Bounds local = filter.sharedMesh.bounds;
                var bounds = new Bounds(car.InverseTransformPoint(filter.transform.TransformPoint(local.center)), Vector3.zero);
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3(
                        (i & 1) == 0 ? local.min.x : local.max.x,
                        (i & 2) == 0 ? local.min.y : local.max.y,
                        (i & 4) == 0 ? local.min.z : local.max.z);
                    bounds.Encapsulate(car.InverseTransformPoint(filter.transform.TransformPoint(corner)));
                }

                return bounds;
            }

            return new Bounds(new Vector3(0f, 0.05f, 0f), new Vector3(1.8f, 0.6f, 4f));
        }

        private sealed class Attached
        {
            public Bounds Body;
            public Material? Material;
            public Vector3 LastPosition;
            public float Speed;
            public bool WantsTrail;
            public bool HadTrail;
            public GameObject? Decal;
            public GameObject? Flag;
            public Transform? Cloth;
            public ParticleSystem? Trail;
            public AudioSource? Horn;
        }
    }
}
