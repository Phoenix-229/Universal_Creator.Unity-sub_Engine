// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 1: Terrain Core
//
//  File:    GraphTemplates.cs
//  Purpose: Builds the default graph presets (Empty, Simple Terrain).
//
//  These return in-memory Graph instances. GraphAssetUtility is what writes them
//  to disk as .asset files and opens them in the Node Graph window.
//
//  SPEC NOTE
//  ---------
//  Module1_Implementation_Prompt.md asks for the on-disk extension to display as
//  .ucgraph. Unity will not import a custom extension as a ScriptableObject
//  without a ScriptedImporter, so templates are saved as .asset. Topology and
//  parameter values match the spec exactly; only the file extension differs.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Creators;
using UniverseCreator.Nodes.Modifiers;
using UniverseCreator.Nodes.Outputs;

namespace UniverseCreator.Editor.Utilities
{
    /// <summary>
    /// Factory for the default graph presets offered in Project Settings, the
    /// Create New World menu item, and the inspector's Create Empty button.
    /// </summary>
    public static class GraphTemplates
    {
        /// <summary>Identifiers for the templates, used by the settings dropdown.</summary>
        public enum Template
        {
            Empty = 0,
            SimpleTerrain = 1
        }

        /// <summary>Display names, index-aligned with the Template enum.</summary>
        public static readonly string[] DisplayNames = { "Empty Graph", "Simple Terrain" };

        /// <summary>Suggested asset file names, index-aligned with the Template enum.</summary>
        public static readonly string[] FileNames = { "New Graph", "Simple Terrain" };

        /// <summary>Builds the requested template as an unsaved in-memory asset.</summary>
        public static Graph Create(Template template)
        {
            switch (template)
            {
                case Template.SimpleTerrain: return CreateSimpleTerrain();
                default: return CreateEmpty();
            }
        }

        /// <summary>
        /// Starter graph: a single Height Output node, nothing feeding it.
        ///
        /// Deliberately NOT a blank canvas. An output node is the one thing a graph
        /// cannot generate terrain without, and it is the only node whose identity
        /// the user cannot infer - so it is pre-placed and left unconnected. Wire
        /// anything into its Height inlet and terrain appears; leave it and the
        /// world stays flat, which is the same result an empty graph would give.
        /// </summary>
        public static Graph CreateEmpty()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            graph.name = "New Graph";

            HeightOutputNode output = new HeightOutputNode();
            output.editorPosition = new Vector2(520f, 200f);
            graph.nodes.Add(output);

            return graph;
        }

        /// <summary>
        /// Perlin Noise -> Hydraulic Erosion -> World Edge Falloff -> Height Output,
        /// pre-connected. Parameter values are the ones named in the spec.
        ///
        /// The edge falloff sits last, after erosion, so the coastline it carves
        /// cannot be re-roughened by a later pass. Without it the continent would end
        /// in a wall of hills sliced flat at the world boundary.
        /// </summary>
        public static Graph CreateSimpleTerrain()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            graph.name = "Simple Terrain";

            PerlinNoiseNode perlin = new PerlinNoiseNode();
            perlin.size = 2000f;
            perlin.detail = 0.45f;
            perlin.intensity = 0.8f;
            perlin.editorPosition = new Vector2(80f, 200f);

            HydraulicErosionNode erosion = new HydraulicErosionNode();
            erosion.iterations = 3;
            erosion.editorPosition = new Vector2(300f, 200f);

            WorldEdgeFalloffNode falloff = new WorldEdgeFalloffNode();
            falloff.falloffMeters = 500f;   // the 500m continent-edge rule
            falloff.edgeHeight = 0f;        // sea level
            falloff.editorPosition = new Vector2(520f, 200f);

            HeightOutputNode output = new HeightOutputNode();
            output.editorPosition = new Vector2(740f, 200f);

            graph.nodes.Add(perlin);
            graph.nodes.Add(erosion);
            graph.nodes.Add(falloff);
            graph.nodes.Add(output);

            // Port 0 on every node here: Height out -> Height in.
            graph.AddConnection(new Connection(perlin.id, 0, erosion.id, 0));
            graph.AddConnection(new Connection(erosion.id, 0, falloff.id, 0));
            graph.AddConnection(new Connection(falloff.id, 0, output.id, 0));

            return graph;
        }
    }
}
