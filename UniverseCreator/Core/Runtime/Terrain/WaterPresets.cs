// -----------------------------------------------------------------------------
//  Universe Creator - Module 1: Terrain Core
//  File:    WaterPresets.cs
//  Purpose: The SINGLE definition of what each water kind starts as. The editor
//           create menu and the procedural HydrologyGenerator both call this, so a
//           lake dropped by hand and a lake produced by a node are configured the
//           same way - one table, not two that drift apart. Runtime (not editor) so
//           the graph path, which cannot see editor code, can reach it too.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Nodes;

namespace UniverseCreator.TerrainSystem
{
    public static class WaterPresets
    {
        // The canonical shape for a kind. Lakes are polygons, rivers are splines,
        // oceans and pools are rectangles. One place, so no caller has to remember it.
        public static UCWaterBody.ShapeMode ShapeFor(WaterBodyKind kind)
        {
            switch (kind)
            {
                case WaterBodyKind.Lake: return UCWaterBody.ShapeMode.Polygon;
                case WaterBodyKind.Swamp: return UCWaterBody.ShapeMode.Polygon; // a still standing body, like a lake
                case WaterBodyKind.River: return UCWaterBody.ShapeMode.RiverSpline;
                default: return UCWaterBody.ShapeMode.Rectangle; // Ocean, Pool
            }
        }

        // Stamps the kind-intrinsic defaults (shape, size, appearance) onto a body
        // whose kind is already locked by SetKindOnce. Per-body geometry - a lake's
        // outline, a river's course, the world position - is the caller's to set
        // afterwards; this only writes what every body of the kind shares.
        /// <summary>Applies kind defaults to a newly created body, including a downwarped lake basin; loading existing bodies never calls this preset.</summary>
        /// <param name="body">New body whose kind has been assigned; null is a no-op. The caller supplies its position and outline.</param>
        /// <remarks>Manual creation and hydrology share these defaults; hydrology then supplies its measured formation selection.</remarks>
        public static void ApplyDefaults(UCWaterBody body)
        {
            if (body == null) return;

            body.shape = ShapeFor(body.kind);

            switch (body.kind)
            {
                case WaterBodyKind.Ocean: ApplyOcean(body); break;
                case WaterBodyKind.Lake: ApplyLake(body); break;
                case WaterBodyKind.River: ApplyRiver(body); break;
                case WaterBodyKind.Pool: ApplyPool(body); break;
                case WaterBodyKind.Swamp: ApplySwamp(body); break;
            }
        }

        // Lets the Water System Manager stamp its new-body defaults (bed source,
        // build trigger, flow) on a body created under it. No-op when there is no
        // manager - a world built before that component existed. Shared so the menu
        // and the graph path apply identical manager settings.
        public static void ApplyManagerDefaults(UCWaterBody body)
        {
            if (body == null) return;

            UCWaterSystemManager manager = body.GetComponentInParent<UCWaterSystemManager>();
            if (manager == null) return;

            manager.ApplyNewBodyDefaults(body);
        }

        private static void ApplyOcean(UCWaterBody body)
        {
            // The one body visible past the last chunk. Its own extent is what carries
            // it to the horizon - there is no separate far-field plane - so a sea that
            // ends in mid-air wants a bigger body, not another setting.
            body.sizeMetres = new Vector2(2000f, 2000f);
            body.meshResolution = 96;

            body.currentMetresPerSecond = new Vector2(0.12f, 0.07f);
            body.manningRoughness = 0.02f;
            body.maxFlowSpeed = 6f;
            body.edgeFeatherMetres = 1.5f;

            body.water.waveHeightMetres = 0.35f;
            body.water.waveLengthMetres = 42f;
            body.water.waveSpeed = 0.8f;
            body.water.depthFadeMetres = 14f;
            body.water.foamEnabled = true;
            body.water.foamEdgeWidthMetres = 2.5f;
        }

        private static void ApplyLake(UCWaterBody body)
        {
            body.lakeFormation = LakeFormation.Tectonic; // UC-T08-15: newly authored lakes start with a broad downwarped basin; legacy assets retain zero.
            body.lakeSedimentation = 0;
            body.sizeMetres = new Vector2(240f, 180f);
            body.meshResolution = 64;
            body.currentMetresPerSecond = new Vector2(0.04f, 0.02f);

            body.water.waveHeightMetres = 0.07f;
            body.water.waveLengthMetres = 14f;
            body.water.depthFadeMetres = 5f;
            body.water.foamEnabled = true;
            body.water.causticsEnabled = true;
            body.water.causticsDepthFadeMetres = 5f;
        }

        private static void ApplySwamp(UCWaterBody body)
        {
            // A swamp is a still, murky, standing body - a tuned lake with almost no wave
            // motion and a shallow depth-fade so the tannin-dark water reads as opaque, and
            // no caustics (light does not reach the peat floor).
            body.sizeMetres = new Vector2(200f, 160f);
            body.meshResolution = 48;
            body.currentMetresPerSecond = new Vector2(0.01f, 0.005f);

            body.water.waveHeightMetres = 0.02f;
            body.water.waveLengthMetres = 8f;
            body.water.depthFadeMetres = 1.5f;
            body.water.foamEnabled = false;
            body.water.causticsEnabled = false;
        }

        // UC-T08-15: course geometry is authored by the caller and solved by the existing river owners.
        // This shared preset supplies appearance and hydraulic defaults without replacing an authored course.
        private static void ApplyRiver(UCWaterBody body)
        {
            body.sizeMetres = new Vector2(300f, 300f);
            body.manningRoughness = 0.035f;
            body.maxFlowSpeed = 4f;

            body.water.waveHeightMetres = 0.04f;
            body.water.waveLengthMetres = 6f;
            body.water.depthFadeMetres = 2.5f;
            body.water.foamEnabled = true;
            body.water.foamEdgeWidthMetres = 0.8f;
            body.water.causticsEnabled = true;
            body.water.causticsDepthFadeMetres = 3f;
        }

        private static void ApplyPool(UCWaterBody body)
        {
            body.sizeMetres = new Vector2(8f, 4f);
            body.meshResolution = 24;

            // A built pool has a floor, not a terrain. Reading the ground here would
            // put the water at whatever the landscape does under the building. The
            // manager leaves a Pool's bed source alone for the same reason.
            body.bedSource = UCWaterBody.BedSource.FlatFloor;
            body.flatFloorDepthMetres = 1.6f;

            body.currentMetresPerSecond = Vector2.zero;
            body.edgeFeatherMetres = 0.15f;
            body.minDepthMetres = 0.02f;

            body.water.waveHeightMetres = 0.015f;
            body.water.waveLengthMetres = 3f;
            body.water.waveSpeed = 0.35f;
            body.water.depthFadeMetres = 2f;
            body.water.transparency = 0.9f;
            body.water.causticsEnabled = true;
            body.water.causticsDepthFadeMetres = 2f;
            body.water.causticsIntensity = 0.9f;
        }
    }
}
