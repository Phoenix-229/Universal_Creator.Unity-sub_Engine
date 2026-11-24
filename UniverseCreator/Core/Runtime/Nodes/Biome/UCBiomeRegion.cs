// -----------------------------------------------------------------------------
//  Universe Creator - Biomes
//  File:    UCBiomeRegion.cs   (BIO-4 / BIO-6 / BIO-7)
//  Purpose: Author-placed biome override. Drop a UCBiomeRegion in the scene, give
//           it a biome and a footprint, and that patch of world is THAT biome
//           regardless of climate - the "paint a biome from a volume" authoring
//           UE's Biome Core offers, done the scene-push way UCWaterBody already
//           uses so a worker thread never touches a component.
//
//  BIO-7 PRIORITY. Regions overlap, and the LOWEST priority number wins (UE's
//  convention), so a small wetland inside a big forest region reads as wetland.
//  BIO-6 AUTHORED BIOMES. The region names a BiomeType; the enum is append-only
//  (its order is serialized), so adding a new biome kind is "append one enum value
//  + place a region", not a runtime-dynamic registry that would break the splat's
//  index contract and the species allowed-biome bitmask.
//
//  The resolver is pure and static so priority is directly self-testable; the
//  MonoBehaviour only produces a plain-data footprint for it.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes.Biome
{
    /// <summary>Plain-data footprint of one biome region, safe to read off the main thread.</summary>
    [System.Serializable] // UC-O01 / UC-A03: include every footprint field in prepared-input hashing.
    public struct BiomeRegionFootprint
    {
        public Vector2 centerXZ;        // world XZ centre
        public Vector2 halfExtentsXZ;   // world XZ half-size of the axis-aligned area
        public int biome;               // BiomeType index this region stamps
        public int priority;            // lower wins where regions overlap
    }

    public static class BiomeRegionField
    {
        /// <summary>
        /// The biome a point falls under: the index of the lowest-priority (highest-precedence)
        /// region covering (x, z), or -1 if no region covers it. Pure, so BIO-7 priority is testable.
        /// </summary>
        public static int ResolveBiome(List<BiomeRegionFootprint> regions, float worldX, float worldZ)
        {
            if (regions == null) return -1;

            int bestBiome = -1;
            int bestPriority = int.MaxValue;

            for (int i = 0; i < regions.Count; i++)
            {
                BiomeRegionFootprint r = regions[i];
                if (Mathf.Abs(worldX - r.centerXZ.x) > r.halfExtentsXZ.x) continue;
                if (Mathf.Abs(worldZ - r.centerXZ.y) > r.halfExtentsXZ.y) continue;

                if (r.priority < bestPriority)
                {
                    bestPriority = r.priority;
                    bestBiome = r.biome;
                }
            }
            return bestBiome;
        }
    }

    /// <summary>
    /// Scene component: an authored biome patch. Placed and sized in the editor; the world object
    /// collects every active one into plain footprints and pushes them to the Biome Classifier,
    /// exactly as water bodies push their footprints to the height output.
    /// </summary>
    [AddComponentMenu("Universe Creator/Biome Region")]
    public class UCBiomeRegion : MonoBehaviour
    {
        [Tooltip("The biome this patch stamps over its footprint, regardless of climate.")]
        public BiomeType biome = BiomeType.TemperateForest;

        [Tooltip("Lower wins where regions overlap (UE convention): a small high-precedence region " +
                 "inside a big one reads as the small one.")]
        public int priority = 0;

        [Tooltip("World-metre half-size of the axis-aligned footprint about this object's position.")]
        public Vector2 halfExtentsM = new Vector2(100f, 100f);

        /// <summary>This region as a worker-safe footprint, in world XZ.</summary>
        public BiomeRegionFootprint ToFootprint()
        {
            Vector3 p = transform.position;
            return new BiomeRegionFootprint
            {
                centerXZ = new Vector2(p.x, p.z),
                halfExtentsXZ = new Vector2(Mathf.Abs(halfExtentsM.x), Mathf.Abs(halfExtentsM.y)),
                biome = (int)biome,
                priority = priority
            };
        }

        /// <summary>
        /// Collects every active biome region under <paramref name="root"/> into plain footprints,
        /// or null when there are none. Main thread only (reads the component hierarchy).
        /// </summary>
        public static List<BiomeRegionFootprint> Collect(Transform root)
        {
            if (root == null) return null;
            UCBiomeRegion[] regions = root.GetComponentsInChildren<UCBiomeRegion>(true);
            if (regions == null || regions.Length == 0) return null;

            List<BiomeRegionFootprint> list = new List<BiomeRegionFootprint>(regions.Length);
            // UC-T01-06: a nested world's regions belong to that world's capture, even inside this root.
            UniverseCreator.TerrainSystem.UniverseCreatorObject world = root.GetComponentInParent<UniverseCreator.TerrainSystem.UniverseCreatorObject>();
            for (int i = 0; i < regions.Length; i++)
                if (regions[i] != null && regions[i].isActiveAndEnabled &&
                    (world == null || regions[i].GetComponentInParent<UniverseCreator.TerrainSystem.UniverseCreatorObject>() == world))
                    list.Add(regions[i].ToFootprint());

            return list.Count > 0 ? list : null;
        }

        /// <summary>
        /// Collects the scene's biome regions under <paramref name="root"/> and pushes them into
        /// every Biome Classifier in <paramref name="graph"/>. Called once per bake on the main
        /// thread (the classifier then reads the plain footprints off-thread), mirroring the
        /// water-body footprint push. Null footprints clear any previous override.
        /// </summary>
        public static void PushToGraph(Graph graph, Transform root)
        {
            if (graph == null || graph.nodes == null) return;

            List<BiomeRegionFootprint> footprints = Collect(root);
            for (int i = 0; i < graph.nodes.Count; i++)
            {
                BiomeClassifierNode classifier = graph.nodes[i] as BiomeClassifierNode;
                if (classifier != null) classifier.SetRegionOverrides(footprints);
            }
        }
    }
}
