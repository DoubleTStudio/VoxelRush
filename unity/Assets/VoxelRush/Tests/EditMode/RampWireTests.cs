using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using VoxelRush.Networking.World;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;
using VoxelRush.World.Persistence;

namespace VoxelRush.Tests.EditMode
{
    /// <summary>BUILD-002: ramp ids (yaw included) travel through the unchanged edit and delta encodings.</summary>
    public sealed class RampWireTests
    {
        [Test]
        public void EveryRampId_RoundTripsThroughTheEditIntent()
        {
            for (int id = BlockIds.FirstRamp; id <= BlockIds.LastRamp; id++)
            {
                var intent = new EditIntent(Guid.NewGuid(), new ChunkCoord(-1, 0, 2), 1234, 7, EditAction.Place, (byte)id);
                Assert.That(EditWire.TryDecodeIntent(EditWire.EncodeIntent(intent), out EditIntent decoded), Is.True);
                Assert.That((decoded.BlockType, decoded.Action, decoded.LocalIndex), Is.EqualTo(((byte)id, EditAction.Place, 1234)));
            }
        }

        [Test]
        public void RampDelta_RoundTripsThroughTheJournalRecord()
        {
            byte ramp = BlockShapes.RampId(BlockShape.Kicker, 2);
            var batch = new ChunkMutationBatch(Guid.NewGuid(), "client:1", new ChunkCoord(0, 0, -1), 3, 4, 99, new[] { new BlockChange(42, ramp) });
            var records = new List<ChunkMutationBatch>();
            JournalCodec.ReadAll(new MemoryStream(JournalCodec.Encode(batch)), records);

            Assert.That(records[0].Changes[0].BlockType, Is.EqualTo(ramp));
            Assert.That(BlockShapes.Yaw(records[0].Changes[0].BlockType), Is.EqualTo(2));
        }
    }
}
