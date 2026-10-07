// The ridge/valley curve network: on a cube the only curves are the twelve edges; smooth shapes get
// none; and through the pipeline, with crease detection off, an edge traced this way comes out as a
// row of output vertices sitting on it.

using NUnit.Framework;
using NativeRemesher.Benchmark;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class CurveNetworkTests
    {
        /// <summary>On an edge of the cube [-1, 1]^3: at least two coordinates at +-1.</summary>
        static bool OnCubeEdge(Vector3 p, float tolerance)
        {
            var atFace = 0;
            if (Mathf.Abs(Mathf.Abs(p.x) - 1f) < tolerance) atFace++;
            if (Mathf.Abs(Mathf.Abs(p.y) - 1f) < tolerance) atFace++;
            if (Mathf.Abs(Mathf.Abs(p.z) - 1f) < tolerance) atFace++;
            return atFace >= 2;
        }

        [Test]
        public void CubeEdgesAreTheCurves()
        {
            var mesh = MeshCorpus.SubdividedCube(2f, 32);
            // A clean 90-degree edge measures about 0.67 (see CurveNetwork.seedTurn), under the
            // defaults, which are set for sharper folds. This tests the tracing, so it starts and
            // continues where a right angle clearly qualifies.
            var saved = (CurveNetwork.seedTurn, CurveNetwork.continueTurn);
            (CurveNetwork.seedTurn, CurveNetwork.continueTurn) = (0.5f, 0.3f);
            try
            {
                var topology = RemeshTopology.Build(mesh);
                var cell = topology.EdgeLengthForQuadCount(600);
                var edges = CurveNetwork.Trace(topology, cell, out var tangents, out var corners,
                    out var stats);

                Assert.Greater(stats.curves, 0, stats.ToString());
                var off = 0;
                foreach (var (a, b) in edges)
                    if (!OnCubeEdge(topology.positions[a], 0.5f * cell)
                        || !OnCubeEdge(topology.positions[b], 0.5f * cell))
                        off++;
                Assert.AreEqual(0, off, $"{off} of {edges.Count} curve edges off the cube's edges; {stats}");

                // Most of the 24 units of edge; the last cell or so into each corner is where the
                // corner's two-way bending stops a curve.
                Assert.Greater(stats.lengthCells * cell, 24f * 0.6f, stats.ToString());

                // Along an edge the direction is the edge's own axis -- away from the corners,
                // where the half-cell chord the direction is taken over reaches round the bend.
                foreach (var (a, _) in edges)
                {
                    var t = tangents[a];
                    if (t == Vector3.zero) continue;
                    var q = topology.positions[a];
                    if (Mathf.Abs(q.x) > 1f - cell && Mathf.Abs(q.y) > 1f - cell
                        && Mathf.Abs(q.z) > 1f - cell) continue;
                    var axis = Mathf.Max(Mathf.Abs(t.x), Mathf.Max(Mathf.Abs(t.y), Mathf.Abs(t.z)));
                    Assert.Greater(axis, 0.95f, $"tangent {t} at {topology.positions[a]}");
                }
            }
            finally
            {
                (CurveNetwork.seedTurn, CurveNetwork.continueTurn) = saved;
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void SmoothShapesHaveNoCurves()
        {
            var sphere = MeshCorpus.Icosphere(4, 1f);
            var plane = MeshCorpus.GridPlane(2f, 24);
            try
            {
                foreach (var mesh in new[] { sphere, plane })
                {
                    var topology = RemeshTopology.Build(mesh);
                    var cell = topology.EdgeLengthForQuadCount(400);
                    var edges = CurveNetwork.Trace(topology, cell, out _, out var stats);
                    Assert.AreEqual(0, edges.Count, $"{mesh.name}: {stats}");
                }
            }
            finally
            {
                Object.DestroyImmediate(sphere);
                Object.DestroyImmediate(plane);
            }
        }

        [Test]
        public void PipelineCutsAlongTracedEdges()
        {
            var mesh = MeshCorpus.SubdividedCube(2f, 32);
            var saved = Extraction.IntegerGridMap.curveNetwork;
            // The crease-loop snap also pulls loops onto the cube's edges, in both arms, and so
            // measures itself rather than the curves; held off so this isolates the curve network.
            var savedSnap = Extraction.IntegerGridMap.creaseLoopSnap;
            Extraction.IntegerGridMap.creaseLoopSnap = false;
            try
            {
                var settings = RemeshSettings.Default;
                settings.targetQuadCount = 600;
                // The scan profile: no dihedral creases, so the edges can only come from the curves.
                var profile = RemeshProfile.For(RemeshProfileKind.Photogrammetry);
                profile.extractor = RemeshExtractor.IntegerGrid;

                int EdgeVertices(bool curves, out string note)
                {
                    Extraction.IntegerGridMap.curveNetwork = curves;
                    var result = RemeshPipeline.Run(mesh, settings, profile);
                    note = result.curveNetworkStats?.ToString() ?? "off";
                    if (curves) Assert.NotNull(result.curveNetworkStats);
                    Assert.Greater(result.extraction.quadFraction, 0.95f);
                    var count = 0;
                    foreach (var p in result.extraction.positions)
                        if (OnCubeEdge(p, 0.005f)) count++;
                    return count;
                }

                var without = EdgeVertices(false, out _);
                var with = EdgeVertices(true, out var stats);
                Assert.Greater(with, without, $"edge vertices: {with} with curves, {without} without; {stats}");
            }
            finally
            {
                Extraction.IntegerGridMap.curveNetwork = saved;
                Extraction.IntegerGridMap.creaseLoopSnap = savedSnap;
                Object.DestroyImmediate(mesh);
            }
        }
    }
}
