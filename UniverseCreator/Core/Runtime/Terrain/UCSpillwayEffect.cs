// -----------------------------------------------------------------------------
//  Universe Creator - Path Tool Integration (Module 6 seam)
//  File:    UCSpillwayEffect.cs
//  Purpose: The U-2 half of a dam request - the spillway flow illusion on the
//           water surface. Where U-1 (DamReservoirReconciler) raises the reservoir
//           and drops the tailwater, U-2 makes the drop READ as a working spillway:
//           whitewater foam where the jet lands and airborne spray above it, driven
//           entirely by the governance U-1 already set on the body.
//
//  WHAT DRIVES IT, AND WHY IT INVENTS NO NEW DATA
//  ----------------------------------------------
//  The section register asks for "flow map, foam mask, plunge spray, driven by the
//  request's outlet position and level difference." Every one of those numbers is
//  already on the governed body after U-1: the outlet is the dam line midpoint
//  (DamBarrierCentre), downstream is the negation of DamUpstreamNormal, and the level
//  difference is the reservoir surface (SurfaceWorldY) minus the tailwater
//  (GovernedTailwaterLevelMetres). So U-2 reads U-1's governance and never the inbox
//  a second time, and the request contract stays exactly the six members U-5 fixed.
//
//    * flow map  - the tailwater is a river, and a river already writes its own
//                  downstream flow into the surface (see UC_WaterPasses.hlsl's vertex
//                  colour). U-2 does not fight it; it biases its foam plume downstream
//                  so the churn reads as directional, and leaves the river's flow map
//                  the river's to own. No river-mesh surgery, no second flow field.
//    * foam mask - the plunge patch is emitted into the EXISTING ripple simulation
//                  (UCWaterRippleSim), whose foam pass is literally "the white patch
//                  under a waterfall". It is a global overlay the water shader already
//                  samples, so the foam lands on whatever surface sits at the toe -
//                  the tailwater river, the reservoir's own downstream edge, either -
//                  with no per-body wiring.
//    * spray     - a particle system built here in code (no prefab, no asset), placed
//                  at the toe and aimed up-and-downstream.
//
//  THE PHYSICS IS REAL, NOT A GUESS
//  --------------------------------
//  The jet leaves the outlet under head H at Torricelli's velocity v = sqrt(2 g H).
//  That single number sets everything: how white the water goes (aeration rises with
//  v), how far the foam carries before it dissipates (the plume length is v times a
//  short travel time), how fast the spray is thrown and thus how high it reaches
//  (v^2/2g returns the head, so a fraction of v gives a believable fraction of H),
//  and how much spray a wide, energetic spillway makes versus a narrow, gentle one
//  (rate scales with crest width times v). A dam with no drop (H<=0) makes nothing,
//  exactly as a real weir under no head is silent.
//
//  IT IS TRANSIENT, LIKE THE GOVERNANCE
//  ------------------------------------
//  The reconciler attaches this the moment a body becomes a reservoir and removes it
//  the moment the dam is withdrawn - the same reversible sync U-1 runs. The component
//  and its spray object are HideAndDontSave: never serialised, re-attached from the
//  re-derived governance after a domain reload, gone without trace when the dam is.
//
//  DETERMINISTIC PLACEMENT, NATURAL BREAK-UP
//  -----------------------------------------
//  Where the foam splats land in the plunge footprint is chosen by a low-discrepancy
//  (R2) walk, not Random - the world is built from rule, not chance, and the coverage
//  is even and reproducible. The spray's sub-droplet jitter is left to the particle
//  system, because turbulent atomisation genuinely is stochastic; the ENVELOPE that
//  jitter fills (rate, speed, lifetime, size) is set from the physics above.
//
//  NO DOTS, NO BURST, NO NATIVEARRAY. Plain managed maths and one particle system.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary> The spillway flow illusion for one dam-governed reservoir. Attached and removed by
    /// <see cref="DamReservoirReconciler"/> as governance begins and ends; it reads the body's dam governance every
    /// frame and drives the shared <see cref="UCWaterRippleSim"/> foam plus a code-built spray particle system. Not
    /// serialised and not meant to be added by hand - dropped on anything but a dam-governed body it removes itself.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class UCSpillwayEffect : MonoBehaviour
    {
        private const float Gravity = 9.81f;

        // Plastic-number (R2) low-discrepancy constants. A deterministic, even 2D
        // scatter with no RNG - see the header note on placement.
        private const float R2A1 = 0.7548776662466927f;
        private const float R2A2 = 0.5698402909980532f;

        [System.NonSerialized] private UCWaterBody body;

        private GameObject sprayObject;
        private ParticleSystem spray;
        private Material sprayMaterial;
        private Texture2D sprayTexture;

        // Cached governance, so the particle system is only reconfigured when the dam
        // actually moved, not every frame. The per-frame cost is then just the foam.
        private float cachedHead = -1f;
        private float cachedWidth = -1f;
        private Vector3 cachedPlunge = new Vector3(float.NaN, 0f, 0f);

        private int scatterIndex;

        // ---------------------------------------------------------------------
        //  Attach / detach - the reconciler's handles
        // ---------------------------------------------------------------------

        /// <summary> Attach the effect to a governed body's object, or return the one already there, and bind it. Cheap
        /// and idempotent: the reconciler may call it every settle a body stays a reservoir. The heavy spray build is
        /// lazy, on the first active frame, so this never costs a particle system inside the settle loop. </summary>
        public static UCSpillwayEffect Ensure(UCWaterBody target)
        {
            if (target == null) { return null; }

            UCSpillwayEffect fx = target.GetComponent<UCSpillwayEffect>();
            if (fx == null) { fx = target.gameObject.AddComponent<UCSpillwayEffect>(); }
            fx.hideFlags = HideFlags.HideAndDontSave;
            fx.body = target;
            return fx;
        }

        /// <summary> Remove the effect from a body that is no longer a reservoir, tearing down its spray. Safe when there
        /// is none - a released body the reconciler never governed costs one null check here. </summary>
        public static void Remove(UCWaterBody target)
        {
            if (target == null) { return; }
            UCSpillwayEffect fx = target.GetComponent<UCSpillwayEffect>();
            if (fx != null) { fx.TearDownAndDestroy(); }
        }

        // ---------------------------------------------------------------------
        //  Drive
        // ---------------------------------------------------------------------

        private void LateUpdate()
        {
            // Auto-bind so a hand-added component on a real water body still works, and
            // self-remove the moment it finds itself on anything that is not a governed
            // reservoir - the belt to the reconciler's braces.
            if (body == null) { body = GetComponent<UCWaterBody>(); }
            if (body == null || !body.IsDamGoverned) { TearDownAndDestroy(); return; }

            float surfaceY = body.SurfaceWorldY();
            float tailwaterY = body.GovernedTailwaterLevelMetres;
            float head = surfaceY - tailwaterY;

            // No drop, no spillway. A reservoir sitting at or below its tailwater has no
            // head to spend, and a silent weir is the honest result - not a puff of foam
            // over still water.
            if (head <= 0.01f) { if (spray != null) { spray.Stop(); } return; }

            Vector2 centre = body.DamBarrierCentre;
            Vector2 upstream = body.DamUpstreamNormal;
            if (upstream.sqrMagnitude < 1e-6f) { return; }
            upstream = upstream.normalized;

            Vector2 downstream = -upstream;
            Vector2 axis = new Vector2(-upstream.y, upstream.x);           // along the crest line
            float width = Mathf.Max(1f, body.GovernedCrestLengthMetres);

            // Torricelli: the speed the jet leaves the outlet under this head.
            float v = Mathf.Sqrt(2f * Gravity * head);

            // How white the water goes. Aeration climbs with the jet speed and is full
            // by roughly an 11 m/s jet (a ~6 m drop), the point a real overfall is solid
            // white rather than green-and-broken.
            float aeration = Mathf.Clamp01(v / 11f);

            // The aerated plume carries downstream for about two-thirds of a second of
            // travel before it dissipates, clamped to a sane span so a colossal head does
            // not paint foam to the horizon.
            float plume = Mathf.Clamp(v * 0.65f, 2f, 60f);

            // The plunge footprint centres half a plume downstream of the dam line, on
            // the tailwater surface.
            Vector2 plungeXZ = centre + downstream * (plume * 0.5f);
            Vector3 plunge = new Vector3(plungeXZ.x, tailwaterY, plungeXZ.y);

            EmitFoam(plunge, downstream, axis, width, plume, v, aeration);
            DriveSpray(plunge, downstream, width, plume, v, aeration, head);
        }

        // ---------------------------------------------------------------------
        //  Foam mask - into the shared ripple simulation
        // ---------------------------------------------------------------------

        private void EmitFoam(Vector3 plunge, Vector2 downstream, Vector2 axis,
                              float width, float plume, float v, float aeration)
        {
            UCWaterRippleSim sim = UCWaterRippleSim.Active;
            if (sim == null) { return; }

            // Nothing to inject into a sim that is not running (edit mode without preview
            // turned on), and nothing worth injecting when the toe is outside the sim's
            // world window - the sim would drop it anyway, off-screen.
            if (!Application.isPlaying && !sim.simulateInEditor) { return; }
            if (!sim.Covers(plunge)) { return; }

            Vector2 toe = new Vector2(plunge.x, plunge.z);

            // A handful of splats a frame, more for a wide and energetic spillway, capped
            // so a giant dam cannot flood the impulse queue.
            int splats = Mathf.Clamp(Mathf.RoundToInt(width * 0.12f * aeration), 1, 12);
            float radius = Mathf.Clamp(0.6f + v * 0.06f, 0.6f, 3.5f);

            for (int i = 0; i < splats; i++)
            {
                scatterIndex++;

                // R2 low-discrepancy pair in [0,1). Even, deterministic, no RNG.
                float s = Frac(0.5f + R2A1 * scatterIndex);
                float t = Frac(0.5f + R2A2 * scatterIndex);

                // Densest at the toe (t*t biases toward 0) and spread across the crest
                // width, so the impact line reads where the jet actually strikes.
                float along = plume * (t * t) - plume * 0.5f;
                float across = (s - 0.5f) * width;

                Vector2 p = toe + downstream * along + axis * across;

                WaterImpulse imp = new WaterImpulse
                {
                    worldXZ = p,
                    radius = radius,
                    strength = -0.12f * aeration,   // a churny dip; the crown rebounds on its own
                    ringness = 0.30f,               // broad turbulence, not a crisp drop-ring
                    sharpness = 1.1f,
                    foam = Mathf.Clamp01(0.55f * aeration)
                };
                sim.Emit(imp);
            }
        }

        // ---------------------------------------------------------------------
        //  Plunge spray - a code-built particle system
        // ---------------------------------------------------------------------

        private void DriveSpray(Vector3 plunge, Vector2 downstream, float width,
                               float plume, float v, float aeration, float head)
        {
            EnsureSpray();
            if (spray == null) { return; }

            // Aim up, leaning downstream with the jet's horizontal momentum. Cone shapes
            // emit along local +Z, so point the object's forward up the jet axis.
            Vector3 down3 = new Vector3(downstream.x, 0f, downstream.y);
            Vector3 jetAxis = (Vector3.up * 2f + down3).normalized;

            // Only rebuild the modules when the dam actually moved. Between moves the
            // particle system runs itself and this method is a few comparisons.
            bool moved = !Mathf.Approximately(head, cachedHead)
                      || !Mathf.Approximately(width, cachedWidth)
                      || (plunge - cachedPlunge).sqrMagnitude > 0.04f;

            sprayObject.transform.SetPositionAndRotation(plunge, Quaternion.LookRotation(jetAxis, Vector3.up));

            if (moved)
            {
                cachedHead = head;
                cachedWidth = width;
                cachedPlunge = plunge;

                ParticleSystem.MainModule main = spray.main;
                // A fraction of the jet speed. v^2/2g is the head, so 0.3..0.55 v reaches
                // roughly a tenth to a third of the head - a believable spray column.
                main.startSpeed = new ParticleSystem.MinMaxCurve(v * 0.30f, v * 0.55f);
                main.startLifetime = Mathf.Clamp(v / Gravity, 0.5f, 3f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.4f, 1.4f);
                main.gravityModifier = 1f;                       // real gravity brings it back down
                main.startColor = Color.white;
                main.maxParticles = 2000;

                ParticleSystem.EmissionModule emission = spray.emission;
                emission.rateOverTime = Mathf.Clamp(width * v * 0.40f, 6f, 350f);

                ParticleSystem.ShapeModule shape = spray.shape;
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = 38f;
                shape.radius = Mathf.Max(0.5f, width * 0.35f);
            }

            // Emission tracks aeration continuously (a dam whose reservoir is filling makes
            // more spray as the head climbs), so nudge the rate every frame it is cheap to.
            if (aeration <= 0.001f) { if (spray.isPlaying) { spray.Stop(); } return; }
            if (!spray.isPlaying) { spray.Play(); }
        }

        private void EnsureSpray()
        {
            if (spray != null) { return; }

            sprayObject = new GameObject("UC Spillway Spray")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            sprayObject.transform.SetParent(transform, false);

            spray = sprayObject.AddComponent<ParticleSystem>();
            spray.Stop();

            ParticleSystem.MainModule main = spray.main;
            main.playOnAwake = false;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;   // spray hangs in the air, not on the object
            main.scalingMode = ParticleSystemScalingMode.Local;           // true metres, even under a scaled water body

            ParticleSystem.ColorOverLifetimeModule col = spray.colorOverLifetime;
            col.enabled = true;
            Gradient g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(new Color(0.9f, 0.95f, 1f), 1f) },
                new[]
                {
                    new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.85f, 0.15f),
                    new GradientAlphaKey(0.55f, 0.55f), new GradientAlphaKey(0f, 1f)
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

            // Sprites/Default is alpha-blended, honours the particle system's per-particle
            // colour, and is present in every pipeline - the safe choice for a droplet
            // billboard without shipping a shader.
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

            // A soft radial dot, built in code so no image asset has to ship. A hard
            // square reads as a decal; the squared falloff makes it a droplet.
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
            // The spray object is our child and dies with us, but the material and texture
            // are ours to release - they are HideAndDontSave and nothing else holds them.
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
