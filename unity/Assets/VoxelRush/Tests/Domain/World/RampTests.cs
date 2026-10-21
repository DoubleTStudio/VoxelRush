using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using VoxelRush.Destruction;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;
using VoxelRush.World.Generation;
using VoxelRush.World.Meshing;
using VoxelRush.World.Persistence;
using VoxelRush.World.Sync;

namespace VoxelRush.Tests.Domain.World
{
    /// <summary>BUILD-002: ramp ids, geometry and culling, validation, sync/persistence and destruction.</summary>
    public sealed class RampTests
    {
        private static readonly ChunkData?[] NoNeighbours = new ChunkData?[6];
        private static readonly BlockShape[] Variants = { BlockShape.RampLow, BlockShape.RampHigh, BlockShape.Kicker };
        private readonly ChunkMesher _mesher = new ChunkMesher(BlockAtlas.Default);
        private readonly ChunkMeshData _mesh = new ChunkMeshData();

        private static IEnumerable<byte> RampIds() => Enumerable.Range(BlockIds.FirstRamp, BlockIds.LastRamp - BlockIds.FirstRamp + 1).Select(i => (byte)i);

        private static ChunkData Chunk(params (int X, int Y, int Z, byte Id)[] blocks)
        {
            var chunk = new ChunkData(new ChunkCoord(0, 0, 0), 1, new byte[ChunkMath.Volume]);
            foreach ((int x, int y, int z, byte id) in blocks)
            {
                chunk.Set(new LocalBlock(x, y, z), id);
            }

            return chunk;
        }

        private ChunkMeshData Mesh(params (int X, int Y, int Z, byte Id)[] blocks)
        {
            _mesher.Build(Chunk(blocks), NoNeighbours, _mesh);
            return _mesh;
        }

        private static (float X, float Y, float Z) Vertex(ChunkMeshData m, int i) => (m.Positions[i * 3], m.Positions[i * 3 + 1], m.Positions[i * 3 + 2]);

        private static (float X, float Y, float Z) Cross((float X, float Y, float Z) a, (float X, float Y, float Z) b, (float X, float Y, float Z) c)
        {
            var (ux, uy, uz) = (b.X - a.X, b.Y - a.Y, b.Z - a.Z);
            var (vx, vy, vz) = (c.X - a.X, c.Y - a.Y, c.Z - a.Z);
            return (uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);
        }

        /// <summary>Enclosed volume by the divergence theorem (positive when every face winds toward its normal).</summary>
        private static double Volume(ChunkMeshData m)
        {
            double volume = 0;
            for (int t = 0; t < m.Indices.Count; t += 3)
            {
                var a = Vertex(m, m.Indices[t]);
                var cross = Cross((0, 0, 0), Vertex(m, m.Indices[t + 1]), Vertex(m, m.Indices[t + 2]));
                volume += (a.X * cross.X + a.Y * cross.Y + a.Z * cross.Z) / 6.0;
            }

            return volume;
        }

        /// <summary>Total face area whose normal points along an axis direction (+X, -X, +Y, -Y, +Z, -Z).</summary>
        private static double Area(ChunkMeshData m, int direction)
        {
            double area = 0;
            for (int t = 0; t < m.Indices.Count; t += 3)
            {
                int v = m.Indices[t];
                float n = m.Normals[v * 3 + direction / 2] * (direction % 2 == 0 ? 1 : -1);
                if (n > 0.999f)
                {
                    var c = Cross(Vertex(m, v), Vertex(m, m.Indices[t + 1]), Vertex(m, m.Indices[t + 2]));
                    area += Math.Sqrt(c.X * c.X + c.Y * c.Y + c.Z * c.Z) / 2;
                }
            }

            return area;
        }

        [Test]
        public void ExistingIds_AreUnchanged_AndRampIdsFollowThem()
        {
            Assert.That(new[] { BlockIds.Air, BlockIds.Grass, BlockIds.Protected, BlockIds.Metal }, Is.EqualTo(new byte[] { 0, 1, 7, 12 }));
            Assert.That(new[] { BlockIds.FirstRamp, BlockIds.RampLow, BlockIds.RampHigh, BlockIds.Kicker, BlockIds.LastRamp }, Is.EqualTo(new byte[] { 13, 13, 17, 21, 24 }));
            Assert.That(BlockIds.Count, Is.EqualTo(25));
        }

        [Test]
        public void RampIds_RoundTripVariantAndYaw_AndRotateWraps()
        {
            var seen = new HashSet<byte>();
            foreach (BlockShape shape in Variants)
            {
                for (int yaw = 0; yaw < 4; yaw++)
                {
                    byte id = BlockShapes.RampId(shape, yaw);
                    Assert.That(seen.Add(id), Is.True);
                    Assert.That((BlockShapes.Shape(id), BlockShapes.Yaw(id), BlockShapes.Variant(id)), Is.EqualTo((shape, yaw, BlockShapes.RampId(shape, 0))));
                    Assert.That(BlockShapes.Rotate(id, 1), Is.EqualTo(BlockShapes.RampId(shape, yaw + 1)));
                    Assert.That(BlockShapes.Rotate(id, 4), Is.EqualTo(id));
                    Assert.That(BlockShapes.Rotate(id, -1), Is.EqualTo(BlockShapes.RampId(shape, yaw + 3)));
                }
            }

            Assert.That(seen, Is.EquivalentTo(RampIds()));
            Assert.That(BlockShapes.Rotate(BlockIds.Brick, 1), Is.EqualTo(BlockIds.Brick));
            Assert.That(BlockShapes.Shape(BlockIds.Air), Is.EqualTo(BlockShape.Empty));
            Assert.That(BlockShapes.Shape(200), Is.EqualTo(BlockShape.Cube), "unknown ids still mesh as cubes");
        }

        [Test]
        public void EveryRamp_IsAClosedSolid_WithTheExpectedVolume_AndUpwardSlope()
        {
            foreach (byte id in RampIds())
            {
                ChunkMeshData m = Mesh((5, 5, 5, id));
                BlockShapes.RampHeights(BlockShapes.Shape(id), out float low, out float high);

                Assert.That(Volume(m), Is.EqualTo((low + high) / 2).Within(1e-5), $"id {id}: closed and consistently wound");
                Assert.That(Area(m, BlockShapes.NegY), Is.EqualTo(1).Within(1e-5), $"id {id}: full bottom");
                Assert.That(Area(m, BlockShapes.PosY), Is.Zero, $"id {id}: no flat top");
                for (int t = 0; t < m.Indices.Count; t += 3)
                {
                    int v = m.Indices[t];
                    var c = Cross(Vertex(m, v), Vertex(m, m.Indices[t + 1]), Vertex(m, m.Indices[t + 2]));
                    double dot = c.X * m.Normals[v * 3] + c.Y * m.Normals[v * 3 + 1] + c.Z * m.Normals[v * 3 + 2];
                    Assert.That(dot, Is.GreaterThan(0), $"id {id}: triangle {t / 3} faces its normal");
                }
            }
        }

        [TestCase(0, 0f, 1f)]
        [TestCase(1, 1f, 0f)]
        [TestCase(2, 0f, -1f)]
        [TestCase(3, -1f, 0f)]
        public void SlopeNormal_LeansAgainstTheUphillDirection(int yaw, float uphillX, float uphillZ)
        {
            // 1:2 slope: normal (0, 2, -1) / sqrt(5) uphill +Z; the kicker: (0, 1, -h) / sqrt(1 + h²).
            ChunkMeshData low = Mesh((5, 5, 5, BlockShapes.RampId(BlockShape.RampLow, yaw)));
            int v = Enumerable.Range(0, low.VertexCount).First(i => low.Normals[i * 3 + 1] > 0.1f && low.Normals[i * 3 + 1] < 0.99f);
            Assert.That(low.Normals[v * 3], Is.EqualTo(-uphillX * 0.4472136f).Within(1e-6));
            Assert.That(low.Normals[v * 3 + 1], Is.EqualTo(0.8944272f).Within(1e-6));
            Assert.That(low.Normals[v * 3 + 2], Is.EqualTo(-uphillZ * 0.4472136f).Within(1e-6));

            ChunkMeshData kicker = Mesh((5, 5, 5, BlockShapes.RampId(BlockShape.Kicker, yaw)));
            v = Enumerable.Range(0, kicker.VertexCount).First(i => kicker.Normals[i * 3 + 1] > 0.1f && kicker.Normals[i * 3 + 1] < 0.99f);
            double length = Math.Sqrt(1 + BlockShapes.KickerHeight * BlockShapes.KickerHeight);
            Assert.That(kicker.Normals[v * 3 + 1], Is.EqualTo(1 / length).Within(1e-6));
            Assert.That(kicker.Normals[v * 3] + kicker.Normals[v * 3 + 2], Is.EqualTo(-(uphillX + uphillZ) * BlockShapes.KickerHeight / length).Within(1e-6));
        }

        [Test]
        public void RampOnABlock_HidesTheTopBelow_AndItsOwnBottom()
        {
            ChunkMeshData m = Mesh((5, 4, 5, BlockIds.Stone), (5, 5, 5, BlockIds.Kicker));

            Assert.That(Area(m, BlockShapes.PosY), Is.Zero);
            Assert.That(Area(m, BlockShapes.NegY), Is.EqualTo(1).Within(1e-5));
        }

        [Test]
        public void BlockAboveARamp_KeepsItsBottomFace()
        {
            ChunkMeshData m = Mesh((5, 5, 5, BlockIds.RampHigh), (5, 6, 5, BlockIds.Stone));

            Assert.That(Area(m, BlockShapes.NegY), Is.EqualTo(2).Within(1e-5), "a slope never fully covers the block above");
        }

        [Test]
        public void FullRampEnd_HidesTheBlockBehindIt_PartialFacesDoNot()
        {
            // Uphill +Z: the full back face meets the block at z = 6.
            ChunkMeshData m = Mesh((5, 5, 5, BlockIds.RampHigh), (5, 5, 6, BlockIds.Stone), (6, 5, 5, BlockIds.Stone));

            Assert.That(Area(m, BlockShapes.NegZ), Is.EqualTo(1 + 0.5).Within(1e-5), "the side block's front and the ramp's half-height front; the stone behind is hidden");
            Assert.That(Area(m, BlockShapes.NegX), Is.EqualTo(1 + 1 + 0.75).Within(1e-5), "the side block's face toward the ramp stays (a trapezoid doesn't cover it)");
            Assert.That(Area(m, BlockShapes.PosX), Is.EqualTo(1 + 1).Within(1e-5), "the ramp's side against the block is hidden");
        }

        [Test]
        public void LowThenHigh_FormALongRamp_WithTheInnerHalfFacesCulled()
        {
            ChunkMeshData m = Mesh((5, 5, 5, BlockIds.RampLow), (5, 5, 6, BlockIds.RampHigh));

            Assert.That(Area(m, BlockShapes.NegZ), Is.Zero, "the low end starts at the ground; the inner half faces are culled");
            Assert.That(Area(m, BlockShapes.PosZ), Is.EqualTo(1).Within(1e-5), "full back of the high piece");
            Assert.That(Area(m, BlockShapes.PosX), Is.EqualTo(0.25 + 0.75).Within(1e-5));
            Assert.That(Volume(m), Is.EqualTo(0.25 + 0.75).Within(1e-5));
        }

        [Test]
        public void SameRampsSideBySide_CullTheSharedSide_TurnedOnesDoNot()
        {
            ChunkMeshData same = Mesh((5, 5, 5, BlockIds.Kicker), (6, 5, 5, BlockIds.Kicker));
            Assert.That(Area(same, BlockShapes.PosX), Is.EqualTo(BlockShapes.KickerHeight / 2).Within(1e-5));
            Assert.That(Area(same, BlockShapes.NegX), Is.EqualTo(BlockShapes.KickerHeight / 2).Within(1e-5));

            ChunkMeshData turned = Mesh((5, 5, 5, BlockIds.Kicker), (6, 5, 5, BlockShapes.RampId(BlockShape.Kicker, 2)));
            Assert.That(Area(turned, BlockShapes.PosX), Is.EqualTo(BlockShapes.KickerHeight).Within(1e-5), "mirrored triangles don't cover each other");
        }

        [Test]
        public void KickersBackToBack_CullTheirMatchingEnds()
        {
            ChunkMeshData m = Mesh((5, 5, 5, BlockIds.Kicker), (5, 5, 6, BlockShapes.RampId(BlockShape.Kicker, 2)));

            Assert.That(Area(m, BlockShapes.PosZ), Is.Zero);
            Assert.That(Area(m, BlockShapes.NegZ), Is.Zero);
            Assert.That(Volume(m), Is.EqualTo(BlockShapes.KickerHeight).Within(1e-5));
        }

        [Test]
        public void SharedSide_IsCulledAcrossAChunkBorder()
        {
            ChunkData a = Chunk((15, 5, 5, BlockIds.Kicker));
            var b = new ChunkData(new ChunkCoord(1, 0, 0), 1, new byte[ChunkMath.Volume]);
            b.Set(new LocalBlock(0, 5, 5), BlockIds.Kicker);

            _mesher.Build(a, new ChunkData?[] { b, null, null, null, null, null }, _mesh);

            Assert.That(Area(_mesh, BlockShapes.PosX), Is.Zero);
            Assert.That(Area(_mesh, BlockShapes.NegX), Is.EqualTo(BlockShapes.KickerHeight / 2).Within(1e-5));
        }

        [Test]
        public void CubeOnlyMesh_IsUnchangedByRampSupport()
        {
            Assert.That(MeshGoldenTests.MeshTerrainHash().ToString("X16"), Is.EqualTo(MeshGoldenTests.Expected.ToString("X16")));
        }

        [Test]
        public void Validator_AcceptsRamps_RefusesUnknownAndProtectedIds()
        {
            var world = new FlatWorld();
            var validator = new EditValidator(ZoneMap.Empty);
            EditRejection Place(byte id) => validator.Validate(EditAction.Place, new BlockPos(2, 0, 2), id, 2.5f, 1.5f, 0.5f, world, Array.Empty<WorldBox>());

            foreach (byte id in RampIds())
            {
                Assert.That(Place(id), Is.EqualTo(EditRejection.None), $"id {id}");
            }

            Assert.That(Place(BlockIds.Count), Is.EqualTo(EditRejection.InvalidBlockType));
            Assert.That(Place(255), Is.EqualTo(EditRejection.InvalidBlockType));
            Assert.That(Place(BlockIds.Protected), Is.EqualTo(EditRejection.InvalidBlockType));
            Assert.That(Place(BlockIds.Air), Is.EqualTo(EditRejection.InvalidBlockType));
            Assert.That(validator.Validate(EditAction.Remove, new BlockPos(2, 0, 2), 255, 2.5f, 1.5f, 0.5f, world, Array.Empty<WorldBox>()),
                Is.EqualTo(EditRejection.NothingToRemove), "removal ignores the block type field");
        }

        [Test]
        public void PlacedRamp_IsJournaled_Replicated_AndPersisted()
        {
            var info = new WorldInfo(Guid.NewGuid(), GeneratorKind.FlatPlot, 0, FlatPlotGenerator.Version, 1, new ChunkCoord(0, -1, 0), new ChunkCoord(0, 0, 0));
            IBaseGenerator generator = info.CreateGenerator();
            var server = new WorldAuthority(new EditValidator(ZoneMap.Empty));
            foreach (ChunkCoord c in info.RegionChunks())
            {
                server.AddChunk(new ChunkData(c, generator.GeneratorVersion, generator.GenerateBlocks(c)));
            }

            var chunk = new ChunkCoord(0, 0, 0);
            byte ramp = BlockShapes.RampId(BlockShape.RampHigh, 3);
            var intent = new EditIntent(Guid.NewGuid(), chunk, new LocalBlock(4, 0, 4).Index, 0, EditAction.Place, ramp);
            EditResult result = server.Submit("alice", intent, 4.5f, 1.5f, 2.5f, Array.Empty<WorldBox>(), 1, 1);
            Assert.That(result.Accepted, Is.True, result.Rejection.ToString());

            var records = new List<ChunkMutationBatch>();
            JournalCodec.ReadAll(new MemoryStream(JournalCodec.Encode(result.Batch!)), records);
            Assert.That(records.Single().Changes.Single().BlockType, Is.EqualTo(ramp));

            var replica = new ClientWorldReplica(info);
            foreach (ChunkCoord c in info.RegionChunks())
            {
                replica.ApplySnapshot(ChunkSnapshotMessage.Decode(ChunkSnapshotMessage.From(new ChunkData(c, generator.GeneratorVersion, generator.GenerateBlocks(c))).Encode()));
            }

            replica.ApplyDelta(records.Single());
            Assert.That(ChunkHash.Compute(replica.Get(chunk)!), Is.EqualTo(ChunkHash.Compute(server.GetChunk(chunk)!)));

            string dir = Path.Combine(Path.GetTempPath(), "vr-ramp-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new WorldStore(dir, generator.GeneratorVersion, generator.GenerateBlocks);
                store.WriteSnapshot(server.GetChunk(chunk)!);
                ChunkData reloaded = new WorldStore(dir, generator.GeneratorVersion, generator.GenerateBlocks).LoadChunk(chunk);
                Assert.That(reloaded.Get(new LocalBlock(4, 0, 4)), Is.EqualTo(ramp));
                Assert.That(ChunkHash.Compute(reloaded), Is.EqualTo(ChunkHash.Compute(server.GetChunk(chunk)!)));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void RampOnAPillar_CollapsesWhenThePillarIsCut()
        {
            var world = new WorldAuthority(new EditValidator(ZoneMap.Empty));
            var gen = new FlatPlotGenerator();
            foreach (ChunkCoord c in new[] { new ChunkCoord(0, -1, 0), new ChunkCoord(0, 0, 0) })
            {
                world.AddChunk(new ChunkData(c, gen.GeneratorVersion, gen.GenerateBlocks(c)));
            }

            var chunk = new ChunkCoord(0, 0, 0);
            var pillar = new[] { new BlockChange((ushort)new LocalBlock(3, 0, 3).Index, BlockIds.Wood), new BlockChange((ushort)new LocalBlock(3, 1, 3).Index, BlockIds.Wood) };
            var ramp = new BlockChange((ushort)new LocalBlock(3, 2, 3).Index, BlockIds.RampLow);
            Assert.That(world.CommitServerChanges(Guid.NewGuid(), "builder", new[] { new ChunkChanges(chunk, pillar.Append(ramp).ToList()) }, 1).Accepted, Is.True);
            Assert.That(world.CommitServerChanges(Guid.NewGuid(), "builder", new[] { new ChunkChanges(chunk, new[] { new BlockChange(pillar[0].Index, BlockIds.Air) }) }, 2).Accepted, Is.True);

            var parts = new List<List<BlockPos>>();
            SupportStatus status = new SupportAnalyzer(new AuthorityStructureView(world)).AfterRemoval(new BlockPos(3, 0, 3), 512, parts);

            Assert.That(status, Is.EqualTo(SupportStatus.Disconnected));
            Assert.That(parts.Single(), Is.EquivalentTo(new[] { new BlockPos(3, 1, 3), new BlockPos(3, 2, 3) }), "the ramp falls with its support");
        }

        [Test]
        public void Ramps_TakeDamage_AndBreakAtTheirHp()
        {
            ChunkData chunk = Chunk((4, 4, 4, BlockIds.RampLow));
            var damage = new ChunkDamage(MaterialCatalog.Default);

            Assert.That(damage.Apply(chunk, new LocalBlock(4, 4, 4), 59).Outcome, Is.EqualTo(DamageOutcome.Damaged));
            Assert.That(damage.Apply(chunk, new LocalBlock(4, 4, 4), 1).Outcome, Is.EqualTo(DamageOutcome.Destroyed));
            Assert.That(chunk.Get(new LocalBlock(4, 4, 4)), Is.EqualTo(BlockIds.Air));
        }

        [Test]
        public void Explosion_BreaksRampsByTheirHp_InAnyYaw()
        {
            var world = new WorldAuthority(new EditValidator(ZoneMap.Empty));
            var gen = new FlatPlotGenerator();
            var chunk = new ChunkCoord(0, 0, 0);
            world.AddChunk(new ChunkData(chunk, gen.GeneratorVersion, gen.GenerateBlocks(chunk)));
            var ramps = new[] { BlockShapes.RampId(BlockShape.RampLow, 3), BlockShapes.RampId(BlockShape.Kicker, 1) };
            var changes = ramps.Select((id, i) => new BlockChange((ushort)new LocalBlock(4 + i, 0, 4).Index, id)).ToList();
            Assert.That(world.CommitServerChanges(Guid.NewGuid(), "builder", new[] { new ChunkChanges(chunk, changes) }, 1).Accepted, Is.True);

            // Both ramps sit 0.5 m from the blast: ceil(92 * 0.75) = 69 HP breaks the 60 HP low ramp, not the 80 HP kicker.
            ExplosionOutcome blast = new ExplosionResolver(new ExplosionModel(), ZoneMap.Empty, new DamageLedger(MaterialCatalog.Default))
                .Explode(world, Guid.NewGuid(), "car-1", 5f, 0.5f, 4.5f, 2f, 92, 2);

            Assert.That(blast.Rejection, Is.EqualTo(EditRejection.None));
            Assert.That(world.GetBlock(new BlockPos(4, 0, 4)), Is.EqualTo(BlockIds.Air));
            Assert.That(world.GetBlock(new BlockPos(5, 0, 4)), Is.EqualTo(ramps[1]));
        }

        private sealed class FlatWorld : IEditWorld
        {
            public bool IsLoaded(BlockPos block) => true;

            public byte GetBlock(BlockPos block) => block.Y < 0 ? BlockIds.Stone : BlockIds.Air;
        }
    }
}
