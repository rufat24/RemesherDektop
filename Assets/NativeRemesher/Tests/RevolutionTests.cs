// Surfaces of revolution built as a turned profile (Revolution), a part's turned pieces found one
// by one, and the floor a thin band is given (PartBake.ThinFloor).

using System.Collections.Generic;
using NUnit.Framework;
using NativeRemesher.Benchmark;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class RevolutionTests
    {
        static void Torus(Vector3 offset, out Vector3[] positions, out int[] triangles)
        {
            var mesh = MeshCorpus.Torus(1f, 0.35f, 96, 32);
            positions = mesh.vertices;
            for (var i = 0; i < positions.Length; i++) positions[i] += offset;
            triangles = mesh.triangles;
            Object.DestroyImmediate(mesh);
        }

        // A closed box of n x n quads a side, as triangles.
        static void Box(Vector3 size, int n, out Vector3[] positions, out int[] triangles)
        {
            var p = new List<Vector3>();
            var t = new List<int>();
            for (var axis = 0; axis < 3; axis++)
                for (var side = -1; side <= 1; side += 2)
                {
                    var o = p.Count;
                    for (var j = 0; j <= n; j++)
                        for (var i = 0; i <= n; i++)
                        {
                            var v = new Vector3();
                            v[axis] = 0.5f * side;
                            v[(axis + 1) % 3] = (float)i / n - 0.5f;
                            v[(axis + 2) % 3] = (float)j / n - 0.5f;
                            p.Add(Vector3.Scale(v, size));
                        }
                    for (var j = 0; j < n; j++)
                        for (var i = 0; i < n; i++)
                        {
                            int a = o + j * (n + 1) + i, b = a + 1, c = a + n + 2, d = a + n + 1;
                            if (side > 0) t.AddRange(new[] { a, b, c, a, c, d });
                            else t.AddRange(new[] { a, c, b, a, d, c });
                        }
                }
            positions = p.ToArray();
            triangles = t.ToArray();
        }

        [Test]
        public void ATorusIsTurnedAndBuiltRoundOnItsSurface()
        {
            Torus(Vector3.zero, out var positions, out var triangles);
            var fit = Revolution.Find(positions, triangles);
            Assert.IsNotNull(fit);
            Assert.Greater(Mathf.Abs(Vector3.Dot(fit.axis.normalized, Vector3.up)), 0.99f, fit.ToString());

            Assert.IsTrue(Revolution.Build(positions, triangles, fit, 300, out var outPositions, out var outFaces));
            Assert.That(outFaces.Count, Is.InRange(150, 600));
            // Every point on the torus: 0.35 from the ring of radius 1, within the profile's chords.
            foreach (var p in outPositions)
            {
                var ring = new Vector3(p.x, 0f, p.z).normalized;
                Assert.AreEqual(0.35f, (p - ring).magnitude, 0.03f, p.ToString());
            }
        }

        [Test]
        public void ABoxIsNotTurned()
        {
            Box(new Vector3(1f, 0.6f, 2f), 12, out var positions, out var triangles);
            Assert.IsNull(Revolution.Find(positions, triangles));
        }

        [Test]
        public void TwoTorusesInOnePartAreTurnedPieceByPiece()
        {
            Torus(new Vector3(-3f, 0f, 0f), out var a, out var at);
            Torus(new Vector3(3f, 0f, 0f), out var b, out var bt);
            var positions = new Vector3[a.Length + b.Length];
            a.CopyTo(positions, 0);
            b.CopyTo(positions, a.Length);
            var triangles = new int[at.Length + bt.Length];
            at.CopyTo(triangles, 0);
            for (var i = 0; i < bt.Length; i++) triangles[at.Length + i] = bt[i] + a.Length;

            Assert.IsNull(Revolution.Find(positions, triangles), "no one axis fits both");
            var pieces = Revolution.FindPieces(positions, triangles);
            Assert.IsNotNull(pieces);
            Assert.AreEqual(2, pieces.fits.Length);
            Assert.IsNotNull(pieces.fits[0]);
            Assert.IsNotNull(pieces.fits[1]);
            Assert.AreEqual(1f, pieces.turnedShare, 1e-3f);
        }

        [Test]
        public void AThinBandIsGivenQuadsToGoRoundItAndASheetIsNot()
        {
            // A band 40 long, 0.2 across: thinner than the cell, along a line.
            Box(new Vector3(40f, 0.2f, 0.2f), 40, out var band, out var bandTriangles);
            var cell = 1f;
            var cells = PartBake.Cells(band, bandTriangles, Matrix4x4.identity, cell, out var closed, out var coarse);
            Assert.IsTrue(closed);
            var area = PartBake.Area(band, bandTriangles, Matrix4x4.identity);
            Assert.AreEqual(4 * cells, PartBake.ThinFloor(cells, coarse, area, cell, closed));

            // A panel 20 by 20 crossing the cells slantwise: under a cell's area in some, but a sheet.
            Box(new Vector3(20f, 20f, 0.2f), 20, out var sheet, out var sheetTriangles);
            var tilt = Matrix4x4.Rotate(Quaternion.Euler(30f, 40f, 0f));
            cells = PartBake.Cells(sheet, sheetTriangles, tilt, cell, out closed, out coarse);
            area = PartBake.Area(sheet, sheetTriangles, tilt);
            Assert.AreEqual(0, PartBake.ThinFloor(cells, coarse, area, cell, closed));
        }
    }
}
