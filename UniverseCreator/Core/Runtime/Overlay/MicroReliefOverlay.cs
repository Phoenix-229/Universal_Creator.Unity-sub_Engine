// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    MicroReliefOverlay.cs
//  Purpose: The camera-following micro-relief patch ring itself: a fixed n×n pool of
//           flat patch meshes, generated ONCE, parented to a root that snaps to the
//           camera in whole-patch steps and RE-ADDRESSES which world cell each patch
//           draws (via PatchRing) instead of ever rebuilding the pool. This is where
//           the future per-process displacement (G-18) will live; for now the ring
//           exists, follows, and - the property that matters - crossing a patch
//           boundary repositions Transforms only, so it allocates zero bytes.
//
//  PLAY MODE ONLY
//  --------------
//  Nothing here runs in edit mode. The pool is built on enable only while the game is
//  playing, and the per-frame snap lives in Update (which the editor never ticks for a
//  component without ExecuteAlways). The scene view therefore never sees a moving ring
//  and the editor never pays for one.
//
//  IT NEVER TOUCHES TerrainData
//  ----------------------------
//  The ring is a separate GameObject hierarchy of meshes floating over the native
//  terrain. It reads the camera and writes only its own Transforms and (later) its own
//  mesh vertices - never a heightmap write-back. That is a view-only overlay by
//  construction, which is the constraint its sibling self-test pins down.
//
//  THE LIMIT THIS OVERLAY CANNOT HIDE: IT HAS NO COLLIDER
//  -----------------------------------------------------
//  Displacement happens in the vertex/domain shader, on the GPU, so it is a PICTURE and
//  nothing else. The physics surface a character walks on is the native TerrainCollider,
//  which follows TerrainData - the un-displaced heightfield. Left unaddressed that is a
//  real bug: a player's feet land in the air over a shader mound and sink into the ground
//  inside a shader pit, because the collider never saw the relief the eye sees. A collider
//  mesh that tracked the displaced surface is not affordable on a phone (it would re-cook a
//  heightfield every time the ring re-addresses), so this overlay does NOT ship one, and
//  says so - here, and in the inspector.
//
//  The honest fix is not a collider but an analytic height query: a cheap CPU function
//  (MicroReliefField.SampleOffset) that returns the very millimetres of displacement the
//  shader applies, from the same code path, so footstep placement, foot IK, decal
//  projection, prop seating and camera collision can add it to the terrain height and read
//  the same surface the picture shows. That query is built as its own step; until then,
//  gameplay contact reads the native terrain, and this class header is where that is stated.
//
//  RUNTIME VIEW STATE, NEVER SAVED, RE-ADOPTED AFTER A DOMAIN RELOAD  (C-15, C-11)
//  ------------------------------------------------------------------------------
//  The ring's GameObjects and its shared mesh are pure runtime view state: they carry
//  HideFlags.DontSave, so saving the user's scene writes none of them. But a domain reload
//  (a script recompile, or entering play with reload enabled) wipes this component's managed
//  references - ringRoot, slots, built all reset - while those DontSave objects survive it.
//  Building blindly on the next enable would then stack a SECOND ring over the first, drawing
//  twice at doubled cost. So on enable we first look for the surviving root BY NAME and re-bind
//  to it (its patch children become the slot array again, one hands back the shared mesh), and
//  only build fresh when nothing was found - adopt, don't duplicate.
// -----------------------------------------------------------------------------

using UnityEngine;
using UniverseCreator.Optimization;

namespace UniverseCreator.Overlay
{
    /// <summary>
    /// Runtime driver for the micro-relief patch ring. Builds an n×n pool of flat patch meshes once,
    /// then follows the camera by snapping its root in whole-patch steps and re-addressing each slot's
    /// world cell - no allocation on the snap path, and no write to any TerrainData.
    /// </summary>
    [DisallowMultipleComponent]
    public class MicroReliefOverlay : MonoBehaviour
    {
        [Tooltip("World size of one square patch in metres. Whole-patch snapping steps by this.")]
        public float overlayPatchSizeM = 8f;

        [Tooltip("Vertices per patch edge for the once-built grid mesh (>= 2).")]
        public int overlayPatchResolution = 16;

        [Tooltip("Camera the ring follows. Falls back to Camera.main when unset.")]
        public Camera targetCamera;

        [Tooltip("Quality source whose tier picks the ring's grid side (5/7/9). Optional.")]
        public AdaptiveQuality quality;

        [Tooltip("Material the patch meshes draw with. Optional; a default is used when unset.")]
        public Material patchMaterial;

        [Tooltip("Force the pre-subdivided non-tessellated path even where hardware tessellation exists " +
                 "- for testing the fallback (the GLES 3.0 path) on a machine that supports tessellation.")]
        public bool forceNonTessellatedFallback = false;

        // Which geometry path the ring built, decided once at build from the GPU's capability. Read-only
        // to consumers (the dedicated geometry inspector shows these); never changes while the app runs.
        public OverlayTessPath ActiveTessPath { get; private set; }
        public string ActiveTessPathReason { get; private set; } = "not built";

        /// <summary>Name of the single ring root under this component - the handle domain-reload adoption re-binds by.</summary>
        private const string RingRootName = "MicroReliefRing";

        // Preallocated pool state - nothing here is reallocated once the ring is built.
        private Transform ringRoot;
        private Transform[] slots;
        private Mesh sharedPatchMesh;
        private int gridN;
        private int lastCentreX;
        private int lastCentreZ;
        private bool built;
        private bool hasCentre;

        private void OnEnable()
        {
            // Edit-mode safety: the ring is a play-mode-only object. In the editor we build nothing,
            // so the scene view never renders or moves it.
            if (!Application.isPlaying) return;
            if (built) return;                    // already live (defensive: a plain re-enable, no reload)
            if (TryAdoptExistingRing()) return;   // survived a domain reload - re-bind, do not build a second ring
            BuildRing();
        }

        private void OnDisable()
        {
            TearDownRing();
        }

        private void Update()
        {
            // Update never ticks in edit mode for a component without ExecuteAlways, so this is
            // inherently play-mode only; the guard is belt-and-braces.
            if (!Application.isPlaying || !built) return;
            UpdateRing();
        }

        /// <summary>Camera the ring follows: the serialized one, else the main camera.</summary>
        private Camera ResolveCamera()
        {
            return targetCamera != null ? targetCamera : Camera.main;
        }

        /// <summary>Grid side for the current tier, or Desktop's 9 when no quality source is present.</summary>
        private int ResolveGridSide()
        {
            var tier = quality != null ? quality.CurrentTier : AdaptiveQuality.QualityTier.High;
            return PatchRing.GridSideForTier(tier);
        }

        /// <summary>
        /// Decides HardwareTessellation vs PreSubdividedFallback once and records the reason. The force
        /// toggle wins so the fallback can be exercised on a machine that supports tessellation; otherwise
        /// the GPU's shader level and the overlay shader's own support flag decide (C-13).
        /// </summary>
        private void ResolveTessPath()
        {
            bool shaderSupported = OverlayShaderSupported();
            if (forceNonTessellatedFallback)
            {
                ActiveTessPath = OverlayTessPath.PreSubdividedFallback;
                ActiveTessPathReason = "Pre-subdivided fallback: forced on for testing (forceNonTessellatedFallback).";
                return;
            }
            ActiveTessPath = OverlayTessellationSupport.DecidePath(
                SystemInfo.graphicsDeviceType, SystemInfo.graphicsShaderLevel, shaderSupported);
            ActiveTessPathReason = OverlayTessellationSupport.DescribePath(
                SystemInfo.graphicsDeviceType, SystemInfo.graphicsShaderLevel, shaderSupported);
        }

        /// <summary>Whether the overlay's tessellation shader compiles on this GPU - from the patch
        /// material's shader when set, else the named detail-tessellation shader if it is in the build.</summary>
        private bool OverlayShaderSupported()
        {
            if (patchMaterial != null && patchMaterial.shader != null)
                return patchMaterial.shader.isSupported;
            Shader s = Shader.Find("UniverseCreator/TerrainDetailTess");
            return s != null && s.isSupported;
        }

        /// <summary>Builds the fixed pool once: one shared flat mesh, n×n patch objects under a root.</summary>
        private void BuildRing()
        {
            if (built) return;

            gridN = ResolveGridSide();
            float size = Mathf.Max(0.01f, overlayPatchSizeM);

            // Decide the geometry path once, here. The hardware path takes a coarse base mesh and lets
            // tessellation add detail per distance; the fallback cannot tessellate, so it draws a denser
            // FIXED mesh through the shader's non-tessellated sub-shader. Either way the ring reports why.
            ResolveTessPath();
            int res = ActiveTessPath == OverlayTessPath.HardwareTessellation
                ? Mathf.Max(2, overlayPatchResolution)
                : OverlayTessellationSupport.FallbackSubdivisionResolution;

            sharedPatchMesh = BuildFlatPatchMesh(res, size);
            sharedPatchMesh.hideFlags = HideFlags.DontSave; // runtime mesh: never serialised into the user's assets

            var rootGo = new GameObject(RingRootName);
            rootGo.hideFlags = HideFlags.DontSave;          // C-15/C-11: view state, excluded from scene save
            ringRoot = rootGo.transform;
            ringRoot.SetParent(transform, false);

            slots = new Transform[gridN * gridN];
            for (int sz = 0; sz < gridN; sz++)
            {
                for (int sx = 0; sx < gridN; sx++)
                {
                    var patch = new GameObject("Patch");
                    patch.hideFlags = HideFlags.DontSave;   // belt-and-braces: the whole subtree stays out of the save
                    var mf = patch.AddComponent<MeshFilter>();
                    mf.sharedMesh = sharedPatchMesh;
                    var mr = patch.AddComponent<MeshRenderer>();
                    if (patchMaterial != null) mr.sharedMaterial = patchMaterial;
                    var t = patch.transform;
                    t.SetParent(ringRoot, false);
                    slots[sz * gridN + sx] = t;
                }
            }

            built = true;
            hasCentre = false;
            UpdateRing(); // seat the slots on the camera's opening frame
        }

        /// <summary>Destroys the pool and its shared mesh. Safe to call when nothing was built.</summary>
        private void TearDownRing()
        {
            if (ringRoot != null)
            {
                if (Application.isPlaying) Destroy(ringRoot.gameObject);
                else DestroyImmediate(ringRoot.gameObject);
                ringRoot = null;
            }
            if (sharedPatchMesh != null)
            {
                if (Application.isPlaying) Destroy(sharedPatchMesh);
                else DestroyImmediate(sharedPatchMesh);
                sharedPatchMesh = null;
            }
            slots = null;
            built = false;
            hasCentre = false;
        }

        /// <summary>
        /// C-15/C-11 reload adoption. A domain reload leaves the ring's DontSave GameObjects alive but resets
        /// this component's managed fields, so a blind rebuild would stack a second ring. This re-binds to the
        /// surviving root found BY NAME via the pure <see cref="OverlayRingAdoption"/> helper (its patch children
        /// become the slot array again, one hands back the shared mesh), then re-derives the geometry path from
        /// the GPU. Returns true when an existing ring was adopted; false when there is none to adopt (the normal
        /// first build) or a malformed leftover was discarded so a clean build can run.
        /// </summary>
        private bool TryAdoptExistingRing()
        {
            Transform existing = transform.Find(RingRootName);
            if (existing == null) return false; // nothing survived - caller builds fresh

            if (!OverlayRingAdoption.IsWholeRing(existing.childCount, out _))
            {
                // Not a whole n×n ring (a partial teardown, or foreign children): discard and build clean.
                DestroyImmediate(existing.gameObject);
                return false;
            }

            OverlayRingAdoption.TryBind(transform, RingRootName, out ringRoot, out slots, out gridN, out sharedPatchMesh);
            ResolveTessPath();     // re-derive the path so the inspector read-out is right after the reload
            built = true;
            hasCentre = false;     // force a full re-address next Update so every slot re-seats from scratch
            UpdateRing();
            return true;
        }

        /// <summary>
        /// The snap path. Reads the camera, snaps the ring centre to whole-patch cells, and - only
        /// when that centre actually moves - re-addresses every slot to the world cell its residue
        /// owns. Every write here is a struct into a preallocated Transform array, so a boundary
        /// crossing allocates zero bytes (G-16 done-when).
        /// </summary>
        private void UpdateRing()
        {
            Camera cam = ResolveCamera();
            if (cam == null) return;

            Vector3 p = cam.transform.position;
            int cx = PatchRing.CentreCell(p.x, overlayPatchSizeM);
            int cz = PatchRing.CentreCell(p.z, overlayPatchSizeM);

            // Inside the current patch the centre is unchanged: no work, not one Transform write.
            if (hasCentre && cx == lastCentreX && cz == lastCentreZ) return;

            for (int sz = 0; sz < gridN; sz++)
            {
                for (int sx = 0; sx < gridN; sx++)
                {
                    int cellX = PatchRing.WorldCell(sx, cx, gridN);
                    int cellZ = PatchRing.WorldCell(sz, cz, gridN);
                    Transform t = slots[sz * gridN + sx];
                    Vector3 cur = t.position;
                    t.position = new Vector3(
                        PatchRing.CellCentreM(cellX, overlayPatchSizeM),
                        cur.y,
                        PatchRing.CellCentreM(cellZ, overlayPatchSizeM));
                }
            }

            lastCentreX = cx;
            lastCentreZ = cz;
            hasCentre = true;
        }

        /// <summary>
        /// Builds one flat square grid mesh spanning [-size/2, size/2] on X and Z at y=0, with
        /// <paramref name="res"/> vertices per edge. Built once and shared by every slot; the snap
        /// path never calls this, so its allocation is a one-time build cost, not a per-frame one.
        /// </summary>
        private static Mesh BuildFlatPatchMesh(int res, float size)
        {
            int vertCount = res * res;
            var positions = new Vector3[vertCount];
            var uv = new Vector2[vertCount];
            float half = size * 0.5f;
            float step = size / (res - 1);

            for (int z = 0; z < res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    int i = z * res + x;
                    positions[i] = new Vector3(-half + x * step, 0f, -half + z * step);
                    uv[i] = new Vector2((float)x / (res - 1), (float)z / (res - 1));
                }
            }

            int quads = (res - 1) * (res - 1);
            var tris = new int[quads * 6];
            int ti = 0;
            for (int z = 0; z < res - 1; z++)
            {
                for (int x = 0; x < res - 1; x++)
                {
                    int a = z * res + x, b = a + 1, c = a + res, d = c + 1;
                    tris[ti++] = a; tris[ti++] = c; tris[ti++] = b;
                    tris[ti++] = b; tris[ti++] = c; tris[ti++] = d;
                }
            }

            var mesh = new Mesh { name = "MicroReliefPatch" };
            if (vertCount > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = positions;
            mesh.uv = uv;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
