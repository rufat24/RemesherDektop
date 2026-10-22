// Paint Fix Areas: a brush in the Scene view that marks where the remesh needs more quads (or
// fewer), kept on the source object and read by the next run. Paint on the result you are looking
// at or on the source -- they share the space -- then press Remesh again.

using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace NativeRemesher
{
    internal sealed partial class NativeRemesherWindow
    {
        enum PaintMode { MoreDetail, LessDetail, Erase }

        [SerializeField] PaintMode m_PaintMode = PaintMode.MoreDetail;
        [SerializeField] float m_BrushSizePercent = 3f;
        [SerializeField] float m_BrushStrength = 0.5f;

        bool m_Painting;

        // The mesh painted on (the object in view) and what was built for it.
        Mesh m_PaintMesh;
        SpatialGrid m_PaintGrid;
        Vector3[] m_PaintVertices;
        Vector3[] m_PaintNormals;
        int[] m_PaintTriangles;
        Mesh m_Overlay;
        Color32[] m_OverlayColors;
        Material m_OverlayMaterial;

        Vector3 m_LastDab;
        bool m_HasLastDab;
        bool m_HoverHit;
        Vector3 m_HoverPoint;
        Vector3 m_HoverNormal;

        /// <summary>The object a result was made from, or the object itself.</summary>
        static GameObject SourceOf(GameObject go)
        {
            if (go == null) return null;
            var link = go.GetComponent<RemeshSource>();
            return link != null && link.source != null ? link.source : go;
        }

        /// <summary>The selected object with a mesh (itself or the first child that has one).</summary>
        static GameObject PaintSurface(out Mesh mesh)
        {
            mesh = null;
            var selected = Selection.activeGameObject;
            if (selected == null) return null;
            mesh = MeshOn(selected);
            if (mesh != null) return selected;
            foreach (var child in selected.GetComponentsInChildren<Transform>(true))
            {
                mesh = MeshOn(child.gameObject);
                if (mesh != null) return child.gameObject;
            }
            return null;
        }

        /// <summary>The paint on a source, read for a run: a copy, so painting on can go on meanwhile.</summary>
        static DensityPaint PaintFor(GameObject source)
        {
            var comp = source != null ? source.GetComponent<RemeshPaint>() : null;
            return comp != null && comp.paint != null && !comp.paint.isEmpty ? comp.paint.Clone() : null;
        }

        void DrawPaintGUI(GameObject source)
        {
            EditorGUILayout.LabelField("Brush over what came out wrong, then remesh again: painted areas get " +
                                       "smaller quads.", EditorStyles.wordWrappedMiniLabel);
            var comp = source != null ? source.GetComponent<RemeshPaint>() : null;
            var dabs = comp != null && comp.paint != null ? comp.paint.dabs.Count : 0;

            using (new EditorGUI.DisabledScope(source == null))
            {
                var painting = GUILayout.Toggle(m_Painting,
                    new GUIContent(m_Painting ? "Stop Painting" : "Paint Fix Areas",
                        "Brush over the parts that came out wrong -- a rim, a strap, a buckle -- in the " +
                        "Scene view, on the result or on the source, then press Remesh again: painted " +
                        "areas get smaller quads (up to " + DensityPaint.range.ToString("0.#") + "x " +
                        "finer) and the target grows by what they add. Less Detail paints the other way."),
                    "Button", GUILayout.Height(24));
                if (painting != m_Painting) SetPainting(painting);
            }

            if (m_Painting)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    m_PaintMode = (PaintMode)GUILayout.Toolbar((int)m_PaintMode,
                        new[] { "More Detail", "Less Detail", "Erase" });
                    m_BrushSizePercent = EditorGUILayout.Slider(
                        new GUIContent("Brush Size %", "Radius, as a percentage of the object's size."),
                        m_BrushSizePercent, 0.25f, 25f);
                    m_BrushStrength = EditorGUILayout.Slider(
                        new GUIContent("Strength", "How much one pass adds. Strokes add up to full strength."),
                        m_BrushStrength, 0.05f, 1f);
                    EditorGUILayout.HelpBox("Drag on the mesh in the Scene view. Shift erases, Ctrl paints " +
                                            "Less Detail, Esc stops. Alt still orbits.", MessageType.None);
                }
            }

            if (source != null && dabs > 0)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"{dabs:N0} dabs painted on '{source.name}'", EditorStyles.miniLabel);
                    if (GUILayout.Button("Clear Paint", GUILayout.Width(90)))
                    {
                        Undo.RecordObject(comp, "Clear Remesh Paint");
                        comp.paint.dabs.Clear();
                        comp.paint.Invalidate();
                        EditorUtility.SetDirty(comp);
                        RefreshOverlay(true);
                        SceneView.RepaintAll();
                    }
                }
            }
        }

        // Undo and redo put the dabs back without going through Dab: the paint's index (built for
        // the dab count it saw) and the overlay's colours are rebuilt, or both show the strokes
        // that were undone until the next one is laid.
        void OnPaintUndoRedo()
        {
            var surface = PaintSurface(out _);
            var source = SourceOf(surface);
            var comp = source != null ? source.GetComponent<RemeshPaint>() : null;
            comp?.paint?.Invalidate();
            if (m_Painting) RefreshOverlay(true);
            SceneView.RepaintAll();
            Repaint();
        }

        void SetPainting(bool on)
        {
            m_Painting = on;
            m_HasLastDab = false;
            if (!on) ReleasePaintMesh();
            SceneView.RepaintAll();
            Repaint();
        }

        void ReleasePaintMesh()
        {
            m_PaintMesh = null;
            m_PaintGrid = null;
            m_PaintVertices = null;
            m_PaintNormals = null;
            m_PaintTriangles = null;
            if (m_Overlay != null) DestroyImmediate(m_Overlay);
            m_Overlay = null;
            m_OverlayColors = null;
        }

        /// <summary>Indexes the mesh in view for picking and builds its overlay, once per mesh.</summary>
        bool PreparePaintMesh(Mesh mesh)
        {
            if (mesh == m_PaintMesh && m_PaintGrid != null) return true;
            ReleasePaintMesh();
            if (mesh == null || !mesh.isReadable) return false;
            RemeshTopology.ReadMesh(mesh, out var vertices, out var triangles);
            if (vertices.Length == 0 || triangles.Length == 0) return false;
            m_PaintMesh = mesh;
            m_PaintVertices = vertices;
            m_PaintTriangles = triangles;
            m_PaintGrid = new SpatialGrid(vertices, triangles);
            var normals = mesh.normals;
            if (normals == null || normals.Length != vertices.Length)
                normals = Extraction.NormalRecalculator.Compute(ToFaces(triangles), vertices, null);
            m_PaintNormals = normals;

            // The overlay: the same surface lifted a hair off it, coloured by the paint.
            var lift = mesh.bounds.size.magnitude * 0.0006f;
            var lifted = new Vector3[vertices.Length];
            for (var i = 0; i < lifted.Length; i++) lifted[i] = vertices[i] + normals[i] * lift;
            m_Overlay = new Mesh { name = "Remesh Paint Overlay", hideFlags = HideFlags.HideAndDontSave };
            m_Overlay.indexFormat = vertices.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            m_Overlay.vertices = lifted;
            m_Overlay.SetTriangles(triangles, 0);
            m_OverlayColors = new Color32[vertices.Length];
            RefreshOverlay(true);
            return true;
        }

        static System.Collections.Generic.List<int[]> ToFaces(int[] triangles)
        {
            var faces = new System.Collections.Generic.List<int[]>(triangles.Length / 3);
            for (var t = 0; t + 2 < triangles.Length; t += 3)
                faces.Add(new[] { triangles[t], triangles[t + 1], triangles[t + 2] });
            return faces;
        }

        static Color32 PaintColour(float value)
        {
            if (value > 0f) return new Color32(255, 110, 20, (byte)(40 + 170 * Mathf.Clamp01(value)));
            if (value < 0f) return new Color32(40, 140, 255, (byte)(40 + 170 * Mathf.Clamp01(-value)));
            return new Color32(0, 0, 0, 0);
        }

        /// <summary>
        /// Recolours the overlay from the paint: everywhere, or only round a point (a dab just laid).
        /// </summary>
        void RefreshOverlay(bool all, Vector3 near = default, float radius = 0f)
        {
            if (m_Overlay == null || m_PaintVertices == null) return;
            var surface = PaintSurface(out _);
            var source = SourceOf(surface);
            var comp = source != null ? source.GetComponent<RemeshPaint>() : null;
            var paint = comp != null ? comp.paint : null;
            var toSource = source != null && surface != null
                ? source.transform.worldToLocalMatrix * surface.transform.localToWorldMatrix
                : Matrix4x4.identity;
            var vertices = m_PaintVertices;
            var colours = m_OverlayColors;
            var r2 = radius * radius;
            var nearLocal = near;
            Parallel.For(0, vertices.Length, i =>
            {
                if (!all && (vertices[i] - nearLocal).sqrMagnitude > r2) return;
                var value = paint != null && !paint.isEmpty ? paint.ValueAt(toSource.MultiplyPoint3x4(vertices[i])) : 0f;
                colours[i] = PaintColour(value);
            });
            m_Overlay.colors32 = colours;
        }

        Material OverlayMaterial()
        {
            if (m_OverlayMaterial != null) return m_OverlayMaterial;
            m_OverlayMaterial = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
            m_OverlayMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            m_OverlayMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            m_OverlayMaterial.SetInt("_Cull", (int)CullMode.Back);
            m_OverlayMaterial.SetInt("_ZWrite", 0);
            m_OverlayMaterial.SetInt("_ZTest", (int)CompareFunction.LessEqual);
            m_OverlayMaterial.SetFloat("_ZBias", -1f);
            return m_OverlayMaterial;
        }

        /// <summary>
        /// The front-facing surface hit first along a local-space ray, or false. The grid's raycast
        /// returns the hit nearest the middle of a segment, so the segment is halved toward the
        /// camera until nothing nearer remains.
        /// </summary>
        bool PickFirst(Vector3 origin, Vector3 direction, out Vector3 point, out Vector3 normal)
        {
            point = normal = Vector3.zero;
            var bounds = m_PaintMesh.bounds;
            bounds.Expand(bounds.size.magnitude * 0.01f);
            if (!bounds.IntersectRay(new Ray(origin, direction), out var enter)) return false;
            var entry = origin + direction * Mathf.Max(0f, enter);
            var length = bounds.size.magnitude;
            var found = false;
            var hitAlong = 0f;
            var hitTriangle = -1;
            for (var facing = -1f; facing <= 0f && !found; facing += 1f)
            {
                var span = length;
                for (var guard = 0; guard < 24 && span > length * 1e-6f; guard++)
                {
                    var half = span * 0.5f;
                    if (!m_PaintGrid.Raycast(entry + direction * half, direction, half, facing,
                            out var t, out var tri, out _, out _))
                        break;
                    found = true;
                    hitAlong = half + t;
                    hitTriangle = tri;
                    span = hitAlong - length * 1e-5f;
                }
            }
            if (!found) return false;
            point = entry + direction * hitAlong;
            m_PaintGrid.TriangleCorners(hitTriangle, out var a, out var b, out var c);
            normal = Vector3.Cross(m_PaintVertices[b] - m_PaintVertices[a], m_PaintVertices[c] - m_PaintVertices[a]).normalized;
            if (Vector3.Dot(normal, direction) > 0f) normal = -normal;
            return true;
        }

        void PaintSceneGUI(SceneView view)
        {
            var surface = PaintSurface(out var mesh);
            var source = SourceOf(surface);
            if (surface == null || source == null || !PreparePaintMesh(mesh)) return;

            var e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                SetPainting(false);
                e.Use();
                return;
            }

            var id = GUIUtility.GetControlID(FocusType.Passive);
            if (e.type == EventType.Layout) HandleUtility.AddDefaultControl(id);

            var toLocal = surface.transform.worldToLocalMatrix;
            var toWorld = surface.transform.localToWorldMatrix;
            var radiusLocal = m_PaintMesh.bounds.size.magnitude * m_BrushSizePercent / 100f;

            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag || e.type == EventType.MouseDown)
            {
                var ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                m_HoverHit = PickFirst(toLocal.MultiplyPoint3x4(ray.origin),
                    toLocal.MultiplyVector(ray.direction).normalized, out m_HoverPoint, out m_HoverNormal);
            }

            var mode = e.shift ? PaintMode.Erase : e.control ? PaintMode.LessDetail : m_PaintMode;
            var painting = !e.alt && e.button == 0
                           && (e.type == EventType.MouseDown || e.type == EventType.MouseDrag);
            if (painting && m_HoverHit)
            {
                if (e.type == EventType.MouseDown)
                {
                    GUIUtility.hotControl = id;
                    Undo.IncrementCurrentGroup();
                    m_HasLastDab = false;
                }
                if (!m_HasLastDab || (m_HoverPoint - m_LastDab).magnitude >= radiusLocal * 0.3f)
                {
                    Dab(surface, source, mode, radiusLocal);
                    m_LastDab = m_HoverPoint;
                    m_HasLastDab = true;
                }
                e.Use();
            }
            else if (e.type == EventType.MouseUp && e.button == 0 && (m_HasLastDab || GUIUtility.hotControl == id))
            {
                if (GUIUtility.hotControl == id) GUIUtility.hotControl = 0;
                Undo.CollapseUndoOperations(Undo.GetCurrentGroup());
                m_HasLastDab = false;
                e.Use();
                Repaint();
            }

            if (e.type == EventType.Repaint)
            {
                var material = OverlayMaterial();
                material.SetPass(0);
                Graphics.DrawMeshNow(m_Overlay, toWorld);
                if (m_HoverHit)
                {
                    Handles.color = mode == PaintMode.Erase ? Color.white
                        : mode == PaintMode.LessDetail ? new Color(0.3f, 0.6f, 1f) : new Color(1f, 0.5f, 0.1f);
                    var centre = toWorld.MultiplyPoint3x4(m_HoverPoint);
                    var normal = toWorld.MultiplyVector(m_HoverNormal).normalized;
                    var radius = toWorld.MultiplyVector(Vector3.right * radiusLocal).magnitude;
                    Handles.DrawWireDisc(centre, normal, radius, 2f);
                    Handles.DrawLine(centre, centre + normal * radius * 0.3f);
                }
            }
            if (e.type == EventType.MouseMove) view.Repaint();
        }

        void Dab(GameObject surface, GameObject source, PaintMode mode, float radiusLocal)
        {
            var comp = source.GetComponent<RemeshPaint>();
            if (comp == null) comp = Undo.AddComponent<RemeshPaint>(source);
            Undo.RecordObject(comp, mode == PaintMode.Erase ? "Erase Remesh Paint" : "Remesh Paint");
            comp.paint ??= new DensityPaint();

            // Into the source's space, where the run reads it.
            var toSource = source.transform.worldToLocalMatrix * surface.transform.localToWorldMatrix;
            var centre = toSource.MultiplyPoint3x4(m_HoverPoint);
            var radius = toSource.MultiplyVector(Vector3.right * radiusLocal).magnitude;
            if (mode == PaintMode.Erase)
            {
                if (comp.paint.EraseNear(centre, radius) == 0) return;
            }
            else
            {
                comp.paint.dabs.Add(new DensityPaint.Dab
                {
                    centre = centre,
                    radius = radius,
                    strength = mode == PaintMode.LessDetail ? -m_BrushStrength : m_BrushStrength,
                });
                comp.paint.Invalidate();
            }
            EditorUtility.SetDirty(comp);

            // Every overlay vertex the change can reach: this brush plus the largest dab it removed.
            var reach = radiusLocal * (mode == PaintMode.Erase ? 2.5f : 1.01f);
            if (mode == PaintMode.Erase) RefreshOverlay(true);
            else RefreshOverlay(false, m_HoverPoint, reach);
            Repaint();
        }
    }
}
