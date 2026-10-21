using System;
using Unity.Netcode;
using UnityEngine;
using VoxelRush.Core.Diagnostics;
using VoxelRush.Networking.Vehicles;
using VoxelRush.Networking.World;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;

namespace VoxelRush.Networking.Bots
{
    /// <summary>
    /// Headless builder (<c>-botBuild N</c>): once the world is synced, places then removes blocks behind its own car,
    /// one edit at a time (waits for each durable result), N edits in total. <c>-botTower H</c> instead stacks an
    /// H-block tower behind the car and then removes its bottom block, so the server must collapse the rest.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BotBuilder : MonoBehaviour
    {
        public const string BuildArgument = "-botBuild";
        public const string TowerArgument = "-botTower";

        private int _target;
        private int _done;
        private int _accepted;
        private bool _waiting;
        private float _nextAt;
        private int _tower;
        private BlockPos? _towerBase;

        public static WorldSyncClient? World { get; set; }

        public bool Finished => _target > 0 && _done >= _target;

        private void Awake()
        {
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, BuildArgument);
            if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int n))
            {
                _target = Mathf.Clamp(n, 0, 1000);
            }

            int t = Array.IndexOf(args, TowerArgument);
            if (t >= 0 && t + 1 < args.Length && int.TryParse(args[t + 1], out int h))
            {
                _tower = Mathf.Clamp(h, 2, 5);
                _target = _tower + 1;
            }
        }

        private void Update()
        {
            if (_target == 0 || Finished || _waiting || World == null || !World.IsSynced || Time.realtimeSinceStartup < _nextAt)
            {
                return;
            }

            VehicleNetworkDriver? car = OwnCar();
            if (car == null)
            {
                return;
            }

            Vector3 p = car.transform.position;
            BlockPos target;
            EditAction action;
            if (_tower > 0)
            {
                // Bottom-up, 4 m behind the car (top block still in reach), then cut the base.
                _towerBase ??= new BlockPos(Mathf.FloorToInt(p.x), 0, Mathf.FloorToInt(p.z) - 4);
                BlockPos b = _towerBase.Value;
                target = _done < _tower ? new BlockPos(b.X, _done, b.Z) : b;
                action = _done < _tower ? EditAction.Place : EditAction.Remove;
            }
            else
            {
                // A 5-block row 5 m behind the car at ground level: in reach, never inside the car.
                int slot = _done % 10;
                target = new BlockPos(Mathf.FloorToInt(p.x) - 2 + slot % 5, 0, Mathf.FloorToInt(p.z) - 5);
                byte current = World.Replica!.Get(ChunkMath.ToChunk(target))?.Get(ChunkMath.ToLocal(target)) ?? BlockIds.Air;
                action = current == BlockIds.Air ? EditAction.Place : EditAction.Remove;
            }

            ChunkCoord chunk = ChunkMath.ToChunk(target);
            _waiting = true;
            World.SendEdit(chunk, ChunkMath.ToLocal(target).Index, action, BlockIds.Stone, (rejection, revision) =>
            {
                _waiting = false;
                _done++;
                _nextAt = Time.realtimeSinceStartup + 0.1f;
                if (rejection == EditRejection.None)
                {
                    _accepted++;
                }
                else
                {
                    Debug.LogWarning(LogEvent.Format("bot_edit_rejected", ("reason", EditValidator.ReasonCode(rejection)), ("target", target)));
                }

                if (Finished)
                {
                    Debug.Log(LogEvent.Format(_tower > 0 ? "bot_tower_done" : "bot_build_done", ("edits", _done), ("accepted", _accepted)));
                }
            });
        }

        private static VehicleNetworkDriver? OwnCar()
        {
            foreach (VehicleNetworkDriver driver in FindObjectsByType<VehicleNetworkDriver>(FindObjectsSortMode.None))
            {
                if (driver.IsOwner)
                {
                    return driver;
                }
            }

            return null;
        }
    }
}
