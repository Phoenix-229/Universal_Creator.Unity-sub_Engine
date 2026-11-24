// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    PaintLayerResolver.cs
//  Purpose: Paint survives a tier change and a layer-array reorder (§ 9.8 G-56, and G-46).
//           A stroke names its target layer by a STABLE KEY (the layer asset's identity),
//           never by its position in the terrain's layer array. Index-based storage is how
//           a paint tool silently loses work: a tier switch that changes the layer count, or
//           any reorder, repoints every stroke at the wrong layer. Keying by identity means
//           the index is re-resolved at composite time from whatever the current array is.
//
//  A REMOVED LAYER IS HELD, NOT DROPPED
//  ------------------------------------
//  When a layer is absent, `ResolveIndex` returns −1 - the stroke is KEPT, just not composited
//  this pass - and `WarnOnceForMissing` fires exactly once per absence so the log is not spammed
//  every frame. Re-adding the layer (even at a different index) makes the key resolve again and
//  the paint reappears, which is precisely the done-when: remove a layer, re-add it, its paint
//  is restored. `Rebind` clears the warned mark for any key that is present again, so a later
//  removal warns afresh.
//
//  PURE AND DEVICE-FREE
//  --------------------
//  Stable keys are plain integers here (the scene layer maps a TerrainLayer asset - by
//  instance id or GUID hash - to one). No TerrainLayer, no Debug.LogWarning: the self-test
//  pins identity-not-index resolution, the hold-don't-drop of a missing layer, the warn-once,
//  and the restore-on-re-add.
// -----------------------------------------------------------------------------

using System.Collections.Generic;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>
    /// Resolves a stroke's stable layer key to the current alphamap channel index (§ 9.8 G-56): identity, not
    /// position, so paint survives a tier change or a layer-array reorder. A missing layer resolves to −1 (the
    /// stroke is held, not dropped) and warns exactly once. Device-free.
    /// </summary>
    public sealed class PaintLayerResolver
    {
        private readonly Dictionary<int, int> _keyToIndex = new Dictionary<int, int>();
        private readonly HashSet<int> _warnedMissing = new HashSet<int>();

        /// <summary>Rebuild the key→index map from the current layer array (call on any tier or layer-array change).
        /// The first occurrence of a key wins; any key now present is cleared from the warned set so a later
        /// removal warns again.</summary>
        public void Rebind(IReadOnlyList<int> currentLayerKeys)
        {
            _keyToIndex.Clear();
            if (currentLayerKeys != null)
                for (int i = 0; i < currentLayerKeys.Count; i++)
                {
                    int key = currentLayerKeys[i];
                    if (!_keyToIndex.ContainsKey(key)) _keyToIndex[key] = i;
                    _warnedMissing.Remove(key);   // present again -> allow a fresh warning if it later leaves
                }
        }

        /// <summary>The current alphamap channel index for a stroke's stable layer key, or −1 if that layer is not
        /// present. Re-resolved every composite pass, so a reorder or tier change cannot mis-target a stroke.</summary>
        public int ResolveIndex(int layerKey)
        {
            return _keyToIndex.TryGetValue(layerKey, out int idx) ? idx : -1;
        }

        /// <summary>Whether the caller should log the "layer missing" warning for this key now: true exactly once
        /// per absence (until <see cref="Rebind"/> sees the key present again). A present key never warns.</summary>
        public bool WarnOnceForMissing(int layerKey)
        {
            if (_keyToIndex.ContainsKey(layerKey)) return false;
            return _warnedMissing.Add(layerKey);   // Add returns false if already warned
        }

        /// <summary>The number of layers currently resolvable.</summary>
        public int ResolvableCount => _keyToIndex.Count;
    }
}
