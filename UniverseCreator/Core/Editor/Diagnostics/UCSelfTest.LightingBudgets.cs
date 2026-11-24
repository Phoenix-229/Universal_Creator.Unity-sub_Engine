// UC-T08-12: real owner admission, validation/retirement and bounded paced work; GPU costs remain device acceptance.
using System;
using System.Reflection;
using UnityEngine;
using UniverseCreator.Optimization;
using UniverseCreator.TerrainSystem;
using UniverseCreator.TerrainSystem.Lighting;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestLightingRefreshBudgets()
        {
            var root = new GameObject("UC lighting refresh budget"); root.SetActive(false);
            UniverseCreatorObject world = null;
            Vector4 incoming = Shader.GetGlobalVector("_IrradianceGridOrigin");
            Texture incomingVolume = Shader.GetGlobalTexture("_IrradianceA");
            var foreignVolume = new Texture2D(1, 1);
            try
            {
                world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false; world.worldSizeKm = 0.032f;
                var lighting = root.AddComponent<UCLightingSystemManager>(); lighting.enabled = false; lighting.followAdaptiveQuality = false;
                lighting.lightingTierOverride = AdaptiveQuality.QualityTier.Low;
                lighting.settings.enableIrradianceGI = true; lighting.settings.probeSpacing = 16; lighting.settings.probesPerFrame = 3; lighting.settings.multiBounce = false;
                Check(lighting.IrradianceProbeLimit == 4096 && !lighting.ResolvedTierSettings.AnyScreenSpaceEffect,
                    "UC-T08-12 low tier retains ambient with no expensive camera passes.");
                lighting.lightingTierOverride = AdaptiveQuality.QualityTier.Medium;
                Check(lighting.IrradianceProbeLimit == 8192 && !lighting.ResolvedTierSettings.screenSpaceGI && lighting.ResolvedTierSettings.contactShadows,
                    "UC-T08-12 medium keeps its existing contact-only screen-space envelope.");
                lighting.lightingTierOverride = AdaptiveQuality.QualityTier.Ultra;
                Check(lighting.IrradianceProbeLimit == 16384 && lighting.ResolvedTierSettings.screenSpaceReflections,
                    "UC-T08-12 optional richest tier admits SSR within the hard probe limit.");
                lighting.lightingTierOverride = AdaptiveQuality.QualityTier.Low;
                Check(UCLightingSystemManager.AdmitsIrradianceGrid(new Vector3(31, 100, 31), 1, 4096) &&
                    !UCLightingSystemManager.AdmitsIrradianceGrid(new Vector3(32, 100, 32), 1, 4096),
                    "UC-T08-12 inclusive whole-grid admission enforces the exact vertical probe cap.");
                foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.MaxValue, -1f, 0f })
                    Check(!UCLightingSystemManager.AdmitsIrradianceGrid(new Vector3(invalid, 100, 32), 16, 4096),
                        "UC-T08-12 invalid/extreme dimensions cannot overflow or allocate: " + invalid);
                Check(!UCLightingSystemManager.AdmitsIrradianceGrid(Vector3.one, float.NaN, 4096), "UC-T08-12 invalid authored spacing is rejected.");
                world.worldSizeKm = 1e20f;
                Check(lighting.EstimatedProbeCount == long.MaxValue && lighting.EstimatedIrradianceBytes == long.MaxValue,
                    "UC-T08-12 extreme inspector previews saturate instead of wrapping negative.");
                world.worldSizeKm = 0.032f;
                var columnGrid = new IrradianceGrid(Vector2.zero, 32, 32, 16, 8, 4, null);
                var probeStorage = columnGrid.probes;
                Check(columnGrid.RefreshColumnHeight(1, 1, 50) && columnGrid.WorldPosition(1, 2, 1).y == 66 &&
                    !columnGrid.RefreshColumnHeight(1, 1, float.NaN) && ReferenceEquals(probeStorage, columnGrid.probes),
                    "UC-T08-12 paced ground updates preserve probe storage and reject invalid heights.");

                var queue = new ProbeRefreshScheduler(); var cadence = new RefreshCadence(); var indices = new int[3];
                for (int i = 15; i >= 0; i--) { queue.MarkDirty(i); queue.MarkDirty(i); }
                int take = queue.Drain(indices, i => Mathf.Abs(i - 8), cadence.ProbesThisFrame(1f / 60, 1800, 3));
                Check(take == 3 && indices[0] == 8 && indices[1] == 7 && indices[2] == 9 && queue.PendingCount == 13,
                    "UC-T08-12 paced owner work is capped and nearest-first with deterministic ties and no duplicate fills.");
                while (queue.PendingCount > 0) queue.Drain(indices, null, 3);
                Check(queue.Drain(indices, null, 3) == 0, "UC-T08-12 an unchanged settled queue performs no refill work.");
                Check(cadence.ProbesThisFrame(float.NaN, 1800, 3) == 0 && cadence.ProbesThisFrame(float.MaxValue, float.MaxValue, 3) == 3 &&
                    cadence.ProbesThisFrame(0, 1800, 3) == 0, "UC-T08-12 invalid time does not poison pacing; extreme hitches remain capped without replay.");

                BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
                Type owner = typeof(UCLightingSystemManager);
                var build = owner.GetMethod("BuildIrradianceGrid", hidden);
                var refresh = owner.GetMethod("RefreshIrradianceGrid", hidden);
                var gridField = owner.GetField("_irrGrid", hidden);
                var atmo = AtmosphereState.From(default, default, Color.gray, 0f); // UC-T08-14: retain the immutable production snapshot factory.
                build.Invoke(lighting, new object[] { world, world.GetWorldSize(), 32f });
                object originalGrid = gridField.GetValue(lighting);
                refresh.Invoke(lighting, new object[] { atmo, false, Vector3.zero, 1f });
                Check(lighting.SupportsIrradianceVolumes ? ReferenceEquals(gridField.GetValue(lighting), originalGrid) && lighting.PendingIrradianceProbes == 0 : gridField.GetValue(lighting) == null,
                    "UC-T08-12 validation does not fill/upload; unsupported device retires the grid to ambient.");
                owner.GetMethod("RetireIrradianceGrid", hidden).Invoke(lighting, null);

                var run = world.GenerationScheduler.BeginRun(); var bounds = new Rect(-16, -16, 32, 32);
                var snapshot = new RiverChannelSnapshot(run, "probe-ground", bounds, Array.Empty<UniverseCreator.Nodes.ChannelGraphPublication>(), new float[3, 3], 0, 100);
                Check(RiverChannelRegistry.ReplaceOwned(world.GenerationScheduler, run, "probe-ground", bounds, snapshot.Channels, null, snapshot, world),
                    "UC-T08-12 accepted ground publishes through the retained registry owner.");
                long first = RiverChannelRegistry.GroundPublicationVersion(world);
                RiverChannelRegistry.ReplaceOwned(world.GenerationScheduler, run, "probe-ground", bounds, snapshot.Channels, null, snapshot, world);
                Check(RiverChannelRegistry.GroundPublicationVersion(world) > first && world.GenerationScheduler.CurrentRun.Equals(run),
                    "UC-T08-12 same-run accepted ground replacement invalidates probe placement without a second generation run.");
                refresh.Invoke(lighting, new object[] { atmo, true, Vector3.zero, 0f });
                if (lighting.SupportsIrradianceVolumes)
                {
                    var active = (IrradianceGrid)gridField.GetValue(lighting);
                    Check(active != null && lighting.PendingIrradianceProbes == active.ProbeCount - 3,
                        "UC-T08-12 initial production grid fills only one authored capped slice.");
                    for (int i = 0; i < 20; i++) refresh.Invoke(lighting, new object[] { atmo, true, Vector3.zero, 0.25f });
                    Check(lighting.PendingIrradianceProbes == 0 && !((bool)owner.GetField("irradianceDirty", hidden).GetValue(lighting)),
                        "UC-T08-12 unchanged production lighting settles refills and volume uploads.");
                    RiverChannelRegistry.ReplaceOwned(world.GenerationScheduler, run, "probe-ground", bounds, snapshot.Channels, null, snapshot, world);
                    refresh.Invoke(lighting, new object[] { atmo, true, Vector3.zero, 0.01f });
                    Check(ReferenceEquals(active, gridField.GetValue(lighting)) && lighting.PendingIrradianceProbes > 0,
                        "UC-T08-12 same-run publication dirties retained production storage instead of reallocating during streaming.");
                    owner.GetMethod("ResetEnvironmentLighting", hidden).Invoke(lighting, null);
                    refresh.Invoke(lighting, new object[] { atmo, true, Vector3.zero, 0.01f });
                    Check(lighting.PendingIrradianceProbes > 0, "UC-T08-12 explicit reset requeues existing probes without allocating a second grid.");
                }
                else Check(gridField.GetValue(lighting) == null && lighting.PendingIrradianceProbes == 0,
                    "UC-T08-12 actual device capability fallback allocates no unsupported native volumes.");
                lighting.settings.enableIrradianceGI = false;
                refresh.Invoke(lighting, new object[] { atmo, true, Vector3.zero, 1f });
                Check(gridField.GetValue(lighting) == null && lighting.PendingIrradianceProbes == 0 && owner.GetField("irradiancePrevious", hidden).GetValue(lighting) == null,
                    "UC-T08-12 opt-out retires textures, pending work and delegates retaining the former grid.");
                Shader.SetGlobalTexture("_IrradianceA", foreignVolume);
                Shader.SetGlobalVector("_IrradianceGridOrigin", new Vector4(10, 20, 30, 40));
                owner.GetField("_irrBound", hidden).SetValue(lighting, true);
                owner.GetMethod("RetireIrradianceGrid", hidden).Invoke(lighting, null);
                Check(Shader.GetGlobalTexture("_IrradianceA") == foreignVolume && Shader.GetGlobalVector("_IrradianceGridOrigin") == new Vector4(10, 20, 30, 40),
                    "UC-T08-12 retiring one owner preserves another world's incoming volume bindings.");
                RiverChannelRegistry.Withdraw(run.WorldId);
                Check(RiverChannelRegistry.GroundPublicationVersion(world) == 0, "UC-T08-12 withdrawn worlds retain no publication stamp.");
            }
            finally
            {
                if (world != null) RiverChannelRegistry.Withdraw(world.GenerationScheduler.CurrentRun.WorldId);
                UnityEngine.Object.DestroyImmediate(root); Shader.SetGlobalVector("_IrradianceGridOrigin", incoming);
                Shader.SetGlobalTexture("_IrradianceA", incomingVolume); UnityEngine.Object.DestroyImmediate(foreignVolume);
            }
        }
    }
}
