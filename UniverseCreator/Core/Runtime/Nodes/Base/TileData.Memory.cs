using System;
using System.Collections.Generic;

namespace UniverseCreator.Nodes
{
    public partial class TileData
    {
        // UC-T02-06: each evaluation owns its pins. Child halo/world views retire with their parent.
        private ProductMemoryBudget memoryBudget;
        private readonly Dictionary<object, ProductMemoryBudget.Lease> memoryPins =
            new Dictionary<object, ProductMemoryBudget.Lease>(ProductMemoryBudget.Identity.Instance);
        private List<TileData> memoryViews;
        private readonly List<IDisposable> graphInputPins = new List<IDisposable>(); // UC-T05-06: typed consumer leases retire after their last worker/Apply.
        internal bool ownsDrainageMemory;

        internal void AttachMemory(ProductMemoryBudget budget) => memoryBudget = budget;
        internal ProductMemoryBudget.Lease ReserveProductMemory(long managedBytes) => memoryBudget?.Reserve(managedBytes);
        internal void AttachMemoryView(TileData view)
        {
            view.publicationParent = this; // UC-T02-07: scene publications follow coarse/halo evaluation back to the owning tile.
            view.AttachMemory(memoryBudget);
            if (memoryViews == null) memoryViews = new List<TileData>();
            memoryViews.Add(view);
        }

        /// <summary>UC repair 29: retires a completed temporary evaluator while preserving its propagated channel metadata.</summary>
        /// <param name="view">An attached view owned by this tile; null or an already retired view is ignored.</param>
        /// <remarks>Owner-thread only, after evaluation has unwound. The caller must pin any returned product on this tile first.
        /// Channel publications are admitted before releasing view pins. An admission failure keeps the view attached for final retirement.</remarks>
        /// <exception cref="InvalidOperationException">Surviving channel metadata cannot fit the world's hard budget.</exception>
        internal void RetireMemoryView(TileData view)
        {
            if (view == null || memoryViews == null || !memoryViews.Contains(view)) return;
            foreach (ChannelGraphPublication publication in view.ChannelPublications())
                TrackProductMemory(publication, publication.Key);
            view.ReleaseProductMemory();
            memoryViews.Remove(view);
        }

        internal void TrackProductMemory(object product, string key = "outputs")
        {
            if (memoryBudget == null || product == null || memoryPins.ContainsKey(product)) return;
            memoryPins.Add(product, memoryBudget.Acquire(product, ProductMemoryFootprint.Estimate(product), 0, key));
        }

        // UC-T05-06: accepted query indexes already know their admitted size; avoid walking them again for each region.
        internal void TrackProductMemory(object product, string key, long bytes)
        {
            if (memoryBudget == null || product == null || memoryPins.ContainsKey(product)) return;
            memoryPins.Add(product, memoryBudget.Acquire(product, bytes, 0, key));
        }

        /// <summary>UC-T07-02: captures immutable CPU pixels after reserving their declared managed bytes.</summary>
        /// <param name="descriptor">Validated dimensions/float channel/encoding/sampling contract.</param>
        /// <param name="pixels">Exactly Width×Height finite RGBA samples; caller retains its independent input array.</param>
        /// <param name="stop">Optional cancellation; returns null and releases the allocation reservation when stopped.</param>
        /// <returns>New immutable image pinned by this tile; consumers must retain their own lifetime pins.</returns>
        /// <exception cref="ArgumentException">Metadata, dimensions or semantic samples are invalid.</exception>
        /// <exception cref="InvalidOperationException">The world's admitted live set cannot fit this allocation.</exception>
        public TextureImageProduct CreateTextureImage(TextureImageDescriptor descriptor, UnityEngine.Color[] pixels, StopToken stop = null)
        {
            descriptor.Validate();
            if (stop != null && stop.StopRequested) return null; // UC-T07-04: cancelled requests never attempt admission.
            ProductMemoryBudget.Lease reservation = ReserveProductMemory(descriptor.ManagedBytes);
            try
            {
                TextureImageProduct image = TextureImageProduct.Capture(descriptor, pixels, stop);
                if (image == null) return null;
                if (reservation != null)
                {
                    memoryBudget.BindReservedProduct(reservation, image);
                    memoryPins.Add(image, reservation);
                    reservation = null;
                }
                return image;
            }
            finally { reservation?.Dispose(); }
        }

        /// <summary>UC-T07-02: builds one immutable image directly into reserved storage, without a second full-image copy.</summary>
        /// <param name="descriptor">Validated float RGBA image contract; dimensions 1…2048 pixels.</param>
        /// <param name="sample">Callback returning a finite semantic pixel for each row-major index; must not retain mutable output storage.</param>
        /// <param name="stop">Optional cooperative cancellation; a cancelled image is not published.</param>
        /// <returns>Tile-pinned immutable image, or null on cancellation.</returns>
        /// <exception cref="ArgumentException">Metadata or a sample is invalid.</exception>
        /// <exception cref="InvalidOperationException">Admitted image storage exceeds the memory budget.</exception>
        public TextureImageProduct GenerateTextureImage(TextureImageDescriptor descriptor, Func<int, UnityEngine.Color> sample, StopToken stop = null)
        {
            descriptor.Validate();
            if (stop != null && stop.StopRequested) return null; // UC-T07-04: cancelled requests never attempt admission.
            ProductMemoryBudget.Lease reservation = ReserveProductMemory(descriptor.ManagedBytes);
            try
            {
                TextureImageProduct image = TextureImageProduct.Build(descriptor, sample, stop);
                if (image == null) return null;
                if (reservation != null)
                {
                    memoryBudget.BindReservedProduct(reservation, image);
                    memoryPins.Add(image, reservation); reservation = null;
                }
                return image;
            }
            finally { reservation?.Dispose(); }
        }

        internal void BindWorldGraphInputs(Graph main, TerrainSystem.RiverChannelSnapshot ground,
            TerrainSystem.WorldGraphWaterInput water, TerrainSystem.WorldGraphEnvironmentInput environment)
        {
            if (ground == null || !ground.Run.Equals(RunId) || main == null || !main.HasSupportedDomainMetadata || main.Domain != UCGraphDomain.TerrainWater)
                throw new InvalidOperationException("Accepted input world/revision or main-domain metadata does not match this tile."); // UC-T05-06: reject foreign payloads at the binding boundary.
            var pin = new GraphProductHandle<TerrainSystem.RiverChannelSnapshot>(ground, RunId, UCGraphDomain.TerrainWater,
                ground.GroundVersion, ground.Bounds, GraphProductUnits.WorldMetres, memoryBudget, ProductMemoryFootprint.Estimate(ground));
            graphInputPins.Add(pin);
            var regionEnvironment = environment?.ForRegion(ground.Bounds); // UC-T05-06: resolve spatial climate ownership once before the pixel loop.
            worldGraphGround = new WorldGraphGround(main, ground, water, regionEnvironment);
            water?.Pin(this); regionEnvironment?.Pin(this);
        }

        internal void OwnGraphInput(IDisposable pin) { if (pin != null) graphInputPins.Add(pin); }

        internal bool TryRetainedProduct(RetainedProduct retainedProduct, out object product)
        {
            product = null;
            if (retainedProduct.memoryEntry == null)
            {
                product = retainedProduct.product;
                return product != null;
            }
            if (!ReferenceEquals(memoryBudget, retainedProduct.memoryOwner)) return false;
            ProductMemoryBudget.Lease pin = memoryBudget.TryAcquire(retainedProduct.memoryEntry);
            if (pin == null) return false;
            object payload = pin.Entry.Product;
            product = payload is RetainedGraphPayload retained ? retained.Product : payload;
            if (memoryPins.ContainsKey(payload)) pin.Dispose();
            else memoryPins.Add(payload, pin);
            return true;
        }

        internal RetainedProduct RetainProduct(ulong hash, object product)
        {
            // UC-T02-06: native graph textures are evaluation-owned. Cache their source fields, never a descriptor of a retired native object.
            if (HasOwnedTextureResources()) return default; // UC-T07-08: function/halo textures retire with their child views too.
            GraphPublication[] publications = CurrentPublications();
            if (memoryBudget == null) return new RetainedProduct { hash = hash, product = product, unbudgetedPublications = publications };
            if (publications.Length > 0)
            {
                var payload = new RetainedGraphPayload(product, publications);
                TrackProductMemory(payload, CurrentPublicationKey);
                return new RetainedProduct { hash = hash, memoryOwner = memoryBudget, memoryEntry = memoryPins[payload].Entry };
            }
            return new RetainedProduct { hash = hash, memoryOwner = memoryBudget, memoryEntry = memoryPins[product].Entry };
        }

        internal void AcceptRetainedProducts()
        {
            if (memoryBudget == null || retained == null) return;
            foreach (RetainedProduct product in retained.Values)
                if (ReferenceEquals(product.memoryOwner, memoryBudget)) memoryBudget.Retain(product.memoryEntry);
        }

        // UC-T07-09: only replaced/deleted cache tokens lose ownership; unused accepted branches remain reusable.
        internal void AcceptRetainedProducts(Dictionary<string, RetainedProduct> previous)
        {
            if (previous != null && retained != null)
                foreach (var pair in previous)
                    if (!retained.TryGetValue(pair.Key, out var next) || !ReferenceEquals(next.memoryEntry, pair.Value.memoryEntry))
                        pair.Value.memoryOwner?.Forget(pair.Value.memoryEntry);
            AcceptRetainedProducts();
        }

        private bool HasOwnedTextureResources()
        {
            if (ownedTextures != null && ownedTextures.Count > 0) return true;
            if (memoryViews != null) foreach (TileData view in memoryViews) if (view.HasOwnedTextureResources()) return true;
            return false;
        }

        internal ProductMemoryBudget.Lease DetachProductMemory(object product)
        {
            if (product == null || !memoryPins.TryGetValue(product, out ProductMemoryBudget.Lease pin)) return null;
            memoryPins.Remove(product);
            return pin;
        }

        /// <summary>UC-T02-06: retires graph payload pins after workers and all output consumers have finished.</summary>
        /// <remarks>Idempotent, owner-thread only. Releases lazy inputs and cache references; never destroys borrowed native assets.
        /// Output references remain readable, but the caller must no longer keep them as active budgeted consumers.</remarks>
        public void ReleaseProductMemory()
        {
            ReleaseTextureMemory(); // UC-T02-06: callers must first transfer renderer references to persistent asset copies.
            ReleaseGenerationInputs();
            evaluator = null;
            cache.Clear();
            ReleaseFunctionViews(); // UC-T07-08: do not keep nested evaluators/argument captures after retirement.
            retained = null;
            ReleasePublications(); // UC-T02-07: publication arrays have the same final-consumer lifetime as their cached products.
            if (ownsDrainageMemory) { drainageCache?.Dispose(); drainageCache = null; ownsDrainageMemory = false; }
            if (memoryViews != null)
            {
                foreach (TileData view in memoryViews) view.ReleaseProductMemory();
                memoryViews.Clear();
            }
            foreach (ProductMemoryBudget.Lease pin in memoryPins.Values) pin.Dispose();
            memoryPins.Clear();
            foreach (var pin in graphInputPins) pin.Dispose(); // UC-T05-06: halo views borrow their parent's pins and do not retire them.
            graphInputPins.Clear(); worldGraphGround = null;
        }
    }
}
