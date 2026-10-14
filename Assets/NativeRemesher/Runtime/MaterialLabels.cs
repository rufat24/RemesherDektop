// Materials, as QuadRemesher's "Use Materials": the borders between the input's submeshes are
// laid as edge loops, and every output face keeps the material it lies on.
//
// The label of any triangle or face derived from the input -- decimated, refined, remeshed -- is
// the label of the input triangle nearest its centre. The decimator keeps the label borders as
// chains of input border vertices (see PreDecimate), so a derived triangle lies on one side of
// them and its centre says which.

using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher
{
    public sealed class MaterialLabels
    {
        readonly SpatialGrid m_Grid;
        readonly int[] m_Labels;
        readonly int[] m_Triangles;

        /// <summary>Distinct labels on the input.</summary>
        public readonly int labelCount;

        /// <summary>Highest label + 1: the number of submeshes an output needs.</summary>
        public readonly int slotCount;

        public MaterialLabels(Vector3[] positions, int[] triangles, int[] triangleLabels)
        {
            m_Grid = new SpatialGrid(positions, triangles);
            m_Labels = triangleLabels;
            m_Triangles = triangles;
            var seen = new HashSet<int>();
            foreach (var l in triangleLabels) seen.Add(l);
            labelCount = seen.Count;
            foreach (var l in seen) slotCount = Mathf.Max(slotCount, l + 1);
        }

        /// <summary>The label of the input triangle nearest <paramref name="point"/>.</summary>
        public int At(Vector3 point)
        {
            m_Grid.NearestDistance(point, out var t);
            return t >= 0 && t < m_Labels.Length ? m_Labels[t] : 0;
        }

        public int[] ForTriangles(Vector3[] positions, int[] triangles)
        {
            var labels = new int[triangles.Length / 3];
            System.Threading.Tasks.Parallel.For(0, labels.Length, BackgroundScheduler.Options, t =>
                labels[t] = At((positions[triangles[3 * t]] + positions[triangles[3 * t + 1]]
                                + positions[triangles[3 * t + 2]]) / 3f));
            return labels;
        }

        public int[] ForFaces(Vector3[] positions, List<int[]> faces)
        {
            var labels = new int[faces.Count];
            System.Threading.Tasks.Parallel.For(0, faces.Count, BackgroundScheduler.Options, i =>
            {
                var f = faces[i];
                var c = Vector3.zero;
                foreach (var v in f) c += positions[v];
                labels[i] = At(c / Mathf.Max(1, f.Length));
            });
            return labels;
        }

        /// <summary>
        /// Labels for a topology built from <paramref name="sourceTriangles"/>: read straight off
        /// when those are the labelled input itself, looked up otherwise.
        /// </summary>
        public int[] ForTopology(RemeshTopology topology, int[] sourceTriangles)
        {
            if (ReferenceEquals(sourceTriangles, m_Triangles) && topology.sourceTriangle != null)
            {
                var labels = new int[topology.sourceTriangle.Length];
                for (var t = 0; t < labels.Length; t++) labels[t] = m_Labels[topology.sourceTriangle[t]];
                return labels;
            }
            return ForTriangles(topology.positions, topology.triangles);
        }

        /// <summary>The edges between two triangles of different labels, each once.</summary>
        public static List<(int a, int b)> BorderEdges(int[] triangles, int[] labels)
        {
            var first = new Dictionary<long, int>(triangles.Length, Extraction.EdgeKey.Comparer);
            var border = new HashSet<long>(Extraction.EdgeKey.Comparer);
            var edges = new List<(int a, int b)>();
            for (var t = 0; t < triangles.Length / 3; t++)
                for (var k = 0; k < 3; k++)
                {
                    int a = triangles[3 * t + k], b = triangles[3 * t + (k + 1) % 3];
                    var key = ((long)Mathf.Min(a, b) << 32) | (uint)Mathf.Max(a, b);
                    if (!first.TryGetValue(key, out var other)) { first[key] = t; continue; }
                    if (labels[other] != labels[t] && border.Add(key)) edges.Add((a, b));
                }
            return edges;
        }
    }
}
