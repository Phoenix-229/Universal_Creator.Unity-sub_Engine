// UC-T05-03: domain configuration only; shared canvas/controller and world executor own behavior.
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.Editor.Windows
{
    /// <summary>Vegetation, placement and ecology authoring for one explicitly applied UC world graph.</summary>
    public sealed class VegetationGraphWindow : CompanionGraphWindow
    {
        protected override UCGraphDomain Domain => UCGraphDomain.Vegetation;
        /// <summary>Opens or focuses the Vegetation Graph editor; existing saved asset/world selection survives editor reload.</summary>
        public static void OpenWindow()
        {
            var window = GetWindow<VegetationGraphWindow>("Universe Creator - Vegetation Graph");
            window.minSize = new Vector2(400, 300); window.Show();
        }
    }
}
