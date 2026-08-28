// -----------------------------------------------------------------------------
//  Universe Creator - Module 3: The Living World
//  File:    UCEcoEnvelopeDrawer.cs
//  Purpose: V-50 and V-53. THE per-species editor. One foldout, drawn inside the
//           four prototype lists that already exist, showing the prefab, the name,
//           the preset it came from, its weight, its live suitability preview and
//           how many instances it actually placed.
//
//  WHY THERE IS EXACTLY ONE OF THESE
//  ---------------------------------
//  V-50 asks for a per-species foldout and V-53 forbids building it in the
//  vegetation inspector, and both bars can only be true at once if the foldout goes
//  where the prototype lists already are - UniverseCreatorInspector's four library
//  blocks. So this drawer is called from there, the vegetation inspector links to
//  the world object instead of redrawing anything, and there is one place in the UI
//  to author a prototype. Adding a second call site in another inspector would
//  break V-53's bar, not extend it.
//
//  IT OWNS ITS OWN SerializedObject, AND THAT IS A CORRECTNESS REQUIREMENT
//  ---------------------------------------------------------------------
//  UniverseCreatorInspector edits its lists by direct object manipulation - ref
//  t.prefab, Undo.RecordObject, EditorUtility.SetDirty - and never calls
//  serializedObject.Update() or ApplyModifiedProperties(). Reusing that inherited
//  serializedObject here would be silently destructive: it holds a snapshot taken
//  when it was created, so applying it would write those stale values back over
//  every direct edit made since. A SerializedObject constructed here reads the
//  object's current state, so the two editing styles cannot fight.
//
//  It is also constructed ONLY for an open foldout. A closed one draws a title and
//  a count and allocates nothing, which matters because a library can hold dozens
//  of prototypes and an inspector repaints constantly.
//
//  EVERY FIELD IS DRAWN THROUGH SerializedProperty, WHICH IS NOT A STYLE CHOICE
//  ---------------------------------------------------------------------------
//  Three things come free that hand-drawn fields would each have to reimplement:
//  one-step undo, correct prefab-override bolding, and the [Tooltip] and [Header]
//  text already written on EcoEnvelope. The fourth matters most - the envelope is
//  iterated rather than enumerated, so a biological field added to EcoEnvelope
//  tomorrow appears here with no edit to this file. A hand-written field list would
//  silently omit it, and the omission would look like the field not existing.
//
//  THE COUNT COMES FROM THE REGISTRY, NOT FROM COUNTING
//  ---------------------------------------------------
//  The instance count is read from the vegetation manager's registry (V-58). This
//  panel does not walk the scene, does not sum anything, and does not cache a total
//  of its own: a second tally would drift from the first the moment a chunk streamed
//  out while the inspector was closed.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UniverseCreator.Ecology;
using UniverseCreator.TerrainSystem;

namespace UniverseCreator.Editor.Inspectors
{
    /// <summary>
    /// Draws one prototype's ecology envelope as a foldout, with its suitability
    /// preview and its measured instance count.
    /// </summary>
    public static class UCEcoEnvelopeDrawer
    {
        /// <summary>Thumbnail edge, in points.</summary>
        private const float ThumbSize = 46f;

        /// <summary>Width of the instance-count badge, in points.</summary>
        private const float BadgeWidth = 92f;

        /// <summary>
        /// Foldout state, keyed by prototype kind and index. Static so it survives
        /// the inspector being rebuilt, which Unity does on almost every change; a
        /// per-instance field would collapse every foldout as the author typed.
        /// </summary>
        private static readonly HashSet<int> Expanded = new HashSet<int>();

        /// <summary>Cached preset menu. The enum cannot change while the editor runs.</summary>
        private static GUIContent[] presetLabels;
        private static SpeciesPreset[] presetValues;

        /// <summary>
        /// Draws the foldout for one prototype.
        /// </summary>
        /// <param name="world">The world that owns the library. Supplies the property
        /// path, the undo target and the relief the altitude curve spans.</param>
        /// <param name="eco">The envelope as a live object, for the preview and the
        /// preset checksum - the physics reads values, not properties.</param>
        /// <param name="prefab">The prototype's prefab, or null. Only the thumbnail
        /// uses it, so a missing prefab costs the thumbnail and nothing else.</param>
        /// <param name="veg">The world's vegetation manager, resolved once by the
        /// caller. Null is normal and disables the preview and the count.</param>
        public static void Draw(UniverseCreatorObject world, EcoEnvelope eco, GameObject prefab,
                                UCScatterKind kind, int index, UCVegetationSystemManager veg)
        {
            if (world == null || eco == null) return;

            int key = ((int)kind << 20) | Mathf.Max(0, index);
            bool open = Expanded.Contains(key);

            // ---- header row, always drawn ------------------------------------
            EditorGUILayout.BeginHorizontal();

            bool nowOpen = EditorGUILayout.Foldout(open, Title(eco, index), true);

            GUILayout.FlexibleSpace();
            DrawCountBadge(veg, kind, index);

            EditorGUILayout.EndHorizontal();

            if (nowOpen != open)
            {
                if (nowOpen) Expanded.Add(key);
                else Expanded.Remove(key);
            }

            if (!nowOpen) return;

            // ---- open: now the property path is worth building ---------------
            // Scoped and disposed. A SerializedObject holds a native handle, and one
            // per open foldout per repaint would accumulate until a finaliser ran,
            // which on an inspector that repaints continuously is not soon enough.
            using (SerializedObject so = new SerializedObject(world))
                DrawOpen(so, world, eco, prefab, kind, index, veg);
        }

        /// <summary>
        /// The open foldout's body. Split out so the SerializedObject can be scoped
        /// without indenting every line below inside a using block.
        /// </summary>
        private static void DrawOpen(SerializedObject so, UniverseCreatorObject world,
                                     EcoEnvelope eco, GameObject prefab,
                                     UCScatterKind kind, int index,
                                     UCVegetationSystemManager veg)
        {
            SerializedProperty ecoProp = FindEco(so, kind, index);
            if (ecoProp == null)
            {
                EditorGUILayout.HelpBox(
                    "Could not resolve this prototype's ecology envelope. The library "
                  + "list changed while the inspector was open - reselect the world.",
                    MessageType.Warning);
                return;
            }

            EditorGUI.indentLevel++;

            // ---- identity ----------------------------------------------------
            EditorGUILayout.BeginHorizontal();
            DrawThumbnail(prefab);

            EditorGUILayout.BeginVertical();
            DrawIfPresent(ecoProp, "displayName");
            DrawPresetRow(world, eco);
            DrawIfPresent(ecoProp, "weight");
            DrawIfPresent(ecoProp, "enabled");
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();

            // ---- the preview -------------------------------------------------
            EditorGUILayout.Space(2f);
            DrawPreview(eco, veg, world.maxHeightMeters);

            // ---- the envelope ------------------------------------------------
            EditorGUILayout.Space(2f);
            DrawEnvelopeFields(ecoProp);

            EditorGUI.indentLevel--;
            EditorGUILayout.Space(3f);

            // Applied immediately, while the snapshot this SerializedObject took is
            // still current. Registers its own undo step.
            so.ApplyModifiedProperties();
        }

        /// <summary>
        /// The envelope property for one prototype, or null if the list shifted.
        /// </summary>
        private static SerializedProperty FindEco(SerializedObject so, UCScatterKind kind, int index)
        {
            SerializedProperty lib = so.FindProperty("scatterLibrary");
            if (lib == null) return null;

            SerializedProperty list = lib.FindPropertyRelative(ListName(kind));
            if (list == null || !list.isArray) return null;

            if (index < 0 || index >= list.arraySize) return null;

            return list.GetArrayElementAtIndex(index).FindPropertyRelative("eco");
        }

        /// <summary>
        /// Which serialised list a kind names. The only kind-to-list mapping in the
        /// editor layer, kept here so a fifth list means one edit.
        /// </summary>
        private static string ListName(UCScatterKind kind)
        {
            switch (kind)
            {
                case UCScatterKind.Tree: return "trees";
                case UCScatterKind.Shrub: return "shrubs";
                case UCScatterKind.Detail: return "details";
                default: return "objects";
            }
        }

        // ---------------------------------------------------------------------
        //  Header
        // ---------------------------------------------------------------------

        private static string Title(EcoEnvelope eco, int index)
        {
            string name = !string.IsNullOrEmpty(eco.displayName)
                        ? eco.displayName
                        : "Species " + index;

            // Worth seeing without opening the foldout: a disabled species scores
            // zero everywhere and looks like a broken envelope from the outside.
            return eco.enabled ? name : name + "   (disabled)";
        }

        /// <summary>
        /// The measured instance count. A world that has not built has no registry
        /// entry, and saying so beats printing a zero that reads as "placed nothing".
        /// </summary>
        private static void DrawCountBadge(UCVegetationSystemManager veg,
                                          UCScatterKind kind, int index)
        {
            if (veg == null)
            {
                GUILayout.Label("no manager", EditorStyles.miniLabel, GUILayout.Width(BadgeWidth));
                return;
            }

            bool built = veg.RegisteredChunkCount > 0;
            if (!built)
            {
                GUILayout.Label("not built", EditorStyles.miniLabel, GUILayout.Width(BadgeWidth));
                return;
            }

            int count = veg.InstanceCountFor(kind, index);

            // Zero placed in a world that HAS built is the case worth flagging: the
            // species is in the library and absent from the world, which is what the
            // preview below exists to explain.
            GUIStyle style = count == 0 ? EditorStyles.boldLabel : EditorStyles.miniLabel;

            GUILayout.Label(count.ToString("N0") + " placed", style, GUILayout.Width(BadgeWidth));
        }

        private static void DrawThumbnail(GameObject prefab)
        {
            Rect box = GUILayoutUtility.GetRect(ThumbSize, ThumbSize,
                                                GUILayout.Width(ThumbSize),
                                                GUILayout.Height(ThumbSize));

            if (Event.current.type != EventType.Repaint) return;

            if (prefab == null)
            {
                EditorGUI.DrawRect(box, new Color(0.55f, 0.22f, 0.20f, 0.35f));
                GUI.Label(box, " none", EditorStyles.miniLabel);
                return;
            }

            // The rendered preview once it is ready, the type icon until then.
            // GetAssetPreview builds asynchronously and returns null on the first
            // repaint after a selection change, which would leave a blank hole.
            Texture2D shot = AssetPreview.GetAssetPreview(prefab);
            if (shot == null) shot = AssetPreview.GetMiniThumbnail(prefab);

            if (shot != null) GUI.DrawTexture(box, shot, ScaleMode.ScaleToFit);
        }

        // ---------------------------------------------------------------------
        //  Preset
        // ---------------------------------------------------------------------

        /// <summary>
        /// The preset the envelope came from, and whether it has been edited since.
        ///
        /// "(modified)" is a checksum over the biological fields only, so changing a
        /// LOD bias does not make a spruce claim to be a modified spruce.
        /// SpeciesPresets owns that decision; this row only reports it.
        /// </summary>
        private static void DrawPresetRow(UniverseCreatorObject world, EcoEnvelope eco)
        {
            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.LabelField("Preset",
                GUILayout.Width(Mathf.Max(40f, EditorGUIUtility.labelWidth - 4f)));

            string label;
            if (string.IsNullOrEmpty(eco.presetName))
            {
                label = "Custom";
            }
            else
            {
                label = SpeciesPresets.IsModified(eco)
                      ? eco.presetName + "   (modified)"
                      : eco.presetName;
            }

            if (EditorGUILayout.DropdownButton(new GUIContent(label), FocusType.Keyboard))
                ShowPresetMenu(world, eco);

            EditorGUILayout.EndHorizontal();
        }

        private static void ShowPresetMenu(UniverseCreatorObject world, EcoEnvelope eco)
        {
            BuildPresetTable();

            GenericMenu menu = new GenericMenu();

            for (int i = 0; i < presetValues.Length; i++)
            {
                SpeciesPreset preset = presetValues[i];

                bool on = !string.IsNullOrEmpty(eco.presetName)
                       && eco.presetName == SpeciesPresets.DisplayName(preset);

                menu.AddItem(presetLabels[i], on, ApplyPreset,
                             new PresetApply(world, eco, preset));
            }

            menu.ShowAsContext();
        }

        /// <summary>What one menu entry carries. A class, because GenericMenu passes object.</summary>
        private class PresetApply
        {
            public readonly UniverseCreatorObject world;
            public readonly EcoEnvelope eco;
            public readonly SpeciesPreset preset;

            public PresetApply(UniverseCreatorObject world, EcoEnvelope eco, SpeciesPreset preset)
            {
                this.world = world;
                this.eco = eco;
                this.preset = preset;
            }
        }

        /// <summary>
        /// Applies a preset by direct manipulation, matching how the rest of this
        /// inspector edits. Safe because a menu callback runs after the GUI pass has
        /// ended, so no SerializedObject holds pending edits at this moment.
        /// </summary>
        private static void ApplyPreset(object payload)
        {
            PresetApply a = payload as PresetApply;
            if (a == null || a.eco == null) return;

            if (a.world != null) Undo.RecordObject(a.world, "Apply Species Preset");

            SpeciesPresets.Apply(a.eco, a.preset);

            if (a.world != null) EditorUtility.SetDirty(a.world);
        }

        /// <summary>
        /// Builds the grouped menu once. None is deliberately absent: it applies
        /// nothing, and an envelope detaches from its preset by being edited, which
        /// the "(modified)" marker already reports.
        /// </summary>
        private static void BuildPresetTable()
        {
            if (presetValues != null) return;

            Array raw = Enum.GetValues(typeof(SpeciesPreset));

            List<SpeciesPreset> values = new List<SpeciesPreset>(raw.Length);
            List<GUIContent> labels = new List<GUIContent>(raw.Length);

            for (int i = 0; i < raw.Length; i++)
            {
                SpeciesPreset p = (SpeciesPreset)raw.GetValue(i);
                if (p == SpeciesPreset.None) continue;

                values.Add(p);
                labels.Add(new GUIContent(
                    ObjectNames.NicifyVariableName(SpeciesPresets.GroupOf(p).ToString())
                    + "/" + SpeciesPresets.DisplayName(p)));
            }

            presetValues = values.ToArray();
            presetLabels = labels.ToArray();
        }

        // ---------------------------------------------------------------------
        //  Preview
        // ---------------------------------------------------------------------

        private static void DrawPreview(EcoEnvelope eco, UCVegetationSystemManager veg,
                                      float topAltitudeM)
        {
            if (veg == null)
            {
                EditorGUILayout.HelpBox(
                    "The suitability preview needs a Vegetation System on this world, "
                  + "because the curve is drawn at the world's own latitude, lapse rate "
                  + "and climate. Add one to see it.",
                    MessageType.Info);
                return;
            }

            EcoSampleSettings settings = veg.ResolveSettings();
            UCSuitabilityPreview.Draw(eco, in settings, topAltitudeM);
        }

        // ---------------------------------------------------------------------
        //  Envelope fields
        // ---------------------------------------------------------------------

        /// <summary>
        /// Every remaining serialised field on the envelope, in declaration order,
        /// with its own tooltips and headers. Iterated rather than listed - see the
        /// file header on why that is the point.
        /// </summary>
        private static void DrawEnvelopeFields(SerializedProperty ecoProp)
        {
            SerializedProperty it = ecoProp.Copy();
            SerializedProperty end = ecoProp.GetEndProperty();

            bool enterChildren = true;

            while (it.NextVisible(enterChildren) && !SerializedProperty.EqualContents(it, end))
            {
                // Only the first step descends into the envelope. After that the walk
                // stays at field level, or it would expand every Vector2 into two
                // floats and lose the paired control.
                enterChildren = false;

                if (AlreadyDrawn(it.name)) continue;

                EditorGUILayout.PropertyField(it, true);
            }
        }

        /// <summary>
        /// Fields the header row above already shows, plus the checksum bookkeeping
        /// SpeciesPresets owns - a hand-edited checksum would make a modified
        /// envelope claim to be an untouched preset.
        /// </summary>
        private static bool AlreadyDrawn(string field)
        {
            return field == "displayName"
                || field == "weight"
                || field == "enabled"
                || field == "presetName"
                || field == "presetChecksum";
        }

        private static void DrawIfPresent(SerializedProperty parent, string field)
        {
            SerializedProperty p = parent.FindPropertyRelative(field);
            if (p != null) EditorGUILayout.PropertyField(p);
        }
    }
}
