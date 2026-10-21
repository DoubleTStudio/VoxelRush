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

        [TestCase(true)]
        [TestCase(false)]
        public void Vibration_RoundTrips(bool vibration)
        {
            new LocalSettings(ControlSettings.Default, null, 1f, 1f, vibration).Save(_dir);

            LocalSettings loaded = LocalSettings.Load(_dir);

            Assert.That(loaded.Vibration, Is.EqualTo(vibration));
            Assert.That(loaded.Source, Is.EqualTo("file"));
            Assert.That(File.ReadAllLines(FilePath), Does.Contain("version=" + LocalSettings.FormatVersion), "still format 1, so older builds keep reading it");
        }

        [Test]
        public void Vibration_MissingFromAnOlderFile_DefaultsOn_AndKeepsTheRest()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllLines(FilePath, new[] { "version=1", "controls.sensitivity=1.7", "controls.buttonScale=0.8", "quality.tier=Low", "audio.music=0.25", "audio.effects=0.5" });

            LocalSettings loaded = LocalSettings.Load(_dir);

            Assert.That(loaded.Vibration, Is.True);
            Assert.That((loaded.Controls.Sensitivity, loaded.QualityOverride, loaded.EffectsVolume, loaded.Source), Is.EqualTo((1.7f, (QualityTier?)QualityTier.Low, 0.5f, "file")));
            Assert.That(LocalSettings.Default.Vibration, Is.True);
        }

        [TestCase("off", false)]
        [TestCase("OFF", false)]
        [TestCase("0", false)]
        [TestCase("false", false)]
        [TestCase("on", true)]
        [TestCase("maybe", true)]
        [TestCase("", true)]
        public void Vibration_ParsesLeniently_GarbageKeepsTheDefault(string value, bool expected)
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllLines(FilePath, new[] { "version=1", "accessibility.vibration=" + value });

            Assert.That(LocalSettings.Load(_dir).Vibration, Is.EqualTo(expected));
        }

        [Test]
        public void Vibration_OffSurvivesASaveOfOtherSettings()
        {
            new LocalSettings(ControlSettings.Default, QualityTier.High, 1f, 1f, vibration: false).Save(_dir);
            LocalSettings loaded = LocalSettings.Load(_dir);
            new LocalSettings(loaded.Controls, loaded.QualityOverride, loaded.MusicVolume, 0.3f, loaded.Vibration).Save(_dir);

            Assert.That(LocalSettings.Load(_dir).Vibration, Is.False);
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
