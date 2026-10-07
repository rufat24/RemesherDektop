// The integer grid map, gated on inputs whose best possible answer is known.
//
// Written before the first measurement, as PLAN.md W1.5 asked: the torus has a defect-free quad
// grid available and a field with no singularities, so anything irregular in its output is
// manufactured by extraction. QuadriFlow lands on 24 there and exactly 8 on the icosphere; those
// are the numbers a port of it has to reach.

using System.Collections.Generic;
using NUnit.Framework;
using NativeRemesher.Benchmark;
using NativeRemesher.Extraction;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class IntegerGridTests
    {
        static RemeshResult Run(Mesh mesh, int target,
            RemeshProfileKind kind = RemeshProfileKind.GameAsset)
        {
            var settings = RemeshSettings.Default;
            settings.targetQuadCount = target;
            var profile = RemeshProfile.For(kind);
            profile.integerGridMap = true;
            return RemeshPipeline.Run(mesh, settings, profile);
        }

        /// <summary>Interior vertices whose valence is not four. Boundary vertices are excluded.</summary>
        public static int IrregularInterior(QuadExtractionResult extraction, out int interior)
        {
            var valence = new Dictionary<int, int>();
            var uses = new Dictionary<long, int>(EdgeKey.Comparer);

            foreach (var face in extraction.faces)
            {
                for (var k = 0; k < face.Length; k++)
                {
                    var a = face[k];
                    var b = face[(k + 1) % face.Length];
                    valence.TryGetValue(a, out var used);
                    valence[a] = used + 1;
                    var key = EdgeKey.PackUndirected(a, b);
                    uses.TryGetValue(key, out var count);
                    uses[key] = count + 1;
                }
            }

            var onBoundary = new HashSet<int>();
            foreach (var entry in uses)
            {
                if (entry.Value == 2) continue;
                onBoundary.Add(EdgeKey.First(entry.Key));
                onBoundary.Add(EdgeKey.Second(entry.Key));
            }

            interior = 0;
            var irregular = 0;
            foreach (var entry in valence)
            {
                if (onBoundary.Contains(entry.Key)) continue;
                interior++;
                if (entry.Value != 4) irregular++;
            }
            return irregular;
        }

        [Test]
        public void Rshift90_IsAQuarterTurnPerStep()
        {
            var v = new Vector2Int(1, 0);
            Assert.AreEqual(new Vector2Int(0, 1), IntegerGridMap.Rshift90(v, 1));
            Assert.AreEqual(new Vector2Int(-1, 0), IntegerGridMap.Rshift90(v, 2));
            Assert.AreEqual(new Vector2Int(0, -1), IntegerGridMap.Rshift90(v, 3));
            Assert.AreEqual(v, IntegerGridMap.Rshift90(v, 4));
            Assert.AreEqual(IntegerGridMap.Rshift90(v, 3), IntegerGridMap.Rshift90(v, -1));

            var w = new Vector2Int(2, -3);
            Assert.AreEqual(w, IntegerGridMap.Rshift90(IntegerGridMap.Rshift90(w, 1), 3));
        }

        [Test]
        public void DisjointOrientTree_KeepsMergedFramesConsistent()
        {
            // Merge(a, b, o0, o1) promises Orient(a) + o1 == Orient(b) + o0 (mod 4), and that
            // promise has to survive later merges re-parenting either side.
            var tree = new DisjointOrientTree(4);
            tree.Merge(0, 1, 1, 0);
            tree.Merge(2, 3, 3, 2);
            tree.Merge(1, 2, 2, 1);

            Assert.AreEqual(IntegerGridMap.Mod4(tree.Orient(0) + 0), IntegerGridMap.Mod4(tree.Orient(1) + 1));
            Assert.AreEqual(IntegerGridMap.Mod4(tree.Orient(2) + 2), IntegerGridMap.Mod4(tree.Orient(3) + 3));
            Assert.AreEqual(IntegerGridMap.Mod4(tree.Orient(1) + 1), IntegerGridMap.Mod4(tree.Orient(2) + 2));
            Assert.AreEqual(tree.Parent(0), tree.Parent(3));
        }

        [Test]
        public void Torus_IsPureQuadNearTheEulerFloor()
        {
            var mesh = MeshCorpus.Torus(1f, 0.35f, 128, 64);
            try
            {
                var result = Run(mesh, 2000);
                var e = result.extraction;

                Assert.NotNull(e.integerGridStats, "the integer-grid path did not run");
                Assert.Greater(e.faceCount, 1000, "far too few faces");
                Assert.GreaterOrEqual(e.quadFraction, 0.99f, $"quad fraction {e.quadFraction:F4}");

                var irregular = IrregularInterior(e, out var interior);
                Assert.LessOrEqual(irregular, 30,
                    $"{irregular} irregular of {interior} interior vertices; floor is 0, QuadriFlow 24\n" +
                    e.integerGridStats);
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void Torus_FlowRoutesEveryUnit()
        {
            var mesh = MeshCorpus.Torus(1f, 0.35f, 128, 64);
            try
            {
                var s = Run(mesh, 2000).extraction.integerGridStats;
                Assert.AreEqual(s.flowSupply, s.flowRouted, "the flow left residual stranded\n" + s);
                Assert.Zero(s.unsatisfiedAfter, "faces still not closing after the flow\n" + s);
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void Icosphere_LandsNearTheEulerFloor()
        {
            var mesh = MeshCorpus.Icosphere(5, 1f);
            try
            {
                var result = Run(mesh, 2000);
                var e = result.extraction;

                Assert.GreaterOrEqual(e.quadFraction, 0.99f, $"quad fraction {e.quadFraction:F4}");
                var irregular = IrregularInterior(e, out var interior);
                Assert.LessOrEqual(irregular, 15,
                    $"{irregular} irregular of {interior} interior vertices; floor is 8\n" +
                    e.integerGridStats);
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void IsDeterministic()
        {
            var mesh = MeshCorpus.Icosphere(3, 1f);
            try
            {
                var a = Run(mesh, 200);
                var b = Run(mesh, 200);

                Assert.AreEqual(a.extraction.faceCount, b.extraction.faceCount);
                Assert.AreEqual(a.extraction.positions.Length, b.extraction.positions.Length);
                for (var i = 0; i < a.extraction.positions.Length; i++)
                    Assert.Less(Vector3.Distance(a.extraction.positions[i], b.extraction.positions[i]),
                        1e-5f, $"output vertex {i} differs between runs");
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void SurvivesOpenBoundary()
        {
            var mesh = MeshCorpus.OpenDisc(1f, 48, 10);
            try
            {
                RemeshResult result = null;
                Assert.DoesNotThrow(() => result = Run(mesh, 120));
                Assert.Greater(result.extraction.faceCount, 0);
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void SurvivesNonManifoldInput()
        {
            var mesh = new Mesh();
            mesh.SetVertices(new[]
            {
                new Vector3(0, -1, 0), new Vector3(0, 1, 0),
                new Vector3(1, 0, 0), new Vector3(-0.5f, 0, 0.87f), new Vector3(-0.5f, 0, -0.87f)
            });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 1, 3, 0, 1, 4 }, 0, false);
            try
            {
                Assert.DoesNotThrow(() => Run(mesh, 8));
            }
            finally { Object.DestroyImmediate(mesh); }
        }
    }
}
