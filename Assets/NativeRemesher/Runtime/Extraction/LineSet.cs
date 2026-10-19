using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    /// <summary>
    /// A set of line segments with a nearest-point query: the quad layout's grid lines, which
    /// output vertices on them keep to while the rest of the mesh relaxes.
    /// </summary>
    public sealed class LineSet
    {
        readonly Vector3[] m_Ends;
        readonly float m_Size;
        readonly Dictionary<(int, int, int), List<int>> m_Bins = new Dictionary<(int, int, int), List<int>>();

        /// <param name="ends">Segment end points, 0-1, 2-3, ...</param>
        /// <param name="reach">The furthest any query looks; sets the bin size.</param>
        public LineSet(Vector3[] ends, float reach)
        {
            m_Ends = ends;
            m_Size = Mathf.Max(reach, 1e-12f);
            for (var s = 0; s + 1 < ends.Length; s += 2)
            {
                var a = ends[s];
                var b = ends[s + 1];
                var steps = Mathf.Clamp(Mathf.CeilToInt((b - a).magnitude / (0.5f * m_Size)), 1, 100000);
                (int, int, int) last = (int.MinValue, 0, 0);
                for (var k = 0; k <= steps; k++)
                {
                    var key = Key(Vector3.Lerp(a, b, k / (float)steps));
                    if (key.Equals(last)) continue;
                    last = key;
                    if (!m_Bins.TryGetValue(key, out var list)) m_Bins[key] = list = new List<int>(2);
                    if (list.Count == 0 || list[list.Count - 1] != s) list.Add(s);
                }
            }
        }

        (int, int, int) Key(Vector3 p) =>
            (Mathf.FloorToInt(p.x / m_Size), Mathf.FloorToInt(p.y / m_Size), Mathf.FloorToInt(p.z / m_Size));

        /// <summary>
        /// The nearest point on any segment within <paramref name="maxDistance"/> (at most the
        /// reach given at construction), and that segment's unit direction. False when none is.
        /// </summary>
        public bool Nearest(Vector3 p, float maxDistance, out Vector3 point, out Vector3 direction)
        {
            point = p;
            direction = Vector3.zero;
            var bestSq = maxDistance * maxDistance;
            var found = false;
            var (x, y, z) = Key(p);
            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
            for (var dz = -1; dz <= 1; dz++)
            {
                if (!m_Bins.TryGetValue((x + dx, y + dy, z + dz), out var list)) continue;
                foreach (var s in list)
                {
                    var a = m_Ends[s];
                    var ab = m_Ends[s + 1] - a;
                    var len = ab.sqrMagnitude;
                    if (len <= 0f) continue;
                    var t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / len);
                    var q = a + ab * t;
                    var d = (q - p).sqrMagnitude;
                    if (d > bestSq) continue;
                    bestSq = d;
                    point = q;
                    direction = ab / Mathf.Sqrt(len);
                    found = true;
                }
            }
            return found;
        }
    }
}
