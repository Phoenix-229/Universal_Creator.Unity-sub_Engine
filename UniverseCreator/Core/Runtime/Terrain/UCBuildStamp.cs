// -----------------------------------------------------------------------------
//  Universe Creator - Build-currency guard
//  File:    UCBuildStamp.cs
//  Purpose: Prove, on every world regeneration, WHICH compiled assembly is
//           actually running. If you edit code, recompile, and the build time
//           logged here does not advance, the editor is still executing an OLDER
//           compile (focus Unity to force a recompile, or a compile error in the
//           Console is blocking it). This is the one-glance answer to
//           "is the editor running my latest code, or an abandoned old build?"
// -----------------------------------------------------------------------------

using System.IO;
using System.Reflection;
using UnityEngine;

namespace UniverseCreator
{
    public static class UCBuildStamp
    {
        // Bump this with any change you want to confirm reached the running world.
        // If the Console does not show this exact tag after you regenerate, the
        // build you are looking at is NOT the one running.
        public const string BUILD_TAG = "2026-09-28 build-guard + river-descent(wip)";

        static string _built;

        /// <summary>Real last-write time of the running UniverseCreator assembly.</summary>
        public static string BuiltAt()
        {
            if (_built != null) return _built;
            string built = "unknown (in-memory assembly)";
            try
            {
                Assembly asm = typeof(UCBuildStamp).Assembly;
                string loc = asm.Location;
                if (!string.IsNullOrEmpty(loc) && File.Exists(loc))
                    built = File.GetLastWriteTime(loc).ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch { /* headless / restricted: leave the fallback text */ }
            _built = built;
            return built;
        }

        /// <summary>One line, logged at the start of each regeneration.</summary>
        public static void LogOnce()
        {
            Debug.Log("[UC BUILD] UniverseCreator.dll built " + BuiltAt() +
                      "  |  tag: " + BUILD_TAG +
                      "\n         If you edited code and this build time did NOT advance after recompiling, " +
                      "the editor is running an OLDER compile - focus the Unity window to recompile, " +
                      "or check the Console for a compile error that is blocking it.");
        }
    }
}
