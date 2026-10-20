using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace VoxelRush.Tests.EditMode
{
    /// <summary>Guards the assembly boundaries from docs/architecture/SYSTEM.md.</summary>
    public sealed class AssemblyArchitectureTests
    {
        private static readonly string[] EngineFreeAssemblies =
        {
            "VoxelRush.Core", "VoxelRush.World", "VoxelRush.Destruction", "VoxelRush.Tests.Domain",
        };

        private static readonly string[] RuntimeAssemblies =
        {
            "VoxelRush.Core", "VoxelRush.World", "VoxelRush.Gameplay", "VoxelRush.Destruction",
            "VoxelRush.Networking", "VoxelRush.Presentation", "VoxelRush.Server",
        };

        private Dictionary<string, AssemblyDefinitionData> _assemblies = null!;

        [OneTimeSetUp]
        public void LoadAssemblyDefinitions()
        {
            _assemblies = AssetDatabase.FindAssets("t:AssemblyDefinitionAsset", new[] { "Assets/VoxelRush" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<AssemblyDefinitionAsset>)
                .Select(asset => JsonUtility.FromJson<AssemblyDefinitionData>(asset.text))
                .ToDictionary(data => data.name);
        }

        [Test]
        public void AllExpectedAssembliesExist()
        {
            Assert.That(_assemblies.Keys, Is.SupersetOf(RuntimeAssemblies.Union(EngineFreeAssemblies)));
        }

        [TestCaseSource(nameof(EngineFreeAssemblies))]
        public void DomainAssembly_HasNoEngineReferences(string name)
        {
            Assert.That(_assemblies[name].noEngineReferences, Is.True, $"{name} must stay engine-free so it runs on plain .NET and the server.");
        }

        [Test]
        public void Core_ReferencesNothing()
        {
            Assert.That(_assemblies["VoxelRush.Core"].references, Is.Empty);
        }

        [TestCaseSource(nameof(RuntimeAssemblies))]
        public void RuntimeAssembly_DoesNotReferenceEditorOrTests(string name)
        {
            Assert.That(
                _assemblies[name].references.Where(r => r.StartsWith("VoxelRush.Editor", StringComparison.Ordinal) || r.StartsWith("VoxelRush.Tests", StringComparison.Ordinal)),
                Is.Empty);
        }

        [Test]
        public void ServerAndPresentation_AreIsolated()
        {
            Assert.That(_assemblies["VoxelRush.Server"].references, Does.Not.Contain("VoxelRush.Presentation"));
            Assert.That(_assemblies["VoxelRush.Presentation"].references, Does.Not.Contain("VoxelRush.Server"));
            Assert.That(_assemblies["VoxelRush.Presentation"].defineConstraints, Does.Contain("!UNITY_SERVER"));
            Assert.That(_assemblies["VoxelRush.Server"].defineConstraints, Does.Contain("UNITY_SERVER || UNITY_EDITOR"));
        }

        [Test]
        public void ProjectAssemblies_HaveNoReferenceCycles()
        {
            var visiting = new HashSet<string>();
            var done = new HashSet<string>();

            foreach (string name in _assemblies.Keys)
            {
                AssertAcyclic(name, visiting, done, new Stack<string>());
            }
        }

        private void AssertAcyclic(string name, HashSet<string> visiting, HashSet<string> done, Stack<string> path)
        {
            if (done.Contains(name) || !_assemblies.TryGetValue(name, out AssemblyDefinitionData data))
            {
                return;
            }

            path.Push(name);
            if (!visiting.Add(name))
            {
                Assert.Fail("Assembly reference cycle: " + string.Join(" <- ", path));
            }

            foreach (string reference in data.references)
            {
                AssertAcyclic(reference, visiting, done, path);
            }

            visiting.Remove(name);
            done.Add(name);
            path.Pop();
        }

        [Serializable]
        private sealed class AssemblyDefinitionData
        {
            public string name = string.Empty;
            public string[] references = Array.Empty<string>();
            public string[] defineConstraints = Array.Empty<string>();
            public bool noEngineReferences;
        }
    }
}
