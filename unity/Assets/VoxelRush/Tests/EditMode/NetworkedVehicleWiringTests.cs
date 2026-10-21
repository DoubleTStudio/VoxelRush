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
            Assert.That(prefab.GetComponent<VehicleLoadout>(), Is.Not.Null, "the cosmetic loadout is replicated on the car (COSM-001, COSM-002)");
            Assert.That(prefab.GetComponentsInChildren<ParticleSystem>(true), Is.Empty, "trails are client-only presentation, never on the networked prefab");
            Assert.That(prefab.GetComponentsInChildren<AudioSource>(true), Is.Empty, "the horn is client-only presentation");
            var vehicle = prefab.GetComponent<ArcadeVehicle>();
            Assert.That(vehicle, Is.Not.Null);
            Assert.That(vehicle.AutoSimulate, Is.False, "networked cars must only be stepped by the tick loop");
            Assert.That(vehicle.Tuning.Validate(), Is.Empty);
        }

        [TestCase("paint")]
        [TestCase("decal")]
        [TestCase("trail")]
        [TestCase("horn")]
        [TestCase("flag")]
        public void CosmeticCatalog_MatchesTheBackendCatalog_ForEveryKind(string kindName)
        {
            string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "../../backend/VoxelRush.Api/catalog.json"));
            Assume.That(System.IO.File.Exists(path), "backend not checked out next to the Unity project");
            string pattern = @"""sku""\s*:\s*""([^""]+)""\s*,\s*""kind""\s*:\s*""" + kindName + @"""";
            string[] backend = System.Text.RegularExpressions.Regex.Matches(System.IO.File.ReadAllText(path), pattern)
                .Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value).ToArray();
            var kind = (VoxelRush.Core.Cosmetics.CosmeticKind)Enumerable.Range(0, VoxelRush.Core.Cosmetics.CosmeticCatalog.KindCount)
                .Single(i => VoxelRush.Core.Cosmetics.CosmeticCatalog.NameOf((VoxelRush.Core.Cosmetics.CosmeticKind)i) == kindName);

            Assert.That(backend, Is.Not.Empty);
            Assert.That(VoxelRush.Core.Cosmetics.CosmeticCatalog.SkusOf(kind), Is.EquivalentTo(backend), "an item the API sells must be one the game can show");
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
