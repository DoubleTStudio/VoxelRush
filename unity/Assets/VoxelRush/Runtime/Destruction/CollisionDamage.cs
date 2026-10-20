using System;
using System.Collections.Generic;
using VoxelRush.World.Chunks;
using VoxelRush.World.Editing;

namespace VoxelRush.Destruction
{
    /// <summary>DESTRUCTION Sources/Collision: energy = ½·m·v² on the normal component, thresholded, scaled, clamped, quantized.</summary>
    public sealed class CollisionDamageModel
    {
        public CollisionDamageModel(float thresholdJoules = 8000f, float hpPerKiloJoule = 2f, int maxDamagePerImpact = 150)
        {
            if (!(thresholdJoules >= 0) || !(hpPerKiloJoule > 0) || maxDamagePerImpact < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(thresholdJoules));
            }

            ThresholdJoules = thresholdJoules;
            HpPerKiloJoule = hpPerKiloJoule;
            MaxDamagePerImpact = maxDamagePerImpact;
        }

        public float ThresholdJoules { get; }

        public float HpPerKiloJoule { get; }

        public int MaxDamagePerImpact { get; }

        /// <param name="normalSpeed">Server-side relative speed along the contact normal (m/s); sign ignored.</param>
        /// <param name="effectiveMass">Server-side mass taking part (kg); never a client value.</param>
        public int Damage(float normalSpeed, float effectiveMass)
        {
            if (float.IsNaN(normalSpeed) || float.IsNaN(effectiveMass) || effectiveMass <= 0)
            {
                return 0;
            }

            double energy = 0.5 * effectiveMass * normalSpeed * normalSpeed;
            if (energy <= ThresholdJoules)
            {
                return 0;
            }

            double hp = (energy - ThresholdJoules) / 1000.0 * HpPerKiloJoule;
            return (int)Math.Min(MaxDamagePerImpact, Math.Ceiling(hp));
        }
    }

    /// <summary>
    /// Coalesces repeated physics contacts (DESTRUCTION HP: "deduplicated by actor/target/contact window"): the first
    /// contact of an actor with a target opens a window; further contacts with that target inside it are ignored.
    /// Memory is bounded by pruning expired windows.
    /// </summary>
    public sealed class ContactDeduplicator
    {
        private readonly Dictionary<(string Actor, BlockPos Target), double> _windows = new Dictionary<(string, BlockPos), double>();
        private readonly List<(string, BlockPos)> _expired = new List<(string, BlockPos)>();

        public ContactDeduplicator(double windowSeconds = 0.3) => WindowSeconds = windowSeconds;

        public double WindowSeconds { get; }

        public int OpenWindows => _windows.Count;

        /// <summary>True when this contact starts a new impact (damage may apply); false inside an open window.</summary>
        public bool TryBeginImpact(string actor, BlockPos target, double now)
        {
            if (_windows.TryGetValue((actor, target), out double opened) && now - opened < WindowSeconds)
            {
                return false;
            }

            _windows[(actor, target)] = now;
            return true;
        }

        public void Prune(double now)
        {
            _expired.Clear();
            foreach (KeyValuePair<(string, BlockPos), double> window in _windows)
            {
                if (now - window.Value >= WindowSeconds)
                {
                    _expired.Add(window.Key);
                }
            }

            foreach ((string, BlockPos) key in _expired)
            {
                _windows.Remove(key);
            }
        }
    }

    public readonly struct ImpactResult
    {
        public ImpactResult(EditRejection zone, bool deduplicated, int damage, DamageResult damageResult)
        {
            Zone = zone;
            Deduplicated = deduplicated;
            Damage = damage;
            DamageResult = damageResult;
        }

        /// <summary>Zone refusal (Protected, ActiveRace) when not None.</summary>
        public EditRejection Zone { get; }

        public bool Deduplicated { get; }

        public int Damage { get; }

        public DamageResult DamageResult { get; }

        public bool Applied => Zone == EditRejection.None && !Deduplicated && DamageResult.Outcome != DamageOutcome.Ignored;
    }

    /// <summary>Server-side collision damage: model + zones + dedup + sparse HP. Inputs are server physics, never client claims.</summary>
    public sealed class ImpactResolver
    {
        private readonly CollisionDamageModel _model;
        private readonly ContactDeduplicator _dedup;
        private readonly ZoneMap _zones;

        public ImpactResolver(CollisionDamageModel model, ContactDeduplicator dedup, ZoneMap zones)
        {
            _model = model;
            _dedup = dedup;
            _zones = zones;
        }

        public long Impacts { get; private set; }

        public long Duplicates { get; private set; }

        public ImpactResult Resolve(string actor, BlockPos target, float normalSpeed, float effectiveMass, double now, ChunkData chunk, ChunkDamage damage)
        {
            if (!ChunkMath.ToChunk(target).Equals(chunk.Coord))
            {
                throw new ArgumentException($"Block {target} is not in chunk {chunk.Coord}.", nameof(target));
            }

            int amount = _model.Damage(normalSpeed, effectiveMass);
            if (amount == 0)
            {
                // Grazes and resting contact neither damage nor open a window.
                return new ImpactResult(EditRejection.None, false, 0, default);
            }

            EditRejection zone = _zones.Authorize(target, WorldRole.Visitor, ZoneAction.Damage);
            if (zone != EditRejection.None)
            {
                return new ImpactResult(zone, false, amount, default);
            }

            if (!_dedup.TryBeginImpact(actor, target, now))
            {
                Duplicates++;
                return new ImpactResult(EditRejection.None, true, amount, default);
            }

            Impacts++;
            return new ImpactResult(EditRejection.None, false, amount, damage.Apply(chunk, ChunkMath.ToLocal(target), amount));
        }
    }
}
