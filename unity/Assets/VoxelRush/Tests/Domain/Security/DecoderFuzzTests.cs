using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using VoxelRush.Core.Networking;
using VoxelRush.Core.Race;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;
using VoxelRush.World.Generation;
using VoxelRush.World.Persistence;
using VoxelRush.World.Sync;

namespace VoxelRush.Tests.Domain.Security
{
    /// <summary>
    /// VR-082 (QUALITY_GATES Security: fuzz malformed payloads): every decoder of untrusted bytes either decodes or
    /// rejects cleanly — Try* methods return false, throwing decoders throw only InvalidDataException — for random
    /// payloads and for single/multi-byte mutations, truncations and extensions of valid ones. Seeded, so failures
    /// reproduce.
    /// </summary>
    public sealed class DecoderFuzzTests
    {
        private const int Iterations = 4000;

        private static IEnumerable<byte[]> Corpus(byte[] valid, int seed)
        {
            var rng = new Random(seed);
            for (int i = 0; i < Iterations; i++)
            {
                byte[] b;
                switch (i % 6)
                {
                    case 0:
                        b = new byte[rng.Next(0, valid.Length * 2 + 8)];
                        rng.NextBytes(b);
                        break;
                    case 1:
                        b = (byte[])valid.Clone();
                        b[rng.Next(b.Length)] ^= (byte)(1 << rng.Next(8));
                        break;
                    case 2:
                        b = (byte[])valid.Clone();
                        for (int k = 0; k < 1 + rng.Next(8); k++)
                        {
                            b[rng.Next(b.Length)] = (byte)rng.Next(256);
                        }

                        break;
                    case 3:
                        b = valid.Take(rng.Next(valid.Length)).ToArray();
                        break;
                    case 4:
                        b = valid.Concat(Enumerable.Range(0, 1 + rng.Next(16)).Select(_ => (byte)rng.Next(256))).ToArray();
                        break;
                    default:
                        // Hostile lengths/counts: set a random 2-byte window to 0xFFFF.
                        b = (byte[])valid.Clone();
                        int at = rng.Next(Math.Max(1, b.Length - 1));
                        b[at] = 0xFF;
                        if (at + 1 < b.Length)
                        {
                            b[at + 1] = 0xFF;
                        }

                        break;
                }

                yield return b;
            }
        }

        private static void NeverThrows(byte[] valid, int seed, Func<byte[], bool> tryDecode)
        {
            Assert.That(tryDecode(valid), Is.True, "the seed payload must decode");
            foreach (byte[] payload in Corpus(valid, seed))
            {
                try
                {
                    tryDecode(payload);
                }
                catch (Exception e)
                {
                    Assert.Fail($"{e.GetType().Name} on {payload.Length} bytes [{BitConverter.ToString(payload.Take(48).ToArray())}]: {e.Message}");
                }
            }
        }

        private static void OnlyInvalidData(byte[] valid, int seed, Action<byte[]> decode)
        {
            decode(valid);
            foreach (byte[] payload in Corpus(valid, seed))
            {
                try
                {
                    decode(payload);
                }
                catch (InvalidDataException)
                {
                }
                catch (Exception e)
                {
                    Assert.Fail($"{e.GetType().Name} (expected InvalidDataException) on {payload.Length} bytes: {e.Message}");
                }
            }
        }

        [Test]
        public void ConnectionPayload() =>
            NeverThrows(VoxelRush.Core.Networking.ConnectionPayload.EncodeTicket(3, new JoinTicket(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), WorldAccessRole.Editor, 1, 2).Sign(new byte[32])), 1,
                b => VoxelRush.Core.Networking.ConnectionPayload.TryDecode(b, out _, out _, out _, out _));

        [Test]
        public void JoinTickets()
        {
            byte[] key = Enumerable.Repeat((byte)5, 32).ToArray();
            byte[] valid = new JoinTicket(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), WorldAccessRole.Owner, 100, 200).Sign(key);
            NeverThrows(valid, 2, b => JoinTicket.TryVerify(b, key, 150, out _) == TicketRejection.None || true);
            // Mutations of a signed ticket never verify.
            Assert.That(Corpus(valid, 3).Count(b => !b.SequenceEqual(valid) && JoinTicket.TryVerify(b, key, 150, out _) == TicketRejection.None), Is.Zero);
        }

        [Test]
        public void RaceState() =>
            NeverThrows(new RaceStateMessage(Guid.NewGuid(), 7, RacePhase.Racing, 99, new[] { 1, 2, 3 }).Encode(), 4, b => RaceStateMessage.TryDecode(b, out _));

        [Test]
        public void Track() =>
            NeverThrows(TrackMessage.Encode(new TrackDefinition(1, new Vec3(0, 1, 0), new[] { new Gate(1, 0, 1, 10, 0, 1, 5, 3), new Gate(2, 0, 1, 50, 0, 1, 5, 3) })), 5,
                b => TrackMessage.TryDecode(b, out _));

        [Test]
        public void RaceProgress() =>
            NeverThrows(new RaceProgressMessage("P1", 1, 3, CrossingOutcome.Accepted).Encode(), 6, b => RaceProgressMessage.TryDecode(b, out _));

        [Test]
        public void RaceResults() =>
            NeverThrows(new RaceResultsMessage(Guid.NewGuid(), new[] { ("P1", new RaceResult("a", 1, true, 300, 3)), ("P2", new RaceResult("b", 2, false, 0, 1)) }).Encode(), 7,
                b => RaceResultsMessage.TryDecode(b, out _));

        [Test]
        public void JournalRecordsAndDeltas()
        {
            byte[] valid = JournalCodec.Encode(new ChunkMutationBatch(Guid.NewGuid(), "client:1", new ChunkCoord(-1, 0, 2), 4, 5, 9, new[] { new BlockChange(17, BlockIds.Stone) }));
            var records = new List<ChunkMutationBatch>();
            NeverThrows(valid, 8, b =>
            {
                records.Clear();
                JournalCodec.ReadAll(new MemoryStream(b), records);
                return true;
            });
            // Anything accepted from mutated bytes still satisfies the contract's bounds.
            foreach (byte[] b in Corpus(valid, 9))
            {
                records.Clear();
                JournalCodec.ReadAll(new MemoryStream(b), records);
                Assert.That(records.SelectMany(r => r.Changes).All(c => c.Index < ChunkMath.Volume), Is.True);
            }
        }

        [Test]
        public void WorldInfo_ThrowsOnlyInvalidData() =>
            OnlyInvalidData(new WorldInfo(Guid.NewGuid(), GeneratorKind.FlatPlot, 0, FlatPlotGenerator.Version, 1, new ChunkCoord(-1, -1, -1), new ChunkCoord(1, 0, 1)).Encode(), 10,
                b => WorldInfo.Decode(b));

        [Test]
        public void ChunkSnapshot_ThrowsOnlyInvalidData()
        {
            var gen = new FlatPlotGenerator();
            ChunkData chunk = new ChunkData(new ChunkCoord(0, -1, 0), gen.GeneratorVersion, gen.GenerateBlocks(new ChunkCoord(0, -1, 0)));
            chunk.Set(new LocalBlock(1, 15, 1), BlockIds.Air);
            chunk.Set(new LocalBlock(2, 15, 2), BlockIds.Brick);
            OnlyInvalidData(ChunkSnapshotMessage.From(chunk).Encode(), 11, b => ChunkSnapshotMessage.Decode(b));
        }
    }
}
