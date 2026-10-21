// The hard-surface fixes of round 30: an open tube is not lidded, dense creases are detail and lone
// ones edges, a strip's width is the gap between its borders, and the decimation stops short of
// its budget rather than cut across the shape.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class HardSurfaceTests
    {
        // A grid of quads (two triangles each) over (u, v) in [0, 1]^2, mapped by `at`.
        static void Sheet(int nu, int nv, System.Func<float, float, Vector3> at, out Vector3[] positions,
            out int[] triangles, bool wrapU = false)
        {
            var columns = wrapU ? nu : nu + 1;
            var p = new List<Vector3>();
            for (var j = 0; j <= nv; j++)
                for (var i = 0; i < columns; i++)
                    p.Add(at(i / (float)nu, j / (float)nv));
            var t = new List<int>();
            for (var j = 0; j < nv; j++)
                for (var i = 0; i < nu; i++)
                {
                    var i1 = wrapU ? (i + 1) % nu : i + 1;
                    int a = j * columns + i, b = j * columns + i1, c = (j + 1) * columns + i1, d = (j + 1) * columns + i;
                    t.AddRange(new[] { a, c, b, a, d, c });
                }
            positions = p.ToArray();
            triangles = t.ToArray();
        }

        [Test]
        public void AnOpenTubeComesOutOpenNotLidded()
        {
            // A rim barrel: open at both ends. The closing passes used to zip each end shut with a
            // ladder of quads -- the flat striped disc over every wheel of the Porsche.
            Sheet(64, 24, (u, v) => new Vector3(Mathf.Cos(2f * Mathf.PI * u), Mathf.Sin(2f * Mathf.PI * u), 3f * v),
                out var positions, out var triangles, wrapU: true);
            var settings = RemeshSettings.Default;
            settings.targetQuadCount = 300;
            settings.modelType = ModelType.HardSurface;
            var profile = RemeshProfile.For(RemeshProfileKind.GameAsset);
            profile.extractor = RemeshExtractor.IntegerGrid;
            var result = RemeshPipeline.Solve(positions, triangles, settings, profile);
            var e = result.extraction;
            var nearAxis = 0;
            foreach (var p in e.positions)
                if (new Vector2(p.x, p.y).magnitude < 0.6f) nearAxis++;
            Assert.That(e.faceCount, Is.GreaterThan(50), e.ToString());
            Assert.That(nearAxis, Is.EqualTo(0), $"{nearAxis} vertices inside the tube: an end was capped");
        }

        [Test]
        public void DenseCreasesAreDetailAndALoneCreaseIsAnEdge()
        {
            // A fold: one crease down the middle, kept.
            Sheet(40, 10, (u, v) =>
            {
                var x = 4f * u;
                return new Vector3(x, x < 2f ? 0f : x - 2f, 2f * v);
            }, out var foldP, out var foldT);
            var fold = RemeshTopology.Build(foldP, foldT);
            var foldCreases = FeatureGraph.Detect(fold, 20f);
            var keptFold = foldCreases.KeepSparseCreases(fold, 0.5f, 4.5f, out var droppedFold);
            Assert.That(foldCreases.featureEdgeCount - fold.boundaryEdgeCount, Is.GreaterThan(0));
            Assert.That(droppedFold, Is.EqualTo(0));
            Assert.That(keptFold.featureEdgeCount, Is.EqualTo(foldCreases.featureEdgeCount));

            // Corrugation: a crease every 0.05 -- a tread at a 0.5 cell. All of it dropped.
            Sheet(80, 10, (u, v) =>
            {
                var x = 4f * u;
                return new Vector3(x, (Mathf.RoundToInt(x / 0.05f) % 2) * 0.05f, 2f * v);
            }, out var treadP, out var treadT);
            var tread = RemeshTopology.Build(treadP, treadT);
            var treadCreases = FeatureGraph.Detect(tread, 20f);
            var keptTread = treadCreases.KeepSparseCreases(tread, 0.5f, 4.5f, out var droppedTread);
            Assert.That(droppedTread, Is.GreaterThan(0.9f * (treadCreases.featureEdgeCount - tread.boundaryEdgeCount)),
                $"dropped {droppedTread} of {treadCreases.featureEdgeCount}");
            Assert.That(keptTread.featureEdgeCount, Is.LessThan(treadCreases.featureEdgeCount));
        }

        [Test]
        public void AStripsWidthIsTheGapBetweenItsBordersAndAPanelHasNone()
        {
            Sheet(100, 4, (u, v) => new Vector3(10f * u, 0f, 0.4f * v), out var stripP, out var stripT);
            var strip = RemeshTopology.Build(stripP, stripT);
            var reach = new float[strip.vertexCount];
            for (var i = 0; i < reach.Length; i++) reach[i] = 2f;
            var width = ScaleField.StripWidth(strip, reach);
            var middle = Nearest(strip.positions, new Vector3(5f, 0f, 0.2f));
            var edge = Nearest(strip.positions, new Vector3(5f, 0f, 0f));
            Assert.That(width[middle], Is.EqualTo(0.4f).Within(0.05f));
            Assert.That(width[edge], Is.EqualTo(0.4f).Within(0.05f));

            Sheet(40, 40, (u, v) => new Vector3(10f * u, 0f, 10f * v), out var panelP, out var panelT);
            var panel = RemeshTopology.Build(panelP, panelT);
            reach = new float[panel.vertexCount];
            for (var i = 0; i < reach.Length; i++) reach[i] = 2f;
            width = ScaleField.StripWidth(panel, reach);
            Assert.That(float.IsPositiveInfinity(width[Nearest(panel.positions, new Vector3(5f, 0f, 0f))]));
            Assert.That(float.IsPositiveInfinity(width[Nearest(panel.positions, new Vector3(5f, 0f, 5f))]));
        }

        [Test]
        public void DecimationStopsShortRatherThanCutAcrossTheShape()
        {
            // A dome: its curve is what any collapse past a few hundred triangles cuts across.
            Sheet(60, 60, (u, v) =>
            {
                float x = 2f * u - 1f, z = 2f * v - 1f;
                return new Vector3(x, 0.5f * Mathf.Max(0f, 1f - x * x - z * z), z);
            }, out var positions, out var triangles);
            var free = DecimateOptions.ForTriangleCount(40);
            PreDecimate.Decimate(positions, triangles, free, out var freeStats, out _, out var freeT);
            var bounded = DecimateOptions.ForTriangleCount(40);
            bounded.maxDistance = 0.01f;
            PreDecimate.Decimate(positions, triangles, bounded, out var boundedStats, out _, out var boundedT);
            Assert.That(freeT.Length / 3, Is.LessThanOrEqualTo(60), freeStats.ToString());
            Assert.That(boundedT.Length / 3, Is.GreaterThan(2 * freeT.Length / 3), boundedStats.ToString());
            Assert.That(boundedStats.rejectedError, Is.GreaterThan(0));
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
