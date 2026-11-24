// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    UCLightingSystemManager.Irradiance.cs  (partial of UCLightingSystemManager)
//  Purpose: THE IRRADIANCE GI GRID GO-LIVE. The grid math (IrradianceGrid, ProbeFill,
//           MultiBounceFeedback, EmissiveContributors, IrradianceUpload) and the shader
//           read (UCSampleIrradiance in UC_Lighting.hlsl, consumed by SSGI/SSR/reflection)
//           were both already shipped - but nothing filled the grid or bound its textures,
//           so UCSampleIrradiance always fell through to the engine ambient. This is the one
//           C# pass that closes that gap: it sizes the grid from the authored probeSpacing
//           (scaled by the resolved tier), fills a budgeted few probes per frame from the
//           same AtmosphereState the driver already resolved, packs them with IrradianceUpload
//           and binds _IrradianceA/_IrradianceB + the grid placement globals the shader reads.
//
//  OPT-IN, DEFAULT-OFF, ONE OWNER. settings.enableIrradianceGI gates the whole pass; OFF binds
//  no grid, so UCSampleIrradiance returns the engine ambient exactly as before (zero regression).
//  This is the only writer of the _Irradiance* globals, so there is no second grid to disagree
//  with it. The probe-count/stack dials (probeSpacing, probesPerFrame, bounceStrength,
//  multiBounce, multiBounceClamp, skyVisibilityDirections) reach the scene only through here.
//
//  HONEST SCOPE. The sizing, packing, binding and the zero-regression/tier behaviour are exact
//  and unit-tested (LIGHT-WIRE3). The per-probe RADIANCE terms (the sky, ground and bounce a
//  probe stores) are a defensible first approximation built from the resolved atmosphere and a
//  neutral ground albedo - they are NOT claimed visually final and want an in-editor sign-off.
//  The emissive cap (emissiveContributorsPerProbe) is wired through to EmissiveContributors, but
//  no author-emitter harvest feeds it yet, so it currently sums an empty set - the cap is live,
//  its input is a named follow-up, not a silent stub.
// -----------------------------------------------------------------------------

using System;
using UnityEngine;
using UniverseCreator.TerrainSystem.Lighting;

namespace UniverseCreator.TerrainSystem
{
    public partial class UCLightingSystemManager
    {
        private static readonly int s_IrrGridOriginId = Shader.PropertyToID("_IrradianceGridOrigin");
        private static readonly int s_IrrGridDimsId   = Shader.PropertyToID("_IrradianceGridDims");
        private static readonly int s_IrrBaseYId      = Shader.PropertyToID("_IrradianceBaseY");
        private static readonly int s_IrrAId          = Shader.PropertyToID(IrradianceUpload.PropIrradianceA);
        private static readonly int s_IrrBId          = Shader.PropertyToID(IrradianceUpload.PropIrradianceB);

        // UC-T08-12: bounded dirty queue, nearest observer first; unchanged lighting stops refilling and uploading.
        [NonSerialized] private IrradianceGrid _irrGrid;
        [NonSerialized] private Color[] _irrPackA, _irrPackB, irradianceBasePixels;
        [NonSerialized] private Texture3D _irrVolA, _irrVolB;
        [NonSerialized] private Texture2D _irrBaseY;
        [NonSerialized] private float _irrBuiltSpacing;
        [NonSerialized] private Vector3 _irrBuiltWorldSize;
        [NonSerialized] private bool _irrBound;
        private readonly ProbeRefreshScheduler irradianceQueue = new ProbeRefreshScheduler(); // UC-T08-12: existing deterministic owner, not a second scheduler.
        private RefreshCadence irradianceCadence;
        private readonly int[] irradianceIndices = new int[256];
        private Func<float, float, float> irradianceHeight;
        private Func<float, float, Vector3> irradianceAlbedo, irradiancePrevious;
        private Func<int, float> irradianceDistance;
        private Vector3 irradianceObserver, irradianceOrigin;
        private float irradianceElevation, irradianceCloud, irradianceTurbidity, irradianceUploadElapsed;
        private bool irradianceInputsValid, irradianceDirty, irradianceBaseDirty;
        private int irradianceSweeps;
        private long irradianceGroundRevision, irradianceGroundPublication;

        /// <summary>Hard whole-grid admission cap in probes: 4096 low, 8192 medium, 16384 high/ultra; larger grids use cached sky ambient.</summary>
        public int IrradianceProbeLimit => ResolvedTier == UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Low ? 4096 :
            ResolvedTier == UniverseCreator.Optimization.AdaptiveQuality.QualityTier.Medium ? 8192 : 16384;
        /// <summary>Probes awaiting the existing paced queue, excluding later bounded feedback sweeps; zero for an absent grid.</summary>
        public int PendingIrradianceProbes => irradianceQueue.PendingCount;

        /// <summary>True when the current graphics device supports this owner's 3D half-float volumes and base-height texture; otherwise cached ambient is used.</summary>
        public bool SupportsIrradianceVolumes => SystemInfo.supports3DTextures && SystemInfo.SupportsTextureFormat(TextureFormat.RGBAHalf) && SystemInfo.SupportsTextureFormat(TextureFormat.RHalf);

        // The empty emitter set the cap selects from until an author-emitter harvest feeds it.
        private static readonly EmissiveEmitter[] s_NoEmitters = new EmissiveEmitter[0];

        /// <summary>
        /// Fill a budgeted slice of the irradiance grid from this frame's atmosphere and bind
        /// it for the shader. Does nothing (and releases a previously bound grid) when the
        /// opt-in is off, so the shader falls back to engine ambient with zero regression.
        /// <paramref name="allowBuild"/> is false from OnValidate paths where allocating the
        /// textures would be unwelcome; an already-built grid rebinds without filling or uploading.
        /// </summary>
        /// <param name="atmo">Captured atmosphere using the weather owner's shared instant.</param>
        /// <param name="allowBuild">True only when native allocation is allowed.</param>
        /// <param name="observerPosition">Sample position in world metres for nearest-first refill priority.</param>
        /// <param name="wallDelta">Real elapsed seconds, zero for nonanimating acceptance; hitches are capped and never replayed.</param>
        private void RefreshIrradianceGrid(in AtmosphereState atmo, bool allowBuild, Vector3 observerPosition, float wallDelta)
        {
            if (!settings.enableIrradianceGI || !SupportsIrradianceVolumes) { RetireIrradianceGrid(); return; }

            UniverseCreatorObject w = World;
            if (w == null) { RetireIrradianceGrid(); return; }

            Vector3 worldSize = w.GetWorldSize();
            LightingTierSettings tier = ResolvedTierSettings;
            float spacing = settings.probeSpacing * tier.probeSpacingScale;
            if (!AdmitsIrradianceGrid(worldSize, spacing, IrradianceProbeLimit))
            { RetireIrradianceGrid(); return; } // UC-T08-12: reject nonfinite/oversized grids before integer conversion or allocation.
            spacing = Mathf.Max(1f, spacing);
            int across = IrradianceGrid.ProbesAcross(worldSize.x, spacing), deep = IrradianceGrid.ProbesAcross(worldSize.z, spacing);
            if (across > SystemInfo.maxTexture3DSize || LightingConstants.IrradianceVerticalLevels > SystemInfo.maxTexture3DSize ||
                deep * 3 > SystemInfo.maxTexture3DSize || across > SystemInfo.maxTextureSize || deep > SystemInfo.maxTextureSize)
            { RetireIrradianceGrid(); return; } // A long thin grid can exceed texture dimensions even inside the probe-count cap.

            bool needBuild = _irrGrid == null
                             || !Mathf.Approximately(spacing, _irrBuiltSpacing)
                             || (worldSize - _irrBuiltWorldSize).sqrMagnitude > 1f
                             || irradianceOrigin != w.transform.position + new Vector3(w.worldOriginOffset.x, 0, w.worldOriginOffset.y);
            if (!allowBuild) { BindViewIrradiance(); return; } // Validation never fills/uploads, even when the existing shape is unchanged.
            if (needBuild)
            {
                BuildIrradianceGrid(w, worldSize, spacing);
            }
            if (_irrGrid == null || _irrGrid.ProbeCount == 0) return;

            long groundPublication = RiverChannelRegistry.GroundPublicationVersion(w);
            if (irradianceGroundRevision != w.GenerationScheduler.CurrentRun.Revision || irradianceGroundPublication != groundPublication)
            {
                irradianceGroundRevision = w.GenerationScheduler.CurrentRun.Revision; irradianceGroundPublication = groundPublication;
                ChunkManager chunks = w.Chunks; irradianceHeight = chunks != null ? (Func<float, float, float>)chunks.SampleHeightAt : null;
                irradianceInputsValid = false; // Same-run cells dirty existing probes instead of reallocating whole grids during streaming.
            }

            irradianceObserver = RiverChannelSnapshot.Finite(observerPosition.x) && RiverChannelSnapshot.Finite(observerPosition.y) && RiverChannelSnapshot.Finite(observerPosition.z) ? observerPosition : w.transform.position;
            wallDelta = RiverChannelSnapshot.Finite(wallDelta) ? Mathf.Clamp(wallDelta, 0, 0.25f) : 0;
            if (!irradianceInputsValid || ProbeRefreshScheduler.SunMovedEnough(irradianceElevation, Mathf.Asin(Mathf.Clamp(atmo.sunDir.y, -1, 1)) * Mathf.Rad2Deg) ||
                ProbeRefreshScheduler.CloudMovedEnough(irradianceCloud, atmo.cloudCoverCombined01) || Mathf.Abs(irradianceTurbidity - atmo.turbidity) >= 0.1f)
            {
                irradianceQueue.MarkRange(0, _irrGrid.ProbeCount); irradianceSweeps = settings.multiBounce && tier.multiBounce ? 3 : 0;
                irradianceElevation = Mathf.Asin(Mathf.Clamp(atmo.sunDir.y, -1, 1)) * Mathf.Rad2Deg;
                irradianceCloud = atmo.cloudCoverCombined01; irradianceTurbidity = atmo.turbidity; irradianceInputsValid = true;
            }
            int cap = Mathf.Clamp(settings.probesPerFrame, 1, 256);
            int paced = needBuild ? cap : irradianceCadence.ProbesThisFrame(wallDelta, LightingConstants.IrradianceRefreshProbesPerSecond, cap);
            int filled = irradianceQueue.Drain(irradianceIndices, irradianceDistance, paced);
            if (filled > 0) { FillIrradianceSlice(in atmo, tier, filled); irradianceDirty = true; }
            if (irradianceQueue.PendingCount == 0 && irradianceSweeps > 0)
            { irradianceSweeps--; irradianceQueue.MarkRange(0, _irrGrid.ProbeCount); }
            irradianceUploadElapsed = Mathf.Min(0.25f, irradianceUploadElapsed + wallDelta);
            if (irradianceDirty && (_irrVolA == null || irradianceUploadElapsed >= 0.25f))
            { UploadAndBindIrradiance(); irradianceDirty = false; irradianceUploadElapsed = 0; }
            else BindViewIrradiance(); // Views and unchanged frames bind retained volumes without packing/uploading.
        }

        /// <summary>Tests finite positive dimensions/spacing against a whole-grid probe cap before allocation; invalid inputs return false.</summary>
        /// <param name="size">World dimensions in metres; X/Z must be positive finite, Y finite.</param>
        /// <param name="spacing">Positive finite horizontal metres, clamped to at least one metre.</param>
        /// <param name="limit">Positive maximum total probes across all vertical levels.</param>
        internal static bool AdmitsIrradianceGrid(Vector3 size, float spacing, int limit)
        {
            if (!RiverChannelSnapshot.Finite(size.x) || !RiverChannelSnapshot.Finite(size.y) || !RiverChannelSnapshot.Finite(size.z) ||
                !RiverChannelSnapshot.Finite(spacing) || size.x <= 0 || size.z <= 0 || spacing <= 0 || limit <= 0) return false;
            double x = Math.Floor((double)size.x / Math.Max(1, spacing)) + 1;
            double z = Math.Floor((double)size.z / Math.Max(1, spacing)) + 1;
            return x * z * LightingConstants.IrradianceVerticalLevels <= limit;
        }

        private void RetireIrradianceGrid()
        {
            // UC-T08-12: a different world's restored view bindings survive this owner's retirement.
            if (_irrBound && _irrVolA != null && Shader.GetGlobalTexture(s_IrrAId) == _irrVolA)
            {
                UnbindIrradianceGrid();
                Shader.SetGlobalTexture(s_IrrAId, null); Shader.SetGlobalTexture(s_IrrBId, null); Shader.SetGlobalTexture(s_IrrBaseYId, null);
            }
            _irrBound = false;
            ReleaseIrradianceTextures(); _irrGrid = null; _irrPackA = _irrPackB = irradianceBasePixels = null;
            irradianceHeight = null; irradianceAlbedo = irradiancePrevious = null; irradianceDistance = null;
            irradianceQueue.Clear(); irradianceCadence.Reset(); irradianceInputsValid = irradianceDirty = irradianceBaseDirty = false;
            irradianceSweeps = 0; irradianceUploadElapsed = 0;
        }

        // Lay out the grid over the whole continent at the resolved spacing, following the
        // live terrain height so each column's stack sits in the shell of air above the ground.
        private void BuildIrradianceGrid(UniverseCreatorObject w, Vector3 worldSize, float spacing)
        {
            Vector3 p = w.transform.position;
            Vector2 originXZ = new Vector2(
                p.x - worldSize.x * 0.5f + w.worldOriginOffset.x,
                p.z - worldSize.z * 0.5f + w.worldOriginOffset.y);

            ChunkManager chunks = w.Chunks;
            Func<float, float, float> heightAt = chunks != null
                ? (Func<float, float, float>)chunks.SampleHeightAt
                : null;

            _irrGrid = new IrradianceGrid(originXZ, worldSize.x, worldSize.z,
                spacing, LightingConstants.IrradianceVerticalSpacingM,
                LightingConstants.IrradianceVerticalLevels, heightAt);

            int packLen = IrradianceUpload.PackedLength(_irrGrid);
            _irrPackA = new Color[packLen];
            _irrPackB = new Color[packLen];
            irradianceBasePixels = new Color[_irrGrid.dimX * _irrGrid.dimZ];
            _irrBuiltSpacing = spacing;
            _irrBuiltWorldSize = worldSize;
            irradianceOrigin = w.transform.position + new Vector3(w.worldOriginOffset.x, 0, w.worldOriginOffset.y);
            irradianceGroundRevision = w.GenerationScheduler.CurrentRun.Revision;
            irradianceGroundPublication = RiverChannelRegistry.GroundPublicationVersion(w);
            irradianceHeight = heightAt; irradianceAlbedo = IrradianceAlbedoAt; irradiancePrevious = PreviousAmbientSampler(); irradianceDistance = IrradianceDistanceToObserver;
            irradianceQueue.Clear(); irradianceCadence.Reset(); irradianceInputsValid = false; irradianceDirty = false; irradianceUploadElapsed = 0;

            // Volumes and the per-column base-Y texture are (re)allocated to the new dims on
            // the next upload; drop the old ones so a resize cannot bind a stale shape.
            ReleaseIrradianceTextures();
        }

        // UC-T08-12: fill only drained dirty indices, capped by authored probesPerFrame. Each probe's
        // stored radiance is the sky it can see, the lit ground beneath it and the bounce from
        // its surroundings - the same terms ProbeFill composes, with the optional multi-bounce
        // and emissive terms gated by the resolved tier.
        private void FillIrradianceSlice(in AtmosphereState atmo, in LightingTierSettings tier, int budget)
        {
            Func<float, float, float> heightAt = irradianceHeight;
            Vector3 albedo = GroundAlbedoVector();
            Func<float, float, Vector3> albedoAt = irradianceAlbedo;

            // Zenith sky radiance for this atmosphere, shared by every probe this slice (the
            // ambient cube up-weights it); a Vector3 for ProbeFill.
            Color zenith = SkyModel.SkyRadiance(Vector3.up, atmo.sunDir, atmo.turbidity);
            Vector3 skyRad = new Vector3(zenith.r, zenith.g, zenith.b);

            // The direct sun on a flat lit surface, reused for the ground term.
            float ndotl = Mathf.Max(0f, atmo.sunDir.y);
            Vector3 sunCol = new Vector3(atmo.sunColor.r, atmo.sunColor.g, atmo.sunColor.b);
            Vector3 directSun = sunCol * (atmo.sunIlluminanceLux * ndotl);
            Vector3 groundRad = Vector3.Scale(albedo, directSun);

            bool mb = settings.multiBounce && tier.multiBounce;
            int emissiveCap = tier.multiBounce ? settings.emissiveContributorsPerProbe : 0;

            for (int n = 0; n < budget; n++)
            {
                int idx = irradianceIndices[n];

                _irrGrid.ToXYZ(idx, out int x, out int y, out int z);
                if (heightAt != null) irradianceBaseDirty |= _irrGrid.RefreshColumnHeight(x, z,
                    heightAt(_irrGrid.originXZ.x + x * _irrGrid.spacingM, _irrGrid.originXZ.y + z * _irrGrid.spacingM));
                Vector3 pos = _irrGrid.WorldPosition(x, y, z);

                float skyVis = ProbeFill.SkyVisibility01(pos, heightAt);
                Vector3 bounce = ProbeFill.BounceRadiance(pos, heightAt, albedoAt,
                    sunCol, atmo.sunIlluminanceLux, atmo.sunDir);

                Vector3 indirect = bounce;
                if (mb)
                {
                    Vector3 feedback = MultiBounceFeedback.FeedbackRadiance(pos,
                        irradiancePrevious, albedoAt, settings.bounceStrength);
                    indirect = MultiBounceFeedback.ClampAccumulated(bounce, feedback,
                        settings.multiBounceClamp, out bool engaged);
                    if (engaged) MultiBounceFeedback.ReportClampEngaged();
                }

                Vector3 ground = groundRad;
                if (emissiveCap > 0)
                    ground += EmissiveContributors.Accumulate(pos, s_NoEmitters, 0, emissiveCap);

                _irrGrid.probes[idx] = ProbeFill.Fill(skyRad, skyVis, ground, indirect);
            }
        }

        // Pack the grid and (re)build the two volumes + the base-Y column texture, then bind
        // the five globals the shader reads. Unsupported volume devices retain engine ambient;
        // the optional atlas path is not allocated by this owner.
        private void UploadAndBindIrradiance()
        {
            IrradianceUpload.Pack(_irrGrid, _irrPackA, _irrPackB);

            int dimX = _irrGrid.dimX, dimY = _irrGrid.dimY, dimZ = _irrGrid.dimZ;
            int depth = IrradianceUpload.VolumeDepth(_irrGrid);

            if (_irrVolA == null || _irrVolA.width != dimX || _irrVolA.height != dimY || _irrVolA.depth != depth)
            {
                ReleaseIrradianceTextures();
                _irrVolA = NewVolume(dimX, dimY, depth, "_UCIrradianceA");
                _irrVolB = NewVolume(dimX, dimY, depth, "_UCIrradianceB");
                _irrBaseY = NewBaseY(dimX, dimZ);
                irradianceBaseDirty = true;
            }
            if (irradianceBaseDirty) { FillBaseY(); irradianceBaseDirty = false; } // Reuse pixels; changed columns upload with the paced radiance publication.

            _irrVolA.SetPixels(_irrPackA); _irrVolA.Apply(false, false);
            _irrVolB.SetPixels(_irrPackB); _irrVolB.Apply(false, false);

            Shader.DisableKeyword(IrradianceUpload.KeywordAtlas);
            Shader.SetGlobalVector(s_IrrGridOriginId, new Vector4(
                _irrGrid.originXZ.x, _irrGrid.originXZ.y, _irrGrid.spacingM, _irrGrid.verticalSpacingM));
            Shader.SetGlobalVector(s_IrrGridDimsId, new Vector4(dimX, dimY, dimZ, 0f));
            Shader.SetGlobalTexture(s_IrrBaseYId, _irrBaseY);
            Shader.SetGlobalTexture(s_IrrAId, _irrVolA);
            Shader.SetGlobalTexture(s_IrrBId, _irrVolB);
            _irrBound = true;
        }

        // Unbind by zeroing the grid origin's spacing: UCSampleIrradiance reads spacing <= 0 as
        // "no grid here" and returns the engine ambient, so turning the opt-in off is lossless.
        private void UnbindIrradianceGrid()
        {
            Shader.SetGlobalVector(s_IrrGridOriginId, Vector4.zero);
            Shader.SetGlobalVector(s_IrrGridDimsId, Vector4.zero);
            _irrBound = false;
        }

        private Texture3D NewVolume(int dimX, int dimY, int depth, string name)
        {
            return new Texture3D(dimX, dimY, depth, TextureFormat.RGBAHalf, false)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave,
            };
        }

        private Texture2D NewBaseY(int dimX, int dimZ)
        {
            return new Texture2D(dimX, dimZ, TextureFormat.RHalf, false, true)
            {
                name = "_UCIrradianceBaseY",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave,
            };
        }

        // One texel per (x,z) column: the terrain height the column was built from, so the
        // shader's vertical read follows the surface rather than a flat box.
        private void FillBaseY()
        {
            int dimX = _irrGrid.dimX, dimZ = _irrGrid.dimZ;
            var px = irradianceBasePixels;
            for (int z = 0; z < dimZ; z++)
                for (int x = 0; x < dimX; x++)
                    px[z * dimX + x] = new Color(_irrGrid.ColumnBaseY(x, z), 0f, 0f, 0f);
            _irrBaseY.SetPixels(px);
            _irrBaseY.Apply(false, false);
        }

        private void ReleaseIrradianceTextures()
        {
            if (_irrVolA != null) { ReleaseLightingObject(_irrVolA); _irrVolA = null; }
            if (_irrVolB != null) { ReleaseLightingObject(_irrVolB); _irrVolB = null; }
            if (_irrBaseY != null) { ReleaseLightingObject(_irrBaseY); _irrBaseY = null; }
        }

        private Vector3 IrradianceAlbedoAt(float x, float z) => GroundAlbedoVector();

        private float IrradianceDistanceToObserver(int index)
        {
            _irrGrid.ToXYZ(index, out int x, out int y, out int z);
            return (_irrGrid.WorldPosition(x, y, z) - irradianceObserver).sqrMagnitude;
        }

        // The previous refresh's stored ambient at a world XZ, read back from the grid's own
        // probes so multi-bounce feeds on last pass's values. The up face of the column's
        // lowest level is a representative ambient for the bounce feedback.
        private Func<float, float, Vector3> PreviousAmbientSampler()
        {
            IrradianceGrid g = _irrGrid;
            if (g == null) return null;
            return (x, z) =>
            {
                int gx = Mathf.Clamp(Mathf.RoundToInt((x - g.originXZ.x) / g.spacingM), 0, g.dimX - 1);
                int gz = Mathf.Clamp(Mathf.RoundToInt((z - g.originXZ.y) / g.spacingM), 0, g.dimZ - 1);
                return g.probes[g.Index(gx, 0, gz)].posY;
            };
        }

        private Vector3 GroundAlbedoVector()
        {
            return new Vector3(GroundAlbedoDefault.r, GroundAlbedoDefault.g, GroundAlbedoDefault.b);
        }
    }
}
