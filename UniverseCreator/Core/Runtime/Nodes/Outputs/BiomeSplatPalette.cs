// -----------------------------------------------------------------------------
//  Universe Creator - Biomes
//  File:    BiomeSplatPalette.cs   (BIO-5)
//  Purpose: A shareable, data-driven biome -> ground-layer assignment, as a
//           ScriptableObject asset instead of a table inlined on one splat node.
//           One palette can drive several worlds, and the buyer fills it; it
//           ships EMPTY (the hard asset-path-free rule), and an empty palette
//           falls back to the node's own default assignment, so nothing changes
//           until the buyer populates it.
//
//  WHY A SCRIPTABLEOBJECT. The old assignment lived as a List<BiomeSplatRule>
//  serialized on each BiomeSplatOutputNode, so two worlds that want the same
//  ground mapping had to re-enter it, and they drifted. A palette asset is the
//  single source: assign it to the node and the node resolves its layers through
//  it. No asset PATHS live here - only biome -> layer-index intent; the textures
//  for those indices are still filled per-world.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.Nodes.Outputs
{
    [CreateAssetMenu(fileName = "BiomeSplatPalette",
                     menuName = "Universe Creator/Biome Splat Palette", order = 220)]
    public class BiomeSplatPalette : ScriptableObject
    {
        [Tooltip("Biome -> ground-layer assignment. SHIPPED EMPTY: an empty palette falls back to " +
                 "the splat node's own defaults, so assigning an unfilled palette changes nothing. " +
                 "Fill a rule per biome to override the node's assignment from one shared asset.")]
        public List<BiomeSplatRule> rules = new List<BiomeSplatRule>();

        /// <summary>True when the palette actually carries an assignment to apply.</summary>
        public bool HasRules { get { return rules != null && rules.Count > 0; } }
    }
}
