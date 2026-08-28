// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//  File:    UCWindManager.cs
//  Purpose: The one authority for wind in a world. Everything that moves in the
//           wind reads from here, so grass, trees, bushes, water and dust all
//           agree on which way it is blowing and how hard.
//
//  WHY A SINGLE COMPONENT
//  ----------------------
//  Wind was previously three unrelated things: TerrainData's waving-grass
//  settings, a bendFactor per tree prototype, and a flow vector on water. Each
//  was tuned separately, so a strong wind on the grass could sit next to still
//  trees and flat water. One source of truth removes that whole class of
//  mismatch: set direction and strength once, and every consumer follows.
//
//  WHAT DRIVES WHAT
//    - Global shader uniforms (_UCWind*)  -> the grass/bush detail shader,
//      water ripples, and anything else that wants wind in a shader.
//    - A real Unity WindZone                -> tree bending, which Unity's own
//      tree shaders read and which nothing else can substitute for.
//    - TerrainData.wavingGrass*             -> Unity's built-in grass, kept in
//      sync so a project that has not switched to our detail shader still
//      responds to the same settings.
//
//  THE FOUR CLASSES MOVE DIFFERENTLY, AND THAT IS THE POINT
//  --------------------------------------------------------
//    grass   Light, no mass to speak of. Lies right over in a breeze, recovers
//            instantly, flutters fast. Bends from the root.
//    shrub   Woody. Its lower quarter does not move at all, it needs more wind
//            than grass before it gives, and it flutters slower because a leaf
//            cluster carries more mass than a blade.
//    tree    Barely moves. The trunk holds and only the canopy shifts, and only
//            a little - treeResponse defaults to 0.18 for that reason. Unity's
//            tree shaders read a WindZone and nothing else, so that is the route.
//    water   Reads the gust crest rather than a bend, so a lake gets drifting
//            patches of ripple instead of a scaled copy of the meadow.
//
//  The four response values below scale those models against each other. They do
//  not change the models: turning grassResponse down makes grass move less, it
//  does not make grass move like a shrub.
//
//  The sway maps baked by the Wind Sway Parameter Bake node are uploaded here as
//  a texture, which is what finally connects that node to something visible.
//
//  EDIT MODE RESOLVES ONCE
//  -----------------------
//  This component is [ExecuteAlways], so its Update ran on every editor tick -
//  and an editor tick is not a frame. Unity pumps it far faster than a frame rate
//  while the mouse moves, and each pass wrote a transform rotation, wrote every
//  terrain's waving-grass settings, and asked Unity for Terrain.activeTerrains,
//  which hands back a freshly allocated array every single call. A still scene
//  nobody was playing therefore ran a garbage treadmill and kept the scene and
//  the terrain asset permanently dirty.
//
//  So edit mode now resolves once: on enable, and again on each edit through
//  OnValidate. Nothing recomputes until something changes. Authors who want a
//  breathing Scene view opt in with animateInEditMode, and even then it is capped
//  rather than run once per tick.
//
//  Play mode still updates every frame - it has to, gusts move - but the two
//  writes that are not shader uniforms are gated behind a change check, so the
//  per-frame cost is four shader globals and one small array upload and nothing
//  that allocates.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    [ExecuteAlways]
    [AddComponentMenu("Universe Creator/Wind Manager")]
    public class UCWindManager : MonoBehaviour
    {
        // ---- shader uniform ids ---------------------------------------------
        //
        //  Resolved once. Shader.PropertyToID does a string hash every call, and
        //  these are set every frame.

        private static readonly int IdWindDir      = Shader.PropertyToID("_UCWindDirection");
        private static readonly int IdWindParams   = Shader.PropertyToID("_UCWindParams");
        private static readonly int IdWindGust     = Shader.PropertyToID("_UCWindGust");
        private static readonly int IdSwayTex      = Shader.PropertyToID("_UCSwayMap");
        private static readonly int IdSwayWindow   = Shader.PropertyToID("_UCSwayWindow");
        private static readonly int IdSwayEnabled  = Shader.PropertyToID("_UCSwayMapEnabled");
        private static readonly int IdResponse     = Shader.PropertyToID("_UCWindResponse");
        private static readonly int IdInteractors  = Shader.PropertyToID("_UCInteractors");
        private static readonly int IdInteractStr  = Shader.PropertyToID("_UCInteractStrength");
        private static readonly int IdInteractPrm  = Shader.PropertyToID("_UCInteractParams");

        /// <summary>
        /// Matches UC_MAX_INTERACTORS in UC_Wind.hlsl. Eight is not a guess: the
        /// array is uploaded every frame and read per vertex, and past a handful of
        /// bodies nobody can tell which plant was parted by which.
        /// </summary>
        public const int MaxInteractors = 8;

        // Registered movers that part vegetation. Static because the interactor is a
        // component on the player and there is no reason to make it find the manager.
        private static readonly List<UCVegetationInteractor> interactors =
            new List<UCVegetationInteractor>(MaxInteractors);

        // Reused so Apply allocates nothing on the per-frame path.
        private static readonly Vector4[] interactorData = new Vector4[MaxInteractors];
        private static readonly float[] interactorStrength = new float[MaxInteractors];

        internal static void Register(UCVegetationInteractor i)
        {
            if (i == null || interactors.Contains(i)) return;
            interactors.Add(i);
        }

        internal static void Unregister(UCVegetationInteractor i)
        {
            if (i == null) return;
            interactors.Remove(i);
        }

        // ---- authored settings ----------------------------------------------

        [Header("Direction and strength")]

        [Tooltip("Compass direction the wind blows towards, in degrees. 0 = +Z (north), 90 = +X (east).")]
        [Range(0f, 360f)]
        public float directionDegrees = 45f;

        [Tooltip("Wind speed in metres per second. 2 is a light breeze, 8 a strong wind, 15+ a gale.")]
        [Range(0f, 30f)]
        public float speedMetresPerSecond = 5f;

        [Header("Gusting")]

        [Tooltip("How much the wind pulses. 0 is a steady flow, 1 swings between calm and double strength.")]
        [Range(0f, 1f)]
        public float gustiness = 0.45f;

        [Tooltip("Seconds per gust cycle. Longer is a slow landscape-scale breathing; shorter is agitated.")]
        [Range(0.5f, 30f)]
        public float gustPeriodSeconds = 7f;

        [Tooltip("How far a gust travels per second, in metres. Gusts sweep across the world rather than "
               + "pulsing everywhere at once, which is what makes a wind field read as real.")]
        [Range(0f, 60f)]
        public float gustTravelSpeed = 14f;

        [Tooltip("Size of one gust cell in metres. 250 is slow rolling; 60 is storm-like chop.")]
        [Range(10f, 600f)]
        public float gustSizeMetres = 250f;

        [Header("Per-consumer response")]

        [Tooltip("How strongly grass and ground cover respond. Grass is light and moves the most.")]
        [Range(0f, 2f)]
        public float grassResponse = 1f;

        [Tooltip("How strongly bushes respond. Heavier than grass, lighter than a tree.")]
        [Range(0f, 2f)]
        public float bushResponse = 0.55f;

        [Tooltip("How strongly trees respond. Deliberately low: a real tree does not sway like grass. "
               + "The trunk barely moves and only the canopy shifts with a gust. Raising this past ~0.4 "
               + "starts to look like rubber.")]
        [Range(0f, 1f)]
        public float treeResponse = 0.18f;

        [Tooltip("How strongly wind ruffles water surfaces into ripples.")]
        [Range(0f, 2f)]
        public float waterResponse = 1f;

        [Header("Integration")]

        [Tooltip("Keep Unity's built-in waving-grass settings in sync with this component. Leave on so "
               + "terrains still respond even where our detail shader is not used.")]
        public bool driveBuiltInGrass = true;

        [Tooltip("Create and drive a Unity WindZone. Required for tree bending: Unity's tree shaders read "
               + "the WindZone and nothing else can stand in for it.")]
        public bool driveWindZone = true;

        [Tooltip("Animate wind in the Scene view. Off by default, because edit mode is not a game loop: "
               + "wind resolves once when this component is enabled and once per edit, and the Scene view "
               + "holds that still frame. Turning this on drives it continuously at a capped rate, which "
               + "costs editor CPU for as long as it is on. Play mode is unaffected either way.")]
        public bool animateInEditMode;

        // ---- runtime state ---------------------------------------------------

        // Edit-mode pacing. Unity pumps the editor's update far faster than a frame
        // rate while the mouse is moving, so even the opt-in animated path is capped
        // instead of running once per tick.
        private const float EditModeApplyHz = 30f;
        private float lastEditApplyTime = float.NegativeInfinity;

        // Integration gate. Rotating the WindZone is a scene write and setting
        // wavingGrass* is an asset write; both are skipped unless the wind that
        // feeds them has actually moved. A quantum of 1/64 is finer than the 8-bit
        // value these end up driving, so nothing visible is lost.
        private const float StrengthQuantum = 1f / 64f;
        private int integrationSignature;
        private bool hasIntegrationSignature;

        // The terrain sync runs on its own slow clock rather than off the signature.
        // Four times a second is enough to pick up a terrain that appeared after the
        // last wind change, and it turns the one call here that allocates - Unity
        // hands back a fresh array for activeTerrains every time it is asked - from a
        // per-frame cost into a rounding error.
        private const float GrassSyncHz = 4f;
        private float lastGrassSyncTime = float.NegativeInfinity;

        // Guarded because WindZone lives in Unity's optional Wind module. Our own
        // manifest requires it, but a buyer who strips built-in modules to shrink a
        // mobile build would otherwise take a compile error in the runtime assembly
        // - and that assembly compiles first, so the whole toolkit would vanish from
        // the menus over one tree-bending feature. Without the module everything
        // except tree bend still works.
#if !UNITY_MODULE_WIND_DISABLED
        private WindZone zone;
#endif
        private Texture2D swayMap;
        private Vector4 swayWindow = new Vector4(0f, 0f, 1f, 1f);
        private bool hasSwayMap;

        /// <summary>Unit vector the wind blows towards, in world XZ.</summary>
        public Vector2 DirectionXZ
        {
            get
            {
                float r = directionDegrees * Mathf.Deg2Rad;
                return new Vector2(Mathf.Sin(r), Mathf.Cos(r));
            }
        }

        /// <summary>
        /// Current gust multiplier, 0.. ~2. Time-varying, so this is the value that
        /// makes a still scene breathe rather than hold one constant strength.
        /// </summary>
        public float CurrentGust
        {
            get
            {
                float t = CurrentTime();
                float period = Mathf.Max(0.01f, gustPeriodSeconds);

                // Two waves at incommensurate rates, so the pattern does not repeat
                // on an obvious beat the way a single sine does.
                float a = Mathf.Sin(t * (Mathf.PI * 2f / period));
                float b = Mathf.Sin(t * (Mathf.PI * 2f / (period * 0.37f)));
                float pulse = (a * 0.7f + b * 0.3f);

                return 1f + pulse * Mathf.Clamp01(gustiness);
            }
        }

        /// <summary>Wind speed including the current gust, in metres per second.</summary>
        public float CurrentSpeed
        {
            get { return Mathf.Max(0f, speedMetresPerSecond) * Mathf.Max(0f, CurrentGust); }
        }

        /// <summary>
        /// Hands the wind over to the weather layer without opening a second wind
        /// source. It writes the same <c>directionDegrees</c> and
        /// <c>speedMetresPerSecond</c> the author's own values feed, so every
        /// consumer - the shader upload, the WindZone, the built-in grass - keeps
        /// following this one manager, and this stays the only thing that writes a
        /// wind direction anywhere. The per-frame <c>Update</c> uploads the result;
        /// nothing here touches the shader itself.
        ///
        /// A near-zero vector carries no bearing, so the direction is left as it was
        /// rather than snapped to north by an atan2 of two zeros. The compass is the
        /// one the getter inverts: 0 deg is +Z, 90 deg is +X.
        /// </summary>
        /// <param name="directionXZ">World XZ the wind blows towards; magnitude ignored.</param>
        /// <param name="speedMPerS">Sustained wind speed in metres per second.</param>
        public void SetWeatherWind(Vector2 directionXZ, float speedMPerS)
        {
            if (directionXZ.sqrMagnitude > 0f)
            {
                directionDegrees =
                    Mathf.Atan2(directionXZ.x, directionXZ.y) * Mathf.Rad2Deg;
            }

            speedMetresPerSecond = Mathf.Max(0f, speedMPerS);
        }

        private void OnEnable()
        {
            Apply();
        }

        private void OnDisable()
        {
            // Leave nothing behind that would make an unrelated scene wave.
            Shader.SetGlobalFloat(IdSwayEnabled, 0f);
        }

        private void Update()
        {
            if (!Application.isPlaying)
            {
                // Edit mode resolved once already, on enable, and resolves again on
                // every edit through OnValidate. Recomputing here as well would be
                // work nobody asked for on a scene nobody is playing.
                if (!animateInEditMode) return;

                float now = Time.realtimeSinceStartup;
                if (now - lastEditApplyTime < 1f / EditModeApplyHz) return;
                lastEditApplyTime = now;
            }

            Apply();
        }

        private void OnValidate()
        {
            if (!isActiveAndEnabled) return;

            // An edit has to show at once, so neither gate is allowed to hold it
            // back: both are there to suppress repeated identical work, not to
            // delay a change the author just made.
            hasIntegrationSignature = false;
            lastGrassSyncTime = float.NegativeInfinity;

            Apply();
        }

        /// <summary>
        /// Pushes the current wind state everywhere it needs to go. Safe to call
        /// every frame; it does no allocation.
        /// </summary>
        public void Apply()
        {
            Vector2 dir = DirectionXZ;
            float gust = CurrentGust;
            float speed = CurrentSpeed;

            // Normalised strength for shaders. Real gales are ~25 m/s, so dividing
            // by 25 puts the useful range in 0..1 without clipping a storm to flat.
            float strength01 = Mathf.Clamp01(speed / 25f);

            Shader.SetGlobalVector(IdWindDir, new Vector4(dir.x, 0f, dir.y, 0f));

            Shader.SetGlobalVector(IdWindParams, new Vector4(
                strength01,                              // x: normalised strength
                Mathf.Max(0.01f, gustSizeMetres),        // y: gust cell size, metres
                Mathf.Max(0f, gustTravelSpeed),          // z: gust travel, m/s
                CurrentTime()));                         // w: wind time

            Shader.SetGlobalVector(IdWindGust, new Vector4(
                gust,
                Mathf.Clamp01(gustiness),
                0f,
                0f));

            // Per-class response. The three vegetation classes are balanced against
            // each other here rather than on each material, so raising the wind
            // never turns a bush into a large blade of grass.
            Shader.SetGlobalVector(IdResponse, new Vector4(
                Mathf.Max(0f, grassResponse),
                Mathf.Max(0f, bushResponse),
                Mathf.Max(0f, treeResponse),
                Mathf.Max(0f, waterResponse)));

            UploadInteractors();

            ApplyIntegrations(strength01);
        }

        /// <summary>
        /// The half of Apply that writes to the scene and to assets rather than to
        /// shader uniforms, and so the half that is rationed.
        ///
        /// WHY THIS IS RATIONED AND THE SHADER UNIFORMS ARE NOT
        /// ---------------------------------------------------
        /// Setting a global shader vector is a cheap, side-effect-free upload. The
        /// two calls below are neither: rotating the transform marks the scene
        /// dirty, writing wavingGrass* marks the terrain asset dirty, and reading
        /// Terrain.activeTerrains allocates an array on every call. Per frame that
        /// is a save prompt that never clears and a steady drip of garbage, for a
        /// value that is quantised to eight bits by the time anything renders it.
        ///
        /// The two are rationed differently because they fail differently. The
        /// WindZone is a pure function of the fields, so a signature comparison
        /// covers it exactly. The terrain write is not - the set of terrains can
        /// change with no field touched at all - so that one runs on a slow clock
        /// instead, which no stale signature can outlast.
        /// </summary>
        private void ApplyIntegrations(float strength01)
        {
            int signature = BuildIntegrationSignature(strength01);

            if (!hasIntegrationSignature || signature != integrationSignature)
            {
                integrationSignature = signature;
                hasIntegrationSignature = true;

                if (driveWindZone) ApplyWindZone(strength01);
            }

            // Not signature-gated. A terrain that appeared after the last wind
            // change would otherwise keep waving on Unity's own defaults until
            // somebody happened to touch a wind field, so this one is on a clock.
            if (driveBuiltInGrass) SyncBuiltInGrass(strength01);
        }

        private void SyncBuiltInGrass(float strength01)
        {
            float now = Time.realtimeSinceStartup;
            if (now - lastGrassSyncTime < 1f / GrassSyncHz) return;
            lastGrassSyncTime = now;

            ApplyBuiltInGrass(strength01);
        }

        /// <summary>
        /// Everything the WindZone reads, folded into one comparable value.
        /// </summary>
        private int BuildIntegrationSignature(float strength01)
        {
            // Unchecked because this is an identity rather than an arithmetic
            // result: wrapping is harmless, and throwing on overflow here would
            // mean an ordinary set of wind settings could crash a build.
            unchecked
            {
                int h = 17;
                h = h * 31 + Mathf.RoundToInt(strength01 / StrengthQuantum);
                h = h * 31 + Mathf.RoundToInt(directionDegrees * 4f);
                h = h * 31 + Mathf.RoundToInt(gustiness * 255f);
                h = h * 31 + Mathf.RoundToInt(gustPeriodSeconds * 16f);
                h = h * 31 + Mathf.RoundToInt(treeResponse * 255f);
                h = h * 31 + (driveWindZone ? 1 : 0);
                return h;
            }
        }

        /// <summary>
        /// Publishes the movers that part vegetation. Grass carries no collider by
        /// design, so this is the only thing that makes a player visibly walk
        /// through a meadow - and it costs one small array upload per frame
        /// regardless of how many blades are on screen.
        /// </summary>
        private static void UploadInteractors()
        {
            int count = 0;

            for (int i = 0; i < interactors.Count && count < MaxInteractors; i++)
            {
                UCVegetationInteractor it = interactors[i];
                if (it == null || !it.isActiveAndEnabled) continue;

                Vector3 p = it.SamplePosition;
                interactorData[count] = new Vector4(p.x, p.y, p.z, Mathf.Max(0.01f, it.radius));
                interactorStrength[count] = Mathf.Max(0f, it.pushStrength);
                count++;
            }

            // Trailing slots are zeroed so a mover that walked away does not leave a
            // permanent dent in the grass where the array was never overwritten.
            for (int i = count; i < MaxInteractors; i++)
            {
                interactorData[i] = Vector4.zero;
                interactorStrength[i] = 0f;
            }

            Shader.SetGlobalVectorArray(IdInteractors, interactorData);
            Shader.SetGlobalFloatArray(IdInteractStr, interactorStrength);
            Shader.SetGlobalVector(IdInteractPrm, new Vector4(count, 0f, 0f, 0f));
        }

        /// <summary>
        /// Drives a Unity WindZone, which is the only route to tree bending -
        /// Unity's tree shaders sample it directly and ignore anything else.
        /// </summary>
        private void ApplyWindZone(float strength01)
        {
#if UNITY_MODULE_WIND_DISABLED
            // Wind module stripped from the build. Grass, shrubs and water still
            // read the sway maps; only Unity's tree bending is unavailable, and
            // nothing here can substitute for it.
#else
            if (zone == null)
            {
                zone = GetComponent<WindZone>();
                if (zone == null)
                {
                    zone = gameObject.AddComponent<WindZone>();
                    zone.hideFlags = HideFlags.DontSave;
                }
            }

            zone.mode = WindZoneMode.Directional;

            // The WindZone blows along its own forward axis, so the transform has
            // to point where the wind goes. Compared before assigning: writing an
            // identical rotation still marks the scene dirty, which in edit mode is
            // a save prompt the author can never clear.
            Vector2 d = DirectionXZ;
            Quaternion facing = Quaternion.LookRotation(new Vector3(d.x, 0f, d.y), Vector3.up);
            if (Quaternion.Angle(transform.rotation, facing) > 0.01f)
                transform.rotation = facing;

            // Scaled down hard on purpose. Unity's tree bending at main = 1 looks
            // like a storm; a tree in a real breeze barely moves.
            zone.windMain       = strength01 * Mathf.Clamp01(treeResponse);
            zone.windTurbulence = strength01 * Mathf.Clamp01(treeResponse) * 0.6f;
            zone.windPulseMagnitude = Mathf.Clamp01(gustiness) * 0.5f;
            zone.windPulseFrequency = 1f / Mathf.Max(0.5f, gustPeriodSeconds);
#endif
        }

        /// <summary>
        /// Mirrors the wind into Unity's built-in waving-grass settings on every
        /// terrain in the scene, so a project that has not adopted our detail
        /// shader still responds to the same controls.
        /// </summary>
        private void ApplyBuiltInGrass(float strength01)
        {
            UnityEngine.Terrain[] terrains = UnityEngine.Terrain.activeTerrains;
            if (terrains == null) return;

            float strength = strength01 * Mathf.Clamp01(grassResponse);

            for (int i = 0; i < terrains.Length; i++)
            {
                UnityEngine.Terrain t = terrains[i];
                if (t == null || t.terrainData == null) continue;

                TerrainData d = t.terrainData;

                // Assigning these is not free, so only write on a real change.
                if (!Mathf.Approximately(d.wavingGrassStrength, strength))
                    d.wavingGrassStrength = strength;

                float amount = Mathf.Clamp01(gustiness);
                if (!Mathf.Approximately(d.wavingGrassAmount, amount))
                    d.wavingGrassAmount = amount;

                float sp = Mathf.Clamp01(1f / Mathf.Max(0.5f, gustPeriodSeconds) * 2f);
                if (!Mathf.Approximately(d.wavingGrassSpeed, sp))
                    d.wavingGrassSpeed = sp;
            }
        }

        /// <summary>
        /// Uploads the amplitude and phase maps baked by Wind Sway Parameter Bake
        /// for a whole world in one call. A chunked world should use SetSwayRegion
        /// instead, which composites per chunk into one world-wide map.
        /// </summary>
        /// <param name="amplitude">Sway amplitude, 0..1, row-major, size*size.</param>
        /// <param name="phase">Sway phase, 0..1, same layout, or null.</param>
        /// <param name="size">Edge length of both maps in samples.</param>
        /// <param name="worldMin">World XZ of the map's minimum corner.</param>
        /// <param name="worldSize">World XZ extent the map covers, in metres.</param>
        public void SetSwayMap(float[] amplitude, float[] phase, int size,
                               Vector2 worldMin, Vector2 worldSize)
        {
            if (amplitude == null || size < 2 || amplitude.Length < size * size)
            {
                ClearSwayMap();
                return;
            }

            // The region covering the whole world is the whole map, so this is the
            // composite path with one chunk. One code path, one set of bugs.
            SetSwayRegion(amplitude, phase, size, size,
                          worldMin, worldSize, worldMin, worldSize);
        }

        // Composite buffer behind the sway texture. Kept alive between calls because
        // each chunk writes only its own rectangle and the rest must survive.
        private Color32[] swayPixels;
        private Vector4 swayWorld;

        /// <summary>
        /// Writes one chunk's baked sway into the world-wide sway map.
        ///
        /// WHY THIS EXISTS SEPARATELY FROM SetSwayMap
        /// ------------------------------------------
        /// The sway map is a single global shader texture, but a chunked world bakes
        /// one set of maps per chunk. Calling SetSwayMap per chunk would leave only
        /// the last chunk's window bound and every other chunk reading uniform wind.
        /// So the manager keeps one texture spanning the whole world and each chunk
        /// blits into its own rectangle of it.
        ///
        /// Unbaked area starts at full amplitude, not zero: a region the graph has
        /// not reached should read as ordinary wind, never as frozen ground.
        /// </summary>
        /// <param name="amplitude">Chunk sway amplitude, 0..1, row-major.</param>
        /// <param name="phase">Chunk sway phase, or null for zero.</param>
        /// <param name="w">Chunk map width in samples.</param>
        /// <param name="h">Chunk map height in samples.</param>
        /// <param name="regionMin">World XZ of the chunk's minimum corner.</param>
        /// <param name="regionSize">World XZ extent of the chunk, in metres.</param>
        /// <param name="worldMin">World XZ of the whole world's minimum corner.</param>
        /// <param name="worldSize">World XZ extent of the whole world, in metres.</param>
        public void SetSwayRegion(float[] amplitude, float[] phase, int w, int h,
                                  Vector2 regionMin, Vector2 regionSize,
                                  Vector2 worldMin, Vector2 worldSize)
        {
            if (amplitude == null || w < 2 || h < 2) return;
            if (amplitude.Length < w * h) return;
            if (regionSize.x <= 1e-3f || regionSize.y <= 1e-3f) return;

            float wx = Mathf.Abs(worldSize.x) < 1e-3f ? regionSize.x : worldSize.x;
            float wz = Mathf.Abs(worldSize.y) < 1e-3f ? regionSize.y : worldSize.y;

            int res = ChooseResolution(wx, wz);
            Vector4 window = new Vector4(worldMin.x, worldMin.y, wx, wz);

            EnsureSwayTexture(res, window);

            // Destination rectangle for this chunk, in composite pixels.
            float u0 = (regionMin.x - window.x) / window.z;
            float v0 = (regionMin.y - window.y) / window.w;
            float u1 = (regionMin.x + regionSize.x - window.x) / window.z;
            float v1 = (regionMin.y + regionSize.y - window.y) / window.w;

            int x0 = Mathf.Clamp(Mathf.FloorToInt(u0 * res), 0, res - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(v0 * res), 0, res - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(u1 * res), x0 + 1, res);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(v1 * res), y0 + 1, res);

            float spanU = Mathf.Max(1e-6f, u1 - u0);
            float spanV = Mathf.Max(1e-6f, v1 - v0);

            for (int y = y0; y < y1; y++)
            {
                // Centre of the destination pixel, back-projected into the chunk's
                // own 0..1 space. Pixel centres and not corners, or the composite
                // shifts half a pixel per chunk and the seams show.
                float fv = (((y + 0.5f) / res) - v0) / spanV;
                int sy = Mathf.Clamp((int)(fv * (h - 1) + 0.5f), 0, h - 1);

                for (int x = x0; x < x1; x++)
                {
                    float fu = (((x + 0.5f) / res) - u0) / spanU;
                    int sx = Mathf.Clamp((int)(fu * (w - 1) + 0.5f), 0, w - 1);

                    int src = sy * w + sx;

                    byte a = (byte)(Mathf.Clamp01(amplitude[src]) * 255f);
                    byte p = phase != null && src < phase.Length
                           ? (byte)(Mathf.Clamp01(phase[src]) * 255f)
                           : (byte)0;

                    swayPixels[y * res + x] = new Color32(a, p, 0, 255);
                }
            }

            swayMap.SetPixels32(swayPixels);
            swayMap.Apply(false, false);

            swayWindow = window;
            hasSwayMap = true;

            Shader.SetGlobalTexture(IdSwayTex, swayMap);
            Shader.SetGlobalVector(IdSwayWindow, swayWindow);
            Shader.SetGlobalFloat(IdSwayEnabled, 1f);
        }

        /// <summary>
        /// One composite pixel per 4 metres, clamped to 128..1024. Sway describes
        /// shelter, which varies over tens of metres, so a finer map would cost
        /// memory to store detail the wind field does not contain.
        /// </summary>
        private static int ChooseResolution(float worldX, float worldZ)
        {
            float extent = Mathf.Max(worldX, worldZ);
            int res = Mathf.NextPowerOfTwo(Mathf.CeilToInt(extent / 4f));
            return Mathf.Clamp(res, 128, 1024);
        }

        private void EnsureSwayTexture(int res, Vector4 window)
        {
            bool sameWindow =
                Mathf.Approximately(swayWorld.x, window.x) &&
                Mathf.Approximately(swayWorld.y, window.y) &&
                Mathf.Approximately(swayWorld.z, window.z) &&
                Mathf.Approximately(swayWorld.w, window.w);

            if (swayMap != null && swayMap.width == res && sameWindow && swayPixels != null)
                return;

            if (swayMap != null) DestroyTexture(swayMap);

            swayMap = new Texture2D(res, res, TextureFormat.RG16, false, true);
            swayMap.name = "UC_SwayMap";
            swayMap.hideFlags = HideFlags.DontSave;
            swayMap.wrapMode = TextureWrapMode.Clamp;
            swayMap.filterMode = FilterMode.Bilinear;

            swayPixels = new Color32[res * res];

            // Full amplitude, zero phase. An area no chunk has baked yet must read
            // as ordinary wind; clearing to zero would freeze it.
            Color32 unbaked = new Color32(255, 0, 0, 255);
            for (int i = 0; i < swayPixels.Length; i++) swayPixels[i] = unbaked;

            swayWorld = window;
        }

        /// <summary>Drops the sway map; consumers fall back to uniform wind.</summary>
        public void ClearSwayMap()
        {
            hasSwayMap = false;
            Shader.SetGlobalFloat(IdSwayEnabled, 0f);
        }

        /// <summary>True when a baked sway map is driving the detail shader.</summary>
        public bool HasSwayMap { get { return hasSwayMap && swayMap != null; } }

        /// <summary>
        /// Wind strength at a world position, 0..1, including shelter from the
        /// baked map when there is one. Gameplay code can use this for dust,
        /// particles, or anything that should ease off in a sheltered hollow.
        /// </summary>
        public float SampleStrength(Vector3 worldPosition)
        {
            float baseStrength = Mathf.Clamp01(CurrentSpeed / 25f);
            if (!HasSwayMap) return baseStrength;

            float u = (worldPosition.x - swayWindow.x) / swayWindow.z;
            float v = (worldPosition.z - swayWindow.y) / swayWindow.w;
            if (u < 0f || u > 1f || v < 0f || v > 1f) return baseStrength;

            int x = Mathf.Clamp((int)(u * (swayMap.width  - 1)), 0, swayMap.width  - 1);
            int y = Mathf.Clamp((int)(v * (swayMap.height - 1)), 0, swayMap.height - 1);

            return baseStrength * Mathf.Clamp01(swayMap.GetPixel(x, y).r);
        }

        private float CurrentTime()
        {
#if UNITY_EDITOR
            // Edit mode has no game clock. When animation has not been asked for,
            // the wind field resolves at a fixed phase, so the still Scene view is
            // the same still Scene view every time the project is opened rather
            // than whatever the editor's uptime happened to be at that moment.
            if (!Application.isPlaying)
                return animateInEditMode ? Time.realtimeSinceStartup : 0f;
#endif
            return Time.time;
        }

        private static void DestroyTexture(Texture2D t)
        {
            if (t == null) return;
            if (Application.isPlaying) Destroy(t);
            else DestroyImmediate(t);
        }

        private void OnDestroy()
        {
            if (swayMap != null) DestroyTexture(swayMap);
            Shader.SetGlobalFloat(IdSwayEnabled, 0f);
        }
    }
}
