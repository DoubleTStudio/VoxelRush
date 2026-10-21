using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VoxelRush.Core.Cosmetics;
using VoxelRush.Core.Performance;
using VoxelRush.Networking.Vehicles;
using VoxelRush.Presentation.Quality;
using VoxelRush.Presentation.Store;

namespace VoxelRush.Tests.PlayMode
{
    /// <summary>COSM-002: non-paint cosmetics confirmed by the server appear on remote cars; trails respect the quality tier.</summary>
    public sealed class CarCosmeticsViewTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<Transform> _cars = new List<Transform>();
        private System.Action? _restore;

        [TearDown]
        public void TearDown()
        {
            foreach (Transform car in _cars)
            {
                CarCosmetics.Remove(car);
            }

            foreach (GameObject go in _objects)
            {
                if (go != null)
                {
                    Object.DestroyImmediate(go);
                }
            }

            _cars.Clear();
            _objects.Clear();
            _restore?.Invoke();
            _restore = null;
        }

        private T Add<T>(string name) where T : Component
        {
            var go = new GameObject(name);
            _objects.Add(go);
            return go.AddComponent<T>();
        }

        // A remote car shaped like NetworkedCar: body box, cabin and a wheel.
        private Transform RemoteCar(string name)
        {
            var car = new GameObject(name).transform;
            _objects.Add(car.gameObject);
            _cars.Add(car);
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "Body";
            body.transform.SetParent(car, false);
            body.transform.localPosition = new Vector3(0f, 0.05f, 0f);
            body.transform.localScale = new Vector3(1.8f, 0.6f, 4f);
            return car;
        }

        private static Transform? Child(Transform car, string name)
        {
            foreach (Transform t in car.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name)
                {
                    return t;
                }
            }

            return null;
        }

        [UnityTest]
        public IEnumerator DecalAndFlag_AppearOnARemoteCar_AndGoWhenUnequipped()
        {
            Transform car = RemoteCar("RemoteCar");
            var view = Add<CarCosmeticsView>("CosmeticsView");

            CarCosmetics.Set(car, CosmeticKind.Decal, "decal.lightning");
            CarCosmetics.Set(car, CosmeticKind.Flag, "flag.checkered-gold");
            yield return null;

            Transform? decal = Child(car, CarCosmeticsView.DecalName);
            Transform? flag = Child(car, CarCosmeticsView.FlagName);
            Assert.That(decal, Is.Not.Null, "lightning decal on the hood");
            Assert.That(flag, Is.Not.Null, "flag at the rear");
            Assert.That(decal!.localPosition.y, Is.GreaterThan(0.35f), "on top of the body");
            Assert.That(decal.localPosition.z, Is.GreaterThan(0.5f), "towards the front");
            Assert.That(flag!.localPosition.z, Is.LessThan(-1.5f), "towards the rear");
            Assert.That(decal.GetComponent<MeshFilter>().sharedMesh.vertexCount, Is.GreaterThan(0));
            var cloth = Child(flag, "Cloth")!.GetComponent<Renderer>();
            var block = new MaterialPropertyBlock();
            cloth.GetPropertyBlock(block);
            Assert.That(block.GetTexture("_BaseMap"), Is.SameAs(CosmeticAssets.Checkered), "gold checkered texture");
            Assert.That(car.Find("Body").GetComponent<Renderer>().HasPropertyBlock(), Is.False, "cosmetics never tint the body");

            CarCosmetics.Set(car, CosmeticKind.Decal, string.Empty);
            CarCosmetics.Set(car, CosmeticKind.Flag, string.Empty);
            yield return null;

            Assert.That(Child(car, CarCosmeticsView.DecalName), Is.Null);
            Assert.That(Child(car, CarCosmeticsView.FlagName), Is.Null);
            Assert.That(view, Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator CosmeticsConfirmedBeforeTheViewStarted_AreShown()
        {
            Transform car = RemoteCar("EarlyCar");
            CarCosmetics.Set(car, CosmeticKind.Decal, "decal.lightning");

            Add<CarCosmeticsView>("CosmeticsView");
            yield return null;

            Assert.That(Child(car, CarCosmeticsView.DecalName), Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator Trail_SparksWhileBoosting_NotWhenParked()
        {
            Transform car = RemoteCar("RemoteCar");
            var view = Add<CarCosmeticsView>("CosmeticsView");
            CarCosmetics.Set(car, CosmeticKind.Trail, "trail.sparks");
            yield return null;

            ParticleSystem? trail = view.TrailOf(car);
            Assert.That(trail, Is.Not.Null);
            Assert.That(trail!.main.maxParticles, Is.EqualTo(view.TrailBudget).And.GreaterThan(0));
            Assert.That(trail.main.maxParticles, Is.LessThanOrEqualTo(QualityPresets.For(QualityTier.High).TrailParticles), "bounded per car");
            Assert.That(view.IsEmitting(car), Is.False, "a parked car doesn't spark");

            CarCosmetics.SetRemoteBoosting(car, true);
            yield return null;
            Assert.That(view.IsEmitting(car), Is.True, "a boosting remote car sparks");

            CarCosmetics.SetRemoteBoosting(car, false);
            yield return null;
            Assert.That(view.IsEmitting(car), Is.False);
        }

        [UnityTest]
        public IEnumerator Trail_IsOff_AtTheLowQualityTier_AndBackAboveIt()
        {
            // The controller sets the frame rate; later tests expect the runner's.
            int frameRate = Application.targetFrameRate;
            _restore = () => Application.targetFrameRate = frameRate;
            var quality = Add<QualityController>("Quality");
            quality.Adaptive = false;
            quality.ForceTier(QualityTier.Low);
            Transform car = RemoteCar("RemoteCar");
            var view = Add<CarCosmeticsView>("CosmeticsView");
            CarCosmetics.Set(car, CosmeticKind.Trail, "trail.sparks");
            CarCosmetics.SetRemoteBoosting(car, true);
            yield return null;

            Assert.That(view.TrailBudget, Is.Zero);
            Assert.That(view.TrailOf(car), Is.Null, "no particle system at the low tier");
            Assert.That(view.ActiveTrails, Is.Zero);
            Assert.That(car.GetComponentsInChildren<ParticleSystem>(true), Is.Empty);

            quality.ForceTier(QualityTier.Medium);
            yield return null;

            Assert.That(view.TrailOf(car), Is.Not.Null, "trails come back when the tier allows");
            Assert.That(view.TrailOf(car)!.main.maxParticles, Is.EqualTo(QualityPresets.For(QualityTier.Medium).TrailParticles));

            quality.ForceTier(QualityTier.Low);
            yield return null;
            Assert.That(view.TrailOf(car), Is.Null, "a hot device drops to low and the trail goes");
        }

        [UnityTest]
        public IEnumerator Trails_ArePooled_AndCapped()
        {
            var view = Add<CarCosmeticsView>("CosmeticsView");
            Transform first = RemoteCar("Car0");
            CarCosmetics.Set(first, CosmeticKind.Trail, "trail.sparks");
            ParticleSystem? pooled = view.TrailOf(first);
            CarCosmetics.Remove(first);
            Assert.That(view.ActiveTrails, Is.Zero);

            var cars = new List<Transform>();
            for (int i = 0; i < CarCosmeticsView.MaxTrails + 2; i++)
            {
                Transform car = RemoteCar("Car" + (i + 1));
                cars.Add(car);
                CarCosmetics.Set(car, CosmeticKind.Trail, "trail.sparks");
            }

            yield return null;
            Assert.That(view.TrailOf(cars[0]), Is.SameAs(pooled), "a released trail is reused");
            Assert.That(view.ActiveTrails, Is.EqualTo(CarCosmeticsView.MaxTrails));
        }

        [UnityTest]
        public IEnumerator Horn_PlaysSpatialised_AtTheCar_WhenTheServerRelaysAPress()
        {
            Transform car = RemoteCar("RemoteCar");
            var view = Add<CarCosmeticsView>("CosmeticsView");
            CarCosmetics.Honk(car);
            Assert.That(view.HornPlays, Is.Zero, "no horn equipped, nothing plays");

            CarCosmetics.Set(car, CosmeticKind.Horn, "horn.retro");
            yield return null;
            AudioSource? horn = view.HornOf(car);
            Assert.That(horn, Is.Not.Null);
            Assert.That(horn!.transform.IsChildOf(car), Is.True);
            Assert.That(horn.spatialBlend, Is.EqualTo(1f), "3D at the car");
            Assert.That(horn.clip, Is.SameAs(CosmeticAssets.Horn));

            CarCosmetics.Honk(car);
            Assert.That(view.HornPlays, Is.EqualTo(1));
        }

        [TestCase(CosmeticKind.Decal, "decal.lightning")]
        [TestCase(CosmeticKind.Horn, "horn.retro")]
        [TestCase(CosmeticKind.Flag, "flag.checkered-gold")]
        public void Garage_EquipsAndUnequips_PerKind(CosmeticKind kind, string sku)
        {
            string key = GarageMenu.KeyOf(kind);
            string saved = PlayerPrefs.GetString(key, string.Empty);
            string paint = PlayerPrefs.GetString(GarageMenu.KeyOf(CosmeticKind.Paint), string.Empty);
            try
            {
                PlayerPrefs.SetString(GarageMenu.KeyOf(CosmeticKind.Paint), "paint.ocean-blue");
                PlayerPrefs.SetString(key, string.Empty);

                GarageMenu.Toggle(kind, sku);
                Assert.That(LocalPlayer.Equipped(kind), Is.EqualTo(sku));
                Assert.That(GarageMenu.EquippedOf(CosmeticKind.Paint), Is.EqualTo("paint.ocean-blue"), "other slots are untouched");

                GarageMenu.Toggle(kind, sku);
                Assert.That(LocalPlayer.Equipped(kind), Is.Null);
                Assert.That(GarageMenu.EquippedOf(kind), Is.Null);
            }
            finally
            {
                PlayerPrefs.SetString(key, saved);
                PlayerPrefs.SetString(GarageMenu.KeyOf(CosmeticKind.Paint), paint);
                PlayerPrefs.Save();
                LocalPlayer.Equip(kind, null);
            }
        }

        [Test]
        public void HornClip_IsAShortProceduralSound()
        {
            AudioClip clip = CosmeticAssets.Horn;
            var samples = new float[clip.samples];
            clip.GetData(samples, 0);
            float peak = 0f;
            foreach (float s in samples)
            {
                peak = Mathf.Max(peak, Mathf.Abs(s));
            }

            Assert.That(clip.length, Is.InRange(0.2f, 1f));
            Assert.That(peak, Is.InRange(0.1f, 1f), "audible, never clipping");
        }

        [Test]
        public void CheckeredTexture_IsGoldAndDark()
        {
            Texture2D texture = CosmeticAssets.Checkered;

            Assert.That(texture.GetPixel(0, 0), Is.EqualTo(CosmeticAssets.FlagGold).Using(new ColorComparer()));
            Assert.That(texture.GetPixel(4, 0), Is.EqualTo(CosmeticAssets.FlagDark).Using(new ColorComparer()));
            Assert.That(texture.filterMode, Is.EqualTo(FilterMode.Point));
        }

        private sealed class ColorComparer : IEqualityComparer<Color>
        {
            public bool Equals(Color a, Color b) => Mathf.Abs(a.r - b.r) < 0.01f && Mathf.Abs(a.g - b.g) < 0.01f && Mathf.Abs(a.b - b.b) < 0.01f;

            public int GetHashCode(Color c) => 0;
        }
    }
}
