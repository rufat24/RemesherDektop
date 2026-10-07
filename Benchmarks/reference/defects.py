# WHERE a mesh is bad, not how bad it is on average.
#
# Every number this project had was a whole-mesh statistic, and the complaint they cannot answer is
# "some parts are a mess of polygons". A mesh can carry 4.5 % of its edges past 45 degrees spread
# evenly and look uniformly soft, or carry the same 4.5 % piled into six places and look broken in
# six places. Those are different defects with different causes and the averages are identical.
#
# So: flag each face by defect kind, cluster the flagged faces that touch each other, and report the
# clusters largest first with a position and an extent. A cluster is a place to go and look.
#
#   blender --background --factory-startup --python defects.py -- ours.asset theirs.obj

import struct, math, sys
from collections import defaultdict


def read_unity_mesh(path):
    text = open(path, "r", encoding="utf-8", errors="replace").read()

    def field(name):
        i = text.index(name)
        return text[i + len(name):text.index("\n", i)].strip()

    idata = bytes.fromhex(field("m_IndexBuffer:"))
    vdata = bytes.fromhex(field("_typelessdata:"))
    vcount = int(field("m_VertexCount:"))
    stride = len(vdata) // vcount

    positions = [struct.unpack_from("<3f", vdata, v * stride) for v in range(vcount)]
    indices = struct.unpack("<%dH" % (len(idata) // 2), idata)

    faces = []
    for block in text.split("- serializedVersion: 2")[1:]:
        if "indexCount:" not in block:
            continue
        first = int(block.split("firstByte:")[1].split("\n")[0])
        count = int(block.split("indexCount:")[1].split("\n")[0])
        topo = int(block.split("topology:")[1].split("\n")[0])
        n = 4 if topo == 2 else 3
        start = first // 2
        for f in range(count // n):
            faces.append(tuple(indices[start + f * n + k] for k in range(n)))
    return positions, faces


def read_obj(path):
    positions, faces = [], []
    for line in open(path, "r", errors="replace"):
        if line.startswith("v "):
            p = line.split()
            positions.append((float(p[1]), float(p[2]), float(p[3])))
        elif line.startswith("f "):
            faces.append(tuple(int(t.split("/")[0]) - 1 for t in line.split()[1:]))
    return positions, faces


def load(path):
    return read_unity_mesh(path) if path.endswith(".asset") else read_obj(path)


def sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def norm(a):
    m = math.sqrt(dot(a, a))
    return (a[0] / m, a[1] / m, a[2] / m) if m > 0 else (0.0, 0.0, 0.0)


def newell(P, f):
    n = [0.0, 0.0, 0.0]
    for k in range(len(f)):
        a, b = P[f[k]], P[f[(k + 1) % len(f)]]
        n[0] += (a[1] - b[1]) * (a[2] + b[2])
        n[1] += (a[2] - b[2]) * (a[0] + b[0])
        n[2] += (a[0] - b[0]) * (a[1] + b[1])
    return tuple(n)


def centroid(P, f):
    return tuple(sum(P[i][k] for i in f) / len(f) for k in range(3))


def audit(name, path, top=12):
    P, F = load(path)

    lo = [min(p[k] for p in P) for k in range(3)]
    hi = [max(p[k] for p in P) for k in range(3)]
    diag = math.sqrt(sum((hi[k] - lo[k]) ** 2 for k in range(3)))

    normals = [norm(newell(P, f)) for f in F]

    edge_faces = defaultdict(list)
    for i, f in enumerate(F):
        for k in range(len(f)):
            a, b = f[k], f[(k + 1) % len(f)]
            edge_faces[(min(a, b), max(a, b))].append(i)

    # Per-face defects. Each is a thing a person would point at and call broken.
    flags = [set() for _ in F]

    for i, f in enumerate(F):
        if len(f) == 3:
            flags[i].add("tri")
        elif len(f) > 4:
            flags[i].add("ngon")

        worst, convex = 180.0, True
        for k in range(len(f)):
            u = sub(P[f[(k - 1) % len(f)]], P[f[k]])
            v = sub(P[f[(k + 1) % len(f)]], P[f[k]])
            lu, lv = math.sqrt(dot(u, u)), math.sqrt(dot(v, v))
            if lu < 1e-12 or lv < 1e-12:
                worst = 0.0
                continue
            worst = min(worst, math.degrees(math.acos(max(-1.0, min(1.0, dot(u, v) / (lu * lv))))))
            if dot(cross(v, u), normals[i]) < 0:
                convex = False
        if worst < 20.0:
            flags[i].add("sliver")
        if not convex:
            flags[i].add("curled")

    # Folds: an edge whose two faces disagree past 60 degrees flags BOTH of them, because a fold is
    # a relationship and the eye sees the crease, not one side of it.
    for (a, b), fs in edge_faces.items():
        if len(fs) != 2:
            for i in fs:
                flags[i].add("openedge")
            continue
        if dot(normals[fs[0]], normals[fs[1]]) < math.cos(math.radians(60.0)):
            flags[fs[0]].add("fold")
            flags[fs[1]].add("fold")

    bad = [i for i, s in enumerate(flags) if s]

    print("")
    print("=== %s ===" % name)
    print("  %d faces, %d flagged (%.2f %%)" % (len(F), len(bad), 100.0 * len(bad) / max(1, len(F))))

    kinds = defaultdict(int)
    for s in flags:
        for k in s:
            kinds[k] += 1
    print("  by kind: " + ", ".join("%s %d" % (k, v)
                                    for k, v in sorted(kinds.items(), key=lambda x: -x[1])))

    # Cluster flagged faces that share an edge. Face adjacency, not distance: two bad faces on
    # opposite sides of a thin limb are near each other in space and unrelated on the surface.
    neighbours = defaultdict(list)
    for fs in edge_faces.values():
        if len(fs) == 2:
            neighbours[fs[0]].append(fs[1])
            neighbours[fs[1]].append(fs[0])

    badset = set(bad)
    seen = set()
    clusters = []
    for start in bad:
        if start in seen:
            continue
        stack, group = [start], []
        seen.add(start)
        while stack:
            i = stack.pop()
            group.append(i)
            for j in neighbours[i]:
                if j in badset and j not in seen:
                    seen.add(j)
                    stack.append(j)
        clusters.append(group)

    clusters.sort(key=len, reverse=True)
    big = [c for c in clusters if len(c) >= 5]
    print("  %d clusters; %d are a single isolated face, %d have 5 or more"
          % (len(clusters), sum(1 for c in clusters if len(c) == 1), len(big)))
    print("  faces inside a cluster of 5+: %d (%.2f %% of the mesh)"
          % (sum(len(c) for c in big), 100.0 * sum(len(c) for c in big) / max(1, len(F))))

    print("  largest clusters (position is the centre as a percentage of the bounding box,")
    print("  extent is the diameter as a percentage of the bounding diagonal):")
    for c in clusters[:top]:
        if len(c) < 3:
            break
        pts = [centroid(P, F[i]) for i in c]
        mid = tuple(sum(p[k] for p in pts) / len(pts) for k in range(3))
        ext = 0.0
        for p in pts:
            for q in pts:
                ext = max(ext, math.sqrt(sum((p[k] - q[k]) ** 2 for k in range(3))))
        kk = defaultdict(int)
        for i in c:
            for k in flags[i]:
                kk[k] += 1
        rel = tuple(100.0 * (mid[k] - lo[k]) / max(1e-12, hi[k] - lo[k]) for k in range(3))
        print("    %3d faces  at (%.0f,%.0f,%.0f)%%  extent %.2f %%  [%s]"
              % (len(c), rel[0], rel[1], rel[2], 100.0 * ext / diag,
                 ", ".join("%s %d" % (k, v) for k, v in sorted(kk.items(), key=lambda x: -x[1]))))
    return len(F), len(bad), clusters


if __name__ == "__main__":
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    audit("OURS", args[0])
    if len(args) > 1:
        audit("THEIRS", args[1])
