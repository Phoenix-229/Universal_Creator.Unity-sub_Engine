// -----------------------------------------------------------------------------
//  Universe Creator - Natural waterfalls (J.2)
//  File:    UCWaterfallEffect.cs
//  Purpose: The natural sibling of UCSpillwayEffect. Where the spillway effect is
//           the seam of a built dam, this is what forms on its own where a river
//           runs down a steep hillside: foam where the water plunges and spray
//           thrown off the fall, driven ENTIRELY by the elevation the course
//           already solved. No marker node, no authored flag - a river that
//           crosses a cliff grows a fall there and one that stays on gentle ground
//           grows none, exactly as the land decides in the real world.
//
//  WHERE THE NUMBERS COME FROM, AND WHY IT INVENTS NONE
//  ----------------------------------------------------
//  UCWaterBody.SolveCourse already walks its solved SURFACE and records every reach
//  that plunges steeper than a waterfall grade as a WaterfallReach (crest, foot,
//  drop, run, width). This component reads that list and nothing else. The drop is
//  a real head, so the same physics the spillway uses applies unchanged:
//
//    * the jet reaches the foot at Torricelli's speed v = sqrt(2 g drop);
//    * aeration (how white it goes) climbs with v and is full by an ~11 m/s fall;
//    * foam is emitted into the shared UCWaterRippleSim - the same global overlay
//      the water shader already samples, so it lands on the river surface at the
//      foot with no per-body wiring and no mesh surgery;
//    * spray is a code-built particle system aimed up and downstream, its rate and
//      speed set from v and the fall's width.
//
//  IT IS TRANSIENT AND SELF-DRIVEN, LIKE THE SPILLWAY
//  --------------------------------------------------
//  Attached by UCWaterBody.RebuildSurface when a river has cascades and removed
//  when it does not. HideAndDontSave: never serialised, re-attached from the
//  re-solved course after a domain reload, gone without trace when the falls are.
//  A river with several falls drives foam at every one but a single spray column at
//  the most energetic - bounded cost, and the lesser falls still read from foam.
//
//  NO DOTS, NO BURST, NO NATIVEARRAY. Plain managed maths and one particle system.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary> The natural-waterfall illusion for one river course. Attached and removed by
    /// <see cref="UCWaterBody"/> as its solved course gains or loses steep reaches; it reads the body's
    /// <see cref="UCWaterBody.Cascades"/> every frame and drives the shared <see cref="UCWaterRippleSim"/> foam plus a
    /// code-built spray particle system. Not serialised and not meant to be added by hand - dropped on a body with no
    /// cascades it removes itself. </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class UCWaterfallEffect : MonoBehaviour
    {
        private const float Gravity = 9.81f;

        // Plastic-number (R2) low-discrepancy constants - a deterministic, even 2D
        // scatter with no RNG, matching the spillway's placement.
        private const float R2A1 = 0.7548776662466927f;
        private const float R2A2 = 0.5698402909980532f;

        [System.NonSerialized] private UCWaterBody body;

        private GameObject sprayObject;
        private ParticleSystem spray;
        private Material sprayMaterial;
        private Texture2D sprayTexture;

        // Cached driver of the strongest fall, so the particle system is only
        // reconfigured when that fall actually changes, not every frame.
        private float cachedDrop = -1f;
        private float cachedWidth = -1f;
        private Vector3 cachedFoot = new Vector3(float.NaN, 0f, 0f);

        private int scatterIndex;

        // ---------------------------------------------------------------------
        //  Attach / detach - UCWaterBody's handles
        // ---------------------------------------------------------------------

        /// <summary> Attach the effect to a river body, or return the one already there, and bind it. Cheap and
        /// idempotent: the rebuild may call it every time a river keeps its falls. The spray build is lazy, on the first
        /// active frame, so this never costs a particle system inside a rebuild. </summary>
        public static UCWaterfallEffect Ensure(UCWaterBody target)
        {
            if (target == null) { return null; }

            UCWaterfallEffect fx = target.GetComponent<UCWaterfallEffect>();
            if (fx == null) { fx = target.gameObject.AddComponent<UCWaterfallEffect>(); }
            fx.hideFlags = HideFlags.HideAndDontSave;
            fx.body = target;
            return fx;
        }

        /// <summary> Remove the effect from a river that no longer has falls, tearing down its spray. Safe when there is
        /// none - a body that never had one costs one null check here. </summary>
        public static void Remove(UCWaterBody target)
        {
            if (target == null) { return; }
            UCWaterfallEffect fx = target.GetComponent<UCWaterfallEffect>();
            if (fx != null) { fx.TearDownAndDestroy(); }
        }

        // ---------------------------------------------------------------------
        //  Drive
        // ---------------------------------------------------------------------

        private void LateUpdate()
        {
            // Auto-bind so a hand-added component on a real river still works, and
            // self-remove the moment it finds itself on a body with no falls.
            if (body == null) { body = GetComponent<UCWaterBody>(); }
            if (body == null) { TearDownAndDestroy(); return; }

            IReadOnlyList<UCWaterBody.WaterfallReach> falls = body.Cascades;
            if (falls == null || falls.Count == 0) { TearDownAndDestroy(); return; }

            // Foam at every fall's foot - cheap, and the lesser falls read entirely
            // from it. Track the most energetic (largest drop) for the spray column.
            int strongest = -1;
            float bestDrop = 0f;
            for (int i = 0; i < falls.Count; i++)
            {
                UCWaterBody.WaterfallReach f = falls[i];
                float v = Mathf.Sqrt(2f * Gravity * Mathf.Max(0f, f.dropMetres));
                float aeration = Mathf.Clamp01(v / 11f);
                EmitFoam(f, v, aeration);

                if (f.dropMetres > bestDrop) { bestDrop = f.dropMetres; strongest = i; }
            }

            if (strongest >= 0) { DriveSpray(falls[strongest]); }
        }

        // ---------------------------------------------------------------------
        //  Foam mask - into the shared ripple simulation
        // ---------------------------------------------------------------------

        private void EmitFoam(UCWaterBody.WaterfallReach f, float v, float aeration)
        {
            UCWaterRippleSim sim = UCWaterRippleSim.Active;
            if (sim == null) { return; }

            // Nothing to inject into a sim that is not running, and nothing worth
            // injecting when the foot is outside the sim's world window.
            if (!Application.isPlaying && !sim.simulateInEditor) { return; }
            if (!sim.Covers(f.foot)) { return; }

            // Downstream and across the fall, in the horizontal plane. A fall with no
            // horizontal run (a sheer drop) still gets a foam patch, just not smeared.
            Vector2 crestXZ = new Vector2(f.crest.x, f.crest.z);
            Vector2 footXZ = new Vector2(f.foot.x, f.foot.z);
            Vector2 flow = footXZ - crestXZ;
            Vector2 downstream = flow.sqrMagnitude > 1e-6f ? flow.normalized : Vector2.right;
            Vector2 axis = new Vector2(-downstream.y, downstream.x);   // across the channel

            float width = Mathf.Max(1f, f.widthMetres);
            float pool = Mathf.Clamp(v * 0.4f, 1.5f, 24f);            // plunge-pool length

            int splats = Mathf.Clamp(Mathf.RoundToInt(width * 0.14f * aeration), 1, 12);
            float radius = Mathf.Clamp(0.6f + v * 0.06f, 0.6f, 3.5f);

            for (int i = 0; i < splats; i++)
            {
                scatterIndex++;

                float s = Frac(0.5f + R2A1 * scatterIndex);
                float t = Frac(0.5f + R2A2 * scatterIndex);

                // Densest at the foot (t*t biases toward 0), spread across the channel
                // and a little downstream where the churn carries.
                float along = pool * (t * t);
                float across = (s - 0.5f) * width;

                Vector2 p = footXZ + downstream * along + axis * across;

                WaterImpulse imp = new WaterImpulse
                {
                    worldXZ = p,
                    radius = radius,
                    strength = -0.12f * aeration,   // a churny dip; the crown rebounds on its own
                    ringness = 0.30f,               // broad turbulence, not a crisp drop-ring
                    sharpness = 1.1f,
                    foam = Mathf.Clamp01(0.6f * aeration)
                };
                sim.Emit(imp);
            }
        }

        // ---------------------------------------------------------------------
        //  Plunge spray - a code-built particle system at the strongest fall
        // ---------------------------------------------------------------------

        private void DriveSpray(UCWaterBody.WaterfallReach f)
        {
            float drop = Mathf.Max(0f, f.dropMetres);
            float v = Mathf.Sqrt(2f * Gravity * drop);
            float aeration = Mathf.Clamp01(v / 11f);

            if (aeration <= 0.001f) { if (spray != null && spray.isPlaying) { spray.Stop(); } return; }

            EnsureSpray();
            if (spray == null) { return; }

            Vector2 flow2 = new Vector2(f.foot.x - f.crest.x, f.foot.z - f.crest.z);
            Vector3 down3 = flow2.sqrMagnitude > 1e-6f
                ? new Vector3(flow2.normalized.x, 0f, flow2.normalized.y)
                : Vector3.forward;

            // Spray leaps back UP-and-back off the plunge, against the flow, as real
            // falls throw mist upstream of the foot.
            Vector3 jetAxis = (Vector3.up * 2f - down3).normalized;
            float width = Mathf.Max(1f, f.widthMetres);

            sprayObject.transform.SetPositionAndRotation(f.foot, Quaternion.LookRotation(jetAxis, Vector3.up));

            bool moved = !Mathf.Approximately(drop, cachedDrop)
                      || !Mathf.Approximately(width, cachedWidth)
                      || (f.foot - cachedFoot).sqrMagnitude > 0.04f;

            if (moved)
            {
                cachedDrop = drop;
                cachedWidth = width;
                cachedFoot = f.foot;

                ParticleSystem.MainModule main = spray.main;
                main.startSpeed = new ParticleSystem.MinMaxCurve(v * 0.25f, v * 0.5f);
                main.startLifetime = Mathf.Clamp(v / Gravity, 0.5f, 3f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.35f, 1.3f);
                main.gravityModifier = 1f;
                main.startColor = Color.white;
                main.maxParticles = 2000;

                ParticleSystem.EmissionModule emission = spray.emission;
                emission.rateOverTime = Mathf.Clamp(width * v * 0.35f, 6f, 350f);

                ParticleSystem.ShapeModule shape = spray.shape;
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = 34f;
                shape.radius = Mathf.Max(0.5f, width * 0.3f);
            }

            if (!spray.isPlaying) { spray.Play(); }
        }

        private void EnsureSpray()
        {
            if (spray != null) { return; }

            sprayObject = new GameObject("UC Waterfall Spray")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            sprayObject.transform.SetParent(transform, false);

            spray = sprayObject.AddComponent<ParticleSystem>();
            spray.Stop();

            ParticleSystem.MainModule main = spray.main;
            main.playOnAwake = false;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;   // mist hangs in the air, not on the object
            main.scalingMode = ParticleSystemScalingMode.Local;           // true metres, even under a scaled water body

            ParticleSystem.ColorOverLifetimeModule col = spray.colorOverLifetime;
            col.enabled = true;
            Gradient g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(new Color(0.9f, 0.95f, 1f), 1f) },
                new[]
                {
                    new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.8f, 0.15f),
                    new GradientAlphaKey(0.5f, 0.55f), new GradientAlphaKey(0f, 1f)
                });
            col.color = new ParticleSystem.MinMaxGradient(g);

            ParticleSystemRenderer renderer = sprayObject.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.material = SprayMaterial();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        private Material SprayMaterial()
        {
            if (sprayMaterial != null) { return sprayMaterial; }

            // Sprites/Default is alpha-blended, honours the per-particle colour, and is
            // present in every pipeline - the safe choice without shipping a shader.
            Shader s = Shader.Find("Sprites/Default");
            if (s == null) { s = Shader.Find("Universal Render Pipeline/Particles/Unlit"); }
            if (s == null) { s = Shader.Find("Unlit/Transparent"); }

            sprayMaterial = new Material(s) { hideFlags = HideFlags.HideAndDontSave };
            sprayMaterial.mainTexture = SprayTexture();
            return sprayMaterial;
        }

        private Texture2D SprayTexture()
        {
            if (sprayTexture != null) { return sprayTexture; }

            // A soft radial dot, built in code so no image asset has to ship.
            const int n = 32;
            sprayTexture = new Texture2D(n, n, TextureFormat.RGBA32, false, true)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            Color32[] px = new Color32[n * n];
            float c = (n - 1) * 0.5f;
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float dx = (x - c) / c;
                    float dy = (y - c) / c;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(1f - d);
                    a *= a;
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            sprayTexture.SetPixels32(px);
            sprayTexture.Apply(false, false);
            return sprayTexture;
        }

        // ---------------------------------------------------------------------
        //  Teardown
        // ---------------------------------------------------------------------

        private void OnDestroy()
        {
            if (sprayMaterial != null) { SafeDestroy(sprayMaterial); sprayMaterial = null; }
            if (sprayTexture != null) { SafeDestroy(sprayTexture); sprayTexture = null; }
        }

        private void TearDownAndDestroy()
        {
            if (sprayObject != null) { SafeDestroy(sprayObject); sprayObject = null; spray = null; }
            SafeDestroy(this);
        }

        private static void SafeDestroy(Object o)
        {
            if (o == null) { return; }
            if (Application.isPlaying) { Destroy(o); }
            else { DestroyImmediate(o); }
        }

        private static float Frac(float x) { return x - Mathf.Floor(x); }
    }
}
