using System;
using NUnit.Framework;
using VoxelRush.Core;
using VoxelRush.Core.Hosting;

namespace VoxelRush.Tests.Domain.Hosting
{
    public sealed class ServerLaunchOptionsTests
    {
        [Test]
        public void Parse_NoArguments_ReturnsDefaults()
        {
            var result = ServerLaunchOptions.Parse(Array.Empty<string>());

            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Options!.Port, Is.EqualTo(ServerLaunchOptions.DefaultPort));
            Assert.That(result.Options.MaxPlayers, Is.EqualTo(SimulationConstants.MaxPlayersPerServer));
            Assert.That(result.Options.SmokeTestTicks, Is.Null);
        }

        [Test]
        public void Parse_KnownArgumentsAmongUnityFlags_ParsesValues()
        {
            var args = new[]
            {
                "/opt/voxelrush/VoxelRushServer.x86_64", "-batchmode", "-nographics",
                "-logFile", "-", "-port", "7800", "-MAXPLAYERS", "4", "-smokeTestTicks", "90",
            };

            var result = ServerLaunchOptions.Parse(args);

            Assert.That(result.IsSuccess, Is.True, string.Join("; ", result.Errors));
            Assert.That(result.Options!.Port, Is.EqualTo(7800));
            Assert.That(result.Options.MaxPlayers, Is.EqualTo(4));
            Assert.That(result.Options.SmokeTestTicks, Is.EqualTo(90));
        }

        [TestCase("-port", "0")]
        [TestCase("-port", "65536")]
        [TestCase("-port", "-1")]
        [TestCase("-port", "abc")]
        [TestCase("-maxPlayers", "0")]
        [TestCase("-maxPlayers", "9")]
        [TestCase("-maxPlayers", "+4")]
        [TestCase("-smokeTestTicks", "0")]
        [TestCase("-smokeTestTicks", "18001")]
        public void Parse_OutOfRangeOrMalformedValue_Fails(string name, string value)
        {
            var result = ServerLaunchOptions.Parse(new[] { name, value });

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Options, Is.Null);
            Assert.That(result.Errors, Has.Count.EqualTo(1));
            Assert.That(result.Errors[0], Does.StartWith(name));
        }

        [Test]
        public void Parse_MissingTrailingValue_Fails()
        {
            var result = ServerLaunchOptions.Parse(new[] { "-batchmode", "-port" });

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Errors[0], Does.Contain("requires a value"));
        }

        [Test]
        public void Parse_MultipleErrors_ReportsEach()
        {
            var result = ServerLaunchOptions.Parse(new[] { "-port", "x", "-maxPlayers", "100" });

            Assert.That(result.Errors, Has.Count.EqualTo(2));
        }

        [Test]
        public void Parse_NullArguments_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => ServerLaunchOptions.Parse(null!));
        }

        [Test]
        public void Constructor_RejectsInvalidValues()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ServerLaunchOptions(0, 8, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ServerLaunchOptions(7777, 9, null));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ServerLaunchOptions(7777, 8, 0));
        }
    }
}
