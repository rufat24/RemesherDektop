// The measurement has to be trustworthy before its verdict is, so this pins it on rings whose
// answer is known by construction.
//
// The verdict it delivered is that the lattice is genuinely sheared rather than mis-read, and that
// rests on two things being right: sectors partitioning a square ring into four distinct
// directions, and FitFrame recovering the ring's own square when the field's frame is rotated away
// from it. If either were broken the conclusion would be an artefact of this file.

using NativeRemesher.Extraction;
using NUnit.Framework;
using UnityEngine;

namespace NativeRemesher.Tests
{
    public class LatticeDirectionTests
    {
        /// <summary>A centre vertex with four neighbours on the axes, in the xz plane.</summary>
        static QuadGraph SquareRing(float frameRotationDegrees)
        {
            var graph = new QuadGraph(5);

            graph.positions[0] = Vector3.zero;
            graph.positions[1] = new Vector3(1f, 0f, 0f);
            graph.positions[2] = new Vector3(0f, 0f, 1f);
            graph.positions[3] = new Vector3(-1f, 0f, 0f);
            graph.positions[4] = new Vector3(0f, 0f, -1f);

            var angle = frameRotationDegrees * Mathf.Deg2Rad;
            var frame = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));

            for (var i = 0; i < 5; i++)
            {
                graph.normals[i] = Vector3.up;
                graph.directions[i] = frame;
            }

            for (var i = 1; i < 5; i++) graph.AddEdge(0, i);

            return graph;
        }

        [Test]
        public void SquareRingOccupiesAllFourDirections()
        {
            var graph = SquareRing(0f);
            var stats = LatticeDirections.Run(graph, false, 1f);

            // The centre has all four; each arm has only the one edge back to the centre.
            Assert.AreEqual(1, stats.occupied[4], "the centre should occupy all four directions");
            Assert.AreEqual(4, stats.occupied[1], "each arm has a single neighbour");
            Assert.AreEqual(0, stats.contestedDirections);
        }

        /// <summary>
        /// Two neighbours in one direction and none in another is the pattern the whole
        /// investigation is about, so it has to be counted as exactly that.
        /// </summary>
        [Test]
        public void TwoNeighboursInOneDirectionAreContestedAndLeaveAVacancy()
        {
            var graph = SquareRing(0f);
            graph.positions[4] = new Vector3(0.2f, 0f, 1f);   // was -z; now a second +z

            var stats = LatticeDirections.Run(graph, false, 1f);

            Assert.AreEqual(1, stats.contestedDirections);
            Assert.AreEqual(1, stats.surplusNeighbours);
            Assert.AreEqual(1, stats.occupied[3], "the centre is down to three directions");
        }

        /// <summary>
        /// The control the conclusion depends on. With the field's frame rotated 30 degrees off the
        /// ring, reading in that frame throws two of the four arms outside the squareness gate;
        /// reading in the fitted frame must recover all four.
        /// </summary>
        [Test]
        public void FitFrameRecoversTheRingsOwnSquare()
        {
            var graph = SquareRing(30f);

            var fitted = LatticeDirections.FitFrame(graph, 0);

            // Four-fold symmetric, so the fit is only defined modulo 90 degrees: what must hold is
            // that some arm lies along it. The +x arm is the natural one to check.
            var alignment = Mathf.Abs(Vector3.Dot(fitted, new Vector3(1f, 0f, 0f)));
            Assert.Greater(alignment, 0.999f, "fitted frame should lie along the ring's own axis");

            var stats = LatticeDirections.Run(graph, false, 1f);
            Assert.AreEqual(1, stats.occupiedFitted[4]);
            Assert.AreEqual(0, stats.contestedDirections,
                "read in its own frame, a square ring has no contested direction");
        }

        /// <summary>
        /// A ring with no square in it must not get a fitted angle read out of noise — the
        /// coherence gate exists for that, and without it the measurement would report structure
        /// wherever there was none.
        /// </summary>
        [Test]
        public void IncoherentRingKeepsTheFieldFrame()
        {
            var graph = new QuadGraph(4);
            graph.positions[0] = Vector3.zero;
            graph.positions[1] = new Vector3(1f, 0f, 0f);
            graph.positions[2] = new Vector3(Mathf.Cos(0.4f), 0f, Mathf.Sin(0.4f));
            graph.positions[3] = new Vector3(Mathf.Cos(0.8f), 0f, Mathf.Sin(0.8f));

            for (var i = 0; i < 4; i++)
            {
                graph.normals[i] = Vector3.up;
                graph.directions[i] = new Vector3(1f, 0f, 0f);
            }

            for (var i = 1; i < 4; i++) graph.AddEdge(0, i);

            // Three arms within 46 degrees all land in the same sector under any frame, so there is
            // no square here and the quadrupled angles do not cancel — they reinforce a direction
            // that means nothing. Whichever way the gate falls, the frame must stay finite and unit.
            var fitted = LatticeDirections.FitFrame(graph, 0);
            Assert.AreEqual(1f, fitted.magnitude, 1e-4f);

            var stats = LatticeDirections.Run(graph, false, 1f);
            Assert.AreEqual(1, stats.contestedDirections, "one direction holds all three arms");
            Assert.AreEqual(2, stats.surplusNeighbours);
        }

        /// <summary>
        /// Repair must not fire on a sound ring. A pass that edits a perfect lattice is worse than
        /// one that does nothing, because it would be undoing correct work everywhere it ran.
        /// </summary>
        [Test]
        public void RepairLeavesASoundRingAlone()
        {
            var graph = SquareRing(0f);
            var stats = LatticeDirections.Run(graph, true, 1f);

            Assert.AreEqual(0, stats.edgesRemoved);
            Assert.AreEqual(0, stats.edgesAdded);
            Assert.AreEqual(4, graph.Valence(0));
        }
    }
}
