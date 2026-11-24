// UC-T05-04: one environment domain on the shared companion canvas and world binding.
using UnityEditor;
using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.Editor.Windows
{
    /// <summary>Authors climate, lighting and weather on one explicitly applied UC world environment graph.</summary>
    public sealed class LightingGraphWindow : CompanionGraphWindow
    {
        protected override UCGraphDomain Domain => UCGraphDomain.LightingClimateWeather;
        /// <summary>Opens or focuses Lighting Graph, preserving serialized asset/world selection across editor reload.</summary>
        public static void OpenWindow()
        {
            var window = GetWindow<LightingGraphWindow>("Universe Creator - Lighting Graph");
            window.minSize = new Vector2(400, 300); window.Show();
        }
    }
}
