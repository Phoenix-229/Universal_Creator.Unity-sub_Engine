using UnityEngine;
using UniverseCreator.Geology;

namespace UniverseCreator.Nodes
{
    // UC-T01-06/07: binds the three shipped geology readers to a copied, snapshot-owned asset.
    internal interface ILithologySnapshotInput
    {
        LithologyProfile SourceProfile { get; }
        LithologyProfile CapturedProfile { get; }
        void BindCapturedProfile(LithologyProfile profile);
    }

    internal static class PreparedLithologyInputs
    {
        // Main thread during preparation; every ordered unit/structure field participates, including removal.
        internal static ulong Fingerprint(LithologyProfile profile)
        {
            if (profile == null) return 1UL;
            string json = JsonUtility.ToJson(profile);
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < json.Length; i++) hash = PreparedRasterInputs.Mix(hash, json[i]);
            return hash == 0UL ? 1UL : hash;
        }
    }
}
