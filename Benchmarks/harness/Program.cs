using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NativeRemesher;
using UnityEngine;

// Offline harness: the window's exact Solve call on an OBJ, with per-stage wall time.
// args: obj target [profile=GameAsset] [auto=1] [extractor=IntegerGrid] [out.obj] [key=value...]
static class Program
{
    static int Main(string[] args)
    {
        var ci = CultureInfo.InvariantCulture;
        var path = args[0];
        var target = int.Parse(args[1]);
        var profileKind = args.Length > 2 ? Enum.Parse<RemeshProfileKind>(args[2]) : RemeshProfileKind.GameAsset;
        var auto = args.Length > 3 ? args[3] == "1" : true;
        var extractor = args.Length > 4 ? Enum.Parse<RemeshExtractor>(args[4]) : RemeshExtractor.IntegerGrid;
        var outPath = args.Length > 5 ? args[5] : null;
        var fc = -1; var noUv = false; var uvTest = false; var dump = false; string creaseSpec = null;
        for (var i = 6; i < args.Length; i++)
            if (args[i].StartsWith("fc=")) fc = int.Parse(args[i].Substring(3));
            else if (args[i] == "nouv") noUv = true;
            else if (args[i] == "uvtest") uvTest = true;
            else if (args[i] == "dump") dump = true;
            else if (args[i].StartsWith("creases=")) creaseSpec = args[i].Substring(8);
            else Flags.Apply(args[i]);

        var sw = Stopwatch.StartNew();
        LoadObj(path, out var positions, out var triangles, out var uvs);
        Console.WriteLine($"loaded {positions.Length:N0} verts {triangles.Length / 3:N0} tris in {sw.ElapsedMilliseconds} ms");

        var settings = RemeshSettings.Default;
        settings.targetQuadCount = target;
        if (fc > 0) settings.faceCountIterations = fc;
        var profile = RemeshProfile.For(profileKind);
        profile.extractor = extractor;

        string stage = null; var stageWatch = Stopwatch.StartNew(); var total = Stopwatch.StartNew();
        var stageTimes = new List<(string, long)>();
        RemeshResult result;
        try
        {
            result = RemeshPipeline.Solve(positions, triangles, settings, profile, (s, p) =>
            {
                if (s != stage)
                {
                    if (stage != null) stageTimes.Add((stage, stageWatch.ElapsedMilliseconds));
                    if (stage != null) Console.WriteLine($"    [{total.ElapsedMilliseconds / 1000.0,7:F1}s] {stage} took {stageWatch.ElapsedMilliseconds} ms");
                    stage = s; stageWatch.Restart();
                }
            }, noUv || uvTest ? null : uvs, auto);
        }
        catch (Exception e)
        {
            Console.WriteLine($"THREW in stage '{stage}': {e}");
            return 1;
        }
        if (stage != null) stageTimes.Add((stage, stageWatch.ElapsedMilliseconds));
        Console.WriteLine($"total {total.ElapsedMilliseconds} ms");
        foreach (var (s, ms) in stageTimes) Console.WriteLine($"  {ms,8} ms  {s}");

        var ex = result.extraction;
        if (creaseSpec != null)
        {
            var parts = creaseSpec.Split(',');
            var angle = float.Parse(parts[0], CultureInfo.InvariantCulture);
            var cell = result.topology.EdgeLengthForQuadCount(result.effectiveTargetQuadCount);
            var ext = float.Parse(parts[1], CultureInfo.InvariantCulture) * cell;
            var straight = float.Parse(parts[2], CultureInfo.InvariantCulture);
            var raw = FeatureGraph.Detect(result.topology, angle);
            var kept = raw.KeepCoherentCurves(result.topology, ext, straight, out var kc, out var dc);
            Console.WriteLine($"CREASES angle {angle} raw edges {raw.featureEdgeCount} polylines {raw.polylineCount} -> kept {kc} dropped {dc}, edges {kept.featureEdgeCount}");
            var sb = new StringBuilder();
            var tp = result.topology.positions;
            foreach (var x in tp) sb.Append("v ").Append((-x.x).ToString("R", CultureInfo.InvariantCulture)).Append(' ').Append(x.y.ToString("R", CultureInfo.InvariantCulture)).Append(' ').Append(x.z.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            for (var c = 0; c < kept.polylineCount; c++)
            {
                sb.Append('l');
                for (var i = kept.polylineStart[c]; i < kept.polylineStart[c + 1]; i++) sb.Append(' ').Append(kept.polylineVertices[i] + 1);
                sb.Append('\n');
            }
            File.WriteAllText(outPath + ".creases.obj", sb.ToString());
        }
        if (dump && outPath != null)
        {
            var sb = new StringBuilder();
            var tp = result.topology.positions; var tt = result.topology.triangles;
            foreach (var x in tp) sb.Append("v ").Append((-x.x).ToString("R", CultureInfo.InvariantCulture)).Append(' ').Append(x.y.ToString("R", CultureInfo.InvariantCulture)).Append(' ').Append(x.z.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            for (var t = 0; t < tt.Length; t += 3) sb.Append("f ").Append(tt[t] + 1).Append(' ').Append(tt[t + 2] + 1).Append(' ').Append(tt[t + 1] + 1).Append('\n');
            File.WriteAllText(outPath + ".topo.obj", sb.ToString());
            Console.WriteLine($"dumped topology {tp.Length} verts {tt.Length / 3} tris");
        }
        if (result.scaleField != null)
        {
            var tp = result.topology.positions; var sp = result.scaleField.spacing;
            double hs = 0, all = 0; int hn = 0;
            float hmin = float.MaxValue;
            for (var i = 0; i < tp.Length; i++) { all += sp[i]; if (Mathf.Abs(tp[i].x) > 0.43f) { hs += sp[i]; hn++; hmin = Mathf.Min(hmin, sp[i]); } }
            Console.WriteLine($"SPACING mean {all / tp.Length:G4} min {result.scaleField.minSpacing:G4} max {result.scaleField.maxSpacing:G4} | hands(|x|>0.43) n={hn} mean {(hn > 0 ? hs / hn : 0):G4} min {hmin:G4}");
        }
        Console.WriteLine($"THIN kept={result.coherentCreasesKept} dropped={result.coherentCreasesDropped} thinned={result.coherentCreasesThinned}"); Console.WriteLine($"CHANGED {NativeRemesher.Extraction.IntegerGridMap.debugChangedEdgesReport}");
        {
            var st = result.topology.adjacencyStart; var adj = result.topology.adjacency; var asym = 0;
            var set = new HashSet<long>();
            for (var v = 0; v < result.topology.vertexCount; v++) for (var k = st[v]; k < st[v + 1]; k++) set.Add(((long)v << 32) | (uint)adj[k]);
            foreach (var key in set) if (!set.Contains(((key & 0xffffffffL) << 32) | (uint)(key >> 32))) asym++;
            var col = result.topology.colouring; var clash = 0;
            if (col != null) { var c = new int[result.topology.vertexCount]; for (var k = 0; k + 1 < col.start.Length; k++) for (var i = col.start[k]; i < col.start[k + 1]; i++) c[col.vertices[i]] = k;
              for (var v = 0; v < result.topology.vertexCount; v++) for (var k = st[v]; k < st[v + 1]; k++) if (adj[k] != v && c[adj[k]] == c[v]) clash++; }
            Console.WriteLine($"ADJ asymmetric={asym} colourClashes={clash}");
        }
        Console.WriteLine($"GFIELD {result.globalFieldStats} cancel {result.singularBeforeCancel}->{result.singularAfterCancel} pairs={result.singularPairsTried}");
        Console.WriteLine($"CSNAP {result.extraction?.creaseLoopStats}");
        Console.WriteLine($"profile {result.profile.kind} auto={result.profileWasAutoSelected} probe={result.preRefineProbeFeatureFraction:F3}");
        Console.WriteLine($"target {result.requestedQuadCount}->{result.effectiveTargetQuadCount} x{result.adaptiveQuadCountFactor:F2} attempts={result.faceCountAttempts} trace={result.faceCountTrace}");
        Console.WriteLine($"decimate {result.decimateStats}");
        Console.WriteLine($"refine {result.refineStats}");
        Console.WriteLine($"localRefine {result.localRefineStats}");
        if (ex != null)
        {
            Console.WriteLine(ex.ToString());
            Console.WriteLine(ex.Diagnostics());
            if (outPath != null) WriteObj(outPath, ex.positions, ex.faces);
            if (uvTest)
            {
                var bad = 0; foreach (var q in ex.positions) if (float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsNaN(q.z) || float.IsInfinity(q.x)) bad++;
                Console.WriteLine($"non-finite output positions: {bad}");
                var uw = Stopwatch.StartNew();
                AttributeTransfer.SampleUvs(positions, triangles, uvs, ex.positions);
                Console.WriteLine($"SampleUvs {uw.ElapsedMilliseconds} ms"); uw.Restart();
                AttributeTransfer.SampleFaceUvs(positions, triangles, uvs, ex.positions, ex.faces, out var fus);
                Console.WriteLine($"SampleFaceUvs {uw.ElapsedMilliseconds} ms charts={fus.charts} seam={fus.seamFaces} reproj={fus.reprojectedCorners}");
            }
            Audit.Valences(ex.faces, out var interior, out var irregular, out var v3,
                out var v5, out var higher, out var boundaryEdges);
            var reference = new NativeRemesher.Benchmark.PolyMesh { positions = positions };
            for (var t = 0; t < triangles.Length; t += 3) reference.faces.Add(new[] { triangles[t], triangles[t + 1], triangles[t + 2] });
            var m = NativeRemesher.Benchmark.MeshMetrics.Measure(NativeRemesher.Benchmark.PolyMesh.FromExtraction(ex), reference, 20000);
            Qual.Report(ex.positions, ex.faces, positions, triangles, result.scale);
            Console.WriteLine($"METRIC faces={ex.faceCount} quad={ex.quadFraction:F4} irregular={irregular}/{interior}={100.0 * irregular / Math.Max(1, interior):F2}% v3={v3} v5={v5} hi={higher} open={boundaryEdges} hausdorff={m.hausdorffMax:F5} chamfer={m.chamferMean:F5} totalMs={total.ElapsedMilliseconds}");
        }
        return 0;
    }

    static void LoadObj(string path, out Vector3[] positions, out int[] triangles, out Vector2[] uvs)
    {
        // Emulates Unity's importer: one vertex per distinct (v, vt) pair, fan triangulation.
        var v = new List<Vector3>(); var vt = new List<Vector2>();
        var map = new Dictionary<long, int>();
        var outP = new List<Vector3>(); var outUv = new List<Vector2>(); var tris = new List<int>();
        var corner = new List<int>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length < 2) continue;
            if (line[0] == 'v' && line[1] == ' ')
            {
                var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                v.Add(new Vector3(-float.Parse(t[1], CultureInfo.InvariantCulture), float.Parse(t[2], CultureInfo.InvariantCulture), float.Parse(t[3], CultureInfo.InvariantCulture)));
            }
            else if (line[0] == 'v' && line[1] == 't')
            {
                var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                vt.Add(new Vector2(float.Parse(t[1], CultureInfo.InvariantCulture), float.Parse(t[2], CultureInfo.InvariantCulture)));
            }
            else if (line[0] == 'f' && line[1] == ' ')
            {
                var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                corner.Clear();
                for (var i = 1; i < t.Length; i++)
                {
                    var parts = t[i].Split('/');
                    var vi = int.Parse(parts[0]); if (vi < 0) vi += v.Count + 1; vi--;
                    var ti = parts.Length > 1 && parts[1].Length > 0 ? int.Parse(parts[1]) : 0;
                    if (ti < 0) ti += vt.Count + 1; ti--;
                    var key = ((long)vi << 32) | (uint)(ti + 1);
                    if (!map.TryGetValue(key, out var idx))
                    {
                        idx = outP.Count; map[key] = idx; outP.Add(v[vi]);
                        outUv.Add(ti >= 0 ? vt[ti] : Vector2.zero);
                    }
                    corner.Add(idx);
                }
                // reversed winding, as the x flip mirrors the mesh
                for (var i = 1; i + 1 < corner.Count; i++) { tris.Add(corner[0]); tris.Add(corner[i + 1]); tris.Add(corner[i]); }
            }
        }
        positions = outP.ToArray(); triangles = tris.ToArray(); uvs = vt.Count > 0 ? outUv.ToArray() : null;
    }

    static void WriteObj(string path, Vector3[] p, List<int[]> faces)
    {
        var sb = new StringBuilder();
        foreach (var x in p) sb.Append("v ").Append((-x.x).ToString("R", CultureInfo.InvariantCulture)).Append(' ').Append(x.y.ToString("R", CultureInfo.InvariantCulture)).Append(' ').Append(x.z.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        foreach (var f in faces) { sb.Append('f'); for (var i = f.Length - 1; i >= 0; i--) sb.Append(' ').Append(f[i] + 1); sb.Append('\n'); }
        File.WriteAllText(path, sb.ToString());
    }
}

static class Flags
{
    // key=value on public static fields, e.g. IntegerGridMap.curveNetwork=true
    public static void Apply(string kv)
    {
        var eq = kv.IndexOf('='); var name = kv.Substring(0, eq); var value = kv.Substring(eq + 1);
        var dot = name.LastIndexOf('.'); var typeName = name.Substring(0, dot); var field = name.Substring(dot + 1);
        var type = typeof(RemeshPipeline).Assembly.GetTypes().First(t => t.Name == typeName);
        var f = type.GetField(field);
        f.SetValue(null, Convert.ChangeType(value, f.FieldType, CultureInfo.InvariantCulture));
        Console.WriteLine($"set {name}={value}");
    }
}

static class Audit
{
        public static void Valences(List<int[]> faces, out int interior, out int irregular,
            out int v3, out int v5, out int higher, out int boundaryEdges)
        {
            var valence = new Dictionary<int, int>();
            var uses = new Dictionary<long, int>(NativeRemesher.Extraction.EdgeKey.Comparer);
            foreach (var face in faces)
            {
                for (var k = 0; k < face.Length; k++)
                {
                    var a = face[k];
                    var b = face[(k + 1) % face.Length];
                    valence.TryGetValue(a, out var used);
                    valence[a] = used + 1;
                    var key = NativeRemesher.Extraction.EdgeKey.PackUndirected(a, b);
                    uses.TryGetValue(key, out var count);
                    uses[key] = count + 1;
                }
            }

            var onBoundary = new HashSet<int>();
            boundaryEdges = 0;
            foreach (var entry in uses)
            {
                if (entry.Value == 2) continue;
                boundaryEdges++;
                onBoundary.Add(NativeRemesher.Extraction.EdgeKey.First(entry.Key));
                onBoundary.Add(NativeRemesher.Extraction.EdgeKey.Second(entry.Key));
            }

            interior = irregular = v3 = v5 = higher = 0;
            foreach (var entry in valence)
            {
                if (onBoundary.Contains(entry.Key)) continue;
                interior++;
                if (entry.Value == 4) continue;
                irregular++;
                if (entry.Value == 3) v3++;
                else if (entry.Value == 5) v5++;
                else higher++;
            }
        }
}

static class Qual
{
    // Waviness: at every interior valence-4 vertex the two opposite edges should continue each
    // other. Their angle, after projecting onto the vertex tangent plane, is the geodesic turn of
    // the loop through that vertex: 0 for a straight loop. Normal curvature of the surface is
    // removed by the projection, so a ring round a cylinder reads 0.
    // Surface: distance of output vertices and quad centres to the INPUT surface, in cells.
    public static void Report(Vector3[] p, List<int[]> faces, Vector3[] inP, int[] inT, float cell)
    {
        var n = p.Length;
        var normal = new Vector3[n];
        var nbr = new List<int>[n];
        var faceCount = new int[n];
        var allQuad = new bool[n];
        for (var i = 0; i < n; i++) allQuad[i] = true;
        var adjacentPairs = new HashSet<long>();
        foreach (var f in faces)
        {
            var fn = Vector3.zero;
            for (var k = 0; k < f.Length; k++) fn += Vector3.Cross(p[f[k]], p[f[(k + 1) % f.Length]]);
            for (var k = 0; k < f.Length; k++)
            {
                var v = f[k];
                normal[v] += fn;
                faceCount[v]++;
                if (f.Length != 4) allQuad[v] = false;
                if (nbr[v] == null) nbr[v] = new List<int>(4);
                var a = f[(k + 1) % f.Length]; var b = f[(k + f.Length - 1) % f.Length];
                if (!nbr[v].Contains(a)) nbr[v].Add(a);
                if (!nbr[v].Contains(b)) nbr[v].Add(b);
                adjacentPairs.Add(Key(v, a, b));
            }
        }
        var turns = new List<float>();
        var orth = new List<float>();
        for (var v = 0; v < n; v++)
        {
            if (nbr[v] == null || nbr[v].Count != 4 || faceCount[v] != 4 || !allQuad[v]) continue;
            var nn = normal[v].normalized;
            Vector3 T(int w) { var e = p[w] - p[v]; return (e - Vector3.Dot(e, nn) * nn).normalized; }
            var ns = nbr[v];
            for (var i = 0; i < 4; i++)
                for (var j = i + 1; j < 4; j++)
                {
                    var adjacent = adjacentPairs.Contains(Key(v, ns[i], ns[j]));
                    var ang = Vector3.Angle(T(ns[i]), T(ns[j]));
                    if (adjacent) orth.Add(Mathf.Abs(ang - 90f));
                    else turns.Add(180f - ang);
                }
        }
        turns.Sort(); orth.Sort();
        float Mean(List<float> x) { double t = 0; foreach (var a in x) t += a; return x.Count > 0 ? (float)(t / x.Count) : 0; }
        float P(List<float> x, float q) => x.Count > 0 ? x[Math.Min(x.Count - 1, (int)(q * x.Count))] : 0;

        var grid = new SpatialGrid(inP, inT);
        var vd = new List<float>(n);
        for (var v = 0; v < n; v++) vd.Add(grid.NearestDistance(p[v], out _) / cell);
        var cd = new List<float>(faces.Count);
        foreach (var f in faces)
        {
            var c = Vector3.zero; foreach (var v in f) c += p[v]; c /= f.Length;
            cd.Add(grid.NearestDistance(c, out _) / cell);
        }
        vd.Sort(); cd.Sort();
        var outTris = new List<int>();
        foreach (var f in faces) for (var k = 1; k + 1 < f.Length; k++) { outTris.Add(f[0]); outTris.Add(f[k]); outTris.Add(f[k + 1]); }
        var back = new SpatialGrid(p, outTris.ToArray());
        var id = new List<float>();
        var far = new List<Vector3>();
        for (var v = 0; v < inP.Length; v += 7) { var d = back.NearestDistance(inP[v], out _) / cell; id.Add(d); if (d > 0.5f) far.Add(inP[v]); }
        id.Sort();
        Console.WriteLine($"QUAL inputToOutput mean={Mean(id):F3} p90={P(id, .9f):F3} p99={P(id, .99f):F3} max={P(id, 1f):F3} cells, >0.5 cell: {far.Count}/{id.Count}");
        var fsb = new StringBuilder();
        foreach (var x in far) fsb.Append("v ").Append((-x.x).ToString("R", CultureInfo.InvariantCulture)).Append(' ').Append(x.y.ToString("R", CultureInfo.InvariantCulture)).Append(' ').Append(x.z.ToString("R", CultureInfo.InvariantCulture)).Append((char)10);
        File.WriteAllText("far_points.obj", fsb.ToString());
        Console.WriteLine($"QUAL turn mean={Mean(turns):F2} p50={P(turns, .5f):F2} p90={P(turns, .9f):F2} deg | orth mean={Mean(orth):F2} p90={P(orth, .9f):F2} | vertDist mean={Mean(vd):F3} p95={P(vd, .95f):F3} max={P(vd, 1f):F3} | centreDist mean={Mean(cd):F3} p95={P(cd, .95f):F3} max={P(cd, 1f):F3} cells (cell={cell:G4})");
    }

    static long Key(int v, int a, int b) => ((long)v << 40) ^ ((long)Math.Min(a, b) << 20) ^ Math.Max(a, b);
}
