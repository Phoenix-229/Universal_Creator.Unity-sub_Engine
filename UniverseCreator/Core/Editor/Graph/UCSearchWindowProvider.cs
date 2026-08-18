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
using UniverseCreator.Editor.Utilities;

namespace UniverseCreator.Editor.GraphUI
{
    public class UCSearchWindowProvider : ScriptableObject, ISearchWindowProvider
    {
        private UCGraphView owner;

        public void Init(UCGraphView graphView)
        {
            owner = graphView;
        }

        public List<SearchTreeEntry> CreateSearchTree(SearchWindowContext context)
        {
            List<SearchTreeEntry> tree = new List<SearchTreeEntry>
            {
                new SearchTreeGroupEntry(new GUIContent("Add Node"), 0)
            };

            // NodeCatalog.All is sorted by category then name, so consecutive
            // entries share a category and one group per run is enough.
            string lastCategory = null;
            foreach (NodeCatalogEntry entry in NodeCatalog.All)
            {
                // The owning window decides which categories it can host. Null
                // means all of them, which is the height graph's answer.
                if (owner != null && owner.CategoryFilter != null &&
                    !owner.CategoryFilter(entry.category)) continue;

                if (entry.category.ToString() != lastCategory)
                {
                    lastCategory = entry.category.ToString();
                    tree.Add(new SearchTreeGroupEntry(new GUIContent(lastCategory), 1));
                }

                tree.Add(new SearchTreeEntry(new GUIContent(entry.nodeName))
                {
                    level = 2,
                    userData = entry
                });
            }

            return tree;
        }

        public bool OnSelectEntry(SearchTreeEntry entry, SearchWindowContext context)
        {
            NodeCatalogEntry catalog = entry.userData as NodeCatalogEntry;
            if (catalog == null || owner == null || owner.Graph == null) return false;

            // The node lands where the user right-clicked (or the toolbar centre),
            // not at the search window - the position was captured before opening.
            owner.AddNodeAt(catalog, owner.PendingAddPosition);
            return true;
        }
    }
}
