// -----------------------------------------------------------------------------
//  Universe Creator - Plugin Contract (terrain-shaping payload)
//  File:    TerrainShapeRequest.cs
//  Purpose: The one piece of data an external system hands the host so Universe
//           Creator can shape its own terrain and water to match what that system
//           built beside it: conform the ground to a road corridor, raise an
//           impounded reservoir behind a dam, key the ground into a dam's abutments,
//           and open a hole for a tunnel or portal mouth. A plugin produces these
//           through IUCTerrainShapeProvider; the host collects and applies them.
//
//  WHY THIS IS DATA AND NOT A METHOD ON A PROVIDER SURFACE
//  ------------------------------------------------------
//  Control points one way: a plugin STATES what it needs as world-space geometry and
//  levels, and the host decides when and how to write them. A "please raise this
//  water" method on a read-the-world interface would put the direction of control
//  the wrong way round, with the plugin reaching in to move UC's terrain. Keeping it
//  as plain serialisable data also means the request means the same thing to the
//  shape inbox, the dam reconciler and the chunk manager, none of which has to know
//  which plugin wrote it.
//
//  Instead a system states what it needs as one of these requests. The host collects
//  them (from IUCTerrainShapeProvider plugins, and from the serialized TerrainShapeInbox
//  for requests that must outlive the frame), decides when to act in its own
//  regeneration order, and owns every heightfield and water write as it always has.
//  Nothing here names a product and nothing here calls back out: it is world-space
//  geometry and levels in metres, and UC could receive the same request from any source.
//
//  WHY WORLD SPACE AND PLAIN FLOATS
//  --------------------------------
//  A request outlives the frame it was made in -- it is serialised with the scene
//  so a domain reload between "the tool built the road" and "the author pressed
//  update" does not lose it. Serialised means no interface references and no other
//  assembly's types: every field below is a float, an int, an enum or a Bounds, so
//  the request means the same thing after a reload, in a build, or read by an
//  inspector that has never heard of the tool that wrote it.
//
//  REVERSIBILITY
//  -------------
//  Each request carries a stable sourceId. UC applies the set it currently holds to
//  ground it re-derives from pristine terrain every rebuild (the same discipline
//  WaterCarveRegistry follows), so withdrawing a request -- the road was deleted,
//  the dam removed -- and rebuilding restores real ground, rather than leaving a
//  trench or a wall the tool once asked for.
// -----------------------------------------------------------------------------

using System;
using UnityEngine;

namespace UniverseCreator.Integration
{
    /// <summary> Which kind of shaping a <see cref="TerrainShapeRequest"/> asks UC to perform. The applier switches on
    /// this and reads only the fields that kind documents; the others are left at their defaults. </summary>
    public enum TerrainShapeKind
    {
        /// <summary> Lower or raise the ground to a road corridor and blend back to natural terrain over the batter, so
        /// the road sits on the surface with no buried carriageway and no open hole at its ends. Reads
        /// <see cref="TerrainShapeRequest.corridor"/>. </summary>
        ConformCorridor = 0,

        /// <summary> Raise the impounded water upstream of a dam line to the held surface level and clip it at the line,
        /// so the reservoir stands behind the structure and not through it. Reads the reservoir and barrier fields.
        /// </summary>
        RaiseWaterBody = 1,

        /// <summary> Fill the ground into a dam's two abutments so the crest keys into the valley walls rather than
        /// floating past them. Reads the crest/abutment fields. </summary>
        KeyAbutments = 2,

        /// <summary> Open a hole in the terrain for a tunnel or metro portal mouth. Reads <see cref="opening"/>. Shared
        /// with the tunnel work (B-18). </summary>
        CutOpening = 3
    }

    /// <summary> Shared, product-neutral arithmetic for the authored corridor cut/fill profile. Keeping this in the
    /// plugin contract lets both the terrain provider and the requesting tool use the same surface calculation without
    /// introducing an assembly dependency from the host back to a plugin. </summary>
    public static class TerrainEarthworkProfile
    {
        public static double SlopeElevationMetres(double bandElevationMetres, double runMetres,
            double runPerRise, bool isFill)
        {
            double rise = runPerRise > 0.0 ? runMetres / runPerRise : 0.0;
            return isFill ? bandElevationMetres - rise : bandElevationMetres + rise;
        }

        public static double ToeEaseHeightMetres(double heightMetres, double easeMetres)
        {
            if (heightMetres <= 0.0) { return 0.0; }
            if (easeMetres <= 0.0001 || heightMetres >= easeMetres) { return heightMetres; }
            return ((2.0 * easeMetres * heightMetres * heightMetres)
                - (heightMetres * heightMetres * heightMetres)) / (easeMetres * easeMetres);
        }

        public static double SurfaceMetres(double naturalElevationMetres, double bandElevationMetres,
            double cutRunMetres, double fillRunMetres, double cutRunPerRise, double fillRunPerRise,
            double toeEaseMetres)
        {
            double cutRun = cutRunMetres > 0.0 ? cutRunMetres : 0.0;
            double fillRun = fillRunMetres > 0.0 ? fillRunMetres : 0.0;
            double cutLine = SlopeElevationMetres(bandElevationMetres, cutRun, cutRunPerRise, false);
            double fillLine = SlopeElevationMetres(bandElevationMetres, fillRun, fillRunPerRise, true);

            if (naturalElevationMetres > cutLine)
            {
                double needed = naturalElevationMetres - cutLine;
                double weight = toeEaseMetres > 0.0001 && cutRunPerRise > 0.0
                    ? Clamp01(cutRun / (toeEaseMetres * cutRunPerRise)) : 0.0;
                return naturalElevationMetres - Lerp(needed, ToeEaseHeightMetres(needed, toeEaseMetres), weight);
            }
            if (naturalElevationMetres < fillLine)
            {
                double needed = fillLine - naturalElevationMetres;
                double weight = toeEaseMetres > 0.0001 && fillRunPerRise > 0.0
                    ? Clamp01(fillRun / (toeEaseMetres * fillRunPerRise)) : 0.0;
                return naturalElevationMetres + Lerp(needed, ToeEaseHeightMetres(needed, toeEaseMetres), weight);
            }
            return naturalElevationMetres;
        }

        private static double Lerp(double a, double b, double t)
        {
            return a + ((b - a) * t);
        }

        private static double Clamp01(double value)
        {
            if (value < 0.0) { return 0.0; }
            if (value > 1.0) { return 1.0; }
            return value;
        }
    }

    /// <summary> One station along a road centreline, in world space, with the widths and earthwork weight the tool
    /// computed for it. A corridor may use the legacy smoothstep blend or an authored trapezoidal cut/fill profile.
    /// Heights and widths are the tool's (it is the authority on the road); the heightfield write is UC's. </summary>
    [Serializable]
    public struct TerrainCorridorSample
    {
        /// <summary> World X of the centreline at this station. </summary>
        public float worldX;

        /// <summary> World Z of the centreline at this station. </summary>
        public float worldZ;

        /// <summary> The finished road surface height in metres at this station -- the level the flat band is held to.
        /// </summary>
        public float roadHeightMetres;

        /// <summary> Half-width of the flat band: carriageway plus shoulder. Ground stays at road height within it.
        /// </summary>
        public float flattenedHalfWidthMetres;

        /// <summary> Half-width at which the blend has finished and the ground is natural again. Between this and the
        /// flattened edge the height smoothsteps from road to natural. </summary>
        public float blendOuterHalfWidthMetres;

        /// <summary> Per-station weight for an authored earthwork profile. Zero preserves natural terrain; one applies
        /// the complete profile. </summary>
        public float earthworkWeight;

        /// <summary> A tunnel, shell, or retaining-wall station that must not receive ordinary cut/fill earthworks. </summary>
        public bool earthworkExcluded;
    }


    /// <summary> One shaping the world side performs on itself at the request of a path tool. A class, not a struct, so
    /// it can carry the corridor array and so a list of them serialises cleanly on the inbox. Every field is world space
    /// in metres or a plain scalar; only the fields the <see cref="kind"/> names are read. </summary>
    [Serializable]
    public class TerrainShapeRequest
    {
        /// <summary> What to do. The applier reads only the fields this kind documents. </summary>
        public TerrainShapeKind kind = TerrainShapeKind.ConformCorridor;

        /// <summary> A stable identifier from the requester -- the road's id, the dam's id. Two requests with the same id
        /// are the same shaping restated: the newer replaces the older rather than stacking a second trench on the
        /// first. Withdrawing the id and rebuilding restores pristine ground. </summary>
        public string sourceId = string.Empty;

        /// <summary> One human sentence the requester wrote, shown verbatim in the inbox inspector so an author can see
        /// what asked for a change without decoding the fields. </summary>
        public string sourceLabel = string.Empty;

        // --- ConformCorridor ------------------------------------------------------------------------------------------

        /// <summary> The road centreline as ordered stations. Empty for every kind but <see cref="TerrainShapeKind.
        /// ConformCorridor"/>. </summary>
        public TerrainCorridorSample[] corridor = Array.Empty<TerrainCorridorSample>();

        /// <summary> True for a sealed road, false for an unsealed one. Does not change the conform; carried so the paint
        /// pass downstream can widen the verge for an unsealed road without a second request. </summary>
        public bool corridorSealed = true;

        /// <summary> Use the authored ratio-based cut/fill profile instead of the legacy smoothstep corridor. </summary>
        public bool useEarthworkProfile;

        /// <summary> Authored cut and fill flat half-widths, before the host's heightmap-cell cut guard. </summary>
        public float cutFlatHalfWidthMetres;
        public float fillFlatHalfWidthMetres;

        /// <summary> Authored cut verge, used with the host sample spacing for the one-cell cut guard. </summary>
        public float cutVergeMetres;

        /// <summary> Horizontal run per vertical rise for cut and fill. </summary>
        public float cutRunPerRise = 1.5f;
        public float fillRunPerRise = 2f;

        /// <summary> Authored fill reach, toe-easing height, along-road end taper and mesh clearance, in metres. </summary>
        public float batterMaxReachMetres;
        /// <summary> Let cut earthworks continue to daylight within available terrain chunks. </summary>
        public bool cutToDaylight;
        /// <summary> Let fill earthworks continue to daylight into natural terrain. The authored reach is retained as
        /// a minimum; the extension is limited by the terrain's vertical range. </summary>
        public bool fillToDaylight;
        public float toeEaseMetres;
        public float endTaperMetres;
        public float matchTerrainSubtractionMetres;

        // --- RaiseWaterBody -------------------------------------------------------------------------------------------

        /// <summary> The level the reservoir is held to, in metres -- crest minus freeboard on the dam side. </summary>
        public float waterSurfaceLevelMetres;

        /// <summary> The water level below the dam, in metres. The reservoir is clipped to the barrier line and does not
        /// spill this side of it. </summary>
        public float tailwaterLevelMetres;

        /// <summary> World X of the dam centre: the barrier line and the reservoir clip pass through here. </summary>
        public float barrierCentreX;

        /// <summary> World Z of the dam centre. </summary>
        public float barrierCentreZ;

        /// <summary> Heading of the dam line in degrees (0 = +Z, 90 = +X). The reservoir lies on the upstream side of a
        /// line through the centre along this heading. </summary>
        public float barrierAxisHeadingDeg;

        /// <summary> Heading of the upstream side in degrees, so the applier knows which half-plane impounds. </summary>
        public float upstreamHeadingDeg;

        /// <summary> World-space extent of the impounded reservoir the tool flood-filled, so the applier need not
        /// re-search the whole world to find the body it must raise. Zero size means "nothing impounded". </summary>
        public float reservoirMinX;
        public float reservoirMaxX;
        public float reservoirMinZ;
        public float reservoirMaxZ;

        // --- KeyAbutments ---------------------------------------------------------------------------------------------

        /// <summary> Top-of-structure level in metres: the ground at each abutment is filled up to at least this so the
        /// crest keys into the wall. </summary>
        public float crestLevelMetres;

        /// <summary> Length of the crest line the structure spans, in metres. </summary>
        public float crestLengthMetres;

        /// <summary> Crest width front-to-back, in metres. </summary>
        public float crestWidthMetres;

        /// <summary> Footprint front-to-back at the base, in metres, so the keyed fill covers the ground the battered
        /// faces actually occupy. </summary>
        public float baseWidthMetres;

        /// <summary> World X of the dam centre for the abutment keying. </summary>
        public float abutmentCentreX;

        /// <summary> World Z of the dam centre for the abutment keying. </summary>
        public float abutmentCentreZ;

        /// <summary> Heading of the crest line in degrees (0 = +Z, 90 = +X). The two abutments are the ends of a segment
        /// of <see cref="crestLengthMetres"/> centred on the dam and running along this heading. </summary>
        public float abutmentAxisHeadingDeg;

        // --- CutOpening -----------------------------------------------------------------------------------------------

        /// <summary> World-space box to open the terrain within, for a tunnel or portal mouth. Zero size means no hole.
        /// </summary>
        public Bounds opening;

        /// <summary> True when this request carries a shaping worth applying, so the inbox can drop a default-constructed
        /// or emptied entry rather than have the applier decide per kind. </summary>
        public bool HasWork
        {
            get
            {
                switch (kind)
                {
                    case TerrainShapeKind.ConformCorridor:
                        return corridor != null && corridor.Length >= 2;
                    case TerrainShapeKind.RaiseWaterBody:
                        return !float.IsNaN(waterSurfaceLevelMetres) && !float.IsInfinity(waterSurfaceLevelMetres);
                    case TerrainShapeKind.KeyAbutments:
                        return crestLengthMetres > 0f && baseWidthMetres > 0f
                            && !float.IsNaN(crestLevelMetres) && !float.IsInfinity(crestLevelMetres);
                    case TerrainShapeKind.CutOpening:
                        return opening.size.x > 0f && opening.size.z > 0f;
                    default:
                        return false;
                }
            }
        }

        /// <summary> The world-space rectangle this request touches, so a per-chunk apply can skip a request that cannot
        /// reach it -- the same early-out WaterCarveLayer.AffectsRegion gives the digs. Returned as a Bounds with the
        /// height axis left wide, because the request is about ground position, not a vertical band. </summary>
        public bool TryGetFootprint(out Bounds footprint)
        {
            footprint = new Bounds();
            switch (kind)
            {
                case TerrainShapeKind.ConformCorridor:
                    if (corridor == null || corridor.Length == 0) { return false; }
                    float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
                    float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
                    for (int i = 0; i < corridor.Length; i++)
                    {
                        float r = Mathf.Max(0f, corridor[i].blendOuterHalfWidthMetres)
                            + (useEarthworkProfile ? Mathf.Max(0f, endTaperMetres) : 0f);
                        minX = Mathf.Min(minX, corridor[i].worldX - r);
                        maxX = Mathf.Max(maxX, corridor[i].worldX + r);
                        minZ = Mathf.Min(minZ, corridor[i].worldZ - r);
                        maxZ = Mathf.Max(maxZ, corridor[i].worldZ + r);
                    }
                    if (maxX < minX || maxZ < minZ) { return false; }
                    SetFootprint(ref footprint, minX, maxX, minZ, maxZ);
                    return true;

                case TerrainShapeKind.RaiseWaterBody:
                    if (reservoirMaxX <= reservoirMinX || reservoirMaxZ <= reservoirMinZ) { return false; }
                    SetFootprint(ref footprint, reservoirMinX, reservoirMaxX, reservoirMinZ, reservoirMaxZ);
                    return true;

                case TerrainShapeKind.KeyAbutments:
                    if (crestLengthMetres <= 0f || baseWidthMetres <= 0f) { return false; }
                    float reach = Mathf.Max(crestLengthMetres, baseWidthMetres);
                    SetFootprint(ref footprint, abutmentCentreX - reach, abutmentCentreX + reach,
                        abutmentCentreZ - reach, abutmentCentreZ + reach);
                    return true;

                case TerrainShapeKind.CutOpening:
                    if (opening.size.x <= 0f || opening.size.z <= 0f) { return false; }
                    footprint = opening;
                    return true;

                default:
                    return false;
            }
        }

        private static void SetFootprint(ref Bounds b, float minX, float maxX, float minZ, float maxZ)
        {
            Vector3 centre = new Vector3((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f);
            Vector3 size = new Vector3(maxX - minX, float.MaxValue * 0.5f, maxZ - minZ);
            b = new Bounds(centre, size);
        }
    }
}
