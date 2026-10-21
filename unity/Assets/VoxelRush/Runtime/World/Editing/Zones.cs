using System;
using System.Collections.Generic;
using VoxelRush.World.Chunks;

namespace VoxelRush.World.Editing
{
    /// <summary>GAMEPLAY_RULES zones, in increasing precedence when they overlap.</summary>
    public enum ZoneKind : byte
    {
        Sandbox = 0,
        RacePreparation = 1,
        ActiveRace = 2,
        /// <summary>Inside an active race: blocks the race pre-tagged as destructible (overlay only, never persisted).</summary>
        RaceDestructible = 3,
        Protected = 4,
    }

    /// <summary>SAVE_GAME WorldMember.role.</summary>
    public enum WorldRole : byte
    {
        Visitor = 0,
        Editor = 1,
        Owner = 2,
    }

    public enum ZoneAction : byte
    {
        Place,
        Remove,
        /// <summary>Damage from collisions/explosions/mining (DES-003/004).</summary>
        Damage,
    }

    public readonly struct Zone
    {
        public Zone(WorldBox box, ZoneKind kind)
        {
            Box = box;
            Kind = kind;
        }

        public WorldBox Box { get; }

        public ZoneKind Kind { get; }
    }

    /// <summary>
    /// Zone layout plus the authorization table (GAMEPLAY_RULES Zones, DESTRUCTION Policy). Overlaps resolve to the
    /// highest-precedence kind, so a protected structure inside a sandbox stays protected.
    /// </summary>
    public sealed class ZoneMap
    {
        private readonly IReadOnlyList<Zone> _zones;

        public ZoneMap(IReadOnlyList<Zone> zones, ZoneKind defaultKind = ZoneKind.Sandbox, bool visitorsCanBuild = false)
        {
            _zones = zones ?? Array.Empty<Zone>();
            DefaultKind = defaultKind;
            VisitorsCanBuild = visitorsCanBuild;
        }

        public static ZoneMap Empty { get; } = new ZoneMap(Array.Empty<Zone>(), visitorsCanBuild: true);

        public ZoneKind DefaultKind { get; }

        /// <summary>Public sandboxes may let visitors build; private worlds don't (owner controls permissions).</summary>
        public bool VisitorsCanBuild { get; }

        public static ZoneMap ProtectedOnly(IReadOnlyList<WorldBox> protectedBoxes, bool visitorsCanBuild = true)
        {
            var zones = new List<Zone>(protectedBoxes.Count);
            foreach (WorldBox box in protectedBoxes)
            {
                zones.Add(new Zone(box, ZoneKind.Protected));
            }

            return new ZoneMap(zones, ZoneKind.Sandbox, visitorsCanBuild);
        }

        public ZoneKind KindAt(BlockPos block)
        {
            ZoneKind kind = DefaultKind;
            foreach (Zone zone in _zones)
            {
                if (zone.Kind > kind && zone.Box.Overlaps(block))
                {
                    kind = zone.Kind;
                }
            }

            return kind;
        }

        public EditRejection Authorize(BlockPos block, WorldRole role, ZoneAction action)
        {
            ZoneKind kind = KindAt(block);
            switch (kind)
            {
                case ZoneKind.Protected:
                    return EditRejection.ProtectedZone;

                case ZoneKind.ActiveRace:
                    return EditRejection.RaceLocked;

                case ZoneKind.RaceDestructible:
                    // Only race damage touches the overlay; nobody builds on a live track.
                    return action == ZoneAction.Damage ? EditRejection.None : EditRejection.RaceLocked;

                case ZoneKind.RacePreparation:
                    return action == ZoneAction.Damage || role >= WorldRole.Editor ? EditRejection.None : EditRejection.NotPermitted;

                default:
                    return action == ZoneAction.Damage || role >= WorldRole.Editor || VisitorsCanBuild ? EditRejection.None : EditRejection.NotPermitted;
            }
        }
    }
}
