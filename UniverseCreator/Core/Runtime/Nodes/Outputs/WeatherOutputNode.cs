// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    WeatherOutputNode.cs
//  Purpose: Terminates a weather branch. Takes up to eight authored bias fields -
//           precipitation, temperature, wind, fog, cloud, snow, storm and
//           turbidity - and records them where the weather driver can read them.
//
//  BIASES, NOT OVERRIDES
//  ---------------------
//  Every port here nudges a value the climate physics already computed; it never
//  replaces it. The driver adds or scales the field onto the per-chunk climate
//  normal, so a wired bias shifts the weather without cutting it loose from the
//  latitude, continentality and altitude that produced it. A port that overrode
//  the physics outright would let an author paint a desert at the pole, which is
//  the guesswork the whole system exists to leave behind.
//
//  WHY EIGHT MATRIX INPUTS
//  -----------------------
//  A bias is a field, not a number: an author wires it from a gradient to make one
//  coast wetter than the other, or from noise for patchy cloud. Each port is
//  optional - an unwired port is no bias at all, and the driver reads a null field
//  as "leave this one alone", so a graph that wires none behaves exactly as one
//  with no weather branch. The first five ports keep the names the retired weather
//  output used, so a graph saved against it reconnects unchanged.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Nodes.Outputs
{
    [System.Serializable]
    public class WeatherOutputNode : Generator, IOutputNode
    {
        public override string NodeName { get { return "Weather Output"; } }
        public override NodeCategory Category { get { return NodeCategory.Output; } }

        public override string Tooltip
        {
            get
            {
                return "Sends authored bias fields to the weather driver. Each port shifts a "
                     + "computed value up or down - it never replaces the climate physics.";
            }
        }

        // The five original ports come first and keep their names so a saved graph
        // reconnects; the last three are appended after them, never inserted.
        private static readonly Port[] Ins =
        {
            new Port("In Precip Bias",      PortType.Matrix, PortDirection.Input),
            new Port("In Temperature Bias", PortType.Matrix, PortDirection.Input),
            new Port("In Wind Bias",        PortType.Matrix, PortDirection.Input),
            new Port("In Fog Bias",         PortType.Matrix, PortDirection.Input),
            new Port("In Cloud Bias",       PortType.Matrix, PortDirection.Input),
            new Port("In Snow Bias",        PortType.Matrix, PortDirection.Input),
            new Port("In Storm Bias",       PortType.Matrix, PortDirection.Input),
            new Port("In Turbidity Bias",   PortType.Matrix, PortDirection.Input),
            new Port("Climate Baseline",   PortType.Climate, PortDirection.Input), // UC-T08-03: existing bias ports retain their indices.
            new Port("Weather Regions",    PortType.Weather, PortDirection.Input) // UC-T08-06: append; never move an existing saved wire.
        };

        public override Port[] GetInputs() { return Ins; }
        public override Port[] GetOutputs() { return SharedPorts.None; }

        public override object Generate(int outputIndex, TileData tile, StopToken stop)
        {
            if (tile == null) return null;

            // UC-T08-03: a climate-only terminal publishes even when no short-term bias matrices are wired.
            var climate = tile.PullInput(this, 8, stop) as Weather.ClimateBaselineProduct;
            if (stop != null && stop.StopRequested) return null;
            if (climate != null) tile.outputs.climateBaseline = climate;
            var regions = tile.PullInput(this, 9, stop) as Weather.WeatherRegionProduct;
            if (stop != null && stop.StopRequested) return null;
            if (regions != null) tile.outputs.weatherRegions = regions;

            Matrix precip = GetInputMatrix(tile, 0, stop);
            if (stop != null && stop.StopRequested) return null;
            Matrix temperature = GetInputMatrix(tile, 1, stop);
            if (stop != null && stop.StopRequested) return null;
            Matrix wind = GetInputMatrix(tile, 2, stop);
            if (stop != null && stop.StopRequested) return null;
            Matrix fog = GetInputMatrix(tile, 3, stop);
            if (stop != null && stop.StopRequested) return null;
            Matrix cloud = GetInputMatrix(tile, 4, stop);
            if (stop != null && stop.StopRequested) return null;
            Matrix snow = GetInputMatrix(tile, 5, stop);
            if (stop != null && stop.StopRequested) return null;
            Matrix storm = GetInputMatrix(tile, 6, stop);
            if (stop != null && stop.StopRequested) return null;
            Matrix turbidity = GetInputMatrix(tile, 7, stop);
            if (stop != null && stop.StopRequested) return null;

            // The sample grid comes from the first wired port; a node with nothing
            // wired has no weather branch to record and leaves the bag as it found it.
            Matrix shape = FirstNonNull(precip, temperature, wind, fog, cloud, snow, storm, turbidity);
            if (shape == null) return null;

            int w = shape.rect.size.x;
            int h = shape.rect.size.z;
            if (w < 2 || h < 2) return null;
            int n = w * h;

            tile.outputs.weatherPrecipBias      = CopyBias(precip, n);
            tile.outputs.weatherTemperatureBias = CopyBias(temperature, n);
            tile.outputs.weatherWindBias        = CopyBias(wind, n);
            tile.outputs.weatherFogBias         = CopyBias(fog, n);
            tile.outputs.weatherCloudBias       = CopyBias(cloud, n);
            tile.outputs.weatherSnowBias        = CopyBias(snow, n);
            tile.outputs.weatherStormBias       = CopyBias(storm, n);
            tile.outputs.weatherTurbidityBias   = CopyBias(turbidity, n);

            tile.outputs.weatherBiasSize      = new Vector2Int(w, h);
            tile.outputs.weatherBiasWorldMin  = new Vector2(tile.worldPos.x, tile.worldPos.z);
            tile.outputs.weatherBiasWorldSize = new Vector2(tile.worldSize.x, tile.worldSize.z);

            return null;
        }

        /// <summary>
        /// Copies one bias field to a flat array, or null when the port is unwired or
        /// its field does not fit the shared grid. Non-finite samples become zero so a
        /// stray NaN upstream cannot poison the whole chunk's weather.
        /// </summary>
        private static float[] CopyBias(Matrix m, int n)
        {
            if (m == null || m.arr == null || m.arr.Length < n) return null;

            float[] o = new float[n];
            for (int i = 0; i < n; i++)
            {
                float v = m.arr[i];
                o[i] = (float.IsNaN(v) || float.IsInfinity(v)) ? 0f : v;
            }
            return o;
        }

        /// <summary>The first non-null matrix, or null when every port is unwired.</summary>
        private static Matrix FirstNonNull(params Matrix[] ms)
        {
            for (int i = 0; i < ms.Length; i++)
                if (ms[i] != null) return ms[i];
            return null;
        }
    }
}
