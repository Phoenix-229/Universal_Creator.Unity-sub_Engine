// -----------------------------------------------------------------------------
//  Universe Creator - Module 2: PCG Extended Capability Port
//
//  File:    UCWaterCharacter.cs
//  Purpose: Water state and water physics for a CharacterController - wading,
//           swimming, diving, surfacing, and the drag that applies in both media.
//
//  WHY NOT JUST USE UCBuoyancy
//  ---------------------------
//  UCBuoyancy drives a Rigidbody. A CharacterController is kinematic: it has no
//  mass, takes no forces, and its only verb is Move(). Everything a rigidbody gets
//  from the solver - gravity, drag, the buoyant force, the resulting velocity -
//  has to be integrated here by hand. Trying to serve both from one component
//  means every line branches on which one it is, and the result is a component
//  that is hard to read and wrong for both.
//
//  IN WATER AND OUT OF WATER
//  -------------------------
//  Drag is computed the same way in both media, with the fluid's own density and
//  kinematic viscosity substituted:
//
//      air    rho 1.204   nu 1.511e-5
//      water  rho  998    nu 1.004e-6
//
//  Water is 830x denser and 15x less viscous per unit density, so the same body at
//  the same speed sits at a Reynolds number roughly fifteen times higher in water,
//  which is why a swimmer is deep in the turbulent regime while a runner at the
//  same speed is barely leaving it. Both terms come from UCWaterSurface, so the
//  transition is the same curve a boat and a floating crate use - one physical
//  model, three components reading it.
//
//  When partly submerged the two are blended by how much of the body is in each,
//  because that is exactly what is happening: a character in waist-deep water is
//  dragging half a body through water and half through air.
//
//  MOBILE BUDGET
//  -------------
//  Three surface samples per frame - feet, waist, head. Each is a bounds test plus
//  three sines. No raycasts, no colliders, no per-frame allocation. driveMovement
//  can be switched off entirely, in which case this reads state and emits ripples
//  and nothing else, which is the cheapest useful thing it can be.
// -----------------------------------------------------------------------------

using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>
    /// Where the character is relative to the water surface.
    /// </summary>
    public enum WaterState
    {
        /// <summary>Not touching water.</summary>
        Dry = 0,

        /// <summary>Feet in, weight still on the ground. Walking is slowed.</summary>
        Wading = 1,

        /// <summary>Off the bottom, head above the surface.</summary>
        Swimming = 2,

        /// <summary>Head below the surface.</summary>
        Submerged = 3
    }

    [AddComponentMenu("Universe Creator/Water Character")]
    [RequireComponent(typeof(CharacterController))]
    [DisallowMultipleComponent]
    public class UCWaterCharacter : MonoBehaviour
    {
        // -- body ------------------------------------------------------------

        [Header("Body")]
        [Tooltip("Mass in kilograms. Only used to convert buoyant force into acceleration.")]
        public float mass = 70f;

        // 985 kg/m3 is a human with air in the lungs - just under water's 998, which
        // is why a relaxed person floats with their face barely clear and a person
        // who breathes out sinks. Exposing it means a character in armour can be
        // given 1200 and will sink, without a separate "can swim" tick box.
        [Tooltip("Average body density in kg/m3. Below the water's 998 the character " +
                 "floats, above it they sink. A human with full lungs is about 985.")]
        [Range(600f, 1600f)] public float bodyDensity = 985f;

        [Tooltip("Drag coefficient in water. 0.35 is a streamlined swimmer, 1.0 upright.")]
        [Range(0.05f, 2f)] public float waterDragCoefficient = 0.9f;

        [Tooltip("Drag coefficient in air. About 1.0 for an upright human.")]
        [Range(0.05f, 2f)] public float airDragCoefficient = 1.0f;

        // -- swimming --------------------------------------------------------

        [Header("Swimming")]
        [Tooltip("Submerged fraction at which the character stops wading and starts " +
                 "swimming. 0.65 is roughly chest deep.")]
        [Range(0.3f, 0.95f)] public float swimThreshold = 0.65f;

        [Tooltip("Horizontal speed a full stroke drives, in m/s. A decent front " +
                 "crawl is about 1.5.")]
        public float swimSpeed = 1.6f;

        [Tooltip("Vertical speed when actively diving or surfacing, in m/s.")]
        public float swimVerticalSpeed = 1.2f;

        [Tooltip("How quickly stroke input reaches full speed. Water has to be " +
                 "accelerated too, so this is never instant.")]
        [Range(0.5f, 12f)] public float strokeResponse = 3.5f;

        [Tooltip("How far the character's eye line sits below the top of the capsule. " +
                 "Head submersion is measured here, so the screen goes under at the " +
                 "same moment the state says Submerged.")]
        [Range(0f, 0.6f)] public float eyeDepthFromTop = 0.12f;

        // -- wading ----------------------------------------------------------

        [Header("Wading")]
        [Tooltip("Speed multiplier at full wading depth. Water resists a leg swing " +
                 "far harder than air does; 0.35 at chest depth is close to real.")]
        [Range(0.05f, 1f)] public float wadeSpeedAtDepth = 0.35f;

        // -- movement --------------------------------------------------------

        [Header("Movement")]
        [Tooltip("On, this component moves the CharacterController itself - gravity, " +
                 "buoyancy, current and drag. Off, it only reports state and emits " +
                 "ripples, and your own controller reads Buoyancy, Current and " +
                 "SpeedMultiplier from it.")]
        public bool driveMovement = true;

        [Tooltip("Gravity in m/s2. Applied only out of water; underwater it is " +
                 "replaced by the buoyancy balance, which already contains weight.")]
        public float gravity = -9.81f;

        [Tooltip("How strongly the water's own motion carries the character. 1 is " +
                 "the full current - a river will sweep them away.")]
        [Range(0f, 2f)] public float currentInfluence = 1f;

        // -- ripples ---------------------------------------------------------

        [Header("Ripples")]
        public bool emitRipples = true;

        [Range(0f, 4f)] public float rippleScale = 1f;

        [Tooltip("Ripples per second while wading at walking pace. Footfalls, " +
                 "roughly - two per second is a walk, four is a run.")]
        [Range(0.5f, 8f)] public float stepRate = 2.2f;

        [Tooltip("Ripples per second while swimming. One per stroke.")]
        [Range(0.2f, 4f)] public float strokeRate = 1.1f;

        // ---------------------------------------------------------------------
        //  Public state - read by game code, and by anything driving audio or VFX
        // ---------------------------------------------------------------------

        /// <summary>Current water state.</summary>
        public WaterState State { get; private set; }

        /// <summary>0 dry, 1 fully under.</summary>
        public float SubmergedFraction { get; private set; }

        /// <summary>Water surface height at the character, in world metres. Only
        /// meaningful when State is not Dry.</summary>
        public float SurfaceY { get; private set; }

        /// <summary>Water depth at the character's feet, in metres.</summary>
        public float FootDepth { get; private set; }

        /// <summary>The water body the character is in, or null.</summary>
        public UCWaterVolume Volume { get; private set; }

        /// <summary>Water velocity at the character - current plus wave orbital
        /// motion. Multiply by currentInfluence to get what is applied.</summary>
        public Vector3 Current { get; private set; }

        /// <summary>Net vertical acceleration from buoyancy and weight, m/s2.
        /// Positive is up. Exposed so a custom controller can use the same number
        /// this one does rather than deriving a second version of it.</summary>
        public float Buoyancy { get; private set; }

        /// <summary>0 fully laminar, 1 fully turbulent, in whichever medium the
        /// character is mostly in. Drives foam, spray and audio.</summary>
        public float Turbulence { get; private set; }

        /// <summary>Multiplier a walking controller should apply to its own speed
        /// while wading. 1 when dry.</summary>
        public float SpeedMultiplier { get; private set; }

        /// <summary>Seconds the head has been continuously under. Resets on
        /// surfacing. No drowning logic here - that is a game decision.</summary>
        public float SubmergedTime { get; private set; }

        /// <summary>Current velocity this component is integrating. Zero when
        /// driveMovement is off.</summary>
        public Vector3 Velocity { get { return velocity; } }

        // ---------------------------------------------------------------------
        //  Internals
        // ---------------------------------------------------------------------

        private CharacterController controller;

        private Vector3 velocity;
        private Vector3 strokeVelocity;
        private Vector3 strokeTarget;

        private float rippleTimer;
        private float bodyVolume;
        private float frontalArea;
        private float wettedArea;
        private float characteristicLength;

        private bool wasWet;
        private float lastVerticalSpeed;
        private readonly WaterContactTracker waterContact = new WaterContactTracker(); // UC-T04-09A: reuse the existing accepted feet sample.
        /// <summary>UC-T04-09: optional UC world for accepted water queries; null infers the nearest parent on enable.</summary>
        [Tooltip("Optional UC world for water queries. Empty infers the nearest parent; bind external characters explicitly when worlds overlap.")]
        public UniverseCreatorObject waterWorld;
        private WaterSample contactSample;

        /// <summary>UC-T04-09A: local body kind/depth/current and swimming state, dry after owner retirement.</summary>
        public WaterContactState Contact => waterContact.Current;

        /// <summary>UC-T04-09A: bounded main-thread water transition/wake notifications for optional sound/particles.</summary>
        public event System.Action<WaterContactEvent> ContactChanged;

        private void OnDisable()
        {
            var change = waterContact.Reset();
            wasWet = false; rippleTimer = 0; Volume = null; Current = Vector3.zero; SubmergedFraction = 0; State = WaterState.Dry;
            if (change.Kind != WaterContactEventKind.None) ContactChanged?.Invoke(change);
        }

        private void OnEnable()
        {
            controller = GetComponent<CharacterController>();
            if (waterWorld == null) waterWorld = GetComponentInParent<UniverseCreatorObject>(true); // UC-T04-09: cache the actor's scope once.
            Rebuild();
            SpeedMultiplier = 1f;
        }

        private void OnValidate()
        {
            if (isActiveAndEnabled) Rebuild();
        }

        /// <summary>
        /// Derives the body's physical dimensions from the CharacterController.
        /// Call after changing its height or radius at runtime.
        /// </summary>
        public void Rebuild()
        {
            if (controller == null) controller = GetComponent<CharacterController>();
            if (controller == null) return;

            float h = Mathf.Max(0.2f, controller.height);
            float r = Mathf.Max(0.05f, controller.radius);

            characteristicLength = h;

            // Volume from mass and density rather than from the capsule. The capsule
            // is a movement shape a designer sized for doorways; using it would make
            // buoyancy depend on level geometry decisions.
            bodyVolume = mass / Mathf.Max(1f, bodyDensity);

            // A capsule seen from the front and from the side is close enough to the
            // same rectangle for drag purposes.
            frontalArea = 2f * r * h * 0.7f;

            // Capsule surface area: cylinder side plus the two hemispherical caps.
            wettedArea = 2f * Mathf.PI * r * Mathf.Max(0f, h - 2f * r) + 4f * Mathf.PI * r * r;
        }

        // ---------------------------------------------------------------------
        //  Input from game code
        // ---------------------------------------------------------------------

        /// <summary>
        /// Swim input. worldDirection is where the character wants to go, its
        /// magnitude 0..1 is how hard. Y is the dive/surface axis. Call it every
        /// frame while swimming; it decays on its own if you stop.
        /// </summary>
        public void Swim(Vector3 worldDirection)
        {
            Vector3 flat = new Vector3(worldDirection.x, 0f, worldDirection.z);
            float flatMag = Mathf.Min(1f, flat.magnitude);
            if (flatMag > 1e-4f) flat = flat.normalized * flatMag;

            strokeTarget = flat * swimSpeed +
                           Vector3.up * Mathf.Clamp(worldDirection.y, -1f, 1f) * swimVerticalSpeed;
        }

        /// <summary>
        /// A one-off push - a kick off the bottom, a dive entry, a shove from a
        /// wave. Added straight to the integrated velocity.
        /// </summary>
        public void AddImpulse(Vector3 worldVelocityChange)
        {
            velocity += worldVelocityChange;
        }

        // ---------------------------------------------------------------------
        //  Per frame
        // ---------------------------------------------------------------------

        private void Update()
        {
            if (controller == null) return;

            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            float time = Time.time;

            SampleBody(time);
            IntegrateStroke(dt);

            if (driveMovement) Integrate(dt);
            else velocity = Vector3.zero;

            UpdateRipples(dt);
            var contactChange = waterContact.Step(contactSample, transform.position, driveMovement ? velocity : controller.velocity,
                time, State == WaterState.Swimming || State == WaterState.Submerged, 1.5f); // UC-T04-09A: preserve this controller's movement/drag ownership.
            if (contactChange.Kind != WaterContactEventKind.None) ContactChanged?.Invoke(contactChange);

            wasWet = State != WaterState.Dry;
            lastVerticalSpeed = driveMovement ? velocity.y : controller.velocity.y;
        }

        /// <summary>
        /// Three samples down the body axis. Three and not one, because a single
        /// sample at the centre cannot tell "standing in a puddle" from "floating
        /// with the head under" - and those are the two states a player most needs
        /// the game to get right.
        /// </summary>
        private void SampleBody(float time)
        {
            float h = Mathf.Max(0.2f, controller.height);
            Vector3 centre = transform.TransformPoint(controller.center);
            Vector3 up = transform.up;

            Vector3 feet = centre - up * (h * 0.5f);
            Vector3 head = centre + up * (h * 0.5f);
            Vector3 eyes = head - up * eyeDepthFromTop;

            WaterSample sFeet = UCWaterVolume.Sample(feet, time, waterWorld); // UC-T04-09: footprint, movement and sound use one world scope.
            contactSample = sFeet; // UC-T04-09A: sound/contact reads the same local footprint as swimming.

            if (!sFeet.submerged)
            {
                // Feet dry means the body is dry. The head cannot be underwater while
                // the feet are above it, short of a character standing on their hands
                // inside a waterfall, which is not a case worth three samples.
                State = WaterState.Dry;
                SubmergedFraction = 0f;
                FootDepth = 0f;
                Volume = null;
                Current = Vector3.zero;
                Buoyancy = 0f;
                SpeedMultiplier = 1f;
                SubmergedTime = 0f;

                Vector3 airRelative = driveMovement ? velocity : controller.velocity;
                Turbulence = UCWaterSurface.Turbulence(airRelative.magnitude,
                                                       characteristicLength, false);
                return;
            }

            Volume = sFeet.volume;
            SurfaceY = sFeet.surfaceY;
            FootDepth = sFeet.depth;
            Current = sFeet.velocity;

            // Fraction of the body below the surface, measured along the body axis
            // rather than in world Y, so a character on a slope or knocked sideways
            // still reports what is actually wet.
            float bodyY = Vector3.Dot(head - feet, Vector3.up);
            float span = Mathf.Abs(bodyY) > 0.05f ? Mathf.Abs(bodyY) : h;
            SubmergedFraction = Mathf.Clamp01((SurfaceY - feet.y) / span);

            WaterSample sEyes = UCWaterVolume.Sample(eyes, time, waterWorld);
            bool headUnder = sEyes.submerged;

            State = headUnder ? WaterState.Submerged
                  : SubmergedFraction >= swimThreshold ? WaterState.Swimming
                  : WaterState.Wading;

            SubmergedTime = headUnder ? SubmergedTime + Time.deltaTime : 0f;

            // Wading resistance. Squared, because drag goes with the square of the
            // area presented and the leg sweeps through a wider band of water the
            // deeper it goes - a linear fall makes knee-deep water feel like a
            // carpet and chest-deep water feel like knee-deep.
            float wade = Mathf.Clamp01(SubmergedFraction / Mathf.Max(0.05f, swimThreshold));
            SpeedMultiplier = Mathf.Lerp(1f, wadeSpeedAtDepth, wade * wade);

            ComputeBuoyancy();
        }

        /// <summary>
        /// Net vertical acceleration from Archimedes and weight.
        ///
        ///     Fb = rho_water * V_submerged * g      up
        ///     Fw = m * g                            down
        ///     a  = (Fb - Fw) / m
        ///
        /// Written as an acceleration rather than a force because a
        /// CharacterController has no mass to divide by later. With the default
        /// density the balance lands just positive, so a floating character rises
        /// slowly to the surface and settles rather than bobbing - which is what a
        /// body at 985 against water at 998 actually does.
        /// </summary>
        private void ComputeBuoyancy()
        {
            float density = Volume != null ? Volume.density : UCWaterSurface.WaterDensity;

            float displaced = bodyVolume * SubmergedFraction;
            float buoyantForce = density * displaced * 9.81f;
            float weight = mass * 9.81f;

            Buoyancy = (buoyantForce - weight) / Mathf.Max(1f, mass);
        }

        private void IntegrateStroke(float dt)
        {
            bool swimming = State == WaterState.Swimming || State == WaterState.Submerged;

            Vector3 target = swimming ? strokeTarget : Vector3.zero;

            // Exponential approach rather than a lerp on dt, so the feel does not
            // change with frame rate.
            float k = 1f - Mathf.Exp(-strokeResponse * dt);
            strokeVelocity = Vector3.Lerp(strokeVelocity, target, k);

            // Consumed. Game code calls Swim every frame it wants to move; not
            // calling it is how you stop.
            strokeTarget = Vector3.zero;
        }

        /// <summary>
        /// One integration step covering both media. The drag term is computed twice
        /// and blended by submerged fraction rather than switched, because a
        /// character crossing the surface would otherwise change deceleration
        /// discontinuously at the exact moment the player is watching it most
        /// closely.
        /// </summary>
        private void Integrate(float dt)
        {
            float f = SubmergedFraction;

            if (State == WaterState.Dry)
            {
                velocity.y += gravity * dt;
                ApplyMediumDrag(Vector3.zero, 0f, dt);

                // Grounded: kill the downward accumulation so the controller does not
                // build up hundreds of m/s standing still, and keep a small push so
                // isGrounded stays true on slopes.
                if (controller.isGrounded && velocity.y < 0f) velocity.y = -2f;

                controller.Move(velocity * dt);
                return;
            }

            Vector3 water = Current * currentInfluence;

            if (State == WaterState.Wading)
            {
                // Still on the ground. Gravity, not buoyancy - a character standing in
                // knee-deep water is held up by the bottom, and letting buoyancy lift
                // them makes them skate over shallow beds.
                velocity.y += gravity * dt;
                if (controller.isGrounded && velocity.y < 0f) velocity.y = -2f;

                // The current still pushes them, scaled by how much of them it has.
                Vector3 push = new Vector3(water.x, 0f, water.z) * f;

                ApplyMediumDrag(water, f, dt);

                controller.Move((velocity + push) * dt);
                return;
            }

            // Swimming or submerged: off the bottom, so vertical motion is the
            // buoyancy balance and nothing else.
            velocity.y += Buoyancy * dt;

            ApplyMediumDrag(water, f, dt);

            Vector3 total = velocity + water + strokeVelocity;

            // Never push a swimmer above the surface. Without this the buoyancy
            // balance launches them clear of the water and they land back in it, over
            // and over, which reads as bobbing that gets worse the deeper they were.
            float headroom = SurfaceY - transform.position.y;
            if (total.y > 0f && headroom < 0f) total.y = 0f;

            controller.Move(total * dt);
        }

        /// <summary>
        /// Drag from both media at once. waterVelocity is the fluid's own motion, so
        /// drifting with a current costs nothing while swimming against it costs
        /// everything - which is what makes a river read as a river.
        /// </summary>
        private void ApplyMediumDrag(Vector3 waterVelocity, float submergedFraction, float dt)
        {
            Vector3 relative = velocity - waterVelocity * submergedFraction;
            float speed = relative.magnitude;

            if (speed < 1e-4f)
            {
                Turbulence = 0f;
                return;
            }

            float viscosityScale = Volume != null ? Volume.viscosityScale : 1f;
            float waterDensity = Volume != null ? Volume.density : UCWaterSurface.WaterDensity;

            float wet = Mathf.Clamp01(submergedFraction);
            float dry = 1f - wet;

            float inWater = UCWaterSurface.DragForce(
                speed, waterDensity, waterDragCoefficient,
                frontalArea * wet, wettedArea * wet, characteristicLength) * viscosityScale;

            float inAir = UCWaterSurface.DragForce(
                speed, UCWaterSurface.AirDensity, airDragCoefficient,
                frontalArea * dry, wettedArea * dry, characteristicLength);

            float force = inWater + inAir;

            // F = ma, and here the mass is real even though the controller has none.
            float deceleration = force / Mathf.Max(1f, mass);
            float delta = Mathf.Min(deceleration * dt, speed);

            velocity -= relative / speed * delta;

            // Reported from whichever medium the body is mostly in. A half-submerged
            // swimmer is turbulent in the water and barely turbulent in the air, and
            // the water is the half that makes the noise and the foam.
            Turbulence = UCWaterSurface.Turbulence(speed, characteristicLength, wet > 0.5f);
        }

        // ---------------------------------------------------------------------
        //  Ripples
        // ---------------------------------------------------------------------

        private void UpdateRipples(float dt)
        {
            if (!emitRipples) return;
            if (UCWaterRippleSim.Active == null) return;
            if (Volume != null && !Volume.emitRipples) return;

            if (State == WaterState.Dry)
            {
                if (wasWet) EmitExit();
                return;
            }

            if (!wasWet) { EmitEntry(); return; }

            rippleTimer -= dt;
            if (rippleTimer > 0f) return;

            Vector3 horizontal = controller.velocity;
            horizontal.y = 0f;
            float speed = horizontal.magnitude;

            if (State == WaterState.Wading) EmitStep(speed);
            else EmitStroke(speed);
        }

        /// <summary>
        /// A footfall. Small, sharp and ringed - a foot entering water displaces a
        /// little of it very quickly, which is a high-frequency ring rather than the
        /// broad dome a body makes.
        /// </summary>
        private void EmitStep(float speed)
        {
            if (speed < 0.15f) { rippleTimer = 0.25f; return; }

            // Faster movement means faster steps. Clamped so a sprinting character
            // does not fill the impulse queue.
            float rate = stepRate * Mathf.Clamp(speed / 1.4f, 0.5f, 2.5f);
            rippleTimer = 1f / Mathf.Max(0.5f, rate);

            float r = Mathf.Max(0.12f, controller.radius * 0.8f);

            WaterImpulse imp = new WaterImpulse();
            imp.worldXZ = new Vector2(transform.position.x, transform.position.z);
            imp.radius = r;
            imp.strength = -Mathf.Min(0.12f, 0.02f + speed * 0.03f) * rippleScale;
            imp.ringness = 0.65f;
            imp.sharpness = 2.6f;

            // Foam from turbulence, so a slow wade is clean and a run through the
            // shallows throws white water. Same number the drag used.
            imp.foam = Mathf.Clamp01(Turbulence * 1.2f) * Mathf.Clamp01(speed / 2f);

            UCWaterRippleSim.Active.Emit(imp);
        }

        /// <summary>
        /// A swimming stroke. Broader and softer than a footfall - an arm pulling
        /// through water moves a lot of it slowly.
        /// </summary>
        private void EmitStroke(float speed)
        {
            float rate = strokeRate * Mathf.Clamp(0.6f + speed, 0.6f, 2.2f);
            rippleTimer = 1f / Mathf.Max(0.2f, rate);

            // Fully submerged strokes disturb the surface much less - the swimmer is
            // below it, and only the displaced column reaches the top.
            float surfaceCoupling = State == WaterState.Submerged ? 0.25f : 1f;

            WaterImpulse imp = new WaterImpulse();
            imp.worldXZ = new Vector2(transform.position.x, transform.position.z);
            imp.radius = Mathf.Max(0.2f, controller.radius * 2.2f);
            imp.strength = -Mathf.Min(0.1f, 0.02f + speed * 0.02f) * rippleScale * surfaceCoupling;
            imp.ringness = 0.35f;
            imp.sharpness = 1.6f;
            imp.foam = Mathf.Clamp01(speed * 0.5f) * Turbulence * surfaceCoupling;

            UCWaterRippleSim.Active.Emit(imp);
        }

        /// <summary>
        /// Hitting the water. Scaled by the speed on the way in, so stepping off a
        /// bank and falling off a cliff do not produce the same splash.
        /// </summary>
        private void EmitEntry()
        {
            float impact = Mathf.Abs(lastVerticalSpeed);

            rippleTimer = 0.12f;

            WaterImpulse imp = new WaterImpulse();
            imp.worldXZ = new Vector2(transform.position.x, transform.position.z);
            imp.radius = Mathf.Max(0.2f, controller.radius * (1f + Mathf.Min(2f, impact * 0.2f)));
            imp.strength = -Mathf.Min(0.45f, 0.03f + impact * 0.05f) * rippleScale;
            imp.ringness = Mathf.Clamp01(impact / 6f);
            imp.sharpness = 1.5f;
            imp.foam = Mathf.Clamp01(impact / 4f);

            UCWaterRippleSim.Active.Emit(imp);
        }

        /// <summary>
        /// Climbing out. The surface dips where the body leaves it, dragging a
        /// column of water up with it.
        /// </summary>
        private void EmitExit()
        {
            WaterImpulse imp = new WaterImpulse();
            imp.worldXZ = new Vector2(transform.position.x, transform.position.z);
            imp.radius = Mathf.Max(0.2f, controller.radius * 1.5f);
            imp.strength = 0.05f * rippleScale;
            imp.ringness = 0.4f;
            imp.sharpness = 2f;
            imp.foam = 0.35f;

            UCWaterRippleSim.Active.Emit(imp);
        }
    }
}
