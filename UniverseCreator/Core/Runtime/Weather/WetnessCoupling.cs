// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    WetnessCoupling.cs
//  Purpose: The one place the world's wetness reaches the GPU. Specular gain,
//           albedo darkening and normal flattening are not three authored dials -
//           they are three faces of a single number, how wet the surface is, and
//           this is the single component that publishes that number. Terrain,
//           foliage and rock all read the same global and each applies its own
//           authored response (a species' wetDarkening, a rock's, the terrain's),
//           so one upload drives the whole world without a second material path.
//
//  WHY A SINGLE UPLOADER
//  ---------------------
//  Two components writing the same wetness global is how the ground and the grass
//  standing on it end up disagreeing about whether it is raining. There is exactly
//  one writer, and this is it; everything that used to set the wetness global now
//  routes through here.
//
//  DEPENDENCY DIRECTION
//  --------------------
//  Weather only. It takes scalars and sets a shader global - it holds no reference
//  to the ecology layer, the terrain module or any material. The shaders read the
//  global; nothing here reaches back at them.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Weather
{
    /// <summary>
    /// The single CPU-side uploader of surface wetness to the shaders. Publishes the
    /// shipped <c>_UCWetParams</c> global that UC_Season.hlsl and the terrain shader read.
    /// </summary>
    public static class WetnessCoupling
    {
        /// <summary>The shipped wetness global: x = wetness, y = rain intensity, z = humidity, w = live flag.</summary>
        public static readonly int WetParamsId = Shader.PropertyToID("_UCWetParams");

        /// <summary>
        /// Publishes the world's wetness for the frame. All three inputs are clamped to
        /// 0..1 here so no caller can push an out-of-range value at the GPU.
        /// </summary>
        public static void Publish(float wetness01, float rainIntensity01, float humidity01)
        {
            Shader.SetGlobalVector(WetParamsId, new Vector4(
                Mathf.Clamp01(wetness01),
                Mathf.Clamp01(rainIntensity01),
                Mathf.Clamp01(humidity01),
                1f));
        }

        /// <summary>
        /// Clears the wetness global to a dry, still world. This is what a material reads
        /// when no weather is present, so an unconfigured scene renders dry rather than broken.
        /// </summary>
        public static void Clear()
        {
            Shader.SetGlobalVector(WetParamsId, Vector4.zero);
        }
    }
}
