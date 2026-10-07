// Principal-curvature guides: on a cylinder they must lie along the rings or the axis (a cross field
// cannot tell the two apart, and either is right), and a sphere -- umbilic everywhere -- must get
// next to none, because there is no direction there to prefer.

using NUnit.Framework;
using NativeRemesher.Benchmark;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class CurvatureDirectionTests
    {
        [Test]
        public void CylinderGuidesFollowRingsOrAxis()
        {
            var mesh = MeshCorpus.CappedCylinder(0.5f, 2f, 48, 32);
            try
            {
                var topology = RemeshTopology.Build(mesh);
                var cell = topology.EdgeLengthForQuadCount(400);
                CurvatureDirections.Compute(topology, cell, 1f, 1f, out var directions,
                    out var weights, out var stats);

                var side = 0;
                var aligned = 0;
                for (var i = 0; i < topology.vertexCount; i++)
                {
                    var p = topology.positions[i];
                    // The wall only, well away from the cap rims where the shape is a corner.
                    if (Mathf.Abs(p.y) > 0.6f) continue;
                    side++;
                    if (weights[i] <= 0f) continue;
                    // 4-RoSy: along the axis or across it both count.
                    var c = Mathf.Abs(Vector3.Dot(directions[i], Vector3.up));
                    if (c > 0.95f || c < 0.05f) aligned++;
                }

                Assert.Greater(side, 100);
                Assert.Greater(aligned, side * 0.9f, $"{aligned}/{side} wall vertices aligned; {stats}");
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void SphereGetsAlmostNoGuides()
        {
            var mesh = MeshCorpus.Icosphere(4, 1f);
            try
            {
                var topology = RemeshTopology.Build(mesh);
                var cell = topology.EdgeLengthForQuadCount(400);
                CurvatureDirections.Compute(topology, cell, 1f, 1f, out _, out _, out var stats);
                Assert.Less(stats.guided, topology.vertexCount / 10, stats.ToString());
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void GuidePullsAFlatFieldWhereASoftConstraintDoesNot()
        {
            // On a flat plane nothing opposes the guide, so a guided solve started from some other
            // direction must end on the guide: the difference between a pull and a starting value.
            var mesh = MeshCorpus.GridPlane(2f, 12);
            try
            {
                var topology = RemeshTopology.Build(mesh);
                var target = new Vector3(1f, 0f, 1f).normalized;
                var guides = new Vector3[topology.vertexCount];
                var weights = new float[topology.vertexCount];
                var start = new Vector3[topology.vertexCount];
                for (var i = 0; i < guides.Length; i++)
                {
                    guides[i] = target;
                    weights[i] = 0.2f;
                    start[i] = Vector3.right;
                }

                var field = OrientationField.Solve(topology, 200, null, -1, null, null, start,
                    guides, weights);

                var worst = 0f;
                for (var i = 0; i < topology.vertexCount; i++)
                {
                    // Distance to the nearest of the four symmetric copies of the target.
                    var d = field.directions[i];
                    var c = Mathf.Max(Mathf.Abs(Vector3.Dot(d, target)),
                        Mathf.Abs(Vector3.Dot(d, Vector3.Cross(Vector3.up, target))));
                    worst = Mathf.Max(worst, 1f - c);
                }
                Assert.Less(worst, 1e-3f);
            }
            finally { Object.DestroyImmediate(mesh); }
        }
    }
}
