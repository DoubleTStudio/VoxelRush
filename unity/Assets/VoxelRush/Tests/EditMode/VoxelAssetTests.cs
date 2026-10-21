using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace VoxelRush.Tests.EditMode
{
    /// <summary>VR-022 asset guards: atlas shader compiles for the active target; atlas imports without tile bleeding.</summary>
    public sealed class VoxelAssetTests
    {
        [Test]
        public void AtlasShader_CompilesWithoutErrors_ForActiveTarget()
        {
            Shader shader = Shader.Find("VoxelRush/VoxelAtlas");
            Assert.That(shader, Is.Not.Null);
            Assert.That(ShaderUtil.ShaderHasError(shader), Is.False, $"shader errors on {EditorUserBuildSettings.activeBuildTarget}");
            Assert.That(shader.isSupported, Is.True);
        }

        [Test]
        public void Atlas_UsesPointSamplingWithoutMipmaps()
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath("Assets/VoxelRush/Art/VoxelAtlas.png");

            Assert.That(importer, Is.Not.Null);
            Assert.That(importer.filterMode, Is.EqualTo(FilterMode.Point), "bilinear filtering bleeds neighbouring tiles");
            Assert.That(importer.mipmapEnabled, Is.False, "mipmaps average neighbouring tiles");
        }

        [Test]
        public void Material_UsesAtlasShaderAndTexture()
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>("Assets/VoxelRush/Art/VoxelAtlas.mat");

            Assert.That(material.shader.name, Is.EqualTo("VoxelRush/VoxelAtlas"));
            Assert.That(material.GetTexture("_Atlas"), Is.Not.Null);
        }
    }
}
