using System;
using System.Collections.Generic;
using VoxelRush.World.Chunks;

namespace VoxelRush.World.Editing
{
    public enum EditAction : byte
    {
        Place = 0,
        Remove = 1,
        /// <summary>Reserved by CONTRACTS; rejected until blocks carry orientation.</summary>
        Rotate = 2,
    }

    /// <summary>Why an edit is refused (GAMEPLAY_RULES Building). Stable codes for logs, wire and UI.</summary>
    public enum EditRejection : byte
    {
        None = 0,
        OutOfReach,
        ProtectedZone,
        Occupied,
        NothingToRemove,
        OverlapsPlayer,
        ChunkNotLoaded,
        RateLimited,
        InvalidBlockType,
        StaleRevision,
        InvalidRevision,
        InvalidIndex,
        UnsupportedAction,
        ServerBusy,
        PersistenceBackpressure,
        NotPermitted,
        RaceLocked,
    }

    /// <summary>Axis-aligned box in world block units (min inclusive, max exclusive).</summary>
    public readonly struct WorldBox
    {
        public WorldBox(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
        {
            MinX = minX; MinY = minY; MinZ = minZ;
            MaxX = maxX; MaxY = maxY; MaxZ = maxZ;
        }

        public float MinX { get; }
        public float MinY { get; }
        public float MinZ { get; }
        public float MaxX { get; }
        public float MaxY { get; }
        public float MaxZ { get; }

        public bool Overlaps(BlockPos block) =>
            block.X + 1 > MinX && block.X < MaxX && block.Y + 1 > MinY && block.Y < MaxY && block.Z + 1 > MinZ && block.Z < MaxZ;
    }

    /// <summary>What the validator needs to know about the world; implemented over chunk storage.</summary>
    public interface IEditWorld
    {
        bool IsLoaded(BlockPos block);

        byte GetBlock(BlockPos block);
    }

    /// <summary>
    /// The single source of edit rules (GAMEPLAY_RULES Building): the client uses it to explain invalid placements,
    /// the authoritative server (VR-031) to enforce them. Order of checks is fixed so both sides report the same reason.
    /// </summary>
    public sealed class EditValidator
    {
        public const float DefaultReach = 6f;

        private readonly ZoneMap _zones;

        /// <summary>Only protected boxes; everyone else may build (public test sandbox).</summary>
        public EditValidator(IReadOnlyList<WorldBox> protectedZones, float reach = DefaultReach)
            : this(ZoneMap.ProtectedOnly(protectedZones ?? Array.Empty<WorldBox>()), reach)
        {
        }

        public EditValidator(ZoneMap zones, float reach = DefaultReach)
        {
            _zones = zones;
            Reach = reach;
        }

        public ZoneMap Zones => _zones;

        public float Reach { get; }

        /// <param name="actorEyeX">Eye position of the authenticated actor (server: its server-side pose, never a client claim).</param>
        /// <param name="occupants">Bounding boxes of players/vehicles that a placed block must not trap.</param>
        /// <param name="role">The actor's role in this world (server: from membership, never from the client).</param>
        public EditRejection Validate(
            EditAction action, BlockPos target, byte blockType,
            float actorEyeX, float actorEyeY, float actorEyeZ,
            IEditWorld world, IReadOnlyList<WorldBox> occupants, WorldRole role = WorldRole.Owner)
        {
            if (action == EditAction.Place && blockType == BlockIds.Air)
            {
                return EditRejection.InvalidBlockType;
            }

            if (DistanceToBlock(target, actorEyeX, actorEyeY, actorEyeZ) > Reach)
            {
                return EditRejection.OutOfReach;
            }

            EditRejection zone = _zones.Authorize(target, role, action == EditAction.Place ? ZoneAction.Place : ZoneAction.Remove);
            if (zone != EditRejection.None)
            {
                return zone;
            }

            if (!world.IsLoaded(target))
            {
                return EditRejection.ChunkNotLoaded;
            }

            byte current = world.GetBlock(target);
            if (action == EditAction.Place)
            {
                if (current != BlockIds.Air)
                {
                    return EditRejection.Occupied;
                }

                foreach (WorldBox occupant in occupants)
                {
                    if (occupant.Overlaps(target))
                    {
                        return EditRejection.OverlapsPlayer;
                    }
                }
            }
            else if (current == BlockIds.Air)
            {
                return EditRejection.NothingToRemove;
            }

            return EditRejection.None;
        }

        /// <summary>Distance from the eye to the nearest point of the target block.</summary>
        public static float DistanceToBlock(BlockPos block, float x, float y, float z)
        {
            float dx = Math.Max(Math.Max(block.X - x, 0), x - (block.X + 1));
            float dy = Math.Max(Math.Max(block.Y - y, 0), y - (block.Y + 1));
            float dz = Math.Max(Math.Max(block.Z - z, 0), z - (block.Z + 1));
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public static string Describe(EditRejection rejection) => rejection switch
        {
            EditRejection.None => "Place block",
            EditRejection.OutOfReach => "Too far away. Move closer",
            EditRejection.ProtectedZone => "Protected area: building isn't allowed here",
            EditRejection.Occupied => "That space is already filled",
            EditRejection.NothingToRemove => "Nothing to remove there",
            EditRejection.OverlapsPlayer => "A player or vehicle is in the way",
            EditRejection.ChunkNotLoaded => "Area still loading",
            EditRejection.RateLimited => "Building too fast. Wait a moment",
            EditRejection.InvalidBlockType => "Pick a block to place",
            EditRejection.StaleRevision => "Someone changed that block first",
            EditRejection.InvalidRevision => "Out of sync. Reloading area",
            EditRejection.InvalidIndex => "Invalid target",
            EditRejection.UnsupportedAction => "Not available yet",
            EditRejection.ServerBusy => "Server busy. Try again",
            EditRejection.PersistenceBackpressure => "Saving is catching up. Try again",
            EditRejection.NotPermitted => "You don't have permission to build here",
            EditRejection.RaceLocked => "The track is locked during the race",
            _ => throw new ArgumentOutOfRangeException(nameof(rejection)),
        };

        public static string ReasonCode(EditRejection rejection) => rejection switch
        {
            EditRejection.None => "none",
            EditRejection.OutOfReach => "out_of_reach",
            EditRejection.ProtectedZone => "protected_zone",
            EditRejection.Occupied => "occupied",
            EditRejection.NothingToRemove => "nothing_to_remove",
            EditRejection.OverlapsPlayer => "overlaps_player",
            EditRejection.ChunkNotLoaded => "chunk_not_loaded",
            EditRejection.RateLimited => "rate_limited",
            EditRejection.InvalidBlockType => "invalid_block_type",
            EditRejection.StaleRevision => "stale_revision",
            EditRejection.InvalidRevision => "invalid_revision",
            EditRejection.InvalidIndex => "invalid_index",
            EditRejection.UnsupportedAction => "unsupported_action",
            EditRejection.ServerBusy => "server_busy",
            EditRejection.PersistenceBackpressure => "persistence_backpressure",
            EditRejection.NotPermitted => "not_permitted",
            EditRejection.RaceLocked => "race_locked",
            _ => throw new ArgumentOutOfRangeException(nameof(rejection)),
        };
    }
}
