using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using VoxelRush.Gameplay.World;
using VoxelRush.World.Chunks;
using VoxelRush.World.Generation;
using VoxelRush.World.Meshing;
using Debug = UnityEngine.Debug;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace VoxelRush.Tests.EditMode
{
    /// <summary>VR-070: the Burst mesher matches the managed one exactly, and the profile that motivated it.</summary>
    public sealed class BurstMeshingTests
    {
        private static readonly TerrainGenerator Generator = new TerrainGenerator(12345);

        private static List<(ChunkData Chunk, ChunkData?[] Neighbours)> Fixture(int radius = 2)
        {
            var list = new List<(ChunkData, ChunkData?[])>();
            for (int x = -radius; x <= radius; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int z = -radius; z <= radius; z++)
                    {
                        var c = new ChunkCoord(x, y, z);
                        ChunkData chunk = Generator.Generate(c);
                        for (int i = 0; i < 40; i++)
                        {
                            chunk.Set(LocalBlock.FromIndex((i * 997 + x * 31 + z * 17 + 4096 * 8) % 4096), i % 3 == 0 ? BlockIds.Stone : BlockIds.Air);
                        }

                        list.Add((chunk, new ChunkData?[]
                        {
                            Generator.Generate(c.Offset(1, 0, 0)), Generator.Generate(c.Offset(-1, 0, 0)), Generator.Generate(c.Offset(0, 1, 0)),
                            null, Generator.Generate(c.Offset(0, 0, 1)), Generator.Generate(c.Offset(0, 0, -1)),
                        }));
                    }
                }
            }

            return list;
        }

        [Test]
        public void BurstMesher_ProducesExactlyTheManagedMesh()
        {
            var managed = new ChunkMesher(BlockAtlas.Default);
            var expected = new ChunkMeshData();
            using var burst = new BurstChunkMesher(BlockAtlas.Default);
            int quads = 0;
            foreach ((ChunkData chunk, ChunkData?[] neighbours) in Fixture())
            {
                managed.Build(chunk, neighbours, expected);
                burst.Build(chunk, neighbours);

                Assert.That(burst.Indices.ToArray(Allocator.Temp).ToArray(), Is.EqualTo(expected.Indices.ToArray()), $"indices {chunk.Coord}");
                Assert.That(burst.Positions.ToArray(Allocator.Temp).ToArray().SelectMany(p => new[] { p.x, p.y, p.z }), Is.EqualTo(expected.Positions), $"positions {chunk.Coord}");
                Assert.That(burst.Normals.ToArray(Allocator.Temp).ToArray().SelectMany(p => new[] { p.x, p.y, p.z }), Is.EqualTo(expected.Normals));
                Assert.That(burst.Uvs.ToArray(Allocator.Temp).ToArray().SelectMany(p => new[] { p.x, p.y }), Is.EqualTo(expected.Uvs));
                Assert.That(burst.TileIds.ToArray(Allocator.Temp).ToArray(), Is.EqualTo(expected.Tiles));
                quads += expected.QuadCount;
            }

            Assert.That(quads, Is.GreaterThan(1000), "fixture actually meshes something");
        }

        [Test]
        public void Schedule_RunsOffTheMainThread_AndRefusesOverlap()
        {
            (ChunkData chunk, ChunkData?[] neighbours) = Fixture(0)[1];
            using var burst = new BurstChunkMesher(BlockAtlas.Default);

            JobHandle handle = burst.Schedule(chunk, neighbours);
            Assert.Throws<InvalidOperationException>(() => burst.Schedule(chunk, neighbours));
            handle.Complete();
            burst.Complete();

            Assert.That(burst.IsEmpty, Is.False);
        }

        [Test]
        public void ManagedMesher_DoesNotAllocateAfterWarmUp()
        {
            (ChunkData chunk, ChunkData?[] neighbours) = Fixture(0)[1];
            var mesher = new ChunkMesher(BlockAtlas.Default);
            var output = new ChunkMeshData();
            mesher.Build(chunk, neighbours, output);

            Assert.That(() => mesher.Build(chunk, neighbours, output), Is.Not.AllocatingGCMemory());
        }

        /// <summary>The VR-070 profile: managed vs Burst meshing and collider cooking per chunk, written to artifacts/perf.</summary>
        [Test]
        public void Profile_MeshingAndCooking()
        {
            List<(ChunkData Chunk, ChunkData?[] Neighbours)> fixture = Fixture(3);   // 147 chunks
            var managed = new ChunkMesher(BlockAtlas.Default);
            var output = new ChunkMeshData();
            using var burst = new BurstChunkMesher(BlockAtlas.Default);
            foreach (var f in fixture.Take(5))
            {
                managed.Build(f.Chunk, f.Neighbours, output);
                burst.Build(f.Chunk, f.Neighbours);
            }

            double Measure(Action<(ChunkData Chunk, ChunkData?[] Neighbours)> work)
            {
                var watch = Stopwatch.StartNew();
                foreach (var f in fixture)
                {
                    work(f);
                }

                return watch.Elapsed.TotalMilliseconds / fixture.Count;
            }

            double managedMs = Measure(f => managed.Build(f.Chunk, f.Neighbours, output));
            double burstMs = Measure(f => burst.Build(f.Chunk, f.Neighbours));
            // Main-thread share of a scheduled build: padded fill + copy + schedule; the wait for the worker is excluded.
            var scheduleWatch = new Stopwatch();
            foreach (var f in fixture)
            {
                scheduleWatch.Start();
                burst.Schedule(f.Chunk, f.Neighbours);
                scheduleWatch.Stop();
                burst.Complete();
            }

            double scheduleMs = scheduleWatch.Elapsed.TotalMilliseconds / fixture.Count;
            var fill = new byte[ChunkMesher.PaddedSize];
            var scratch = new byte[ChunkMath.Volume];
            double fillMs = Measure(f => ChunkMesher.FillPadded(f.Chunk, f.Neighbours, fill, scratch));

            var mesh = new Mesh();
            double cookMs = Measure(f =>
            {
                burst.Build(f.Chunk, f.Neighbours);
                if (!burst.IsEmpty)
                {
                    mesh.Clear();
                    mesh.SetVertices(burst.Positions.AsArray());
                    mesh.SetIndices(burst.Indices.AsArray(), MeshTopology.Triangles, 0);
                    Physics.BakeMesh(mesh.GetEntityId(), false);
                }
            }) - burstMs;
            UnityEngine.Object.DestroyImmediate(mesh);

            int quads = 0;
            foreach (var f in fixture)
            {
                managed.Build(f.Chunk, f.Neighbours, output);
                quads += output.QuadCount;
            }

            string json = "{\n" +
                $"  \"task\": \"VR-070\",\n  \"unity\": \"{Application.unityVersion}\",\n  \"host\": \"{SystemInfo.processorType.Replace("\"", "")} ({SystemInfo.processorCount} threads), editor EditMode\",\n" +
                $"  \"chunks\": {fixture.Count},\n  \"meanQuadsPerChunk\": {quads / (double)fixture.Count:0.0},\n" +
                $"  \"managedMeshMsPerChunk\": {managedMs:0.000},\n  \"burstMeshMsPerChunk\": {burstMs:0.000},\n" +
                $"  \"burstSpeedup\": {managedMs / burstMs:0.0},\n  \"paddedFillMsPerChunk\": {fillMs:0.000},\n" +
                $"  \"scheduledMainThreadMsPerChunk\": {scheduleMs:0.000},\n  \"physxCookMsPerChunk\": {cookMs:0.000}\n}}\n";
            string path = Path.Combine(Application.dataPath, "..", "..", "artifacts", "perf", "VR-070-meshing.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
            Debug.Log(json);

            Assert.That(burstMs, Is.LessThan(managedMs), "Burst must beat the managed mesher");
        }
    }
}
