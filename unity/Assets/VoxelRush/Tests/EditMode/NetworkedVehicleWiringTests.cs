using System.Linq;
using NUnit.Framework;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VoxelRush.Editor;
using VoxelRush.Gameplay.Vehicles;
using VoxelRush.Networking.Vehicles;

namespace VoxelRush.Tests.EditMode
{
    public sealed class NetworkedVehicleWiringTests
    {
        [Test]
        public void NetworkedCarPrefab_IsTickDrivenAndNetworked()
        {
            GameObject prefab = VehicleSpawner.LoadPrefab();

            Assert.That(prefab.GetComponent<NetworkObject>(), Is.Not.Null);
            Assert.That(prefab.GetComponent<VehicleNetworkDriver>(), Is.Not.Null);
            var vehicle = prefab.GetComponent<ArcadeVehicle>();
            Assert.That(vehicle, Is.Not.Null);
            Assert.That(vehicle.AutoSimulate, Is.False, "networked cars must only be stepped by the tick loop");
            Assert.That(vehicle.Tuning.Validate(), Is.Empty);
        }

        [TestCase(BuildEntryPoints.ServerScene)]
        [TestCase(BuildEntryPoints.ClientScene)]
        [TestCase(BuildEntryPoints.BotScene)]
        public void SimulatingScenes_ShareTheArenaWithEightSpawnPoints(string scenePath)
        {
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            try
            {
                Transform? spawns = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                    .FirstOrDefault(t => t.name == VehicleSpawner.SpawnPointsName);

                Assert.That(spawns, Is.Not.Null, "Arena with SpawnPoints missing");
                Assert.That(spawns!.childCount, Is.EqualTo(8));
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }
    }
}
