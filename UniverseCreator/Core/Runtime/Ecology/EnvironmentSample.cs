// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  Module 3: Living World
//
//  File:    EnvironmentSample.cs
//  Purpose: The whole environment at one point on the ground, in real units, as
//           one flat value type.
//
//  WHY A STRUCT
//  ------------
//  A chunk evaluates on the order of forty thousand candidate sites. One class
//  instance per candidate is forty thousand heap allocations per chunk, which
//  does not appear in an average frame time - it appears as a collection pause
//  on a phone, minutes later, in a place unrelated to the cause. So: a value
//  type, no reference-typed field, under 160 bytes, and it never touches the
//  heap. Pass it by "in" wherever it is read.
//
//  WHY NOT A READONLY STRUCT
//  -------------------------
//  Deliberate, and recorded rather than silent. A readonly struct must assign
//  all thirty-seven fields through one constructor, and a thirty-seven argument
//  positional constructor is exactly the shape that lets a slope end up in a
//  soil-fertility slot with nothing to catch it. The sampler is the only writer,
//  it assigns by name, and every read is a field read through "in" - which the
//  compiler does not defensively copy, because this type has no methods. So the
//  performance of readonly is kept and the failure mode of readonly is not.
//
//  UNITS ARE IN THE NAMES
//  ----------------------
//  Every field that has a unit carries it in its name. A field called altitude
//  is a question; altitudeM is an answer. Fractions are 0-1 and say so.
// -----------------------------------------------------------------------------

using UnityEngine;
using System.Runtime.InteropServices;
using UniverseCreator.Nodes;

namespace UniverseCreator.Ecology
{
    /// <summary>
    /// Everything the ecology layer needs to know about one point on the ground.
    /// Filled once per candidate by EnvironmentSampler.
    /// </summary>
    public struct EnvironmentSample
    {
        // ---- Position and form -------------------------------------------------

        /// <summary>Absolute world position of the ground point. Absolute, not chunk-local: determinism depends on it.</summary>
        public Vector3 worldPos;

        /// <summary>Terrain surface normal, already normalised.</summary>
        public Vector3 normal;

        /// <summary>m above sea level. Sea level is world Y = 0 unless the water system moved it.</summary>
        public float altitudeM;

        /// <summary>deg from horizontal, 0 flat to 90 vertical.</summary>
        public float slopeDeg;

        /// <summary>deg compass azimuth the slope faces, 0 north, 90 east. Undefined and unused on flat ground.</summary>
        public float aspectDeg;

        /// <summary>Mean curvature, negative concave (a hollow that collects) to positive convex (a ridge that sheds).</summary>
        public float curvature;

        /// <summary>deg latitude, negative south. Decides which way the warm slope faces and how the seasons run.</summary>
        public float latitudeDeg;

        // ---- Thermal climate ---------------------------------------------------

        /// <summary>degC annual mean at this altitude, lapse-adjusted.</summary>
        public float meanAnnualTempC;

        /// <summary>degC mean of the warmest month. The treeline gate reads this one, not the annual mean.</summary>
        public float warmestMonthTempC;

        /// <summary>degC mean of the coldest month. Gates evergreen broadleaf and palms.</summary>
        public float coldestMonthTempC;

        /// <summary>0-1 share of the year above freezing.</summary>
        public float frostFreeFraction;

        /// <summary>degC-day accumulated warmth over the growing season above the species base.</summary>
        public float gdd;

        // ---- Water balance -----------------------------------------------------

        /// <summary>mm/yr precipitation, from the shipped orographic climate nodes.</summary>
        public float annualPrecipMm;

        /// <summary>mm/yr potential evapotranspiration.</summary>
        public float petMm;

        /// <summary>Aridity index, precipitation over PET.</summary>
        public float aridity;

        /// <summary>0-1 plant-available soil moisture, after drainage and slope.</summary>
        public float soilMoisture;

        /// <summary>ln(a / tan beta) topographic wetness. High in drainage hollows, low on ridges.</summary>
        public float twi;

        /// <summary>m below the surface to the water table. Large means out of reach of any root.</summary>
        public float waterTableDepthM;

        // ---- Water bodies and coast --------------------------------------------

        /// <summary>
        /// m to the nearest water body edge. When no body lies inside the sampler's
        /// search radius this holds that radius, so a riparian species simply fails
        /// its band test instead of needing a special case.
        /// </summary>
        public float distanceToWaterM;

        /// <summary>Kind of the nearest body. Only meaningful while distanceToWaterM is inside the search radius.</summary>
        public WaterBodyKind nearestWaterKind;

        // hasWaterInRange belongs to this group and is declared with the byte-sized
        // fields at the end of the struct instead. See the layout note down there:
        // a lone bool here costs three bytes of padding and pushes the struct over
        // its size budget, and the budget is the reason this type is a struct at all.

        /// <summary>m below the water surface, 0 on dry land. Positive means submerged, which kelp and seagrass need.</summary>
        public float submergedDepthM;

        /// <summary>0-1 salt loading from spray, decaying inland from the shore.</summary>
        public float saltFraction;

        // ---- Soil --------------------------------------------------------------

        /// <summary>m of soil above rock. Derived from slope and curvature, never authored per cell.</summary>
        public float soilDepthM;

        /// <summary>0-1 fertility. One scalar on purpose: pH, N, P and K would be four authored fields for an effect no player can see.</summary>
        public float soilFertility;

        /// <summary>0-1 exposed rock and loose stone. Above a species' limit the site is scree and holds stone, not stems.</summary>
        public float rockiness;

        // ---- Disturbance -------------------------------------------------------

        /// <summary>0-1 topographic wind exposure. The reason ridgelines are bare.</summary>
        public float windExposure;

        /// <summary>kg/m2 peak snow load. The reason conifers dominate snow country.</summary>
        public float snowLoadKgPerM2;

        /// <summary>yr mean fire return interval. Large means fire is effectively absent here.</summary>
        public float fireReturnYears;

        /// <summary>0-1 grazing and browsing pressure. What keeps a meadow open.</summary>
        public float browsePressure;

        // ---- Competition and biome --------------------------------------------
        //
        // Light is three figures rather than one because shade is a stack, not a
        // scalar (V-16, L-17). A canopy oak is not shaded by its own canopy, an
        // understory hazel is, and moss on the floor is shaded by both. One number
        // would have to pick which of those three to be wrong about, and picking
        // "floor light" would exclude every canopy tree from the wood it forms.
        // The sampler resolves all three from one grid read.

        /// <summary>0-1 light below the Emergent layer. What a Canopy-layer species competes in.</summary>
        public float subcanopyLightFraction;

        /// <summary>0-1 light below the closed canopy. What an Understory species receives.</summary>
        public float canopyLightFraction;

        /// <summary>0-1 light reaching the ground. What grass, moss and seedlings get.</summary>
        public float floorLightFraction;

        /// <summary>Total leaf area index of everything standing above this point, m2/m2.</summary>
        public float canopyLai;

        /// <summary>0-1 confidence in that biome. Low means a transition zone, where two assemblages should mix.</summary>
        public float biomeBlend;

        // ---- Byte-sized tail ---------------------------------------------------
        //
        // LAYOUT, NOT TASTE. These three are last and in this order so they share one
        // four-byte word. Sequential layout pads every small field up to the next
        // four-byte boundary, so a bool sitting among the floats costs four bytes to
        // carry one bit, and two biome indices as int cost eight to carry a number
        // that never exceeds nine. Grouped here they cost four bytes together, which
        // is what holds the struct at 156 bytes against the 160 it is allowed. Move
        // any of them back up among the floats and the budget check fails.
        //
        // sbyte, not int: WhittakerBiome.Count is 10 and the unclassified sentinel is
        // -1, so the whole range is -1..9 and sbyte carries it with room to spare.

        /// <summary>Dominant biome index from the shipped classifier, or -1 unclassified.</summary>
        public sbyte biomeIndex;

        /// <summary>
        /// The other biome this point lies between, or -1 when it is not near a break.
        /// Selection mixes the two assemblages across the band so a boundary reads as a
        /// transition rather than a line, which needs to know which two are meeting.
        /// </summary>
        public sbyte biomeIndexAlt;

        /// <summary>
        /// Whether a water body was actually found inside the search radius.
        ///
        /// Needed because WaterBodyKind has no "none" member, so nearestWaterKind has
        /// to hold something even in dry country, and whatever it holds is a lie that
        /// a rule keyed off it will believe. This is the flag that lets a caller ask
        /// the honest question instead.
        /// </summary>
        /// <remarks>
        /// U1 because bool is the one field here that is not blittable: by default it
        /// marshals as a four-byte Win32 BOOL, so Marshal.SizeOf reported four bytes
        /// for one that occupies one in managed memory - inflating the measured size
        /// of the struct by three bytes that no candidate ever actually pays for.
        /// Pinning it to one unmanaged byte makes the measured size the real one.
        /// </remarks>
        [MarshalAs(UnmanagedType.U1)]
        public bool hasWaterInRange;
    }
}
