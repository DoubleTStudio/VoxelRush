using System;
using NUnit.Framework;
using VoxelRush.Core.Diagnostics;

namespace VoxelRush.Tests.Domain.Diagnostics
{
    public sealed class LogEventTests
    {
        [Test]
        public void Format_BareValues_WritesLogfmt()
        {
            string line = LogEvent.Format("server_started", ("port", (ushort)7777), ("tickRate", 30), ("headless", true));

            Assert.That(line, Is.EqualTo("event=server_started port=7777 tickRate=30 headless=true"));
        }

        [Test]
        public void Format_ValueWithSpacesAndNewline_IsQuotedAndEscaped()
        {
            string line = LogEvent.Format("error", ("message", "bad \"input\"\nevent=forged"));

            Assert.That(line, Is.EqualTo("event=error message=\"bad \\\"input\\\"\\nevent=forged\""));
        }

        [Test]
        public void Format_UsesInvariantCulture()
        {
            Assert.That(LogEvent.Format("tick", ("ms", 33.5)), Is.EqualTo("event=tick ms=33.5"));
        }

        [Test]
        public void Format_NullAndEmptyValues()
        {
            Assert.That(LogEvent.Format("e", ("a", null), ("b", "")), Is.EqualTo("event=e a=null b=\"\""));
        }

        [TestCase("")]
        [TestCase("has space")]
        [TestCase("a=b")]
        public void Format_InvalidKey_Throws(string key)
        {
            Assert.Throws<ArgumentException>(() => LogEvent.Format("e", (key, 1)));
        }

        [Test]
        public void Format_MissingEventName_Throws()
        {
            Assert.Throws<ArgumentException>(() => LogEvent.Format(" "));
        }
    }
}
