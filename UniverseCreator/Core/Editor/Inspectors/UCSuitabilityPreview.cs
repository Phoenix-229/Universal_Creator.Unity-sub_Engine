// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    UCSuitabilityPreview.cs
//  Purpose: V-50. Turns fifty-five numbers into a picture. Two response curves -
//           suitability against altitude, and against soil moisture - drawn at the
//           current world's latitude, so an author can see that their spruce
//           cannot grow anywhere before they spend a rebuild finding out.
//
//  IT IS A RESPONSE CURVE, NOT A PREDICTION OF A REAL SITE
//  ------------------------------------------------------
//  This is the standard way an envelope model is presented: vary one axis, hold
//  the rest at the species' own optimum, and read off the tolerance along that
//  axis. It is deliberately NOT the joint answer for a real place - a real site
//  has a slope, an aspect and a wetness index all disagreeing at once, and the
//  curve would then be near zero everywhere and tell the author nothing.
//
//  So: soil depth, rockiness, salt, wind, snow load and browse are set to no
//  stress; slope, wetness index, fertility and curvature are held at what THIS
//  species asks for; light is full daylight with no competitors.
//
//  WHAT IS NOT HELD AT OPTIMUM, AND WHY THAT IS THE WHOLE POINT
//  -----------------------------------------------------------
//  Temperature. Altitude does not merely correlate with warmth, it CAUSES it
//  through the lapse rate, so on the altitude axis the temperature is derived -
//  mean, warmest month, coldest month, frost-free share, growing degree days and
//  the whole water balance follow from the altitude being previewed and the
//  world's own latitude. That is what makes the curve answer a question about
//  THIS world rather than about the species in the abstract, and it is why a
//  treeline appears in the picture without anyone authoring one.
//
//  EVERY NUMBER COMES FROM THE SHIPPED MODEL
//  -----------------------------------------
//  MeanTempAtC, LocalTempHalfAmplitudeC, FrostFreeFractionFromExtremes,
//  GrowingDegreeDays, HargreavesPetMmPerDay, AridityIndex and Evaluate are all
//  called here exactly as EnvironmentSampler calls them, in the same order, from
//  the same resolved EcoSampleSettings. There is no second climate model in this
//  file and there must never be one: a preview that disagrees with the generator
//  is worse than no preview, because it is believed.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEditor;
using UniverseCreator.Ecology;
using UniverseCreator.Nodes;   // WaterBodyKind

namespace UniverseCreator.Editor.Inspectors
{
    /// <summary>
    /// Builds physically-derived preview sites and draws suitability response
    /// curves for one species envelope.
    /// </summary>
    public static class UCSuitabilityPreview
    {
        /// <summary>Samples across a curve. Enough to show a shoulder; the two loops
        /// of this many climate evaluations are memoized per envelope (see Draw), so
        /// the cost is paid on a content change, not on every repaint.</summary>
        private const int Samples = 72;

        /// <summary>Height of one curve box, in points.</summary>
        private const float CurveHeight = 46f;

        /// <summary>
        /// How maritime the preview site is, as a share of the water search radius.
        /// A mid value rather than 0 or 1: at 0 every world would preview as a mild
        /// coast and hide its own continental winter, and at 1 an island world would
        /// preview a winter it does not have. Half is the honest middle, and it is
        /// stated here rather than hidden so a coastal author knows to read the
        /// warmest and coldest month readouts instead.
        /// </summary>
        private const float ShoreFractionMid = 0.5f;

        /// <summary>Soil depth for the preview site, in metres. Deep enough to satisfy any envelope.</summary>
        private const float PreviewSoilDepthM = 25f;

        private static readonly Color CurveFill = new Color(0.28f, 0.70f, 0.32f, 0.30f);
        private static readonly Color CurveLine = new Color(0.42f, 0.88f, 0.46f, 1.00f);
        private static readonly Color CurveBack = new Color(0.16f, 0.17f, 0.18f, 1.00f);
        private static readonly Color CurveGrid = new Color(1f, 1f, 1f, 0.07f);
        private static readonly Color CurveDead = new Color(0.85f, 0.35f, 0.32f, 0.55f);

        // ---------------------------------------------------------------------
        //  Public drawing
        // ---------------------------------------------------------------------

        /// <summary>
        /// Draws both response curves plus the one-line verdict under them.
        /// </summary>
        /// <param name="topAltitudeM">Metres above sea level at the right edge of
        /// the altitude curve. The world's own usable relief, so the curve spans
        /// what this world actually has rather than a made-up range.</param>
        public static void Draw(EcoEnvelope eco, in EcoSampleSettings settings,
                                float topAltitudeM)
        {
            if (eco == null) return;

            if (!eco.enabled)
            {
                EditorGUILayout.HelpBox(
                    "This species is disabled, so it scores zero everywhere and no "
                  + "curve is drawn. Enable it to preview its envelope.",
                    MessageType.Info);
                return;
            }

            float span = Mathf.Max(1f, topAltitudeM);

            // The 144+ climate evaluations behind the two curves are the whole cost of
            // this preview, and this panel is now hosted inside the Vegetation manager
            // inspector where a repaint fires on hover and on every open species foldout
            // at once (V-53). So the sample loops are MEMOIZED per envelope: recomputed
            // only when a cheap content signature (the envelope's own fields, the resolved
            // settings, and the span) changes, and reused as-is on every other repaint.
            // The picture is never stale - editing this species changes the signature and
            // forces the recompute - but hovering, or editing a DIFFERENT species, no
            // longer re-runs this species' climate model. (Step 31)
            CurveCache curves = ResolveCurves(eco, in settings, span);

            DrawCurve("Altitude  0 - " + curves.span.ToString("0") + " m",
                      curves.byAltitude, curves.bestAltitude);
            DrawCurve("Soil moisture  0 - 1", curves.byMoisture, curves.bestMoisture);

            DrawVerdict(eco, in settings, curves.span,
                        curves.bestAltitude, curves.bestAltitudeIndex, curves.bestMoisture);
        }

        // ---------------------------------------------------------------------
        //  Memoized curve computation (Step 31)
        // ---------------------------------------------------------------------

        /// <summary>The computed curves for one envelope, kept between repaints.</summary>
        private sealed class CurveCache
        {
            public long signature;
            public float span;
            public float[] byAltitude;
            public float[] byMoisture;
            public float bestAltitude;
            public int bestAltitudeIndex;
            public float bestMoisture;
        }

        // Keyed by envelope identity so an entry dies with its envelope - no manual
        // eviction, no leak when a library shrinks or a scene closes.
        private static readonly ConditionalWeakTable<EcoEnvelope, CurveCache> Cache =
            new ConditionalWeakTable<EcoEnvelope, CurveCache>();

        // Public instance fields per type, resolved once. The reflection is only for
        // building the change signature, never for the drawing itself.
        private static readonly Dictionary<System.Type, FieldInfo[]> FieldCache =
            new Dictionary<System.Type, FieldInfo[]>();

        /// <summary>
        /// Returns the cached curves for this envelope, recomputing the sample loops
        /// only when the content signature has moved since the last draw.
        /// </summary>
        private static CurveCache ResolveCurves(EcoEnvelope eco, in EcoSampleSettings settings,
                                                float span)
        {
            long sig = Signature(eco, in settings, span);

            CurveCache c;
            if (Cache.TryGetValue(eco, out c) && c.signature == sig)
                return c;   // unchanged - skip the climate evaluations entirely.

            if (c == null)
            {
                c = new CurveCache
                {
                    byAltitude = new float[Samples],
                    byMoisture = new float[Samples]
                };
                Cache.Add(eco, c);
            }

            Compute(eco, in settings, span, c);
            c.signature = sig;
            return c;
        }

        /// <summary>
        /// The two sample loops that were the body of Draw. Writes straight into the
        /// cache entry so the arrays are reused rather than reallocated each recompute.
        /// </summary>
        private static void Compute(EcoEnvelope eco, in EcoSampleSettings settings,
                                    float span, CurveCache c)
        {
            c.span = span;

            // Altitude axis: climate is derived from the altitude being sampled.
            float bestAltitude = 0f;
            int bestAltitudeIndex = -1;
            for (int i = 0; i < Samples; i++)
            {
                float alt = span * i / (Samples - 1);
                EnvironmentSample site = BuildSite(eco, in settings, alt, eco.moistureOptimum);
                float v = SpeciesSuitability.Evaluate(eco, in site);
                c.byAltitude[i] = v;
                if (v > bestAltitude) { bestAltitude = v; bestAltitudeIndex = i; }
            }
            c.bestAltitude = bestAltitude;
            c.bestAltitudeIndex = bestAltitudeIndex;

            // Moisture axis: altitude held at the middle of the world's relief, so the
            // temperature behind the curve is this world's mid-slope climate.
            float bestMoisture = 0f;
            for (int i = 0; i < Samples; i++)
            {
                float wet = (float)i / (Samples - 1);
                EnvironmentSample site = BuildSite(eco, in settings, span * 0.5f, wet);
                float v = SpeciesSuitability.Evaluate(eco, in site);
                c.byMoisture[i] = v;
                if (v > bestMoisture) bestMoisture = v;
            }
            c.bestMoisture = bestMoisture;
        }

        /// <summary>
        /// A 64-bit content signature over everything the curves depend on: the
        /// envelope's own fields, the resolved climate settings, and the altitude span.
        /// Reflection over the public fields rather than a hand-listed set, so a field
        /// added to the envelope or the settings later cannot silently freeze a stale
        /// preview. Cheap next to one climate Evaluate, let alone 144 of them.
        /// </summary>
        private static long Signature(EcoEnvelope eco, in EcoSampleSettings settings, float span)
        {
            long h = 1469598103934665603L;           // FNV-1a 64-bit offset basis
            h = Mix(h, HashFields(eco));
            EcoSampleSettings s = settings;           // local copy to box once for reflection
            h = Mix(h, HashFields(s));
            h = Mix(h, span.GetHashCode());
            return h;
        }

        private static long Mix(long h, int value)
        {
            return (h ^ (uint)value) * 1099511628211L; // FNV-1a 64-bit prime
        }

        /// <summary>
        /// Combines the hash codes of an object's public instance fields. The envelope
        /// and settings hold only value-type fields (floats, bools, enums, Vector2),
        /// whose GetHashCode is content-based, so equal content hashes equal.
        /// </summary>
        private static int HashFields(object obj)
        {
            if (obj == null) return 0;

            System.Type t = obj.GetType();
            FieldInfo[] fields;
            if (!FieldCache.TryGetValue(t, out fields))
            {
                fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance);
                FieldCache[t] = fields;
            }

            int h = 17;
            for (int i = 0; i < fields.Length; i++)
            {
                object v = fields[i].GetValue(obj);
                h = h * 31 + (v == null ? 0 : v.GetHashCode());
            }
            return h;
        }

        /// <summary>
        /// The one line that saves the rebuild. Says where the species peaks, or
        /// which gate shut it out of the whole world.
        /// </summary>
        private static void DrawVerdict(EcoEnvelope eco, in EcoSampleSettings settings,
                                        float span,
                                        float bestAltitude, int bestAltitudeIndex,
                                        float bestMoisture)
        {
            float best = Mathf.Max(bestAltitude, bestMoisture);

            if (best <= 0f)
            {
                EditorGUILayout.HelpBox(
                    "Scores zero at every altitude and every moisture in this world. "
                  + Blocker(eco, in settings, span),
                    MessageType.Warning);
                return;
            }

            float peakM = bestAltitudeIndex >= 0
                        ? span * bestAltitudeIndex / (Samples - 1)
                        : 0f;

            string line = "Peaks at " + best.ToString("0.00")
                        + " around " + peakM.ToString("0") + " m above sea level.";

            // Below any usable threshold the species is present in the list and
            // absent from the world, which is the confusing case worth naming.
            if (best < 0.2f)
            {
                EditorGUILayout.HelpBox(
                    line + " That is below the suitability threshold most libraries "
                  + "use, so it will lose every draw it enters.",
                    MessageType.Warning);
                return;
            }

            EditorGUILayout.LabelField(" " + line, EditorStyles.miniLabel);
        }

        /// <summary>
        /// Which single gate rejects this species everywhere. Reported by testing
        /// the world's own extremes against the envelope's hard limits, in the same
        /// order Evaluate rejects them, so the answer names the first real cause
        /// rather than guessing.
        /// </summary>
        private static string Blocker(EcoEnvelope eco, in EcoSampleSettings settings,
                                     float span)
        {
            // Warmest ground is at sea level, coldest at the summit.
            EnvironmentSample low = BuildSite(eco, in settings, 0f, eco.moistureOptimum);
            EnvironmentSample high = BuildSite(eco, in settings, span, eco.moistureOptimum);

            if (low.warmestMonthTempC < eco.warmestMonthMinC)
            {
                return "Nowhere is warm enough: this world's warmest month reaches "
                     + low.warmestMonthTempC.ToString("0.0") + " degC at sea level and the "
                     + "species needs " + eco.warmestMonthMinC.ToString("0.0")
                     + " degC to grow wood.";
            }

            if (high.coldestMonthTempC < eco.tempLethalMinC && low.coldestMonthTempC < eco.tempLethalMinC)
            {
                return "Every altitude freezes past its lethal minimum: the coldest month "
                     + "reaches " + low.coldestMonthTempC.ToString("0.0")
                     + " degC even at sea level, below the stated "
                     + eco.tempLethalMinC.ToString("0.0") + " degC.";
            }

            if (low.warmestMonthTempC > eco.tempLethalMaxC && high.warmestMonthTempC > eco.tempLethalMaxC)
            {
                return "Everywhere is too hot: the warmest month reaches "
                     + high.warmestMonthTempC.ToString("0.0") + " degC at the summit, above "
                     + "the lethal maximum of " + eco.tempLethalMaxC.ToString("0.0") + " degC.";
            }

            if (low.aridity > eco.aridityMax && high.aridity > eco.aridityMax)
            {
                return "This world is wetter than the species admits: aridity index "
                     + low.aridity.ToString("0.00") + " against a stated maximum of "
                     + eco.aridityMax.ToString("0.00")
                     + ". The index is precipitation over PET, so a high value is wet.";
            }

            if (low.aridity < eco.aridityMin && high.aridity < eco.aridityMin)
            {
                return "This world is drier than the species admits: aridity index "
                     + low.aridity.ToString("0.00") + " against a stated minimum of "
                     + eco.aridityMin.ToString("0.00") + ".";
            }

            if (eco.gddMinRequired > 0f && low.gdd < eco.gddMinRequired)
            {
                return "Not enough accumulated warmth: the season delivers "
                     + low.gdd.ToString("0") + " growing degree days at sea level against "
                     + eco.gddMinRequired.ToString("0") + " required.";
            }

            if (low.frostFreeFraction < eco.frostFreeFractionMin)
            {
                return "The frost-free season is too short: "
                     + (low.frostFreeFraction * 100f).ToString("0") + "% of the year against "
                     + (eco.frostFreeFractionMin * 100f).ToString("0") + "% required.";
            }

            if (eco.useAbsoluteAltitude && eco.minAltitudeM > span)
            {
                return "Its absolute altitude band starts at " + eco.minAltitudeM.ToString("0")
                     + " m, above this world's highest ground at " + span.ToString("0") + " m.";
            }

            if (eco.tidalZone == TidalZone.Supratidal)
            {
                // The altitude lattice can step straight over the splash zone on a
                // world with real relief, so this is a preview limit and is named as
                // one rather than being reported as a broken envelope.
                float band = EcoConstants.TidalRangeMetres + EcoConstants.SplashZoneHeightMetres;
                return "It lives only in the splash zone, between "
                     + EcoConstants.TidalRangeMetres.ToString("0.0") + " and "
                     + band.ToString("0.0") + " m above sea level. The altitude curve steps in "
                     + (span / (Samples - 1)).ToString("0.0")
                     + " m, so that band falls between two samples. Judge this one in the world.";
            }

            if (eco.submergedDepthRangeM.y > 0f
             && eco.submergedDepthRangeM.x > eco.submergedDepthRangeM.y)
            {
                return "Its submerged depth range is inverted: "
                     + eco.submergedDepthRangeM.x.ToString("0.0") + " m minimum is deeper than "
                     + eco.submergedDepthRangeM.y.ToString("0.0")
                     + " m maximum, so no depth satisfies it.";
            }

            if (eco.distanceToWaterMinM > eco.distanceToWaterMaxM && eco.distanceToWaterMaxM >= 0f)
            {
                return "Its shore-distance band is inverted: minimum "
                     + eco.distanceToWaterMinM.ToString("0") + " m is beyond maximum "
                     + eco.distanceToWaterMaxM.ToString("0") + " m, so no distance satisfies it.";
            }

            return "No single hard limit explains it, so it is the tolerance curves "
                 + "collapsing together. Widen the temperature or moisture tolerance.";
        }

        // ---------------------------------------------------------------------
        //  The preview site
        // ---------------------------------------------------------------------

        /// <summary>
        /// A site at one altitude and one soil moisture, with everything the
        /// species does not care about set to no stress and everything topographic
        /// held at what it asks for. The climate is derived, never assumed.
        /// </summary>
        private static EnvironmentSample BuildSite(EcoEnvelope eco, in EcoSampleSettings settings,
                                                   float altitudeM, float soilMoisture)
        {
            EnvironmentSample s = new EnvironmentSample();

            // The resolved settings already carry the world's latitude, so it is read
            // from there rather than passed in alongside them. One source, so the
            // preview can never state a latitude the generator did not use.
            s.latitudeDeg = settings.latitudeDeg;
            s.altitudeM = altitudeM;

            // ---- topography, held at what this species wants ----------------
            // Not flattery: the axis under test is climate, and a slope the species
            // dislikes would scale the whole curve down by a constant without
            // changing its shape, which teaches the author nothing and hides the
            // treeline they came here to find.
            s.slopeDeg = Mathf.Max(0f, eco.slopeOptimumDeg);
            s.twi = eco.twiOptimum;
            s.soilFertility = eco.soilFertilityOptimum;
            s.curvature = Mathf.Clamp(eco.curvaturePreference, -1f, 1f)
                        * EcoConstants.SoilDepthCurvatureSpanPerMetre;

            // Negative means "no aspect", which scores neutral rather than picking
            // a bearing that would flatter a south-facing species.
            s.aspectDeg = -1f;

            // ---- no stress ---------------------------------------------------
            s.soilDepthM = PreviewSoilDepthM;
            s.rockiness = 0f;
            s.saltFraction = 0f;
            s.windExposure = 0f;
            s.snowLoadKgPerM2 = 0f;
            s.browsePressure = 0f;
            s.waterTableDepthM = 0f;

            // ---- open ground, no competitors --------------------------------
            // All three layers at full daylight, so the light factor is 1 whichever
            // canopy layer the species occupies. A canopy species must not be
            // previewed under a canopy it has not grown yet.
            s.subcanopyLightFraction = 1f;
            s.canopyLightFraction = 1f;
            s.floorLightFraction = 1f;
            s.canopyLai = 0f;

            // ---- shore ------------------------------------------------------
            // Sitting exactly at the near edge of the band satisfies it, since the
            // gate rejects below the minimum rather than at it.
            s.distanceToWaterM = Mathf.Max(0f, eco.distanceToWaterMinM);
            s.hasWaterInRange = true;

            // WaterBodyKind has no "none" member - hasWaterInRange above is what
            // carries absence - and the ocean is the only kind the tidal gate
            // admits. So an ocean shore is stated unconditionally: a species with a
            // tidal zone previews in the zone it asks for, and a species without one
            // never reads this field, because the gate returns early for it.
            s.nearestWaterKind = WaterBodyKind.Ocean;

            // An aquatic species is previewed at the shallow end of the water it
            // needs, which is the same "hold it at what this species asks for"
            // convention the topography above follows. Zero would drown the whole
            // curve for every aquatic species and report a defect that is not there.
            s.submergedDepthM = eco.submergedDepthRangeM.y > 0f
                              ? eco.submergedDepthRangeM.x
                              : 0f;

            // ---- climate, derived exactly as EnvironmentSampler derives it ---
            s.meanAnnualTempC = settings.MeanTempAtC(altitudeM);

            float halfAmplitude = settings.LocalTempHalfAmplitudeC(ShoreFractionMid);
            s.warmestMonthTempC = s.meanAnnualTempC + halfAmplitude;
            s.coldestMonthTempC = s.meanAnnualTempC - halfAmplitude;

            s.frostFreeFraction = EcoMath.FrostFreeFractionFromExtremes(
                s.coldestMonthTempC, s.warmestMonthTempC);

            float seasonDays = s.frostFreeFraction * EcoConstants.DaysPerTropicalYear;
            s.gdd = EcoMath.GrowingDegreeDays(
                s.meanAnnualTempC, EcoConstants.GddBaseTemperateC, seasonDays);

            // ---- water balance ----------------------------------------------
            // The neutral moisture mask, so precipitation is the world's authored
            // annual figure rather than a wetter or drier corner of it.
            s.annualPrecipMm = settings.annualPrecipMm;

            float halfDiurnal = settings.diurnalTempRangeC * 0.5f;
            float petPerDay = EcoMath.HargreavesPetMmPerDay(
                s.meanAnnualTempC - halfDiurnal,
                s.meanAnnualTempC + halfDiurnal,
                s.meanAnnualTempC,
                settings.radiationMjPerM2Day);

            s.petMm = petPerDay * EcoConstants.DaysPerTropicalYear;
            s.aridity = EcoMath.AridityIndex(s.annualPrecipMm, s.petMm);

            // The axis under test, not inferred from the balance above.
            s.soilMoisture = Mathf.Clamp01(soilMoisture);

            return s;
        }

        // ---------------------------------------------------------------------
        //  Curve rendering
        // ---------------------------------------------------------------------

        /// <summary>
        /// One filled sparkline, 0 at the bottom and 1 at the top, with a midline
        /// so a peak can be read as above or below half without a scale.
        /// </summary>
        private static void DrawCurve(string label, float[] values, float peak)
        {
            EditorGUILayout.LabelField(label, EditorStyles.miniLabel);

            Rect box = GUILayoutUtility.GetRect(
                10f, CurveHeight, GUILayout.ExpandWidth(true));

            // Layout runs twice per repaint and only the repaint pass has real
            // geometry, so drawing in the layout pass would draw into a zero rect.
            if (Event.current.type != EventType.Repaint) return;

            EditorGUI.DrawRect(box, CurveBack);

            // Midline at 0.5, the only reference an author needs to read a peak.
            Rect mid = new Rect(box.x, box.y + box.height * 0.5f, box.width, 1f);
            EditorGUI.DrawRect(mid, CurveGrid);

            if (values == null || values.Length < 2) return;

            float step = box.width / (values.Length - 1);

            // Column fill rather than a polyline: a filled area reads as "how much
            // of this range works" at a glance, which is the question being asked,
            // and it needs no Handles or a material to draw.
            for (int i = 0; i < values.Length; i++)
            {
                float v = Mathf.Clamp01(values[i]);
                float x = box.x + step * i;
                float w = Mathf.Max(1f, step + 1f);

                if (v <= 0f)
                {
                    // A dead band is worth seeing. One thin mark along the floor,
                    // so "impossible here" does not look like "not drawn yet".
                    EditorGUI.DrawRect(new Rect(x, box.yMax - 2f, w, 2f), CurveDead);
                    continue;
                }

                float h = v * (box.height - 2f);
                EditorGUI.DrawRect(new Rect(x, box.yMax - h, w, h), CurveFill);
                EditorGUI.DrawRect(new Rect(x, box.yMax - h, w, 1.5f), CurveLine);
            }

            if (peak <= 0f) return;

            // The peak value, drawn inside the box so the curve carries its own
            // scale and the author does not have to hover anything.
            Rect text = new Rect(box.x + 3f, box.y + 1f, box.width - 6f, 14f);
            GUI.Label(text, "peak " + peak.ToString("0.00"), EditorStyles.miniLabel);
        }
    }
}
