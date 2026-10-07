// Serialising benchmark rows, and comparing a run against a committed baseline.
//
// (Covers what the plan listed as CsvWriter.cs and MarkdownTable.cs — they share the column schema,
// and splitting them would mean maintaining that schema in two places.)
//
// CSV is the canonical form: one row per (mesh, target, profile, variant), fixed column order,
// diffs cleanly in git, loads in a spreadsheet. Markdown is rendered from the identical rows purely
// for pasting into a commit message or a PR.
//
// The column schema is declared once, in order, together with each metric's direction of
// improvement. That last part is what turns the harness from a report into a gate: a run can be
// compared against a baseline automatically, and the process can exit non-zero on a regression,
// without anybody having to remember whether lower Chamfer is better.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace NativeRemesher.Benchmark
{
    public enum Better
    {
        /// <summary>Not a quality metric — identity, size, or cost. Never gates.</summary>
        Neutral,
        Higher,
        Lower,
        /// <summary>Must not change at all; any movement is a regression.</summary>
        Exact
    }

    public sealed class Column
    {
        public string name;
        public Better better;
        public Func<MeshMetrics, string> read;

        public Column(string name, Better better, Func<MeshMetrics, string> read)
        {
            this.name = name;
            this.better = better;
            this.read = read;
        }
    }

    public sealed class RunContext
    {
        public string gitSha = "unknown";
        public string unityVersion = Application.unityVersion;
        public int corpusVersion = MeshCorpus.corpusVersion;

        public static RunContext Current()
        {
            return new RunContext { gitSha = ResolveGitSha() };
        }

        static string ResolveGitSha()
        {
            // Read .git directly rather than shelling out: a git subprocess in batch mode is slow,
            // can prompt, and may not be on PATH on a build agent.
            try
            {
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
                if (projectRoot == null) return "unknown";

                var gitDir = Path.Combine(projectRoot, ".git");
                if (!Directory.Exists(gitDir)) return "no-repo";

                var head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
                if (!head.StartsWith("ref:")) return head.Substring(0, Math.Min(12, head.Length));

                var refPath = Path.Combine(gitDir, head.Substring(4).Trim().Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(refPath))
                    return File.ReadAllText(refPath).Trim().Substring(0, 12);

                // Packed refs: the branch has no loose ref file.
                var packed = Path.Combine(gitDir, "packed-refs");
                if (File.Exists(packed))
                {
                    var wanted = head.Substring(4).Trim();
                    foreach (var line in File.ReadAllLines(packed))
                    {
                        if (line.StartsWith("#")) continue;
                        var parts = line.Split(' ');
                        if (parts.Length == 2 && parts[1] == wanted)
                            return parts[0].Substring(0, 12);
                    }
                }

                return "unknown";
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Benchmark] Could not resolve git SHA: {e.Message}");
                return "unknown";
            }
        }
    }

    public static class BenchmarkReport
    {
        /// <summary>
        /// Bump whenever the column list below changes, so a stale baseline fails loudly instead of
        /// silently comparing mismatched columns.
        /// </summary>
        public const int schemaVersion = 3;   // 2: components. 3: spacingSpread

        static readonly CultureInfo k_Culture = CultureInfo.InvariantCulture;

        static string F(float v) => v.ToString("G7", k_Culture);
        static string I(int v) => v.ToString(k_Culture);
        static string L(long v) => v.ToString(k_Culture);
        static string B(bool v) => v ? "1" : "0";

        public static readonly List<Column> columns = new List<Column>
        {
            // --- identity ---
            new Column("schemaVersion", Better.Neutral, _ => I(schemaVersion)),
            new Column("mesh",          Better.Neutral, m => m.meshName),
            new Column("target",        Better.Neutral, m => I(m.targetQuadCount)),
            new Column("profile",       Better.Neutral, m => m.profile),
            new Column("variant",       Better.Neutral, m => m.variant),

            // --- size ---
            new Column("inputVerts",    Better.Neutral, m => I(m.inputVertices)),
            new Column("inputTris",     Better.Neutral, m => I(m.inputTriangles)),
            new Column("outVerts",      Better.Neutral, m => I(m.outputVertices)),
            new Column("outFaces",      Better.Neutral, m => I(m.outputFaces)),

            // --- topology quality ---
            new Column("quadFraction",  Better.Higher,  m => F(m.quadFraction)),
            new Column("quads",         Better.Neutral, m => I(m.quadCount)),
            new Column("tris",          Better.Neutral, m => I(m.triangleCount)),
            new Column("ngons",         Better.Lower,   m => I(m.ngonCount)),
            new Column("valenceDefect", Better.Lower,   m => I(m.valenceDefect)),
            new Column("irregularFrac", Better.Lower,   m => F(m.irregularFraction)),

            // --- validity ---
            new Column("nonManifoldEdges", Better.Lower, m => I(m.nonManifoldEdges)),
            // Lower, not Neutral: a rise in boundary edges on closed input is holes appearing,
            // which is a regression the gate must fail on rather than merely report.
            new Column("boundaryEdges",    Better.Lower, m => I(m.boundaryEdges)),
            new Column("isolatedVerts",    Better.Lower, m => I(m.isolatedVertices)),
            new Column("duplicateFaces",   Better.Lower, m => I(m.duplicateFaces)),
            new Column("degenerateFaces",  Better.Lower, m => I(m.degenerateFaces)),
            new Column("euler",            Better.Neutral, m => I(m.eulerCharacteristic)),
            // chi = 2C - 2g, so euler is unreadable without C. Four spheres also give chi = 8.
            new Column("components",       Better.Lower, m => I(m.componentCount)),
            new Column("windingOk",        Better.Higher, m => B(m.windingConsistent)),
            new Column("watertight",       Better.Neutral, m => B(m.isWatertight)),
            new Column("planarityMean",    Better.Lower, m => F(m.planarityMean)),
            new Column("planarityP95",     Better.Lower, m => F(m.planarityP95)),

            // --- geometry ---
            new Column("chamferMean",   Better.Lower,   m => F(m.chamferMean)),
            new Column("chamferP95",    Better.Lower,   m => F(m.chamferP95)),
            new Column("hausdorffMax",  Better.Lower,   m => F(m.hausdorffMax)),
            new Column("normalConsist", Better.Higher,  m => F(m.normalConsistency)),
            new Column("normalFlipped", Better.Lower,   m => F(m.normalFlippedFraction)),

            // --- intent ---
            new Column("faceCountRatio", Better.Neutral, m => F(m.faceCountRatio)),
            new Column("maxQuadsInput",  Better.Neutral, m => I(m.maxQuadCountForInput)),
            new Column("targetClamped",  Better.Neutral, m => B(m.targetWasClamped)),

            // --- field ---
            new Column("spacingSpread",    Better.Neutral, m => F(m.spacingSpread)),
            new Column("latticeMismatch",  Better.Lower,  m => F(m.latticeMismatch)),
            new Column("reprDrift",        Better.Lower,  m => F(m.representativeDrift)),
            new Column("sameCellFrac",     Better.Neutral, m => F(m.sameCellFraction)),
            new Column("oneStepFrac",      Better.Higher, m => F(m.oneStepFraction)),
            new Column("discardedFrac",    Better.Lower,  m => F(m.discardedFraction)),
            new Column("orientEnergy",     Better.Higher, m => F(m.orientationEnergy)),
            new Column("orientIters",      Better.Neutral, m => I(m.orientationIterations)),
            new Column("positionIters",    Better.Neutral, m => I(m.positionIterations)),
            new Column("singularities",    Better.Neutral, m => I(m.singularityCount)),
            // A topological invariant, so any change at all is a bug in the detector rather than a
            // quality movement — hence Exact rather than Higher/Lower.
            new Column("indexSum",         Better.Exact,  m => I(m.indexSum)),

            // --- cost ---
            new Column("topologyMs",   Better.Neutral, m => L(m.topologyMs)),
            new Column("orientationMs", Better.Neutral, m => L(m.orientationMs)),
            new Column("positionMs",   Better.Neutral, m => L(m.positionMs)),
            new Column("extractionMs", Better.Neutral, m => L(m.extractionMs)),
            new Column("totalMs",      Better.Neutral, m => L(m.totalMs)),

            // --- provenance / notes ---
            new Column("gitSha",        Better.Neutral, _ => s_Context.gitSha),
            new Column("unityVersion",  Better.Neutral, _ => s_Context.unityVersion),
            new Column("corpusVersion", Better.Neutral, _ => I(s_Context.corpusVersion)),
            new Column("notes",         Better.Neutral, m => m.notes)
        };

        static RunContext s_Context = new RunContext();

        // ------------------------------------------------------------------
        public static void WriteCsv(string path, List<MeshMetrics> rows, RunContext context)
        {
            s_Context = context ?? new RunContext();

            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var sb = new StringBuilder();

            for (var c = 0; c < columns.Count; c++)
            {
                if (c > 0) sb.Append(',');
                sb.Append(columns[c].name);
            }
            sb.Append('\n');

            foreach (var row in rows)
            {
                for (var c = 0; c < columns.Count; c++)
                {
                    if (c > 0) sb.Append(',');
                    sb.Append(Escape(columns[c].read(row)));
                }
                sb.Append('\n');
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Debug.Log($"[Benchmark] Wrote {rows.Count} rows to {path}");
        }

        static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
            return '"' + value.Replace("\"", "\"\"") + '"';
        }

        /// <summary>
        /// A compact table for humans. Only the columns worth reading at a glance — the CSV is where
        /// the full picture lives.
        /// </summary>
        public static string ToMarkdown(List<MeshMetrics> rows)
        {
            // boundaryEdges and euler are here, not just in the CSV, because leaving them out is
            // how this project reported "validity is clean everywhere" for weeks while the sphere
            // output had 83 boundary edges and an Euler characteristic of -7. The CSV had both
            // numbers the whole time. Nobody reads the CSV at a glance; this is what gets read.
            // Do not trim the validity group to make the table narrower.
            var shown = new[]
            {
                "mesh", "target", "variant", "outFaces", "quadFraction",
                "nonManifoldEdges", "boundaryEdges", "euler", "components", "watertight",
                "chamferMean", "hausdorffMax", "irregularFrac", "sameCellFrac", "oneStepFrac",
                "discardedFrac", "totalMs"
            };

            var picked = new List<Column>();
            foreach (var name in shown)
            {
                var found = columns.Find(c => c.name == name);
                if (found != null) picked.Add(found);
            }

            var sb = new StringBuilder();

            sb.Append('|');
            foreach (var c in picked) sb.Append(' ').Append(c.name).Append(" |");
            sb.Append('\n').Append('|');
            foreach (var c in picked) sb.Append(c.better == Better.Neutral ? " --- |" : " ---: |");
            sb.Append('\n');

            foreach (var row in rows)
            {
                sb.Append('|');
                foreach (var c in picked) sb.Append(' ').Append(c.read(row)).Append(" |");
                sb.Append('\n');
            }

            return sb.ToString();
        }

        // ------------------------------------------------------------------
        public sealed class Regression
        {
            public string key;
            public string column;
            public string baseline;
            public string current;
            public float relativeChange;

            public override string ToString() =>
                $"{key} :: {column}  {baseline} -> {current}  ({relativeChange:P1})";
        }

        /// <summary>
        /// Compares a run against a committed baseline CSV. Rows are matched on
        /// (mesh, target, profile, variant); rows present in only one file are reported as notes
        /// rather than regressions, since adding a corpus mesh is not a regression.
        /// </summary>
        public static List<Regression> CompareToBaseline(string baselinePath, List<MeshMetrics> current,
            float tolerance, out List<string> notes)
        {
            notes = new List<string>();
            var regressions = new List<Regression>();

            if (!File.Exists(baselinePath))
            {
                notes.Add($"No baseline at '{baselinePath}' — nothing to compare. " +
                          "Commit this run as the baseline if it is the intended reference.");
                return regressions;
            }

            var lines = File.ReadAllLines(baselinePath);
            if (lines.Length < 1)
            {
                notes.Add($"Baseline '{baselinePath}' is empty.");
                return regressions;
            }

            var header = SplitCsvLine(lines[0]);
            var schemaIndex = Array.IndexOf(header, "schemaVersion");

            var baselineRows = new Dictionary<string, string[]>();
            for (var i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var fields = SplitCsvLine(lines[i]);

                if (schemaIndex >= 0 && schemaIndex < fields.Length &&
                    fields[schemaIndex] != schemaVersion.ToString(k_Culture))
                {
                    notes.Add($"Baseline schemaVersion is {fields[schemaIndex]}, current is " +
                              $"{schemaVersion}. Columns have changed — regenerate the baseline. " +
                              "Comparison abandoned.");
                    return regressions;
                }

                baselineRows[RowKey(header, fields)] = fields;
            }

            foreach (var row in current)
            {
                var key = $"{row.meshName}|{row.targetQuadCount}|{row.profile}|{row.variant}";
                if (!baselineRows.TryGetValue(key, out var baseFields))
                {
                    notes.Add($"New row not in baseline: {key}");
                    continue;
                }

                for (var c = 0; c < columns.Count; c++)
                {
                    var column = columns[c];
                    if (column.better == Better.Neutral) continue;

                    var columnIndex = Array.IndexOf(header, column.name);
                    if (columnIndex < 0 || columnIndex >= baseFields.Length) continue;

                    var baseText = baseFields[columnIndex];
                    var currentText = column.read(row);

                    if (column.better == Better.Exact)
                    {
                        if (baseText != currentText)
                            regressions.Add(new Regression
                            {
                                key = key, column = column.name,
                                baseline = baseText, current = currentText, relativeChange = 0f
                            });
                        continue;
                    }

                    if (!float.TryParse(baseText, NumberStyles.Float, k_Culture, out var baseValue) ||
                        !float.TryParse(currentText, NumberStyles.Float, k_Culture, out var currentValue))
                        continue;

                    var delta = currentValue - baseValue;
                    var magnitude = Mathf.Max(Mathf.Abs(baseValue), 1e-6f);
                    var relative = delta / magnitude;

                    var worse = column.better == Better.Higher ? -relative : relative;
                    if (worse > tolerance)
                        regressions.Add(new Regression
                        {
                            key = key, column = column.name,
                            baseline = baseText, current = currentText, relativeChange = relative
                        });
                }
            }

            return regressions;
        }

        static string RowKey(string[] header, string[] fields)
        {
            string Get(string name)
            {
                var index = Array.IndexOf(header, name);
                return index >= 0 && index < fields.Length ? fields[index] : "";
            }

            return $"{Get("mesh")}|{Get("target")}|{Get("profile")}|{Get("variant")}";
        }

        static string[] SplitCsvLine(string line)
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            var quoted = false;

            for (var i = 0; i < line.Length; i++)
            {
                var ch = line[i];

                if (quoted)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else quoted = false;
                    }
                    else sb.Append(ch);
                }
                else if (ch == '"') quoted = true;
                else if (ch == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(ch);
            }

            fields.Add(sb.ToString());
            return fields.ToArray();
        }
    }
}
