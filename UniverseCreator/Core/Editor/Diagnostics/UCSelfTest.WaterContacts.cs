// UC-T04-09A/10: contact ordering/rate/retirement and deterministic refusal of the racing GPU kernel.
using UnityEngine;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestWaterContactEvents()
        {
            var root = new GameObject("UC water contact event fixture"); root.SetActive(false);
            try
            {
                var river = root.AddComponent<UCWaterVolume>(); river.kind = UCWaterVolume.BodyKind.River;
                var tracker = new WaterContactTracker();
                var sample = new WaterSample { volume = river, submerged = true, depth = 0.1f, waterDepth = 2, velocity = Vector3.forward };
                var change = tracker.Step(sample, Vector3.zero, Vector3.down * 3, 0, false, 1.5f);
                Check((change.Kind & (WaterContactEventKind.Enter | WaterContactEventKind.Splash)) == (WaterContactEventKind.Enter | WaterContactEventKind.Splash) &&
                    change.State.Kind == UCWaterVolume.BodyKind.River && change.State.Wet && !change.State.Swimming,
                    "UC-T04-09A river entry emits one ordered contact/splash snapshot with local body/depth state.");
                int wakes = 0;
                for (int i = 1; i <= 1000; i++)
                {
                    change = tracker.Step(sample, Vector3.zero, Vector3.zero, i * 0.001f, true, 1.5f);
                    if ((change.Kind & WaterContactEventKind.Wake) != 0) wakes++;
                    Check((change.Kind & WaterContactEventKind.Enter) == 0, "UC-T04-09A sustained swimming cannot duplicate entry events.");
                }
                Check(wakes <= 8 && tracker.Current.Swimming && tracker.Current.Current == Vector3.forward,
                    "UC-T04-09A wakes are limited to eight per second and stationary contacts still see current-relative flow.");
                change = tracker.Reset();
                Check(change.Kind == WaterContactEventKind.Exit && !change.State.Wet && tracker.Reset().Kind == WaterContactEventKind.None,
                    "UC-T04-09A disable/unload emits one exit and retires swimming/current state without duplicate notifications.");
                sample.submerged = false;
                Check(tracker.Step(sample, Vector3.zero, Vector3.zero, 2, false, 1).Kind == WaterContactEventKind.None,
                    "UC-T04-09A a dry-bank/above-water sample cannot create contact or sound events.");
                sample.submerged = true;
                foreach (UCWaterVolume.BodyKind kind in System.Enum.GetValues(typeof(UCWaterVolume.BodyKind)))
                {
                    tracker.Reset(); river.kind = kind;
                    change = tracker.Step(sample, Vector3.zero, Vector3.zero, 3, false, 1);
                    Check(change.State.Wet && change.State.Kind == kind && (change.Kind & WaterContactEventKind.Enter) != 0,
                        "UC-T04-09A every existing water kind re-enters through the same accepted contact contract.");
                }
                Check(!DropletErosionGPU.HasValidatedKernel && !DropletErosionGPU.IsSupported,
                    "UC-T04-10 the unprotected shared-write GPU kernel cannot be enabled by a hardware capability flag.");
                TestOptionalWaterAudio(); // UC-T04-09A: production clip routing, process-wide voice cap and lifecycle release.
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static void TestOptionalWaterAudio()
        {
            var roots = new System.Collections.Generic.List<GameObject>();
            // UC acceptance: invoke the real hook directly; SendMessage rejects non-ExecuteAlways behaviours in Edit mode.
            var disable = typeof(UCWaterAudio).GetMethod("OnDisable", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var loop = AudioClip.Create("UC synthetic routing fixture", 32, 1, 8000, false);
            var splash = AudioClip.Create("UC synthetic splash routing fixture", 32, 1, 8000, false);
            int baseline = UCWaterAudio.AudibleActors;
            try
            {
                var root = new GameObject("UC optional contact audio"); root.SetActive(false); roots.Add(root);
                var audio = root.AddComponent<UCWaterAudio>();
                root.SetActive(true);
                var contact = new WaterContactState(true, false, UCWaterVolume.BodyKind.River, Vector3.zero, 0.2f, 1, Vector3.forward * 2, 1);
                audio.RefreshContact(contact, 0);
                Check(root.GetComponentsInChildren<AudioSource>(true).Length == 0 && UCWaterAudio.AudibleActors == baseline,
                    "UC-T04-09A absent licensed clips allocate no voices and stay silent.");
                audio.fallbackClips = new WaterAudioClips { ambience = loop, splash = splash, entry = loop, swimming = splash };
                audio.maxDistanceMetres = float.NaN; audio.volume = float.PositiveInfinity;
                audio.ReceiveContact(new WaterContactEvent(WaterContactEventKind.Enter | WaterContactEventKind.Splash, contact), 1);
                var sources = root.GetComponentsInChildren<AudioSource>(true);
                Check(sources.Length == 2 && sources[0].clip == loop && sources[1].clip == splash &&
                    sources[0].spatialBlend == 1 && sources[0].maxDistance == 30 && sources[1].volume == 0.5f,
                    "UC-T04-09A combined entry/splash selects one spatial transition voice with finite controls and an independent loop.");
                audio.ReceiveContact(new WaterContactEvent(WaterContactEventKind.Enter, contact), 1.01f);
                Check(sources[1].clip == splash,
                    "UC-T04-09A repeated entry audio cannot replace the transition voice inside its eight-Hz budget.");
                var swimming = new WaterContactState(true, true, UCWaterVolume.BodyKind.River, Vector3.zero, 1, 2, Vector3.zero, 1);
                Check(audio.LoopFor(swimming) == splash, "UC-T04-09A swimming uses accepted swimming state rather than another water query.");
                foreach (UCWaterVolume.BodyKind kind in System.Enum.GetValues(typeof(UCWaterVolume.BodyKind)))
                    Check(audio.LoopFor(new WaterContactState(true, false, kind, Vector3.zero, 1, 2, Vector3.zero, 0)) == loop,
                        "UC-T04-09A every existing water kind can use optional fallback licensed ambience.");
                for (int i = 1; i <= UCWaterAudio.MaxAudibleActors; i++)
                {
                    var actor = new GameObject("UC bounded audio actor"); actor.SetActive(false); roots.Add(actor);
                    var other = actor.AddComponent<UCWaterAudio>(); other.fallbackClips = audio.fallbackClips;
                    actor.SetActive(true);
                    other.RefreshContact(contact, 2);
                }
                Check(UCWaterAudio.AudibleActors == UCWaterAudio.MaxAudibleActors &&
                    roots[roots.Count - 1].GetComponentsInChildren<AudioSource>(true).Length == 0,
                    "UC-T04-09A process-wide admission caps contact audio at sixteen actors and refuses extra native voices.");
                disable.Invoke(audio, null); root.SetActive(false);
                Check(sources[0].clip == null && sources[1].clip == null && UCWaterAudio.AudibleActors == UCWaterAudio.MaxAudibleActors - 1,
                    "UC-T04-09A disable unsubscribes, clears both clips and releases its admission exactly once.");
            }
            finally
            {
                foreach (var root in roots) { disable.Invoke(root.GetComponent<UCWaterAudio>(), null); UnityEngine.Object.DestroyImmediate(root); }
                UnityEngine.Object.DestroyImmediate(loop); UnityEngine.Object.DestroyImmediate(splash);
            }
            Check(UCWaterAudio.AudibleActors == baseline, "UC-T04-09A actor destruction retires every admitted voice without leaked budget slots.");
        }
    }
}
