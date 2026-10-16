using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher
{
    /// <summary>
    /// Areas the user marked for more (or fewer) quads, as brush dabs in the mesh's own space.
    /// </summary>
    /// <remarks>
    /// Dabs rather than per-vertex weights so the paint is independent of any one mesh: it can be
    /// laid on the remeshed result the user is looking at and read back on the source, the
    /// decimated solve mesh and the refined one, which share the space and nothing else. The value
    /// at a point is the sum of the dabs reaching it, each falling off smoothly to its rim, clamped
    /// to [-1, 1]; the lattice spacing there is multiplied by <see cref="range"/> to the minus that
    /// value -- a third of the cell where it is fully painted, three times it where fully painted
    /// for less.
    /// </remarks>
    [Serializable]
    public sealed class DensityPaint
    {
        [Serializable]
        public struct Dab
        {
            public Vector3 centre;
            public float radius;

            /// <summary>Positive asks for more quads, negative for fewer.</summary>
            public float strength;
        }

        public List<Dab> dabs = new List<Dab>();

        /// <summary>Cell size over the painted one at full strength.</summary>
        public static float range = 3f;

        public bool isEmpty => dabs == null || dabs.Count == 0;

        // A plane the paint is read symmetrically about (Symmetry): the value at a point is the
        // stronger of its own and its mirror image's. Axis -1: none.
        [NonSerialized] int m_MirrorAxis = -1;
        [NonSerialized] float m_MirrorPlane;

        // A uniform grid over the dabs, built on first query.
        Dictionary<long, List<int>> m_Cells;
        float m_CellSize;
        int m_BuiltFor = -1;

        /// <summary>Call after changing <see cref="dabs"/>, so the next query rebuilds its index.</summary>
        public void Invalidate() => m_BuiltFor = -1;

        void EnsureBuilt()
        {
            if (m_BuiltFor == dabs.Count && m_Cells != null) return;
            lock (dabs) Build();
        }

        void Build()
        {
            if (m_BuiltFor == dabs.Count && m_Cells != null) return;
            m_Cells = new Dictionary<long, List<int>>();
            var largest = 0f;
            foreach (var d in dabs) largest = Mathf.Max(largest, d.radius);
            m_CellSize = Mathf.Max(largest, 1e-6f);
            for (var i = 0; i < dabs.Count; i++)
            {
                var key = Key(Cell(dabs[i].centre));
                if (!m_Cells.TryGetValue(key, out var list)) m_Cells[key] = list = new List<int>();
                list.Add(i);
            }
            m_BuiltFor = dabs.Count;
        }

        Vector3Int Cell(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x / m_CellSize),
            Mathf.FloorToInt(p.y / m_CellSize), Mathf.FloorToInt(p.z / m_CellSize));

        static long Key(Vector3Int c) => ((long)(c.x & 0x1FFFFF) << 42) | ((long)(c.y & 0x1FFFFF) << 21) | (long)(c.z & 0x1FFFFF);

        /// <summary>Smooth falloff: 1 at the centre, 0 at the rim, flat at both.</summary>
        public static float Falloff(float distance, float radius)
        {
            if (radius <= 0f || distance >= radius) return 0f;
            var x = distance / radius;
            var s = 1f - x * x;
            return s * s;
        }

        /// <summary>The painted value at a point, in [-1, 1]; 0 where nothing was painted.</summary>
        public float ValueAt(Vector3 p)
        {
            if (isEmpty) return 0f;
            EnsureBuilt();
            var value = Clamped(p);
            if (m_MirrorAxis < 0) return value;
            var q = p;
            q[m_MirrorAxis] = 2f * m_MirrorPlane - q[m_MirrorAxis];
            var mirrored = Clamped(q);
            return Mathf.Abs(mirrored) > Mathf.Abs(value) ? mirrored : value;
        }

        float Clamped(Vector3 p)
        {
            var c = Cell(p);
            var sum = 0f;
            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
            for (var dz = -1; dz <= 1; dz++)
            {
                if (!m_Cells.TryGetValue(Key(new Vector3Int(c.x + dx, c.y + dy, c.z + dz)), out var list)) continue;
                foreach (var i in list)
                {
                    var d = dabs[i];
                    sum += d.strength * Falloff((p - d.centre).magnitude, d.radius);
                }
            }
            return Mathf.Clamp(sum, -1f, 1f);
        }

        /// <summary>Spacing multiplier at a point: 1 unpainted, 1/range fully painted for detail.</summary>
        public float SpacingFactorAt(Vector3 p)
        {
            var v = ValueAt(p);
            return v == 0f ? 1f : Mathf.Pow(Mathf.Max(1f, range), -v);
        }

        /// <summary>Spacing multipliers for many points; null when nothing is painted.</summary>
        public float[] SpacingFactors(Vector3[] positions)
        {
            if (isEmpty || positions == null) return null;
            EnsureBuilt();
            var factors = new float[positions.Length];
            var any = false;
            System.Threading.Tasks.Parallel.For(0, positions.Length, BackgroundScheduler.Options, i =>
            {
                factors[i] = SpacingFactorAt(positions[i]);
            });
            foreach (var f in factors)
                if (f != 1f) { any = true; break; }
            return any ? factors : null;
        }

        /// <summary>Removes the dabs whose centres lie within <paramref name="radius"/> of a point.</summary>
        public int EraseNear(Vector3 p, float radius)
        {
            var removed = dabs.RemoveAll(d => (d.centre - p).magnitude <= radius);
            if (removed > 0) Invalidate();
            return removed;
        }

        public DensityPaint Clone() => new DensityPaint
        {
            dabs = new List<Dab>(dabs), m_MirrorAxis = m_MirrorAxis, m_MirrorPlane = m_MirrorPlane
        };

        /// <summary>
        /// A copy read symmetrically about the plane where coordinate <paramref name="axis"/> (0-2)
        /// is <paramref name="plane"/>: what is painted on either side counts on both. Symmetry
        /// solves one half, and paint on the other was lost.
        /// </summary>
        public DensityPaint Mirrored(int axis, float plane)
        {
            var copy = Clone();
            copy.m_MirrorAxis = axis;
            copy.m_MirrorPlane = plane;
            return copy;
        }
    }
}
