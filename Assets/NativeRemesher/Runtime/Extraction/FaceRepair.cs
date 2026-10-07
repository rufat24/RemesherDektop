// Removes the faces that cannot be shaded: duplicates, opposite-wound twins, and degenerates.
//
// Asked for directly — "check for flipped or duplicate faces" — and never checked before, which was
// an omission and not a judgement call. Nothing upstream guarantees a face is emitted once. Three
// stages can each produce the same face independently:
//
//   * HoleFiller lids a boundary loop. Two loops that share their whole rim get two lids.
//   * NgonQuadrangulator splits an n-gon around a hub, and a rim visiting a vertex twice can pair
//     the same two corners into two different quads. There is a guard for the non-manifold case;
//     there was none for this one.
//   * QuadRegularizer merges triangle pairs, and a pair reachable from either side merges twice.
//
// Why a duplicate looks exactly like bad normals rather than like a duplicate. Two coincident faces
// occupy the same depth, so which one wins the depth test varies per pixel with floating-point
// rasterisation order. If they are wound oppositely — the common case, since one of them came from a
// pass that decided winding independently — one of the two is backface-culled per pixel, so the
// surface flickers between shaded and black in a fixed dithered pattern. That is the mottling in the
// report, and no amount of correcting normals can fix it while both faces exist.
//
// This runs BEFORE FaceOrienter, because a duplicate pair joins its component through edges it
// should not own, and its area votes twice.

using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    public sealed class RepairStats
    {
        public int faceCountBefore;
        public int faceCountAfter;

        /// <summary>Faces removed because an identical corner set was already present.</summary>
        public int duplicatesRemoved;

        /// <summary>Of those, the ones wound the opposite way round. These are the ones that mottle.</summary>
        public int oppositeWound;

        /// <summary>Faces whose corner list repeated a vertex next to itself, shortened in place.</summary>
        public int cornersCollapsed;

        /// <summary>Faces dropped for having fewer than three distinct corners after that.</summary>
        public int degenerateRemoved;

        /// <summary>Faces dropped for enclosing no area. Kept separate: these are real folds.</summary>
        public int zeroAreaRemoved;

        /// <summary>Undirected edges shared by more than two of the surviving faces.</summary>
        public int overIncidentEdges;

        public override string ToString()
        {
            return $"faces={faceCountBefore}->{faceCountAfter} dup={duplicatesRemoved} " +
                   $"(opposite={oppositeWound}) collapsed={cornersCollapsed} " +
                   $"degenerate={degenerateRemoved} zeroArea={zeroAreaRemoved} " +
                   $"overIncident={overIncidentEdges}";
        }
    }

    public static class FaceRepair
    {
        public static RepairStats Run(List<int[]> faces, Vector3[] positions)
        {
            var stats = new RepairStats();
            if (faces == null || positions == null) return stats;

            stats.faceCountBefore = faces.Count;

            // A face's identity is its SET of corners, not its corner order: two faces on the same
            // corners are coincident geometry whichever rotation or reflection each was written in.
            // The canonical key is therefore the sorted corner list, and the rotation each face was
            // stored in is compared separately only to report how many were opposite-wound.
            var seen = new Dictionary<string, int>(faces.Count);
            var kept = new List<int[]>(faces.Count);
            var scratch = new int[16];

            foreach (var raw in faces)
            {
                if (raw == null || raw.Length < 3) { stats.degenerateRemoved++; continue; }

                // Drop a corner equal to the one before it — that is a zero-length edge, and it
                // makes an n-gon claim an arity it does not have.
                var face = CollapseRepeats(raw, out var collapsed);
                if (collapsed) stats.cornersCollapsed++;

                if (face.Length < 3) { stats.degenerateRemoved++; continue; }

                var area = QuadExtractor.FaceNormal(positions, face).magnitude;
                if (!(area > 0f)) { stats.zeroAreaRemoved++; continue; }

                if (face.Length > scratch.Length) scratch = new int[face.Length];
                System.Array.Copy(face, scratch, face.Length);
                System.Array.Sort(scratch, 0, face.Length);

                var key = Key(scratch, face.Length);
                if (seen.TryGetValue(key, out var firstIndex))
                {
                    stats.duplicatesRemoved++;
                    if (!SameWinding(kept[firstIndex], face)) stats.oppositeWound++;
                    continue;
                }

                seen[key] = kept.Count;
                kept.Add(face);
            }

            faces.Clear();
            faces.AddRange(kept);
            stats.faceCountAfter = faces.Count;

            // Report, do not repair. An edge in three faces is a structural fault upstream and
            // dropping one of the three would open a hole, which is the worse defect.
            var incidence = new Dictionary<long, int>(faces.Count * 2, EdgeKey.Comparer);
            foreach (var face in faces)
                for (var i = 0; i < face.Length; i++)
                {
                    var key = EdgeKey.PackUndirected(face[i], face[(i + 1) % face.Length]);
                    incidence.TryGetValue(key, out var count);
                    incidence[key] = count + 1;
                }

            foreach (var kv in incidence)
                if (kv.Value > 2) stats.overIncidentEdges++;

            return stats;
        }

        static int[] CollapseRepeats(int[] face, out bool collapsed)
        {
            var needed = false;
            for (var i = 0; i < face.Length; i++)
                if (face[i] == face[(i + 1) % face.Length]) { needed = true; break; }

            collapsed = needed;
            if (!needed) return face;

            var result = new List<int>(face.Length);
            foreach (var v in face)
                if (result.Count == 0 || result[result.Count - 1] != v) result.Add(v);

            // The wrap-around pair, once the rest is settled.
            while (result.Count > 1 && result[0] == result[result.Count - 1])
                result.RemoveAt(result.Count - 1);

            return result.ToArray();
        }

        static string Key(int[] sorted, int length)
        {
            var builder = new System.Text.StringBuilder(length * 8);
            for (var i = 0; i < length; i++)
            {
                builder.Append(sorted[i]);
                builder.Append(',');
            }
            return builder.ToString();
        }

        /// <summary>
        /// True when the two faces traverse their shared corner set the same way round. Compared by
        /// finding a's first corner in b and walking both forward; a mismatch means b is a
        /// reflection, which is what makes the pair mutually backface-culling.
        /// </summary>
        static bool SameWinding(int[] a, int[] b)
        {
            if (a.Length != b.Length) return false;

            var offset = -1;
            for (var i = 0; i < b.Length; i++)
                if (b[i] == a[0]) { offset = i; break; }

            if (offset < 0) return false;

            for (var i = 0; i < a.Length; i++)
                if (b[(offset + i) % b.Length] != a[i]) return false;

            return true;
        }
    }
}
