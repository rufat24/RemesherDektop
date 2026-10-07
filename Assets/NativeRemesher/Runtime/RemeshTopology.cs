// Native quad remesher for Unity — Stage 1: topology.
//
// Implemented from the published method:
//   Jakob, Tarini, Panozzo, Sorkine-Hornung, "Instant Field-Aligned Meshes", SIGGRAPH Asia 2015
//   Huang, Zhou, Niessner, Shewchuk, Guibas, "QuadriFlow", SGP 2018
//
// Everything downstream (orientation field, position field, extraction) works on a *welded*
// vertex graph. A Unity render mesh is not that: it splits vertices at every UV and normal seam,
// so a sphere that looks closed is topologically a pile of disconnected patches. Field smoothing
// over that would propagate nothing across the seams. So the first job is to rebuild the real
// surface graph.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativeRemesher
{
    /// <summary>
    /// A welded vertex graph with the per-vertex quantities the field solvers need.
    /// Adjacency is stored in CSR form: neighbours of vertex <c>i</c> are
    /// <c>adjacency[adjacencyStart[i] .. adjacencyStart[i + 1] - 1]</c>.
    /// </summary>
    public sealed class RemeshTopology
    {
        public Vector3[] positions;
        /// <summary>Area-weighted vertex normals, unit length.</summary>
        public Vector3[] normals;
        /// <summary>Barycentric vertex area (a third of each incident triangle).</summary>
        public float[] areas;

        public int[] adjacencyStart;
        public int[] adjacency;

        /// <summary>Triangle corners, as indices into <see cref="positions"/>.</summary>
        public int[] triangles;

        /// <summary>
        /// Maps a source mesh vertex index to its welded index. Kept because every guidance array
        /// a caller might supply (density, constraints, an externally computed field) is indexed by
        /// *welded* vertex, while anything outside this class — a Python sidecar, a baked asset, an
        /// authoring tool — only ever sees the *source* mesh. Without this there is no way to line
        /// the two up.
        /// </summary>
        public int[] sourceToWelded;

        // --- half-edge structure -------------------------------------------
        // Directed edges, three per triangle: half-edges 3t, 3t+1, 3t+2 belong to triangle t, and
        // half-edge h runs from halfEdgeVertex[h] to halfEdgeVertex[Next(h)].
        //
        // Next and Prev are arithmetic rather than arrays, so only the twin needs storing.

        /// <summary>Origin vertex of each directed edge. Length 3 * triangleCount.</summary>
        public int[] halfEdgeVertex;

        /// <summary>The opposing directed edge, or -1 on a boundary.</summary>
        public int[] halfEdgeTwin;

        /// <summary>
        /// One outgoing directed edge per vertex. For a boundary vertex this is specifically the
        /// first edge of its fan, so walking forward from it covers the whole ring exactly once.
        /// -1 for an isolated vertex.
        /// </summary>
        public int[] vertexHalfEdge;

        public bool[] isBoundaryVertex;

        /// <summary>
        /// Vertices touching an edge with more than two incident triangles, or an edge traversed
        /// twice in the same direction. Their neighbour ring is not a single cycle, so anything that
        /// assumes a clean fan must skip them rather than produce nonsense.
        /// </summary>
        public bool[] isNonManifoldVertex;

        public int boundaryEdgeCount { get; private set; }
        public int nonManifoldEdgeCount { get; private set; }

        public float averageEdgeLength;
        public float totalArea;

        public int vertexCount => positions.Length;
        public int triangleCount => triangles.Length / 3;

        /// <summary>Number of source vertices that collapsed away during welding.</summary>
        public int weldedAway { get; private set; }

        /// <summary>Vertices with no neighbours — isolated points the solvers must skip.</summary>
        public int isolatedVertices { get; private set; }

        /// <summary>
        /// Per-stage milliseconds for the last <see cref="Build"/>, for diagnosing a slow build.
        /// </summary>
        /// <remarks>
        /// Not decoration. A 1M-triangle asset once spent 29 minutes in here and nothing in the
        /// project could say which stage was responsible, because every benchmark row was capped at
        /// 25k triangles — the range where a linear and a quadratic stage are indistinguishable.
        /// </remarks>
        public string buildTimings { get; private set; }


        /// <summary>
        /// The input's own boundary as a flat edge list, two vertex indices per edge.
        /// </summary>
        /// <remarks>
        /// A boundary half-edge is one with no twin, and each such edge is unpaired by definition,
        /// so this needs no deduplication. Non-manifold half-edges also carry twin -1 and are
        /// therefore included; that is the conservative choice for the one consumer, which uses
        /// this to decide where NOT to cap a hole.
        /// </remarks>
        public void CollectBoundaryEdges(out int[] edges)
        {
            if (halfEdgeTwin == null || boundaryEdgeCount == 0)
            {
                edges = Array.Empty<int>();
                return;
            }

            edges = new int[boundaryEdgeCount * 2];
            var write = 0;

            for (var h = 0; h < halfEdgeTwin.Length && write + 1 < edges.Length; h++)
            {
                if (halfEdgeTwin[h] >= 0) continue;

                edges[write++] = halfEdgeVertex[h];
                edges[write++] = HalfEdgeTarget(h);
            }

            if (write != edges.Length)
                Array.Resize(ref edges, write);
        }

        GraphColouring m_Colouring;

        /// <summary>
        /// An independent-set partition of the vertices, built on first use and cached.
        /// </summary>
        /// <remarks>
        /// Lazy rather than part of <c>Build</c> because the topology is also built for callers
        /// that never sweep a field over it — the decimator builds one per invocation — and
        /// colouring half a million vertices for those would be pure waste.
        /// </remarks>
        public GraphColouring colouring =>
            m_Colouring ??= GraphColouring.Build(vertexCount, adjacencyStart, adjacency);

        /// <summary>
        /// A field-solver view over an already-coarsened vertex graph.
        /// </summary>
        /// <remarks>
        /// NOT a topology, and the name says so on purpose: there are no triangles, no half-edges,
        /// no boundary flags and no welding here. It exists so the two field solvers can run on a
        /// MeshHierarchy level without either of them being rewritten — both read exactly
        /// positions, normals, CSR adjacency and a colouring, and nothing else — which keeps the
        /// hierarchy from being a second copy of two solvers that are already tested.
        ///
        /// Anything that reaches for a triangle through one of these will get a null reference
        /// immediately rather than a wrong answer quietly, which is the intended failure mode.
        /// </remarks>
        public static RemeshTopology VertexGraphView(Vector3[] positions, Vector3[] normals,
            float[] areas, int[] adjacencyStart, int[] adjacency, GraphColouring colouring)
        {
            return new RemeshTopology
            {
                positions = positions,
                normals = normals,
                areas = areas,
                adjacencyStart = adjacencyStart,
                adjacency = adjacency,
                m_Colouring = colouring,
            };
        }

        /// <summary>The next directed edge around the same triangle.</summary>
        public static int Next(int halfEdge) => halfEdge - halfEdge % 3 + (halfEdge + 1) % 3;

        /// <summary>The previous directed edge around the same triangle.</summary>
        public static int Prev(int halfEdge) => halfEdge - halfEdge % 3 + (halfEdge + 2) % 3;

        public static int TriangleOf(int halfEdge) => halfEdge / 3;

        /// <summary>Destination vertex of a directed edge.</summary>
        public int HalfEdgeTarget(int halfEdge) => halfEdgeVertex[Next(halfEdge)];

        /// <summary>
        /// Builds from a <see cref="Mesh"/>. Reads the mesh's arrays up front and defers to the
        /// array overload.
        /// </summary>
        public static RemeshTopology Build(Mesh mesh)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));

            ReadMesh(mesh, out var positions, out var triangles);
            return Build(positions, triangles);
        }

        /// <summary>
        /// Gathers a mesh's vertices and its triangle submeshes into plain arrays.
        /// </summary>
        /// <remarks>
        /// Split out from <see cref="Build(Mesh)"/> because the <see cref="Mesh"/> API is
        /// main-thread only, and reaching for it from inside the solve is what forced the whole
        /// pipeline onto the main thread — which is what froze the editor for the duration of a
        /// run. Call this once on the main thread; everything after it takes arrays and can run on
        /// a worker.
        /// </remarks>
        public static void ReadMesh(Mesh mesh, out Vector3[] positions, out int[] triangles)
        {
            ReadMesh(mesh, out positions, out triangles, out _);
        }

        /// <summary>
        /// As <see cref="ReadMesh(Mesh, out Vector3[], out int[])"/>, and also reads UV0 when the
        /// mesh carries one.
        /// </summary>
        /// <remarks>
        /// The UVs come out unwelded and index-parallel to <paramref name="positions"/> on purpose.
        /// Welding merges vertices that share a position, and at a UV seam those duplicates carry
        /// *different* UVs — that is what makes it a seam. Anything that samples the texture has to
        /// do it against this array, before the weld, or it blends across every seam in the mesh.
        /// </remarks>
        public static void ReadMesh(Mesh mesh, out Vector3[] positions, out int[] triangles,
            out Vector2[] uvs)
        {
            if (mesh == null)
                throw new ArgumentNullException(nameof(mesh));

            positions = mesh.vertices;

            var sourceUvs = mesh.uv;
            uvs = sourceUvs != null && sourceUvs.Length == positions.Length ? sourceUvs : null;

            var gathered = new List<int>(mesh.triangles.Length);
            for (var sub = 0; sub < mesh.subMeshCount; sub++)
            {
                if (mesh.GetTopology(sub) != MeshTopology.Triangles)
                    continue;

                gathered.AddRange(mesh.GetTriangles(sub));
            }

            triangles = gathered.ToArray();
        }

        /// <summary>
        /// Builds the welded, half-edge topology from plain arrays. Touches no Unity object, so it
        /// is safe to call from a worker thread.
        /// </summary>
        public static RemeshTopology Build(Vector3[] sourcePositions, int[] sourceTriangles)
        {
            if (sourcePositions == null)
                throw new ArgumentNullException(nameof(sourcePositions));
            if (sourcePositions.Length == 0)
                throw new ArgumentException("No vertices.", nameof(sourcePositions));

            sourceTriangles ??= Array.Empty<int>();

            var topology = new RemeshTopology();

            // --- weld ---
            // Seam-split duplicates are bitwise identical, so they always land in the same cell.
            // The epsilon additionally merges positions that are merely very close, which is
            // common in imported or scanned geometry.
            var watch = System.Diagnostics.Stopwatch.StartNew();

            // Computed rather than read from Mesh.bounds, which is the same axis-aligned box but
            // only reachable on the main thread.
            var min = sourcePositions[0];
            var max = min;
            for (var i = 1; i < sourcePositions.Length; i++)
            {
                var p = sourcePositions[i];
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            var epsilon = Mathf.Max((max - min).magnitude * 1e-6f, 1e-9f);
            var remap = new int[sourcePositions.Length];
            var welded = new List<Vector3>(sourcePositions.Length);

            // Explicit comparer, not the default. Unity's Vector3Int.GetHashCode XORs shifted
            // components, and the cell coordinates here are large (a position divided by a 1e-6
            // epsilon), highly structured, and strongly correlated across a subdivided surface —
            // the combination that turns a hash table into a set of long collision chains.
            var cells = new Dictionary<Vector3Int, int>(sourcePositions.Length, CellComparer.instance);

            for (var i = 0; i < sourcePositions.Length; i++)
            {
                var p = sourcePositions[i];
                var cell = new Vector3Int(
                    Mathf.RoundToInt(p.x / epsilon),
                    Mathf.RoundToInt(p.y / epsilon),
                    Mathf.RoundToInt(p.z / epsilon));

                if (!cells.TryGetValue(cell, out var index))
                {
                    index = welded.Count;
                    welded.Add(p);
                    cells.Add(cell, index);
                }

                remap[i] = index;
            }

            topology.positions = welded.ToArray();
            topology.weldedAway = sourcePositions.Length - welded.Count;
            topology.sourceToWelded = remap;

            var weldMs = watch.ElapsedMilliseconds;
            watch.Restart();

            // --- triangles, remapped onto welded indices ---
            var tris = new List<int>(sourceTriangles.Length);
            for (var i = 0; i + 2 < sourceTriangles.Length; i += 3)
            {
                int a = remap[sourceTriangles[i]], b = remap[sourceTriangles[i + 1]],
                    c = remap[sourceTriangles[i + 2]];

                // Welding can collapse a sliver into a degenerate triangle; drop those,
                // they carry no normal and would poison the area weights.
                if (a == b || b == c || a == c)
                    continue;

                tris.Add(a);
                tris.Add(b);
                tris.Add(c);
            }

            topology.triangles = tris.ToArray();

            var remapMs = watch.ElapsedMilliseconds;
            watch.Restart();

            topology.BuildAdjacency();
            var adjacencyMs = watch.ElapsedMilliseconds;
            watch.Restart();

            topology.ComputeNormalsAndAreas();
            var normalsMs = watch.ElapsedMilliseconds;
            watch.Restart();

            topology.ComputeAverageEdgeLength();
            var edgeLengthMs = watch.ElapsedMilliseconds;

            topology.buildTimings =
                $"weld={weldMs}ms remap={remapMs}ms adjacency={adjacencyMs}ms " +
                $"({topology.adjacencyTimings}) normals={normalsMs}ms edgeLength={edgeLengthMs}ms";

            return topology;
        }

        /// <summary>
        /// Hash for a weld cell that actually mixes. Unity's own <c>Vector3Int.GetHashCode</c> is a
        /// XOR of shifted components, which discards low bits of z and leaves the result strongly
        /// correlated for the near-lattice coordinates a weld produces.
        /// </summary>
        sealed class CellComparer : IEqualityComparer<Vector3Int>
        {
            public static readonly CellComparer instance = new CellComparer();

            public bool Equals(Vector3Int a, Vector3Int b) => a.x == b.x && a.y == b.y && a.z == b.z;

            public int GetHashCode(Vector3Int c)
            {
                // Three odd multipliers, then a finalising avalanche — enough to decorrelate
                // neighbouring lattice cells, which is all this needs to do.
                unchecked
                {
                    var h = (uint)c.x * 0x9E3779B1u;
                    h ^= (uint)c.y * 0x85EBCA77u;
                    h ^= (uint)c.z * 0xC2B2AE3Du;
                    h ^= h >> 15;
                    h *= 0x2545F491u;
                    h ^= h >> 13;
                    return (int)h;
                }
            }
        }

        /// <summary>
        /// Builds the half-edge structure, then derives the CSR adjacency from it with each
        /// vertex's neighbours in cyclic (fan) order.
        /// </summary>
        /// <remarks>
        /// The order is derived rather than sorted for two reasons. Cyclic order is what any pass
        /// reasoning about faces around a vertex needs — a plain ascending sort is deterministic but
        /// geometrically meaningless. And both field solvers are Gauss-Seidel, reading neighbours
        /// already updated within the same sweep, so their output depends on this order; deriving it
        /// from the mesh makes it reproducible, where filling from a HashSet left it unspecified.
        /// </remarks>
        /// <summary>Per-stage milliseconds inside <see cref="BuildAdjacency"/>.</summary>
        string adjacencyTimings;

        void BuildAdjacency()
        {
            var adjacencyWatch = System.Diagnostics.Stopwatch.StartNew();

            var count = positions.Length;
            var halfEdgeCount = triangles.Length;

            halfEdgeVertex = new int[halfEdgeCount];
            halfEdgeTwin = new int[halfEdgeCount];
            vertexHalfEdge = new int[count];
            isBoundaryVertex = new bool[count];
            isNonManifoldVertex = new bool[count];

            for (var h = 0; h < halfEdgeCount; h++)
            {
                halfEdgeVertex[h] = triangles[h];
                halfEdgeTwin[h] = -1;
            }
            for (var v = 0; v < count; v++) vertexHalfEdge[v] = -1;

            // --- incident outgoing half-edges per vertex, as CSR ---
            //
            // Built first, because everything below is a scan of one vertex's own list. This is the
            // reference's structure: `build_dedge` chains the outgoing edges of each vertex and then
            // finds each twin by walking the chain at its target. It never hashes an edge.
            //
            // What was here before did: a Dictionary<long, int> keyed on ((long)from << 32) | to.
            // In .NET, long.GetHashCode() is (int)v ^ (int)(v >> 32) — for that packing, literally
            // `from ^ to`. Vertex indices on a subdivided surface are small and highly structured,
            // so a huge number of distinct edges collided into the same buckets and every lookup
            // walked a chain that grew with the mesh. That made the whole build quadratic:
            // measured 33ms at 5k triangles, 379ms at 20k, 8,571ms at 82k — 4x the data for 22x
            // the time — and about fifty minutes at 1.3M.
            //
            // Any change here must keep the per-vertex bound. Do not reintroduce an edge hash map.
            var incidentStart = new int[count + 1];
            for (var h = 0; h < halfEdgeCount; h++) incidentStart[halfEdgeVertex[h]]++;

            var running = 0;
            for (var v = 0; v < count; v++)
            {
                var degree = incidentStart[v];
                incidentStart[v] = running;
                running += degree;
            }
            incidentStart[count] = running;

            var incident = new int[running];
            var fill = new int[count];
            for (var h = 0; h < halfEdgeCount; h++)
            {
                var v = halfEdgeVertex[h];
                incident[incidentStart[v] + fill[v]++] = h;
            }

            var incidentMs = adjacencyWatch.ElapsedMilliseconds;
            adjacencyWatch.Restart();

            // --- twins and duplicate directed edges, both by scanning one vertex's own list ---
            for (var h = 0; h < halfEdgeCount; h++)
            {
                var from = halfEdgeVertex[h];
                var to = HalfEdgeTarget(h);

                // A second half-edge with the same origin AND the same target: duplicated geometry,
                // or two triangles wound inconsistently. Either way the fan through both endpoints
                // is not a single cycle. Counted only against the earlier of the pair, so a
                // directed edge appearing k times contributes k-1 — what the map-based version did.
                for (var k = incidentStart[from]; k < incidentStart[from + 1]; k++)
                {
                    var g = incident[k];
                    if (g >= h || HalfEdgeTarget(g) != to) continue;

                    isNonManifoldVertex[from] = true;
                    isNonManifoldVertex[to] = true;
                    nonManifoldEdgeCount++;
                    break;
                }

                // The twin is the lowest-indexed half-edge running the other way. `incident` is
                // filled in ascending h, so the first match found is that one — which is the same
                // choice the map made by keeping its first insertion.
                var twin = -1;
                for (var k = incidentStart[to]; k < incidentStart[to + 1]; k++)
                {
                    var g = incident[k];
                    if (HalfEdgeTarget(g) != from) continue;
                    twin = g;
                    break;
                }

                if (twin >= 0) halfEdgeTwin[h] = twin;
                else boundaryEdgeCount++;
            }

            var twinMs = adjacencyWatch.ElapsedMilliseconds;
            adjacencyWatch.Restart();

            // Each half-edge is a candidate starting point for its origin. A boundary half-edge
            // wins, because walking forward from it traverses the whole fan exactly once; without
            // that the ring walk would start mid-fan and stop early.
            for (var h = 0; h < halfEdgeCount; h++)
            {
                var v = halfEdgeVertex[h];
                if (vertexHalfEdge[v] < 0) vertexHalfEdge[v] = h;

                // Outgoing edge whose PREVIOUS edge has no twin: nothing comes before it in the fan.
                if (halfEdgeTwin[Prev(h)] < 0)
                {
                    vertexHalfEdge[v] = h;
                    isBoundaryVertex[v] = true;
                }
                if (halfEdgeTwin[h] < 0)
                    isBoundaryVertex[v] = true;
            }

            // --- CSR, in fan order ---
            var ring = new List<int>(16);
            var seen = new HashSet<int>();

            adjacencyStart = new int[count + 1];
            var rows = new int[count][];

            for (var v = 0; v < count; v++)
            {
                rows[v] = BuildRing(v, incident, incidentStart, ring, seen);
                if (rows[v].Length == 0) isolatedVertices++;
            }

            var total = 0;
            for (var v = 0; v < count; v++)
            {
                adjacencyStart[v] = total;
                total += rows[v].Length;
            }
            adjacencyStart[count] = total;

            adjacency = new int[total];
            for (var v = 0; v < count; v++)
                Array.Copy(rows[v], 0, adjacency, adjacencyStart[v], rows[v].Length);

            adjacencyTimings = $"incident={incidentMs}ms twins={twinMs}ms " +
                               $"rings={adjacencyWatch.ElapsedMilliseconds}ms";
        }

        /// <summary>
        /// The neighbours of <paramref name="vertex"/> in cyclic order around it.
        /// </summary>
        /// <remarks>
        /// Rotation around a vertex is <c>h -&gt; twin(prev(h))</c>: prev(h) is the edge arriving at
        /// the vertex within the same triangle, and its twin is the next edge leaving it in the
        /// adjacent triangle.
        ///
        /// A boundary vertex has one more neighbour than it has outgoing edges — the far end of its
        /// last spoke is reached by no outgoing edge at all — so that one is added explicitly. A
        /// fan of one triangle makes this concrete: one outgoing edge, two neighbours.
        ///
        /// Non-manifold vertices fall back to an unordered set. Their ring is genuinely not a cycle,
        /// so there is no correct order to produce, and looping forever trying to find one is the
        /// failure this guards against.
        /// </remarks>
        int[] BuildRing(int vertex, int[] incident, int[] incidentStart,
            List<int> ring, HashSet<int> seen)
        {
            var from = incidentStart[vertex];
            var to = incidentStart[vertex + 1];
            if (from == to) return Array.Empty<int>();

            var start = vertexHalfEdge[vertex];
            if (start < 0) return Array.Empty<int>();

            ring.Clear();
            seen.Clear();

            if (isNonManifoldVertex[vertex])
            {
                // Unordered but deterministic. Taking both other corners of each incident triangle
                // covers neighbours reached by an outgoing edge and by an incoming one, at a cost of
                // this vertex's valence — where scanning the whole half-edge array, as this used to,
                // made the whole build quadratic.
                for (var k = from; k < to; k++)
                {
                    var h = incident[k];
                    AddNeighbour(ring, seen, HalfEdgeTarget(h));
                    AddNeighbour(ring, seen, halfEdgeVertex[Prev(h)]);
                }

                var unordered = ring.ToArray();
                Array.Sort(unordered);
                return unordered;
            }

            var current = start;
            var guard = 0;

            // Bounded by this vertex's own fan, so a malformed one costs its valence rather than a
            // traversal of every half-edge in the mesh.
            var limit = to - from + 1;

            while (current >= 0 && guard++ < limit)
            {
                AddNeighbour(ring, seen, HalfEdgeTarget(current));

                var next = halfEdgeTwin[Prev(current)];
                if (next == start) break;      // closed fan, back where we began
                current = next;
            }

            // Boundary: close the fan with the far end of the final spoke.
            if (current >= 0 && halfEdgeTwin[Prev(current)] < 0)
                AddNeighbour(ring, seen, halfEdgeVertex[Prev(current)]);

            return ring.ToArray();
        }

        static void AddNeighbour(List<int> ring, HashSet<int> seen, int neighbour)
        {
            if (seen.Add(neighbour)) ring.Add(neighbour);
        }

        void ComputeNormalsAndAreas()
        {
            var count = positions.Length;
            normals = new Vector3[count];
            areas = new float[count];

            for (var i = 0; i + 2 < triangles.Length; i += 3)
            {
                int ia = triangles[i], ib = triangles[i + 1], ic = triangles[i + 2];
                var a = positions[ia];
                var b = positions[ib];
                var c = positions[ic];

                // Length of the cross product is twice the triangle area, so accumulating it
                // unnormalised weights each vertex normal by area for free.
                var cross = Vector3.Cross(b - a, c - a);
                normals[ia] += cross;
                normals[ib] += cross;
                normals[ic] += cross;

                var third = cross.magnitude / 6f; // (area / 2) / 3
                areas[ia] += third;
                areas[ib] += third;
                areas[ic] += third;
                totalArea += cross.magnitude * 0.5f;
            }

            for (var i = 0; i < count; i++)
            {
                var n = normals[i];
                var length = n.magnitude;
                // An isolated or fully degenerate vertex has no meaningful normal; park it on +Y
                // so downstream tangent construction still produces something finite.
                normals[i] = length > 1e-20f ? n / length : Vector3.up;
            }
        }

        void ComputeAverageEdgeLength()
        {
            double sum = 0;
            var edges = 0;

            for (var i = 0; i < positions.Length; i++)
            {
                for (var k = adjacencyStart[i]; k < adjacencyStart[i + 1]; k++)
                {
                    var j = adjacency[k];
                    if (j <= i)
                        continue; // count each undirected edge once

                    sum += Vector3.Distance(positions[i], positions[j]);
                    edges++;
                }
            }

            averageEdgeLength = edges > 0 ? (float)(sum / edges) : 0f;
        }

        /// <summary>
        /// Target edge length for a desired quad count. A quad covers roughly its edge length
        /// squared, so length = sqrt(area / quads).
        /// </summary>
        public float EdgeLengthForQuadCount(int quadCount)
        {
            if (quadCount <= 0 || totalArea <= 0f)
                return averageEdgeLength;
            return Mathf.Sqrt(totalArea / quadCount);
        }

        /// <summary>
        /// The most quads this input can actually produce. Field-aligned remeshing samples the
        /// lattice *at the input vertices*, so it can only coarsen: a quad needs roughly a 2x2
        /// block of input vertices, which puts a floor on the quad size in terms of the input edge
        /// length. Asking for more than this leaves every vertex on its own lattice point and
        /// hands back something close to the original triangulation — output that looks exactly
        /// like a solver bug and has already been misdiagnosed as one once. Callers should clamp
        /// against this and say so.
        /// </summary>
        /// <remarks>
        /// The floor is <see cref="minUsefulSpacingMultiple"/>, which is also what
        /// <see cref="UsefulTargetBand"/> reports as its finest end. Those two have to agree, and
        /// they did not: this used a hard-coded 2x while the band used 1.5x, so the same object gave
        /// two different answers to one question and the stricter one won wherever the clamp ran.
        ///
        /// The cost was not theoretical. The ceiling is 1.78x higher at 1.5x than at 2x, so on the
        /// reference scan a request for 5,474 faces was clamped to 4,253 and the tool quietly
        /// delivered 25% fewer faces than asked for, with the shortfall attributed to nothing. 1.5x
        /// is the measured floor — see <see cref="maxUsefulSpacingMultiple"/> for how that band was
        /// established — so it is the one to encode here.
        /// </remarks>
        public int MaxQuadCountForInput() => QuadCountForSpacingMultiple(minUsefulSpacingMultiple);

        /// <summary>
        /// The quad count corresponding to a lattice spacing of <paramref name="multiple"/> times
        /// the input's average edge length.
        /// </summary>
        public int QuadCountForSpacingMultiple(float multiple)
        {
            if (totalArea <= 0f || averageEdgeLength <= 0f || multiple <= 0f)
                return 0;

            var spacing = multiple * averageEdgeLength;
            return Mathf.Max(4, Mathf.FloorToInt(totalArea / (spacing * spacing)));
        }

        /// <summary>
        /// Coarsest lattice spacing, as a multiple of the input edge length, at which output quality
        /// holds up. Beyond this the result degrades sharply rather than gracefully.
        /// </summary>
        /// <remarks>
        /// Measured, not guessed. Across the procedural corpus the quad fraction sits at 83-98% while
        /// the spacing is within about 2.5x the input edge length, and falls to 20-49% by 3.3x. The
        /// cause is structural: as the spacing grows, nearly every input edge has both endpoints
        /// inside one lattice cell, so almost no edges are classified as one-step and the output graph
        /// ends up carrying mostly refused-collapse fallback edges, which are not lattice-aligned.
        ///
        /// The real fix for going coarser is to raise the INPUT's edge length rather than the
        /// spacing — decimate first, then remesh — which keeps the ratio inside this band whatever
        /// the final face count. A multiresolution hierarchy would also address it, by organising the
        /// lattice coarse-to-fine instead of asking one level to span many input edges.
        /// </remarks>
        public const float maxUsefulSpacingMultiple = 2.5f;

        /// <summary>Finest spacing worth requesting; below this the lattice cannot coarsen at all.</summary>
        public const float minUsefulSpacingMultiple = 1.5f;

        /// <summary>
        /// The band of target quad counts this input can serve well. Requests outside it still
        /// produce output, but quality degrades — see <see cref="maxUsefulSpacingMultiple"/>.
        /// </summary>
        public void UsefulQuadCountRange(out int coarsest, out int finest)
        {
            coarsest = QuadCountForSpacingMultiple(maxUsefulSpacingMultiple);
            finest = QuadCountForSpacingMultiple(minUsefulSpacingMultiple);
        }

        public override string ToString()
        {
            return $"{vertexCount:N0} vertices ({weldedAway:N0} welded away), " +
                   $"{triangleCount:N0} triangles, avg edge {averageEdgeLength:F4}, " +
                   $"area {totalArea:F3}" +
                   (isolatedVertices > 0 ? $", {isolatedVertices} isolated" : "");
        }
    }
}
