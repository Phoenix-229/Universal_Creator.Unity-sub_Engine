// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    OverlayTessellationSupport.cs
//  Purpose: Decides, once, whether this GPU can run the hull+domain displacement path
//           or must take the pre-subdivided fallback - and says so in words the
//           inspector can show. Hardware tessellation needs a shader model with hull
//           and domain stages (SM 4.6+, i.e. Vulkan / Metal / D3D11+ / GLES 3.2), and a
//           real fraction of the mid-range Android fleet has none of them. A shader
//           that silently fails there takes the ground with it, so the ring asks this
//           class at initialisation and, when the answer is no, draws a denser
//           non-tessellated patch mesh through the shader's fallback sub-shader instead.
//
//  WHY A SEPARATE DECISION, READ ONCE  (C-13)
//  ------------------------------------------
//  The capability never changes while the app runs, so querying it per frame would be
//  waste; querying it per patch would be worse. It is read once when the ring is built
//  and cached as a path plus a human-readable reason. The reason string is not
//  decoration: the done-when requires the inspector to state which path is active, so
//  an author staring at flat ground on a test device learns why from the toolkit
//  rather than from a forum thread three days later.
// -----------------------------------------------------------------------------

using UnityEngine.Rendering;

namespace UniverseCreator.Overlay
{
    /// <summary>Which geometry path the overlay ring is drawing, for the inspector to report.</summary>
    public enum OverlayTessPath { HardwareTessellation, PreSubdividedFallback }

    /// <summary>
    /// Pure capability decision for the micro-relief overlay: hardware tessellation when the shader
    /// model supports hull+domain stages, the pre-subdivided fallback otherwise. Takes its inputs as
    /// arguments so it is testable without a device; a live convenience reads them from SystemInfo.
    /// </summary>
    public static class OverlayTessellationSupport
    {
        /// <summary>Shader level (10 = SM x.y as level x*10+y) at which hull/domain stages exist: SM 4.6.</summary>
        public const int TessellationShaderLevel = 46;

        /// <summary>Vertices per edge of the fallback patch mesh - a fixed, moderate density that shows
        /// displaced relief without the distance-scaled subdivision the hardware path gets.</summary>
        public const int FallbackSubdivisionResolution = 33;

        /// <summary>
        /// True where the hull+domain path is safe to use: the overlay shader compiled at all, the shader
        /// level reaches SM 4.6, and the device is a real rendering device (not the Null/headless one).
        /// The shader-level test is the true tessellation gate - GLES 3.2 clears it and GLES 3.0 does not -
        /// so it, not a device-type allow-list that would rot as new platforms appear, makes the decision.
        /// </summary>
        public static bool SupportsHardwareTessellation(GraphicsDeviceType deviceType, int graphicsShaderLevel, bool overlayShaderSupported)
        {
            if (!overlayShaderSupported) return false;      // the shader will not run here at all
            if (deviceType == GraphicsDeviceType.Null) return false; // headless/batch - nothing to tessellate
            return graphicsShaderLevel >= TessellationShaderLevel;
        }

        /// <summary>The path to draw, given the capability inputs.</summary>
        public static OverlayTessPath DecidePath(GraphicsDeviceType deviceType, int graphicsShaderLevel, bool overlayShaderSupported)
        {
            return SupportsHardwareTessellation(deviceType, graphicsShaderLevel, overlayShaderSupported)
                ? OverlayTessPath.HardwareTessellation
                : OverlayTessPath.PreSubdividedFallback;
        }

        /// <summary>A one-line reason for the chosen path, for the inspector to show verbatim.</summary>
        public static string DescribePath(GraphicsDeviceType deviceType, int graphicsShaderLevel, bool overlayShaderSupported)
        {
            if (!overlayShaderSupported)
                return "Pre-subdivided fallback: the overlay shader is not supported on this GPU.";
            if (deviceType == GraphicsDeviceType.Null)
                return "Pre-subdivided fallback: headless (Null) device, no tessellation.";
            if (graphicsShaderLevel >= TessellationShaderLevel)
                return $"Hardware tessellation: {deviceType}, shader level {graphicsShaderLevel} (>= {TessellationShaderLevel}).";
            return $"Pre-subdivided fallback: {deviceType}, shader level {graphicsShaderLevel} (< {TessellationShaderLevel}); "
                 + $"drawing a fixed {FallbackSubdivisionResolution}x{FallbackSubdivisionResolution} patch mesh.";
        }
    }
}

