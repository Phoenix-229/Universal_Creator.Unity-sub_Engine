// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    ViewSideDegradation.cs
//  Purpose: Every degradation step in § 9.10 is a VIEW-SIDE change and nothing more (G-67, C-04,
//           G-17). Pixel error, ring size, impostor and shadow distances, cache range, whether a
//           shader stage runs - these change what the renderer DOES, never what the world IS. If a
//           degradation step could alter heights, splats, holes or trees, the world would depend on
//           how hot the phone got, and two players on the same seed would no longer share a world.
//           So the applied step touches only a view-side state and is structurally incapable of
//           reaching TerrainData.
//
//  THE LAW IS STRUCTURAL, NOT A PROMISE
//  ------------------------------------
//  `Apply` takes and returns only a <see cref="ViewSideState"/> - it never sees a TerrainData model,
//  so it cannot write one. `WritesTerrainData` returns false for every step as the explicit encoding
//  of the law, and `TerrainDataFingerprint` gives the self-test a hash of the four world channels so
//  it can degrade to the bottom of the G-63 ladder and assert the fingerprint is bit-identical - the
//  done-when. Pure, device-free, integer-folded so the hash is deterministic (C-04).
// -----------------------------------------------------------------------------

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>The view-side knobs a degradation step may adjust (§ 9.10 G-67). Deliberately holds NOTHING that
    /// lives in TerrainData - no heights, splats, holes or trees - so a step applied to it cannot alter the world.</summary>
    public struct ViewSideState
    {
        public float AcuityPixels;            // terrain silhouette error, toward the tier cap
        public int   OverlayRingRows;         // § 9.5 overlay ring size in rows
        public float ImpostorSwitchDistanceM; // distance at which props swap to impostors
        public float PropShadowDistanceM;     // shadow distance for props (G-38)
        public int   CacheRangeRings;         // § 9.7 cache range in rings
        public bool  OverlayTessellationOn;   // overlay tessellation vs. G-24 pre-subdivided mesh
        public bool  OverlayEnabled;          // overlay drawn at all

        /// <summary>An undegraded starting state with generous view-side settings.</summary>
        public static ViewSideState Default => new ViewSideState
        {
            AcuityPixels = 1.0f,
            OverlayRingRows = 4,
            ImpostorSwitchDistanceM = 120f,
            PropShadowDistanceM = 80f,
            CacheRangeRings = 3,
            OverlayTessellationOn = true,
            OverlayEnabled = true,
        };
    }

    /// <summary>
    /// The view-side scope of § 9.10 degradation (G-67): applying a step touches only a <see cref="ViewSideState"/>,
    /// never TerrainData. Provides a deterministic fingerprint of the world channels so the self-test can prove the
    /// world is untouched across a full descent of the G-63 ladder. Device-free.
    /// </summary>
    public static class ViewSideDegradation
    {
        /// <summary>Whether a degradation step writes any TerrainData channel: always false. Every § 9.10 step is
        /// view-side (G-67); the world may not depend on device thermals (C-04, G-17).</summary>
        public static bool WritesTerrainData(DegradeStep step) => false;

        /// <summary>Apply one degradation step to the view-side state, returning the adjusted state. Takes and returns
        /// only view knobs - it has no TerrainData to write. Amounts are illustrative; the invariant under test is
        /// that ONLY view state changes.</summary>
        public static ViewSideState Apply(in ViewSideState state, DegradeStep step)
        {
            ViewSideState s = state;
            switch (step)
            {
                case DegradeStep.AcuityPixels:          s.AcuityPixels += 1.0f; break;                       // coarser silhouette
                case DegradeStep.OverlayRingRow:        s.OverlayRingRows = s.OverlayRingRows > 0 ? s.OverlayRingRows - 1 : 0; break;
                case DegradeStep.ImpostorSwitchDistance:s.ImpostorSwitchDistanceM *= 0.75f; break;            // impostors sooner
                case DegradeStep.PropShadowDistance:    s.PropShadowDistanceM *= 0.75f; break;                // shorter shadows
                case DegradeStep.CacheRangeRing:        s.CacheRangeRings = s.CacheRangeRings > 0 ? s.CacheRangeRings - 1 : 0; break;
                case DegradeStep.OverlayTessellation:   s.OverlayTessellationOn = false; break;               // fall back to G-24 mesh
                case DegradeStep.OverlayDisabled:       s.OverlayEnabled = false; break;
            }
            return s;
        }

        /// <summary>A deterministic hash of the four TerrainData channels (heights, splats, holes, trees). Two worlds
        /// with identical channels hash identically; any write changes the hash. Integer-folded for bit-stability
        /// across runs and machines (C-04). Null channels contribute a stable sentinel.</summary>
        public static int TerrainDataFingerprint(float[] heights, int[] splats, bool[] holes, int[] trees)
        {
            unchecked
            {
                int h = 17;
                h = Fold(h, heights);
                h = Fold(h, splats);
                h = Fold(h, holes);
                h = Fold(h, trees);
                return h;
            }
        }

        private static int Fold(int h, float[] a)
        {
            unchecked
            {
                if (a == null) return h * 31 + 0x9E37;
                h = h * 31 + a.Length;
                for (int i = 0; i < a.Length; i++)
                    h = h * 31 + a[i].GetHashCode();
                return h;
            }
        }

        private static int Fold(int h, int[] a)
        {
            unchecked
            {
                if (a == null) return h * 31 + 0x9E37;
                h = h * 31 + a.Length;
                for (int i = 0; i < a.Length; i++)
                    h = h * 31 + a[i];
                return h;
            }
        }

        private static int Fold(int h, bool[] a)
        {
            unchecked
            {
                if (a == null) return h * 31 + 0x9E37;
                h = h * 31 + a.Length;
                for (int i = 0; i < a.Length; i++)
                    h = h * 31 + (a[i] ? 1 : 0);
                return h;
            }
        }
    }
}
