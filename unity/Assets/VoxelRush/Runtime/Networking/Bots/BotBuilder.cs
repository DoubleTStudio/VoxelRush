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
    /// one edit at a time (waits for each durable result), N edits in total.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BotBuilder : MonoBehaviour
    {
        public const string BuildArgument = "-botBuild";

        private int _target;
        private int _done;
        private int _accepted;
        private bool _waiting;
        private float _nextAt;

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

            // A 5-block row 5 m behind the car at ground level: in reach, never inside the car.
            Vector3 p = car.transform.position;
            int slot = _done % 10;
            var target = new BlockPos(Mathf.FloorToInt(p.x) - 2 + slot % 5, 0, Mathf.FloorToInt(p.z) - 5);
            ChunkCoord chunk = ChunkMath.ToChunk(target);
            byte current = World.Replica!.Get(chunk)?.Get(ChunkMath.ToLocal(target)) ?? BlockIds.Air;
            EditAction action = current == BlockIds.Air ? EditAction.Place : EditAction.Remove;
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
                    Debug.Log(LogEvent.Format("bot_build_done", ("edits", _done), ("accepted", _accepted)));
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
