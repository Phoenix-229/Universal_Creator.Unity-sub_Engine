// -----------------------------------------------------------------------------
//  Universe Creator - Procedural Content Generation Toolkit
//
//  File:    UCTextureBudget.cs
//  Purpose: Gives every texture under Content a per-platform import ceiling, so a
//           mid-range phone loads a fraction of the bytes a desktop does, without
//           anyone hand-editing a .meta file.
//
//  WHY THIS EXISTS - THE ARITHMETIC, NOT A PREFERENCE
//  --------------------------------------------------
//  ASTC 6x6 packs a 6x6 block of pixels into 16 bytes: 0.444 bytes per pixel. A
//  full mip chain adds a third on top of that.
//
//      2048 x 2048 -> 4.19 Mpx * 0.444 = 1.86 MB, with mips 2.49 MB
//      1024 x 1024 -> 1.05 Mpx * 0.444 = 0.47 MB, with mips 0.62 MB
//       512 x  512 -> 0.26 Mpx * 0.444 = 0.12 MB, with mips 0.16 MB
//
//  Content holds 182 textures. Every one of them was capped at 2048 or 4096 and
//  not one had an active mobile override anywhere in the project, so a phone build
//  would have asked for on the order of 182 * 2.49 = 453 MB of texture memory. On
//  a device whose entire process budget is 1.2-1.8 GB that is not a slow path, it
//  is an out-of-memory kill. The role ceilings below bring the same set to roughly
//  90 MB, which is a budget a mid-range phone can actually hold.
//
//  ETC2 makes the case stronger, not weaker: RGBA8 ETC2 is a full byte per pixel,
//  so that same 2048 map costs 5.33 MB with mips. A device old enough to fall back
//  to ETC2 needs this more than an ASTC device does, not less.
//
//  HOW A TEXTURE'S ROLE IS DECIDED - BINDINGS, NOT FILENAMES
//  ---------------------------------------------------------
//  A ceiling is only defensible if the role is certain, and the authoritative
//  record of a texture's role is the shader property a material binds it to, not
//  the characters at the end of its filename. So the first pass walks every
//  material under Content, asks the shader which properties are texture-typed, and
//  records assetPath -> property.
//
//  That pass is what settled the two ambiguous suffixes in this content set: _R
//  binds to _MetallicGlossMap and _SSC binds to _TransmissionMap. No suffix table
//  would have told us that reliably - _R reads equally well as "roughness", and
//  _SSC could have been a subsurface *colour* map deserving a colour ceiling.
//
//  Filename suffix and TextureImporter.textureType are the fallback only, for a
//  texture that no material references yet. Anything still unrecognised is treated
//  as Color, the most generous ceiling, so that a wrong guess costs memory rather
//  than visible quality.
//
//  WHY WriteImportSettingsIfDirty AND NOT SaveAndReimport
//  ------------------------------------------------------
//  Nothing here changes how a texture imports for the platform the editor is
//  currently on - only the Android and iOS ceilings, which are read at build time.
//  SaveAndReimport would re-encode all 182 textures for the desktop target for no
//  benefit at all, costing minutes of wall clock and a large RAM spike.
//  WriteImportSettingsIfDirty writes the .meta and stops, which is exactly the
//  change that was asked for and nothing else.
//
//  It has a second, wanted effect: 52 of these .meta files are still the 2018 stub
//  form - eight lines, no platformSettings block at all. Writing settings through
//  the importer serialises the whole current-version block, so those stubs come out
//  in the modern format as a side effect of this pass.
//
//  WHY THIS IS A COMMAND AND NOT AN AssetPostprocessor
//  ---------------------------------------------------
//  An OnPreprocessTexture hook would reapply this on every reimport forever, which
//  means it would also silently overwrite any deliberate per-texture decision made
//  later - the one texture someone raises to 2048 on purpose would quietly drop
//  back. So the policy applies when asked, and UCSelfTest verifies it stayed
//  applied. Apply once, verify always; never fight the user's own edits.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UniverseCreator.Editor.Utilities
{
    /// <summary>
    /// Applies and audits the per-platform texture size ceiling for everything
    /// under Content. Editor only. The single source of truth for the policy -
    /// the apply command, the audit command and the self-test all read it here.
    /// </summary>
    public static class UCTextureBudget
    {
        /// <summary>Everything this policy governs lives under here.</summary>
        public const string ContentRoot = "Assets/UniverseCreator/Content";

        /// <summary>What a texture is for, which is what decides its ceiling.</summary>
        public enum Role
        {
            /// <summary>Base colour, albedo, atlases, billboards.</summary>
            Color,
            /// <summary>Tangent-space normals.</summary>
            Normal,
            /// <summary>Masks, gloss, AO, height, transmission, emission.</summary>
            Data
        }

        // Ceilings, in pixels, for Android and iOS.
        //
        // Color  1024 - a tiling bark or leaf atlas read at arm's length on a 6"
        //               screen cannot resolve more than this; the mip chain was
        //               already throwing the top level away on that device.
        // Normal 1024 - kept level with Color on purpose. A normal map half the
        //               resolution of the albedo it accompanies reads as soft
        //               shading under sharp colour detail, which looks like a bug
        //               rather than like a lower setting.
        // Data    512 - masks, gloss, AO, height and transmission are all
        //               low-frequency by nature. This is the one place real bytes
        //               come back with no perceptible cost, and it is 64 of the
        //               182 files.
        private const int CapColor = 1024;
        private const int CapNormal = 1024;
        private const int CapData = 512;

        // Unity's platform *API* name for iOS is "iPhone", even though the
        // serialised buildTarget in a current .meta reads "iOS". The audit reads
        // the ceiling back out of the importer afterwards, so if a future editor
        // renames this the verification fails loudly instead of quietly skipping
        // the platform.
        private const string PlatformAndroid = "Android";
        private const string PlatformIos = "iPhone";

        /// <summary>
        /// Shader property -> role. This is the authoritative mapping; the suffix
        /// table below is only consulted for textures no material binds.
        /// Covers URP Lit/SimpleLit/SpeedTree, Standard, and the UC shaders.
        /// </summary>
        private static readonly Dictionary<string, Role> PropertyRoles =
            new Dictionary<string, Role>(StringComparer.OrdinalIgnoreCase)
            {
                // ---- colour ----
                { "_BaseMap",           Role.Color  },
                { "_MainTex",           Role.Color  },
                { "_BaseColorMap",      Role.Color  },
                { "_ColorMap",          Role.Color  }, // Content audit: observed ShaderGraph albedo slot; preserve colour-space classification after sample relocation.
                { "_AlbedoMap",         Role.Color  },
                { "_DetailAlbedoMap",   Role.Color  },
                { "_Splat0",            Role.Color  },
                { "_Splat1",            Role.Color  },
                { "_Splat2",            Role.Color  },
                { "_Splat3",            Role.Color  },
                { "_BillboardTex",      Role.Color  },

                // ---- normals ----
                { "_BumpMap",           Role.Normal },
                { "_NormalMap",         Role.Normal },
                { "_DetailNormalMap",   Role.Normal },
                { "_Normal0",           Role.Normal },
                { "_Normal1",           Role.Normal },
                { "_Normal2",           Role.Normal },
                { "_Normal3",           Role.Normal },

                // ---- data / masks ----
                { "_MetallicGlossMap",  Role.Data   },
                { "_SpecGlossMap",      Role.Data   },
                { "_SpecularMap",       Role.Data   },
                { "_OcclusionMap",      Role.Data   },
                { "_AOMap",             Role.Data   },
                { "_MaskMap",           Role.Data   },
                { "_ExtraMap",          Role.Data   }, // Content audit: the observed vegetation slot binds a packed mask; report its data import requirements explicitly.
                { "_Mask0",             Role.Data   },
                { "_Mask1",             Role.Data   },
                { "_Mask2",             Role.Data   },
                { "_Mask3",             Role.Data   },
                { "_DetailMask",        Role.Data   },
                { "_ParallaxMap",       Role.Data   },
                { "_HeightMap",         Role.Data   },
                { "_TransmissionMap",   Role.Data   },
                { "_EmissionMap",       Role.Data   },
                { "_SmoothnessMap",     Role.Data   },
                { "_MudTex",            Role.Data   },
                { "_DetailTex",         Role.Data   },
                { "_GradientTex",       Role.Data   }
            };

        /// <summary>
        /// Filename suffix -> role, the fallback for unreferenced textures. Every
        /// token here was taken from an actual filename under Content, not from a
        /// general convention.
        /// </summary>
        private static readonly Dictionary<string, Role> SuffixRoles =
            new Dictionary<string, Role>(StringComparer.OrdinalIgnoreCase)
            {
                { "n",       Role.Normal }, { "normal",   Role.Normal },
                { "normalgl",Role.Normal }, { "nrm",      Role.Normal },

                { "r",       Role.Data   }, { "roughness",Role.Data   },
                { "ssc",     Role.Data   }, { "m",        Role.Data   },
                { "metalness",Role.Data  }, { "mask",     Role.Data   },
                { "s",       Role.Data   }, { "specul",   Role.Data   },
                { "smooth",  Role.Data   }, { "ao",       Role.Data   },
                { "ambientocclusion", Role.Data },
                { "displacement", Role.Data }, { "dis",   Role.Data   },
                { "h",       Role.Data   }, { "e",        Role.Data   },
                { "trans",   Role.Data   },

                { "bc",      Role.Color  }, { "color",    Role.Color  },
                { "atlas",   Role.Color  }, { "billboards", Role.Color },
                { "a",       Role.Color  }, { "leaf",     Role.Color  },
                { "sea",     Role.Color  }
            };

        /// <summary>One texture's decision, for reporting.</summary>
        public struct Entry
        {
            public string Path;
            public Role Role;
            public string Reason;
            public int Cap;
            public int AndroidBefore;
            public bool AndroidOverridden;
            public bool NormalTypeWrong;
            /// <summary>True when an observed packed _ExtraMap mask is incorrectly sampled as sRGB colour.</summary>
            public bool PackedMaskSrgbWrong;
            public bool Changed;
        }

        // ---------------------------------------------------------------------
        //  Menu entry points
        // ---------------------------------------------------------------------

        [MenuItem("Tools/Universe Creator/Setup/Apply Mobile Texture Budget", priority = 202)]
        private static void ApplyMenu()
        {
            List<Entry> entries;
            int changed = Apply(false, out entries);
            Debug.Log("Universe Creator: mobile texture budget applied. " +
                      changed + " of " + entries.Count + " textures updated.\n" +
                      Summarise(entries));
        }

        [MenuItem("Tools/Universe Creator/Diagnostics/Audit Texture Budget", priority = 212)]
        private static void AuditMenu()
        {
            List<Entry> entries;
            int nonCompliant = Audit(out entries);
            if (nonCompliant == 0)
                Debug.Log("Universe Creator: texture budget OK. All " + entries.Count +
                          " textures under Content carry a compliant mobile ceiling.\n" +
                          Summarise(entries));
            else
                Debug.LogWarning("Universe Creator: texture budget NOT applied to " +
                                 nonCompliant + " of " + entries.Count + " textures. " +
                                 "Run Tools/Universe Creator/Setup/Apply Mobile Texture Budget.\n" +
                                 Summarise(entries));
        }

        /// <summary>
        /// Headless entry point, for
        ///   -executeMethod UniverseCreator.Editor.Utilities.UCTextureBudget.ApplyBatch
        ///
        /// Applies the policy and prints the before/after roll-up into the batch log.
        /// It does not call EditorApplication.Exit - the caller passes -quit, so the
        /// editor closes on its own once this returns and the log is complete.
        /// </summary>
        public static void ApplyBatch()
        {
            List<Entry> entries;
            int changed = Apply(false, out entries);

            Debug.Log("=== UC TEXTURE BUDGET ===\n" +
                      "  changed      : " + changed + " of " + entries.Count + "\n" +
                      Summarise(entries));

            // Prove it landed by re-reading the importers rather than trusting the
            // write. A ceiling that did not serialise is exactly the failure this
            // whole pass exists to prevent, so it gets checked, not assumed.
            List<Entry> after;
            int stillBad = Audit(out after);
            Debug.Log("=== UC TEXTURE BUDGET VERIFY ===\n" +
                      "  non-compliant after apply : " + stillBad + "\n" +
                      Summarise(after));
        }

        // ---------------------------------------------------------------------
        //  Apply / Audit
        // ---------------------------------------------------------------------

        /// <summary>
        /// Sets the Android and iOS ceiling on every texture under Content.
        /// Returns how many .meta files actually changed. dryRun decides nothing
        /// is written, which is what Audit uses.
        /// </summary>
        public static int Apply(bool dryRun, out List<Entry> entries)
        {
            return Apply(dryRun, out entries, null);
        }

        /// <summary>Applies or audits the existing mobile policy only for the supplied exact imported asset paths.</summary>
        /// <param name="dryRun">True reports required changes without writing import settings.</param>
        /// <param name="entries">Decisions for selected textures; other assets are untouched.</param>
        /// <param name="assetPaths">Exact Unity paths to include, or null for all Content textures.</param>
        /// <returns>Number of selected texture importers needing or receiving a correction.</returns>
        public static int Apply(bool dryRun, out List<Entry> entries, ISet<string> assetPaths)
        {
            entries = new List<Entry>();

            Dictionary<string, string> bindings = BuildBindingMap();
            string[] guids = AssetDatabase.FindAssets("t:Texture", new[] { ContentRoot });
            int changed = 0;

            // One asset-editing bracket for the whole sweep. Without it Unity
            // reconsiders the import graph after each .meta write, which on 182
            // files is the difference between seconds and minutes.
            if (!dryRun) AssetDatabase.StartAssetEditing();
            try
            {
                for (int i = 0; i < guids.Length; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    if (assetPaths != null && !assetPaths.Contains(path)) continue; // Owner cleanup: preserve existing Surfaces importers.
                    TextureImporter importer =
                        AssetImporter.GetAtPath(path) as TextureImporter;

                    // Render textures, cubemaps authored as assets and anything
                    // else that is a Texture but not an imported image have no
                    // TextureImporter. They have no import ceiling to set.
                    if (importer == null) continue;

                    string reason;
                    Role role = RoleFor(path, importer, bindings, out reason);
                    int cap = CapFor(role);

                    TextureImporterPlatformSettings android =
                        importer.GetPlatformTextureSettings(PlatformAndroid);

                    // A texture the project uses as a normal map but imports as a
                    // plain colour texture is a live rendering bug, not a setting
                    // preference: UnpackNormal expects the two-channel DXT5nm /
                    // ASTC layout that only textureType NormalMap produces, so a
                    // Default-typed normal map lights the surface wrongly - and
                    // silently, because nothing errors.
                    //
                    // The _NormalGL naming in the Surfaces sets is already the
                    // convention Unity wants (OpenGL, +Y up), so these need the
                    // type corrected and nothing else. A DirectX-convention map
                    // would additionally need its green channel inverted, which is
                    // deliberately NOT done here - there are none in this content.
                    bool needsType = role == Role.Normal &&
                                     importer.textureType != TextureImporterType.NormalMap;
                    bool needsLinear = role == Role.Data && bindings.TryGetValue(path, out string property) && property == "_ExtraMap" && importer.sRGBTexture;

                    Entry e = new Entry
                    {
                        Path = path,
                        Role = role,
                        Reason = reason,
                        Cap = cap,
                        AndroidBefore = android.maxTextureSize,
                        AndroidOverridden = android.overridden,
                        NormalTypeWrong = needsType,
                        PackedMaskSrgbWrong = needsLinear,
                        Changed = false
                    };

                    bool needsCap = !android.overridden || android.maxTextureSize != cap;

                    if ((needsCap || needsType || needsLinear) && !dryRun)
                    {
                        if (needsCap)
                        {
                            SetCap(importer, PlatformAndroid, cap);
                            SetCap(importer, PlatformIos, cap);
                        }

                        if (needsLinear) importer.sRGBTexture = false; // Observed packed mask: preserve channel values during sampling.
                        if (needsType || needsLinear)
                        {
                            if (needsType) importer.textureType = TextureImporterType.NormalMap;

                            // Normal encoding and packed-mask colour space change the imported artifact,
                            // so these require reimport; a platform ceiling alone uses the cheap write below.
                            importer.SaveAndReimport();
                        }
                        else
                        {
                            // Writes the .meta if anything above marked the importer
                            // dirty. No reimport - see the header note.
                            AssetDatabase.WriteImportSettingsIfDirty(path);
                        }

                        e.Changed = true;
                        changed++;
                    }
                    else if (needsCap || needsType || needsLinear)
                    {
                        changed++;
                    }

                    entries.Add(e);
                }
            }
            finally
            {
                if (!dryRun) AssetDatabase.StopAssetEditing();
            }

            // BuildBindingMap had to load every material, and a material pulls its
            // textures in with it. Hand that memory back rather than carrying a few
            // hundred MB of source textures around for the rest of the session.
            EditorUtility.UnloadUnusedAssetsImmediate();

            if (!dryRun) AssetDatabase.Refresh();
            return changed;
        }

        /// <summary>
        /// Reports how many textures under Content are missing their ceiling,
        /// without writing anything. 0 means the policy is fully applied.
        /// </summary>
        public static int Audit(out List<Entry> entries)
        {
            return Apply(true, out entries);
        }

        /// <summary>Audit with no detail, for callers that only want the count.</summary>
        public static int Audit()
        {
            List<Entry> ignored;
            return Audit(out ignored);
        }

        private static void SetCap(TextureImporter importer, string platform, int cap)
        {
            TextureImporterPlatformSettings s =
                importer.GetPlatformTextureSettings(platform);

            // Only these two fields are touched. Format, compression and quality
            // are left exactly as Unity handed them over, so a texture that was
            // deliberately set to a particular format keeps it and Automatic keeps
            // choosing ASTC or ETC2 from the player settings as it should.
            s.overridden = true;
            s.maxTextureSize = cap;

            importer.SetPlatformTextureSettings(s);
        }

        private static int CapFor(Role role)
        {
            if (role == Role.Normal) return CapNormal;
            if (role == Role.Data) return CapData;
            return CapColor;
        }

        // ---------------------------------------------------------------------
        //  Role resolution
        // ---------------------------------------------------------------------

        /// <summary>
        /// assetPath -> the shader property a material binds it to. Built once per
        /// Apply. Where two materials bind the same texture to different slots the
        /// first wins, which in practice does not happen in this content set.
        /// </summary>
        private static Dictionary<string, string> BuildBindingMap()
        {
            Dictionary<string, string> map =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string[] guids = AssetDatabase.FindAssets("t:Material", new[] { ContentRoot });
            for (int i = 0; i < guids.Length; i++)
            {
                string matPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null || mat.shader == null) continue;

                Shader shader = mat.shader;
                // UC-T02-06/10: GetTexture can crash Unity 6000.5 with headless/imported shaders.
                // Audit authored bindings without asking the renderer to resolve native defaults.
                Dictionary<string, Texture> savedTextures = ReadSavedTextureBindings(mat);
                int count = shader.GetPropertyCount();
                for (int p = 0; p < count; p++)
                {
                    if (shader.GetPropertyType(p) != ShaderPropertyType.Texture) continue;

                    string prop = shader.GetPropertyName(p);
                    if (!savedTextures.TryGetValue(prop, out Texture tex) || tex == null) continue;

                    string texPath = AssetDatabase.GetAssetPath(tex);
                    if (string.IsNullOrEmpty(texPath)) continue;
                    if (map.ContainsKey(texPath)) continue;

                    map[texPath] = prop;
                }
            }

            return map;
        }

        /// <summary>UC-T02-06/10: reads authored texture references without renderer access or material mutation.</summary>
        /// <param name="material">Non-null material inspected on the Editor main thread.</param>
        /// <returns>Property names and borrowed texture references, including explicitly cleared null bindings.</returns>
        /// <exception cref="ArgumentNullException">The material is null or destroyed.</exception>
        /// <exception cref="InvalidOperationException">The installed material serialization layout is unsupported.</exception>
        internal static Dictionary<string, Texture> ReadSavedTextureBindings(Material material)
        {
            if (material == null) throw new ArgumentNullException(nameof(material));
            var result = new Dictionary<string, Texture>(StringComparer.Ordinal);
            using (var serialized = new SerializedObject(material))
            {
                // The versioned Editor layout is checked explicitly; a failed audit must not look compliant.
                SerializedProperty bindings = serialized.FindProperty("m_SavedProperties.m_TexEnvs");
                if (bindings == null || !bindings.isArray)
                    throw new InvalidOperationException("Cannot inspect saved texture bindings for " + material.name + ".");
                for (int i = 0; i < bindings.arraySize; i++)
                {
                    SerializedProperty entry = bindings.GetArrayElementAtIndex(i);
                    SerializedProperty name = entry.FindPropertyRelative("first");
                    SerializedProperty texture = entry.FindPropertyRelative("second.m_Texture");
                    if (name == null || name.propertyType != SerializedPropertyType.String ||
                        texture == null || texture.propertyType != SerializedPropertyType.ObjectReference)
                        throw new InvalidOperationException("Unsupported saved texture entry for " + material.name + ".");
                    result[name.stringValue] = texture.objectReferenceValue as Texture;
                }
            }
            return result;
        }

        /// <summary>
        /// Decides a texture's role. Binding first because it is authoritative,
        /// then the importer's own textureType, then the filename suffix, then
        /// Color as the generous default.
        /// </summary>
        public static Role RoleFor(string assetPath, TextureImporter importer,
                                   Dictionary<string, string> bindings,
                                   out string reason)
        {
            string prop;
            if (bindings != null && bindings.TryGetValue(assetPath, out prop))
            {
                Role bound;
                if (PropertyRoles.TryGetValue(prop, out bound))
                {
                    reason = "bound to " + prop;
                    return bound;
                }

                // Bound to a property this table does not know. Say so rather than
                // silently falling through - an unknown slot is a sign the table
                // needs a line adding, and the audit log is where that shows up.
                reason = "bound to unlisted property " + prop + ", treated as Color";
                return Role.Color;
            }

            if (importer != null && importer.textureType == TextureImporterType.NormalMap)
            {
                reason = "textureType NormalMap";
                return Role.Normal;
            }

            string name = System.IO.Path.GetFileNameWithoutExtension(assetPath);
            string[] parts = name.Split('_', '-');
            if (parts.Length > 1)
            {
                Role bySuffix;
                if (SuffixRoles.TryGetValue(parts[parts.Length - 1], out bySuffix))
                {
                    reason = "unreferenced, suffix _" + parts[parts.Length - 1];
                    return bySuffix;
                }
            }

            reason = "unreferenced, unrecognised name, treated as Color";
            return Role.Color;
        }

        // ---------------------------------------------------------------------
        //  Reporting
        // ---------------------------------------------------------------------

        /// <summary>
        /// Whether one texture already satisfies the policy. The single definition
        /// of "compliant" - Summarise, the audit count and UCSelfTest all ask here
        /// rather than each restating the condition and drifting apart.
        /// </summary>
        public static bool IsCompliant(Entry e)
        {
            if (e.PackedMaskSrgbWrong) return false;
            if (e.NormalTypeWrong) return false;
            if (!e.AndroidOverridden) return false;
            return e.AndroidBefore == e.Cap;
        }

        /// <summary>
        /// A short roll-up: how many of each role, and the estimated mobile
        /// texture memory before and after, using the ASTC 6x6 figures from the
        /// header. The estimate assumes each texture actually reaches its ceiling,
        /// which is the worst case and therefore the useful one to budget against.
        /// </summary>
        public static string Summarise(List<Entry> entries)
        {
            int color = 0, normal = 0, data = 0, notCompliant = 0, mistyped = 0;
            double before = 0.0, after = 0.0;

            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                if (e.Role == Role.Normal) normal++;
                else if (e.Role == Role.Data) data++;
                else color++;

                if (!IsCompliant(e)) notCompliant++;
                if (e.NormalTypeWrong) mistyped++;

                before += MegabytesAstc(e.AndroidBefore);
                after += MegabytesAstc(e.Cap);
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("  textures     : " + entries.Count +
                          "  (Color " + color + ", Normal " + normal + ", Data " + data + ")");
            sb.AppendLine("  ceilings     : Color " + CapColor + "px, Normal " + CapNormal +
                          "px, Data " + CapData + "px");
            sb.AppendLine("  needing work : " + notCompliant);
            sb.AppendLine("  mistyped nrm : " + mistyped +
                          "  (normal maps importing as plain colour)");
            sb.AppendLine("  mobile est.  : " + before.ToString("F1") + " MB -> " +
                          after.ToString("F1") + " MB  (ASTC 6x6, worst case, mips included)");
            return sb.ToString();
        }

        /// <summary>
        /// ASTC 6x6 cost of a square texture of this edge, mip chain included.
        /// 16 bytes per 36 pixels, times 4/3 for the mips.
        /// </summary>
        private static double MegabytesAstc(int edge)
        {
            if (edge <= 0) return 0.0;
            double pixels = (double)edge * edge;
            return pixels * (16.0 / 36.0) * (4.0 / 3.0) / (1024.0 * 1024.0);
        }
    }
}
