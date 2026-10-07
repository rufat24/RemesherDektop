// Quad Remesher Bridge for Unity
// Port of the Blender bridge by Maxime Rouca (Exoside).
//
// Locates the QuadRemesher engine (xremesh) and the license manager.
// Mirrors getQREngineFolder() / getEnginePath() / getLicenseManagerPath() from qr_operators.py.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Exoside.QuadRemesher
{
    internal static class QuadRemesherEngine
    {
        public const string Version = "1.3";

        /// <summary>
        /// Host applications the engine may have been installed under. The engine keeps its
        /// binaries, preferences and license under "Datas_&lt;HostApp&gt;", so whichever of these
        /// folders exists is both where xremesh lives and which HostApp name we must report.
        /// Blender is first because that is the install this bridge was ported from.
        /// </summary>
        static readonly string[] k_KnownHostApps =
        {
            "Blender", "Unity", "3dsMax", "Maya", "Modo", "Houdini", "Cinema4D", "Rhino", "ZBrush"
        };

        static bool isMacOS =>
            Application.platform == RuntimePlatform.OSXEditor;

        static bool isLinux =>
            Application.platform == RuntimePlatform.LinuxEditor;

        /// <summary>Root under which Exoside stores every "Datas_&lt;HostApp&gt;" folder.</summary>
        static string DatasRoot()
        {
            if (isMacOS)
                return "/Users/Shared/Exoside/QuadRemesher";
            if (isLinux)
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local/share/Exoside/QuadRemesher");

            // Windows: ALLUSERSPROFILE is C:\ProgramData
            var allUsers = Environment.GetEnvironmentVariable("ALLUSERSPROFILE");
            if (string.IsNullOrEmpty(allUsers))
                allUsers = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(allUsers, "Exoside/QuadRemesher");
        }

        /// <summary>Every folder that could hold an installed engine, best candidate first.</summary>
        public static IEnumerable<string> CandidateEngineFolders()
        {
            var over = QuadRemesherSettings.instance.engineFolderOverride;
            if (!string.IsNullOrEmpty(over))
                yield return over;

            var root = DatasRoot();
            foreach (var host in k_KnownHostApps)
                yield return Path.Combine(root, "Datas_" + host, "QuadRemesherEngine_" + Version);
        }

        /// <summary>
        /// The folder holding xremesh, or null when the engine is not installed anywhere we look.
        /// </summary>
        public static string ResolveEngineFolder()
        {
            foreach (var folder in CandidateEngineFolders())
            {
                if (!string.IsNullOrEmpty(folder) && File.Exists(EnginePathIn(folder)))
                    return UnixifyPath(folder);
            }
            return null;
        }

        static string EnginePathIn(string folder)
        {
            return Path.Combine(folder, (isMacOS || isLinux) ? "xremesh" : "xremesh.exe");
        }

        static string LicenseManagerPathIn(string folder)
        {
            if (isMacOS)
                return Path.Combine(folder, "xrLicenseManager.app/Contents/MacOS/xrLicenseManager");
            if (isLinux)
                return Path.Combine(folder, "xrLicenseManager");
            return Path.Combine(folder, "xrLicenseManager.exe");
        }

        /// <summary>Full path to xremesh(.exe), or null when the engine is not installed.</summary>
        public static string ResolveEnginePath()
        {
            var folder = ResolveEngineFolder();
            return folder == null ? null : UnixifyPath(EnginePathIn(folder));
        }

        /// <summary>Full path to xrLicenseManager(.exe), or null when the engine is not installed.</summary>
        public static string ResolveLicenseManagerPath()
        {
            var folder = ResolveEngineFolder();
            if (folder == null)
                return null;
            var path = LicenseManagerPathIn(folder);
            return File.Exists(path) ? UnixifyPath(path) : null;
        }

        /// <summary>
        /// The name to write as "HostApp=" in the settings file. The engine derives its
        /// preferences and license folder from it ("Datas_&lt;HostApp&gt;"), so it must name the
        /// install we are actually running out of — otherwise the engine looks for a license
        /// that isn't there and falls back to trial/activation.
        /// </summary>
        public static string ResolveHostApp()
        {
            var folder = ResolveEngineFolder();
            if (folder != null)
            {
                // .../Datas_Blender/QuadRemesherEngine_1.3 -> "Blender"
                var parent = Path.GetFileName(Path.GetDirectoryName(folder) ?? "");
                if (parent != null && parent.StartsWith("Datas_", StringComparison.Ordinal))
                {
                    var host = parent.Substring("Datas_".Length);
                    if (host.Length > 0)
                        return host;
                }
            }
            return "Blender";
        }

        public static bool IsInstalled => ResolveEnginePath() != null;

        /// <summary>The engine only ever accepts forward slashes in its settings file.</summary>
        public static string UnixifyPath(string path)
        {
            return path == null ? null : path.Replace('\\', '/');
        }

        /// <summary>
        /// Scratch folder used to hand meshes to the engine and read results back.
        /// Mirrors the Blender addon's "&lt;temp&gt;/Exoside/QuadRemesher/&lt;host&gt;".
        /// </summary>
        public static string TempFolder()
        {
            string root;
            if (isMacOS)
                root = "/var/tmp/Exoside";
            else if (isLinux)
                root = "/tmp/Exoside";
            else
                root = Path.Combine(Path.GetTempPath(), "Exoside");

            var path = UnixifyPath(Path.Combine(root, "QuadRemesher/Unity"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
