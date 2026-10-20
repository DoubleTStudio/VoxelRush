using System;
using System.Collections.Generic;
using VoxelRush.World.Chunks;

namespace VoxelRush.World.Editing
{
    /// <summary>CONTRACTS EditIntent: what a client asks for. Untrusted until validated by <see cref="WorldAuthority"/>.</summary>
    public readonly struct EditIntent
    {
        public EditIntent(Guid operationId, ChunkCoord chunk, int localIndex, ulong expectedRevision, EditAction action, byte blockType, byte orientation = 0)
        {
            OperationId = operationId;
            Chunk = chunk;
            LocalIndex = localIndex;
            ExpectedRevision = expectedRevision;
            Action = action;
            BlockType = blockType;
            Orientation = orientation;
        }

        public Guid OperationId { get; }

        public ChunkCoord Chunk { get; }

        /// <summary>uint16 on the wire; bounds-checked against [0, 4095].</summary>
        public int LocalIndex { get; }

        /// <summary>The chunk revision the client saw when it aimed.</summary>
        public ulong ExpectedRevision { get; }

        public EditAction Action { get; }

        public byte BlockType { get; }

        public byte Orientation { get; }
    }

    /// <summary>CONTRACTS ChunkMutationBatch: an accepted operation's changes to one chunk, ordered by revision.</summary>
    public sealed class ChunkMutationBatch
    {
        public ChunkMutationBatch(Guid operationId, string actorId, ChunkCoord chunk, ulong baseRevision, ulong newRevision, uint serverTick, IReadOnlyList<BlockChange> changes)
        {
            OperationId = operationId;
            ActorId = actorId;
            Chunk = chunk;
            BaseRevision = baseRevision;
            NewRevision = newRevision;
            ServerTick = serverTick;
            Changes = changes;
        }

        public Guid OperationId { get; }

        public string ActorId { get; }

        public ChunkCoord Chunk { get; }

        public ulong BaseRevision { get; }

        public ulong NewRevision { get; }

        public uint ServerTick { get; }

        public IReadOnlyList<BlockChange> Changes { get; }
    }

    public readonly struct BlockChange
    {
        public BlockChange(ushort index, byte blockType)
        {
            Index = index;
            BlockType = blockType;
        }

        public ushort Index { get; }

        public byte BlockType { get; }
    }

    public readonly struct EditResult
    {
        private EditResult(EditRejection rejection, ChunkMutationBatch? batch, bool duplicate)
        {
            Rejection = rejection;
            Batch = batch;
            IsDuplicate = duplicate;
        }

        public EditRejection Rejection { get; }

        public bool Accepted => Rejection == EditRejection.None;

        /// <summary>The applied mutation (for publishing and, from VR-032, journaling) when accepted.</summary>
        public ChunkMutationBatch? Batch { get; }

        /// <summary>The operation id was already processed; this is the original outcome, nothing applied twice.</summary>
        public bool IsDuplicate { get; }

        internal static EditResult Accept(ChunkMutationBatch batch) => new EditResult(EditRejection.None, batch, false);

        internal static EditResult Reject(EditRejection rejection) => new EditResult(rejection, null, false);

        internal EditResult AsDuplicate() => new EditResult(Rejection, Batch, true);
    }
}
