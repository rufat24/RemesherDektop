// Quad Remesher Bridge for Unity
// Port of the Blender bridge by Maxime Rouca (Exoside).
//
// This file mirrors QRSettingsPropertyGroup from the Blender addon's __init__.py.
// The Blender addon stores its settings on the Scene; a Unity scene is not a good
// home for tool settings, so they live in UserSettings/ instead (per project, not
// versioned, which is what ScriptableSingleton's ProjectFolder location gives us).

using UnityEditor;
using UnityEngine;

namespace Exoside.QuadRemesher
{
    [FilePath("UserSettings/QuadRemesherSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class QuadRemesherSettings : ScriptableSingleton<QuadRemesherSettings>
    {
        // ---- Tooltips (copied verbatim from the Blender addon) ----
        public const string CurvatureAdaptivnessTooltip =
            "Allows to control how quad's size locally adapts to curvatures.\n" +
            "The higher it is, the smaller the quads will be on high curvature areas.\n" +
            "Set it at 0, to get uniform quads size";

        public const string AdaptQuadCountTooltip =
            "Adaptive Quad-Count :\n" +
            "On: Creates more polygons than asked to fit high curvatures area.\n" +
            "Off (default): Respect the Target-Quad-Count more exactly.\n" +
            "It's advised to let it 'Off' to better respect the Target-Quad-Count.";

        public const string UseMaterialsTooltip =
            "If On, QuadRemesher will use existing 'Materials' to guide the quad remeshing on " +
            "Materials borders.\nMaterialIds will be maintained after remeshing.\n" +
            "In Unity, one submesh = one material id.";

        public const string UseNormalsTooltip =
            "TAKE CARE: this option is useful in specific cases, BUT should be 'Off' by default " +
            "(facetted mesh). Read the doc for more information...\n" +
            "If On, QuadRemesher will use the existing 'Normals' to guide the remeshing on edge " +
            "loops where the normals are split/creased.\n" +
            "On smooth organic shapes, it's advised to disable it.";

        public const string DetectHardEdgesTooltip =
            "If On, QuadRemesher will detect/compute Hard-Edges automatically based on the geometry " +
            "(using a mix of edge's angles and other geometrical considerations).\n" +
            "If 'Use Normals Splitting' is checked, it's often better to uncheck " +
            "'Detect Hard Edges by angle'.\nOn smooth organic shapes, it's advised to disable it.";

        public const string SymmetryTooltip =
            "These options allow to perform symmetrical quad remeshing. " +
            "It's possible to combine all 3 symmetry axis.\n" +
            "The axis are the object's LOCAL axis.";

        public const string HideInputTooltip =
            "If On (default), the input object will be deactivated after remeshing.";

        // ---- Remeshing settings (defaults match the Blender addon exactly) ----

        [SerializeField] int m_TargetCount = 5000;
        [SerializeField] float m_AdaptiveSize = 50f;
        [SerializeField] bool m_AdaptQuadCount = true;

        [SerializeField] bool m_UseMaterials;
        [SerializeField] bool m_UseNormals;
        [SerializeField] bool m_AutoDetectHardEdges = true;

        [SerializeField] bool m_SymmetryX;
        [SerializeField] bool m_SymmetryY;
        [SerializeField] bool m_SymmetryZ;

        [SerializeField] bool m_HideInput = true;

        // ---- Unity-specific settings (no Blender equivalent) ----

        [SerializeField] string m_EngineFolderOverride = "";
        [SerializeField] string m_OutputFolder = "Assets/QuadRemesher/Output";

        public int targetCount
        {
            get => m_TargetCount;
            set => Set(ref m_TargetCount, Mathf.Max(1, value));
        }

        /// <summary>Curvature adaptivity, as a percentage in [0, 100].</summary>
        public float adaptiveSize
        {
            get => m_AdaptiveSize;
            set => Set(ref m_AdaptiveSize, Mathf.Clamp(value, 0f, 100f));
        }

        public bool adaptQuadCount
        {
            get => m_AdaptQuadCount;
            set => Set(ref m_AdaptQuadCount, value);
        }

        public bool useMaterials
        {
            get => m_UseMaterials;
            set => Set(ref m_UseMaterials, value);
        }

        public bool useNormals
        {
            get => m_UseNormals;
            set => Set(ref m_UseNormals, value);
        }

        public bool autoDetectHardEdges
        {
            get => m_AutoDetectHardEdges;
            set => Set(ref m_AutoDetectHardEdges, value);
        }

        public bool symmetryX
        {
            get => m_SymmetryX;
            set => Set(ref m_SymmetryX, value);
        }

        public bool symmetryY
        {
            get => m_SymmetryY;
            set => Set(ref m_SymmetryY, value);
        }

        public bool symmetryZ
        {
            get => m_SymmetryZ;
            set => Set(ref m_SymmetryZ, value);
        }

        public bool hideInput
        {
            get => m_HideInput;
            set => Set(ref m_HideInput, value);
        }

        /// <summary>
        /// Explicit path to the folder holding xremesh.exe. Empty means "auto-detect",
        /// see <see cref="QuadRemesherEngine.ResolveEngineFolder"/>.
        /// </summary>
        public string engineFolderOverride
        {
            get => m_EngineFolderOverride;
            set => Set(ref m_EngineFolderOverride, value ?? "");
        }

        /// <summary>Project-relative folder the retopo meshes are saved into.</summary>
        public string outputFolder
        {
            get => string.IsNullOrEmpty(m_OutputFolder) ? "Assets/QuadRemesher/Output" : m_OutputFolder;
            set => Set(ref m_OutputFolder, value);
        }

        /// <summary>Restores every remeshing setting to its default (Blender: "Reset Settings").</summary>
        public void ResetToDefaults()
        {
            m_TargetCount = 5000;
            m_AdaptiveSize = 50f;
            m_AdaptQuadCount = true;

            m_UseMaterials = false;
            m_UseNormals = false;
            m_AutoDetectHardEdges = true;

            m_SymmetryX = false;
            m_SymmetryY = false;
            m_SymmetryZ = false;

            m_HideInput = true;

            Save(true);
        }

        void Set<T>(ref T field, T value)
        {
            if (Equals(field, value))
                return;
            field = value;
            Save(true);
        }
    }
}
