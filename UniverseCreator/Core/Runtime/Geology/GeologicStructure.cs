// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    GeologicStructure.cs
//  Purpose: The § 9.4 answer to WHERE the beds of the lithology column sit in space.
//           LithologyProfile says what the rock IS and in what order; this says how
//           that ordered stack is folded, tilted and faulted, so a bed top has an
//           elevation at every (x,z). Beds are not horizontal - they dip, they fold
//           into broad trains, they step across faults - and it is precisely that
//           structure meeting topography that draws an outcrop pattern. Without it,
//           every unit would be a flat layer cake and the map would show stripes, not
//           the V-shaped bed traces a real geologic map shows bending around valleys.
//
//  DETERMINISTIC AND CELL-BLIND BY CONSTRUCTION
//  --------------------------------------------
//  Every elevation here is a pure function of ABSOLUTE world position and the world
//  seed. Nothing reads a cell index, a cell-local coordinate, or terrain height. That
//  is what makes the pattern coherent across cell borders: two chunks that sample the
//  same world point necessarily get the same bed elevation, because the function never
//  saw which chunk asked. The single 1-D structural coordinate is the along-dip
//  distance - the projection of the point onto the down-dip azimuth (strike + 90) -
//  so folds and faults run parallel to strike, exactly as they do in a fold belt.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.Geology
{
    /// <summary>
    /// The structural model of a lithology column: a regional strike and dip, a broad
    /// fold train and an optional strike-parallel fault set. Serializable so it authors
    /// inline on the profile; evaluated from absolute world position plus the world seed,
    /// never from a cell, so the bed pattern it produces is coherent across every border.
    /// </summary>
    [System.Serializable]
    public class GeologicStructure
    {
        [Tooltip("Elevation in metres of the top of the youngest bed where the structure " +
                 "is undisplaced - the datum the whole stack hangs from. Fold, dip and " +
                 "fault are measured as departures from this.")]
        public float datumElevationM = 0f;

        [Range(0f, 360f)]
        [Tooltip("Compass bearing of the strike line in degrees - the horizontal direction " +
                 "along which the beds run level. Beds dip down the perpendicular (strike+90).")]
        public float strikeDeg = 0f;

        [Range(0f, 89f)]
        [Tooltip("Dip angle in degrees - how steeply the beds tilt down the dip direction. " +
                 "0 is flat-lying (layer cake), higher tilts the stack so older rock rises " +
                 "on one side. Kept below vertical so a bed top has one elevation per point.")]
        public float dipDeg = 8f;

        [Tooltip("Fold train wavelength in kilometres - the distance between successive " +
                 "antiforms. Broad regional folds are kilometres across; this and the " +
                 "amplitude together bend the bed traces into the arches a map shows.")]
        public float foldWavelengthKm = 6f;

        [Tooltip("Fold amplitude in metres - how far the fold train lifts and drops the beds " +
                 "about the dip plane. Zero leaves a planar dipping stack with no folding.")]
        public float foldAmplitudeM = 60f;

        [Range(0, 12)]
        [Tooltip("Number of strike-parallel faults across the span. 0 is an unfaulted block; " +
                 "each fault steps every bed by its throw on one side, offsetting the trace.")]
        public int faultCount = 0;

        [Tooltip("Typical fault throw in metres - the vertical offset a fault puts across the " +
                 "beds. Individual faults jitter around this from the seed so they are not clones.")]
        public float faultThrowM = 25f;

        [Tooltip("The along-dip span in kilometres across which the fault set is distributed. " +
                 "Faults are placed deterministically from the seed within this window.")]
        public float faultSpanKm = 20f;

        /// <summary>
        /// Clamps every field into a physically meaningful range: dip stays below vertical so
        /// a bed top is single-valued, wavelength and span stay positive so the periodic and
        /// distribution maths never divides by zero. Called from the profile's OnValidate.
        /// </summary>
        public void Sanitize()
        {
            strikeDeg        = Mathf.Repeat(strikeDeg, 360f);
            dipDeg           = Mathf.Clamp(dipDeg, 0f, 89f);
            foldWavelengthKm = Mathf.Max(0.001f, foldWavelengthKm);
            foldAmplitudeM   = Mathf.Max(0f, foldAmplitudeM);
            faultCount       = Mathf.Clamp(faultCount, 0, 12);
            faultThrowM      = Mathf.Max(0f, faultThrowM);
            faultSpanKm      = Mathf.Max(0.001f, faultSpanKm);
        }

        /// <summary>
        /// The single structural coordinate: the signed distance of a world point along the
        /// down-dip azimuth (strike + 90 degrees), in metres. Beds run level along strike, so
        /// everything structural - fold phase, dip drop, which side of a fault - depends on the
        /// point only through this one projection. It is a pure linear function of world (x,z),
        /// which is exactly why the pattern is identical no matter which cell samples the point.
        /// </summary>
        public float AlongDipDistanceM(float x, float z)
        {
            float dipAz = (strikeDeg + 90f) * Mathf.Deg2Rad;
            // Compass convention: 0 deg = +Z (north), 90 deg = +X (east).
            float ex = Mathf.Sin(dipAz);
            float ez = Mathf.Cos(dipAz);
            return x * ex + z * ez;
        }

        /// <summary>
        /// Cumulative fault throw at an along-dip coordinate, in metres. Faults strike parallel
        /// to the beds, so each one is a fixed along-dip line; a point picks up a fault's throw
        /// once it lies down-dip of it. Positions and per-fault throws are hashed from the seed,
        /// so the set is deterministic, jittered (not clones), and coherent across cell borders.
        /// </summary>
        public float FaultThrowM(float alongDipM, int seed)
        {
            if (faultCount <= 0 || faultThrowM <= 0f) return 0f;
            float spanM = faultSpanKm * 1000f;
            float total = 0f;
            for (int k = 0; k < faultCount; k++)
            {
                float pos    = (Hash01(seed, k) - 0.5f) * spanM;   // fault location along dip
                float throwK = faultThrowM * (0.5f + Hash01(seed, k + 977)); // 0.5x .. 1.5x
                if (alongDipM > pos) total += throwK;
            }
            return total;
        }

        /// <summary>
        /// The structural displacement of the datum at a world point, in metres - the part of a
        /// bed's elevation that does NOT depend on which unit it is: fold train, dip drop and
        /// fault steps summed. A bed top is then this plus the datum minus the section above it.
        /// </summary>
        public float StructuralDisplacementM(float x, float z, int seed)
        {
            float s        = AlongDipDistanceM(x, z);
            float fold     = foldAmplitudeM * Mathf.Sin(2f * Mathf.PI * s / (foldWavelengthKm * 1000f));
            float dipDrop  = -Mathf.Tan(dipDeg * Mathf.Deg2Rad) * s;
            return fold + dipDrop + FaultThrowM(s, seed);
        }

        /// <summary>
        /// Elevation in metres of the TOP of unit <paramref name="unitIndex"/> at a world point.
        /// The column is oldest-first, so the units physically ABOVE unit i are the younger ones
        /// at higher indices; their summed thickness is how far the top of unit i sits below the
        /// section datum. Structural displacement then folds, tilts and faults that surface.
        /// Older beds therefore always sit lower than younger ones at the same point, as they must.
        /// (The § 9.4 pseudo-formula writes the thickness term from a basement datum with a plus;
        /// this uses a section-top datum and subtracts the section above - the same surface up to
        /// the choice of datum constant, but "top of section" is the datum an author can read off.)
        /// </summary>
        public float BedTopElevationM(LithologyProfile profile, int unitIndex, float x, float z, int seed)
        {
            if (ReferenceEquals(profile, null)) return datumElevationM; // UC-T01-06: snapshot lease owns the copied column.

            float sumAbove = 0f;
            int count = profile.UnitCount;
            for (int k = unitIndex + 1; k < count; k++)
            {
                LithoUnit u = profile.UnitAt(k);
                if (u != null) sumAbove += u.thicknessM;
            }
            return datumElevationM - sumAbove + StructuralDisplacementM(x, z, seed);
        }

        /// <summary>
        /// A cheap, allocation-free integer hash to [0,1) - used to place faults and jitter their
        /// throws deterministically from the world seed. Same (seed, k) always yields the same
        /// value, which is what keeps the fault set identical everywhere the same point is sampled.
        /// </summary>
        private static float Hash01(int seed, int k)
        {
            unchecked
            {
                uint h = (uint)seed * 2654435761u + (uint)k * 40503u + 1013904223u;
                h ^= h >> 15; h *= 2246822519u;
                h ^= h >> 13; h *= 3266489917u;
                h ^= h >> 16;
                return (h & 0x00FFFFFFu) / (float)0x01000000;
            }
        }
    }
}

