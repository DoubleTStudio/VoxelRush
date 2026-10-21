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
            Assert.That(VoxelRush.Presentation.Store.GarageMenu.PaintColor("paint.racing-red"), Is.Not.Null);
            Assert.That(VoxelRush.Presentation.Store.GarageMenu.PaintColor("decal.lightning"), Is.Null);
        }

        [UnityTest]
        public IEnumerator ClientScene_HasChaseCameraAndNetworkWorldView()
        {
            yield return SceneManager.LoadSceneAsync("Client", LoadSceneMode.Single);
            Assert.That(Camera.main, Is.Not.Null);
            Assert.That(Camera.main!.GetComponent<ChaseCamera>(), Is.Not.Null);
            Assert.That(UnityEngine.Object.FindAnyObjectByType<NetworkWorldView>(), Is.Not.Null);
            Assert.That(UnityEngine.Object.FindAnyObjectByType<VoxelRush.Presentation.Store.GarageMenu>(), Is.Not.Null);
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
