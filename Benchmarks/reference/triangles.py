# Are the leftover triangles PAIRABLE, or structurally stranded?
#
# The output is 8 % triangles where the competitor is 0 %, and the fix depends entirely on which of
# two things that is:
#
#   - two triangles sitting side by side that nobody merged. A local defect, and the regulariser
#     should simply be taking them.
#   - triangles alone in a field of quads, several faces from the nearest other one. Those cannot be
#     merged with anything: a quad mesh can only lose triangles in PAIRS (each merge takes two and
#     makes one quad), so an odd triangle has to be walked to a partner along a path of quads,
#     flipping as it goes, or absorbed by splitting. Completely different work.
#
# So: for every triangle, how many face-adjacency steps to the nearest other triangle.
#
#   blender --background --factory-startup --python triangles.py -- mesh.asset

import sys, math
from collections import defaultdict, deque

sys.path.insert(0, __file__.rsplit("\\", 1)[0].rsplit("/", 1)[0])
from defects import load, newell, norm, sub, dot


def report(path):
    P, F = load(path)

    edge_faces = defaultdict(list)
    for i, f in enumerate(F):
        for k in range(len(f)):
            a, b = f[k], f[(k + 1) % len(f)]
            edge_faces[(min(a, b), max(a, b))].append(i)

    neighbours = defaultdict(list)
    for fs in edge_faces.values():
        if len(fs) == 2:
            neighbours[fs[0]].append(fs[1])
            neighbours[fs[1]].append(fs[0])

    tris = [i for i, f in enumerate(F) if len(f) == 3]
    triset = set(tris)

    print("")
    print("=== %s ===" % path.rsplit("/", 1)[-1])
    print("  %d faces, %d triangles (%.2f %%)" % (len(F), len(tris), 100.0 * len(tris) / len(F)))
    if not tris:
        return

    # Breadth-first from each triangle until another triangle is reached. Capped, because a
    # triangle twenty faces from its nearest partner is stranded for any practical purpose.
    hist = defaultdict(int)
    for t in tris:
        seen = {t}
        queue = deque([(t, 0)])
        found = None
        while queue and found is None:
            i, d = queue.popleft()
            if d >= 8:
                break
            for j in neighbours[i]:
                if j in seen:
                    continue
                if j in triset:
                    found = d + 1
                    break
                seen.add(j)
                queue.append((j, d + 1))
        hist[found if found is not None else "8+"] += 1

    print("  steps from each triangle to the nearest other triangle:")
    for k in sorted(hist, key=lambda x: (x == "8+", x)):
        label = "adjacent (mergeable now)" if k == 1 else ("%s steps" % k)
        print("    %-26s %4d  (%.1f %%)" % (label, hist[k], 100.0 * hist[k] / len(tris)))

    # A pair of adjacent triangles is only worth merging if the quad it makes is not awful, so
    # score the ones that are adjacent: the merged quad has to stay convex and reasonably shaped.
    mergeable, poor = 0, 0
    done = set()
    for t in tris:
        if t in done:
            continue
        for j in neighbours[t]:
            if j in triset and j not in done:
                shared = [v for v in F[t] if v in F[j]]
                if len(shared) != 2:
                    continue
                a = [v for v in F[t] if v not in shared][0]
                b = [v for v in F[j] if v not in shared][0]
                quad = [a, shared[0], b, shared[1]]
                worst = 180.0
                for k in range(4):
                    u = sub(P[quad[(k - 1) % 4]], P[quad[k]])
                    v = sub(P[quad[(k + 1) % 4]], P[quad[k]])
                    lu, lv = math.sqrt(dot(u, u)), math.sqrt(dot(v, v))
                    if lu < 1e-12 or lv < 1e-12:
                        worst = 0.0
                        break
                    worst = min(worst, math.degrees(
                        math.acos(max(-1.0, min(1.0, dot(u, v) / (lu * lv))))))
                if worst >= 25.0:
                    mergeable += 1
                else:
                    poor += 1
                done.add(t)
                done.add(j)
                break

    print("  adjacent pairs: %d would merge into a quad with every angle >= 25 deg, "
          "%d would not" % (mergeable, poor))
    print("  so %d triangles could go today for free, leaving %d that need a path or a split"
          % (2 * mergeable, len(tris) - 2 * mergeable))

    # Where the stranded ones sit relative to a valence-3 or valence-5 vertex: a triangle next to a
    # singularity is the tracer failing to close a ring, which is a different cause again.
    valence = defaultdict(int)
    for f in F:
        for v in f:
            valence[v] += 1
    near_sing = sum(1 for t in tris if any(valence[v] != 4 for v in F[t]))
    print("  %d of %d triangles touch a vertex of valence != 4 (%.0f %%)"
          % (near_sing, len(tris), 100.0 * near_sing / len(tris)))


if __name__ == "__main__":
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    for a in args:
        report(a)
