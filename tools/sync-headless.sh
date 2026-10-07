#!/usr/bin/env bash
# Mirror NativeRemesher's code into a lean headless project, so batch-mode measurement runs
# while the real project stays open in the editor.
#
# Unity refuses to open a project that another instance holds (HandleProjectAlreadyOpenInAnotherInstance
# aborts before -executeMethod runs), which made every benchmark a request to close the editor.
# This sidesteps that instead of negotiating with it.
#
# Runtime, Benchmark and Tests are mirrored. Editor/ is deliberately left out: it is window UI with
# nothing to measure headlessly, and leaving it behind keeps the mirror clear of the GUI assemblies.
# Everything mirrored is engine-only apart from the test framework, so the manifest below is the
# whole dependency set.
#
# The mirror is one-way and destructive on the destination. Never edit code there — Headless/Assets/
# is gitignored for that reason, and everything under it is regenerated from this repo by this
# script. What IS committed under Headless/ is the project scaffolding: ProjectSettings, the package
# manifest, and a README. So a fresh clone runs this once and has a working measurement project.
#
# The one thing a clone cannot regenerate is Benchmarks/reference/ref_high.obj — 56 MB derived from
# the user-supplied scan in Assets/ccnb/, which is deliberately not in the repo. Headless/README.md
# says how to rebuild it.
set -euo pipefail

# Both default to this checkout, so a fresh clone needs no editing: the script finds the repo from
# its own location, and the mirror lives inside it at Headless/.
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC="${SRC:-$(dirname "$HERE")}"
DST="${DST:-$SRC/Headless}"
EDITOR="${EDITOR_EXE:-/c/Program Files/Unity/Hub/Editor/6000.0.58f2/Editor/Unity.exe}"

mkdir -p "$DST/Assets" "$DST/Packages" "$DST/ProjectSettings" "$DST/Benchmarks/reference"

# Every NativeRemesher* folder. Only NativeRemesher exists today - the two parallel architectures
# that once lived alongside it were measured, found not to help, and removed. The glob stays so a
# future parallel implementation is measured by the same command as the one it competes with.
for module in "$SRC/Assets"/NativeRemesher*; do
  [ -d "$module" ] || continue
  name="$(basename "$module")"

  rm -rf "$DST/Assets/$name"
  mkdir -p "$DST/Assets/$name"

  # Editor/ is skipped for the same reason everywhere: window UI, nothing to measure headlessly.
  for part in Runtime Benchmark Tests; do
    [ -d "$module/$part" ] && cp -r "$module/$part" "$DST/Assets/$name/"
  done

  echo "  mirrored $name"
done

cp "$SRC/ProjectSettings/ProjectVersion.txt" "$DST/ProjectSettings/" 2>/dev/null || true

cat > "$DST/Packages/manifest.json" <<'JSON'
{
  "dependencies": {
    "com.unity.test-framework": "1.5.1",
    "com.unity.modules.imgui": "1.0.0",
    "com.unity.modules.jsonserialize": "1.0.0"
  },
  "testables": ["com.unity.test-framework"]
}
JSON

echo "synced $SRC -> $DST"
