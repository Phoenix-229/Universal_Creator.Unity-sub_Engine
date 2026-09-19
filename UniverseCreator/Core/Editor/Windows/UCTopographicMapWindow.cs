using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace UniverseCreator.Editor.Windows
{
    public class UCTopographicMapWindow : EditorWindow
    {
        public enum MapViewMode { Topography, Satellite, Terrain }
        private MapViewMode viewMode = MapViewMode.Topography;

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

        // Averaged colour per splat layer, keyed off the layer name (readable-texture-free), cached per
        // terrain data so the satellite view is one lookup per pixel.
        private readonly Dictionary<TerrainData, Color[]> layerColorCache =
            new Dictionary<TerrainData, Color[]>();
        private readonly Dictionary<TerrainData, float[,,]> alphaCache =
            new Dictionary<TerrainData, float[,,]>();

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
                    
                    float contourHeight = Mathf.Round(height / 50f) * 50f;
                    
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
            // The real painted surface colour at each pixel, read from the terrain's own splat layers, so
            // the satellite view shows grass, rock, sand and snow where the tool actually painted them
            // rather than a colour invented from height. Default mid-grey until a terrain fills it.
            Color[] surfaceCols = new Color[mapWidth * mapHeight];
            for (int i = 0; i < surfaceCols.Length; i++) { surfaceCols[i] = new Color(0.5f, 0.5f, 0.5f); }
            // Per pixel: is this point under a real water body, and how deep (0..1) for the water shading.
            bool[] waterMask = new bool[mapWidth * mapHeight];
            float[] waterDepth01 = new float[mapWidth * mapHeight];

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
                            surfaceCols[y * mapWidth + x] = DominantSplatColor(t, worldX, worldZ);
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
                    }
                    else
                    {
                        heights[y * mapWidth + x] = float.MinValue;
                    }
                }
            }

            maxTerrainHeight = trueMax;
            minTerrainHeight = trueMin;

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
                        float normalizedHeight = Mathf.InverseLerp(trueMin, trueMax, heights[i]);
                        Color c = Color.black;

                        // Real water first, in every mode: deep water dark blue, shallows lighter, so a
                        // lake, a river and the sea read as water and not as low ground.
                        if (waterMask[i])
                        {
                            c = Color.Lerp(new Color(0.30f, 0.55f, 0.72f), new Color(0.05f, 0.16f, 0.34f),
                                waterDepth01[i]);
                        }
                        else if (viewMode == MapViewMode.Topography)
                        {
                            c = GetHeatMapColor(heights[i], trueMin, trueMax);
                        }
                        else if (viewMode == MapViewMode.Satellite)
                        {
                            // The real painted surface, straight from the terrain's splat layers.
                            c = surfaceCols[i];
                        }
                        else if (viewMode == MapViewMode.Terrain)
                        {
                            // Real surface, shaded by a simple hillshade from the neighbouring heights so
                            // relief reads without inventing colour.
                            float shade = 1f;
                            if (x > 0 && y > 0)
                            {
                                float hl = heights[y * mapWidth + (x - 1)];
                                float hd = heights[(y - 1) * mapWidth + x];
                                if (hl != float.MinValue && hd != float.MinValue)
                                {
                                    float dx = heights[i] - hl;
                                    float dz = heights[i] - hd;
                                    shade = Mathf.Clamp(1f - (dx + dz) * 0.03f, 0.55f, 1.25f);
                                }
                            }
                            c = surfaceCols[i] * shade;
                            c.a = 1f;
                        }
                        
                        // Draw contours every 50m for Topography and Terrain
                        if ((viewMode == MapViewMode.Topography || viewMode == MapViewMode.Terrain) && x > 0 && y > 0)
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

        // Read every placed level water body once per map build. Caches are cleared here too, so a map
        // refresh after the world changed re-reads the real layers and bodies rather than a stale copy.
        private void GatherWater()
        {
            mapWaters.Clear();
            layerColorCache.Clear();
            alphaCache.Clear();

            var bodies = UnityEngine.Object.FindObjectsByType<UniverseCreator.TerrainSystem.UCWaterBody>(
                FindObjectsSortMode.None);
            if (bodies == null) { return; }
            foreach (var b in bodies)
            {
                if (b == null || !b.IsLevelBody) { continue; }
                Bounds plan;
                if (!b.TryGetPlanBounds(out plan)) { continue; }
                float surf = b.SurfaceWorldY();
                mapWaters.Add(new MapWater { surfaceY = surf, floorY = plan.min.y, plan = plan });
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

        // The colour of the dominant splat layer at a world point, from the terrain's own alphamap and the
        // layer's name. This is the real painted surface, not a colour invented from height.
        private Color DominantSplatColor(Terrain t, float worldX, float worldZ)
        {
            TerrainData td = t.terrainData;
            if (td == null || td.alphamapLayers == 0) { return new Color(0.45f, 0.5f, 0.4f); }

            float[,,] alpha;
            if (!alphaCache.TryGetValue(td, out alpha))
            {
                try { alpha = td.GetAlphamaps(0, 0, td.alphamapWidth, td.alphamapHeight); }
                catch { alpha = null; }
                alphaCache[td] = alpha;
            }
            if (alpha == null) { return new Color(0.45f, 0.5f, 0.4f); }

            Vector3 local = t.transform.InverseTransformPoint(new Vector3(worldX, 0f, worldZ));
            int aw = td.alphamapWidth, ah = td.alphamapHeight;
            int mx = Mathf.Clamp(Mathf.RoundToInt(local.x / td.size.x * (aw - 1)), 0, aw - 1);
            int mz = Mathf.Clamp(Mathf.RoundToInt(local.z / td.size.z * (ah - 1)), 0, ah - 1);

            int layers = Mathf.Min(td.alphamapLayers, alpha.GetLength(2));
            int best = 0; float bestW = -1f;
            for (int l = 0; l < layers; l++)
            {
                float w = alpha[mz, mx, l];
                if (w > bestW) { bestW = w; best = l; }
            }
            Color[] cols = LayerColors(td);
            return best < cols.Length ? cols[best] : new Color(0.45f, 0.5f, 0.4f);
        }

        private Color[] LayerColors(TerrainData td)
        {
            Color[] cols;
            if (layerColorCache.TryGetValue(td, out cols)) { return cols; }
            var layers = td.terrainLayers;
            cols = new Color[layers != null ? layers.Length : 0];
            for (int i = 0; i < cols.Length; i++)
            {
                string n = layers[i] != null ? layers[i].name : string.Empty;
                cols[i] = ColorForLayerName(n);
            }
            layerColorCache[td] = cols;
            return cols;
        }

        // A from-above colour for a splat layer, decided by its name -- the real painted material, mapped to
        // what that material looks like from orbit. Unknown names fall to a neutral scrub green.
        private Color ColorForLayerName(string name)
        {
            string n = string.IsNullOrEmpty(name) ? string.Empty : name.ToLowerInvariant();
            if (n.Contains("snow") || n.Contains("ice")) { return new Color(0.92f, 0.94f, 0.97f); }
            if (n.Contains("sand") || n.Contains("beach") || n.Contains("desert") || n.Contains("dune"))
            { return new Color(0.78f, 0.71f, 0.5f); }
            if (n.Contains("rock") || n.Contains("cliff") || n.Contains("stone") || n.Contains("scree"))
            { return new Color(0.44f, 0.42f, 0.4f); }
            if (n.Contains("gravel") || n.Contains("slate")) { return new Color(0.55f, 0.54f, 0.52f); }
            if (n.Contains("grass") || n.Contains("meadow") || n.Contains("lush") || n.Contains("plain"))
            { return new Color(0.32f, 0.5f, 0.22f); }
            if (n.Contains("forest") || n.Contains("wood") || n.Contains("jungle") || n.Contains("moss"))
            { return new Color(0.18f, 0.34f, 0.15f); }
            if (n.Contains("dirt") || n.Contains("soil") || n.Contains("mud") || n.Contains("ground")
                || n.Contains("earth")) { return new Color(0.42f, 0.32f, 0.2f); }
            if (n.Contains("tundra") || n.Contains("dry") || n.Contains("savanna") || n.Contains("steppe"))
            { return new Color(0.6f, 0.56f, 0.34f); }
            return new Color(0.3f, 0.42f, 0.24f);
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

        private Color GetSatelliteColor(float height, float minHeight, float maxHeight)
        {
            float range = maxHeight - minHeight;
            if (range <= 0) return Color.black;
            
            float seaLevel = minHeight + (range * 0.15f); // Rough heuristic for sea level
            
            if (height <= seaLevel) 
            {
                float waterDepth = Mathf.InverseLerp(minHeight, seaLevel, height);
                return Color.Lerp(new Color(0.02f, 0.1f, 0.2f), new Color(0.1f, 0.3f, 0.4f), waterDepth); 
            }
            
            float landVal = Mathf.InverseLerp(seaLevel, maxHeight, height);
            
            if (landVal < 0.03f) return new Color(0.76f, 0.7f, 0.5f); // Beach sand
            if (landVal < 0.3f) return Color.Lerp(new Color(0.2f, 0.35f, 0.15f), new Color(0.15f, 0.25f, 0.1f), (landVal - 0.03f) / 0.27f); // Dense forest
            if (landVal < 0.7f) return Color.Lerp(new Color(0.15f, 0.25f, 0.1f), new Color(0.4f, 0.4f, 0.4f), (landVal - 0.3f) / 0.4f); // Forest to rock
            if (landVal < 0.85f) return Color.Lerp(new Color(0.4f, 0.4f, 0.4f), new Color(0.6f, 0.6f, 0.65f), (landVal - 0.7f) / 0.15f); // Bare rock
            return Color.Lerp(new Color(0.6f, 0.6f, 0.65f), new Color(0.95f, 0.95f, 0.98f), (landVal - 0.85f) / 0.15f); // Snow caps
        }
    }
}
