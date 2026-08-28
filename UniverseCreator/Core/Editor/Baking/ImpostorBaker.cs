// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: Living World
//
//  File:    ImpostorBaker.cs
//  Purpose: The editor driver that turns one species' highest-LOD prefab into an
//           impostor atlas, OFFLINE and without ever freezing the editor. It owns the
//           parts a plan cannot: the bake camera, the render target, the instantiated
//           source and the written asset. What it does NOT own is the order or the
//           geometry of the bake - those come from the device-free ImpostorBakePlan,
//           so the sequence of view directions and tile rects is identical on every
//           run. That is the deterministic half of the byte-identical guarantee; the
//           other half is that the render is a pure function of a fixed camera and a
//           fixed mesh, with no jitter, no time and no random seeded anywhere here.
//
//  IT NEVER BLOCKS
//  ---------------
//  A fifty-species library is thousands of tile renders. Doing them in one loop behind
//  a modal progress bar makes the editor unusable for the minutes it takes. Instead the
//  baker renders at most ImpostorBakePlan.DefaultTilesPerFrame tiles per EditorApplication
//  update tick and yields, so the editor keeps repainting and responding while the bake
//  advances a few tiles at a time. Progress is a fraction any window can read and draw in
//  its own panel - never a blocking bar. Jobs queue, so "bake all species" is just fifty
//  enqueues that drain over many frames.
//
//  IT WRITES INTO THE PROJECT, NEVER AN ABSOLUTE PATH
//  --------------------------------------------------
//  The atlas lands at ImpostorBakePlan.AtlasAssetPath - an "Assets/"-rooted, sanitised
//  path (C-04) - so a baked project is portable. The albedo atlas bakes from the source's
//  own materials, which need no new shader; the depth, normal and leaf-mask channels are
//  rendered through an optional replacement shader supplied by the caller (the UC_Impostor
//  bake pass, G-73/G-35). When no replacement shader is supplied those channels are skipped
//  rather than faked, so the baker never writes a map it did not actually render.
//
//  EDIT-MODE SAFETY
//  ----------------
//  The bake is an explicit, user-invoked editor action, not per-frame world simulation: it
//  instantiates a hidden, DontSave copy of the source, renders it on a private layer through
//  a hidden camera into an offscreen target, and destroys the copy the moment the job ends.
//  Nothing it creates persists in the open scene and nothing here runs unless a bake was asked for.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UniverseCreator.TerrainSystem.Geometry;

namespace UniverseCreator.Editor.Baking
{
    /// <summary>
    /// One queued or in-flight impostor bake: the source prefab, the atlas grid it bakes into, and the cursor
    /// tracking how far through its cells the amortised render has advanced. A plain state holder; all geometry
    /// and ordering come from <see cref="ImpostorBakePlan"/>.
    /// </summary>
    public sealed class ImpostorBakeJob
    {
        public GameObject source;         // the highest-LOD prefab to bake
        public string speciesId = "";     // names the output asset, sanitised by the plan
        public int viewsPerAxis = 8;      // atlas is viewsPerAxis² tiles
        public int tilePx = 256;          // pixels per tile
        public bool hemi = true;          // hemi-octahedron for grounded props, full for tumbling ones
        public Shader replacementShader;  // optional aux-channel bake pass (depth/normal/leaf); null = albedo only

        internal ImpostorBakeCell[] cells;
        internal int cursor;              // next cell index to render
        internal RenderTexture atlas;     // the target being filled
        internal GameObject instance;     // hidden DontSave copy of the source
        internal Bounds bounds;           // world bounds of the instance's renderers

        /// <summary>Fraction of this job's tiles rendered, in [0,1].</summary>
        public float Progress01 => cells == null ? 0f : ImpostorBakePlan.Progress01(cursor, cells.Length);
    }

    /// <summary>
    /// The editor driver that bakes impostor atlases from prefabs, offline and without blocking. Callers
    /// <see cref="Enqueue"/> one job per species; the baker renders at most
    /// <see cref="ImpostorBakePlan.DefaultTilesPerFrame"/> tiles per editor update and writes each finished atlas
    /// to the project-relative path from <see cref="ImpostorBakePlan.AtlasAssetPath"/>. Ordering and geometry come
    /// from the device-free plan, so a re-bake reproduces the identical atlas. Progress is a fraction a window
    /// draws in its own panel - never a modal bar.
    /// </summary>
    public static class ImpostorBaker
    {
        private static readonly Queue<ImpostorBakeJob> Queue = new Queue<ImpostorBakeJob>();
        private static ImpostorBakeJob _current;
        private static Camera _bakeCamera;
        private static GameObject _bakeCameraGo;
        private static bool _hooked;

        /// <summary>A private layer the bake camera alone sees, so nothing else in the scene renders into the atlas.</summary>
        private const int BakeLayer = 31;

        /// <summary>True while a bake is in flight or queued - the window's "is a bake running" gate.</summary>
        public static bool IsBaking => _current != null || Queue.Count > 0;

        /// <summary>The species id currently baking, or empty when idle.</summary>
        public static string CurrentSpecies => _current != null ? _current.speciesId : "";

        /// <summary>Progress of the current job in [0,1]; 0 when idle.</summary>
        public static float Progress01 => _current != null ? _current.Progress01 : 0f;

        /// <summary>A one-line status the debug window can show: species, tile cursor and queue depth.</summary>
        public static string StatusLine
        {
            get
            {
                if (_current == null) return Queue.Count > 0 ? "Queued: " + Queue.Count : "Idle";
                int total = _current.cells != null ? _current.cells.Length : 0;
                return _current.speciesId + "  " + _current.cursor + " / " + total + " tiles"
                       + (Queue.Count > 0 ? "  (+" + Queue.Count + " queued)" : "");
            }
        }

        /// <summary>
        /// Queues one species for baking. The bake begins on the next editor update and advances a few tiles per
        /// frame until done, then writes its atlas and moves to the next queued job. A null source or empty species
        /// id is ignored. <paramref name="replacementShader"/> may be null (albedo only) or the aux-channel pass.
        /// </summary>
        public static void Enqueue(GameObject source, string speciesId, int viewsPerAxis = 8, int tilePx = 256,
                                   bool hemi = true, Shader replacementShader = null)
        {
            if (source == null || string.IsNullOrEmpty(speciesId)) return;
            Queue.Enqueue(new ImpostorBakeJob
            {
                source = source, speciesId = speciesId, viewsPerAxis = viewsPerAxis,
                tilePx = tilePx, hemi = hemi, replacementShader = replacementShader,
            });
            Hook();
        }

        private static void Hook()
        {
            if (_hooked) return;
            EditorApplication.update += Tick;
            _hooked = true;
        }

        private static void Unhook()
        {
            if (!_hooked) return;
            EditorApplication.update -= Tick;
            _hooked = false;
        }

        // ----- The amortised loop: advance a few tiles per editor update, never blocking -----

        private static void Tick()
        {
            if (_current == null)
            {
                if (Queue.Count == 0) { Unhook(); return; }
                _current = Queue.Dequeue();
                if (!BeginJob(_current)) { EndJob(false); return; }
            }

            int budget = ImpostorBakePlan.DefaultTilesPerFrame;
            for (int i = 0; i < budget && _current.cursor < _current.cells.Length; i++)
            {
                RenderTile(_current, _current.cells[_current.cursor]);
                _current.cursor++;
            }

            if (_current.cursor >= _current.cells.Length)
                EndJob(true);
        }

        /// <summary>Builds the plan, instantiates a hidden copy of the source, measures its bounds and allocates the
        /// atlas target and the bake camera. Returns false (and the job is abandoned) if the source has no renderers
        /// or the atlas dimensions are degenerate.</summary>
        private static bool BeginJob(ImpostorBakeJob job)
        {
            job.cells = ImpostorBakePlan.BuildCells(job.viewsPerAxis, job.tilePx, job.hemi);
            int edge = ImpostorAtlasBudget.AtlasEdgePx(job.tilePx, job.viewsPerAxis);
            if (job.cells.Length == 0 || edge <= 0) return false;

            job.instance = Object.Instantiate(job.source);
            job.instance.hideFlags = HideFlags.HideAndDontSave;
            job.instance.transform.position = Vector3.zero;
            job.instance.transform.rotation = Quaternion.identity;
            SetLayerRecursive(job.instance, BakeLayer);

            if (!TryComputeBounds(job.instance, out job.bounds)) return false;

            job.atlas = new RenderTexture(edge, edge, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            {
                name = "UC_ImpostorAtlas_" + job.speciesId,
                antiAliasing = 1,
            };
            job.atlas.Create();
            EnsureBakeCamera(job);
            return true;
        }

        /// <summary>Renders one atlas cell: frames the source with an orthographic camera pointed along the cell's
        /// view direction and draws it into the cell's normalised viewport rectangle. Deterministic - fixed
        /// framing, no jitter - so the same cell renders the same pixels on every run.</summary>
        private static void RenderTile(ImpostorBakeJob job, ImpostorBakeCell cell)
        {
            float radius = job.bounds.extents.magnitude;
            if (radius <= 1e-4f) radius = 0.5f;
            float dist = radius * 2f;
            Vector3 dir = cell.viewDirection.sqrMagnitude > 1e-8f ? cell.viewDirection.normalized : Vector3.forward;
            Vector3 up = Mathf.Abs(Vector3.Dot(dir, Vector3.up)) > 0.999f ? Vector3.forward : Vector3.up;

            var cam = _bakeCamera;
            cam.transform.position = job.bounds.center - dir * dist;
            cam.transform.rotation = Quaternion.LookRotation(dir, up);
            cam.orthographic = true;
            cam.orthographicSize = radius;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = dist + radius * 2f;

            int edge = job.atlas.width;
            cam.rect = new Rect(cell.pixelRect.x / (float)edge, cell.pixelRect.y / (float)edge,
                                cell.pixelRect.width / (float)edge, cell.pixelRect.height / (float)edge);
            cam.targetTexture = job.atlas;

            if (job.replacementShader != null) cam.RenderWithShader(job.replacementShader, "");
            else cam.Render();
        }

        /// <summary>Finishes the current job: on success reads the atlas back and writes it to the project folder;
        /// then destroys the instance and the render target and advances to the next queued job. Called both on
        /// completion (<paramref name="wrote"/> true) and on an abandoned begin (false).</summary>
        private static void EndJob(bool wrote)
        {
            var job = _current;
            _current = null;
            if (job == null) return;

            try
            {
                if (wrote && job.atlas != null) WriteAtlasAsset(job);
            }
            finally
            {
                if (job.instance != null) Object.DestroyImmediate(job.instance);
                if (_bakeCamera != null) _bakeCamera.targetTexture = null;
                if (job.atlas != null) { job.atlas.Release(); Object.DestroyImmediate(job.atlas); }
                job.atlas = null;
            }

            if (Queue.Count == 0) DisposeBakeCamera();
        }

        /// <summary>Reads the filled atlas into a Texture2D and writes it as a PNG at the plan's project-relative
        /// path. PNG so identical pixels give identical bytes - the byte-identical re-bake. The AssetDatabase import
        /// makes it a usable texture; the file path is derived from the project's own data folder, never absolute.</summary>
        private static void WriteAtlasAsset(ImpostorBakeJob job)
        {
            int edge = job.atlas.width;
            var prevActive = RenderTexture.active;
            RenderTexture.active = job.atlas;
            var tex = new Texture2D(edge, edge, TextureFormat.RGBA32, false, true);
            tex.ReadPixels(new Rect(0, 0, edge, edge), 0, 0, false);
            tex.Apply(false);
            RenderTexture.active = prevActive;

            byte[] png = tex.EncodeToPNG();
            Object.DestroyImmediate(tex);

            string assetPath = ImpostorBakePlan.AtlasAssetPath(job.speciesId, "albedo");
            // dataPath ends in "/Assets"; strip the leading "Assets" from the asset path to make the full path.
            string fullPath = Application.dataPath + assetPath.Substring("Assets".Length);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            File.WriteAllBytes(fullPath, png);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
        }

        // ----- The bake camera: hidden, orthographic, sees only the bake layer -----

        private static void EnsureBakeCamera(ImpostorBakeJob job)
        {
            if (_bakeCamera == null)
            {
                _bakeCameraGo = new GameObject("UC_ImpostorBakeCamera") { hideFlags = HideFlags.HideAndDontSave };
                _bakeCamera = _bakeCameraGo.AddComponent<Camera>();
            }
            _bakeCamera.enabled = false;                     // we drive it with explicit Render() calls only
            _bakeCamera.clearFlags = CameraClearFlags.SolidColor;
            _bakeCamera.backgroundColor = new Color(0f, 0f, 0f, 0f); // transparent surround, so tiles matte cleanly
            _bakeCamera.cullingMask = 1 << BakeLayer;        // only the hidden instance renders
            _bakeCamera.allowMSAA = false;
            _bakeCamera.allowHDR = false;
        }

        private static void DisposeBakeCamera()
        {
            if (_bakeCameraGo != null) Object.DestroyImmediate(_bakeCameraGo);
            _bakeCameraGo = null;
            _bakeCamera = null;
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform) SetLayerRecursive(child.gameObject, layer);
        }

        /// <summary>World-space bounds enclosing every renderer under the instance. False when there are none.</summary>
        private static bool TryComputeBounds(GameObject go, out Bounds bounds)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            bounds = default;
            if (renderers.Length == 0) return false;
            bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return true;
        }
    }
}
