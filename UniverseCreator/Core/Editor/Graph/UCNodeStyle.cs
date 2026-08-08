// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    UCNodeStyle.cs
//  Purpose: Header colour, port colour, and title-bar icon for a node.
//
//  Correction C-10. Icons are glyphs rather than PNGs so the node chrome works
//  with no texture import step and cannot show a pink missing-texture box.
//  Everything here is deliberately inside the Basic Multilingual Plane - the
//  editor font renders these; colour emoji above U+FFFF would draw as tofu.
//  Category colours delegate to the runtime NodeCategoryColors so the editor
//  legend and the node header can never drift apart.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.Editor.GraphUI
{
    public static class UCNodeStyle
    {
        /// <summary>Title-bar colour for a category. Darkened slightly so white
        /// bold text stays legible on the lighter categories.</summary>
        public static Color HeaderColor(NodeCategory category)
        {
            Color c = NodeCategoryColors.Get(category);
            return new Color(c.r * 0.82f, c.g * 0.82f, c.b * 0.82f, 1f);
        }

        public static Color PortColor(PortType type)
        {
            switch (type)
            {
                case PortType.Matrix: return new Color(0.55f, 0.85f, 0.45f);
                default:              return new Color(0.80f, 0.80f, 0.80f);
            }
        }

        /// <summary>Colour for an edge carrying this port type.</summary>
        public static Color EdgeColor(PortType type)
        {
            Color c = PortColor(type);
            return new Color(c.r, c.g, c.b, 0.9f);
        }

        /// <summary>Edges reaching a terminal Output node, highlighted so the path
        /// that actually writes the terrain stands out from the rest of the graph.</summary>
        public static readonly Color TerminalEdgeColor = new Color(0.95f, 0.78f, 0.25f);

        // Per-node glyphs keyed on display name. Every shipping node is listed, so
        // the category fallback in Icon() is reached only by a node added later -
        // it keeps that node from drawing blank until it gets its own entry here.
        private static readonly Dictionary<string, string> ByName =
            new Dictionary<string, string>
            {
                // Creators
                { "Perlin Noise",           "∿" },  // sine wave
                { "Simplex Noise",          "◇" },
                { "Value Noise",            "░" },
                { "Voronoi",                "⬡" },  // hexagon
                { "Ridged Noise",           "▲" },
                { "Billow Noise",           "◕" },
                { "Constant",               "▬" },
                { "Simple Form",            "◢" },
                { "Import Heightmap",       "⤓" },  // down arrow to bar
                { "Spot Brush",             "●" },
                { "Tectonic Plates",        "◤" },
                { "Real-World Import",      "◎" },

                // Modifiers
                { "Hydraulic Erosion",      "≈" },  // almost-equal, water
                { "Thermal Erosion",        "♨" },  // heat
                { "Wind Erosion",           "≫" },
                { "Coastal Erosion",        "≋" },
                { "Curve",                  "⤴" },
                { "Levels",                 "≡" },
                { "Contrast",               "◐" },
                { "Blur",                   "▒" },
                { "Sharpen",                "✦" },
                { "Distort",                "⌇" },
                { "Terrace",                "◧" },
                { "Ledge",                  "⌷" },
                { "Direction",              "➤" },
                { "Parallax",               "◰" },
                { "Clamp",                  "⌶" },
                { "Invert",                 "◑" },
                { "Power",                  "ˆ" },
                { "Sediment Map",           "≂" },
                { "World Edge Falloff",     "▢" },

                // Selectors. The class is HeightSelectorNode but NodeName is the
                // bare noun, so both spellings are keyed - a rename on either
                // side still finds a glyph.
                { "Height",                 "↨" },
                { "Height Selector",        "↨" },
                { "Slope",                  "◥" },
                { "Slope Selector",         "◥" },
                { "Cavity",                 "◡" },
                { "Cavity Selector",        "◡" },
                { "Aspect",                 "⦿" },
                { "Aspect Selector",        "⦿" },
                { "Curvature",              "⌒" },
                { "Curvature Selector",     "⌒" },
                { "Noise Mask",             "▓" },
                { "Texture Import Mask",    "▦" },
                { "Flow Map",               "⇝" },

                // Combiners
                { "Blend",                  "⧉" },
                { "Mask Mix",               "◒" },
                { "Normalize",              "↕" },
                { "Maximum",                "↑" },
                { "Minimum",                "↓" },
                { "Weighted Average",       "⚖" },  // scales

                // Scatter
                { "Object Scatter",         "✵" },
                { "Tree Scatter",           "♣" },  // club, reads as a tree
                { "Grass Detail",           "⸙" },
                { "Rock Scatter",           "◆" },
                { "Debris Scatter",         "⁂" },
                { "Underwater Scatter",     "⚓" },  // anchor

                // Outputs
                { "Height Output",          "▰" },
                { "Texture Output",         "▩" },
                { "Grass Output",           "❦" },
                { "Holes Output",           "○" },
                { "Custom Mesh Output",     "⬢" },
                { "Normal Map Output",      "◬" },

                // Utility
                { "Portal In",              "↦" },
                { "Portal Out",             "↤" },
                { "Shared Value",           "ƒ" },
                { "Math",                   "±" },
                { "Comment Group",          "⌗" },
                { "Preview",                "▣" },

                // Biome
                { "Temperature Map",        "☀" },
                { "Moisture Map",           "☔" },  // umbrella with rain
                { "Biome Classifier",       "⌘" },
                { "Biome Mask",             "▤" },
            };

        public static string Icon(Generator node)
        {
            if (node == null) return "□";

            string glyph;
            if (ByName.TryGetValue(node.NodeName, out glyph)) return glyph;

            return CategoryIcon(node.Category);
        }

        public static string CategoryIcon(NodeCategory category)
        {
            switch (category)
            {
                // Distinct from every per-node glyph above: a category glyph labels a
                // search-window group that sits directly over its own nodes, and the
                // same mark in both places reads as a repeated entry.
                case NodeCategory.Creator:  return "✧";  // open four-pointed star
                case NodeCategory.Modifier: return "⚙";  // gear
                case NodeCategory.Selector: return "◓";
                case NodeCategory.Combiner: return "⊞";  // boxed plus
                case NodeCategory.Scatter:  return "⁘";
                case NodeCategory.Output:   return "▼";
                case NodeCategory.Utility:  return "⚒";  // hammer and pick
                case NodeCategory.Biome:    return "❈";
                default:                    return "□";
            }
        }
    }
}
