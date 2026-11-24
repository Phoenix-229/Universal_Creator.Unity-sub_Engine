// UC-T08-11: camera identity, temporal invalidation and existing native pass ownership.
#if UC_URP
using System;
using System.Reflection;
using UnityEngine;
using UniverseCreator.TerrainSystem;
using UniverseCreator.TerrainSystem.Lighting;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private sealed class HistoryLifetimeFixture : IDisposable
        {
            internal bool Retired;
            public void Dispose() { Retired = true; }
        }

        private static void TestCameraLightingHistories()
        {
            var root = new GameObject("UC camera lighting histories"); root.SetActive(false);
            try
            {
                var world = root.AddComponent<UniverseCreatorObject>(); world.enabled = false; world.autoRegenerate = false;
                var weather = root.AddComponent<UCWeatherSystemManager>(); weather.enabled = false;
                Camera[] cameras = new Camera[9];
                for (int i = 0; i < cameras.Length; i++) { var go = new GameObject("UC history camera " + i); go.transform.SetParent(root.transform, false); cameras[i] = go.AddComponent<Camera>(); }
                root.SetActive(true);
                using (var owners = new LightingCameraPasses<HistoryLifetimeFixture>(() => new HistoryLifetimeFixture()))
                {
                    var first = owners.For(cameras[0]); var second = owners.For(cameras[1]);
                    Check(first == owners.For(cameras[0]) && first != second, "UC-T08-11 separate camera histories retain stable independent owners.");
                    for (int i = 2; i < 8; i++) owners.For(cameras[i]);
                    Check(owners.For(cameras[8]) == null, "UC-T08-11 nine live cameras cannot exceed the eight-history cap.");
                    cameras[0].enabled = false;
                    Check(owners.For(cameras[8]) != null && first.Retired, "UC-T08-11 disabled cameras release their owner before camera admission.");
                    owners.Clear(); Check(second.Retired, "UC-T08-11 scene/reset retirement disposes all resident histories.");
                    var resident = owners.For(cameras[1]); cameras[1].enabled = false; owners.RetireInactive();
                    Check(resident.Retired, "UC-T08-11 disabled cameras retire without another camera admission."); cameras[1].enabled = true;
                }
                cameras[0].enabled = true;
                var key = new LightingHistoryKey(); var descriptor = new RenderTextureDescriptor(128, 64);
                Matrix4x4 projection = cameras[0].nonJitteredProjectionMatrix; Vector4 settings = new Vector4(1, 2, 3, 4);
                Check(key.Changed(cameras[0], descriptor, projection, 10, settings) && !key.Changed(cameras[0], descriptor, projection, 11, settings),
                    "UC-T08-11 first frame rejects history; unchanged consecutive camera frames retain it.");
                descriptor.width = 256; Check(key.Changed(cameras[0], descriptor, projection, 12, settings), "UC-T08-11 resizing one camera rejects only its own history.");
                cameras[0].transform.position = new Vector3(100, 0, 0);
                Check(key.Changed(cameras[0], descriptor, projection, 13, settings), "UC-T08-11 camera teleport invalidates reprojected samples.");
                weather.Advance(1);
                Check(key.Changed(cameras[0], descriptor, projection, 14, settings), "UC-T08-11 an explicit shared-clock seek invalidates camera lighting history.");
                Check(key.Changed(cameras[0], descriptor, projection, 16, settings), "UC-T08-11 skipped frames reject stale temporal samples.");
                Check(key.Changed(cameras[0], descriptor, projection, 17, Vector4.zero), "UC-T08-11 changed effective quality/settings reject obsolete history.");
                var sibling = new GameObject("UC sibling lighting owner"); sibling.transform.SetParent(root.transform, false);
                var lighting = sibling.AddComponent<UCLightingSystemManager>(); lighting.enabled = false;
                typeof(LightingHistoryKey).GetField("nextOwnerDiscovery", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(key, 0f);
                Check(key.LightingFor(cameras[0]) == lighting, "UC-T08-12 late-added sibling lighting is discovered through the retained nested-world-safe lookup.");
                UnityEngine.Object.DestroyImmediate(sibling);
                Check(key.LightingFor(cameras[0]) == null, "UC-T08-12 destroyed sibling lighting is retired immediately.");
                foreach (Type featureType in new[] { typeof(ScreenSpaceReflectionFeature), typeof(ScreenSpaceGIFeature), typeof(ContactShadowFeature) })
                {
                    var feature = (UnityEngine.Rendering.Universal.ScriptableRendererFeature)ScriptableObject.CreateInstance(featureType);
                    try
                    {
                        feature.Create();
                        var field = featureType.GetField("m_Passes", BindingFlags.Instance | BindingFlags.NonPublic);
                        object cache = field.GetValue(feature); MethodInfo resolve = cache.GetType().GetMethod("For", BindingFlags.Instance | BindingFlags.NonPublic);
                        object a = resolve.Invoke(cache, new object[] { cameras[0] }), b = resolve.Invoke(cache, new object[] { cameras[1] });
                        var buffers = a.GetType().GetField("m_History", BindingFlags.Instance | BindingFlags.NonPublic);
                        Check(a != b && buffers.GetValue(a) != buffers.GetValue(b), "UC-T08-11 actual URP pass histories are independent: " + featureType.Name);
                        if (featureType == typeof(ScreenSpaceGIFeature))
                        {
                            var authored = new UniverseCreator.TerrainSystem.Lighting.LightingSettings { ssgiSampleCount = 12, ssgiTemporalWeight = 0.8f };
                            a.GetType().GetMethod("Setup").Invoke(a, new object[] { null, new ScreenSpaceGIFeature.Settings { ssgiSampleCount = 8 }, authored, 6 });
                            Check((int)a.GetType().GetField("m_SampleCount", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(a) == 6 &&
                                Mathf.Approximately((float)a.GetType().GetField("m_TemporalWeight", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(a), 0.8f),
                                "UC-T08-12 actual SSGI setup honors the lowest sample cap and world-authored temporal weight.");
                        }
                        if (featureType == typeof(ContactShadowFeature))
                        {
                            var shared = new ContactShadowFeature.Settings(); var setup = a.GetType().GetMethod("Setup");
                            setup.Invoke(a, new object[] { null, shared, 0.25f }); setup.Invoke(b, new object[] { null, shared, 1.5f });
                            var stored = a.GetType().GetField("m_Settings", BindingFlags.Instance | BindingFlags.NonPublic);
                            Check(Mathf.Approximately(((ContactShadowFeature.Settings)stored.GetValue(a)).contactShadowLength, 0.25f) &&
                                Mathf.Approximately(((ContactShadowFeature.Settings)stored.GetValue(b)).contactShadowLength, 1.5f) && !ReferenceEquals(stored.GetValue(a), shared),
                                "UC-T08-12 sibling contact passes keep independent world lengths without changing shared feature settings.");
                        }
                        feature.Create();
                        Check(resolve.Invoke(cache, new object[] { cameras[0] }) == null, "UC-T08-11 renderer recreation retires its previous pass cache: " + featureType.Name);
                        cache = field.GetValue(feature); a = resolve.Invoke(cache, new object[] { cameras[0] });
                        feature.SetActive(false); cache.GetType().GetMethod("RetireInactive", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(cache, null);
                        feature.SetActive(true);
                        Check(a != resolve.Invoke(cache, new object[] { cameras[0] }), "UC-T08-11 runtime feature disable retires resident passes: " + featureType.Name);
                        UnityEngine.Object.DestroyImmediate(feature);
                        Check(resolve.Invoke(cache, new object[] { cameras[0] }) == null, "UC-T08-11 feature destruction unsubscribes and disposes its cache: " + featureType.Name);
                    }
                    finally { UnityEngine.Object.DestroyImmediate(feature); }
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
#endif
