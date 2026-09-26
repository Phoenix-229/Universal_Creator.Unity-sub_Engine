// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    UCSearchWindowProvider.cs
//  Purpose: Search tree for the add-node SearchWindow: category groups at level
//           1, node entries at level 2, so the picker reads as a browsable
//           catalogue rather than a flat wall of 66 items.
//
//  Correction C-09. Built from NodeCatalog, so every node that compiles appears
//  here with no registration step.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.Editor.Utilities;

namespace UniverseCreator.Editor.GraphUI
{
    public class UCSearchWindowProvider : ScriptableObject, ISearchWindowProvider
    {
        private UCGraphView owner;

        // Sentinels the tree hands back for the combined "Portal" items: selecting one
        // drops a matched In+Out pair (UCGraphView.AddPortalPair) instead of one node.
        private const string PairHeight = "UC_PORTAL_PAIR_HEIGHT";
        private const string PairTexture = "UC_PORTAL_PAIR_TEXTURE";

        public void Init(UCGraphView graphView)
        {
            owner = graphView;
        }

        // The four raw portal nodes are hidden from the menu: a portal is always a
        // pair, so listing "Portal In" and "Portal Out" separately only made the user
        // add two nodes and hand-match their channels. One "Portal" item per graph
        // drops both, already on a shared fresh channel. The node classes are
        // untouched - this is purely how the ADD MENU presents them.
        private static bool IsRawPortal(System.Type t)
        {
            return t == typeof(UniverseCreator.Nodes.Utilities.PortalInNode)
                || t == typeof(UniverseCreator.Nodes.Utilities.PortalOutNode)
                || t == typeof(UniverseCreator.Nodes.Texturing.TexturePortalInNode)
                || t == typeof(UniverseCreator.Nodes.Texturing.TexturePortalOutNode);
        }

        public List<SearchTreeEntry> CreateSearchTree(SearchWindowContext context)
        {
            // Collect every visible item as (category, label, userData), then sort by
            // category+label so one group header per category is enough - the same
            // shape the old streaming loop produced, but now the synthetic "Portal"
            // item slots into its category in order and keeps its group non-empty even
            // if the raw portals were the only other members.
            List<KeyValuePair<string, SearchTreeEntry>> items =
                new List<KeyValuePair<string, SearchTreeEntry>>();

            foreach (NodeCatalogEntry entry in NodeCatalog.All)
            {
                if (owner != null && owner.CategoryFilter != null &&
                    !owner.CategoryFilter(entry.category)) continue;
                if (IsRawPortal(entry.type)) continue;   // replaced by the combined item

                items.Add(new KeyValuePair<string, SearchTreeEntry>(
                    entry.category.ToString(),
                    new SearchTreeEntry(new GUIContent(entry.nodeName)) { level = 2, userData = entry }));
            }

            // Height graph (hosts Utility) -> Utility/Portal; texture graph (hosts
            // Texture but not Utility) -> Texture/Portal. A null filter is the height
            // graph, which hosts everything.
            bool hostsUtility = owner == null || owner.CategoryFilter == null
                                || owner.CategoryFilter(NodeCategory.Utility);
            bool hostsTexture = owner != null && owner.CategoryFilter != null
                                && owner.CategoryFilter(NodeCategory.Texture);

            if (hostsUtility)
                items.Add(new KeyValuePair<string, SearchTreeEntry>(
                    NodeCategory.Utility.ToString(),
                    new SearchTreeEntry(new GUIContent("Portal (In + Out)")) { level = 2, userData = PairHeight }));
            else if (hostsTexture)
                items.Add(new KeyValuePair<string, SearchTreeEntry>(
                    NodeCategory.Texture.ToString(),
                    new SearchTreeEntry(new GUIContent("Portal (In + Out)")) { level = 2, userData = PairTexture }));

            items.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.Key, b.Key);
                return c != 0 ? c : string.CompareOrdinal(a.Value.content.text, b.Value.content.text);
            });

            List<SearchTreeEntry> tree = new List<SearchTreeEntry>
            {
                new SearchTreeGroupEntry(new GUIContent("Add Node"), 0)
            };

            string lastCategory = null;
            foreach (KeyValuePair<string, SearchTreeEntry> item in items)
            {
                if (item.Key != lastCategory)
                {
                    lastCategory = item.Key;
                    tree.Add(new SearchTreeGroupEntry(new GUIContent(lastCategory), 1));
                }
                tree.Add(item.Value);
            }

            return tree;
        }

        public bool OnSelectEntry(SearchTreeEntry entry, SearchWindowContext context)
        {
            if (owner == null || owner.Graph == null) return false;

            // Combined portal items drop a matched pair; everything else is one node.
            string sentinel = entry.userData as string;
            if (sentinel == PairHeight) { owner.AddPortalPair(false, owner.PendingAddPosition); return true; }
            if (sentinel == PairTexture) { owner.AddPortalPair(true, owner.PendingAddPosition); return true; }

            NodeCatalogEntry catalog = entry.userData as NodeCatalogEntry;
            if (catalog == null) return false;

            // The node lands where the user right-clicked (or the toolbar centre),
            // not at the search window - the position was captured before opening.
            owner.AddNodeAt(catalog, owner.PendingAddPosition);
            return true;
        }
    }
}
