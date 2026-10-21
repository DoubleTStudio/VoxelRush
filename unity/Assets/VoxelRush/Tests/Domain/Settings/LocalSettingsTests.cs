using System;
using System.IO;
using NUnit.Framework;
using VoxelRush.Core.Input;
using VoxelRush.Core.Performance;
using VoxelRush.Core.Settings;

namespace VoxelRush.Tests.Domain.Settings
{
    /// <summary>SAVE-008: device-local settings survive restarts and never break on bad or newer files.</summary>
    public sealed class LocalSettingsTests
    {
        private string _dir = null!;

        [SetUp]
        public void SetUp() => _dir = Path.Combine(Path.GetTempPath(), "voxelrush-settings-" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }

        private string FilePath => Path.Combine(_dir, LocalSettings.FileName);

        [Test]
        public void RoundTrips()
        {
            new LocalSettings(new ControlSettings(1.7f, 0.8f), QualityTier.Low, 0.25f, 0.5f).Save(_dir);

            LocalSettings loaded = LocalSettings.Load(_dir);

            Assert.That((loaded.Controls.Sensitivity, loaded.Controls.ButtonScale, loaded.QualityOverride, loaded.MusicVolume, loaded.EffectsVolume, loaded.Source),
                Is.EqualTo((1.7f, 0.8f, (QualityTier?)QualityTier.Low, 0.25f, 0.5f, "file")));
            Assert.That(File.Exists(FilePath + ".tmp"), Is.False);
        }

        [Test]
        public void MissingFile_GivesDefaults_WithAdaptiveQuality()
        {
            LocalSettings loaded = LocalSettings.Load(_dir);

            Assert.That(loaded.QualityOverride, Is.Null);
            Assert.That(loaded.Controls.Sensitivity, Is.EqualTo(ControlSettings.Default.Sensitivity));
        }

        [Test]
        public void OutOfRangeOrGarbageValues_FallBackPerKey()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllLines(FilePath, new[] { "version=1", "controls.sensitivity=99", "controls.buttonScale=NaN", "quality.tier=Ultra", "audio.music=0.3", "unknown.key=1", "garbage" });

            LocalSettings loaded = LocalSettings.Load(_dir);

            Assert.That(loaded.Controls.Sensitivity, Is.EqualTo(ControlSettings.Default.Sensitivity));
            Assert.That(loaded.Controls.ButtonScale, Is.EqualTo(ControlSettings.Default.ButtonScale));
            Assert.That(loaded.QualityOverride, Is.Null);
            Assert.That(loaded.MusicVolume, Is.EqualTo(0.3f), "valid keys still apply");
        }

        [TestCase("version=2\ncontrols.sensitivity=1.9", "newer_format")]
        [TestCase("controls.sensitivity=1.9", "corrupt")]
        [TestCase("version=abc", "corrupt")]
        public void UnreadableVersions_GiveDefaults_AndLeaveTheFileAlone(string content, string source)
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(FilePath, content);

            LocalSettings loaded = LocalSettings.Load(_dir);

            Assert.That(loaded.Source, Is.EqualTo(source));
            Assert.That(loaded.Controls.Sensitivity, Is.EqualTo(ControlSettings.Default.Sensitivity));
            Assert.That(File.ReadAllText(FilePath), Is.EqualTo(content));
        }
    }
}
