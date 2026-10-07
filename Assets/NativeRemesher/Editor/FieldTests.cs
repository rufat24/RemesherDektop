// Compatibility shim. The real tests moved to Assets/NativeRemesher/Tests/ and run under NUnit.
//
// Why they moved: this file used to be a hand-rolled harness that counted failures itself and
// called EditorApplication.Exit when it was done. That made it unusable from inside anything
// larger — the benchmark runner, a combined test pass, or an IDE test window — because it would
// tear the editor down mid-run. com.unity.test-framework was already a dependency of this project,
// so the harness was reimplementing a worse version of something already available, and getting
// machine-readable results out of it required parsing the log.
//
// Every assertion it carried was ported, and several were strengthened along the way: surface area
// is now checked against its exact analytic value rather than just "> 0", the welded cube is
// checked via its Euler characteristic, and there are new tests for adjacency ordering and
// cross-build determinism that this harness had no equivalent of.
//
// The menu item and method are kept so the command documented in HANDOVER.md keeps working instead
// of failing with a missing method. This shim can be deleted once that document is updated.

using UnityEditor;
using UnityEngine;

namespace NativeRemesher
{
    internal static class FieldTests
    {
        const string k_Message =
            "[Native Remesher] The field tests moved to NUnit and this entry point is a shim.\n" +
            "  In the editor:  Window > General > Test Runner > EditMode\n" +
            "  Headless:       Unity.exe -batchmode -nographics -projectPath . " +
            "-runTests -testPlatform EditMode -testResults results.xml\n" +
            "  Source:         Assets/NativeRemesher/Tests/\n" +
            "  Benchmark:      Tools > Native Remesher > Run Benchmark, or " +
            "-executeMethod NativeRemesher.Benchmark.BenchmarkRunner.RunFromCommandLine";

        [MenuItem("Tools/Native Remesher/Run Field Tests (moved)", false, 100)]
        public static void Run()
        {
            Debug.Log(k_Message);

            // Deliberately does NOT exit with a failure code. Returning 0 from a shim that ran no
            // assertions would be a lie if anyone gated on it, and returning 1 would break a build
            // that is merely calling a stale command. Exiting 2 says "this told you nothing".
            if (Application.isBatchMode)
                EditorApplication.Exit(2);
        }
    }
}
