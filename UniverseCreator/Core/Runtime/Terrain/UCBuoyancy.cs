// -----------------------------------------------------------------------------
//  Universe Creator - Module 2: PCG Extended Capability Port
//
//  File:    UCBuoyancy.cs
//  Purpose: Everything a rigid body does in water. Floating, sinking, rolling with
//           the swell, being pushed by a current, slowing down correctly at both
//           ends of the speed range, the splash going in, the wake while moving,
//           and the drip coming out.
//
//  THE THING THAT MAKES FLOATING LOOK RIGHT
//  ----------------------------------------
//  One buoyant force at the centre of mass makes an object bob and never tilt. It
//  is a cork that cannot capsize, and every player reads it as wrong without being
//  able to say why. Buoyancy has to be sampled at several points spread across the
//  hull and applied at each of them, because the torque comes from the DIFFERENCE
//  between how deep the bow is and how deep the stern is. That difference is the
//  whole effect. Four points is the minimum that produces roll and pitch; eight is
//  where it stops improving for anything smaller than a ship.
//
//  MOBILE BUDGET - WHAT THIS DELIBERATELY DOES NOT DO
//  --------------------------------------------------
//  No mesh voxelisation, no per-triangle submerged-volume solve, no GPU readback,
//  no raycasts. A sample is a bounds test and about twenty flops, and the default
//  of four samples on a fixed 50Hz step costs a few microseconds for a whole scene
//  of floating objects. The budget line is drawn at: nothing that scales with mesh
//  complexity, and nothing that stalls the pipeline.
//
//  The two knobs that matter for a phone are sampleCount and whether ripples are
//  emitted. Both are on the component, and the defaults are the mid-range phone
//  setting rather than the desktop one.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    [RequireComponent(typeof(Rigidbody))]
    [AddComponentMenu("Universe Creator/Buoyancy")]
    public class UCBuoyancy : MonoBehaviour
    {
        [Header("Body")]
        [Tooltip("Displaced volume in cubic metres. This and mass decide everything: " +
                 "an object floats when mass/volume is below the water's density and " +
                 "sinks when it is above. Left at 0, it is estimated from the " +
                 "collider bounds on Awake.")]
        public float volume = 0f;

        [Tooltip("Points across the body where buoyancy is sampled. Four gives roll " +
                 "and pitch; eight is smoother on a long hull. Above that nothing " +
                 "visibly improves and the cost is linear.")]
        [Range(1, 12)] public int sampleCount = 4;

        [Tooltip("How far the sample points spread from the centre, as a fraction of " +
                 "the collider bounds. Below 1 the body feels stiffer in roll.")]
        [Range(0.2f, 1f)] public float sampleSpread = 0.8f;

        [Header("Drag")]
        [Tooltip("Form drag coefficient. 0.04 is a streamlined hull, 0.47 a sphere, " +
                 "1.05 a cube presented flat.")]
        public float dragCoefficient = 0.45f;

        [Tooltip("Angular damping while submerged. Water resists rotation far more " +
                 "than air does, and without this a body spins in water the way it " +
                 "would in a vacuum.")]
        [Range(0f, 20f)] public float angularDrag = 3f;

        [Tooltip("Scales the whole drag result. Physical is 1; lower it for arcade " +
                 "handling without breaking the laminar/turbulent shape of the curve.")]
        [Range(0f, 4f)] public float dragScale = 1f;

        [Header("Stability")]
        [Tooltip("Metres the centre of buoyancy sits above the centre of mass. This " +
                 "is metacentric height, and it is the only thing that decides " +
                 "whether a hull rights itself or capsizes. Zero is neutral and " +
                 "unstable; a boat wants a positive value.")]
        public float metacentricHeight = 0.35f;

        [Header("Interaction")]
        [Tooltip("Push ripples into the water simulation. The main cost switch on " +
                 "mobile after sampleCount.")]
        public bool emitRipples = true;

        [Tooltip("Scales every ripple this body makes.")]
        [Range(0f, 4f)] public float rippleScale = 1f;

        [Tooltip("Speed at which entering the water counts as a splash rather than " +
                 "as settling in, metres per second.")]
        public float splashSpeed = 1.5f;

        // ---------------------------------------------------------------------
        //  State
        // ---------------------------------------------------------------------

        private Rigidbody body;
        private Vector3[] samplePoints;
        private float sampleVolume;
        private float characteristicLength;
        private float frontalArea;
        private float wettedArea;

        private bool wasSubmerged;
        private float rippleTimer;
        private float lastVerticalSpeed;
        private readonly WaterContactTracker waterContact = new WaterContactTracker(); // UC-T04-09A: sampled once by the existing physics owner.
        /// <summary>UC-T04-09: optional water-world binding; a null field infers the nearest parent once in Awake, or uses standalone scene water.</summary>
        [Tooltip("Optional UC world for water queries. Empty infers the nearest parent world; bind external vehicles explicitly when worlds overlap.")]
        public UniverseCreatorObject waterWorld;

        /// <summary>UC-T04-09A: latest local water/contact state; dry after disable or leaving the accepted footprint.</summary>
        public WaterContactState Contact => waterContact.Current;

        /// <summary>UC-T04-09A: bounded main-thread enter/exit/splash/wake notifications for optional audio/particle consumers.</summary>
        public event System.Action<WaterContactEvent> ContactChanged;

        private void OnDisable()
        {
            var change = waterContact.Reset();
            SubmergedFraction = 0; Turbulence = 0; wasSubmerged = false; rippleTimer = 0;
            if (change.Kind != WaterContactEventKind.None) ContactChanged?.Invoke(change);
        }

        /// <summary>Fraction of the body under the surface last step, 0 to 1.</summary>
        public float SubmergedFraction { get; private set; }

        /// <summary>True while any part of the body is in water.</summary>
        public bool InWater { get { return SubmergedFraction > 0f; } }

        /// <summary>
        /// Flow state around the body, 0 laminar to 1 fully turbulent. Exposed so
        /// game code can drive audio and particles from the same number the physics
        /// uses, rather than from a second speed threshold that drifts out of sync
        /// with it.
        /// </summary>
        public float Turbulence { get; private set; }

        // ---------------------------------------------------------------------

        private void Awake()
        {
            body = GetComponent<Rigidbody>();
            if (waterWorld == null) waterWorld = GetComponentInParent<UniverseCreatorObject>(true); // UC-T04-09: cache scope outside the physics sample loop.
            Rebuild();
        }

        private void OnValidate()
        {
            if (Application.isPlaying && body != null) Rebuild();
        }

        /// <summary>
        /// Recomputes the sample layout and the derived areas. Call after changing
        /// the collider or the sample count at runtime.
        /// </summary>
        public void Rebuild()
        {
            Bounds b = LocalBounds();

            characteristicLength = Mathf.Max(0.05f, Mathf.Max(b.size.x, b.size.z));

            // Frontal area from the two smaller dimensions and wetted area from the
            // box surface. Approximations, deliberately - the alternative is a mesh
            // integral, and the ratio between them is what the drag model actually
            // cares about, not their absolute values.
            frontalArea = Mathf.Max(0.01f, b.size.y * Mathf.Min(b.size.x, b.size.z));
            wettedArea = Mathf.Max(0.01f,
                2f * (b.size.x * b.size.y + b.size.y * b.size.z + b.size.x * b.size.z) * 0.5f);

            if (volume <= 0f)
            {
                // Box volume times 0.6. A hull is not a box, and using the box
                // outright makes everything float far too high - the number is the
                // rough block coefficient of a displacement hull.
                volume = Mathf.Max(0.001f, b.size.x * b.size.y * b.size.z * 0.6f);
            }

            BuildSamples(b);
            sampleVolume = volume / Mathf.Max(1, samplePoints.Length);
        }

        private Bounds LocalBounds()
        {
            Collider col = GetComponent<Collider>();
            if (col != null)
            {
                // World bounds converted to local extents. Not exact under rotation,
                // and it does not need to be: this only sizes the sample spread.
                Bounds w = col.bounds;
                Vector3 size = transform.InverseTransformVector(w.size);
                return new Bounds(Vector3.zero,
                    new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z)));
            }
            return new Bounds(Vector3.zero, Vector3.one);
        }

        private void BuildSamples(Bounds b)
        {
            int n = Mathf.Clamp(sampleCount, 1, 12);
            samplePoints = new Vector3[n];

            Vector3 e = b.extents * Mathf.Clamp01(sampleSpread);

            if (n == 1)
            {
                samplePoints[0] = Vector3.zero;
                return;
            }

            if (n <= 4)
            {
                // The four corners of the waterline plane. The minimum layout that
                // produces both roll and pitch.
                Vector3[] quad =
                {
                    new Vector3(-e.x, 0f, -e.z),
                    new Vector3( e.x, 0f, -e.z),
                    new Vector3( e.x, 0f,  e.z),
                    new Vector3(-e.x, 0f,  e.z)
                };
                for (int i = 0; i < n; i++) samplePoints[i] = quad[i];
                return;
            }

            // Above four, spread around the perimeter. A ring rather than a grid,
            // because the interior points sit near the centre of mass and add
            // almost no torque for their cost.
            for (int i = 0; i < n; i++)
            {
                float a = (i / (float)n) * Mathf.PI * 2f;
                samplePoints[i] = new Vector3(Mathf.Cos(a) * e.x, 0f, Mathf.Sin(a) * e.z);
            }
        }

        // ---------------------------------------------------------------------
        //  Physics
        // ---------------------------------------------------------------------

        private void FixedUpdate()
        {
            if (body == null || samplePoints == null) return;

            float time = Time.time;
            float dt = Time.fixedDeltaTime;

            int submergedSamples = 0;
            float totalDepth = 0f;
            Vector3 waterVelocity = Vector3.zero;
            UCWaterVolume volumeHit = null;
            WaterSample contactSample = default; // UC-T04-09A: reuse accepted physics samples, with no additional registry walk.

            for (int i = 0; i < samplePoints.Length; i++)
            {
                Vector3 world = transform.TransformPoint(samplePoints[i]);
                WaterSample s = UCWaterVolume.Sample(world, time, waterWorld); // UC-T04-09: local ownership survives overlapping other worlds.

                if (!s.submerged) continue;

                submergedSamples++;
                totalDepth += s.depth;
                waterVelocity += s.velocity;
                volumeHit = s.volume;
                contactSample = s;

                ApplyBuoyancy(world, s, dt);
            }

            float fraction = submergedSamples / (float)samplePoints.Length;
            SubmergedFraction = fraction;
            var contactChange = waterContact.Step(contactSample, transform.position, body.linearVelocity, time, false, splashSpeed);
            if (contactChange.Kind != WaterContactEventKind.None) ContactChanged?.Invoke(contactChange); // UC-T04-09A: one shared state/event source for hull and vehicle contact.

            if (submergedSamples == 0)
            {
                HandleExit();
                Turbulence = 0f;
                lastVerticalSpeed = body.linearVelocity.y;
                return;
            }

            waterVelocity /= submergedSamples;

            ApplyDrag(waterVelocity, fraction, volumeHit, dt);
            ApplyRighting(fraction);

            HandleEntry(volumeHit, totalDepth / submergedSamples);
            EmitWake(waterVelocity, fraction, volumeHit, dt);

            lastVerticalSpeed = body.linearVelocity.y;
            wasSubmerged = true;
        }

        /// <summary>
        /// Archimedes at one sample point. The force is the weight of the water the
        /// point's share of the body is displacing, and it is capped at that share's
        /// full volume - a point two metres under does not displace more water than
        /// a point just under, which is the mistake that launches objects out of
        /// deep water like a beach ball.
        /// </summary>
        private void ApplyBuoyancy(Vector3 world, in WaterSample s, float dt)
        {
            float density = s.volume != null ? s.volume.density : UCWaterSurface.WaterDensity;

            // Depth-limited submerged fraction of this sample's share. The height
            // over which it transitions is a fraction of the body, so a small object
            // does not pop as it crosses the surface.
            float transition = Mathf.Max(0.05f, characteristicLength * 0.25f);
            float submerged = Mathf.Clamp01(s.depth / transition);

            float displaced = sampleVolume * submerged;
            float force = density * displaced * 9.81f;

            // Along the surface normal, not straight up. On a steep wave face this
            // is what slides a boat down into the trough instead of holding it
            // pinned to the slope.
            Vector3 dir = Vector3.Slerp(Vector3.up, s.normal, 0.35f);

            body.AddForceAtPosition(dir * force, world, ForceMode.Force);
        }

        /// <summary>
        /// Drag, split into form and skin friction with the laminar-to-turbulent
        /// transition in between. See UCWaterSurface.DragForce for why both terms
        /// are written out instead of being folded into one coefficient.
        /// </summary>
        private void ApplyDrag(Vector3 waterVelocity, float fraction, UCWaterVolume vol, float dt)
        {
            Vector3 relative = body.linearVelocity - waterVelocity;
            float speed = relative.magnitude;

            Turbulence = UCWaterSurface.Turbulence(speed, characteristicLength, true);

            if (speed > 1e-4f)
            {
                float density = vol != null ? vol.density : UCWaterSurface.WaterDensity;
                float viscosity = vol != null ? vol.viscosityScale : 1f;

                float magnitude = UCWaterSurface.DragForce(
                    speed, density, dragCoefficient,
                    frontalArea * fraction, wettedArea * fraction,
                    characteristicLength) * dragScale * viscosity;

                // Clamped to what would bring the body to rest this step. Above that
                // the drag overshoots, reverses the velocity, and the body jitters -
                // the classic explicit-integration failure, and it happens at
                // exactly the speeds a fast boat reaches.
                float maxImpulse = speed * body.mass / Mathf.Max(1e-4f, dt);
                magnitude = Mathf.Min(magnitude, maxImpulse * 0.9f);

                body.AddForce(-relative.normalized * magnitude, ForceMode.Force);
            }

            // Angular damping, applied as a force rather than by writing
            // angularDamping, so leaving the water restores the body's own value
            // without this component having to remember what it was.
            if (angularDrag > 0f)
            {
                Vector3 w = body.angularVelocity;
                if (w.sqrMagnitude > 1e-6f)
                {
                    float scale = angularDrag * fraction *
                                  (vol != null ? vol.viscosityScale : 1f);
                    body.AddTorque(-w * scale * body.mass * 0.05f, ForceMode.Force);
                }
            }
        }

        /// <summary>
        /// Righting moment from metacentric height. A hull is stable because its
        /// centre of buoyancy moves to the low side as it heels, and the offset
        /// between that and the centre of mass is a lever that pushes it back up.
        /// Modelled directly rather than emerging from the sample layout, because
        /// getting it from geometry alone needs far more samples than a phone can
        /// afford.
        /// </summary>
        private void ApplyRighting(float fraction)
        {
            if (metacentricHeight <= 0f) return;

            Vector3 up = transform.up;
            Vector3 axis = Vector3.Cross(up, Vector3.up);
            float heel = axis.magnitude;

            if (heel < 1e-4f) return;

            // GZ = GM * sin(heel). The sine is already the cross product magnitude.
            float righting = metacentricHeight * heel * body.mass * 9.81f * fraction;
            body.AddTorque(axis.normalized * righting, ForceMode.Force);
        }

        // ---------------------------------------------------------------------
        //  Interaction with the ripple simulation
        // ---------------------------------------------------------------------

        /// <summary>
        /// The splash on the way in. Sized by the impact speed and the body's own
        /// footprint, and it is a downward impulse with a sharp ring, because that
        /// is what a body hitting water does - it punches a hole, and the crown
        /// comes back up out of the wave equation on its own.
        /// </summary>
        private void HandleEntry(UCWaterVolume vol, float averageDepth)
        {
            if (wasSubmerged) return;
            if (!emitRipples || !CanEmit(vol)) return;

            float impact = Mathf.Abs(lastVerticalSpeed);
            if (impact < 0.2f) return;

            float radius = Mathf.Max(0.25f, characteristicLength * 0.6f);
            float strength = Mathf.Min(0.5f, impact * 0.05f) * rippleScale;

            WaterImpulse imp = new WaterImpulse();
            imp.worldXZ = new Vector2(transform.position.x, transform.position.z);
            imp.radius = radius;
            imp.strength = -strength;

            // Fast entry rings sharply; something lowered in gently just makes a
            // dome. The threshold is a speed because at low speed the surface has
            // time to part around the body instead of being torn by it.
            imp.ringness = Mathf.Clamp01((impact - splashSpeed) / (splashSpeed * 2f));
            imp.sharpness = 1.4f;
            imp.foam = imp.ringness * 0.8f;

            UCWaterRippleSim.Active.Emit(imp);
        }

        /// <summary>
        /// Leaving the water. A small upward pull, because a body coming out drags
        /// a column of water with it and the surface dips where it left.
        /// </summary>
        private void HandleExit()
        {
            if (!wasSubmerged) return;
            wasSubmerged = false;

            if (!emitRipples || UCWaterRippleSim.Active == null) return;

            float speed = Mathf.Abs(lastVerticalSpeed);
            if (speed < 0.5f) return;

            WaterImpulse imp = new WaterImpulse();
            imp.worldXZ = new Vector2(transform.position.x, transform.position.z);
            imp.radius = Mathf.Max(0.2f, characteristicLength * 0.4f);
            imp.strength = Mathf.Min(0.25f, speed * 0.03f) * rippleScale;
            imp.ringness = 0.3f;
            imp.sharpness = 1.8f;
            imp.foam = 0.4f;

            UCWaterRippleSim.Active.Emit(imp);
        }

        /// <summary>
        /// The continuous wake. Rate-limited rather than emitted every step: at 50Hz
        /// a moving body would queue fifty overlapping impulses a second into the
        /// same few texels, which costs fifty quads to produce one indistinguishable
        /// result.
        ///
        /// Strength follows the turbulence, not the speed. Below the transition the
        /// body parts the water quietly however fast it looks to be going; above it
        /// the flow separates and that is where the white water is.
        /// </summary>
        private void EmitWake(Vector3 waterVelocity, float fraction, UCWaterVolume vol, float dt)
        {
            if (!emitRipples || !CanEmit(vol)) return;

            Vector3 relative = body.linearVelocity - waterVelocity;
            float speed = relative.magnitude;

            if (speed < 0.15f) return;

            rippleTimer -= dt;
            if (rippleTimer > 0f) return;

            // Faster bodies emit more often, so the wake is a continuous line
            // rather than a dotted one, but never faster than 20Hz.
            rippleTimer = Mathf.Clamp(0.35f / Mathf.Max(0.1f, speed), 0.05f, 0.5f);

            float froude = UCWaterSurface.Froude(speed, characteristicLength);

            WaterImpulse imp = new WaterImpulse();
            imp.worldXZ = new Vector2(transform.position.x, transform.position.z);
            imp.radius = Mathf.Max(0.2f, characteristicLength * 0.35f);

            // Downward. A hull pushes water aside and down; the bow wave rises
            // around it as the displaced water has nowhere else to go, which the
            // wave equation produces from the depression.
            imp.strength = -Mathf.Min(0.2f, speed * 0.02f * fraction) * rippleScale;
            imp.ringness = Mathf.Clamp01(froude);
            imp.sharpness = 1.2f;

            // White water above the hull speed, where the body starts climbing its
            // own bow wave. Below it a hull moves cleanly and leaves no foam.
            imp.foam = Mathf.Clamp01((froude - 0.4f) * 1.6f) * Turbulence;

            UCWaterRippleSim.Active.Emit(imp);
        }

        private static bool CanEmit(UCWaterVolume vol)
        {
            if (UCWaterRippleSim.Active == null) return false;
            if (vol != null && !vol.emitRipples) return false;
            return true;
        }
    }
}
