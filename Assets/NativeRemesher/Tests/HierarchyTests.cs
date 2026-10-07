// The hierarchy's contract, and the depth rule that keeps it from making things worse.
//
// Two things here are load-bearing beyond their size. UsefulPositionLevels is the whole reason
// cylinder_capped does not regress from 0.0008 worst-case deviation to 0.2, and it is a monotone
// scan over an array — exactly the kind of thing that is right until someone changes an inequality.
// And OrientationField's initialDirections path is what the hierarchy's prolongation and the plan's
// externally-supplied-field extension point both rely on: it must sanitise rather than trust, since
// a prolongated direction is tangent to the wrong normal and anything from outside may not be unit.

using NativeRemesher.Benchmark;
using NUnit.Framework;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class HierarchyTests
    {
        static RemeshTopology Sphere() =>
            BuildTopology(MeshCorpus.Icosphere(3, 1f));

        static RemeshTopology BuildTopology(Mesh mesh)
        {
            try
            {
                RemeshTopology.ReadMesh(mesh, out var positions, out var triangles);
                return RemeshTopology.Build(positions, triangles);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void HierarchyCoarsensAndEveryLevelIsColoured()
        {
            var topology = Sphere();
            var hierarchy = MeshHierarchy.Build(topology);

            Assert.Greater(hierarchy.levelCount, 3, "a 640-triangle sphere should coarsen several times");
            Assert.AreEqual(topology.vertexCount, hierarchy.levels[0].vertexCount);

            for (var level = 1; level < hierarchy.levelCount; level++)
            {
                Assert.Less(hierarchy.levels[level].vertexCount,
                    hierarchy.levels[level - 1].vertexCount,
                    $"level {level} did not shrink");

                Assert.Greater(hierarchy.levels[level].averageEdgeLength,
                    hierarchy.levels[level - 1].averageEdgeLength,
                    $"level {level} should have longer edges than the level below it");
            }

            foreach (var level in hierarchy.levels)
            {
                Assert.NotNull(level.colouring, "every level needs a colouring to be swept");
                Assert.Greater(level.colouring.classCount, 0);
            }
        }

        /// <summary>
        /// Each fine vertex belongs to exactly one coarse vertex, and each coarse vertex claims the
        /// one or two it was built from. Prolongation reads toUpperA/B and restriction reads
        /// toLower, so a disagreement between them silently mixes levels.
        /// </summary>
        [Test]
        public void MergeMapsAreConsistentBothWays()
        {
            var topology = Sphere();
            var hierarchy = MeshHierarchy.Build(topology);

            for (var level = 1; level < hierarchy.levelCount; level++)
            {
                var fine = hierarchy.levels[level - 1];
                var coarse = hierarchy.levels[level];

                Assert.AreEqual(fine.vertexCount, coarse.toLower.Length);

                var claimed = new int[fine.vertexCount];

                for (var c = 0; c < coarse.vertexCount; c++)
                {
                    var a = coarse.toUpperA[c];
                    Assert.GreaterOrEqual(a, 0, $"coarse {c} has no first child");
                    Assert.AreEqual(c, coarse.toLower[a]);
                    claimed[a]++;

                    var b = coarse.toUpperB[c];
                    if (b == HierarchyLevel.invalid) continue;

                    Assert.AreEqual(c, coarse.toLower[b]);
                    claimed[b]++;
                }

                for (var f = 0; f < fine.vertexCount; f++)
                    Assert.AreEqual(1, claimed[f], $"fine vertex {f} is claimed {claimed[f]} times");
            }
        }

        /// <summary>
        /// A lattice much coarser than the finest edges admits several levels; one at the input's
        /// own edge length admits only the finest. This is the rule, stated as the two ends of it.
        /// </summary>
        [Test]
        public void PositionDepthFollowsTheLatticeSpacing()
        {
            var topology = Sphere();
            var hierarchy = MeshHierarchy.Build(topology);

            var finestEdge = hierarchy.levels[0].averageEdgeLength;

            var atInputSpacing = HierarchicalSolve.UsefulPositionLevels(hierarchy, finestEdge, 1f);
            Assert.AreEqual(1, atInputSpacing,
                "a lattice as fine as the input's own edges leaves no coarse level anything to say");

            var atEightTimes = HierarchicalSolve.UsefulPositionLevels(hierarchy, finestEdge * 8f, 1f);
            Assert.Greater(atEightTimes, atInputSpacing,
                "a lattice eight times the input edge should reach several levels");
            Assert.LessOrEqual(atEightTimes, hierarchy.levelCount);

            // Monotone in both arguments, which is what makes the rule predictable. A coarser
            // lattice can only admit more levels; a larger multiple can only admit fewer.
            var atSixteen = HierarchicalSolve.UsefulPositionLevels(hierarchy, finestEdge * 16f, 1f);
            Assert.GreaterOrEqual(atSixteen, atEightTimes);

            var shallower = HierarchicalSolve.UsefulPositionLevels(hierarchy, finestEdge * 8f, 4f);
            Assert.LessOrEqual(shallower, atEightTimes);
        }

        /// <summary>
        /// Every level solved must have edges no longer than one lattice cell. Asserted directly
        /// against the level data rather than trusting the loop that produced the count.
        /// </summary>
        [Test]
        public void EverySolvedLevelIsFinerThanTheLattice()
        {
            var topology = Sphere();
            var hierarchy = MeshHierarchy.Build(topology);
            var scale = hierarchy.levels[0].averageEdgeLength * 6f;

            var used = HierarchicalSolve.UsefulPositionLevels(hierarchy, scale, 1f);

            for (var level = 1; level < used; level++)
                Assert.LessOrEqual(hierarchy.levels[level].averageEdgeLength, scale,
                    $"level {level} was admitted with edges longer than the lattice cell");

            if (used < hierarchy.levelCount)
                Assert.Greater(hierarchy.levels[used].averageEdgeLength, scale,
                    "the first excluded level should be the first one too coarse");
        }

        /// <summary>
        /// The hierarchy has to reach the position field through the pipeline, not just exist. A
        /// profile with the hierarchy off must solve exactly one level.
        /// </summary>
        [Test]
        public void PipelineSolvesMultipleLevelsOnACoarseTargetAndOneWhenDisabled()
        {
            var mesh = MeshCorpus.Icosphere(5, 1f);
            var other = MeshCorpus.Icosphere(5, 1f);

            try
            {
                var settings = RemeshSettings.Default;
                settings.targetQuadCount = 500;

                var on = RemeshProfile.Default;
                var off = RemeshProfile.Default;
                off.hierarchyLevelIterations = -1;

                var withHierarchy = RemeshPipeline.Run(mesh, settings, on);
                var without = RemeshPipeline.Run(other, settings, off);

                Assert.NotNull(withHierarchy.hierarchy, "the hierarchy was never built");
                Assert.Greater(withHierarchy.positionLevels.levels, 1,
                    "a 500-quad target on a 20k-triangle sphere should reach past the finest level");

                Assert.IsNull(without.hierarchy, "a negative level count must skip the build entirely");
                Assert.IsNull(without.positionLevels);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mesh);
                UnityEngine.Object.DestroyImmediate(other);
            }
        }

        /// <summary>
        /// initialDirections must be sanitised, not trusted. Prolongation hands over a direction
        /// tangent to a DIFFERENT vertex's normal, and an external producer may hand over something
        /// not even unit length — and PositionField feeds these straight into Rotate90 with no
        /// validation of its own.
        /// </summary>
        [Test]
        public void SuppliedInitialDirectionsAreProjectedAndNormalised()
        {
            var topology = Sphere();

            var supplied = new Vector3[topology.vertexCount];
            for (var i = 0; i < supplied.Length; i++)
            {
                // Deliberately wrong on both counts: 17 units long, and mostly along the normal.
                supplied[i] = topology.normals[i] * 17f + Vector3.right * 0.5f;
            }

            var field = OrientationField.Solve(topology, 0, null, -1, null, null, supplied);

            for (var i = 0; i < field.directions.Length; i++)
            {
                Assert.AreEqual(1f, field.directions[i].magnitude, 1e-4f,
                    $"direction {i} is not unit length");
                Assert.AreEqual(0f, Vector3.Dot(field.directions[i], topology.normals[i]), 1e-4f,
                    $"direction {i} is not tangent");
            }
        }

        /// <summary>
        /// With zero sweeps the field is exactly what was supplied, sanitised. That is what lets a
        /// caller SCORE an externally produced field rather than only seed one — the plan's stated
        /// use for this parameter, and it needs the iteration floor to be zero rather than one.
        /// </summary>
        [Test]
        public void ZeroSweepsReturnsTheSuppliedFieldUnchanged()
        {
            var topology = Sphere();

            var supplied = new Vector3[topology.vertexCount];
            for (var i = 0; i < supplied.Length; i++)
                supplied[i] = OrientationField.ArbitraryTangent(topology.normals[i]);

            var field = OrientationField.Solve(topology, 0, null, -1, null, null, supplied);

            for (var i = 0; i < supplied.Length; i++)
                Assert.Less(Vector3.Distance(field.directions[i], supplied[i]), 1e-5f,
                    $"direction {i} moved despite a zero sweep budget");
        }

        /// <summary>
        /// initialPositions is the same contract for the lattice, and the one prolongation actually
        /// uses. Supplied points must survive as the starting state rather than being replaced by
        /// the vertices.
        /// </summary>
        [Test]
        public void SuppliedInitialPositionsSeedTheLattice()
        {
            var topology = Sphere();
            var orientation = OrientationField.Solve(topology, 10);
            var scale = topology.averageEdgeLength * 3f;

            var offset = new Vector3(0.013f, -0.007f, 0.021f);
            var supplied = new Vector3[topology.vertexCount];
            for (var i = 0; i < supplied.Length; i++) supplied[i] = topology.positions[i] + offset;

            var seeded = PositionField.Solve(topology, orientation, scale, 0, null, supplied);
            var plain = PositionField.Solve(topology, orientation, scale, 0);

            var moved = 0;
            for (var i = 0; i < supplied.Length; i++)
                if (Vector3.Distance(seeded.positions[i], plain.positions[i]) > 1e-6f) moved++;

            Assert.Greater(moved, supplied.Length / 2,
                "seeding the lattice changed almost nothing, so the parameter is not being read");
        }
    }
}
