// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCWaterVolume.cs
//  Purpose: A body of water as the physics layer sees it. One per ocean, lake or
//           river reach. Answers "is this point underwater, how deep, and which
//           way is the water moving" without a raycast and without touching the
//           GPU.
//
//  WHY A REGISTRY AND NOT A TRIGGER COLLIDER
//  -----------------------------------------
//  Trigger colliders answer "am I inside" and nothing else. Buoyancy needs the
//  submerged fraction, which means the depth at a specific point, and it needs it
//  for a dozen points on one body every fixed step. Asking the physics engine
//  twelve times per object per step, for every floating object, is how water
//  becomes the most expensive thing in a mobile frame.
//
//  So volumes register themselves in a static list and queries are a bounds test
//  plus arithmetic. Twelve queries is twelve bounds tests, and on the phone budget
//  that is free.
//
//  IT IS A BOX WITH A FLOOR, NOT A HALF-SPACE
//  ------------------------------------------
//  A water body occupies the space between its surface and its bed. Below the bed
//  is rock. Testing only the plan footprint and the surface elevation leaves a
//  volume that reaches down forever, and the result is not subtle: a lake on a
//  mountain reports every point in the valley beneath it as submerged, so a walk
//  along the valley floor is a walk underwater. The floor is therefore part of the
//  test, at the depth the body actually holds - see SampleLocal.
//
//  RIVERS
//  ------
//  A river reach carries a flow direction and a speed, and the flow follows the
//  reach rather than pointing one way for the whole world. Anything in the water
//  is pushed by it, which is what makes a river a hazard rather than a texture.
//
//  A river surface is also not level, which one still-water elevation cannot
//  describe: a reach that falls three hundred metres from head to mouth, given one
//  level, is a wall of water over half its length and dry over the other half. So a
//  river carries its solved profile and every query is answered against the reach
//  under it - see surfaceProfile.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using UnityEngine;

namespace UniverseCreator.TerrainSystem
{
    /// <summary>What a query at a point found.</summary>
    public struct WaterSample
    {
        /// <summary>True when the point is below the surface.</summary>
        public bool submerged;

        /// <summary>Metres below the surface. Negative above it.</summary>
        public float depth;

        /// <summary>World Y of the surface directly above the point, waves included.</summary>
        public float surfaceY;

        /// <summary>Surface normal, for aligning things that float flat.</summary>
        public Vector3 normal;

        /// <summary>Water velocity at the point - current plus wave orbital motion.</summary>
        public Vector3 velocity;

        /// <summary>The volume that answered, or null when the point is dry.</summary>
        public UCWaterVolume volume;
    }

    /// <summary>
    /// A body of water. Attach to the GameObject that carries a water surface mesh;
    /// UCWaterBuilder adds one automatically to everything it generates.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Universe Creator/Water Volume")]
    public class UCWaterVolume : MonoBehaviour
    {
        public enum BodyKind { Ocean = 0, Lake = 1, River = 2, Pool = 3, Swamp = 4 }

        [Header("Body")]
        public BodyKind kind = BodyKind.Lake;

        [Tooltip("Still-water surface height in world Y. A river overrides it per " +
                 "reach from its own solved profile, because a river surface is not level.")]
        public float surfaceLevel;

        [Tooltip("How deep the body goes below its surface. This is also its FLOOR: " +
                 "below the bed is rock, not water, so a query that deep is dry.")]
        public float maxDepth = 40f;

        [Tooltip("XZ extent of the body in world space, with its lid at the water and " +
                 "reaching downward by its own depth. An ocean leaves this huge; a pool " +
                 "matches its mesh. Queries outside it in plan are dry.")]
        // Lid on the plane, box hanging under it. The old default was centred on the
        // origin - 50 m of it above the water - which is what the volume being "centred
        // or above" the surface meant on a body that never got fitted.
        public Bounds worldBounds = new Bounds(new Vector3(0f, -50f, 0f),
                                               new Vector3(1000f, 100f, 1000f));

        [Tooltip("The reach a river runs along: xz is the plan position of a node and " +
                 "y is the water surface elevation there. Empty for still water, which " +
                 "uses Surface Level for the whole body.")]
        public Vector3[] surfaceProfile;

        [Header("Surface")]
        public WaterSurfaceParams surface = WaterSurfaceParams.Default(0f);

        [Header("Flow")]
        [Tooltip("Steady current in world XZ, metres per second. A river reach sets " +
                 "this from its own downstream direction.")]
        public Vector2 flow = Vector2.zero;

        [Tooltip("Scales how hard the current pushes floating bodies. 1 is physical; " +
                 "lower it if a river is meant to be crossable.")]
        [Range(0f, 4f)] public float flowForce = 1f;

        [Header("Medium")]
        [Tooltip("Density in kg/m3. 998 is fresh water at 20C, 1025 is sea water. " +
                 "Something denser than this sinks; something less dense floats, and " +
                 "no other number decides it.")]
        public float density = 998f;

        [Tooltip("Extra viscous damping for thick water - mud, tar, a swamp. 1 is " +
                 "ordinary water.")]
        [Range(0.2f, 12f)] public float viscosityScale = 1f;

        [Header("Interaction")]
        [Tooltip("Emit ripples into the ripple simulation when something moves in " +
                 "this water. Turn off for water the player can never reach.")]
        public bool emitRipples = true;

        [Header("Seen From Inside")]
        [Tooltip("Whether a camera inside this body sees the world through water.\n\n" +
                 "Off means diving in looks exactly like walking on land, which is the " +
                 "single loudest tell that water is a flat sheet rather than a volume.")]
        public bool underwaterView = true;

        [Tooltip("The colour of the water itself, seen from inside it.\n\n" +
                 "This is not the surface colour: from above you are looking at a " +
                 "reflection and through a few metres of water, from inside you are " +
                 "IN the medium and every ray reaching your eye has been filtered by " +
                 "it. Water absorbs red first, then green, which is why the deep is " +
                 "blue-green and why anything red goes grey a few metres down.")]
        public Color underwaterColor = new Color(0.08f, 0.24f, 0.32f, 1f);

        [Tooltip("How far you can see through this water, in metres.\n\n" +
                 "Clear tropical sea is 30 or more, a lake is 8 to 15, a silty river " +
                 "is 2 to 5. This is the whole difference between diving in the sea " +
                 "and diving in a river, so it is worth setting per body.")]
        public float visibilityMetres = 14f;

        [Tooltip("How much light reaches this water, 0 to 1. Scales the underwater " +
                 "colour, so a deep body reads darker without needing a second colour " +
                 "picked by hand.")]
        [Range(0f, 2f)] public float underwaterBrightness = 1f;

        /// <summary>
        /// What each kind of water looks like from inside it. One table, two callers:
        /// the component path seeds a new body from it, and the graph path applies it
        /// to volumes it builds. Kept here rather than duplicated in both so a lake
        /// dives the same however it was made.
        ///
        /// The numbers are what these waters do in life. Absorption removes red
        /// within a few metres and green more slowly, so every case trends
        /// blue-green; what separates them is how much is suspended in the water,
        /// which sets both how far you can see and how much the green pulls ahead of
        /// the blue.
        /// </summary>
        public static void UnderwaterDefaultsFor(BodyKind kind, out Color color,
                                                 out float visibility, out float brightness)
        {
            switch (kind)
            {
                case BodyKind.Ocean:
                    // Nothing suspended, no bed within reach to stir: blue survives.
                    color = new Color(0.035f, 0.20f, 0.34f, 1f);
                    visibility = 26f;
                    brightness = 0.85f;
                    return;

                case BodyKind.River:
                    // Moving over its own bed, so always carrying some of it.
                    color = new Color(0.13f, 0.22f, 0.17f, 1f);
                    visibility = 6.5f;
                    brightness = 0.72f;
                    return;

                case BodyKind.Pool:
                    // Treated water in a pale basin - the one case that really is
                    // "bluish and almost colourless".
                    color = new Color(0.16f, 0.42f, 0.50f, 1f);
                    visibility = 32f;
                    brightness = 0.95f;
                    return;

                default:
                    // Lake. Algae and dissolved plant matter absorb the blue the sea
                    // keeps, which is why fresh water reads green.
                    color = new Color(0.075f, 0.26f, 0.24f, 1f);
                    visibility = 12f;
                    brightness = 0.78f;
                    return;
            }
        }

        // ---------------------------------------------------------------------
        //  Registry
        // ---------------------------------------------------------------------

        private static readonly List<UCWaterVolume> volumes = new List<UCWaterVolume>();

        public static IReadOnlyList<UCWaterVolume> All { get { return volumes; } }

        private void OnEnable() { if (!volumes.Contains(this)) volumes.Add(this); }
        private void OnDisable() { volumes.Remove(this); }

        // ---------------------------------------------------------------------
        //  Query
        // ---------------------------------------------------------------------

        /// <summary>
        /// Samples every registered volume and returns the one whose surface is
        /// highest above the point.
        ///
        /// Highest wins rather than first-found, because a river running into a
        /// lake overlaps it, and the water you are actually in is the one whose
        /// surface is above you - anything else and a boat crossing the confluence
        /// drops through the surface for one frame.
        /// </summary>
        public static WaterSample Sample(Vector3 worldPosition, float time)
        {
            WaterSample best = new WaterSample();
            best.depth = float.NegativeInfinity;
            best.normal = Vector3.up;
            best.surfaceY = float.NegativeInfinity;

            for (int i = 0; i < volumes.Count; i++)
            {
                UCWaterVolume v = volumes[i];
                if (v == null || !v.isActiveAndEnabled) continue;

                WaterSample s;
                if (!v.SampleLocal(worldPosition, time, out s)) continue;

                if (s.surfaceY > best.surfaceY) best = s;
            }

            if (best.volume == null)
            {
                best.depth = 0f;
                best.surfaceY = 0f;
                best.submerged = false;
                best.velocity = Vector3.zero;
            }

            return best;
        }

        /// <summary>Samples this volume alone. False when the point is outside it.</summary>
        public bool SampleLocal(Vector3 worldPosition, float time, out WaterSample sample)
        {
            sample = new WaterSample();

            // Cheapest possible rejection first. Everything else in this method
            // costs more than the bounds test, and most queries miss.
            //
            // XZ ONLY, on purpose. worldBounds is a plan footprint here, and its Y is
            // read by the gizmo and by nothing else. Rejecting on the lid would look
            // tidier and would break flotation: buoyancy asks about hull points ABOVE
            // the waterline and needs the surface elevation handed back for them, so a
            // point above the lid has to be answered, not dropped. The floor test below
            // is the only vertical rejection, and it is measured against the LOCAL
            // surface rather than the box.
            if (worldPosition.x < worldBounds.min.x || worldPosition.x > worldBounds.max.x ||
                worldPosition.z < worldBounds.min.z || worldPosition.z > worldBounds.max.z)
                return false;

            WaterSurfaceParams p = surface;
            p.level = surfaceLevel;
            p.flow = flow;

            // A river answers from the reach the query is under; still water answers
            // from its one level. Both then go through the same wave and depth
            // arithmetic, so a boat cannot tell which kind of body it is floating on.
            if (surfaceProfile != null && surfaceProfile.Length >= 2)
                ReachAt(worldPosition, ref p);

            // The floor. Below the bed is rock, and rock is dry however far inside the
            // plan footprint it is - which is what stops a mountain lake from reporting
            // the whole valley under it as submerged. Expressed against the LOCAL
            // surface so it holds for a river too, where the bed at the head is
            // hundreds of metres above the bed at the mouth and one box floor for the
            // whole reach would mean nothing.
            //
            // maxDepth is the body's DEEPEST point, and it is now the water's own depth
            // rather than the mesh skirt's bound. It used to inherit
            // ShorelineConformer.MaxSkirtDepthMetres, which is the right limit for how
            // far a skirt is worth drawing and the wrong one for how deep the sea is:
            // every point more than that far down answered DRY, so diving to the sea
            // floor turned the underwater view off and the world came back in air.
            // UCWaterBody reads the uncapped depth for this field. See the note at its
            // Assemble call.
            if (worldPosition.y < p.level - Mathf.Max(0.01f, maxDepth)) return false;

            float surfaceY = p.level + UCWaterSurface.WaveOffset(
                new Vector2(worldPosition.x, worldPosition.z), time, p);

            float depth = surfaceY - worldPosition.y;

            // Clamped at the boundary: a point exactly on the bed reports the body's
            // full depth and no more, so a hull resting there receives the buoyant
            // force of the water above it rather than of an imaginary column below.
            if (depth > maxDepth) depth = maxDepth;

            sample.volume = this;
            sample.submerged = depth > 0f;
            sample.depth = depth;
            sample.surfaceY = surfaceY;
            sample.normal = UCWaterSurface.SurfaceNormal(worldPosition, time, p);

            Vector3 wave = UCWaterSurface.SurfaceVelocity(worldPosition, time, p);

            // Current decays with depth. Surface water moves; water near the bed is
            // held back by friction against it, which is why a swimmer can duck
            // under a current to cross it and why debris settles in the deep parts
            // of a river bend instead of being swept on.
            float depthFalloff = sample.submerged
                ? Mathf.Exp(-Mathf.Max(0f, depth) / Mathf.Max(0.5f, maxDepth * 0.25f))
                : 1f;

            sample.velocity = new Vector3(
                wave.x * depthFalloff * flowForce,
                wave.y,
                wave.z * depthFalloff * flowForce);

            return true;
        }

        // =====================================================================
        //  THE REACH - how a river answers a query
        // =====================================================================

        /// <summary>
        /// Nodes kept in a river's profile. Bounded because every query walks all of
        /// them, and a solved course can be a thousand points long.
        /// </summary>
        /// <remarks>
        /// Sixty-four segments is a few hundred float operations - the same order as
        /// the bounds tests the caller already pays for, so the registry stays as cheap
        /// as its header claims. It is also plenty: the profile describes where the
        /// water SURFACE is, and a river surface is smooth by construction (the solve
        /// smooths it and forces it downhill), so it is the one part of a river that
        /// loses nothing to being described coarsely. The mesh keeps the full detail.
        /// </remarks>
        public const int MaxProfileNodes = 64;

        /// <summary>
        /// Gives this volume a river's surface profile, thinned to
        /// <see cref="MaxProfileNodes"/>. Pass null to make it still water again.
        /// </summary>
        /// <param name="points">The solved course in world space.</param>
        /// <param name="surfaceY">Water surface elevation at each of those points.</param>
        public void SetReachProfile(IList<Vector3> points, IList<float> surfaceY)
        {
            if (points == null || surfaceY == null ||
                points.Count < 2 || surfaceY.Count != points.Count)
            {
                surfaceProfile = null;
                return;
            }

            int n = points.Count;
            int keep = Mathf.Min(n, MaxProfileNodes);
            Vector3[] profile = new Vector3[keep];

            // Ends kept exactly: the head and the mouth are the two elevations a user
            // placed, and thinning them would move the water off the body it joins.
            for (int i = 0; i < keep; i++)
            {
                int src = keep > 1
                    ? Mathf.Clamp(Mathf.RoundToInt(i * (n - 1) / (float)(keep - 1)), 0, n - 1)
                    : 0;

                profile[i] = new Vector3(points[src].x, surfaceY[src], points[src].z);
            }

            surfaceProfile = profile;
        }

        /// <summary>
        /// Answers a query from the nearest point on the reach: the surface elevation
        /// there, and the flow pointing the way the water is going at that point.
        /// </summary>
        /// <remarks>
        /// The flow direction is taken from the segment rather than from the body's one
        /// flow vector, because a river bends. Its SPEED is the body's own, so the
        /// hydraulics that computed it stay the single authority on how fast the water
        /// moves and this only decides which way.
        /// </remarks>
        private void ReachAt(Vector3 worldPosition, ref WaterSurfaceParams p)
        {
            Vector3[] line = surfaceProfile;

            float bestSq = float.MaxValue;
            float bestY = p.level;
            float bestDirX = 0f;
            float bestDirZ = 0f;

            for (int i = 0; i < line.Length - 1; i++)
            {
                Vector3 a = line[i];
                Vector3 b = line[i + 1];

                float ex = b.x - a.x;
                float ez = b.z - a.z;
                float lenSq = ex * ex + ez * ez;

                float t = lenSq > 1e-6f
                    ? Mathf.Clamp01(((worldPosition.x - a.x) * ex +
                                     (worldPosition.z - a.z) * ez) / lenSq)
                    : 0f;

                float onX = a.x + ex * t;
                float onZ = a.z + ez * t;

                float dx = onX - worldPosition.x;
                float dz = onZ - worldPosition.z;
                float sq = dx * dx + dz * dz;

                if (sq >= bestSq) continue;

                bestSq = sq;
                bestY = Mathf.Lerp(a.y, b.y, t);

                if (lenSq > 1e-6f)
                {
                    float len = Mathf.Sqrt(lenSq);
                    bestDirX = ex / len;
                    bestDirZ = ez / len;
                }
            }

            p.level = bestY;

            float speed = flow.magnitude;
            if (speed > 1e-4f && (bestDirX != 0f || bestDirZ != 0f))
                p.flow = new Vector2(bestDirX * speed, bestDirZ * speed);
        }

        /// <summary>
        /// Turns a water SURFACE bounds into the VOLUME bounds beneath it.
        /// </summary>
        /// <remarks>
        /// A surface is flat, so its own bounds have no useful height and a query one
        /// metre under visible water falls outside them. One implementation because two
        /// callers need the same box - the renderer-driven fit and the body that hands
        /// its built bounds straight over - and two copies of it is how the gizmo ends
        /// up drawing a volume the queries do not use.
        ///
        /// The box has its LID at the water and grows DOWNWARD from there; it is never
        /// centred on the plane, which would put half of it in the air. The floor is
        /// measured from the surface's LOWEST point and not from the lid, and that
        /// difference is the whole reason this is a method and not a subtraction: an
        /// ocean's surface is one level so the two are the same, but a river's lid is
        /// its head elevation and its floor is the bed at its mouth. A river that
        /// descends 300 m and is 4 m deep would, measured from the lid, have a box
        /// ending 296 m above its own mouth - the mouth would be outside its own
        /// volume, which is every river.
        /// </remarks>
        public static Bounds ExtendDown(Bounds surfaceBounds, float depthMetres)
        {
            Bounds b = surfaceBounds;
            b.Encapsulate(new Vector3(b.center.x,
                                      b.min.y - Mathf.Max(0f, depthMetres),
                                      b.center.z));
            return b;
        }

        /// <summary>
        /// Sets the bounds from a renderer, so generated water bodies size their own
        /// query volume without anyone having to type numbers in.
        /// </summary>
        public void FitToRenderer()
        {
            Renderer r = GetComponent<Renderer>();
            if (r == null) return;

            worldBounds = ExtendDown(r.bounds, maxDepth);
        }

        private void OnDrawGizmosSelected()
        {
#if UNITY_EDITOR
            // Only when THIS object is the selection. Unity also runs this for every
            // child of whatever is selected, and a volume sits under a water body
            // under the world root - so untested, selecting the world painted a blue
            // box for every body in the scene, refreshed on every scene-view repaint.
            if (UnityEditor.Selection.activeGameObject != gameObject) return;

            // Off unless the developer asked for the debug bounds. Selecting a water
            // body to edit it should not paint a box over the water it belongs to.
            if (!UCWaterGizmos.ShowDebugBounds) return;

            Gizmos.color = new Color(0.2f, 0.6f, 0.9f, 0.35f);
            Gizmos.DrawWireCube(worldBounds.center, worldBounds.size);
#endif
        }
    }
}
