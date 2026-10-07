# Do an FBX's stored normals agree with its own geometry?
#
# A mesh can have perfect vertex positions and still shade as though it were dented, inverted or
# faceted, because lighting reads the STORED normals and not the surface. Nothing in this project's
# geometric measurements can see that: deviation, coverage, dihedral and planarity are all computed
# from positions, and every one of them is happy with a mesh whose normals point the wrong way.
#
# So this compares, on the file as written:
#
#   FACE vs STORED. For each polygon, the Newell normal of its own corners against the average of
#   the stored normals at those corners. Agreement near +1 is correct. Near -1 means the winding
#   and the normals disagree - the classic result of negating one axis for handedness while
#   reversing corner order, when only one of the two also gets applied to the normals. That mesh
#   renders inside-out, and under a smooth shader every bulge reads as a hollow.
#
#   UNIT LENGTH, because a normal that is not unit length scales the lighting term.
#
#   WINDING CONSISTENCY between neighbouring faces, which is a property of the index buffer alone.
#
#   python fbxcheck.py file.fbx
#
# ASCII FBX only - which is what FbxWriter emits. Blender cannot import those at all, so this is
# also the only way to inspect one.

import sys, re, math


def block(text, key, after=0):
    m = re.search(re.escape(key) + r"\s*:\s*\*\d+\s*\{\s*a:\s*", text[after:])
    if not m:
        return None, after
    i = after + m.end()
    depth, out = 1, []
    while i < len(text):
        c = text[i]
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                break
        else:
            out.append(c)
        i += 1
    return "".join(out), i


def nums(s):
    return [float(x) for x in s.replace("\n", "").split(",") if x.strip()]


def main(path):
    text = open(path, "r", errors="ignore").read()

    vraw, _ = block(text, "Vertices")
    iraw, _ = block(text, "PolygonVertexIndex")
    nraw, _ = block(text, "Normals")
    if vraw is None or iraw is None:
        raise SystemExit("not an ASCII FBX with Vertices/PolygonVertexIndex")

    coords = nums(vraw)
    idx = [int(x) for x in iraw.replace("\n", "").split(",") if x.strip()]
    verts = [(coords[i], coords[i + 1], coords[i + 2]) for i in range(0, len(coords), 3)]

    faces, cur = [], []
    for v in idx:
        if v < 0:
            cur.append(-v - 1)
            faces.append(cur)
            cur = []
        else:
            cur.append(v)

    sizes = {}
    for f in faces:
        sizes[len(f)] = sizes.get(len(f), 0) + 1

    print(f"\n{path}")
    print(f"  {len(verts)} vertices, {len(faces)} polygons, sizes {sizes}")

    if nraw is None:
        print("  NO stored normals - the importer will compute them.")
        return

    nc = nums(nraw)
    norms = [(nc[i], nc[i + 1], nc[i + 2]) for i in range(0, len(nc), 3)]
    mode = re.search(r'MappingInformationType:\s*"(\w+)"', text)
    mode = mode.group(1) if mode else "?"
    print(f"  {len(norms)} stored normals, mapping {mode}")

    lens = [math.sqrt(n[0] ** 2 + n[1] ** 2 + n[2] ** 2) for n in norms]
    bad_len = sum(1 for L in lens if abs(L - 1.0) > 0.01)
    print(f"  length: min {min(lens):.4f} max {max(lens):.4f}, {bad_len} not unit")

    if mode != "ByVertice" or len(norms) != len(verts):
        print("  (agreement test needs ByVertice normals, one per vertex)")
        return

    agree, flipped, total = 0, 0, 0
    worst = 1.0
    for f in faces:
        n = [0.0, 0.0, 0.0]
        for i in range(len(f)):
            a, b = verts[f[i]], verts[f[(i + 1) % len(f)]]
            n[0] += (a[1] - b[1]) * (a[2] + b[2])
            n[1] += (a[2] - b[2]) * (a[0] + b[0])
            n[2] += (a[0] - b[0]) * (a[1] + b[1])
        m = math.sqrt(sum(c * c for c in n))
        if m < 1e-20:
            continue
        n = [c / m for c in n]

        s = [0.0, 0.0, 0.0]
        for i in f:
            s = [s[k] + norms[i][k] for k in range(3)]
        sm = math.sqrt(sum(c * c for c in s))
        if sm < 1e-20:
            continue
        s = [c / sm for c in s]

        d = sum(n[k] * s[k] for k in range(3))
        total += 1
        worst = min(worst, d)
        if d > 0:
            agree += 1
        else:
            flipped += 1

    print(f"  face winding vs stored normals: {agree} agree, {flipped} OPPOSED "
          f"({100.0 * flipped / max(total, 1):.2f} %), worst dot {worst:+.3f}")
    if flipped > total * 0.5:
        print("  >> INSIDE OUT: the stored normals oppose the winding for most of the mesh.")
        print("     Under a smooth shader every bulge reads as a hollow.")
    elif flipped:
        print("  >> some faces disagree with their own normals; those shade as local dents.")
    print()


if __name__ == "__main__":
    a = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    for p in a:
        main(p)
