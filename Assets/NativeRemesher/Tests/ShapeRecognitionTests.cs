// Model types read off the mesh, the thickness cap's rods and plates, and which parts of a model
// can be seen from outside.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class ShapeRecognitionTests
    {
        // A box from min to max, each face a grid of `cells` x `cells` quads (triangulated), welded.
        static void Box(Vector3 min, Vector3 max, int cells, out Vector3[] positions, out int[] triangles,
            System.Func<Vector3, Vector3> warp = null)
        {
            var index = new Dictionary<Vector3Int, int>();
            var p = new List<Vector3>();
            var t = new List<int>();
            int Vertex(Vector3Int g)
            {
                if (index.TryGetValue(g, out var i)) return i;
                var x = new Vector3(Mathf.Lerp(min.x, max.x, g.x / (float)cells),
                    Mathf.Lerp(min.y, max.y, g.y / (float)cells), Mathf.Lerp(min.z, max.z, g.z / (float)cells));
                p.Add(warp != null ? warp(x) : x);
                return index[g] = p.Count - 1;
            }
            for (var axis = 0; axis < 3; axis++)
            for (var side = 0; side < 2; side++)
            {
                var u = (axis + 1) % 3;
                var v = (axis + 2) % 3;
                for (var a = 0; a < cells; a++)
                for (var b = 0; b < cells; b++)
                {
                    Vector3Int G(int da, int db)
                    {
                        var g = Vector3Int.zero;
                        g[axis] = side * cells;
                        g[u] = a + da;
                        g[v] = b + db;
                        return g;
                    }
                    int c0 = Vertex(G(0, 0)), c1 = Vertex(G(1, 0)), c2 = Vertex(G(1, 1)), c3 = Vertex(G(0, 1));
                    if (side == 1) t.AddRange(new[] { c0, c1, c2, c0, c2, c3 });
                    else t.AddRange(new[] { c0, c2, c1, c0, c3, c2 });
                }
            }
            positions = p.ToArray();
            triangles = t.ToArray();
        }

        [Test]
        public void ALongBoxOfThinQuadsIsHardSurface()
        {
            // 8 x 1 x 1, two quads along each face: every quad far longer than wide, as the support
            // loops of a modelled part are.
            Box(Vector3.zero, new Vector3(8f, 1f, 1f), 2, out var positions, out var triangles);
            var type = ShapeRecognition.Classify(positions, triangles, out var signals);
            Assert.That(signals.quadPairs, Is.GreaterThan(0.9f), signals.ToString());
            Assert.That(signals.elongated, Is.GreaterThan(0.3f), signals.ToString());
            Assert.That(type, Is.EqualTo(ModelType.HardSurface));
        }

        [Test]
        public void AnEvenQuadSphereIsACharacterNotHardSurface()
        {
            // A cube's grid pushed onto a sphere: quads, but square ones -- what a ZRemesher or voxel
            // remesh export looks like. Quads alone must not make it hard surface.
            Box(-Vector3.one, Vector3.one, 12, out var positions, out var triangles, x => x.normalized);
            var type = ShapeRecognition.Classify(positions, triangles, out var signals);
            Assert.That(signals.elongated, Is.LessThan(0.05f), signals.ToString());
            Assert.That(type, Is.EqualTo(ModelType.Character), signals.ToString());
        }

        [Test]
        public void AClosedUnstructuredBuildingIsAScanNotACharacter()
        {
            // A closed box of uneven triangles (no quads in it, no border), as a dense building
            // sculpted or scanned is: its walls face six ways, which no character's surface does.
            var random = new System.Random(5);
            Box(new Vector3(0f, 0f, 0f), new Vector3(6f, 4f, 5f), 14, out var positions, out var triangles,
                x => x + Vector3.Scale(new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f,
                    (float)random.NextDouble() - 0.5f), new Vector3(0.12f, 0.12f, 0.12f)));
            // Pressed back onto the walls: only the jitter along them stays.
            for (var i = 0; i < positions.Length; i++)
            {
                var q = positions[i];
                var d = new[] { Mathf.Abs(q.x), Mathf.Abs(q.x - 6f), Mathf.Abs(q.y), Mathf.Abs(q.y - 4f), Mathf.Abs(q.z), Mathf.Abs(q.z - 5f) };
                var k = System.Array.IndexOf(d, Mathf.Min(d));
                if (k < 2) q.x = k == 0 ? 0f : 6f; else if (k < 4) q.y = k == 2 ? 0f : 4f; else q.z = k == 4 ? 0f : 5f;
                positions[i] = q;
            }
            var type = ShapeRecognition.Classify(positions, triangles, out var signals);
            Assert.That(signals.elongated, Is.LessThan(ShapeRecognition.elongatedThreshold), signals.ToString());
            Assert.That(signals.openBorder, Is.LessThan(0.5f), signals.ToString());
            Assert.That(signals.flatDirections, Is.GreaterThan(0.6f), signals.ToString());
            Assert.That(type, Is.EqualTo(ModelType.Scan), signals.ToString());
        }

        [Test]
        public void AnOpenJitteredSheetIsAScan()
        {
            var random = new System.Random(3);
            const int n = 40;
            var positions = new Vector3[(n + 1) * (n + 1)];
            for (var j = 0; j <= n; j++)
                for (var i = 0; i <= n; i++)
                    positions[j * (n + 1) + i] = new Vector3(i + 0.35f * ((float)random.NextDouble() - 0.5f),
                        0.3f * (float)random.NextDouble(), j + 0.35f * ((float)random.NextDouble() - 0.5f));
            var t = new List<int>();
            for (var j = 0; j < n; j++)
                for (var i = 0; i < n; i++)
                {
                    var a = j * (n + 1) + i;
                    t.AddRange(new[] { a, a + n + 1, a + n + 2, a, a + n + 2, a + 1 });
                }
            var type = ShapeRecognition.Classify(positions, t.ToArray(), out var signals);
            Assert.That(signals.openBorder, Is.GreaterThan(2f), signals.ToString());
            Assert.That(type, Is.EqualTo(ModelType.Scan), signals.ToString());
        }

        [Test]
        public void ARodHasAThicknessAndAPlatesFaceHasNone()
        {
            // The thickness cap is for rods (fingers, toes): a plate's face is thin one way only.
            Box(Vector3.zero, new Vector3(10f, 0.4f, 0.4f), 20, out var rodP, out var rodT);
            var rod = RemeshTopology.Build(rodP, rodT);
            var reach = new float[rod.vertexCount];
            for (var i = 0; i < reach.Length; i++) reach[i] = 2f;
            var rodThickness = ScaleField.Thickness(rod, reach);
            var middle = Nearest(rod.positions, new Vector3(5f, 0.4f, 0.2f));
            Assert.That(rodThickness[middle], Is.EqualTo(0.4f).Within(0.05f));

            Box(Vector3.zero, new Vector3(10f, 0.4f, 10f), 20, out var plateP, out var plateT);
            var plate = RemeshTopology.Build(plateP, plateT);
            reach = new float[plate.vertexCount];
            for (var i = 0; i < reach.Length; i++) reach[i] = 2f;
            var plateThickness = ScaleField.Thickness(plate, reach);
            var face = Nearest(plate.positions, new Vector3(5f, 0.4f, 5f));
            Assert.That(float.IsPositiveInfinity(plateThickness[face]), $"plate face read as {plateThickness[face]}");
        }

        [Test]
        public void APartShutInsideAnotherIsHiddenUnlessTheOuterIsGlass()
        {
            Box(-Vector3.one * 2f, Vector3.one * 2f, 4, out var outerP, out var outerT);
            Box(-Vector3.one * 0.5f, Vector3.one * 0.5f, 4, out var innerP, out var innerT);
            var parts = new List<PartBake.Part>
            {
                new PartBake.Part { positions = outerP, triangles = outerT },
                new PartBake.Part { positions = innerP, triangles = innerT },
            };
            var shut = PartBake.Visibility(parts, new[] { true, true });
            Assert.That(shut[0], Is.GreaterThan(0.9f));
            Assert.That(shut[1], Is.LessThan(0.05f));
            var glass = PartBake.Visibility(parts, new[] { false, true });
            Assert.That(glass[1], Is.GreaterThan(0.9f));
        }

        static int Nearest(Vector3[] positions, Vector3 p)
        {
            var best = 0;
            for (var i = 1; i < positions.Length; i++)
                if ((positions[i] - p).sqrMagnitude < (positions[best] - p).sqrMagnitude) best = i;
            return best;
        }
    }
}
