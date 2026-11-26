using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace UniverseCreator.Nodes
{
    /// <summary>UC-T02-06: world-owned byte admission for managed graph payloads and explicitly reserved native allocations.</summary>
    /// <remarks>All operations are thread-safe. Byte figures are payload estimates, not GC heap or graphics-driver measurements.
    /// Pinned consumers are never evicted; an over-budget live set fails explicitly. Imported native objects remain borrowed.</remarks>
    public sealed class ProductMemoryBudget
    {
        internal sealed class Entry
        {
            internal volatile object Product;
            internal long Managed, Native, Used;
            internal int Pins, TransientRetainers;
            internal bool Retained;
            internal string Key;
        }

        internal sealed class Identity : IEqualityComparer<object>
        {
            internal static readonly Identity Instance = new Identity();
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }

        /// <summary>A consumer pin; dispose once the owner has dropped every reference it protects.</summary>
        public sealed class Lease : IDisposable
        {
            private ProductMemoryBudget owner;
            private List<Lease> dependencies;
            internal readonly Entry Entry;
            internal Lease(ProductMemoryBudget owner, Entry entry) { this.owner = owner; Entry = entry; }
            /// <summary>UC repair 30: transfers a shared-product pin to this unpublished, exclusively owned reservation.</summary>
            /// <param name="dependency">Non-null pin; its caller relinquishes ownership after this call succeeds.</param>
            /// <exception cref="ObjectDisposedException">This reservation has already retired.</exception>
            /// <remarks>Attach on the constructing thread before publishing the reservation to another owner.</remarks>
            internal void Own(Lease dependency)
            {
                if (dependency == null) throw new ArgumentNullException(nameof(dependency));
                if (owner == null) throw new ObjectDisposedException(nameof(Lease));
                if (dependencies == null) dependencies = new List<Lease>();
                dependencies.Add(dependency);
            }
            /// <summary>Releases this pin and its transferred shared-product pins exactly once; retained payloads become eligible for eviction.</summary>
            public void Dispose()
            {
                ProductMemoryBudget current = Interlocked.Exchange(ref owner, null);
                if (current == null) return;
                if (dependencies != null) foreach (Lease dependency in dependencies) dependency.Dispose();
                dependencies = null;
                current.Release(Entry);
            }
        }

        private readonly object gate = new object();
        private readonly Dictionary<object, Entry> entries = new Dictionary<object, Entry>(Identity.Instance);
        private long managed, native, peak, clock, evictions;
        private long limit;
        /// <summary>Maximum admitted managed plus native payload bytes. UC fix: follows the world's authored MiB setting live (see <see cref="Resize"/>).</summary>
        public long LimitBytes { get { return Interlocked.Read(ref limit); } }

        /// <summary>UC fix: applies a new limit to a live budget. Pinned consumers are never evicted; a limit below the resident set only refuses new admissions until space frees.</summary>
        /// <param name="newLimitBytes">Positive byte limit.</param>
        public void Resize(long newLimitBytes)
        {
            if (newLimitBytes <= 0) throw new ArgumentOutOfRangeException(nameof(newLimitBytes));
            lock (gate) Interlocked.Exchange(ref limit, newLimitBytes);
        }
        /// <summary>Currently admitted managed payload estimate, including retained and pinned products.</summary>
        public long ManagedBytes { get { lock (gate) return managed; } }
        /// <summary>Explicit native reservations; excludes imported assets and untracked engine/driver overhead.</summary>
        public long NativeBytes { get { lock (gate) return native; } }
        /// <summary>Maximum total admitted bytes since construction.</summary>
        public long PeakBytes { get { lock (gate) return peak; } }
        /// <summary>Number of eligible payloads evicted to admit new work.</summary>
        public long Evictions { get { lock (gate) return evictions; } }
        /// <summary>Bytes currently protected by one or more consumers; each shared product is counted once.</summary>
        public long PinnedBytes
        {
            get { lock (gate) { long bytes = 0; foreach (Entry entry in entries.Values) if (entry.Pins > 0) bytes += entry.Managed + entry.Native; return bytes; } }
        }

        // UC repair 30: inspect admission owners without traversing payloads or allocating in the generation/render loop.
        internal string DescribeLiveSet()
        {
            lock (gate)
            {
                var largest = new List<Entry>();
                long pinned = 0;
                foreach (Entry entry in entries.Values)
                {
                    if (entry.Pins > 0) pinned += entry.Managed + entry.Native;
                    int slot = 0;
                    while (slot < largest.Count && largest[slot].Managed + largest[slot].Native >= entry.Managed + entry.Native) slot++;
                    if (slot >= 8) continue;
                    largest.Insert(slot, entry);
                    if (largest.Count > 8) largest.RemoveAt(8);
                }
                var report = new System.Text.StringBuilder();
                report.Append("entries=").Append(entries.Count).Append(" pinned=").Append(pinned)
                    .Append(" eligible=").Append(managed + native - pinned);
                foreach (Entry entry in largest)
                    report.Append("\n  ").Append(entry.Product.GetType().Name).Append(" key=").Append(entry.Key)
                        .Append(" managed=").Append(entry.Managed).Append(" native=").Append(entry.Native)
                        .Append(" pins=").Append(entry.Pins).Append(" retained=").Append(entry.Retained);
                return report.ToString();
            }
        }

        /// <summary>Creates an empty budget with a positive byte limit.</summary>
        /// <param name="limitBytes">Total admitted payload limit in bytes, greater than zero.</param>
        /// <exception cref="ArgumentOutOfRangeException">The limit is not positive.</exception>
        public ProductMemoryBudget(long limitBytes)
        {
            if (limitBytes <= 0) throw new ArgumentOutOfRangeException(nameof(limitBytes));
            limit = limitBytes;
        }

        /// <summary>Reserves bytes before an indivisible allocation; no product or native object is created or destroyed here.</summary>
        /// <param name="managedBytes">Nonnegative managed payload bytes.</param>
        /// <param name="nativeBytes">Nonnegative native allocation estimate.</param>
        /// <returns>Pin to dispose after the allocation is released.</returns>
        /// <exception cref="ArgumentOutOfRangeException">A byte count is negative or overflows.</exception>
        /// <exception cref="InvalidOperationException">Pinned demand cannot fit after eligible eviction.</exception>
        public Lease Reserve(long managedBytes, long nativeBytes = 0) => Acquire(new object(), managedBytes, nativeBytes, string.Empty);

        internal Lease Acquire(object product, long managedBytes, long nativeBytes, string key)
        {
            if (product == null) throw new ArgumentNullException(nameof(product));
            if (managedBytes < 0 || nativeBytes < 0 || managedBytes > long.MaxValue - nativeBytes)
                throw new ArgumentOutOfRangeException(nameof(managedBytes));
            lock (gate)
            {
                if (!entries.TryGetValue(product, out Entry entry))
                {
                    MakeRoom(managedBytes + nativeBytes);
                    entry = new Entry { Product = product, Managed = managedBytes, Native = nativeBytes, Key = key ?? string.Empty };
                    entries.Add(product, entry);
                    managed += managedBytes; native += nativeBytes;
                    peak = Math.Max(peak, managed + native);
                }
                entry.Pins++; entry.Used = ++clock;
                return new Lease(this, entry);
            }
        }

        // UC-T07-02: transfer a sole allocation reservation into a published image without a double charge or admission gap.
        internal void BindReservedProduct(Lease lease, object product)
        {
            if (lease == null || product == null) throw new ArgumentNullException();
            lock (gate)
            {
                Entry entry = lease.Entry;
                if (entry.Product == null || entry.Pins != 1 || entry.Retained || entry.TransientRetainers != 0 ||
                    !entries.TryGetValue(entry.Product, out Entry registered) || !ReferenceEquals(entry, registered) ||
                    entries.ContainsKey(product))
                    throw new InvalidOperationException("UC-T07-02: allocation reservation is not exclusively owned by this budget.");
                entries.Remove(entry.Product);
                entry.Product = product;
                entries.Add(product, entry);
            }
        }

        internal Lease TryAcquire(Entry entry)
        {
            lock (gate)
            {
                if (entry == null || entry.Product == null) return null;
                entry.Pins++; entry.Used = ++clock;
                return new Lease(this, entry);
            }
        }

        internal void Retain(Entry entry)
        {
            lock (gate) if (entry != null && entry.Product != null) entry.Retained = true;
        }

        // UC repair 37: temporary window reuse is independently owned and remains evictable between consumers.
        internal void RetainTransient(Entry entry)
        {
            lock (gate) entry.TransientRetainers++;
        }

        internal void ForgetTransient(Entry entry)
        {
            lock (gate)
            {
                if (entry.Product != null && --entry.TransientRetainers == 0 && entry.Pins == 0 && !entry.Retained)
                    Remove(entry);
            }
        }

        internal void Forget(Entry entry)
        {
            lock (gate)
            {
                if (entry == null || entry.Product == null) return;
                entry.Retained = false;
                if (entry.Pins == 0 && entry.TransientRetainers == 0) Remove(entry);
            }
        }

        /// <summary>Withdraws cross-bake retention; live consumers keep their pins until retirement.</summary>
        public void ClearRetained()
        {
            lock (gate)
            {
                var remove = new List<Entry>();
                foreach (Entry entry in entries.Values)
                {
                    entry.Retained = false;
                    if (entry.Pins == 0 && entry.TransientRetainers == 0) remove.Add(entry);
                }
                foreach (Entry entry in remove) Remove(entry);
            }
        }

        private void Release(Entry entry)
        {
            lock (gate)
            {
                if (--entry.Pins == 0 && !entry.Retained && entry.TransientRetainers == 0) Remove(entry);
            }
        }

        private void MakeRoom(long bytes)
        {
            if (bytes > LimitBytes)
                throw new InvalidOperationException("UC-T02-06: one payload requests " + bytes + " bytes above the " + LimitBytes + " byte budget.");
            // Evict temporary window results before cross-bake results. Within each group use LRU, then ordinal key.
            while (bytes > LimitBytes - managed - native)
            {
                Entry oldest = null;
                foreach (Entry entry in entries.Values)
                    if (entry.Pins == 0 && (oldest == null || (entry.Retained != oldest.Retained
                        ? !entry.Retained
                        : entry.Used < oldest.Used ||
                            (entry.Used == oldest.Used && string.CompareOrdinal(entry.Key, oldest.Key) < 0)))) oldest = entry;
                if (oldest == null)
                {
                    // UC repair 30: admission never sleeps; queue work through the existing scheduler instead.
                    throw new InvalidOperationException("UC-T02-06: product memory budget exhausted; requested " + bytes +
                        " bytes, resident " + (managed + native) + ", limit " + LimitBytes + ". Active consumers remain protected.");
                }
                Remove(oldest); evictions++;
            }
        }

        private void Remove(Entry entry)
        {
            entries.Remove(entry.Product);
            managed -= entry.Managed; native -= entry.Native;
            // Retained dictionaries hold only this token; clearing it really releases the budget's product reference.
            entry.Product = null;
        }
    }
}
