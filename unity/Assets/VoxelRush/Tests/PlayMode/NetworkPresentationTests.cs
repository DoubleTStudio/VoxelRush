using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using VoxelRush.Presentation.World;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;
using VoxelRush.World.Generation;
using VoxelRush.World.Sync;

namespace VoxelRush.Tests.PlayMode
{
    /// <summary>MVP-002: a joined player sees their car from behind and the synced world, not a static scene.</summary>
    public sealed class NetworkPresentationTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _objects)
            {
                if (go != null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }

            _objects.Clear();
        }

        private T Add<T>(string name) where T : Component
        {
            var go = new GameObject(name);
            _objects.Add(go);
            return go.GetComponent<T>() ?? go.AddComponent<T>();
        }

        [Test]
        public void ChaseCamera_SitsBehindAndAboveTheCarAndLooksAtIt()
        {
            var car = Add<Transform>("Car");
            car.SetPositionAndRotation(new Vector3(-21f, 1f, -40f), Quaternion.Euler(0f, 90f, 0f));
            var camera = Add<ChaseCamera>("Camera");

            camera.SetTarget(car);

            Vector3 offset = camera.transform.position - car.position;
            Assert.That(Vector3.Dot(offset, car.forward), Is.LessThan(-5f), "camera should be behind the car");
            Assert.That(offset.y, Is.GreaterThan(1f), "camera should be above the car");
            Vector3 toCar = (car.position - camera.transform.position).normalized;
            Assert.That(Vector3.Dot(camera.transform.forward, toCar), Is.GreaterThan(0.95f), "camera should look at the car");
        }

        [Test]
        public void ChaseCamera_FollowsAMovingCarAndTurnsWithIt()
        {
            var car = Add<Transform>("Car");
            var camera = Add<ChaseCamera>("Camera");
            camera.SetTarget(car);

            // Drive 20 m/s for 2 s, then turn to face +X.
            for (int i = 0; i < 60; i++)
            {
                car.position += Vector3.forward * (20f / 30f);
                camera.Follow(1f / 30f);
            }

            Assert.That(car.position.z - camera.transform.position.z, Is.InRange(5f, 12f), "camera kept pace with the car");
            car.rotation = Quaternion.Euler(0f, 90f, 0f);
            for (int i = 0; i < 90; i++)
            {
                camera.Follow(1f / 30f);
            }

            Assert.That(Vector3.Dot(camera.transform.position - car.position, Vector3.right), Is.LessThan(-5f), "camera swung behind the turned car");
        }

        [Test]
        public void ChaseCamera_OrbitAndZoomMoveTheView_ThenItRecentersBehindTheCar()
        {
            var car = Add<Transform>("Car");
            var camera = Add<ChaseCamera>("Camera");
            camera.SetTarget(car);
            float distance = Vector3.Distance(camera.transform.position, car.position);

            camera.Orbit(90f, 20f);
            camera.Zoom(2f);
            for (int i = 0; i < 60; i++)
            {
                camera.Follow(1f / 30f);
            }

            Vector3 offset = camera.transform.position - car.position;
            Assert.That(Mathf.Abs(offset.x), Is.GreaterThan(Mathf.Abs(offset.z)), "orbit swung the camera to the car's side");
            Assert.That(offset.magnitude, Is.LessThan(distance * 0.7f), "pinch out moved the camera closer");
            Assert.That(camera.Elevation, Is.GreaterThan(30f));
        }

        [UnityTest]
        public IEnumerator ChaseCamera_RecentersAfterTheGestureEnds()
        {
            var car = Add<Transform>("Car");
            var camera = Add<ChaseCamera>("Camera");
            camera.SetTarget(car);
            camera.Orbit(120f, 0f);
            yield return new WaitForSecondsRealtime(1.6f);
            for (int i = 0; i < 120; i++)
            {
                camera.Follow(1f / 30f);
            }

            Assert.That(Mathf.Abs(camera.YawOffset), Is.LessThan(5f));
        }

        [UnityTest]
        public IEnumerator ChaseCamera_FindsALocalCarOnItsOwn()
        {
            var camera = Add<ChaseCamera>("Camera");
            yield return null;
            Assert.That(camera.Target, Is.Null);

            var car = UnityEngine.Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/VoxelRush/Prefabs/ArcadeCar.prefab"));
            _objects.Add(car);
            // The camera searches a few times a second, not every frame.
            float until = Time.realtimeSinceStartup + 2f;
            while (camera.Target == null && Time.realtimeSinceStartup < until)
            {
                yield return null;
            }

            Assert.That(camera.Target, Is.SameAs(car.transform));
        }

        [Test]
        public void NetworkWorldView_RendersSyncedChunksAndRemeshesOnDelta()
        {
            var info = new WorldInfo(Guid.NewGuid(), GeneratorKind.FlatPlot, 0, FlatPlotGenerator.Version, 1, new ChunkCoord(-1, -1, -1), new ChunkCoord(0, 0, 0));
            var replica = new ClientWorldReplica(info);
            var gen = new FlatPlotGenerator();
            foreach (ChunkCoord c in info.RegionChunks())
            {
                replica.ApplySnapshot(ChunkSnapshotMessage.From(new ChunkData(c, gen.GeneratorVersion, gen.GenerateBlocks(c))));
            }

            var view = Add<NetworkWorldView>("View");
            view.Show(replica);
            for (int i = 0; i < 4; i++)
            {
                view.Tick();
            }

            // The y = -1 layer holds the ground; y = 0 is air.
            Assert.That(view.PendingChunks, Is.EqualTo(0));
            Assert.That(view.ChunkObjects.Keys, Is.EquivalentTo(new[]
            {
                new ChunkCoord(-1, -1, -1), new ChunkCoord(-1, -1, 0), new ChunkCoord(0, -1, -1), new ChunkCoord(0, -1, 0),
            }));

            // A block placed on the ground (world 0,0,0) gives the air chunk above a mesh.
            var air = new ChunkCoord(0, 0, 0);
            ushort index = (ushort)ChunkMath.ToLocal(new BlockPos(0, 0, 0)).Index;
            replica.ApplyDelta(new ChunkMutationBatch(Guid.NewGuid(), "p", air, 0, 1, 1, new[] { new BlockChange(index, BlockIds.Stone) }));
            view.Tick();

            Assert.That(view.ChunkObjects.ContainsKey(air), Is.True);
            Assert.That(view.ChunkObjects[air].GetComponent<MeshFilter>().sharedMesh.vertexCount, Is.GreaterThan(0));
        }

        [Test]
        public void Garage_ItemStates_FollowLevelOwnershipAndStore()
        {
            var levelGated = new VoxelRush.Networking.Online.CatalogEntry { sku = "paint.ocean-blue", unlock = new VoxelRush.Networking.Online.CatalogUnlock { level = 2 } };
            var storeOnly = new VoxelRush.Networking.Online.CatalogEntry { sku = "paint.chrome", unlock = new VoxelRush.Networking.Online.CatalogUnlock { store = true }, productId = "voxelrush.paint.chrome" };

            Assert.That(VoxelRush.Presentation.Store.GarageMenu.StateOf(levelGated, 1, owned: false), Is.EqualTo("locked"));
            Assert.That(VoxelRush.Presentation.Store.GarageMenu.StateOf(levelGated, 2, owned: false), Is.EqualTo("claim"));
            Assert.That(VoxelRush.Presentation.Store.GarageMenu.StateOf(storeOnly, 99, owned: false), Is.EqualTo("buy"), "levels never unlock store items");
            Assert.That(VoxelRush.Presentation.Store.GarageMenu.StateOf(storeOnly, 1, owned: true), Is.EqualTo("owned"));
            Assert.That(VoxelRush.Presentation.Store.PaintPalette.ColorOf("paint.racing-red"), Is.Not.Null);
            Assert.That(VoxelRush.Presentation.Store.PaintPalette.ColorOf("decal.lightning"), Is.Null);
        }

        [Test]
        public void PaintPalette_HasAColourForEveryPaintTheServerCanShow()
        {
            foreach (string sku in VoxelRush.Core.Cosmetics.CosmeticCatalog.SkusOf(VoxelRush.Core.Cosmetics.CosmeticKind.Paint))
            {
                Assert.That(VoxelRush.Presentation.Store.PaintPalette.ColorOf(sku), Is.Not.Null, sku);
            }
        }

        // COSM-001: a remote car's body shows its owner's server-confirmed paint; wheels keep their colour.
        [Test]
        public void CarPaintView_PaintsARemoteCar_FromTheServerConfirmedSku()
        {
            Transform car = PaintableCar("RemoteCar", out Renderer body, out Renderer cabin, out Renderer wheel);
            var view = Add<VoxelRush.Presentation.Store.CarPaintView>("CarPaintView");
            try
            {
                VoxelRush.Networking.Vehicles.CarCosmetics.Set(car, VoxelRush.Core.Cosmetics.CosmeticKind.Paint, "paint.ocean-blue");

                Color blue = Stored(VoxelRush.Presentation.Store.PaintPalette.ColorOf("paint.ocean-blue")!.Value);
                Assert.That(BaseColorOf(body), Is.EqualTo(blue));
                Assert.That(BaseColorOf(cabin), Is.EqualTo(blue));
                Assert.That(wheel.HasPropertyBlock(), Is.False, "wheels keep their colour");

                VoxelRush.Networking.Vehicles.CarCosmetics.Set(car, VoxelRush.Core.Cosmetics.CosmeticKind.Paint, string.Empty);
                Assert.That(body.HasPropertyBlock(), Is.False, "back to the default colour");
            }
            finally
            {
                VoxelRush.Networking.Vehicles.CarCosmetics.Remove(car);
            }
        }

        [Test]
        public void CarPaintView_PaintsCarsThatSpawnedBeforeIt()
        {
            Transform car = PaintableCar("EarlyCar", out Renderer body, out _, out _);
            try
            {
                VoxelRush.Networking.Vehicles.CarCosmetics.Set(car, VoxelRush.Core.Cosmetics.CosmeticKind.Paint, "paint.chrome");
                Add<VoxelRush.Presentation.Store.CarPaintView>("CarPaintView");

                Assert.That(BaseColorOf(body), Is.EqualTo(Stored(VoxelRush.Presentation.Store.PaintPalette.ColorOf("paint.chrome")!.Value)));
            }
            finally
            {
                VoxelRush.Networking.Vehicles.CarCosmetics.Remove(car);
            }
        }

        private Transform PaintableCar(string name, out Renderer body, out Renderer cabin, out Renderer wheel)
        {
            var car = Add<Transform>(name);
            Renderer Part(string part, Transform parent)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = part;
                go.transform.SetParent(parent, false);
                return go.GetComponent<Renderer>();
            }

            body = Part("Body", car);
            cabin = Part("Cabin", car);
            wheel = Part("Tyre", Part("WheelFL", car).transform);
            return car;
        }

        // The colour as a property block stores it (colour-space conversion included).
        private static Color Stored(Color color)
        {
            var block = new MaterialPropertyBlock();
            block.SetColor(VoxelRush.Presentation.Store.PaintPalette.BaseColor, color);
            return block.GetColor(VoxelRush.Presentation.Store.PaintPalette.BaseColor);
        }

        private static Color BaseColorOf(Renderer renderer)
        {
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            return block.GetColor(VoxelRush.Presentation.Store.PaintPalette.BaseColor);
        }

        [UnityTest]
        public IEnumerator ClientScene_HasChaseCameraAndNetworkWorldView()
        {
            yield return SceneManager.LoadSceneAsync("Client", LoadSceneMode.Single);
            Assert.That(Camera.main, Is.Not.Null);
            Assert.That(Camera.main!.GetComponent<ChaseCamera>(), Is.Not.Null);
            Assert.That(UnityEngine.Object.FindAnyObjectByType<NetworkWorldView>(), Is.Not.Null);
            Assert.That(UnityEngine.Object.FindAnyObjectByType<VoxelRush.Presentation.Store.GarageMenu>(), Is.Not.Null);
            Assert.That(UnityEngine.Object.FindAnyObjectByType<VoxelRush.Presentation.UI.GameMenu>(), Is.Not.Null);
            var quality = VoxelRush.Presentation.Quality.QualityController.Instance;
            Assert.That(quality, Is.Not.Null);
            VoxelRush.Presentation.UI.GameMenu.Apply(new VoxelRush.Core.Settings.LocalSettings(VoxelRush.Core.Input.ControlSettings.Default, VoxelRush.Core.Performance.QualityTier.Low, 1f, 0.4f));
            Assert.That(quality!.Tier, Is.EqualTo(VoxelRush.Core.Performance.QualityTier.Low), "a fixed graphics choice applies at once");
            Assert.That(quality.Adaptive, Is.False);
            Assert.That(AudioListener.volume, Is.EqualTo(0.4f).Within(1e-4f));
            VoxelRush.Presentation.UI.GameMenu.Apply(VoxelRush.Core.Settings.LocalSettings.Default);
            Assert.That(quality.Adaptive, Is.True, "Auto hands control back to adaptation");
            Assert.That(UnityEngine.Object.FindAnyObjectByType<VoxelRush.Presentation.Store.CarPaintView>(), Is.Not.Null);
            Assert.That(UnityEngine.Object.FindAnyObjectByType<VoxelRush.Presentation.Store.CarCosmeticsView>(), Is.Not.Null);
            yield return null;
            var horn = UnityEngine.Object.FindAnyObjectByType<VoxelRush.Presentation.UI.HornButton>();
            Assert.That(horn, Is.Not.Null);
            Assert.That(horn!.Button, Is.Not.Null, "HORN cloned from the BUILD button");
            Assert.That(horn.Button!.activeSelf, Is.False, "hidden without a confirmed horn");
            Assert.That(horn.Button.GetComponent<UnityEngine.InputSystem.OnScreen.OnScreenButton>().controlPath, Is.EqualTo(VoxelRush.Presentation.Input.DrivingControls.TouchHornPath));
            var builder = UnityEngine.Object.FindAnyObjectByType<PhoneBuilder>();
            Assert.That(builder, Is.Not.Null);
            builder!.SetMode(PhoneBuilder.Mode.Place);
            Assert.That(builder.TapAt(new Vector2(Screen.width / 2f, Screen.height / 2f)), Is.Not.EqualTo(EditRejection.None), "nothing is sent while offline");
            Assert.That(builder.Sent, Is.EqualTo(0));
            builder.SelectBlock(BlockIds.Glass);
            Assert.That(builder.BlockType, Is.EqualTo(BlockIds.Glass));
            builder.SelectBlock(BlockIds.Protected);
            Assert.That(builder.BlockType, Is.EqualTo(BlockIds.Glass), "protected blocks aren't on the palette");
            yield return TestScenes.UnloadAll();
        }
    }
}
