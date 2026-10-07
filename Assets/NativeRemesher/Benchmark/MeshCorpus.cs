// The benchmark's mesh corpus: procedural (committed as code) plus an optional external folder.
//
// Unity's built-in primitives are not adequate for this. Sphere.fbx welds to 382 vertices, so the
// coarsen-only constraint means it cannot be remeshed to 2000 quads *at all* — every vertex stays
// on its own lattice point and the result is the input triangulation. Measuring a remesher on it
// tells you nothing. Every mesh below exists to stress one specific thing, named in its comment.
//
// Determinism matters as much as coverage: these are generated from closed-form formulas with no
// randomness, so `corpusVersion` plus a git SHA fully identifies what a CSV row was measured on.
// Bump corpusVersion whenever a generator changes, or old baselines silently compare apples to
// pears.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using NativeRemesher.Extraction;

namespace NativeRemesher.Benchmark
{
    public enum CorpusTier
    {
        /// <summary>Procedural, committed as code, always available.</summary>
        Procedural,
        /// <summary>External asset folder; skipped with a warning when absent.</summary>
        External
    }

    public sealed class CorpusEntry
    {
        public string name;
        public CorpusTier tier;
        public string what;          // what this mesh is here to stress
        public Func<Mesh> create;

        public override string ToString() => $"{name} ({tier})";
    }

    public static class MeshCorpus
    {
        /// <summary>Bump on any generator change so stale baselines fail loudly.</summary>
        public const int corpusVersion = 1;

        const string k_CorpusEnvVar = "NATIVEREMESHER_CORPUS";

        /// <summary>
        /// Where Tier 2 meshes are looked for. Never committed — only
        /// <c>Benchmarks/corpus.manifest.json</c> is, so the repo stays asset-free and CI stays
        /// green when the folder is missing.
        /// </summary>
        public static string ExternalRoot
        {
            get
            {
                var fromEnv = Environment.GetEnvironmentVariable(k_CorpusEnvVar);
                if (!string.IsNullOrEmpty(fromEnv))
                    return fromEnv;

                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
                return Path.GetFullPath(Path.Combine(projectRoot, "..", "QuadremeshCorpus"));
            }
        }

        public static List<CorpusEntry> Procedural()
        {
            return new List<CorpusEntry>
            {
                new CorpusEntry
                {
                    name = "icosphere_5k", tier = CorpusTier.Procedural,
                    what = "baseline closed smooth surface; the everything-should-work case",
                    create = () => Icosphere(4, 1f)
                },
                new CorpusEntry
                {
                    name = "icosphere_80k", tier = CorpusTier.Procedural,
                    what = "scale-up; where single-level Gauss-Seidel starts to hurt",
                    create = () => Icosphere(6, 1f)
                },
                new CorpusEntry
                {
                    name = "icosphere_320k", tier = CorpusTier.Procedural,
                    what = "the hierarchy's justification; also the perf ceiling test",
                    create = () => Icosphere(7, 1f)
                },
                new CorpusEntry
                {
                    name = "torus", tier = CorpusTier.Procedural,
                    what = "genus 1 — singularity placement has nowhere to hide",
                    create = () => Torus(1f, 0.35f, 128, 48)
                },
                new CorpusEntry
                {
                    name = "cylinder_capped", tier = CorpusTier.Procedural,
                    what = "two sharp circular creases meeting flat caps",
                    create = () => CappedCylinder(0.5f, 2f, 96, 24)
                },
                new CorpusEntry
                {
                    name = "disc_open", tier = CorpusTier.Procedural,
                    what = "BOUNDARY handling — currently untested anywhere in the project",
                    create = () => OpenDisc(1f, 96, 24)
                },
                new CorpusEntry
                {
                    name = "cube_subdivided", tier = CorpusTier.Procedural,
                    what = "90 degree creases; the hardest case for field alignment",
                    create = () => SubdividedCube(1f, 32)
                },
                new CorpusEntry
                {
                    name = "sphere_bumpy", tier = CorpusTier.Procedural,
                    what = "varying curvature — drives adaptive density and curvature-aligned flow",
                    create = () => BumpySphere(6, 1f, 0.12f, 6f)
                },
                new CorpusEntry
                {
                    name = "sphere_holed", tier = CorpusTier.Procedural,
                    what = "boundary plus non-trivial topology at once",
                    create = () => HoledSphere(5, 1f, 0.45f)
                },
                new CorpusEntry
                {
                    name = "robust_nonmanifold", tier = CorpusTier.Procedural,
                    what = "ROBUSTNESS: three triangles on one edge. Must not crash or hang",
                    create = NonManifoldFan
                },
                new CorpusEntry
                {
                    name = "robust_selfintersect", tier = CorpusTier.Procedural,
                    what = "ROBUSTNESS: two interpenetrating spheres. Must not crash or hang",
                    create = () => TwoInterpenetratingSpheres(4, 1f, 1.2f)
                },
                new CorpusEntry
                {
                    name = "robust_degenerate", tier = CorpusTier.Procedural,
                    what = "ROBUSTNESS: zero-area slivers and duplicate vertices",
                    create = DegenerateSoup
                }
            };
        }

        /// <summary>
        /// Tier 2 entries, resolved from <see cref="ExternalRoot"/>. Returns an empty list and logs
        /// a single warning when the folder is absent, so a headless run stays green.
        /// </summary>
        public static List<CorpusEntry> External()
        {
            var entries = new List<CorpusEntry>();
            var root = ExternalRoot;

            if (!Directory.Exists(root))
            {
                Debug.LogWarning($"[Benchmark] External corpus not found at '{root}'. " +
                                 $"Procedural tier only. Set {k_CorpusEnvVar} to point elsewhere.");
                return entries;
            }

            foreach (var path in Directory.GetFiles(root, "*.obj", SearchOption.TopDirectoryOnly))
            {
                var captured = path;
                entries.Add(new CorpusEntry
                {
                    name = Path.GetFileNameWithoutExtension(captured),
                    tier = CorpusTier.External,
                    what = "external corpus mesh",
                    create = () => ObjReader.Read(captured)
                });
            }

            if (entries.Count == 0)
                Debug.LogWarning($"[Benchmark] External corpus folder '{root}' contains no .obj files.");

            return entries;
        }

        // ==================================================================
        // Generators
        // ==================================================================

        /// <summary>
        /// Geodesic sphere by recursive subdivision of an icosahedron. Chosen over a UV sphere
        /// deliberately: a UV sphere has two poles of extreme valence and wildly varying triangle
        /// area, which would confound every metric with artefacts of the *input* rather than the
        /// remesher. An icosphere is near-uniform everywhere.
        /// </summary>
        public static Mesh Icosphere(int subdivisions, float radius)
        {
            var t = (1f + Mathf.Sqrt(5f)) * 0.5f;

            var vertices = new List<Vector3>
            {
                new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
                new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
                new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1)
            };
            // Plain .normalized is correct here and the three uses in this file are deliberately
            // exempt from the MeshMath sweep: these are icosahedron vertex POSITIONS on a unit
            // sphere, magnitude 1 by construction, nowhere near the 1e-5 floor that made cross
            // products of mesh edges collapse to zero. Note that this exemption is also why no
            // corpus mesh could ever have caught that bug — every mesh here is unit-scale.
            for (var i = 0; i < vertices.Count; i++) vertices[i] = vertices[i].normalized;

            var faces = new List<int>
            {
                0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11,
                1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
                3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9,
                4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1
            };

            // Midpoint cache keyed on the ordered vertex pair, so shared edges produce one vertex.
            for (var s = 0; s < subdivisions; s++)
            {
                var midpoints = new Dictionary<long, int>(faces.Count, EdgeKey.Comparer);
                var next = new List<int>(faces.Count * 4);

                int Midpoint(int a, int b)
                {
                    var lo = Mathf.Min(a, b);
                    var hi = Mathf.Max(a, b);
                    var key = ((long)lo << 32) | (uint)hi;
                    if (midpoints.TryGetValue(key, out var index)) return index;

                    index = vertices.Count;
                    vertices.Add(((vertices[a] + vertices[b]) * 0.5f).normalized);
                    midpoints.Add(key, index);
                    return index;
                }

                for (var f = 0; f < faces.Count; f += 3)
                {
                    int a = faces[f], b = faces[f + 1], c = faces[f + 2];
                    int ab = Midpoint(a, b), bc = Midpoint(b, c), ca = Midpoint(c, a);

                    // Added one at a time on purpose. AddRange(new[] { ... }) allocated a
                    // twelve-element array per triangle, which at the top subdivision level is
                    // 1.3M throwaway arrays and 32 seconds of the test suite's runtime.
                    next.Add(a); next.Add(ab); next.Add(ca);
                    next.Add(b); next.Add(bc); next.Add(ab);
                    next.Add(c); next.Add(ca); next.Add(bc);
                    next.Add(ab); next.Add(bc); next.Add(ca);
                }

                faces = next;
            }

            for (var i = 0; i < vertices.Count; i++) vertices[i] *= radius;
            return Build("icosphere", vertices, faces);
        }

        public static Mesh BumpySphere(int subdivisions, float radius, float amplitude, float frequency)
        {
            var mesh = Icosphere(subdivisions, radius);
            var vertices = mesh.vertices;

            for (var i = 0; i < vertices.Length; i++)
            {
                var d = vertices[i].normalized;
                // Product of three sinusoids: smooth, non-axis-aligned, and with genuinely varying
                // principal curvature, which is what an adaptive sizing field has to respond to.
                var bump = Mathf.Sin(d.x * frequency) * Mathf.Sin(d.y * frequency) * Mathf.Sin(d.z * frequency);
                vertices[i] = d * (radius + amplitude * bump);
            }

            mesh.vertices = vertices;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.name = "sphere_bumpy";
            return mesh;
        }

        /// <summary>Icosphere with every triangle above a height threshold removed, leaving one hole.</summary>
        public static Mesh HoledSphere(int subdivisions, float radius, float cutHeight)
        {
            var source = Icosphere(subdivisions, radius);
            var vertices = new List<Vector3>(source.vertices);
            var sourceTris = source.triangles;
            var faces = new List<int>(sourceTris.Length);

            for (var i = 0; i + 2 < sourceTris.Length; i += 3)
            {
                var a = vertices[sourceTris[i]];
                var b = vertices[sourceTris[i + 1]];
                var c = vertices[sourceTris[i + 2]];

                // Drop the triangle only if all three corners are above the cut, so the boundary
                // follows existing edges and stays a clean loop.
                if (a.y > cutHeight * radius && b.y > cutHeight * radius && c.y > cutHeight * radius)
                    continue;

                faces.Add(sourceTris[i]);
                faces.Add(sourceTris[i + 1]);
                faces.Add(sourceTris[i + 2]);
            }

            return Build("sphere_holed", vertices, faces);
        }

        public static Mesh Torus(float majorRadius, float minorRadius, int majorSegments, int minorSegments)
        {
            var vertices = new List<Vector3>(majorSegments * minorSegments);
            var faces = new List<int>(majorSegments * minorSegments * 6);

            for (var i = 0; i < majorSegments; i++)
            {
                var u = i / (float)majorSegments * Mathf.PI * 2f;
                var cu = Mathf.Cos(u);
                var su = Mathf.Sin(u);

                for (var j = 0; j < minorSegments; j++)
                {
                    var v = j / (float)minorSegments * Mathf.PI * 2f;
                    var cv = Mathf.Cos(v);
                    var sv = Mathf.Sin(v);

                    vertices.Add(new Vector3(
                        (majorRadius + minorRadius * cv) * cu,
                        minorRadius * sv,
                        (majorRadius + minorRadius * cv) * su));
                }
            }

            for (var i = 0; i < majorSegments; i++)
            for (var j = 0; j < minorSegments; j++)
            {
                var i1 = (i + 1) % majorSegments;
                var j1 = (j + 1) % minorSegments;

                var a = i * minorSegments + j;
                var b = i1 * minorSegments + j;
                var c = i1 * minorSegments + j1;
                var d = i * minorSegments + j1;

                faces.AddRange(new[] { a, b, c, a, c, d });
            }

            return Build("torus", vertices, faces);
        }

        public static Mesh CappedCylinder(float radius, float height, int radialSegments, int heightSegments)
        {
            var vertices = new List<Vector3>();
            var faces = new List<int>();
            var half = height * 0.5f;

            // Side wall.
            for (var y = 0; y <= heightSegments; y++)
            {
                var v = y / (float)heightSegments;
                for (var i = 0; i < radialSegments; i++)
                {
                    var a = i / (float)radialSegments * Mathf.PI * 2f;
                    vertices.Add(new Vector3(Mathf.Cos(a) * radius, -half + v * height, Mathf.Sin(a) * radius));
                }
            }

            for (var y = 0; y < heightSegments; y++)
            for (var i = 0; i < radialSegments; i++)
            {
                var i1 = (i + 1) % radialSegments;
                var a = y * radialSegments + i;
                var b = y * radialSegments + i1;
                var c = (y + 1) * radialSegments + i1;
                var d = (y + 1) * radialSegments + i;
                faces.AddRange(new[] { a, b, c, a, c, d });
            }

            // Caps, as concentric rings rather than a single fan — a fan would put a
            // pathologically high-valence vertex at each pole, an input artefact the remesher
            // would then get blamed for.
            AddCap(vertices, faces, radius, -half, radialSegments, heightSegments / 2, false);
            AddCap(vertices, faces, radius, half, radialSegments, heightSegments / 2, true);

            return Build("cylinder_capped", vertices, faces);
        }

        static void AddCap(List<Vector3> vertices, List<int> faces, float radius, float y,
            int radialSegments, int rings, bool upward)
        {
            rings = Mathf.Max(1, rings);
            var ringStart = new int[rings + 1];

            for (var r = 0; r <= rings; r++)
            {
                ringStart[r] = vertices.Count;
                var rr = radius * (1f - r / (float)rings);

                if (r == rings)
                {
                    vertices.Add(new Vector3(0f, y, 0f)); // centre
                    break;
                }

                for (var i = 0; i < radialSegments; i++)
                {
                    var a = i / (float)radialSegments * Mathf.PI * 2f;
                    vertices.Add(new Vector3(Mathf.Cos(a) * rr, y, Mathf.Sin(a) * rr));
                }
            }

            for (var r = 0; r < rings; r++)
            {
                var inner = r + 1 == rings;
                for (var i = 0; i < radialSegments; i++)
                {
                    var i1 = (i + 1) % radialSegments;
                    var a = ringStart[r] + i;
                    var b = ringStart[r] + i1;

                    if (inner)
                    {
                        var centre = ringStart[rings];
                        faces.AddRange(upward ? new[] { a, b, centre } : new[] { b, a, centre });
                    }
                    else
                    {
                        var c = ringStart[r + 1] + i1;
                        var d = ringStart[r + 1] + i;
                        if (upward) faces.AddRange(new[] { a, b, c, a, c, d });
                        else faces.AddRange(new[] { b, a, d, c, b, d });
                    }
                }
            }
        }

        /// <summary>
        /// A flat n x n grid in the XZ plane. Not in the corpus list — it is too easy to be an
        /// interesting benchmark row — but it is the single most useful test mesh there is, because
        /// a 4-RoSy field on a flat surface *must* comb out to globally parallel. Any error in the
        /// symmetry handling leaves the field frustrated below that no matter how long it runs, so
        /// this is the one case with an exactly known correct answer.
        /// </summary>
        public static Mesh GridPlane(float size, int n)
        {
            n = Mathf.Max(1, n);
            var vertices = new List<Vector3>((n + 1) * (n + 1));
            var faces = new List<int>(n * n * 6);
            var step = size / n;
            var half = size * 0.5f;

            for (var z = 0; z <= n; z++)
            for (var x = 0; x <= n; x++)
                vertices.Add(new Vector3(-half + x * step, 0f, -half + z * step));

            for (var z = 0; z < n; z++)
            for (var x = 0; x < n; x++)
            {
                var a = z * (n + 1) + x;
                var b = a + 1;
                var c = a + (n + 1) + 1;
                var d = a + (n + 1);
                faces.AddRange(new[] { a, b, c, a, c, d });
            }

            return Build("plane_grid", vertices, faces);
        }

        /// <summary>A flat disc with an open boundary loop and no closure. Tests boundary handling.</summary>
        public static Mesh OpenDisc(float radius, int radialSegments, int rings)
        {
            var vertices = new List<Vector3> { Vector3.zero };
            var faces = new List<int>();

            for (var r = 1; r <= rings; r++)
            {
                var rr = radius * r / rings;
                for (var i = 0; i < radialSegments; i++)
                {
                    var a = i / (float)radialSegments * Mathf.PI * 2f;
                    vertices.Add(new Vector3(Mathf.Cos(a) * rr, 0f, Mathf.Sin(a) * rr));
                }
            }

            // Innermost fan.
            for (var i = 0; i < radialSegments; i++)
            {
                var i1 = (i + 1) % radialSegments;
                faces.AddRange(new[] { 0, 1 + i, 1 + i1 });
            }

            for (var r = 1; r < rings; r++)
            {
                var inner = 1 + (r - 1) * radialSegments;
                var outer = 1 + r * radialSegments;
                for (var i = 0; i < radialSegments; i++)
                {
                    var i1 = (i + 1) % radialSegments;
                    faces.AddRange(new[] { inner + i, outer + i, outer + i1, inner + i, outer + i1, inner + i1 });
                }
            }

            return Build("disc_open", vertices, faces);
        }

        /// <summary>Cube with each face an n x n grid. Shared corner/edge vertices, so it welds to a closed surface.</summary>
        public static Mesh SubdividedCube(float size, int n)
        {
            n = Mathf.Max(1, n);
            var half = size * 0.5f;
            var vertices = new List<Vector3>();
            var faces = new List<int>();

            // Deduplicate on a quantised grid so face borders share vertices exactly.
            var lookup = new Dictionary<Vector3Int, int>();
            var step = size / n;

            int Vertex(Vector3 p)
            {
                var key = new Vector3Int(
                    Mathf.RoundToInt(p.x / step * 2f),
                    Mathf.RoundToInt(p.y / step * 2f),
                    Mathf.RoundToInt(p.z / step * 2f));
                if (lookup.TryGetValue(key, out var index)) return index;
                index = vertices.Count;
                vertices.Add(p);
                lookup.Add(key, index);
                return index;
            }

            // Six faces: (axis, sign) with a local 2-D basis.
            var axes = new[]
            {
                (new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1)),
                (new Vector3(0, 1, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 0)),
                (new Vector3(0, 0, 1), new Vector3(1, 0, 0), new Vector3(0, 1, 0))
            };

            foreach (var (normal, u, v) in axes)
            foreach (var sign in new[] { 1f, -1f })
            {
                var origin = normal * (half * sign);
                for (var i = 0; i < n; i++)
                for (var j = 0; j < n; j++)
                {
                    var u0 = -half + i * step;
                    var u1 = u0 + step;
                    var v0 = -half + j * step;
                    var v1 = v0 + step;

                    var a = Vertex(origin + u * u0 + v * v0);
                    var b = Vertex(origin + u * u1 + v * v0);
                    var c = Vertex(origin + u * u1 + v * v1);
                    var d = Vertex(origin + u * u0 + v * v1);

                    if (sign > 0f) faces.AddRange(new[] { a, b, c, a, c, d });
                    else faces.AddRange(new[] { a, c, b, a, d, c });
                }
            }

            return Build("cube_subdivided", vertices, faces);
        }

        static Mesh TwoInterpenetratingSpheres(int subdivisions, float radius, float offset)
        {
            var left = Icosphere(subdivisions, radius);
            var right = Icosphere(subdivisions, radius);

            var vertices = new List<Vector3>();
            var faces = new List<int>();

            foreach (var p in left.vertices) vertices.Add(p - new Vector3(offset * 0.5f, 0, 0));
            faces.AddRange(left.triangles);

            var shift = vertices.Count;
            foreach (var p in right.vertices) vertices.Add(p + new Vector3(offset * 0.5f, 0, 0));
            foreach (var t in right.triangles) faces.Add(t + shift);

            return Build("robust_selfintersect", vertices, faces);
        }

        /// <summary>Three triangles sharing a single edge — the canonical non-manifold configuration.</summary>
        static Mesh NonManifoldFan()
        {
            var vertices = new List<Vector3>
            {
                new Vector3(0, -1, 0), new Vector3(0, 1, 0),        // the shared edge
                new Vector3(1, 0, 0), new Vector3(-0.5f, 0, 0.87f), new Vector3(-0.5f, 0, -0.87f)
            };

            var faces = new List<int> { 0, 1, 2, 0, 1, 3, 0, 1, 4 };
            return Build("robust_nonmanifold", vertices, faces);
        }

        /// <summary>Zero-area slivers, exact duplicate positions, and a repeated-index triangle.</summary>
        static Mesh DegenerateSoup()
        {
            var vertices = new List<Vector3>
            {
                new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0),
                new Vector3(0, 0, 0),                       // exact duplicate of 0
                new Vector3(1, 0, 0), new Vector3(2, 0, 0), // collinear with 0-1 -> zero area
                new Vector3(0.5f, 0.5f, 0)
            };

            var faces = new List<int>
            {
                0, 1, 2,      // valid
                0, 1, 4,      // zero area (4 duplicates 1)
                1, 4, 5,      // collinear, zero area
                3, 1, 2,      // duplicate of the valid one after welding
                6, 6, 6       // repeated index
            };

            return Build("robust_degenerate", vertices, faces);
        }

        static Mesh Build(string name, List<Vector3> vertices, List<int> faces)
        {
            var mesh = new Mesh { name = name };
            if (vertices.Count > 65534)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            mesh.SetVertices(vertices);
            mesh.SetTriangles(faces, 0, false);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
