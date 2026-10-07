# Convert an ASCII FBX written by FbxWriter into OBJ, preserving quads.
#
# WHY THIS EXISTS AND WHAT IT SAYS ABOUT THE EXPORT. export-competitor.py routes the competitor's
# FBX through Blender, which is the only thing in reach that keeps quad topology. It cannot do the
# same for ours: Blender's FBX importer refuses ASCII outright - "ASCII FBX files are not
# supported" - and FbxWriter writes ASCII. So our own export cannot be opened by the tool the
# comparison runs in, which is a defect in the export and not in the measurement.
#
# This reads it directly instead, so the shipped artefact can be scored rather than a re-export of
# it. Keep it even after FbxWriter learns to write binary: measuring the file the user actually
# receives, rather than a regenerated stand-in, is the point.
#
# Only what the writer emits is parsed - Vertices, PolygonVertexIndex, and the unit scale. The last
# polygon index of each face is bitwise-negated in FBX (-i-1), which is how face boundaries are
# marked and is what preserves quads.
#
#   python fbx2obj.py in.fbx out.obj

import sys, re


def block(text, key):
    """The comma-separated payload of `key: *N { a: ... }`, as one string."""
    m = re.search(re.escape(key) + r"\s*:\s*\*\d+\s*\{\s*a:\s*", text)
    if not m:
        return None
    i = m.end()
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
    return "".join(out)


def main(src, dst):
    text = open(src, "r", errors="ignore").read()

    raw = block(text, "Vertices")
    idx = block(text, "PolygonVertexIndex")
    if raw is None or idx is None:
        raise SystemExit("not an ASCII FBX with Vertices/PolygonVertexIndex")

    coords = [float(x) for x in raw.replace("\n", "").split(",") if x.strip()]
    indices = [int(x) for x in idx.replace("\n", "").split(",") if x.strip()]

    scale = 1.0
    m = re.search(r'"UnitScaleFactor"[^,]*,[^,]*,[^,]*,[^,]*,\s*([0-9.eE+-]+)', text)
    if m:
        try:
            scale = float(m.group(1))
        except ValueError:
            pass

    verts = [(coords[i], coords[i + 1], coords[i + 2]) for i in range(0, len(coords), 3)]

    faces, cur = [], []
    for v in indices:
        if v < 0:
            cur.append(-v - 1)
            faces.append(cur)
            cur = []
        else:
            cur.append(v)

    with open(dst, "w") as fh:
        fh.write(f"# from {src}  (UnitScaleFactor {scale})\n")
        # X is written UNCHANGED, and getting this wrong once already produced a false diagnosis.
        # FbxWriter negates X for FBX's handedness, and the scan this is scored against came out of
        # Unity through an export that negates it too, so the two cancel and the FBX lands in the
        # same space as ref_high.obj. "Undoing the writer" here mirrors the mesh instead - and on a
        # near-symmetric figure a mirror still puts most vertices near the surface, so it does not
        # look like an axis bug. It looks like 215 vertices dented into the shoulders and the chest,
        # which is precisely the defect that was being hunted at the time. Verify a converter
        # against the reference mesh, never against the writer's source.
        for p in verts:
            fh.write(f"v {p[0]:.6f} {p[1]:.6f} {p[2]:.6f}\n")
        for f in faces:
            fh.write("f " + " ".join(str(i + 1) for i in reversed(f)) + "\n")

    sizes = {}
    for f in faces:
        sizes[len(f)] = sizes.get(len(f), 0) + 1
    print(f"{dst}: {len(verts)} verts, {len(faces)} faces, sizes {sizes}")


if __name__ == "__main__":
    a = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    main(a[0], a[1])
