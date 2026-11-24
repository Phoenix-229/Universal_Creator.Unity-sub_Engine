// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    BrushInputModel.cs
//  Purpose: The deterministic input kernel of the paint scene tool (§ 9.8 G-55): how a
//           scroll wheel and its modifier map to brush size and strength, and where one
//           "stroke group" begins and ends for the undo stack. The tool itself - the
//           SceneView gizmo, the live preview, the C-14 queue onto a mid-build chunk, the
//           `Undo.RegisterCompleteObjectUndo` call - is Editor-and-scene wiring, deferred to
//           the topic gate. This class is the part that can be pinned without a SceneView:
//           the arithmetic that must feel right and never runs away to zero or infinity.
//
//  SIZE SCROLLS MULTIPLICATIVELY, STRENGTH ADDITIVELY
//  --------------------------------------------------
//  Brush size scales by a factor per scroll notch (a fixed number of metres per notch feels
//  glacial when the brush is huge and twitchy when it is tiny), clamped to [min, max] metres.
//  Strength moves by a fixed step per notch, clamped to [0, 1]. A held modifier decides which
//  of the two the wheel drives, so one wheel serves both without a dialog (C-08).
//
//  A STROKE GROUP IS ONE PRESS-TO-RELEASE
//  --------------------------------------
//  Undo granularity is the drag, not the frame: `IsStrokeGroupStart` fires on the frame the
//  pointer goes down, which is the single point where the tool registers a complete-object
//  undo before mutating the store. Pure and device-free; the self-test pins the clamps, the
//  modifier routing and the group boundary.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem.Geometry
{
    /// <summary>Which brush property the scroll wheel drives this frame.</summary>
    public enum BrushScrollTarget { Size = 0, Strength = 1 }

    /// <summary>
    /// The deterministic input arithmetic of the paint scene tool (§ 9.8 G-55): multiplicative size scroll,
    /// additive strength scroll, modifier routing, and the press-to-release stroke-group boundary that gates the
    /// undo registration. Device-free; the SceneView tool that consumes it is the deferred scene layer.
    /// </summary>
    public static class BrushInputModel
    {
        /// <summary>Which property the wheel drives: the modifier (e.g. Shift) switches from size to strength.</summary>
        public static BrushScrollTarget ResolveTarget(bool modifierHeld)
        {
            return modifierHeld ? BrushScrollTarget.Strength : BrushScrollTarget.Size;
        }

        /// <summary>Adjust brush size multiplicatively: size · factor^notches, clamped to [min, max] metres. A
        /// positive notch grows the brush; a factor at or below zero is treated as no change.</summary>
        public static float AdjustSize(float currentSizeM, float scrollNotches, float minSizeM, float maxSizeM,
                                       float factorPerNotch = 1.1f)
        {
            if (factorPerNotch <= 0f) return Clamp(currentSizeM, minSizeM, maxSizeM);
            float scaled = currentSizeM * Mathf.Pow(factorPerNotch, scrollNotches);
            return Clamp(scaled, minSizeM, maxSizeM);
        }

        /// <summary>Adjust brush strength additively: strength + step·notches, clamped to [0, 1].</summary>
        public static float AdjustStrength(float currentStrength01, float scrollNotches, float stepPerNotch = 0.05f)
        {
            return Clamp(currentStrength01 + stepPerNotch * scrollNotches, 0f, 1f);
        }

        /// <summary>The frame a stroke group begins: the pointer was up last frame and is down this frame. This is
        /// the single point at which the tool registers a complete-object undo before mutating the store.</summary>
        public static bool IsStrokeGroupStart(bool pointerDownPrev, bool pointerDownNow)
        {
            return !pointerDownPrev && pointerDownNow;
        }

        /// <summary>The frame a stroke group ends: the pointer was down last frame and is up this frame.</summary>
        public static bool IsStrokeGroupEnd(bool pointerDownPrev, bool pointerDownNow)
        {
            return pointerDownPrev && !pointerDownNow;
        }

        private static float Clamp(float v, float lo, float hi)
        {
            if (hi < lo) { float t = lo; lo = hi; hi = t; }
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }
}
