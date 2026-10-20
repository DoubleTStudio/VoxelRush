using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;
using VoxelRush.World.Generation;
using VoxelRush.World.Persistence;

// write <dir>: edit forever, print "ACK <operationId> <x,y,z> <newRevision>" only after the record is durable.
// verify <dir> <ackFile>: recover and check every ACKed operation survived.
var generator = new TerrainGenerator(12345);
var chunks = new[] { new ChunkCoord(0, 0, 0), new ChunkCoord(-1, 0, 0) };
string mode = args[0];
string dir = args[1];
var store = new WorldStore(dir, TerrainGenerator.Version, generator.GenerateBlocks);
store.RecoverJournal();
var writer = mode == "write" ? new JournalWriter(store.JournalPath, capacity: 256) : null;
var authority = new WorldAuthority(new EditValidator(Array.Empty<WorldBox>()), maxEditsPerSecond: 1_000_000, maxQueuedPerTick: 1_000_000, sink: writer);
foreach (ChunkCoord c in chunks)
{
    authority.AddChunk(store.LoadChunk(c));
}

authority.RememberRecovered(store.RecoveredRecords);

if (mode == "verify")
{
    var acked = File.ReadAllLines(args[2]).Where(l => l.StartsWith("ACK ")).Select(l => l.Split(' ')).ToList();
    var durableOps = new HashSet<Guid>(store.RecoveredRecords.Select(r => r.OperationId));
    int missing = 0;
    foreach (string[] ack in acked)
    {
        var op = Guid.Parse(ack[1]);
        int[] xyz = ack[2].Split(',').Select(int.Parse).ToArray();
        ulong revision = ulong.Parse(ack[3]);
        ChunkData chunk = authority.GetChunk(new ChunkCoord(xyz[0], xyz[1], xyz[2]))!;
        bool covered = durableOps.Contains(op) || chunk.Revision >= revision;
        if (!covered || chunk.Revision < revision)
        {
            missing++;
            Console.WriteLine($"LOST {op} chunk {ack[2]} acked revision {revision}, recovered revision {chunk.Revision}");
        }
    }

    foreach (string problem in store.Problems)
    {
        Console.WriteLine($"PROBLEM {problem}");
    }

    Console.WriteLine($"VERIFY acked={acked.Count} lost={missing} records={store.RecoveredRecords.Count} truncatedBytes={store.TruncatedBytes} " +
        $"revisions={string.Join(",", chunks.Select(c => authority.GetChunk(c)!.Revision))}");
    return missing == 0 && store.Problems.Count == 0 ? 0 : 1;
}

var random = new Random(Environment.ProcessId);
var durable = new List<ChunkMutationBatch>();
long acks = 0;
for (uint tick = 1; ; tick++)
{
    ChunkCoord chunk = chunks[random.Next(chunks.Length)];
    ChunkData data = authority.GetChunk(chunk)!;
    var local = new LocalBlock(random.Next(16), random.Next(16), random.Next(16));
    bool solid = data.Get(local) != BlockIds.Air;
    var intent = new EditIntent(Guid.NewGuid(), chunk, local.Index, data.Revision, solid ? EditAction.Remove : EditAction.Place, BlockIds.Stone);
    BlockPos target = ChunkMath.ToWorld(chunk, local);
    EditResult result = authority.Submit("crash-harness", intent, target.X + 0.5f, target.Y + 2.5f, target.Z + 0.5f, Array.Empty<WorldBox>(), tick, tick);
    if (result.Rejection == EditRejection.PersistenceBackpressure)
    {
        // The authority refused before applying; give the writer a moment.
        System.Threading.Thread.Sleep(1);
    }

    writer!.DrainDurable(durable);
    foreach (ChunkMutationBatch batch in durable)
    {
        Console.WriteLine($"ACK {batch.OperationId} {batch.Chunk.X},{batch.Chunk.Y},{batch.Chunk.Z} {batch.NewRevision}");
        acks++;
    }

    Console.Out.Flush();
    // Snapshot only fully durable state, so a snapshot never contains an un-journaled (un-ACKed) edit.
    if (acks > 0 && acks % 300 == 0 && writer.Pending == 0 && durable.Count > 0)
    {
        foreach (ChunkCoord c in chunks)
        {
            store.WriteSnapshot(authority.GetChunk(c)!);
        }
    }
}
