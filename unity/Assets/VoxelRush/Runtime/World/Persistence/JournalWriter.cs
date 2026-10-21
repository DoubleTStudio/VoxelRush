using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using VoxelRush.World.Editing;

namespace VoxelRush.World.Persistence
{
    /// <summary>
    /// Background, group-committing journal appender (ADR-007, PERSISTENCE_SECURITY Commit protocol): the simulation
    /// enqueues accepted batches without blocking; a writer thread appends and fsyncs, then reports them durable.
    /// Only durable batches may be acknowledged. A full queue is backpressure: callers must refuse new edits.
    /// </summary>
    public sealed class JournalWriter : IDisposable, IMutationSink
    {
        private readonly Stream _file;
        private readonly BlockingCollection<ChunkMutationBatch> _queue;
        private readonly ConcurrentQueue<ChunkMutationBatch> _durable = new ConcurrentQueue<ChunkMutationBatch>();
        private readonly Thread _thread;
        private readonly int _maxGroup;
        private readonly int _capacity;
        private volatile Exception? _failure;
        private long _pending;

        public JournalWriter(string path, int capacity = 1024, int maxGroup = 64)
            // Exclusive: two writers on one world journal would interleave records (ADR-010 single writer).
            : this(new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough), path, capacity, maxGroup)
        {
        }

        /// <summary>Over any stream; tests inject failing streams (disk full, fsync errors).</summary>
        internal JournalWriter(Stream file, string path, int capacity = 1024, int maxGroup = 64)
        {
            Path = path;
            _file = file;
            _file.Seek(0, SeekOrigin.End);
            _queue = new BlockingCollection<ChunkMutationBatch>(capacity);
            _capacity = capacity;
            _maxGroup = maxGroup;
            _thread = new Thread(Run) { IsBackground = true, Name = "VoxelRush.JournalWriter" };
            _thread.Start();
        }

        public string Path { get; }

        /// <summary>Enqueued but not yet durable.</summary>
        public long Pending => Interlocked.Read(ref _pending);

        /// <summary>Set when the writer failed; the world must stop acknowledging edits.</summary>
        public Exception? Failure => _failure;

        public long DurableCount { get; private set; }

        /// <summary>Room for at least one more batch and no write failure. Only the authority enqueues, so this holds until it does.</summary>
        public bool CanAccept => CanAcceptMany(1);

        public bool CanAcceptMany(int count) => _failure == null && !_queue.IsAddingCompleted && _queue.Count + count <= _capacity;

        void IMutationSink.Accept(ChunkMutationBatch batch)
        {
            if (!TryEnqueue(batch))
            {
                throw new InvalidOperationException("Journal refused a batch after CanAccept: the single-producer invariant was broken.");
            }
        }

        /// <summary>Non-blocking. False means backpressure or failure: refuse the edit instead of acknowledging it.</summary>
        public bool TryEnqueue(ChunkMutationBatch batch)
        {
            if (_failure != null || _queue.IsAddingCompleted)
            {
                return false;
            }

            Interlocked.Increment(ref _pending);
            if (_queue.TryAdd(batch))
            {
                return true;
            }

            Interlocked.Decrement(ref _pending);
            return false;
        }

        /// <summary>Main thread: batches that are now on disk, in append order. Acknowledge these to clients.</summary>
        public int DrainDurable(List<ChunkMutationBatch> into)
        {
            into.Clear();
            while (_durable.TryDequeue(out ChunkMutationBatch? batch))
            {
                into.Add(batch);
            }

            DurableCount += into.Count;
            return into.Count;
        }

        /// <summary>Graceful shutdown: drain, flush, close (SAVE_GAME Shutdown).</summary>
        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join();
            _file.Dispose();
            _queue.Dispose();
        }

        private void Run()
        {
            var group = new List<ChunkMutationBatch>(_maxGroup);
            try
            {
                foreach (ChunkMutationBatch first in _queue.GetConsumingEnumerable())
                {
                    group.Clear();
                    group.Add(first);
                    while (group.Count < _maxGroup && _queue.TryTake(out ChunkMutationBatch? next))
                    {
                        group.Add(next);
                    }

                    foreach (ChunkMutationBatch batch in group)
                    {
                        byte[] record = JournalCodec.Encode(batch);
                        _file.Write(record, 0, record.Length);
                    }

                    // One fsync per group (group commit); nothing is reported durable before it returns.
                    if (_file is FileStream fs)
                    {
                        fs.Flush(flushToDisk: true);
                    }
                    else
                    {
                        _file.Flush();
                    }
                    foreach (ChunkMutationBatch batch in group)
                    {
                        _durable.Enqueue(batch);
                        Interlocked.Decrement(ref _pending);
                    }
                }
            }
            catch (Exception e)
            {
                _failure = e;
            }
        }
    }
}
