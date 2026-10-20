using System;
using NUnit.Framework;
using VoxelRush.Core.Hosting;

namespace VoxelRush.Tests.Domain.Hosting
{
    public sealed class ClientLaunchOptionsTests
    {
        [TestCase("127.0.0.1:7777", "127.0.0.1", 7777)]
        [TestCase("voxelrush-server:7800", "voxelrush-server", 7800)]
        [TestCase("play.example.com:1", "play.example.com", 1)]
        public void Endpoint_ParsesHostAndPort(string text, string host, int port)
        {
            Assert.That(ServerEndpoint.TryParse(text, out ServerEndpoint endpoint, out string error), Is.True, error);
            Assert.That(endpoint.Host, Is.EqualTo(host));
            Assert.That(endpoint.Port, Is.EqualTo(port));
            Assert.That(endpoint.ToString(), Is.EqualTo(text));
        }

        [TestCase("")]
        [TestCase("7777")]
        [TestCase(":7777")]
        [TestCase("host:")]
        [TestCase("host:0")]
        [TestCase("host:65536")]
        [TestCase("host:-1")]
        [TestCase("::1:7777")]
        [TestCase("bad host:7777")]
        [TestCase("host/../x:7777")]
        [TestCase("-host:7777")]
        public void Endpoint_RejectsInvalidInput(string text)
        {
            Assert.That(ServerEndpoint.TryParse(text, out _, out string error), Is.False);
            Assert.That(error, Is.Not.Empty);
        }

        [Test]
        public void Endpoint_RejectsOverlongHost()
        {
            Assert.That(ServerEndpoint.TryParse(new string('a', 254) + ":7777", out _, out _), Is.False);
        }

        [Test]
        public void Parse_NoArguments_HasNoConnectOrSmoke()
        {
            var result = ClientLaunchOptions.Parse(Array.Empty<string>());

            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Options!.Connect, Is.Null);
            Assert.That(result.Options.SmokeTestSeconds, Is.Null);
        }

        [Test]
        public void Parse_ConnectAndSmokeAmongUnityFlags()
        {
            var result = ClientLaunchOptions.Parse(new[] { "VoxelRush", "-batchmode", "-connect", "server:7777", "-smokeTestSeconds", "5", "-nographics" });

            Assert.That(result.IsSuccess, Is.True, string.Join("; ", result.Errors));
            Assert.That(result.Options!.Connect!.Value.ToString(), Is.EqualTo("server:7777"));
            Assert.That(result.Options.SmokeTestSeconds, Is.EqualTo(5));
        }

        [Test]
        public void Parse_SmokeWithoutConnect_Fails()
        {
            var result = ClientLaunchOptions.Parse(new[] { "-smokeTestSeconds", "5" });

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Errors[0], Does.Contain("-connect"));
        }

        [TestCase("-connect", "nope")]
        [TestCase("-smokeTestSeconds", "0")]
        [TestCase("-smokeTestSeconds", "601")]
        public void Parse_InvalidValues_Fail(string name, string value)
        {
            var args = name == "-smokeTestSeconds" ? new[] { "-connect", "h:1", name, value } : new[] { name, value };

            var result = ClientLaunchOptions.Parse(args);

            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.Errors[0], Does.StartWith(name));
        }

        [Test]
        public void Parse_MissingValue_Fails()
        {
            Assert.That(ClientLaunchOptions.Parse(new[] { "-connect" }).IsSuccess, Is.False);
        }
    }
}

namespace VoxelRush.Tests.Domain.Hosting
{
    public sealed class NetworkConditionOptionsTests
    {
        [Test]
        public void TryParse_ReadsConditionsAmongOtherArgs()
        {
            Assert.That(NetworkConditionOptions.TryParse(new[] { "-batchmode", "-simRttMs", "100", "-simJitterMs", "20", "-simLossPercent", "2", "-connect", "h:1" }, out var o, out string e), Is.True, e);
            Assert.That(o.RttMs, Is.EqualTo(100));
            Assert.That(o.OneWaySeconds, Is.EqualTo(0.05).Within(1e-9));
            Assert.That(o.OneWayJitterSeconds, Is.EqualTo(0.02).Within(1e-9));
            Assert.That(o.LossPercent, Is.EqualTo(2));
            Assert.That(o.IsActive, Is.True);
        }

        [Test]
        public void TryParse_NoArgs_IsInactive()
        {
            Assert.That(NetworkConditionOptions.TryParse(Array.Empty<string>(), out var o, out _), Is.True);
            Assert.That(o.IsActive, Is.False);
        }

        [TestCase("-simRttMs", "x")]
        [TestCase("-simRttMs", "-5")]
        [TestCase("-simRttMs", "5000")]
        [TestCase("-simLossPercent", "101")]
        public void TryParse_RejectsInvalid(string name, string value)
        {
            Assert.That(NetworkConditionOptions.TryParse(new[] { name, value }, out _, out string error), Is.False);
            Assert.That(error, Is.Not.Empty);
        }

        [Test]
        public void TryParse_RejectsJitterLargerThanHalfRtt()
        {
            Assert.That(NetworkConditionOptions.TryParse(new[] { "-simRttMs", "40", "-simJitterMs", "30" }, out _, out _), Is.False);
        }
    }
}
