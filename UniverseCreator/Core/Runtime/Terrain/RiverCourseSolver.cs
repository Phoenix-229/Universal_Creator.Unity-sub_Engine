// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    RiverCourseSolver.cs
//  Purpose: Turns a course somebody dragged roughly across a landscape into one
//           water could actually follow.
//
//  THE PROBLEM THIS SOLVES
//  -----------------------
//  Drawing a spline by eye in a scene view gets the intent right and the metres
//  wrong. The line reads as being in the valley from the camera angle it was
//  drawn from and is twenty metres up the side of it from anywhere else, and it
//  crosses two spurs on the way because the ground it crosses is not visible
//  from above. Dig a channel along that line and you get a trench cut across
//  hillsides with water climbing them.
//
//  So the drawn line is treated as intent, not as geometry. Build time runs it
//  through three passes:
//
//    1. VALLEY SEEK    slide each point sideways to the low ground it is near
//    2. DESCENT WALK   from the head down, never accept a step that climbs;
//                      where the direct step would, swing round the obstacle
//                      into whatever adjacent low ground does descend
//    3. TERMINATION    if the walk runs into ground with no way down and no
//                      water nearby, it stops there rather than climbing out
//
//  WHY THE WALK BEATS "JUST LOWER THE HEIGHTS"
//  -------------------------------------------
//  Forcing the surface profile monotonically downhill along a fixed line makes
//  the water stop climbing, but it does it by cutting the terrain deeper and
//  deeper to keep up - a hundred-metre-deep slot through the hill the line
//  happened to cross. Moving the line instead keeps the cut shallow, because
//  the line now goes round the hill the way water does.
//
//  HIGH GROUND IS THE ONLY THING THAT MATTERS
//  ------------------------------------------
//  On flat ground every direction descends about equally, so the walk barely
//  moves the line and the drawn course is honoured almost exactly. It is only
//  where the ground rises that the search has anything to choose between, which
//  is why a river drawn across a plain stays where it was put and one drawn
//  across a ridge visibly bends around it.
//
//  NO ALLOCATION IN THE INNER LOOPS
//  --------------------------------
//  Everything works in place on the caller's list. The passes run once per
//  build, on the main thread, over a few hundred points.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// Build-time course fitting for rivers. Stateless: every method takes the
    /// points and a ground sampler and returns what it worked out.
    /// </summary>
    public static class RiverCourseSolver
    {
        /// <summary>Reads world ground elevation at a world XZ. Supplied by the caller
        /// so this file never needs to know what a Terrain is.</summary>
        public delegate float GroundSampler(float worldX, float worldZ);

        /// <summary>
        /// Slides each point sideways onto the lowest ground within reach, across the
        /// course rather than along it.
        ///
        /// Across, not in a disc, for a specific reason: a disc search on a sloping
        /// valley floor walks every point downstream until they all pile up at the
        /// lowest point in the search radius, and the course collapses. Searching
        /// perpendicular to the direction of travel can only ever move a point to the
        /// side, so the course keeps its length and its shape and simply finds the
        /// bottom of the trench it is near.
        /// </summary>
        public static void SnapToValley(List<Vector3> points, GroundSampler ground,
                                        float searchMetres, float strength, int samples,
                                        float deadbandMetres = 0f)
        {
            if (points == null || points.Count < 2 || ground == null) return;

            float reach = Mathf.Max(0f, searchMetres);
            if (reach < 0.25f) return;

            float pull = Mathf.Clamp01(strength);
            if (pull <= 1e-4f) return;

            // A point is only pulled sideways when the valley floor it finds is lower
            // than where it already sits by MORE than this deadband - i.e. only when the
            // point has genuinely climbed a bank. On natural terrain there is almost
            // always SOME marginally lower cell within reach, so without a deadband every
            // point drifts toward the nearest micro-dip and a course that was already
            // seated on the thalweg (a D8-planned river, or a carefully drawn one) is
            // dragged into a serrated wander off its own line. With it, a well-seated
            // point stays exactly on its planned position and only a real hillside bow is
            // corrected. Zero (the default) keeps the original always-pull behaviour for
            // the wide fitToValley seek, where moving toward any lower ground is intended.
            float dead = Mathf.Max(0f, deadbandMetres);

            int half = Mathf.Clamp(samples, 2, 32);
            int n = points.Count;

            // Written to a second list so every point is judged against the original
            // course. Updating in place would let point i be pulled toward where
            // point i-1 has already moved to, which drags the whole course sideways.
            Vector3[] moved = new Vector3[n];

            for (int i = 0; i < n; i++)
            {
                Vector3 p = points[i];

                int prev = Mathf.Max(0, i - 1);
                int next = Mathf.Min(n - 1, i + 1);

                Vector2 dir = new Vector2(points[next].x - points[prev].x,
                                          points[next].z - points[prev].z);

                if (dir.sqrMagnitude < 1e-8f) { moved[i] = p; continue; }

                dir.Normalize();
                Vector2 side = new Vector2(-dir.y, dir.x);

                float hereHeight = ground(p.x, p.z);
                float bestOffset = 0f;
                float bestHeight = hereHeight;

                for (int s = -half; s <= half; s++)
                {
                    if (s == 0) continue;

                    float offset = reach * (s / (float)half);
                    float h = ground(p.x + side.x * offset, p.z + side.y * offset);

                    // Ties go to the smaller offset, because a flat valley floor
                    // should leave the drawn line where it is rather than jumping it
                    // to whichever end of the search the loop happened to reach last.
                    if (h < bestHeight - 1e-4f ||
                        (h < bestHeight + 1e-4f && Mathf.Abs(offset) < Mathf.Abs(bestOffset)))
                    {
                        bestHeight = h;
                        bestOffset = offset;
                    }
                }

                // Deadband: only correct a point that has genuinely climbed a bank -
                // the floor it found must be lower than where it sits by more than the
                // deadband. A point already on (or within the deadband of) the local low
                // is left exactly where the planner/designer put it, so a seated course
                // keeps its planned line instead of drifting into every micro-dip.
                if (hereHeight - bestHeight <= dead)
                {
                    moved[i] = p;
                    continue;
                }

                float take = bestOffset * pull;
                moved[i] = new Vector3(p.x + side.x * take, p.y, p.z + side.y * take);
            }

            for (int i = 0; i < n; i++) points[i] = moved[i];
        }

        /// <summary>
        /// Light XZ smoothing. The valley seek judges each point on its own, so a
        /// noisy valley floor can leave the course visibly serrated even though every
        /// individual point found real low ground.
        /// </summary>
        public static void Relax(List<Vector3> points, int passes)
        {
            if (points == null || points.Count < 3) return;

            int n = points.Count;

            for (int pass = 0; pass < passes; pass++)
            {
                // Ends held: they are the mouth and the head, and those are the two
                // places a designer is most likely to have meant exactly what they drew.
                for (int i = 1; i < n - 1; i++)
                {
                    Vector3 a = points[i - 1];
                    Vector3 b = points[i];
                    Vector3 c = points[i + 1];

                    points[i] = new Vector3((a.x + b.x * 2f + c.x) * 0.25f,
                                            b.y,
                                            (a.z + b.z * 2f + c.z) * 0.25f);
                }
            }
        }

        // =====================================================================
        //  MEANDER
        // =====================================================================

        /// <summary>
        /// Lets a river wander in natural meander loops across the flat ground it
        /// runs over, and leaves it straight where the ground is steep.
        /// </summary>
        /// <remarks>
        /// WHY THIS IS NOT "A SINE WIGGLE"
        /// A meander is not a decoration added to a straight line; it is what a river
        /// on a floodplain unavoidably becomes. The fastest water on the outside of any
        /// chance bend scours that bank - the cut-bank - while the slack water on the
        /// inside drops its sediment - the point-bar - so every bend deepens and the
        /// river snakes. Three things about that are measurable and are the whole of
        /// what this pass encodes, so nothing here is guessed:
        ///
        ///   WAVELENGTH scales with the channel. Across rivers from a ditch to the
        ///   Mississippi the loop wavelength is about eleven channel widths (Leopold &
        ///   Wolman). So the loops of a growing river lengthen downstream exactly as its
        ///   width does - the phase below is advanced by arc length over the LOCAL
        ///   wavelength, not a fixed one, which is why a headwater and its trunk carry
        ///   loops of different size on the same course.
        ///
        ///   THE SHAPE is a sine-generated curve, not a sine wave of position (Langbein
        ///   & Leopold): it is the DIRECTION that varies sinusoidally with distance, and
        ///   the path that produces has the fat, round loops of a real river rather than
        ///   the thin wiggle a positional sine draws. The Kinoshita skew term makes each
        ///   loop steeper on its downstream limb, which is the cut-bank/point-bar
        ///   asymmetry the report asked for - real meanders lean downstream, they are
        ///   not symmetric.
        ///
        ///   IT ONLY HAPPENS ON FLAT GROUND. A river in a gorge has no floodplain to
        ///   wander across; it is pinned to the valley floor. So the amplitude fades out
        ///   as the downstream grade rises and is gone above <paramref name="maxGrade"/>,
        ///   and every proposed offset is refused if it would push the centre line up
        ///   into ground more than a little above where it already is - a loop cannot
        ///   climb the valley side, which is precisely the fault ("wiggle everywhere,
        ///   even through steep terrain") this guards against.
        ///
        /// Runs in place, but every point is judged against the ORIGINAL line: moving a
        /// point and then reading its neighbour would let the loops feed on themselves.
        /// The head and the mouth are held, so a meander never drags a river off its
        /// source or off the body it empties into.
        /// </remarks>
        /// <param name="points">The fitted course, world space, head first. Modified.</param>
        /// <param name="ground">Ground elevation sampler, for the grade gate and the
        /// floodplain clamp.</param>
        /// <param name="widths">Per-point channel width in metres, index-aligned with
        /// <paramref name="points"/>. May be null, in which case
        /// <paramref name="defaultWidthMetres"/> sets the loop scale everywhere.</param>
        /// <param name="strength">0..1 overall amplitude. 0 leaves the course straight.</param>
        /// <param name="maxGrade">Downstream slope (fall per run) at and above which the
        /// reach is treated as confined and does not meander.</param>
        /// <param name="defaultWidthMetres">Width used where the series has none.</param>
        public static void Meander(List<Vector3> points, GroundSampler ground,
                                   IList<float> widths, float strength,
                                   float maxGrade, float defaultWidthMetres)
        {
            if (points == null || points.Count < 4 || ground == null) return;

            float amp = Mathf.Clamp01(strength);
            if (amp <= 1e-4f) return;

            // Leopold & Wolman: wavelength ~ 11 w. Peak lateral offset held to about
            // three widths, so the meander belt is a few channel widths across - a
            // lowland river, not an oxbow field. Kinoshita skew leans each loop
            // downstream. The clamp tolerance is how far a loop may rise into the bank
            // before it is refused, so it can round a low rise but not climb a wall.
            const float WavelengthWidths = 11f;
            const float AmplitudeWidths = 3f;
            const float PhaseSkew = 0.25f;
            const float ClampToleranceMetres = 0.5f;

            // The floodplain a meander lives on is roughly LEVEL across the flow, so a
            // loop may only slide to ground that is within tolerance of the centre in
            // EITHER direction. A one-sided guard (climb only) let a loop walk downhill
            // at full amplitude - three widths for a trunk - which is how a channel ends
            // up flowing on the SIDE of a hill instead of along its floor. The tolerance
            // grows a little with the channel so a wide river's genuinely broad
            // floodplain is not pinched to the headwater's, but stays a small band.

            int n = points.Count;
            float gradeCut = Mathf.Max(0.002f, maxGrade);

            Vector3[] moved = new Vector3[n];
            moved[0] = points[0];
            moved[n - 1] = points[n - 1];

            // Phase is advanced along the course by arc length over the LOCAL
            // wavelength, so a reach that has widened carries longer loops than the
            // headwater above it - the loops grow with the river.
            float phase = 0f;

            for (int i = 1; i < n - 1; i++)
            {
                Vector3 p = points[i];

                float dsx = points[i].x - points[i - 1].x;
                float dsz = points[i].z - points[i - 1].z;
                float ds = Mathf.Sqrt(dsx * dsx + dsz * dsz);

                float wLocal = defaultWidthMetres;
                if (widths != null && i < widths.Count && widths[i] > 0.05f) wLocal = widths[i];
                wLocal = Mathf.Max(0.5f, wLocal);

                float wavelength = Mathf.Max(4f, WavelengthWidths * wLocal);
                phase += 2f * Mathf.PI * ds / wavelength;

                // Mean flow from the original neighbours, so the offset is across the
                // river's actual travel and a point on a bend measures its own normal.
                Vector2 flow = new Vector2(points[i + 1].x - points[i - 1].x,
                                           points[i + 1].z - points[i - 1].z);
                if (flow.sqrMagnitude < 1e-8f) { moved[i] = p; continue; }

                flow.Normalize();
                Vector2 side = new Vector2(-flow.y, flow.x);

                // Confined reaches do not meander. Grade over this step; the loop
                // fades to nothing as the grade approaches maxGrade.
                float gHere = ground(p.x, p.z);
                float gNext = ground(points[i + 1].x, points[i + 1].z);
                float grade = ds > 1e-3f ? Mathf.Abs(gHere - gNext) / ds : 0f;
                float gradeGate = 1f - Mathf.Clamp01(grade / gradeCut);
                if (gradeGate <= 1e-3f) { moved[i] = p; continue; }

                float endWindow = EndTaper(i, n);

                // Sine-generated curve with a downstream skew: the fat, asymmetric loop
                // of a real river, not a symmetric positional sine.
                float wave = Mathf.Sin(phase + PhaseSkew * Mathf.Sin(phase));

                // The loop's UNTAPERED peak amplitude - width, meander strength and grade,
                // but NOT the end taper. The taper is applied last (below), deliberately
                // AFTER the floodplain cap, so the cap's release never lands inside the
                // taper ramp. This ordering is the whole trick: in the body the untapered
                // amplitude is a large near-constant (three channel widths), so the cap
                // holds flat at the belt all the way to the mouth, and the taper then scales
                // that already-flat, already-capped value smoothly to zero.
                float peakAmp = AmplitudeWidths * wLocal * amp * gradeGate;

                float levelBand = ClampToleranceMetres + wLocal * 0.05f;

                // How wide the LEVEL floodplain belt is here, measured to BOTH sides and
                // the narrower kept - the loop swings both ways, and the belt is a property
                // of the ground, not of which way this particular point happens to lean.
                // Each side is marched outward and the band crossing INTERPOLATED, so the
                // belt is a continuous function of the ground: no stepped snap between
                // neighbours (the old four-fraction search), and a rise OR a fall out of
                // the band ends the march, so the belt never licenses climbing the valley
                // wall or sliding down a hill flank.
                float reachOut = peakAmp * 1.5f + 1f;
                float beltPlus = MarchLevelReach(ground, p, side, +1f, gHere, levelBand, reachOut);
                float beltMinus = MarchLevelReach(ground, p, side, -1f, gHere, levelBand, reachOut);
                float belt = Mathf.Min(beltPlus, beltMinus);

                // Cap the AMPLITUDE against the belt with a smooth minimum, apply the end
                // taper, THEN take the wave. Because the capped-and-tapered amplitude and
                // the wave are each smooth functions of arc length, their product is smooth
                // - a confined reach reads as a genuinely smaller, rounded meander belt,
                // not a full-size loop clipped flat against the floodplain edge (which left
                // the faceted corner the author saw). A loop that already fits is not
                // shrunk: the smooth min equals the amplitude until it comes within a
                // channel-scaled margin of the belt. The taper multiplies a value that is
                // already flat through the body, so it eases the loops to zero at the mouth
                // and head with no ramp-rate jump.
                float mergeMargin = Mathf.Max(1f, wLocal * 0.1f);
                float cappedAmp = Mathf.Max(0f, SmoothMin(peakAmp, belt, mergeMargin));
                float allowed = cappedAmp * endWindow * wave;

                moved[i] = new Vector3(p.x + side.x * allowed, p.y, p.z + side.y * allowed);
            }

            for (int i = 0; i < n; i++) points[i] = moved[i];

            // Capping the AMPLITUDE against the floodplain (above) removes the confinement
            // corner from the BODY of the meander - there the capped amplitude is smooth
            // and amplitude x wave is a clean sine (measured second difference ~0.2 m).
            // What it leaves is a small spike where the floodplain cap transition lands
            // inside the END-TAPER: as endWindow sweeps the amplitude down toward the mouth,
            // it crosses the belt cap, and the ramp RATE jumps over a point or two while the
            // wave is near a crest. Reordering the taper to AFTER the cap (above) removes
            // that release corner at its source, so this relax is now only a light
            // denoiser for residual ground-sample jitter - it eases each point toward its
            // neighbours' midpoint, doing nothing to the smooth body or to a free loop's
            // amplitude (a point already at the midpoint does not move). Two passes: the
            // fewest that clears the jitter, kept low deliberately so a real meander in the
            // field is never smoothed down out of its own amplitude. Ends held - mouth and
            // head stay where the fit put them.
            Relax(points, 2);
        }

        /// <summary>
        /// Fades a per-point strength to zero over the first and last twelfth of a
        /// course, so a planform effect holds the head and the mouth exactly where the
        /// fit left them.
        /// </summary>
        /// <remarks>
        /// Smoothstepped, not linear. A linear ramp reaches full strength with a CORNER -
        /// its slope jumps from the ramp rate to zero at the point it meets the flat - and
        /// when it scales a meander amplitude that corner is a curvature spike in the path
        /// a step or two in from the mouth, a small facet exactly where the loops are
        /// meant to ease in. Smoothstep has zero slope at both ends, so the taper meets the
        /// flat with no corner and the amplitude grows into the course smoothly.
        /// </remarks>
        private static float EndTaper(int i, int n)
        {
            int fade = Mathf.Max(2, n / 12);
            float head = Mathf.Clamp01(i / (float)fade);
            float mouth = Mathf.Clamp01((n - 1 - i) / (float)fade);
            return Mathf.SmoothStep(0f, 1f, Mathf.Min(head, mouth));
        }

        /// <summary>
        /// A minimum with the corner rounded off over a width k - the polynomial smooth-min.
        /// Equal to Mathf.Min(a, b) once the two are more than k apart, and eased between
        /// them within k, so a value clamped against a limit approaches it without the sharp
        /// break a hard min leaves. Used to fit a meander loop to its floodplain width: the
        /// loop is its full amplitude while it fits and bends smoothly to the floodplain
        /// edge as it runs out of room, instead of clipping to a flat top with faceted
        /// corners. k of 0 falls back to the exact minimum.
        /// </summary>
        private static float SmoothMin(float a, float b, float k)
        {
            if (k <= 1e-4f) return Mathf.Min(a, b);
            float h = Mathf.Clamp01(0.5f + 0.5f * (b - a) / k);
            return Mathf.Lerp(b, a, h) - k * h * (1f - h);
        }

        /// <summary>
        /// How far the level floodplain reaches from a point in one across-flow direction:
        /// marches outward from the point until the ground first departs the given band
        /// above or below the point's own level, and returns the interpolated distance to
        /// that crossing. Marched a fixed number of small steps out to <paramref name="maxReach"/>;
        /// if the ground never leaves the band the full reach is returned. The crossing is
        /// interpolated between the last two samples so the reach is a continuous function
        /// of the ground - the meander cap that reads it does not step between samples.
        /// </summary>
        private static float MarchLevelReach(GroundSampler ground, Vector3 p, Vector2 side,
                                             float dir, float centreY, float band, float maxReach)
        {
            const int Steps = 24;
            float step = maxReach / Steps;
            float gPrev = centreY;
            for (int s = 1; s <= Steps; s++)
            {
                float r = step * s;
                float gh = ground(p.x + side.x * dir * r, p.z + side.y * dir * r);
                if (Mathf.Abs(gh - centreY) > band)
                {
                    float dPrev = Mathf.Abs(gPrev - centreY);
                    float dHere = Mathf.Abs(gh - centreY);
                    float t = dHere > dPrev ? Mathf.InverseLerp(dPrev, dHere, band) : 0f;
                    return step * ((s - 1) + Mathf.Clamp01(t));
                }
                gPrev = gh;
            }
            return maxReach;
        }

        // =====================================================================
        //  THE DESCENT WALK
        // =====================================================================

        /// <summary>Why a descent walk stopped where it did.</summary>
        public enum DescentEnding
        {
            /// <summary>It ran the whole drawn course and never had to give up.</summary>
            ReachedEnd = 0,

            /// <summary>It arrived at a body of water it could join.</summary>
            ReachedWater = 1,

            /// <summary>Every direction climbed. The river sinks here.</summary>
            NoWayDown = 2
        }

        /// <summary>
        /// Rewrites the course as a path that never climbs.
        ///
        /// It walks from the head, one step at a time, each step aiming at the next
        /// drawn point. If the direct step descends, it is taken and the drawn line is
        /// followed exactly - which is why a river drawn along a valley or across a
        /// plain comes out almost precisely where it was put. If the direct step
        /// climbs, the walk fans out sideways and takes the best descending direction
        /// it can find while still making progress toward the target. That is the
        /// swing round the spur.
        ///
        /// If nothing within the fan descends, the walk stops and reports NoWayDown,
        /// and the caller decides how the river ends there.
        /// </summary>
        /// <param name="drawn">The course as authored, world space, head first.</param>
        /// <param name="result">Receives the fitted course. Cleared first.</param>
        /// <param name="ground">Ground elevation sampler.</param>
        /// <param name="stepMetres">Distance per step. The authored point spacing.</param>
        /// <param name="fanDegrees">How far off the direct heading the walk may swing.</param>
        /// <param name="climbToleranceMetres">Rise accepted as level. A real river bed
        /// is not monotonic to the millimetre, and a zero tolerance makes the walk
        /// zig-zag across every one-centimetre bump in the heightfield.</param>
        /// <param name="water">Optional test for arriving at joinable water; null for none.</param>
        public static DescentEnding WalkDownhill(List<Vector3> drawn, List<Vector3> result,
                                                 GroundSampler ground,
                                                 float stepMetres, float fanDegrees,
                                                 float climbToleranceMetres,
                                                 System.Func<Vector3, bool> water)
        {
            if (result == null) return DescentEnding.ReachedEnd;
            result.Clear();

            if (drawn == null || drawn.Count < 2 || ground == null)
            {
                if (drawn != null) result.AddRange(drawn);
                return DescentEnding.ReachedEnd;
            }

            float step = Mathf.Max(0.5f, stepMetres);
            float fan = Mathf.Clamp(fanDegrees, 0f, 170f);
            float tolerance = Mathf.Max(0f, climbToleranceMetres);

            Vector3 at = drawn[0];
            at.y = ground(at.x, at.z);
            result.Add(at);

            // Every step must be shorter than the whole drawn course by construction,
            // so this bounds the walk at a generous multiple of the point count and
            // can only be hit by a genuine loop.
            int budget = drawn.Count * 8 + 64;

            int target = 1;

            while (target < drawn.Count && budget-- > 0)
            {
                Vector2 toTarget = new Vector2(drawn[target].x - at.x, drawn[target].z - at.z);

                // Close enough - move the aim to the next drawn point. Half a step
                // rather than a fixed epsilon, so a tight meander does not leave the
                // walk circling a target it keeps overshooting.
                if (toTarget.magnitude < step * 0.5f) { target++; continue; }

                Vector2 heading = toTarget.normalized;

                float here = ground(at.x, at.z);

                Vector2 bestDir = Vector2.zero;
                float bestScore = float.MinValue;
                float bestHeight = 0f;
                bool found = false;

                // 0 first, so a direct step that descends is always the one taken and
                // the drawn line is honoured wherever it is already valid.
                int rays = fan > 1f ? 12 : 0;

                for (int r = 0; r <= rays; r++)
                {
                    // Alternating out from centre: 0, +1, -1, +2, -2 ... Ties then go
                    // to the smaller deviation without needing a second comparison.
                    int signed = (r + 1) / 2 * ((r % 2 == 0) ? 1 : -1);
                    float angle = rays > 0 ? fan * signed / (rays * 0.5f) : 0f;
                    if (Mathf.Abs(angle) > fan) continue;

                    float rad = angle * Mathf.Deg2Rad;
                    float cos = Mathf.Cos(rad);
                    float sin = Mathf.Sin(rad);

                    Vector2 dir = new Vector2(heading.x * cos - heading.y * sin,
                                              heading.x * sin + heading.y * cos);

                    float nx = at.x + dir.x * step;
                    float nz = at.z + dir.y * step;
                    float h = ground(nx, nz);

                    if (h > here + tolerance) continue;

                    // Drop is what the water wants; alignment is what the designer
                    // wants. Weighting drop by the step keeps the two in the same
                    // units, so neither silently dominates at a different scale.
                    float drop = (here - h) / step;
                    float alignment = Vector2.Dot(dir, heading);
                    float score = drop * 2f + alignment;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestDir = dir;
                        bestHeight = h;
                        found = true;
                    }
                }

                if (!found)
                {
                    // Nowhere in the fan goes down. A real river here either ponds and
                    // spills over the lowest lip, or sinks. Finding the spill would
                    // mean flooding a basin, which the graph's Lake Fill already does
                    // properly; here the honest answer is to stop.
                    return DescentEnding.NoWayDown;
                }

                at = new Vector3(at.x + bestDir.x * step, bestHeight, at.z + bestDir.y * step);
                result.Add(at);

                if (water != null && water(at)) return DescentEnding.ReachedWater;
            }

            return DescentEnding.ReachedEnd;
        }

        /// <summary>
        /// Tapers the last stretch of a course that ended with nowhere to go.
        ///
        /// This is the river disappearing: the channel narrows and shallows over the
        /// final run, so it reads as water sinking into the ground rather than as a
        /// channel that was cut and then stopped mid-hillside with a wall at the end.
        /// Real rivers do exactly this in karst and in desert - they run out.
        /// </summary>
        public static void TaperSink(List<Vector3> points, List<float> widths,
                                     List<float> depths, float sinkMetres)
        {
            if (points == null || widths == null || depths == null) return;
            if (points.Count < 2) return;

            float totalLength = 0f;
            for (int i = 0; i < points.Count - 1; i++) 
            {
                Vector3 a = points[i];
                Vector3 b = points[i + 1];
                totalLength += new Vector2(b.x - a.x, b.z - a.z).magnitude;
            }
            float run = Mathf.Min(Mathf.Max(0.5f, sinkMetres), totalLength * 0.5f);

            int n = points.Count;
            float travelled = 0f;

            for (int i = n - 1; i > 0; i--)
            {
                if (i < n - 1)
                {
                    Vector3 a = points[i];
                    Vector3 b = points[i + 1];
                    travelled += new Vector2(b.x - a.x, b.z - a.z).magnitude;
                }

                if (travelled > run) break;
                if (i >= widths.Count || i >= depths.Count) continue;

                // Squared, so the taper is gentle where it starts and closes quickly
                // at the very end. A linear taper reads as a wedge.
                float t = Mathf.Clamp01(travelled / run);
                float k = t * t;

                widths[i] *= Mathf.Max(0.08f, k);
                depths[i] *= Mathf.Max(0.05f, k);
            }
        }
    }
}
