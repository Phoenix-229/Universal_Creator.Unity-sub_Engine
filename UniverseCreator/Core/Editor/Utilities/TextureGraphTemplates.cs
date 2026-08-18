// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 2: PCG Extended Capability Port
//
//  File:    TextureGraphTemplates.cs
//  Purpose: Starter presets for the Texture Graph window.
//
//  WHY THIS IS NOT AN ADDITION TO GraphTemplates
//  ---------------------------------------------
//  GraphTemplates.Template is read by the Project Settings "Default Template"
//  dropdown that drives GameObject > Create New World. A texture-surface preset
//  in that list would offer the user a world whose graph contains no height
//  output - a world that generates nothing. The two template sets answer two
//  different questions, so they are two types.
//
//  Both write through GraphAssetUtility.SaveAsset, so a texture graph and a
//  height graph are the same asset type on disk, land in the same folder, and are
//  both double-clickable. Only the nodes inside differ.
//
//  A NOTE ON WHY THE PRESETS ARE WIRED AND NOT JUST PLACED
//  ------------------------------------------------------
//  An unwired preset teaches nothing. The value of Full PBR Surface is that a
//  user can open it, see six feature nodes already feeding one chain in the order
//  that matters, and learn the topology by deleting from it rather than by
//  guessing at it from an empty canvas.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Nodes.Texturing;

namespace UniverseCreator.Editor.Utilities
{
    /// <summary>
    /// Factory for the texture graph presets offered in the Texture Graph window's
    /// no-graph panel and under Assets > Create > Universe Creator.
    /// </summary>
    public static class TextureGraphTemplates
    {
        public enum Template
        {
            /// <summary>Shader Output alone. Wire anything into it and a material appears.</summary>
            Empty = 0,

            /// <summary>Four albedo layers, splatted, budgeted, written. The minimum useful graph.</summary>
            BasicSurface = 1,

            /// <summary>Basic Surface plus the six feature nodes most terrains want.</summary>
            FullPbrSurface = 2
        }

        /// <summary>Display names, index-aligned with Template.</summary>
        public static readonly string[] DisplayNames =
        { "Empty Texture Graph", "Basic Surface", "Full PBR Surface" };

        /// <summary>Suggested asset file names, index-aligned with Template.</summary>
        public static readonly string[] FileNames =
        { "New Texture Graph", "Basic Surface", "Full PBR Surface" };

        /// <summary>Builds the requested preset as an unsaved in-memory asset.</summary>
        public static Graph Create(Template template)
        {
            switch (template)
            {
                case Template.BasicSurface:   return CreateBasicSurface();
                case Template.FullPbrSurface: return CreateFullPbrSurface();
                default:                      return CreateEmpty();
            }
        }

        /// <summary>Builds a preset and writes it to disk under the graphs folder.</summary>
        public static Graph CreateAsset(Template template, string fileName = null)
        {
            Graph graph = Create(template);
            if (graph == null) return null;

            if (string.IsNullOrEmpty(fileName))
            {
                int index = (int)template;
                fileName = (index >= 0 && index < FileNames.Length)
                    ? FileNames[index]
                    : "New Texture Graph";
            }

            return GraphAssetUtility.SaveAsset(graph, GraphAssetUtility.DefaultFolder, fileName);
        }

        // =====================================================================
        //  PRESETS
        // =====================================================================

        /// <summary>
        /// A single Shader Output node.
        ///
        /// The same reasoning as the height graph's empty preset: the terminal node
        /// is the one node a graph cannot work without and the one whose identity a
        /// user cannot infer, so it is pre-placed and left unconnected.
        /// </summary>
        public static Graph CreateEmpty()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            graph.name = "New Texture Graph";

            ShaderOutputNode output = new ShaderOutputNode();
            output.editorPosition = new Vector2(560f, 220f);
            graph.nodes.Add(output);

            return graph;
        }

        /// <summary>
        /// Four Albedo Layers -> Texture Splat -> Terrain Blend Final -> Shader Output.
        ///
        /// The layer names and tiling are the defaults a temperate world wants, and
        /// they are in the order the control map indexes: R is what shows where the
        /// height graph painted nothing, so it is the ground cover, not the rock.
        ///
        /// Tiling deliberately differs per layer (8 / 6 / 4 / 12 m). Four layers on
        /// the same period line up into one visible lattice, and staggering the
        /// periods so they are not integer multiples of each other pushes the point
        /// where they realign out past anything the camera will frame.
        /// </summary>
        public static Graph CreateBasicSurface()
        {
            Graph graph = ScriptableObject.CreateInstance<Graph>();
            graph.name = "Basic Surface";

            AlbedoLayerNode grass = MakeLayer("Grass", new Color(0.32f, 0.44f, 0.20f), 8f, 0.30f, 40f);
            AlbedoLayerNode dirt  = MakeLayer("Dirt",  new Color(0.42f, 0.33f, 0.24f), 6f, 0.22f, 170f);
            AlbedoLayerNode rock  = MakeLayer("Rock",  new Color(0.46f, 0.45f, 0.43f), 4f, 0.38f, 300f);
            AlbedoLayerNode sand  = MakeLayer("Sand",  new Color(0.72f, 0.65f, 0.48f), 12f, 0.26f, 430f);

            TextureSplatNode splat = new TextureSplatNode();
            splat.editorPosition = new Vector2(320f, 200f);

            TerrainBlendFinalNode blend = new TerrainBlendFinalNode();
            blend.editorPosition = new Vector2(560f, 200f);

            ShaderOutputNode output = new ShaderOutputNode();
            output.editorPosition = new Vector2(800f, 200f);

            graph.nodes.Add(grass);
            graph.nodes.Add(dirt);
            graph.nodes.Add(rock);
            graph.nodes.Add(sand);
            graph.nodes.Add(splat);
            graph.nodes.Add(blend);
            graph.nodes.Add(output);

            // Layer -> control map channel. Port index IS the channel index.
            graph.AddConnection(new Connection(grass.id, 0, splat.id, 0));
            graph.AddConnection(new Connection(dirt.id,  0, splat.id, 1));
            graph.AddConnection(new Connection(rock.id,  0, splat.id, 2));
            graph.AddConnection(new Connection(sand.id,  0, splat.id, 3));

            // Splat feeds Terrain Blend Final's port 1; port 0 is the Surface chain,
            // which Basic Surface leaves empty on purpose - the layers alone are a
            // complete, correct material.
            graph.AddConnection(new Connection(splat.id, 0, blend.id, 1));
            graph.AddConnection(new Connection(blend.id, 0, output.id, 0));

            return graph;
        }

        /// <summary>
        /// Basic Surface with the surface chain populated.
        ///
        /// THE ORDER OF THE CHAIN IS THE POINT
        /// -----------------------------------
        ///   Cliff Surface    - first, because it decides what the steep ground IS
        ///   Macro Variation  - large-scale colour drift over that
        ///   Snow Cover       - lies on top of whatever is underneath, including cliff
        ///   Wetness          - darkens and smooths whatever is exposed, snow included
        ///   Distance Fade    - a budget decision, so it sits after the look is fixed
        ///   Colour Grade     - last, so it grades the finished result
        ///
        /// Every node here copies-then-mutates, so the order is not cosmetic: a grade
        /// placed before snow would grade the ground and leave the snow ungraded.
        /// </summary>
        public static Graph CreateFullPbrSurface()
        {
            Graph graph = CreateBasicSurface();
            graph.name = "Full PBR Surface";

            // The splat/blend/output spine already exists; find the two nodes the
            // chain must attach to rather than rebuilding them.
            TerrainBlendFinalNode blend = FindFirst<TerrainBlendFinalNode>(graph);
            if (blend == null) return graph;

            // Push the spine right to make room for the chain above it.
            blend.editorPosition = new Vector2(1040f, 320f);
            ShaderOutputNode output = FindFirst<ShaderOutputNode>(graph);
            if (output != null) output.editorPosition = new Vector2(1280f, 320f);

            CliffSurfaceNode cliff = new CliffSurfaceNode();
            cliff.editorPosition = new Vector2(80f, -60f);

            MacroVariationNode macro = new MacroVariationNode();
            macro.editorPosition = new Vector2(240f, -60f);

            SnowCoverNode snow = new SnowCoverNode();
            snow.editorPosition = new Vector2(400f, -60f);

            WetnessNode wetness = new WetnessNode();
            wetness.editorPosition = new Vector2(560f, -60f);

            DistanceFadeNode fade = new DistanceFadeNode();
            fade.editorPosition = new Vector2(720f, -60f);

            ColorGradeNode grade = new ColorGradeNode();
            grade.editorPosition = new Vector2(880f, -60f);

            graph.nodes.Add(cliff);
            graph.nodes.Add(macro);
            graph.nodes.Add(snow);
            graph.nodes.Add(wetness);
            graph.nodes.Add(fade);
            graph.nodes.Add(grade);

            // Every one of these is Surface in port 0 -> Surface out port 0. Snow
            // Cover's port 1 outputs are left unwired; the traction mask is for
            // physics, not for the material.
            graph.AddConnection(new Connection(cliff.id,   0, macro.id,   0));
            graph.AddConnection(new Connection(macro.id,   0, snow.id,    0));
            graph.AddConnection(new Connection(snow.id,    0, wetness.id, 0));
            graph.AddConnection(new Connection(wetness.id, 0, fade.id,    0));
            graph.AddConnection(new Connection(fade.id,    0, grade.id,   0));
            graph.AddConnection(new Connection(grade.id,   0, blend.id,   0));

            return graph;
        }

        // =====================================================================
        //  HELPERS
        // =====================================================================

        private static AlbedoLayerNode MakeLayer(string name, Color tint, float tileMetres,
                                                 float smoothness, float y)
        {
            AlbedoLayerNode layer = new AlbedoLayerNode();
            layer.layerName = name;
            layer.tint = tint;
            layer.tileSizeMetres = new Vector2(tileMetres, tileMetres);
            layer.smoothness = smoothness;
            layer.editorPosition = new Vector2(80f, y);
            return layer;
        }

        private static T FindFirst<T>(Graph graph) where T : Generator
        {
            for (int i = 0; i < graph.nodes.Count; i++)
            {
                T typed = graph.nodes[i] as T;
                if (typed != null) return typed;
            }
            return null;
        }
    }
}
