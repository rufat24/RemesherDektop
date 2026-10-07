# Compares the DELIVERED asset against the DELIVERED competitor output on the four things
# the user named: face direction, normal direction, topology distortion, smoothness.
#
# Runs under Blender's bundled python (there is no system python on this machine):
#   blender --background --factory-startup --python shading.py
# It imports nothing from bpy; blender is only the interpreter.

import struct, math, sys, os
from collections import defaultdict

ROOT = "C:/Users/user/Quadremesh"


# ---------------------------------------------------------------- loaders

def read_unity_mesh(path):
    """Positions, normals and polygon faces out of a Unity .asset mesh in YAML form."""
    text = open(path, "r", encoding="utf-8", errors="replace").read()

    def field(name):
        i = text.index(name)
        j = text.index("\n", i)
        return text[i + len(name):j].strip()

    index_hex = field("m_IndexBuffer:")
    vertex_hex = field("_typelessdata:")
    vcount = int(field("m_VertexCount:"))

    vdata = bytes.fromhex(vertex_hex)
    stride = len(vdata) // vcount
    assert stride == 24, "expected float3 position + float3 normal"

    positions, normals = [], []
    for v in range(vcount):
        px, py, pz, nx, ny, nz = struct.unpack_from("<6f", vdata, v * stride)
        positions.append((px, py, pz))
        normals.append((nx, ny, nz))

    idata = bytes.fromhex(index_hex)
    indices = struct.unpack("<%dH" % (len(idata) // 2), idata)

    # Submeshes carry their own topology: 2 = quads, 0 = triangles.
    faces = []
    for block in text.split("- serializedVersion: 2")[1:]:
        if "indexCount:" not in block:
            continue
        first_byte = int(block.split("firstByte:")[1].split("\n")[0])
        count = int(block.split("indexCount:")[1].split("\n")[0])
        topo = int(block.split("topology:")[1].split("\n")[0])
        n = 4 if topo == 2 else 3
        start = first_byte // 2
        for f in range(count // n):
            faces.append(tuple(indices[start + f * n + k] for k in range(n)))

    return positions, normals, faces


def read_obj(path, want_faces=True):
    positions, faces = [], []
    with open(path, "r", errors="replace") as handle:
        for line in handle:
            if line.startswith("v "):
                p = line.split()
                positions.append((float(p[1]), float(p[2]), float(p[3])))
            elif want_faces and line.startswith("f "):
                corners = []
                for token in line.split()[1:]:
                    corners.append(int(token.split("/")[0]) - 1)
                faces.append(tuple(corners))
    return positions, faces


# ---------------------------------------------------------------- geometry

def sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def norm(a):
    m = math.sqrt(dot(a, a))
    return (a[0] / m, a[1] / m, a[2] / m) if m > 0 else (0.0, 0.0, 0.0)


def newell(positions, face):
    """Best-fit plane normal, unnormalised; magnitude is twice the projected area."""
    n = [0.0, 0.0, 0.0]
    for k in range(len(face)):
        a = positions[face[k]]
        b = positions[face[(k + 1) % len(face)]]
        n[0] += (a[1] - b[1]) * (a[2] + b[2])
        n[1] += (a[2] - b[2]) * (a[0] + b[0])
        n[2] += (a[0] - b[0]) * (a[1] + b[1])
    return tuple(n)


def percentile(values, fraction):
    if not values:
        return 0.0
    s = sorted(values)
    return s[min(len(s) - 1, max(0, int(round(fraction * (len(s) - 1)))))]


# ---------------------------------------------------------------- metrics

def analyse(name, positions, faces, stored_normals=None):
    print("")
    print("=== %s ===" % name)
    print("  vertices %d   faces %d" % (len(positions), len(faces)))

    sizes = defaultdict(int)
    for f in faces:
        sizes[len(f)] += 1
    print("  face sizes " + ", ".join("%d:%d" % (k, sizes[k]) for k in sorted(sizes)))

    # Scale, so every length below can be quoted against the model's own size.
    lo = [min(p[i] for p in positions) for i in range(3)]
    hi = [max(p[i] for p in positions) for i in range(3)]
    diag = math.sqrt(sum((hi[i] - lo[i]) ** 2 for i in range(3)))
    print("  bbox diagonal %.4f" % diag)

    face_normal, face_area, centroid = [], [], []
    for f in faces:
        n = newell(positions, f)
        area = 0.5 * math.sqrt(dot(n, n))
        face_normal.append(norm(n))
        face_area.append(area)
        c = [0.0, 0.0, 0.0]
        for v in f:
            for i in range(3):
                c[i] += positions[v][i]
        centroid.append(tuple(x / len(f) for x in c))

    mean_edge = 0.0
    edge_count = 0
    edges = defaultdict(list)
    directed = set()
    for fi, f in enumerate(faces):
        for k in range(len(f)):
            a, b = f[k], f[(k + 1) % len(f)]
            edges[(min(a, b), max(a, b))].append(fi)
            directed.add((a, b))
            mean_edge += math.sqrt(dot(sub(positions[a], positions[b]),
                                       sub(positions[a], positions[b])))
            edge_count += 1
    mean_edge /= max(1, edge_count)
    print("  mean edge %.5f  (%.2f %% of diagonal)" % (mean_edge, 100.0 * mean_edge / diag))

    # --- FACE DIRECTION, part one: is the winding consistent at all?
    # In a consistently wound manifold every interior edge is traversed once each way. An edge
    # traversed the SAME way by both its faces is a winding contradiction, and it is what makes
    # one face light from the front and its neighbour from the back.
    contradictions = 0
    interior = 0
    for (a, b), fs in edges.items():
        if len(fs) != 2:
            continue
        interior += 1
        if ((a, b) in directed) == ((b, a) in directed):
            continue
        contradictions += 1
    print("  interior edges %d   winding contradictions %d (%.2f %%)"
          % (interior, contradictions, 100.0 * contradictions / max(1, interior)))

    boundary = sum(1 for fs in edges.values() if len(fs) == 1)
    nonmanifold = sum(1 for fs in edges.values() if len(fs) > 2)
    print("  boundary edges %d   non-manifold edges %d" % (boundary, nonmanifold))

    # --- SMOOTHNESS: the angle between neighbouring faces across their shared edge.
    # This is the metric that decides how a surface SHADES, and it is independent of how close
    # the vertices sit to the original. A mesh can sit perfectly on the true surface and still
    # zigzag from face to face; that zigzag is what reads as "slimed".
    dihedral = []
    for (a, b), fs in edges.items():
        if len(fs) != 2:
            continue
        n0, n1 = face_normal[fs[0]], face_normal[fs[1]]
        if n0 == (0.0, 0.0, 0.0) or n1 == (0.0, 0.0, 0.0):
            continue
        dihedral.append(math.degrees(math.acos(max(-1.0, min(1.0, dot(n0, n1))))))
    dihedral.sort()
    mean_dihedral = sum(dihedral) / max(1, len(dihedral))
    print("  DIHEDRAL between neighbours: mean %.2f deg  p50 %.2f  p90 %.2f  p99 %.2f  max %.2f"
          % (mean_dihedral, percentile(dihedral, 0.50), percentile(dihedral, 0.90),
             percentile(dihedral, 0.99), dihedral[-1] if dihedral else 0.0))
    for threshold in (10, 20, 30, 45, 60, 90):
        share = 100.0 * sum(1 for d in dihedral if d > threshold) / max(1, len(dihedral))
        print("      > %2d deg: %6.2f %%" % (threshold, share))

    # --- FACE DIRECTION, part two: how many faces point against their own neighbourhood.
    # A face whose normal opposes the average of the faces around it is the "wrongly directed"
    # one: it is not merely skewed, it faces the other way and lights black.
    flipped = 0
    for fi, f in enumerate(faces):
        acc = [0.0, 0.0, 0.0]
        got = 0
        for k in range(len(f)):
            key = (min(f[k], f[(k + 1) % len(f)]), max(f[k], f[(k + 1) % len(f)]))
            for g in edges[key]:
                if g == fi:
                    continue
                for i in range(3):
                    acc[i] += face_normal[g][i]
                got += 1
        if got and dot(face_normal[fi], norm(tuple(acc))) < 0.0:
            flipped += 1
    print("  faces opposing their own neighbourhood: %d (%.2f %%)"
          % (flipped, 100.0 * flipped / max(1, len(faces))))

    # --- TOPOLOGY DISTORTION: shape of the faces themselves.
    worst_angle, aspect, planarity = [], [], []
    nonconvex = 0
    for fi, f in enumerate(faces):
        w = 180.0
        shortest, longest = 1e30, 0.0
        convex = True
        for k in range(len(f)):
            prv = positions[f[(k - 1) % len(f)]]
            cur = positions[f[k]]
            nxt = positions[f[(k + 1) % len(f)]]
            u, v = sub(prv, cur), sub(nxt, cur)
            lu = math.sqrt(dot(u, u))
            lv = math.sqrt(dot(v, v))
            if lu < 1e-12 or lv < 1e-12:
                w = 0.0
                continue
            w = min(w, math.degrees(math.acos(max(-1.0, min(1.0, dot(u, v) / (lu * lv))))))
            if dot(cross(v, u), face_normal[fi]) < 0.0:
                convex = False
            shortest = min(shortest, lv)
            longest = max(longest, lv)
        if not convex:
            nonconvex += 1
        worst_angle.append(w)
        aspect.append(shortest / longest if longest > 0 else 0.0)
        if len(f) == 4:
            off = max(abs(dot(face_normal[fi], sub(positions[v], centroid[fi]))) for v in f)
            planarity.append(off / mean_edge)

    print("  min interior angle: p01 %.1f  p05 %.1f  p50 %.1f   worst %.1f"
          % (percentile(worst_angle, 0.01), percentile(worst_angle, 0.05),
             percentile(worst_angle, 0.50), min(worst_angle)))
    print("  edge aspect ratio:  p01 %.3f  p05 %.3f  p50 %.3f"
          % (percentile(aspect, 0.01), percentile(aspect, 0.05), percentile(aspect, 0.50)))
    print("  non-convex (curled) faces: %d (%.2f %%)"
          % (nonconvex, 100.0 * nonconvex / max(1, len(faces))))
    if planarity:
        print("  quad non-planarity / edge: p50 %.4f  p95 %.4f  max %.4f"
              % (percentile(planarity, 0.50), percentile(planarity, 0.95), max(planarity)))

    # --- NORMAL DIRECTION: does the stored shading normal agree with the geometry it is on?
    if stored_normals:
        disagree = []
        backwards = 0
        for fi, f in enumerate(faces):
            for v in f:
                sn = stored_normals[v]
                if dot(sn, sn) < 1e-12:
                    continue
                c = max(-1.0, min(1.0, dot(norm(sn), face_normal[fi])))
                disagree.append(math.degrees(math.acos(c)))
                if c < 0.0:
                    backwards += 1
        disagree.sort()
        print("  stored normal vs its own face: mean %.1f deg  p50 %.1f  p95 %.1f  p99 %.1f"
              % (sum(disagree) / max(1, len(disagree)), percentile(disagree, 0.50),
                 percentile(disagree, 0.95), percentile(disagree, 0.99)))
        print("  corners whose stored normal points AWAY from its face: %d (%.2f %%)"
              % (backwards, 100.0 * backwards / max(1, len(disagree))))

    return {"diag": diag, "mean_edge": mean_edge, "faces": len(faces),
            "dihedral": dihedral, "mean_dihedral": mean_dihedral}


# ---------------------------------------------------------------- main

ours_path = os.path.join(ROOT, "Assets/NativeRemesher/Output/20260515221044_54b8f8f4_Quads 26.asset")
theirs_path = os.path.join(ROOT, "Benchmarks/reference/qr-character.obj")

import sys
args = sys.argv[sys.argv.index("--")+1:] if "--" in sys.argv else []
if args:
    op, of = read_obj(args[0]); on = None; ours_path = args[0]
    if len(args) > 1: theirs_path = args[1]
else:
    op, on, of = read_unity_mesh(ours_path)
ours = analyse("OURS  (%s)" % os.path.basename(ours_path), op, of, on)

tp, tf = read_obj(theirs_path)
theirs = analyse("THEIRS  (QuadRemesher 20260515221044_54b8f8f4_Retopo 4.fbx)", tp, tf)

print("")
print("=== side by side ===")
print("  face count      ours %6d      theirs %6d      ratio %.2f x"
      % (ours["faces"], theirs["faces"], theirs["faces"] / max(1, ours["faces"])))
print("  mean edge / diag  ours %.4f %%   theirs %.4f %%"
      % (100 * ours["mean_edge"] / ours["diag"], 100 * theirs["mean_edge"] / theirs["diag"]))
print("  mean dihedral   ours %6.2f deg  theirs %6.2f deg"
      % (ours["mean_dihedral"], theirs["mean_dihedral"]))
