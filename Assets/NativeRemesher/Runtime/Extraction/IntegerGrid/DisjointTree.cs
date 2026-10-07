// Union-find, twice: plain, and carrying a quarter-turn offset per link.
//
// Ports of QuadriFlow's DisajointTree and DisajointOrientTree (Huang et al. 2018, BSD-3). The
// orient variant is what turns a soup of per-face frames into one global frame: merging two faces
// across a shared edge records the rotation that makes their statements of that edge agree, and
// Orient(f) then reads off, for any face, the total rotation into its component's root frame.
// Cycles that cannot be made consistent — loops around a singularity — are simply never merged
// twice, so the holonomy survives on exactly the edges the flow later treats as valves.

using System.Collections.Generic;

namespace NativeRemesher.Extraction
{
    public sealed class DisjointTree
    {
        readonly int[] m_Parent;
        readonly int[] m_Rank;
        int[] m_Index;

        public DisjointTree(int count)
        {
            m_Parent = new int[count];
            m_Rank = new int[count];
            for (var i = 0; i < count; i++)
            {
                m_Parent[i] = i;
                m_Rank[i] = 1;
            }
        }

        public int count => m_Parent.Length;

        public int compactCount { get; private set; }

        public int Parent(int x)
        {
            var root = x;
            while (m_Parent[root] != root) root = m_Parent[root];
            while (m_Parent[x] != root)
            {
                var next = m_Parent[x];
                m_Parent[x] = root;
                x = next;
            }
            return root;
        }

        public void Merge(int x, int y)
        {
            var px = Parent(x);
            var py = Parent(y);
            if (px == py) return;

            if (m_Rank[px] < m_Rank[py])
            {
                m_Rank[py] += m_Rank[px];
                m_Parent[px] = py;
            }
            else
            {
                m_Rank[px] += m_Rank[py];
                m_Parent[py] = px;
            }
        }

        /// <summary>Numbers the roots consecutively so <see cref="Index"/> can be used as an array index.</summary>
        public void BuildCompactParent()
        {
            var compact = new int[m_Parent.Length];
            compactCount = 0;
            for (var i = 0; i < m_Parent.Length; i++)
                if (m_Parent[i] == i) compact[i] = compactCount++;

            m_Index = new int[m_Parent.Length];
            for (var i = 0; i < m_Parent.Length; i++)
                m_Index[i] = compact[Parent(i)];
        }

        public int Index(int x) => m_Index[x];
    }

    public sealed class DisjointOrientTree
    {
        readonly int[] m_Parent;
        readonly int[] m_Offset;
        readonly int[] m_Rank;
        readonly List<int> m_Path = new List<int>();

        public DisjointOrientTree(int count)
        {
            m_Parent = new int[count];
            m_Offset = new int[count];
            m_Rank = new int[count];
            for (var i = 0; i < count; i++)
            {
                m_Parent[i] = i;
                m_Rank[i] = 1;
            }
        }

        /// <summary>Root of <paramref name="j"/>, compressing the path and folding the offsets along it.</summary>
        public int Parent(int j)
        {
            var root = j;
            m_Path.Clear();
            while (m_Parent[root] != root)
            {
                m_Path.Add(root);
                root = m_Parent[root];
            }

            // Nearest-to-root first, so a node's parent is already pointing at the root with its
            // offset folded by the time the node itself is handled.
            for (var i = m_Path.Count - 1; i >= 0; i--)
            {
                var node = m_Path[i];
                var parent = m_Parent[node];
                if (parent != root) m_Offset[node] = (m_Offset[node] + m_Offset[parent]) % 4;
                m_Parent[node] = root;
            }

            return root;
        }

        /// <summary>Quarter turns from <paramref name="j"/>'s own frame into its root's frame.</summary>
        public int Orient(int j)
        {
            var root = Parent(j);
            return j == root ? m_Offset[j] : (m_Offset[j] + m_Offset[root]) % 4;
        }

        public void Merge(int v0, int v1, int orient0, int orient1)
        {
            var p0 = Parent(v0);
            var p1 = Parent(v1);
            if (p0 == p1) return;

            var orientP0 = Orient(v0);
            var orientP1 = Orient(v1);

            if (m_Rank[p1] < m_Rank[p0])
            {
                m_Rank[p0] += m_Rank[p1];
                m_Parent[p1] = p0;
                m_Offset[p1] = (orient1 - orient0 + orientP0 - orientP1 + 8) % 4;
            }
            else
            {
                m_Rank[p1] += m_Rank[p0];
                m_Parent[p0] = p1;
                m_Offset[p0] = (orient0 - orient1 + orientP1 - orientP0 + 8) % 4;
            }
        }
    }
}
