using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace UniverseCreator.Editor.Windows
{
    public class UCTopographicMapWindow : EditorWindow
    {
        // WorldMap = the real surface a satellite would see (painted material colour +
        // relief hillshade + vegetation + water), merging the old Satellite and Terrain
        // views into one physical map. Topography = the elevation heat map.
        public enum MapViewMode { WorldMap, Topography }
        private MapViewMode viewMode = MapViewMode.WorldMap;

        private Texture2D mapTexture;
        private Rect mapRect;
        private float maxTerrainHeight = 500f;
        private float minTerrainHeight = 0f;
        private float zoom = 1f;
        private Vector2 pan = Vector2.zero;

        // Real water read from the placed bodies, refreshed per GenerateMap. Each entry is a level body
        // (lake or ocean) with its surface world Y and its plan-view footprint; a point is water when it
        // lies inside the footprint and below the surface. Rivers are level-body-excluded here because a
        // thin meandering channel's bounding box is not its shape -- they read on the terrain relief.
        private struct MapWater { public float surfaceY; public float floorY; public Bounds plan; }
        private readonly List<MapWater> mapWaters = new List<MapWater>();

        // Real rivers, read from the placed spline bodies. A river's bounding box is not its
        // shape, so it cannot go in mapWaters (which tests a footprint); instead its world
        // course and half-width are kept and stamped as a line onto the water mask, so the
        // map shows the river where the channel actually runs. Width tapers along the course
        // the same way the channel does, so a river reads narrow at its head and wide at its
        // mouth on the map exactly as on the ground.
        private struct MapRiver { public List<Vector3> course; public float halfWidth; }
        private readonly List<MapRiver> mapRivers = new List<MapRiver>();

        // Real vegetation, read from the terrains' own tree instances and detail (grass)
        // density - not invented from height or slope. Tree world positions are stamped as
        // canopy, and a per-terrain detail-density grid gives ground cover, so the map shows
        // green where the tool actually planted it.
        private struct MapTrees { public List<Vector2> xz; }
        private readonly List<MapTrees> mapTrees = new List<MapTrees>();
        private struct MapDetail
        {
            public Terrain terrain; public int[] density; public int res; // summed over all layers
        }
        private readonly List<MapDetail> mapDetails = new List<MapDetail>();

        // Opened via the World (UniverseCreatorInspector) rather than the top menu
        public static void ShowWindow()
        {
            var window = GetWindow<UCTopographicMapWindow>("Topographic Map");
            window.minSize = new Vector2(400, 400);
            window.GenerateMap();
            window.Show();
        }

        private void OnEnable()
        {
            GenerateMap();
        }

        private void OnGUI()
        {
            GUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("Refresh Map", EditorStyles.toolbarButton))
            {
                GenerateMap();
            }

            EditorGUI.BeginChangeCheck();
            viewMode = (MapViewMode)EditorGUILayout.EnumPopup(viewMode, EditorStyles.toolbarDropDown, GUILayout.Width(120));
            if (EditorGUI.EndChangeCheck())
            {
                GenerateMap();
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label($"Height Range: {minTerrainHeight:F0}m - {maxTerrainHeight:F0}m");
            GUILayout.EndHorizontal();

            if (mapTexture == null)
            {
                EditorGUILayout.HelpBox("No terrain found to generate map.", MessageType.Warning);
                return;
            }

            // Draw the map maintaining aspect ratio
            float aspect = (float)mapTexture.width / mapTexture.height;
            float windowAspect = position.width / (position.height - EditorGUIUtility.singleLineHeight);
            
            float drawWidth = position.width;
            float drawHeight = position.width / aspect;

            if (drawHeight > position.height - EditorGUIUtility.singleLineHeight)
            {
                drawHeight = position.height - EditorGUIUtility.singleLineHeight;
                drawWidth = drawHeight * aspect;
            }

            mapRect = new Rect((position.width - drawWidth) / 2f, EditorGUIUtility.singleLineHeight + (position.height - EditorGUIUtility.singleLineHeight - drawHeight) / 2f, drawWidth, drawHeight);
            
            GUI.DrawTexture(mapRect, mapTexture, ScaleMode.ScaleToFit);

            GUI.DrawTexture(mapRect, mapTexture, ScaleMode.ScaleToFit);

            if (zoom >= 2f && Event.current.type == EventType.Repaint)
            {
                DrawContourLabels();
            }

            HandleInput();
        }

        private void DrawContourLabels()
        {
            Terrain[] terrains = Terrain.activeTerrains;
            if (terrains == null || terrains.Length == 0) return;

            Bounds bounds = GetTerrainBounds(terrains);
            float viewWidth = bounds.size.x / zoom;
            float viewDepth = bounds.size.z / zoom;
            float viewMinX = bounds.center.x + pan.x - viewWidth / 2f;
            float viewMinZ = bounds.center.z + pan.y - viewDepth / 2f;
            float viewMaxX = viewMinX + viewWidth;
            float viewMaxZ = viewMinZ + viewDepth;

            GUIStyle labelStyle = new GUIStyle(EditorStyles.miniLabel);
            labelStyle.normal.textColor = Color.white;
            labelStyle.alignment = TextAnchor.MiddleCenter;

            GUIStyle shadowStyle = new GUIStyle(labelStyle);
            shadowStyle.normal.textColor = new Color(0, 0, 0, 0.8f);

            int gridSteps = 6;
            for (int i = 1; i < gridSteps; i++)
            {
                for (int j = 1; j < gridSteps; j++)
                {
                    float u = (float)i / gridSteps;
                    float v = (float)j / gridSteps;
                    
                    float worldX = Mathf.Lerp(viewMinX, viewMaxX, u);
                    float worldZ = Mathf.Lerp(viewMinZ, viewMaxZ, v);
                    
                    float height = 0f;
                    foreach (var t in terrains)
                    {
                        // A terrain whose data asset is momentarily null (mid-rebuild, or a chunk being
                        // streamed) must not throw here: OnGUI runs constantly and one throw becomes the
                        // "there is a problem" popup the author saw. Skip it and read the next terrain.
                        if (t == null || t.terrainData == null) { continue; }
                        Vector3 local = t.transform.InverseTransformPoint(new Vector3(worldX, 0, worldZ));
                        if (local.x >= 0 && local.x <= t.terrainData.size.x && local.z >= 0 && local.z <= t.terrainData.size.z)
                        {
                            height = t.SampleHeight(new Vector3(worldX, 0, worldZ)) + t.transform.position.y;
                            break;
                        }
                    }
                    
                    // Contour label step. 4 m, not 50 m (author request 2026-10-09): a 50 m
                    // bucket is so coarse that a plain and the hill above it read the same
                    // number, so a lift of a few metres is invisible on the map and the
                    // author cannot confirm the sea-level fixes actually moved the ground.
                    // 4 m resolves the difference while staying readable. Editor display
                    // only - it changes no terrain, just how finely the readout is quantised.
                    float contourHeight = Mathf.Round(height / 4f) * 4f;
                    
                    float screenX = mapRect.x + mapRect.width * u;
                    float screenY = mapRect.y + mapRect.height * (1f - v); 
                    
                    Rect labelRect = new Rect(screenX - 25, screenY - 10, 50, 20);
                    
                    GUI.Label(new Rect(labelRect.x + 1, labelRect.y + 1, labelRect.width, labelRect.height), contourHeight.ToString("F0") + "m", shadowStyle);
                    GUI.Label(labelRect, contourHeight.ToString("F0") + "m", labelStyle);
                }
            }
        }

        private void HandleInput()
        {
            Event e = Event.current;

            if (e.type == EventType.ScrollWheel)
            {
                if (mapRect.Contains(e.mousePosition))
                {
                    float oldZoom = zoom;
                    zoom -= e.delta.y * 0.1f * zoom;
                    zoom = Mathf.Clamp(zoom, 1f, 50f);
                    
                    if (zoom != oldZoom)
                    {
                        ClampPan();
                        GenerateMap();
                        Repaint();
                    }
                    e.Use();
                }
            }
            else if (e.type == EventType.MouseDrag && (e.button == 2 || (e.button == 0 && e.alt)))
            {
                Terrain[] terrains = Terrain.activeTerrains;
                if (terrains != null && terrains.Length > 0)
                {
                    Bounds bounds = GetTerrainBounds(terrains);
                    float worldViewWidth = bounds.size.x / zoom;
                    float worldViewHeight = bounds.size.z / zoom;
                    
                    pan.x -= (e.delta.x / mapRect.width) * worldViewWidth;
                    pan.y += (e.delta.y / mapRect.height) * worldViewHeight; // Y is inverted on screen vs Z
                    
                    ClampPan();
                    GenerateMap();
                    Repaint();
                }
                e.Use();
            }
            else if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                if (mapRect.Contains(e.mousePosition))
                {
                    float u = (e.mousePosition.x - mapRect.x) / mapRect.width;
                    float v = 1f - ((e.mousePosition.y - mapRect.y) / mapRect.height);

                    JumpToPosition(u, v);
                    e.Use();
                }
            }
        }

        private void ClampPan()
        {
            Terrain[] terrains = Terrain.activeTerrains;
            if (terrains == null || terrains.Length == 0) return;
            Bounds bounds = GetTerrainBounds(terrains);
            float maxXPan = Mathf.Max(0, (bounds.size.x - bounds.size.x / zoom) / 2f);
            float maxZPan = Mathf.Max(0, (bounds.size.z - bounds.size.z / zoom) / 2f);
            pan.x = Mathf.Clamp(pan.x, -maxXPan, maxXPan);
            pan.y = Mathf.Clamp(pan.y, -maxZPan, maxZPan);
        }

        private void JumpToPosition(float u, float v)
        {
            Terrain[] terrains = Terrain.activeTerrains;
            if (terrains == null || terrains.Length == 0) return;

            Bounds bounds = GetTerrainBounds(terrains);
            float viewWidth = bounds.size.x / zoom;
            float viewDepth = bounds.size.z / zoom;
            float viewMinX = bounds.center.x + pan.x - viewWidth / 2f;
            float viewMinZ = bounds.center.z + pan.y - viewDepth / 2f;
            
            float worldX = Mathf.Lerp(viewMinX, viewMinX + viewWidth, u);
            float worldZ = Mathf.Lerp(viewMinZ, viewMinZ + viewDepth, v);

            // Find height at this point
            float worldY = 0f;
            foreach (var t in terrains)
            {
                if (t == null || t.terrainData == null) { continue; }
                Vector3 local = t.transform.InverseTransformPoint(new Vector3(worldX, 0, worldZ));
                if (local.x >= 0 && local.x <= t.terrainData.size.x && local.z >= 0 && local.z <= t.terrainData.size.z)
                {
                    worldY = t.SampleHeight(new Vector3(worldX, 0, worldZ)) + t.transform.position.y;
                    break;
                }
            }

            if (SceneView.lastActiveSceneView != null)
            {
                Vector3 focusPoint = new Vector3(worldX, worldY, worldZ);
                SceneView.lastActiveSceneView.LookAt(focusPoint, SceneView.lastActiveSceneView.rotation, 50f);
            }
        }

        private Bounds GetTerrainBounds(Terrain[] terrains)
        {
            // Skip any terrain whose data asset is momentarily null so a mid-rebuild chunk cannot throw
            // out of a bounds build called from OnGUI.
            Bounds bounds = new Bounds();
            bool started = false;
            foreach (var t in terrains)
            {
                if (t == null || t.terrainData == null) { continue; }
                Bounds b = new Bounds(t.transform.position + t.terrainData.size / 2f, t.terrainData.size);
                if (!started) { bounds = b; started = true; }
                else { bounds.Encapsulate(b); }
            }
            return bounds;
        }

        private void GenerateMap()
        {
            Terrain[] terrains = Terrain.activeTerrains;
            if (terrains == null || terrains.Length == 0) return;

            Bounds bounds = GetTerrainBounds(terrains);

            // WORLD MAP = the true satellite look (§11): an orthographic top-down camera
            // rendered to a texture, so it shows the ACTUAL scene - terrain textures,
            // vegetation, water, everything a camera sees - not a CPU reconstruction that
            // guessed a colour per pixel from the splat layer (which read flat brown/green,
            // the reported fault). This is literally the Unity scene top-view the author
            // asked for. Topography stays the CPU elevation heat map below, so the height
            // numbers remain a separate, readable view.
            if (viewMode == MapViewMode.WorldMap)
            {
                RenderWorldMapCamera(terrains, bounds);
                return;
            }

            // Real water, read from the water bodies the tool actually placed rather than guessed from a
            // percentage of the height range. The ocean gives a true sea level; each lake gives a surface
            // level over its own footprint. A point is water when the ground there lies below the surface
            // of a body that covers it. This is what lets the map tell water from land honestly.
            GatherWater();

            minTerrainHeight = bounds.min.y;
            maxTerrainHeight = bounds.max.y + 500f; // Roughly estimate max height since Bounds Y doesn't account for SampleHeight max

            // Recalculate true max height by sampling
            float trueMax = 0f;
            float trueMin = float.MaxValue;

            int mapWidth = 512;
            int mapHeight = Mathf.RoundToInt(512 * (bounds.size.z / bounds.size.x));

            if (mapTexture != null) DestroyImmediate(mapTexture);
            mapTexture = new Texture2D(mapWidth, mapHeight, TextureFormat.RGB24, false);

            Color[] pixels = new Color[mapWidth * mapHeight];
            float[] heights = new float[mapWidth * mapHeight];
            // Per pixel: is this point under a real water body, and how deep (0..1) for the water shading.
            bool[] waterMask = new bool[mapWidth * mapHeight];
            float[] waterDepth01 = new float[mapWidth * mapHeight];
            // Per pixel vegetation strength (0..1), read from the terrain's real detail density
            // and trees. Trees are stamped after the sampling loop; detail is sampled here.
            float[] vegetation01 = new float[mapWidth * mapHeight];

            float viewWidth = bounds.size.x / zoom;
            float viewDepth = bounds.size.z / zoom;
            float viewMinX = bounds.center.x + pan.x - viewWidth / 2f;
            float viewMinZ = bounds.center.z + pan.y - viewDepth / 2f;
            float viewMaxX = viewMinX + viewWidth;
            float viewMaxZ = viewMinZ + viewDepth;

            for (int y = 0; y < mapHeight; y++)
            {
                for (int x = 0; x < mapWidth; x++)
                {
                    float u = (float)x / mapWidth;
                    float v = (float)y / mapHeight;

                    float worldX = Mathf.Lerp(viewMinX, viewMaxX, u);
                    float worldZ = Mathf.Lerp(viewMinZ, viewMaxZ, v);

                    float height = bounds.min.y;
                    bool hit = false;
                    foreach (var t in terrains)
                    {
                        if (t == null || t.terrainData == null) { continue; }
                        Vector3 local = t.transform.InverseTransformPoint(new Vector3(worldX, 0, worldZ));
                        if (local.x >= 0 && local.x <= t.terrainData.size.x && local.z >= 0 && local.z <= t.terrainData.size.z)
                        {
                            height = t.SampleHeight(new Vector3(worldX, 0, worldZ)) + t.transform.position.y;
                            hit = true;
                            break;
                        }
                    }

                    if (hit)
                    {
                        if (height > trueMax) trueMax = height;
                        if (height < trueMin) trueMin = height;
                        heights[y * mapWidth + x] = height;
                        int wi = y * mapWidth + x;
                        float surf, floor;
                        if (WaterAt(worldX, worldZ, height, out surf, out floor))
                        {
                            waterMask[wi] = true;
                            float span = Mathf.Max(0.5f, surf - floor);
                            waterDepth01[wi] = Mathf.Clamp01((surf - height) / span);
                        }

                        // Real ground-cover vegetation, sampled from the terrain's detail grid.
                        vegetation01[wi] = VegetationAt(worldX, worldZ);
                    }
                    else
                    {
                        heights[y * mapWidth + x] = float.MinValue;
                    }
                }
            }

            maxTerrainHeight = trueMax;
            minTerrainHeight = trueMin;

            // Stamp the real rivers as water lines onto the mask. Done after sampling so a
            // river reads as water in every view mode, over whatever ground it crosses, at
            // the width the channel actually has. Pixels-per-metre converts the world
            // half-width to a stamp radius, floored at one pixel so the thinnest brook still
            // draws a continuous line rather than a dotted one.
            float pxPerMetreX = mapWidth / Mathf.Max(0.0001f, viewWidth);
            float pxPerMetreZ = mapHeight / Mathf.Max(0.0001f, viewDepth);
            float pxPerMetre = Mathf.Min(pxPerMetreX, pxPerMetreZ);
            for (int r = 0; r < mapRivers.Count; r++)
            {
                MapRiver river = mapRivers[r];
                int radius = Mathf.Max(1, Mathf.RoundToInt(river.halfWidth * pxPerMetre));
                StampRiver(river.course, radius, viewMinX, viewMinZ, viewMaxX, viewMaxZ,
                           mapWidth, mapHeight, waterMask, waterDepth01);
            }

            // Stamp the real trees as canopy onto the vegetation strength. A tree covers a
            // small radius; overlapping canopies saturate to full green, which is how a
            // wood reads on a map versus a scattered stand.
            int treeRadius = Mathf.Max(1, Mathf.RoundToInt(3f * pxPerMetre)); // ~3 m canopy
            for (int g = 0; g < mapTrees.Count; g++)
            {
                List<Vector2> xz = mapTrees[g].xz;
                for (int k = 0; k < xz.Count; k++)
                    StampTree(xz[k], treeRadius, viewMinX, viewMinZ, viewMaxX, viewMaxZ,
                              mapWidth, mapHeight, vegetation01);
            }

            for (int y = 0; y < mapHeight; y++)
            {
                for (int x = 0; x < mapWidth; x++)
                {
                    int i = y * mapWidth + x;
                    if (heights[i] == float.MinValue)
                    {
                        pixels[i] = Color.black;
                    }
                    else
                    {
                        Color c = Color.black;

                        // Real water first: deep water dark blue, shallows lighter, so a
                        // lake, a river and the sea read as water and not as low ground.
                        if (waterMask[i])
                        {
                            c = Color.Lerp(new Color(0.30f, 0.55f, 0.72f), new Color(0.05f, 0.16f, 0.34f),
                                waterDepth01[i]);
                        }
                        else
                        {
                            c = GetHeatMapColor(heights[i], trueMin, trueMax);
                        }

                        // Real vegetation over the land (never over water): a light green
                        // touch by ground-cover strength. Topography is about elevation, so
                        // the tint stays subtle - just enough to show where the tool planted
                        // vegetation without hiding the height ramp.
                        if (!waterMask[i] && vegetation01[i] > 0f)
                        {
                            Color green = new Color(0.25f, 0.45f, 0.20f);
                            c = Color.Lerp(c, green, vegetation01[i] * 0.45f);
                        }

                        // Contour lines every 50 m.
                        if (x > 0 && y > 0)
                        {
                            float hLeft = heights[y * mapWidth + (x - 1)];
                            float hDown = heights[(y - 1) * mapWidth + x];
                            
                            if (hLeft != float.MinValue && hDown != float.MinValue)
                            {
                                int level = Mathf.FloorToInt(heights[i] / 50f);
                                int levelLeft = Mathf.FloorToInt(hLeft / 50f);
                                int levelDown = Mathf.FloorToInt(hDown / 50f);
                                
                                if (level != levelLeft || level != levelDown)
                                {
                                    c = Color.Lerp(c, new Color(0, 0, 0, 0.4f), 0.6f); // Darken for contour line
                                }
                            }
                        }
                        pixels[i] = c;
                    }
                }
            }

            mapTexture.SetPixels(pixels);
            mapTexture.Apply();
        }

        // Renders the world from a top-down orthographic camera into a texture - the true
        // satellite view (§11). The camera sees exactly what the Scene view sees from
        // straight above: painted terrain, trees, grass, water surfaces. Zoom and pan are
        // honoured through the ortho size and the camera XZ, so the same navigation the CPU
        // views use works here. The camera is temporary - created, rendered once, destroyed
        // - so nothing is left in the scene.
        private void RenderWorldMapCamera(Terrain[] terrains, Bounds bounds)
        {
            // The visible world rectangle after zoom/pan, matching the CPU views exactly so
            // switching view mode keeps the same framing.
            float viewWidth = bounds.size.x / zoom;
            float viewDepth = bounds.size.z / zoom;
            float centreX = bounds.center.x + pan.x;
            float centreZ = bounds.center.z + pan.y;

            // Texture keeps the world's aspect, same as the CPU path.
            int mapWidth = 1024;
            int mapHeight = Mathf.Max(1, Mathf.RoundToInt(1024 * (viewDepth / Mathf.Max(0.0001f, viewWidth))));

            // Height range for the on-screen label. A coarse sample is enough - the label is
            // informational and the camera, not this, draws the map.
            SampleHeightRange(terrains, bounds, out minTerrainHeight, out maxTerrainHeight);

            // A camera high enough to clear the tallest peak, looking straight down. Far
            // plane reaches past the lowest ground. Orthographic, so there is no perspective
            // - every metre of ground maps to the same number of pixels, which is what makes
            // it a map and not a photo taken from altitude.
            float top = bounds.max.y + 1000f;
            float depthSpan = (bounds.max.y - bounds.min.y) + 2000f;

            GameObject camGo = EditorUtility.CreateGameObjectWithHideFlags(
                "UC Map Camera", HideFlags.HideAndDontSave, typeof(Camera));
            Camera cam = camGo.GetComponent<Camera>();

            // NOT RenderTexture.GetTemporary.
            //
            // The temporary pool is aged out by a check that Unity only registers from
            // inside a render frame. Taking a temporary from editor code - an OnGUI, an
            // EditorApplication.update - takes it from outside any frame, and the age
            // check then runs having never been registered: "Checking lifetime of
            // RenderTextures but m_AgeCheckAdded = false", logged once per allocation and
            // so climbing every time the map is redrawn. Releasing it correctly, which
            // the finally below always did, does not prevent the message; the pool is the
            // wrong tool in an editor context, not the usage.
            //
            // A texture we own outright has no pool and no age check. It is created here,
            // released in the finally, and cannot contribute to that warning at all.
            RenderTexture rt = new RenderTexture(mapWidth, mapHeight, 24, RenderTextureFormat.ARGB32);
            rt.name = "UC Topographic Map";
            rt.hideFlags = HideFlags.HideAndDontSave;
            rt.Create();

            RenderTexture prevActive = RenderTexture.active;

            try
            {
                cam.transform.position = new Vector3(centreX, top, centreZ);
                cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);   // look straight down, +Z up on screen
                cam.orthographic = true;
                cam.orthographicSize = viewDepth * 0.5f;                  // half-height in world metres
                cam.aspect = viewWidth / Mathf.Max(0.0001f, viewDepth);
                cam.nearClipPlane = 0.3f;
                cam.farClipPlane = depthSpan;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.02f, 0.05f, 0.1f);      // deep-water blue for off-world
                cam.cullingMask = ~0;                                     // everything the scene draws
                cam.enabled = false;                                      // we drive it by hand

                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                if (mapTexture == null || mapTexture.width != mapWidth || mapTexture.height != mapHeight)
                {
                    if (mapTexture != null) DestroyImmediate(mapTexture);
                    mapTexture = new Texture2D(mapWidth, mapHeight, TextureFormat.RGB24, false);
                }
                mapTexture.ReadPixels(new Rect(0, 0, mapWidth, mapHeight), 0, 0);
                mapTexture.Apply();
            }
            finally
            {
                RenderTexture.active = prevActive;
                cam.targetTexture = null;
                if (rt != null) { rt.Release(); DestroyImmediate(rt); }
                DestroyImmediate(camGo);
            }
        }

        // A coarse min/max terrain height over the visible bounds, for the height-range
        // label. Sampled on a small grid - the number only has to be about right.
        private void SampleHeightRange(Terrain[] terrains, Bounds bounds, out float lo, out float hi)
        {
            lo = float.MaxValue; hi = float.MinValue;
            const int N = 48;
            for (int j = 0; j <= N; j++)
            {
                for (int i = 0; i <= N; i++)
                {
                    float wx = Mathf.Lerp(bounds.min.x, bounds.max.x, i / (float)N);
                    float wz = Mathf.Lerp(bounds.min.z, bounds.max.z, j / (float)N);
                    foreach (var t in terrains)
                    {
                        if (t == null || t.terrainData == null) continue;
                        Vector3 local = t.transform.InverseTransformPoint(new Vector3(wx, 0, wz));
                        if (local.x >= 0 && local.x <= t.terrainData.size.x && local.z >= 0 && local.z <= t.terrainData.size.z)
                        {
                            float h = t.SampleHeight(new Vector3(wx, 0, wz)) + t.transform.position.y;
                            if (h < lo) lo = h;
                            if (h > hi) hi = h;
                            break;
                        }
                    }
                }
            }
            if (lo > hi) { lo = bounds.min.y; hi = bounds.max.y; }
        }

        // Read every placed level water body once per map build. Lists are cleared here too, so a map
        // refresh after the world changed re-reads the real bodies rather than a stale copy.
        private void GatherWater()
        {
            mapWaters.Clear();

            mapRivers.Clear();
            mapTrees.Clear();
            mapDetails.Clear();

            var bodies = UnityEngine.Object.FindObjectsByType<UniverseCreator.TerrainSystem.UCWaterBody>(
                FindObjectsInactive.Exclude);
            if (bodies != null)
            {
                foreach (var b in bodies)
                {
                    if (b == null) { continue; }

                    // Only level bodies (lakes, ocean, pools) draw on the map. Rivers no
                    // longer contribute here: the river teardown removed the solved course
                    // (PreviewCourseWorld) and the per-station width the map used to stamp
                    // them. mapRivers stays empty until the rebuilt river system fills it.
                    if (!b.IsLevelBody) { continue; }

                    Bounds plan;
                    if (!b.TryGetPlanBounds(out plan)) { continue; }
                    float surf = b.SurfaceWorldY();
                    mapWaters.Add(new MapWater { surfaceY = surf, floorY = plan.min.y, plan = plan });
                }
            }

            GatherVegetation();
        }

        // Reads the real vegetation off every active terrain: the tree instances the tool
        // planted, and the summed detail (grass) density grid. Nothing is guessed from the
        // shape of the land - a pixel is vegetated only where the terrain actually carries
        // a tree or a detail cell.
        private void GatherVegetation()
        {
            Terrain[] terrains = Terrain.activeTerrains;
            if (terrains == null) { return; }

            foreach (Terrain t in terrains)
            {
                if (t == null || t.terrainData == null) { continue; }
                TerrainData td = t.terrainData;
                Vector3 pos = t.transform.position;
                Vector3 size = td.size;

                // Trees. normalizedPosition is 0..1 over the terrain; lift to world XZ.
                TreeInstance[] trees = td.treeInstances;
                if (trees != null && trees.Length > 0)
                {
                    var xz = new List<Vector2>(trees.Length);
                    for (int i = 0; i < trees.Length; i++)
                    {
                        Vector3 np = trees[i].position;
                        xz.Add(new Vector2(pos.x + np.x * size.x, pos.z + np.z * size.z));
                    }
                    mapTrees.Add(new MapTrees { xz = xz });
                }

                // Detail (grass) density, summed over every detail layer into one grid, so a
                // cell reads vegetated if any grass type grows there. Read once per terrain.
                int res = td.detailResolution;
                int layers = td.detailPrototypes != null ? td.detailPrototypes.Length : 0;
                if (res > 0 && layers > 0)
                {
                    int[] sum = new int[res * res];
                    for (int l = 0; l < layers; l++)
                    {
                        int[,] layer = td.GetDetailLayer(0, 0, res, res, l);
                        for (int dz = 0; dz < res; dz++)
                            for (int dx = 0; dx < res; dx++)
                                sum[dz * res + dx] += layer[dz, dx];
                    }
                    mapDetails.Add(new MapDetail { terrain = t, density = sum, res = res });
                }
            }
        }

        // Is this world point vegetated, and how strongly (0..1)? Detail density gives the
        // ground-cover strength; trees are added by the stamp pass. Returns 0 off-vegetation.
        private float VegetationAt(float worldX, float worldZ)
        {
            for (int i = 0; i < mapDetails.Count; i++)
            {
                MapDetail d = mapDetails[i];
                if (d.terrain == null || d.terrain.terrainData == null) { continue; }
                Vector3 pos = d.terrain.transform.position;
                Vector3 size = d.terrain.terrainData.size;

                float u = (worldX - pos.x) / Mathf.Max(0.0001f, size.x);
                float v = (worldZ - pos.z) / Mathf.Max(0.0001f, size.z);
                if (u < 0f || u > 1f || v < 0f || v > 1f) { continue; }

                int cx = Mathf.Clamp(Mathf.FloorToInt(u * d.res), 0, d.res - 1);
                int cz = Mathf.Clamp(Mathf.FloorToInt(v * d.res), 0, d.res - 1);
                int dens = d.density[cz * d.res + cx];
                if (dens <= 0) { return 0f; }

                // Detail density is a small integer count per cell; a handful already reads as
                // full ground cover on a map, so it saturates quickly rather than linearly.
                return Mathf.Clamp01(dens / 4f);
            }
            return 0f;
        }

        // Rasterises one river course onto the water mask: walks each segment, converts the
        // endpoints to pixels, and stamps a filled disc of the given radius along the line.
        // The disc gives the channel its width; walking the whole segment (not just its ends)
        // keeps the line continuous however far apart the course points are. Depth is set to
        // a mid value so the river reads as clearly wet without pretending to be deep.
        private static void StampRiver(List<Vector3> course, int radius,
                                       float viewMinX, float viewMinZ, float viewMaxX, float viewMaxZ,
                                       int mapWidth, int mapHeight, bool[] waterMask, float[] waterDepth01)
        {
            for (int s = 0; s < course.Count - 1; s++)
            {
                Vector3 a = course[s];
                Vector3 b = course[s + 1];

                float ax = Mathf.InverseLerp(viewMinX, viewMaxX, a.x) * (mapWidth - 1);
                float az = Mathf.InverseLerp(viewMinZ, viewMaxZ, a.z) * (mapHeight - 1);
                float bx = Mathf.InverseLerp(viewMinX, viewMaxX, b.x) * (mapWidth - 1);
                float bz = Mathf.InverseLerp(viewMinZ, viewMaxZ, b.z) * (mapHeight - 1);

                // Step along the segment in pixel space, one step per pixel of its length, so
                // no gap opens between two discs however the segment is angled.
                int steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(Mathf.Abs(bx - ax), Mathf.Abs(bz - az))));
                for (int t = 0; t <= steps; t++)
                {
                    float f = t / (float)steps;
                    int px = Mathf.RoundToInt(Mathf.Lerp(ax, bx, f));
                    int pz = Mathf.RoundToInt(Mathf.Lerp(az, bz, f));
                    StampDisc(px, pz, radius, mapWidth, mapHeight, waterMask, waterDepth01, 0.5f);
                }
            }
        }

        // Fills a disc of the given radius on the water mask, clamped to the map. The depth
        // is written only where a river is stamped onto dry ground; where a river crosses a
        // lake or the sea the existing deeper value is kept, so a confluence does not draw a
        // shallow scar across the body it joins.
        private static void StampDisc(int cx, int cz, int radius, int mapWidth, int mapHeight,
                                      bool[] waterMask, float[] waterDepth01, float depth01)
        {
            int r2 = radius * radius;
            for (int dz = -radius; dz <= radius; dz++)
            {
                int pz = cz + dz;
                if (pz < 0 || pz >= mapHeight) { continue; }
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int px = cx + dx;
                    if (px < 0 || px >= mapWidth) { continue; }
                    if (dx * dx + dz * dz > r2) { continue; }

                    int i = pz * mapWidth + px;
                    if (!waterMask[i])
                    {
                        waterMask[i] = true;
                        waterDepth01[i] = depth01;
                    }
                }
            }
        }

        // Adds one tree's canopy to the vegetation strength: a filled disc that ACCUMULATES,
        // so overlapping canopies build toward full cover (a wood) while a lone tree is a
        // faint dot. Clamped to 1.
        private static void StampTree(Vector2 xz, int radius,
                                      float viewMinX, float viewMinZ, float viewMaxX, float viewMaxZ,
                                      int mapWidth, int mapHeight, float[] vegetation01)
        {
            float fx = Mathf.InverseLerp(viewMinX, viewMaxX, xz.x) * (mapWidth - 1);
            float fz = Mathf.InverseLerp(viewMinZ, viewMaxZ, xz.y) * (mapHeight - 1);
            int cx = Mathf.RoundToInt(fx);
            int cz = Mathf.RoundToInt(fz);

            int r2 = radius * radius;
            for (int dz = -radius; dz <= radius; dz++)
            {
                int pz = cz + dz;
                if (pz < 0 || pz >= mapHeight) { continue; }
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int px = cx + dx;
                    if (px < 0 || px >= mapWidth) { continue; }
                    if (dx * dx + dz * dz > r2) { continue; }

                    int i = pz * mapWidth + px;
                    vegetation01[i] = Mathf.Clamp01(vegetation01[i] + 0.6f);
                }
            }
        }

        // Is this world point under a real water body, and if so what are its surface and floor levels?
        // First covering body whose surface stands above the ground wins.
        private bool WaterAt(float worldX, float worldZ, float groundY, out float surfaceY, out float floorY)
        {
            surfaceY = 0f; floorY = 0f;
            for (int i = 0; i < mapWaters.Count; i++)
            {
                MapWater w = mapWaters[i];
                if (worldX < w.plan.min.x || worldX > w.plan.max.x
                    || worldZ < w.plan.min.z || worldZ > w.plan.max.z) { continue; }
                if (groundY > w.surfaceY + 0.05f) { continue; }
                surfaceY = w.surfaceY;
                floorY = Mathf.Min(w.floorY, groundY);
                return true;
            }
            return false;
        }

        private Color GetHeatMapColor(float height, float minHeight, float maxHeight)
        {
            float range = maxHeight - minHeight;
            if (range <= 0) return Color.black;
            
            // Assume bottom 15% of the height range is water if it's the lowest point
            float seaLevel = minHeight + (range * 0.15f);
            
            if (height <= seaLevel) 
            {
                float waterDepth = Mathf.InverseLerp(minHeight, seaLevel, height);
                return Color.Lerp(new Color(0f, 0f, 0.5f), new Color(0f, 0.5f, 1f), waterDepth); 
            }
            
            float landVal = Mathf.InverseLerp(seaLevel, maxHeight, height);
            
            if (landVal < 0.2f) return Color.Lerp(new Color(0.1f, 0.8f, 0.1f), new Color(0.8f, 0.8f, 0.2f), landVal / 0.2f);
            if (landVal < 0.6f) return Color.Lerp(new Color(0.8f, 0.8f, 0.2f), new Color(0.5f, 0.3f, 0.1f), (landVal - 0.2f) / 0.4f);
            return Color.Lerp(new Color(0.5f, 0.3f, 0.1f), Color.white, (landVal - 0.6f) / 0.4f);
        }

    }
}
