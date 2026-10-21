using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using VoxelRush.Destruction;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;
using VoxelRush.World.Generation;

namespace VoxelRush.Tests.Domain.Destruction
{
    /// <summary>DES-010: player-built anchor graph and disconnected component detection.</summary>
    public sealed class StructuralSupportTests
    {
        private static readonly FlatPlotGenerator Generator = new FlatPlotGenerator();

        private WorldAuthority _world = null!;
        private SupportAnalyzer _analyzer = null!;

        [SetUp]
        public void SetUp()
        {
            _world = new WorldAuthority(new EditValidator(ZoneMap.Empty));
            for (int x = -2; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int z = -1; z <= 0; z++)
                    {
                        var c = new ChunkCoord(x, y, z);
                        _world.AddChunk(new ChunkData(c, Generator.GeneratorVersion, Generator.GenerateBlocks(c)));
                    }
                }
            }

            _analyzer = new SupportAnalyzer(new AuthorityStructureView(_world));
        }

        private void Set(IEnumerable<BlockPos> blocks, byte type)
        {
            var changes = blocks.GroupBy(ChunkMath.ToChunk)
                .Select(g => new ChunkChanges(g.Key, g.Select(b => new BlockChange((ushort)ChunkMath.ToLocal(b).Index, type)).ToList()))
                .ToList();
            Assert.That(_world.CommitServerChanges(Guid.NewGuid(), "builder", changes, 1).Accepted, Is.True);
        }

        private void Place(IEnumerable<BlockPos> blocks) => Set(blocks, BlockIds.Wood);

        private void Remove(params BlockPos[] blocks) => Set(blocks, BlockIds.Air);

        private static IEnumerable<BlockPos> Column(int x, int z, int height) => Enumerable.Range(0, height).Select(y => new BlockPos(x, y, z));

        private static IEnumerable<BlockPos> Span(int x0, int x1, int y, int z) => Enumerable.Range(x0, x1 - x0 + 1).Select(x => new BlockPos(x, y, z));

        private List<List<BlockPos>> RemoveAndAnalyze(BlockPos block, out SupportStatus status, int budget = 512)
        {
            Remove(block);
            var result = new List<List<BlockPos>>();
            status = _analyzer.AfterRemoval(block, budget, result);
            return result;
        }

        [Test]
        public void Tower_CutAtTheBase_IsDisconnected()
        {
            Place(Column(3, 3, 6));

            List<List<BlockPos>> parts = RemoveAndAnalyze(new BlockPos(3, 0, 3), out SupportStatus status);

            Assert.That(status, Is.EqualTo(SupportStatus.Disconnected));
            Assert.That(parts.Single(), Is.EquivalentTo(Column(3, 3, 6).Skip(1)));
        }

        [Test]
        public void Bridge_CutInTheMiddle_BothHalvesStayAnchored()
        {
            Place(Column(0, 3, 4).Concat(Column(10, 3, 4)).Concat(Span(1, 9, 3, 3)));

            List<List<BlockPos>> parts = RemoveAndAnalyze(new BlockPos(5, 3, 3), out SupportStatus status);

            Assert.That(status, Is.EqualTo(SupportStatus.Supported));
            Assert.That(parts, Is.Empty);
        }

        [Test]
        public void Bridge_LosingBothPillars_FallsAsOneComponent()
        {
            Place(Column(0, 3, 4).Concat(Column(10, 3, 4)).Concat(Span(1, 9, 3, 3)));
            Remove(new BlockPos(0, 0, 3));
            Assert.That(_analyzer.AfterRemoval(new BlockPos(0, 0, 3), 512, new List<List<BlockPos>>()), Is.EqualTo(SupportStatus.Supported), "still held by the far pillar");

            List<List<BlockPos>> parts = RemoveAndAnalyze(new BlockPos(10, 0, 3), out SupportStatus status);

            Assert.That(status, Is.EqualTo(SupportStatus.Disconnected));
            Assert.That(parts.Single().Count, Is.EqualTo(3 + 3 + 9), "both pillar remnants and the deck");
        }

        [Test]
        public void Cut_SplitsIntoTwoFloatingPieces_ReportedSeparately_NoDuplicates()
        {
            // A T: two arms hanging off one post. Cutting the post below the arms frees one component (post top + arms);
            // removing the post top then splits it into two separate arms.
            Place(Column(4, 3, 3));                                   // post y 0..2
            Place(Span(5, 7, 2, 3).Concat(Span(1, 3, 2, 3)));        // arms at y=2 on both sides of the post top
            Remove(new BlockPos(4, 1, 3));                            // post no longer reaches the ground

            var parts = new List<List<BlockPos>>();
            Assert.That(_analyzer.AfterRemoval(new BlockPos(4, 1, 3), 512, parts), Is.EqualTo(SupportStatus.Disconnected));
            Assert.That(parts.Single().Count, Is.EqualTo(7), "post top + both arms are one component, reported once");

            // Now remove the post top: the two arms are separate components.
            Remove(new BlockPos(4, 2, 3));
            Assert.That(_analyzer.AfterRemoval(new BlockPos(4, 2, 3), 512, parts), Is.EqualTo(SupportStatus.Disconnected));
            Assert.That(parts.Count, Is.EqualTo(2));
            Assert.That(parts.SelectMany(p => p).Distinct().Count(), Is.EqualTo(6));
        }

        [Test]
        public void NaturalTerrain_IsExempt_AndAnchors()
        {
            // Dig a pit under natural ground: natural blocks never "collapse".
            Remove(new BlockPos(2, -2, 2));
            var parts = new List<List<BlockPos>>();

            Assert.That(_analyzer.AfterRemoval(new BlockPos(2, -2, 2), 512, parts), Is.EqualTo(SupportStatus.Supported));
            Assert.That(parts, Is.Empty);
        }

        [Test]
        public void SidewaysAttachmentToTerrain_Anchors()
        {
            // Dig through the 2-deep plot (open air below) and put a block back at the bottom: it touches natural
            // terrain only sideways, which still holds it.
            Remove(new BlockPos(6, -1, 6), new BlockPos(6, -2, 6));
            Place(new[] { new BlockPos(6, -2, 6) });

            Assert.That(_analyzer.Analyze(new BlockPos(6, -2, 6), 512, new List<BlockPos>()), Is.EqualTo(SupportStatus.Supported));
        }

        [Test]
        public void CrossChunkStructure_IsTracedAcrossBorders()
        {
            // An L-shaped beam crossing x = 0 and z = 0 chunk borders, cut at its only post.
            Place(Column(-1, -1, 4).Concat(Span(-1, 6, 3, -1)).Concat(new[] { new BlockPos(6, 3, 0), new BlockPos(6, 3, 1) }));

            List<List<BlockPos>> parts = RemoveAndAnalyze(new BlockPos(-1, 0, -1), out SupportStatus status);

            Assert.That(status, Is.EqualTo(SupportStatus.Disconnected));
            Assert.That(parts.Single().Select(ChunkMath.ToChunk).Distinct().Count(), Is.GreaterThanOrEqualTo(3));
        }

        [Test]
        public void UnloadedChunk_CountsAsSupport()
        {
            // A beam running off the loaded region (x >= 32) is assumed held there.
            Place(Span(25, 31, 3, 3).Concat(Column(25, 3, 3)));
            List<List<BlockPos>> parts = RemoveAndAnalyze(new BlockPos(25, 0, 3), out SupportStatus status);

            Assert.That(status, Is.EqualTo(SupportStatus.Supported));
            Assert.That(parts, Is.Empty);
        }

        [Test]
        public void ProtectedBlocks_Anchor()
        {
            Set(new[] { new BlockPos(8, 5, 8) }, BlockIds.Protected);     // world-authored, floating
            Place(Span(9, 12, 5, 8));

            Assert.That(_analyzer.Analyze(new BlockPos(12, 5, 8), 512, new List<BlockPos>()), Is.EqualTo(SupportStatus.Supported));
        }

        [Test]
        public void Budget_ExhaustedBeforeAnchor_IsUnresolved()
        {
            // A long deck reachable from the removal point only through many blocks.
            Place(Column(0, 3, 4).Concat(Span(1, 20, 3, 3)));
            var component = new List<BlockPos>();

            SupportStatus status = _analyzer.Analyze(new BlockPos(20, 3, 3), 8, component);

            Assert.That(status, Is.EqualTo(SupportStatus.Unresolved));
            Assert.That(component, Is.Empty);
            Assert.That(_analyzer.LastChecks, Is.EqualTo(8));
            Assert.That(_analyzer.Analyze(new BlockPos(20, 3, 3), 512, component), Is.EqualTo(SupportStatus.Supported), "enough budget finds the post");
        }

        [Test]
        public void AirAndNaturalStart_AreTriviallySupported()
        {
            var component = new List<BlockPos>();

            Assert.That(_analyzer.Analyze(new BlockPos(0, 10, 0), 512, component), Is.EqualTo(SupportStatus.Supported));
            Assert.That(_analyzer.Analyze(new BlockPos(0, -1, 0), 512, component), Is.EqualTo(SupportStatus.Supported));
            Assert.That(_analyzer.LastChecks, Is.Zero);
        }
    }
}
