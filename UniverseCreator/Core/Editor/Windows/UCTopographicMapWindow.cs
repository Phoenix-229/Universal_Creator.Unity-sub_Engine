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
            Bounds bounds = new Bounds(terrains[0].transform.position + terrains[0].terrainData.size / 2f, terrains[0].terrainData.size);
            foreach (var t in terrains)
            {
                bounds.Encapsulate(new Bounds(t.transform.position + t.terrainData.size / 2f, t.terrainData.size));
            }
            return bounds;
        }

        private void GenerateMap()
        {
            Terrain[] terrains = Terrain.activeTerrains;
            if (terrains == null || terrains.Length == 0) return;

            Bounds bounds = GetTerrainBounds(terrains);
            
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
                        
                        if (viewMode == MapViewMode.Topography)
                        {
                            c = GetHeatMapColor(heights[i], trueMin, trueMax);
                        }
                        else if (viewMode == MapViewMode.Satellite)
                        {
                            c = GetSatelliteColor(heights[i], trueMin, trueMax);
                        }
                        else if (viewMode == MapViewMode.Terrain)
                        {
                            Color sat = GetSatelliteColor(heights[i], trueMin, trueMax);
                            // Blend satellite with topography contour brightness
                            c = Color.Lerp(sat, new Color(normalizedHeight, normalizedHeight, normalizedHeight), 0.3f);
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
