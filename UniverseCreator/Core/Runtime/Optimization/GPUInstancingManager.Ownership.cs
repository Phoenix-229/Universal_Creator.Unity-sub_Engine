// UC-T06-12: unpublished replacement records never enter the camera submission dictionary.
using System;
using System.Collections.Generic;

namespace UniverseCreator.Optimization
{
    public partial class GPUInstancingManager
    {
        private readonly Dictionary<int, OwnerReplacement> replacements = new Dictionary<int, OwnerReplacement>();
        private readonly HashSet<int> suspendedOwners = new HashSet<int>();

        internal sealed class OwnerReplacement : IDisposable
        {
            private readonly GPUInstancingManager manager;
            internal readonly int owner;
            internal readonly Dictionary<BatchKey, InstanceBatch> staged = new Dictionary<BatchKey, InstanceBatch>();
            internal readonly List<BatchKey> keys = new List<BatchKey>();
            private bool finished;

            internal OwnerReplacement(GPUInstancingManager manager, int owner)
            { this.manager = manager; this.owner = owner; }

            internal void PrepareCommit()
            {
                if (finished) throw new InvalidOperationException("GPU replacement already retired.");
                // Reserve dictionary capacity before withdrawing accepted records.
                manager.batches.EnsureCapacity(manager.batches.Count + staged.Count);
                manager.byOwner.EnsureCapacity(manager.byOwner.Count + 1);
            }

            internal void Commit()
            {
                manager.ClearOwner(owner);
                foreach (var pair in staged) manager.batches.Add(pair.Key, pair.Value);
                if (keys.Count > 0) manager.byOwner.Add(owner, keys);
                manager.replacements.Remove(owner); finished = true;
            }

            /// <summary>Abandons unpublished renderer records; committed and neighbouring owners remain unchanged.</summary>
            public void Dispose()
            {
                if (finished) return;
                manager.replacements.Remove(owner); staged.Clear(); keys.Clear(); finished = true;
            }
        }

        internal OwnerReplacement BeginOwnerReplacement(int owner)
        {
            if (replacements.ContainsKey(owner)) throw new InvalidOperationException("GPU owner already has a staged replacement.");
            var replacement = new OwnerReplacement(this, owner);
            replacements.Add(owner, replacement); return replacement;
        }

        // Parking retains borrowed meshes/materials and resident records, but submits neither colour nor shadows.
        internal void SetOwnerLive(int owner, bool live)
        { if (live) suspendedOwners.Remove(owner); else suspendedOwners.Add(owner); }
    }
}
