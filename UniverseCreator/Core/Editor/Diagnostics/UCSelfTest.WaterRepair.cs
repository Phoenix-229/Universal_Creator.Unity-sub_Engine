// UC-T08-15: accepted water survives coastal guards; saved manual cuts remain sparse terrain intent.
using System;
using System.Reflection;
using UnityEngine;
using UniverseCreator.Nodes;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Diagnostics
{
    public static partial class UCSelfTest
    {
        private static void TestWaterRepairContracts()
        {
            TestAcceptedLakeGuards();
            TestOccupiedLakeGeometry();
            TestSavedManualRiverCut();
            TestLakeFormationContracts();
            TestLakeFormationSelection();
            TestLakeFormationSamplingCost();
        }

        private static void TestAcceptedLakeGuards()
        {
            const int res = 17;
            var height = new Matrix(new CoordRect(0, 0, res, res));
            height.worldPos = Vector3.zero;
            height.worldSize = new Vector3(1600, 600, 1600);
            for (int i = 0; i < height.arr.Length; i++) height.arr[i] = 0.42f;
            for (int z = 0; z < res; z++) height.arr[z * res] = 0.2f;
            int lake = 8 * res + 8;
            height.arr[lake] = 0.31f;
            var filled = (float[])height.arr.Clone(); filled[lake] = 0.43f;
            var wd = WorldDrainage.CreateForTests(height, filled);
            var mask = new float[height.arr.Length]; mask[lake] = 1;
            var work = new Matrix(height);
            SeaLevelFloor.Apply(wd, work, 0.35f, 0.01f, 0.02f, 0.04f, 1, null, mask, 300, 80);
            CoastalShelf.Apply(wd, work, 0.35f, 1200, 10, 40, 0.03f, 0.05f, 0.6f, 1, 0.04f, null, mask, 300, 80);
            ContinentalRise.Apply(wd, work, 0.35f, 0.04f, 0.1f, 1000, 0.02f, 300, 80, mask, 1, null);
            CheckClose(work.arr[lake], height.arr[lake], "UC-T08-15 accepted sub-sea lake bed remains at its original elevation through all three guards.");
            CheckClose(work.arr[8 * res], 0.2f, "UC-T08-15 connected ocean depth survives all guards.");
            Check(mask[lake] == 1 && filled[lake] == 0.43f,
                "UC-T08-15 guard passes retain immutable accepted mask and drainage inputs.");

            bool continuous = true;
            const float floor = 0.39f, band = 0.01f, step = 0.000001f;
            float previous = ContinentalRise.SoftMax(floor - band * 2, floor, band);
            for (int i = 1; i <= 40000; i++)
            {
                float h = floor - band * 2 + i * step;
                float value = ContinentalRise.SoftMax(h, floor, band);
                continuous &= value >= h && value >= floor && value >= previous - step && value - previous < step * 1.2f;
                previous = value;
            }
            Check(continuous, "UC-T08-15 compact guard maximum has no threshold jump, is monotonic and never lowers either input.");
            CheckClose(ContinentalRise.SoftMax(floor + 2 * band, floor, band), floor + 2 * band,
                "UC-T08-15 satisfied high ground receives exactly zero guard lift.");
            Check(SeaLevelFloor.ShoreRelease(80, 300, 80) == 0 && SeaLevelFloor.ShoreRelease(380, 300, 80) == 1 &&
                Mathf.Abs(SeaLevelFloor.ShoreRelease(80.01f, 300, 80)) < 1e-6f,
                "UC-T08-15 shore release has continuous height and slope at the deadzone in world metres.");
        }

        private static void TestOccupiedLakeGeometry()
        {
            MethodInfo overlaps = typeof(HydrologyGenerator).GetMethod("LakeFootprintsOverlap", BindingFlags.NonPublic | BindingFlags.Static);
            Check(overlaps != null, "UC-T08-15 occupied-basin acceptance uses the existing generation owner.");
            if (overlaps == null) return;
            Vector2[] square = { new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 10), new Vector2(0, 10) };
            Vector2[] inside = { new Vector2(2, 2), new Vector2(3, 2), new Vector2(3, 3), new Vector2(2, 3) };
            Vector2[] crossing = { new Vector2(-1, 4), new Vector2(11, 4), new Vector2(11, 6), new Vector2(-1, 6) };
            Vector2[] touching = { new Vector2(10, 2), new Vector2(12, 2), new Vector2(12, 4), new Vector2(10, 4) };
            Vector2[] concave = { new Vector2(0, 0), new Vector2(10, 0), new Vector2(10, 2), new Vector2(2, 2), new Vector2(2, 10), new Vector2(0, 10) };
            Vector2[] separate = { new Vector2(4, 4), new Vector2(6, 4), new Vector2(6, 6), new Vector2(4, 6) };
            bool Overlap(Vector2[] a, Vector2[] b)
            {
                Bounds Bound(Vector2[] ring)
                {
                    var bounds = new Bounds(new Vector3(ring[0].x, 0, ring[0].y), Vector3.zero);
                    foreach (var p in ring) bounds.Encapsulate(new Vector3(p.x, 0, p.y));
                    return bounds;
                }
                return (bool)overlaps.Invoke(null, new object[] { a, Bound(a), b, Bound(b) });
            }
            Check(Overlap(square, square) && Overlap(square, inside) && Overlap(square, crossing) && Overlap(square, touching),
                "UC-T08-15 generation rejects duplicate, contained, crossing and touching occupied shores.");
            Check(!Overlap(concave, separate), "UC-T08-15 distinct concave basins are accepted despite overlapping bounding rectangles.");
        }

        private static void TestSavedManualRiverCut()
        {
            const int res = 33;
            var bounds = new Bounds(new Vector3(50, 300, 50), new Vector3(100, 600, 100));
            var baseline = new float[res, res];
            for (int z = 0; z < res; z++) for (int x = 0; x < res; x++) baseline[z, x] = 0.5f;
            var edited = (float[,])baseline.Clone();
            for (int z = 2; z < res - 2; z++) edited[z, 14 + z / 10] -= 0.01f + z * 0.0001f;
            var layer = new TerrainOverrideLayer();
            Check(layer.CaptureDifference(edited, baseline, bounds, 3.125f) == 29,
                "UC-T08-15 Capture Manual Edits records an unconnected hand-cut river as sparse signed terrain edits.");
            var replacement = (float[,])baseline.Clone();
            layer.ApplyTo(replacement, bounds, 3.125f);
            bool same = true;
            for (int z = 0; z < res; z++) for (int x = 0; x < res; x++) same &= Mathf.Abs(replacement[z, x] - edited[z, x]) < 1e-5f;
            Check(same, "UC-T08-15 saved manual trench survives a procedural rebuild without any River Course node.");
            Check(layer.CaptureDifference(replacement, edited, bounds, 3.125f) == 0,
                "UC-T08-15 accepting the captured trench as baseline avoids counting the same manual cut twice.");
        }

        private static void TestLakeFormationContracts()
        {
            var presetObject = new GameObject("UC formation preset fixture"); presetObject.SetActive(false);
            try
            {
                var body = presetObject.AddComponent<UCWaterBody>(); body.SetKindOnce(WaterBodyKind.Lake);
                Check(body.lakeFormation == LakeFormation.Legacy, "UC-T08-15 absent serialized formation retains the old lake profile.");
                WaterPresets.ApplyDefaults(body);
                Check(body.lakeFormation == LakeFormation.Tectonic && body.lakeSedimentation == 0,
                    "UC-T08-15 new manual lake presets use a formation without migrating existing lake assets.");
            }
            finally { UnityEngine.Object.DestroyImmediate(presetObject); }
            var ring = new System.Collections.Generic.List<Vector3>();
            for (int i = 0; i < 64; i++)
            {
                float angle = i * Mathf.PI * 2 / 64;
                ring.Add(new Vector3(120 * Mathf.Cos(angle), 100, 80 * Mathf.Sin(angle)));
            }
            var legacy = LakeFormationMath.Prepare(LakeFormation.Legacy, 1, ring);
            Check(legacy.Fraction(0.6f, 0.37f, 17, 12) == 0.37f && legacy.Sedimentation == 0,
                "UC-T08-15 legacy profiles ignore new sedimentation and preserve their original bed and roughness.");
            var signatures = new System.Collections.Generic.List<float[]>();
            for (int family = 1; family <= 11; family++)
            {
                var prepared = LakeFormationMath.Prepare((LakeFormation)family, 0, ring);
                var settled = LakeFormationMath.Prepare((LakeFormation)family, 1, ring);
                var carve = new WaterCarveLayer { surfaceY = 100, maxDepthMetres = 12, shoreRunMetres = 70,
                    basinIrregularity = 0, ruggednessMetres = 0, bevelMetres = 0, corridorMetres = 0 };
                carve.ringPoints.AddRange(ring); carve.PrepareLakeFormation((LakeFormation)family, 0); carve.RecomputeBounds();
                var signature = new float[25]; bool finite = true, cut = false, sedimentChanged = false;
                for (int z = 0; z < 5; z++) for (int x = 0; x < 5; x++)
                {
                    float wx = x * 30 - 60, wz = z * 20 - 40;
                    float bed = carve.HeightAt(wx, wz, 105);
                    signature[z * 5 + x] = bed;
                    finite &= !float.IsNaN(bed) && !float.IsInfinity(bed) && bed >= 88 && bed <= 100;
                    cut |= bed < 99;
                    sedimentChanged |= Mathf.Abs(prepared.Fraction(0.6f, 0, wx, wz) - settled.Fraction(0.6f, 0, wx, wz)) > 1e-4f;
                }
                bool distinct = true;
                foreach (var previous in signatures)
                {
                    float difference = 0; for (int i = 0; i < signature.Length; i++) difference += Mathf.Abs(previous[i] - signature[i]);
                    distinct &= difference > 0.01f;
                }
                signatures.Add(signature);
                Check(finite && cut && distinct && sedimentChanged && prepared.Fraction(0, 0, 120, 0) == 0,
                    "UC-T08-15 " + (LakeFormation)family + " carves a distinct bounded bed under the accepted shore and responds to sedimentation.");
                CheckClose(carve.HeightAt(200, 200, 105), 105,
                    "UC-T08-15 " + (LakeFormation)family + " leaves ground outside the accepted ring and bank unchanged.");
                carve.Clear();
                CheckClose(carve.HeightAt(0, 0, 105), 105, "UC-T08-15 retired " + (LakeFormation)family + " retains no excavation.");
            }
            var retained = LakeFormationMath.Prepare(LakeFormation.Karst, 0.4f, ring);
            float before = retained.Fraction(0.7f, 0, 15, 20); ring.Clear();
            Check(before == retained.Fraction(0.7f, 0, 15, 20),
                "UC-T08-15 worker-safe prepared axes retain no mutable source outline.");
            ring.AddRange(new[] { Vector3.zero, Vector3.right * 10, Vector3.forward * 10 });
            bool Reject(Action run) { try { run(); return false; } catch (ArgumentException) { return true; } }
            Check(Reject(() => LakeFormationMath.Prepare((LakeFormation)12, 0, ring)) &&
                Reject(() => LakeFormationMath.Prepare(LakeFormation.Tectonic, float.NaN, ring)) &&
                Reject(() => LakeFormationMath.Select(1, Vector3.zero, 2, 0, 1, false)),
                "UC-T08-15 unsupported formation, invalid sedimentation/ring and zero-area selection fail explicitly.");
        }

        private static void TestLakeFormationSelection()
        {
            var selected = new System.Collections.Generic.HashSet<LakeFormation>();
            bool deterministic = true, inland = true;
            var state = UnityEngine.Random.state;
            float expected = UnityEngine.Random.value; UnityEngine.Random.state = state;
            for (int seed = 0; seed < 4096; seed++)
            {
                Vector3 centre = new Vector3(312, 104, -850);
                var family = LakeFormationMath.Select(seed, centre, 4, 5000, 1.2f, true, 3, 0.1f);
                selected.Add(family);
                deterministic &= family == LakeFormationMath.Select(seed, centre, 4, 5000, 1.2f, true, 3, 0.1f);
                inland &= LakeFormationMath.Select(seed, centre, 4, 5000, 1.2f, false) != LakeFormation.CoastalBarrier;
            }
            float actual = UnityEngine.Random.value; UnityEngine.Random.state = state;
            Check(selected.Count == 11 && deterministic && inland && expected == actual,
                "UC-T08-15 all eleven seeded families are reachable; selection is repeatable, excludes unproven coasts and preserves global random state.");
        }

        private static void TestLakeFormationSamplingCost()
        {
            var ring = new[] { new Vector3(-100, 0, -100), new Vector3(100, 0, -100),
                new Vector3(100, 0, 100), new Vector3(-100, 0, 100) };
            var prepared = LakeFormationMath.Prepare(LakeFormation.Tectonic, 0, ring);
            const int count = 65536;
            double tableSum = 0, analyticSum = 0; float worstError = 0;
            // UC-T08-15: compare the same tectonic spatial relief; timing a complete basin against Pow alone is unequal work.
            float Analytic(float t, float worldZ)
            {
                float v = worldZ / 100;
                return Mathf.Clamp01(Mathf.Pow(t * t * (3 - 2 * t), 1.65f) * (1 - 0.25f * Mathf.Clamp01(v * v)));
            }
            prepared.Fraction(0.5f, 0, 0, 0); // Warm static tables/JIT before reporting sampling cost.
            Analytic(0.5f, 0);
            var timer = new System.Diagnostics.Stopwatch();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            timer.Start();
            for (int i = 0; i < count; i++) tableSum += prepared.Fraction((i % 1024) / 1023f, 0, 0, i % 201 - 100);
            timer.Stop(); long tableTicks = timer.ElapsedTicks;
            long sampledAllocation = GC.GetAllocatedBytesForCurrentThread() - allocated;
            timer.Restart();
            for (int i = 0; i < count; i++)
            {
                float t = (i % 1024) / 1023f;
                float exact = Analytic(t, i % 201 - 100);
                analyticSum += exact;
            }
            timer.Stop(); long analyticTicks = timer.ElapsedTicks;
            for (int i = 0; i < 1024; i++)
            {
                float t = i / 1023f;
                worstError = Mathf.Max(worstError, Mathf.Abs(prepared.Fraction(t, 0, 0, i % 201 - 100) - Analytic(t, i % 201 - 100)));
            }
            Check(worstError < 0.0001f && Math.Abs(tableSum - analyticSum) / count < 0.0001,
                "UC-T08-15 cached full-detail formation stays within 0.01 percent of the analytic depth range.");
            // Report rather than assert Mono's allocation counter or noisy timings; device performance needs matched rendered workloads.
            Debug.Log("UC-T08-15 matched formation samples=" + count + " tableTicks=" + tableTicks + " analyticTicks=" + analyticTicks +
                " worstDepthFractionError=" + worstError + " measuredSamplingBytes=" + sampledAllocation +
                "; batch timing is not device performance.");
        }
    }
}
