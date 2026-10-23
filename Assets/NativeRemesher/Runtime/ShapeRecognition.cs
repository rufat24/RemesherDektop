// What kind of model this is, read off its triangles: the Model Type's Auto.
//
// WHY. The settings that measured best differ by kind of model, and the user should not have to
// know which kind a mesh "is" in the remesher's terms. The old Auto Profile could only tell a scan
// from a modelled mesh, by the share of vertices on 30-degree creases after decimation, and it read
// every panel of a coarse modelled car as a scan (probe 0.25-0.67, the vertex-fraction limit its own
// remarks predicted). The signals here are read off the INPUT, where the way it was made still shows.
//
// THE SIGNALS, measured on every model in the project (43 car parts, the Sketchfab helmet's three,
// the helmet head, the body, main1, ccnb) and on organic quad meshes made for the test (main1 and
// the body voxel-remeshed in Blender, with and without a subdivision, triangulated -- what a ZBrush
// or ZRemesher export looks like after Unity's import):
//
//   quad pairs   triangles in pairs that were one quad: both angles facing the shared edge within
//                8 degrees of square.  Car parts 0.43-1.0; organic quad meshes 0.32-0.58; sculpts,
//                scans 0.04-0.11.
//   elongated    of those pairs, the share longer than 3 to 1: the thin quads of support loops and
//                chamfers.  Car parts 0.22-0.97; organic quad meshes 0.000-0.001; sculpts 0.04-0.12.
//   open border  boundary length over the square root of the area, welded by position so a UV
//                seam is not a border.  ccnb 4.5; closed sculpts 0; one open helmet shell 9.5.
//
//   flat dirs    share of the area in the 8 fullest of 1,536 normal-direction bins.  White house
//                0.46, ccnb 0.69; characters, helmet parts, the plant 0.03-0.06.
//
// Hard surface needs both of the first two -- quads alone would take a ZRemesher character for a
// car. A scan is an open, dense, unstructured mesh, or an unstructured one whose surface faces a
// few ways (a dense building). Everything else is sculpted or organic.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher
{
    /// <summary>
    /// The window's Model Type: which kind of model the settings are chosen for.
    /// </summary>
    public enum ModelType
    {
        /// <summary>No type: the pipeline's own defaults, as before Model Type existed.</summary>
        General,
        /// <summary>Read the type off each mesh (each part of a model in parts). See <see cref="ShapeRecognition"/>.</summary>
        Auto,
        /// <summary>Sculpted or organic: characters, creatures, heads. Fingers and toes get cells across them.</summary>
        Character,
        /// <summary>Modelled: vehicles, props, machines, buildings made in a modelling tool.</summary>
        HardSurface,
        /// <summary>Photogrammetry or a laser scan: terrain, buildings, objects captured from photos.</summary>
        Scan,
    }

    /// <summary>What <see cref="ShapeRecognition.Measure"/> read off a mesh.</summary>
    [Serializable]
    public struct ShapeSignals
    {
        /// <summary>Share of triangles in a pair that was one quad (both angles facing the shared edge square).</summary>
        public float quadPairs;
        /// <summary>Share of those pairs longer than 3 to 1.</summary>
        public float elongated;
        /// <summary>Boundary length over the square root of the area.</summary>
        public float openBorder;
        /// <summary>Share of the area whose normals fall in the 8 fullest of 1,536 direction bins: a building's walls and roof planes.</summary>
        public float flatDirections;
        public int triangles;
        /// <summary>False when nothing was measured (no mesh, or not asked).</summary>
        public bool measured;

        public override string ToString() =>
            $"quad pairs {quadPairs:P0}, elongated {elongated:P0}, open border {openBorder:F1}, flat directions {flatDirections:P0}";
    }

    public static class ShapeRecognition
    {
        /// <summary>Quad pairs at or above this, with <see cref="elongatedThreshold"/>, make a mesh hard surface.</summary>
        public static float quadPairThreshold = 0.25f;
        /// <summary>Elongated pairs at or above this, with <see cref="quadPairThreshold"/>, make a mesh hard surface.</summary>
        public static float elongatedThreshold = 0.12f;
        /// <summary>Open border at or above this makes an unstructured mesh a scan.</summary>
        public static float openBorderThreshold = 2f;
        /// <summary>
        /// Flat directions at or above this make an unstructured mesh a scan too: a building or a
        /// prop sculpted or scanned dense, its walls facing a few ways.
        /// </summary>
        /// <remarks>
        /// The white house (1.8M triangles, closed, 3 % quad pairs) is 46 %; the helmet's head and
        /// horn 3-6 %, the plant 3 %. Read as a character it came out with rounded walls and
        /// soft eaves, as hard surface with its lower walls holed; as a scan, flat and sharp.
        /// </remarks>
        public static float flatDirectionsThreshold = 0.25f;

        /// <summary>Angle from square, in degrees, still read as a quad's corner.</summary>
        const float k_SquareTolerance = 8f;
        const float k_ElongatedAspect = 3f;

        public static ModelType Classify(ShapeSignals signals)
        {
            if (!signals.measured) return ModelType.Character;
            if (signals.quadPairs >= quadPairThreshold && signals.elongated >= elongatedThreshold)
                return ModelType.HardSurface;
            if (signals.openBorder >= openBorderThreshold) return ModelType.Scan;
            if (flatDirectionsThreshold > 0f && signals.flatDirections >= flatDirectionsThreshold) return ModelType.Scan;
            return ModelType.Character;
        }

        public static ModelType Classify(Vector3[] positions, int[] triangles, out ShapeSignals signals)
        {
            signals = Measure(positions, triangles);
            return Classify(signals);
        }

        public static ShapeSignals Measure(Vector3[] positions, int[] triangles)
        {
            var signals = new ShapeSignals();
            if (positions == null || triangles == null || triangles.Length < 3 || positions.Length == 0)
                return signals;

            // Weld by position: an imported mesh is split at every UV seam and hard normal.
            var min = positions[0];
            var max = positions[0];
            foreach (var p in positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            var extent = Mathf.Max(max.x - min.x, Mathf.Max(max.y - min.y, max.z - min.z));
            var quantum = Mathf.Max(extent * 1e-6f, 1e-30f);
            var weld = new int[positions.Length];
            var seen = new Dictionary<(long, long, long), int>(positions.Length);
            for (var i = 0; i < positions.Length; i++)
            {
                var p = positions[i] - min;
                var key = ((long)Math.Round(p.x / quantum), (long)Math.Round(p.y / quantum), (long)Math.Round(p.z / quantum));
                if (!seen.TryGetValue(key, out var w)) seen[key] = w = seen.Count;
                weld[i] = w;
            }
            var welded = seen.Count;

            var triangleCount = triangles.Length / 3;
            // Every half-edge: its welded ends as one key, and the corner facing it.
            var keys = new long[triangleCount * 3];
            var facing = new int[triangleCount * 3];
            double area = 0;
            for (var t = 0; t < triangleCount; t++)
            {
                int a = triangles[3 * t], b = triangles[3 * t + 1], c = triangles[3 * t + 2];
                area += 0.5 * Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]).magnitude;
                for (var k = 0; k < 3; k++)
                {
                    var u = weld[triangles[3 * t + k]];
                    var v = weld[triangles[3 * t + (k + 1) % 3]];
                    keys[3 * t + k] = u < v ? (long)u * welded + v : (long)v * welded + u;
                    facing[3 * t + k] = triangles[3 * t + (k + 2) % 3];
                }
            }
            var order = new int[keys.Length];
            for (var i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort((long[])keys.Clone(), order);

            var square = Mathf.Cos((90f - k_SquareTolerance) * Mathf.Deg2Rad);
            int pairs = 0, elongated = 0;
            double border = 0;
            for (var i = 0; i < order.Length;)
            {
                var j = i + 1;
                while (j < order.Length && keys[order[j]] == keys[order[i]]) j++;
                var h = order[i];
                var t = h / 3;
                var k = h % 3;
                var p0 = positions[triangles[3 * t + k]];
                var p1 = positions[triangles[3 * t + (k + 1) % 3]];
                if (j - i == 1) border += (p1 - p0).magnitude;
                else if (j - i == 2)
                {
                    var o0 = positions[facing[order[i]]];
                    var o1 = positions[facing[order[i + 1]]];
                    if (Square(o0, p0, p1, square) && Square(o1, p0, p1, square))
                    {
                        pairs++;
                        var l0 = (p0 - o0).magnitude;
                        var l1 = (p1 - o0).magnitude;
                        if (Mathf.Max(l0, l1) > k_ElongatedAspect * Mathf.Min(l0, l1)) elongated++;
                    }
                }
                i = j;
            }

            signals.flatDirections = FlatDirections(positions, triangles);
            signals.triangles = triangleCount;
            signals.quadPairs = Mathf.Min(1f, 2f * pairs / triangleCount);
            signals.elongated = pairs > 0 ? elongated / (float)pairs : 0f;
            signals.openBorder = area > 0 ? (float)(border / Math.Sqrt(area)) : 0f;
            signals.measured = true;
            return signals;
        }

        /// <summary>
        /// Share of the area in the 8 fullest of 6 x 16 x 16 cube-map bins of face normal.
        /// </summary>
        public static float FlatDirections(Vector3[] positions, int[] triangles)
        {
            const int n = 16;
            var bins = new double[6 * n * n];
            double total = 0;
            for (var t = 0; t + 2 < triangles.Length; t += 3)
            {
                var c = Vector3.Cross(positions[triangles[t + 1]] - positions[triangles[t]],
                    positions[triangles[t + 2]] - positions[triangles[t]]);
                var area = c.magnitude;
                if (!(area > 0f)) continue;
                var d = c / area;
                float ax = Mathf.Abs(d.x), ay = Mathf.Abs(d.y), az = Mathf.Abs(d.z);
                int face;
                float u, v;
                if (ax >= ay && ax >= az) { face = d.x > 0 ? 0 : 1; u = d.y / ax; v = d.z / ax; }
                else if (ay >= az) { face = d.y > 0 ? 2 : 3; u = d.x / ay; v = d.z / ay; }
                else { face = d.z > 0 ? 4 : 5; u = d.x / az; v = d.y / az; }
                var i = Mathf.Clamp((int)((u * 0.5f + 0.5f) * n), 0, n - 1);
                var j = Mathf.Clamp((int)((v * 0.5f + 0.5f) * n), 0, n - 1);
                bins[(face * n + j) * n + i] += area;
                total += area;
            }
            if (total <= 0) return 0f;
            Array.Sort(bins);
            double top = 0;
            for (var k = 1; k <= 8; k++) top += bins[bins.Length - k];
            return (float)(top / total);
        }

        // The corner at `o` facing the edge p0-p1 is within the tolerance of square.
        static bool Square(Vector3 o, Vector3 p0, Vector3 p1, float cosTolerance)
        {
            var a = p0 - o;
            var b = p1 - o;
            var la = a.magnitude;
            var lb = b.magnitude;
            if (!(la > 0f) || !(lb > 0f)) return false;
            return Mathf.Abs(Vector3.Dot(a, b) / (la * lb)) <= cosTolerance;
        }
    }
}
