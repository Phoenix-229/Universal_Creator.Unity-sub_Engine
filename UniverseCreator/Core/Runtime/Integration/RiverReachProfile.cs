// -----------------------------------------------------------------------------
//  Universe Creator - Path Tool Integration (Module 6 seam)
//  File:    RiverReachProfile.cs
//  Purpose: The one piece of data Universe Creator publishes back OUT across the
//           linker so a city or road tool can build its riverbanks to the river UC
//           actually cut: per station, the channel's width, the water-surface level
//           and the bed level, all in world space and metres.
//
//  WHY UC PUBLISHES THIS AND WHY IT IS NOT A PROVIDER MEMBER
//  --------------------------------------------------------
//  A tool reading the terrain gets height and normal from the six-member provider
//  surface (IPCGDataProvider). That is enough to sit a road ON the ground, but not
//  to build a bank BESIDE a river: a single height sample at the water's edge cannot
//  say how deep the channel was cut below the surface, or how wide it runs, and a
//  bank guessed from the surface height alone stands too tall on a shallow reach and
//  too short on a deep one. RCM's bank maths needs the design water depth (surface
//  minus bed) and the wetted width, and only UC -- which solved the course, fitted
//  it to the valley and dug it -- knows those.
//
//  The answer is NOT a seventh member on the provider. That surface is the contract
//  a tool READS the world through, it is versioned, and growing it would report as
//  drift to any tool built against the older count (PathProviderSocket.Status ->
//  SurfaceMismatch). Instead UC STATES what it built, as this data, and leaves it
//  where the tool already looks -- the same serialised-component-on-the-world-object
//  contract TerrainShapeInbox uses for requests coming the other way. The provider
//  stays exactly six members; this is a published fact, not a call.
//
//  WHY WORLD SPACE AND PLAIN FLOATS
//  --------------------------------
//  Like a shape request, a published reach outlives the frame it was made in -- it
//  is serialised with the scene so a tool can read the river UC built last session
//  without UC rebuilding first. Serialised means no interface references and no
//  other assembly's types: every field here is a float, a string or an array of a
//  plain struct, so the reach means the same thing after a domain reload, in a
//  build, or read by an inspector that has never heard of the tool that consumes it.
//  It is the same datum the terrain reader answers in -- world metres -- so a tool
//  can mix a height sample and a reach without converting between frames.
//
//  BED = SURFACE - DEPTH
//  ---------------------
//  Every station carries both the surface level and the bed level directly, rather
//  than a surface and a depth, so a reader never has to know UC's sign convention to
//  place a bank: the bank top rides above surfaceLevelMetres by its freeboard, the
//  toe sits at bedLevelMetres, and the design water depth is simply the difference.
// -----------------------------------------------------------------------------

using System;
using UnityEngine;

namespace UniverseCreator.Integration
{
    /// <summary> One cross-section along a river's solved course, in world space, with the figures UC dug it to. The
    /// publisher walks the river's fitted stations and records each one; a reader finds the nearest station to a bank it
    /// is building and reads the width and levels there. All world metres -- the same datum the terrain reader answers
    /// in. </summary>
    [Serializable]
    public struct RiverProfileStation
    {
        /// <summary> World X of the channel centreline at this station. </summary>
        public float worldX;

        /// <summary> World Z of the channel centreline at this station. </summary>
        public float worldZ;

        /// <summary> Full wetted width of the channel here, in metres -- the width UC cut the bed to, not a bank-to-bank
        /// distance. A bank is set back half of this from the centreline. </summary>
        public float widthMetres;

        /// <summary> World Y of the water surface here. The surface descends from head to mouth; this is the level UC's
        /// solve settled on after fitting the course to the ground and holding the water inside its own banks. </summary>
        public float surfaceLevelMetres;

        /// <summary> World Y of the channel bed here -- the surface less the depth UC dug. Never above the surface. The
        /// design water depth at this station is <see cref="surfaceLevelMetres"/> - <see cref="bedLevelMetres"/>. </summary>
        public float bedLevelMetres;

        /// <summary> The design water depth at this station: surface less bed, floored at zero so float noise at a dried
        /// reach never reports a negative depth. </summary>
        public float DepthMetres { get { return Mathf.Max(0f, surfaceLevelMetres - bedLevelMetres); } }
    }


    /// <summary> One river UC has built, published as its ordered stations so a city or road tool can lay a bank, a
    /// revetment or a crossing against the real channel. A class, not a struct, so the station array serialises cleanly
    /// in a list on <see cref="RiverProfilePublication"/> and so a reach can be sampled without copying it. </summary>
    [Serializable]
    public class RiverReachProfile
    {
        /// <summary> A stable identifier for the river this reach describes, set by the publisher. Restating the same id
        /// replaces the reach in place rather than adding a second; dropping the id (the river was deleted) withdraws it.
        /// Reading a reach never needs the id -- it exists only so the publisher can keep the set reversible. </summary>
        public string sourceId = string.Empty;

        /// <summary> One human sentence naming the river, shown verbatim in the publication inspector so an author can
        /// see which body a reach came from without decoding the stations. </summary>
        public string sourceLabel = string.Empty;

        /// <summary> The river's solved course as ordered cross-sections from head to mouth. At least two are needed for
        /// the reach to describe a line; fewer is treated as no reach at all. </summary>
        public RiverProfileStation[] stations = Array.Empty<RiverProfileStation>();

        /// <summary> True when this reach carries a usable line -- two or more stations. The publication drops a reach
        /// that does not (an unsolved or single-point river) rather than store a degenerate one. </summary>
        public bool HasWork { get { return stations != null && stations.Length >= 2; } }

        /// <summary> The world-space rectangle this reach touches, widened each side by the local half-width, so a reader
        /// can skip a reach that cannot reach the ground it is working. Returned as a Bounds with the height axis left
        /// wide -- the reach is about plan position, not a vertical band -- the same shape
        /// <see cref="TerrainShapeRequest.TryGetFootprint"/> returns for a request. </summary>
        public bool TryGetFootprint(out Bounds footprint)
        {
            footprint = new Bounds();
            if (stations == null || stations.Length == 0) { return false; }

            float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
            float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
            for (int i = 0; i < stations.Length; i++)
            {
                float r = Mathf.Max(0f, stations[i].widthMetres) * 0.5f;
                minX = Mathf.Min(minX, stations[i].worldX - r);
                maxX = Mathf.Max(maxX, stations[i].worldX + r);
                minZ = Mathf.Min(minZ, stations[i].worldZ - r);
                maxZ = Mathf.Max(maxZ, stations[i].worldZ + r);
            }
            if (maxX < minX || maxZ < minZ) { return false; }

            Vector3 centre = new Vector3((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f);
            Vector3 size = new Vector3(maxX - minX, float.MaxValue * 0.5f, maxZ - minZ);
            footprint = new Bounds(centre, size);
            return true;
        }

        /// <summary> The channel width and the surface and bed levels where the river passes closest to a world position.
        /// Finds the nearest point on the station line in plan and interpolates the three figures along the segment it
        /// falls on, so a bank built between two stations gets a value that eases rather than steps. Returns false only
        /// when the reach has fewer than two stations. The design water depth is surface less bed. </summary>
        public bool TrySampleAt(float worldX, float worldZ,
            out float widthMetres, out float surfaceLevelMetres, out float bedLevelMetres)
        {
            widthMetres = 0f; surfaceLevelMetres = 0f; bedLevelMetres = 0f;
            if (stations == null || stations.Length < 2) { return false; }

            float bestSqr = float.MaxValue;
            int bestSeg = 0;
            float bestT = 0f;

            for (int i = 0; i < stations.Length - 1; i++)
            {
                float ax = stations[i].worldX, az = stations[i].worldZ;
                float dx = stations[i + 1].worldX - ax;
                float dz = stations[i + 1].worldZ - az;

                float len2 = dx * dx + dz * dz;
                float t = len2 < 1e-8f ? 0f : Mathf.Clamp01(((worldX - ax) * dx + (worldZ - az) * dz) / len2);

                float ox = worldX - (ax + dx * t);
                float oz = worldZ - (az + dz * t);
                float sqr = ox * ox + oz * oz;

                if (sqr < bestSqr) { bestSqr = sqr; bestSeg = i; bestT = t; }
            }

            RiverProfileStation a = stations[bestSeg];
            RiverProfileStation b = stations[bestSeg + 1];
            widthMetres = Mathf.Lerp(a.widthMetres, b.widthMetres, bestT);
            surfaceLevelMetres = Mathf.Lerp(a.surfaceLevelMetres, b.surfaceLevelMetres, bestT);
            bedLevelMetres = Mathf.Lerp(a.bedLevelMetres, b.bedLevelMetres, bestT);
            return true;
        }
    }
}
