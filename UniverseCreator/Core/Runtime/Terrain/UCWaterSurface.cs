// -----------------------------------------------------------------------------
//  Universe Creator - Module 2: PCG Extended Capability Port
//
//  File:    UCWaterSurface.cs
//  Purpose: The one definition of where the water surface is. Both the shader and
//           the physics read it, and they must agree to the millimetre.
//
//  WHY THIS FILE EXISTS
//  --------------------
//  The wave displacement is computed twice - once in UC_WaterInput.hlsl for the
//  vertices you see, once here for the buoyancy you feel. If the two drift, a boat
//  floats a hand's width above the crest or sinks into it, and the bug is
//  invisible in a screenshot and obvious the moment anything moves. So the C#
//  below is a line-for-line mirror of UCWaveOffset, with the constants written out
//  in the same order, and the comment on each side points at the other.
//
//  CHANGING THE WAVE SHAPE MEANS CHANGING BOTH.
//
//  MOBILE BUDGET
//  -------------
//  Three sine calls per sample, and a float lookup. A boat sampling twelve points
//  costs thirty-six sines a frame, which is nothing. The expensive part of water
//  physics is never the surface height - it is asking the GPU where the water is,
//  which this deliberately never does.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// Water surface parameters, shared by the renderer and the physics. One of
    /// these lives on the water body; physics queries go through it so a lake with
    /// small chop and an ocean with a two-metre swell behave differently without
    /// either of them knowing about the other.
    /// </summary>
    [System.Serializable]
    public struct WaterSurfaceParams
    {
        [Tooltip("Still-water level in world Y, in metres.")]
        public float level;

        [Tooltip("Peak vertical displacement, in metres. Must match _WaveHeight on " +
                 "the water material or floating objects sit off the visible surface.")]
        public float waveHeight;

        [Tooltip("Distance between crests, in metres. Must match _WaveLength.")]
        public float waveLength;

        [Tooltip("Must match _WaveSpeed.")]
        public float waveSpeed;

        [Tooltip("How many wave trains are active, 1..4. Must match _WaveOctaves on " +
                 "the material - a lake runs one or two, an open ocean four - or a boat " +
                 "sits on a different swell than the one drawn.")]
        public float octaves;

        [Tooltip("Storm swell multiplier, 1 on a calm day. Must match _WaveStormAmp; " +
                 "the weather layer raises both together so rough seas toss a hull the " +
                 "same amount they visibly heave.")]
        public float stormAmp;

        [Tooltip("Surface current in world XZ, metres per second. Drives the drift " +
                 "term on anything floating.")]
        public Vector2 flow;

        /// <summary>UC-T04-09: downstream ambient-pattern advection multiplier, 0 for still water and 1 for rivers.</summary>
        public float flowAdvection;

        /// <summary>UC-T04-09: enable the renderer's bounded horizontal ambient displacement; false preserves legacy standalone queries.</summary>
        public bool horizontalDisplacement;

        public static WaterSurfaceParams Default(float level)
        {
            WaterSurfaceParams p = new WaterSurfaceParams();
            p.level = level;
            p.waveHeight = 0.12f;
            p.waveLength = 26f;
            p.waveSpeed = 0.6f;
            p.octaves = 3f;
            p.stormAmp = 1f;
            p.flow = new Vector2(0.06f, 0.03f);
            return p;
        }
    }

    /// <summary>
    /// Where the water is, how fast it is moving, and what it does to something
    /// moving through it. Stateless and thread-safe.
    /// </summary>
    public static class UCWaterSurface
    {
        // ---------------------------------------------------------------------
        //  Physical constants
        // ---------------------------------------------------------------------

        /// <summary>Fresh water at 20C, kg/m3.</summary>
        public const float WaterDensity = 998f;

        /// <summary>Air at sea level, 20C, kg/m3.</summary>
        public const float AirDensity = 1.204f;

        /// <summary>Kinematic viscosity of water at 20C, m2/s.</summary>
        public const float WaterKinematicViscosity = 1.004e-6f;

        /// <summary>Kinematic viscosity of air at 20C, m2/s.</summary>
        public const float AirKinematicViscosity = 1.511e-5f;

        // ---------------------------------------------------------------------
        //  Surface height
        // ---------------------------------------------------------------------

        /// <summary>
        /// Vertical wave displacement at a world XZ position.
        ///
        /// MIRROR OF UCAmbientWaveOffset IN UC_WaterInput.hlsl - the ambient term
        /// specifically, not the UCWaveOffset wrapper around it. Up to four octaves
        /// at incommensurate directions, vertical term only, same weights, same
        /// direction vectors, same frequency ratios, same fractional octave gate off
        /// <see cref="WaterSurfaceParams.octaves"/> and same storm scale off
        /// <see cref="WaterSurfaceParams.stormAmp"/>. If you change one, change the
        /// other, or physics and rendering disagree about where the water is.
        ///
        /// The wrapper's other terms are left out on purpose and must stay out:
        /// UC-T04-09: river advection now shares the material's accepted local flow.
        /// Interactive ripple and wind micro-height remain GPU-only, capped at 5 cm
        /// before the shared local-depth clamp; normals retain finer visual detail.
        /// Accepted-mesh queries interpolate displaced triangle vertices instead of
        /// evaluating this analytic function at an already displaced sample position.
        /// </summary>
        public static float WaveOffset(Vector2 worldXZ, float time, in WaterSurfaceParams p, float weight = 1f)
        {
            if (p.waveHeight <= 0.0001f) return 0f;

            worldXZ -= p.flow * (time * p.flowAdvection); // UC-T04-09: mirror the river shader's current advection.

            float k = 6.28318530718f / Mathf.Max(0.5f, p.waveLength);

            float o0 = Mathf.Clamp01(p.octaves);
            float o1 = Mathf.Clamp01(p.octaves - 1f);
            float o2 = Mathf.Clamp01(p.octaves - 2f);
            float o3 = Mathf.Clamp01(p.octaves - 3f);

            float a = Mathf.Sin((worldXZ.x * 0.86f + worldXZ.y * 0.51f) * k + time * p.waveSpeed);
            float b = Mathf.Sin((worldXZ.x * -0.39f + worldXZ.y * 0.92f) * k * 1.71f + time * p.waveSpeed * 1.43f);
            float c = Mathf.Sin((worldXZ.x * 0.62f + worldXZ.y * -0.78f) * k * 2.63f + time * p.waveSpeed * 0.79f);
            float d = Mathf.Sin((worldXZ.x * -0.81f + worldXZ.y * -0.34f) * k * 3.55f + time * p.waveSpeed * 1.17f);

            float h = a * 0.55f * o0 + b * 0.30f * o1 + c * 0.15f * o2 + d * 0.07f * o3;

            float storm = p.stormAmp > 0f ? p.stormAmp : 1f;
            return h * p.waveHeight * storm * weight;
        }

        /// <summary>UC-T04-09: horizontal ambient displacement in metres, using the same phases and conservative spectral derivative bound as the shader.</summary>
        /// <param name="worldXZ">Undisplaced world XZ in metres.</param>
        /// <param name="time">Finite simulation time in seconds.</param>
        /// <param name="p">Accepted material parameters; horizontalDisplacement selects the legacy zero-offset fallback.</param>
        /// <param name="weight">Shore/junction attenuation in 0…1.</param>
        /// <returns>XZ offset; zero for disabled waves. The derivative bound applies to constant weight and flow, not arbitrary shore gradients.</returns>
        public static Vector2 HorizontalOffset(Vector2 worldXZ, float time, in WaterSurfaceParams p, float weight = 1f)
        {
            if (!p.horizontalDisplacement || p.waveHeight <= 0.0001f) return Vector2.zero;
            worldXZ -= p.flow * (time * p.flowAdvection);
            float k = 6.28318530718f / Mathf.Max(0.5f, p.waveLength);
            Vector2 a = new Vector2(0.86f, 0.51f), b = new Vector2(-0.39f, 0.92f);
            Vector2 c = new Vector2(0.62f, -0.78f), d = new Vector2(-0.81f, -0.34f);
            float o0 = Mathf.Clamp01(p.octaves), o1 = Mathf.Clamp01(p.octaves - 1);
            float o2 = Mathf.Clamp01(p.octaves - 2), o3 = Mathf.Clamp01(p.octaves - 3);
            Vector2 g = a * (Mathf.Cos(Vector2.Dot(worldXZ, a) * k + time * p.waveSpeed) * 0.55f * o0)
                + b * (Mathf.Cos(Vector2.Dot(worldXZ, b) * k * 1.71f + time * p.waveSpeed * 1.43f) * 0.30f * o1)
                + c * (Mathf.Cos(Vector2.Dot(worldXZ, c) * k * 2.63f + time * p.waveSpeed * 0.79f) * 0.15f * o2)
                + d * (Mathf.Cos(Vector2.Dot(worldXZ, d) * k * 3.55f + time * p.waveSpeed * 1.17f) * 0.07f * o3);
            float spectral = 0.55f * o0 * a.sqrMagnitude + 0.30f * 1.71f * o1 * b.sqrMagnitude
                + 0.15f * 2.63f * o2 * c.sqrMagnitude + 0.07f * 3.55f * o3 * d.sqrMagnitude;
            float storm = p.stormAmp > 0 ? p.stormAmp : 1;
            float amplitude = Mathf.Min(p.waveHeight * storm * 0.5f, 0.35f / Mathf.Max(0.0001f, k * spectral));
            return -g * (amplitude * weight * weight);
        }

        /// <summary>Absolute world height of the surface at a point.</summary>
        public static float SurfaceHeight(Vector3 worldPosition, float time, in WaterSurfaceParams p)
        {
            return p.level + WaveOffset(new Vector2(worldPosition.x, worldPosition.z), time, p);
        }

        /// <summary>
        /// Surface normal by central difference. The step is a fraction of the
        /// wavelength rather than a fixed epsilon: a fixed step that is fine for a
        /// two-metre chop samples the same point twice on a fifty-metre swell and
        /// returns straight up, which makes a boat refuse to roll.
        /// </summary>
        public static Vector3 SurfaceNormal(Vector3 worldPosition, float time, in WaterSurfaceParams p)
        {
            if (p.waveHeight <= 0.0001f) return Vector3.up;

            float e = Mathf.Max(0.05f, p.waveLength * 0.02f);
            Vector2 xz = new Vector2(worldPosition.x, worldPosition.z);

            float hx = WaveOffset(xz + new Vector2(e, 0f), time, p)
                     - WaveOffset(xz - new Vector2(e, 0f), time, p);
            float hz = WaveOffset(xz + new Vector2(0f, e), time, p)
                     - WaveOffset(xz - new Vector2(0f, e), time, p);

            return new Vector3(-hx, 2f * e, -hz).normalized;
        }

        /// <summary>
        /// Orbital velocity of the water itself at a point on the surface.
        ///
        /// Water in a wave does not travel with the wave - it moves in circles, and
        /// a floating object bobs forward on the crest and back in the trough
        /// rather than being carried along. Approximated here by the time
        /// derivative of the displacement plus the steady current, which captures
        /// the bob without the cost of a full orbital solve.
        /// </summary>
        public static Vector3 SurfaceVelocity(Vector3 worldPosition, float time, in WaterSurfaceParams p)
        {
            Vector2 xz = new Vector2(worldPosition.x, worldPosition.z);

            const float dt = 0.05f;
            float dh = (WaveOffset(xz, time + dt, p) - WaveOffset(xz, time - dt, p)) / (2f * dt);

            return new Vector3(p.flow.x, dh, p.flow.y);
        }

        // ---------------------------------------------------------------------
        //  Drag - the laminar / turbulent split
        // ---------------------------------------------------------------------

        /// <summary>
        /// Reynolds number for a body of characteristic length L moving at speed v
        /// through a fluid of kinematic viscosity nu. Dimensionless; it is the
        /// ratio of inertial to viscous forces, and it is the single number that
        /// decides whether flow is laminar or turbulent.
        /// </summary>
        public static float Reynolds(float speed, float length, float kinematicViscosity)
        {
            return speed * Mathf.Max(0.001f, length) / Mathf.Max(1e-9f, kinematicViscosity);
        }

        /// <summary>
        /// Skin-friction coefficient, blended across the laminar-to-turbulent
        /// transition.
        ///
        /// Below Re 5e5 the boundary layer is laminar and Blasius applies:
        ///     Cf = 1.328 / sqrt(Re)
        /// Above about Re 1e6 it is fully turbulent and the Prandtl-Schlichting
        /// power law applies:
        ///     Cf = 0.074 / Re^0.2
        /// Between them it is transitional, and nature blends rather than switches,
        /// so this does too.
        ///
        /// This matters in a game and is not academic dressing: turbulent friction
        /// falls off far more slowly with speed than laminar friction does, so a
        /// swimmer who has just pushed off decelerates on a visibly different curve
        /// from one who is drifting. Using one coefficient for both makes slow
        /// motion feel like syrup or fast motion feel frictionless, and no amount of
        /// tuning a single drag number fixes both ends at once.
        /// </summary>
        public static float SkinFriction(float reynolds)
        {
            if (reynolds < 1f) return 1f;

            float laminar = 1.328f / Mathf.Sqrt(reynolds);
            float turbulent = 0.074f / Mathf.Pow(reynolds, 0.2f);

            // Transition band. Log-space because Re spans six decades over the
            // range a game actually visits, and a linear blend would spend the
            // whole band on its top decade.
            float t = Mathf.InverseLerp(5.69897f, 6f, Mathf.Log10(reynolds));   // 5e5 -> 1e6
            return Mathf.Lerp(laminar, turbulent, Mathf.Clamp01(t));
        }

        /// <summary>
        /// How turbulent the flow around a body is, 0 fully laminar to 1 fully
        /// turbulent. Drives foam and wake emission - a body moving slowly through
        /// water parts it silently, and the same body moving fast tears it and goes
        /// white. That threshold is a Reynolds number, not a speed.
        /// </summary>
        public static float Turbulence(float speed, float length, bool submerged)
        {
            float nu = submerged ? WaterKinematicViscosity : AirKinematicViscosity;
            float re = Reynolds(speed, length, nu);
            if (re < 1f) return 0f;

            return Mathf.Clamp01(Mathf.InverseLerp(5.69897f, 6.3f, Mathf.Log10(re)));
        }

        /// <summary>
        /// Drag force magnitude on a body, combining form drag and skin friction.
        ///
        ///     form  = 0.5 * rho * v^2 * Cd * A          (turbulent, dominates fast)
        ///     skin  = 0.5 * rho * v^2 * Cf * Awetted    (viscous, dominates slow)
        ///
        /// Both are written out rather than folded into one coefficient, because
        /// they scale differently with size and the ratio between them is what
        /// makes a large slow hull and a small fast pebble feel like different
        /// objects rather than the same object at different speeds.
        /// </summary>
        public static float DragForce(float speed, float density, float dragCoefficient,
                                      float frontalArea, float wettedArea, float length)
        {
            if (speed <= 1e-4f) return 0f;

            float nu = density > 100f ? WaterKinematicViscosity : AirKinematicViscosity;
            float cf = SkinFriction(Reynolds(speed, length, nu));

            float q = 0.5f * density * speed * speed;
            return q * (dragCoefficient * frontalArea + cf * wettedArea);
        }

        /// <summary>
        /// Froude number - the ratio of a body's speed to the speed of the waves it
        /// makes. Above about 0.4 a hull starts climbing its own bow wave, which is
        /// where the visible wake and the white water begin, and it is why a small
        /// fast boat throws far more spray than a large one at the same speed.
        /// </summary>
        public static float Froude(float speed, float waterlineLength)
        {
            return speed / Mathf.Sqrt(9.81f * Mathf.Max(0.05f, waterlineLength));
        }
    }
}
