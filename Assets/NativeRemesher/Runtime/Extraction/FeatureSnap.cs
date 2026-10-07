// Pass J: pull the finished quad mesh onto the creases it was supposed to land on.
//
// WHY A POST-PROCESS AND NOT A CONSTRAINT. The principled place to do this is the position field:
// tell the solver where cell boundaries must fall and let everything downstream inherit it. That was
// built and measured and it made things worse - creases kept fell from 25.8 % to 21.7 % - because
// the constraint lands after the anchoring snap, moves the representative off the lattice, and the
// next sweep spends itself undoing it. Doing it properly needs each crease to carry its own
// one-dimensional lattice with an origin shared along the polyline, which is a much larger change.
//
// This pass takes the finished mesh instead, where nothing can be destabilised by it: the faces are
// final, the winding is settled, and moving a vertex changes only where that vertex is.
//
// THE DEFECT IT ADDRESSES. On the armour asset the input has 11,574 crease vertices - panel edges of
// a plate carrier, webbing rows, a belt, a collar - and only a quarter of them have any output
// vertex sitting on them. The cross field is combed along those creases correctly, so the quads run
// PARALLEL to a panel edge; they are simply offset from it by whatever the lattice picked. A crease
// missed by a third of a quad is not a crease, it is a bevel, and a whole asset of them reads as the
// melted-mannequin the user has been pointing at.
//
// FOUR GUARDS, because a snap is a move and every move can manufacture a defect:
//
//   1. Only within `maxSnapCells` of a crease. A vertex further away than that belongs to the
//      smooth surface between panels, and dragging it to an edge would tear the quad flow rather
//      than sharpen it.
//   2. Never if a face at that vertex would end up facing the other way. The same test surface
//      relaxation uses, and for the same reason.
//   3. Never if the move would take a face's smallest edge below a fraction of a cell. Two
//      neighbouring vertices snapping to the same crease point is how a snap makes a sliver.
//   4. Junctions first and unconditionally within range. Where two creases meet, the corner is the
//      part of the shape a viewer checks first, and a corner claimed by a plain crease vertex is a
//      corner rounded off.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher.Extraction
{
    public sealed class SnapStats
    {
        public int candidates;
        public int snapped;
        public int snappedCorners;
        public int rejectedFlip;
        public int rejectedSliver;
        public float meanMoveCells;

        public override string ToString()
        {
            return $"snapped {snapped} ({snappedCorners} corners) of {candidates} candidates, " +
                   $"mean move {meanMoveCells:F3} cells, " +
                   $"rejected flip {rejectedFlip} sliver {rejectedSliver}";
        }
    }

    public static class FeatureSnap
    {
        /// <summary>
        /// How far, in lattice cells, an output vertex may be from a crease and still be pulled onto
        /// it.
        /// </summary>
        /// <remarks>
        /// Half a cell is the largest value that cannot reorder anything. A vertex further than half
        /// a cell from a crease has some other lattice point nearer to that crease than it is, so
        /// snapping it would put two output vertices on the same feature and leave the one that
        /// belonged there with nowhere to go.
        /// </remarks>
        public const float defaultMaxSnapCells = 0.5f;

        /// <summary>Shortest edge a face may be left with, in cells, after a snap.</summary>
        public const float defaultMinEdgeCells = 0.25f;

        public static SnapStats Run(List<int[]> faces, Vector3[] positions, Vector3[] inputPositions,
            FeatureGraph features, float scale, float maxSnapCells = defaultMaxSnapCells,
            float minEdgeCells = defaultMinEdgeCells)
        {
            var stats = new SnapStats();
            if (faces == null || positions == null || inputPositions == null || features == null)
                return stats;
            if (features.isFeatureVertex == null || !(scale > 0f)) return stats;

            // Crease vertices only. Everything else in the input is smooth surface and has nothing
            // to say about where an output vertex belongs.
            var creases = new List<int>();
            for (var i = 0; i < features.isFeatureVertex.Length && i < inputPositions.Length; i++)
                if (features.isFeatureVertex[i]) creases.Add(i);

            if (creases.Count == 0) return stats;

            var radius = maxSnapCells * scale;
            var grid = new PointGrid(inputPositions, creases, radius);

            var facesAt = new List<int>[positions.Length];
            for (var f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                if (face == null || face.Length < 3) continue;
                foreach (var v in face)
                {
                    if (v < 0 || v >= positions.Length) continue;
                    (facesAt[v] ??= new List<int>()).Add(f);
                }
            }

            // Corners first, so a junction is claimed by the vertex nearest it before any plain
            // crease vertex can take that vertex for itself.
            var totalMove = 0.0;
            for (var pass = 0; pass < 2; pass++)
            {
                for (var v = 0; v < positions.Length; v++)
                {
                    if (facesAt[v] == null) continue;

                    var best = grid.Nearest(positions[v], radius, features,
                        wantCorner: pass == 0, out var distance);
                    if (best < 0) continue;
                    if (pass == 0) stats.candidates++;

                    var target = inputPositions[best];
                    if ((target - positions[v]).sqrMagnitude <= 1e-20f) continue;

                    var original = positions[v];
                    positions[v] = target;

                    if (Flips(faces, facesAt[v], positions, original, v))
                    {
                        positions[v] = original;
                        stats.rejectedFlip++;
                        continue;
                    }

                    if (TooShort(faces, facesAt[v], positions, minEdgeCells * scale))
                    {
                        positions[v] = original;
                        stats.rejectedSliver++;
                        continue;
                    }

                    stats.snapped++;
                    if (pass == 0) stats.snappedCorners++;
                    totalMove += distance;
                }
            }

            stats.meanMoveCells = stats.snapped > 0
                ? (float)(totalMove / stats.snapped / scale)
                : 0f;
            return stats;
        }

        /// <summary>Would any face at this vertex now face the other way?</summary>
        static bool Flips(List<int[]> faces, List<int> at, Vector3[] positions, Vector3 original,
            int vertex)
        {
            foreach (var f in at)
            {
                var face = faces[f];
                if (face == null || face.Length < 3) continue;

                var after = QuadExtractor.FaceNormal(positions, face);

                var saved = positions[vertex];
                positions[vertex] = original;
                var before = QuadExtractor.FaceNormal(positions, face);
                positions[vertex] = saved;

                if (Vector3.Dot(before, after) < 0f) return true;
            }

            return false;
        }

        static bool TooShort(List<int[]> faces, List<int> at, Vector3[] positions, float minimum)
        {
            var square = minimum * minimum;
            foreach (var f in at)
            {
                var face = faces[f];
                if (face == null || face.Length < 3) continue;

                for (var k = 0; k < face.Length; k++)
                {
                    var a = face[k];
                    var b = face[(k + 1) % face.Length];
                    if ((positions[a] - positions[b]).sqrMagnitude < square) return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Uniform hash over a subset of the input vertices, sized to the query radius so a lookup
        /// touches 27 buckets at most.
        /// </summary>
        sealed class PointGrid
        {
            readonly Dictionary<long, List<int>> m_Buckets = new Dictionary<long, List<int>>(EdgeKey.Comparer);
            readonly Vector3[] m_Positions;
            readonly float m_Cell;

            public PointGrid(Vector3[] positions, List<int> subset, float cell)
            {
                m_Positions = positions;
                m_Cell = Mathf.Max(cell, 1e-6f);

                foreach (var i in subset)
                {
                    var key = Key(positions[i]);
                    if (!m_Buckets.TryGetValue(key, out var bucket))
                        m_Buckets[key] = bucket = new List<int>();
                    bucket.Add(i);
                }
            }

            long Key(Vector3 p)
            {
                long x = Mathf.FloorToInt(p.x / m_Cell);
                long y = Mathf.FloorToInt(p.y / m_Cell);
                long z = Mathf.FloorToInt(p.z / m_Cell);
                return (x * 73856093L) ^ (y * 19349663L) ^ (z * 83492791L);
            }

            public int Nearest(Vector3 query, float radius, FeatureGraph features, bool wantCorner,
                out float distance)
            {
                var best = -1;
                var bestSquare = radius * radius;
                distance = 0f;

                long cx = Mathf.FloorToInt(query.x / m_Cell);
                long cy = Mathf.FloorToInt(query.y / m_Cell);
                long cz = Mathf.FloorToInt(query.z / m_Cell);

                for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                for (var dz = -1; dz <= 1; dz++)
                {
                    var key = ((cx + dx) * 73856093L) ^ ((cy + dy) * 19349663L)
                                                      ^ ((cz + dz) * 83492791L);
                    if (!m_Buckets.TryGetValue(key, out var bucket)) continue;

                    foreach (var i in bucket)
                    {
                        if (wantCorner && features.featureDegree[i] < 3) continue;

                        var d = (m_Positions[i] - query).sqrMagnitude;
                        if (d >= bestSquare) continue;

                        bestSquare = d;
                        best = i;
                    }
                }

                if (best >= 0) distance = Mathf.Sqrt(bestSquare);
                return best;
            }
        }
    }
}
