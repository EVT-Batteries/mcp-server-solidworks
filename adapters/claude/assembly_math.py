"""Pure helpers for the adapter-side composite tools (no SolidWorks, no HTTP).

Transforms are SOLIDWORKS' 13-number component transforms: a 3x3 rotation (row-major) +
translation (m) + scale, applied to row vectors: world = p . R + t.
"""

import json
import math
import os

IDENTITY = [1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0]


# ---------------------------------------------------------------------------
# Vectors and transforms
# ---------------------------------------------------------------------------
def dot(a, b) -> float:
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def sub(a, b) -> list:
    return [a[0] - b[0], a[1] - b[1], a[2] - b[2]]


def add(a, b) -> list:
    return [a[0] + b[0], a[1] + b[1], a[2] + b[2]]


def scale(a, s: float) -> list:
    return [a[0] * s, a[1] * s, a[2] * s]


def norm(a) -> float:
    return math.sqrt(dot(a, a))


def unit(a) -> list:
    n = norm(a)
    return [a[0] / n, a[1] / n, a[2] / n] if n > 0 else [0.0, 0.0, 0.0]


def xf_point(t, p) -> list:
    return [
        p[0] * t[i] + p[1] * t[3 + i] + p[2] * t[6 + i] + t[9 + i] for i in range(3)
    ]


def xf_dir(t, d) -> list:
    return [d[0] * t[i] + d[1] * t[3 + i] + d[2] * t[6 + i] for i in range(3)]


def xf_mul(a, b) -> list:
    """The transform applying a, then b (row vectors: p . A . B)."""
    r = [
        sum(a[3 * i + k] * b[3 * k + j] for k in range(3))
        for i in range(3)
        for j in range(3)
    ]
    return r + xf_point(b, a[9:12]) + [1.0]


def xf_inv(t) -> list:
    rt = [t[0], t[3], t[6], t[1], t[4], t[7], t[2], t[5], t[8]]
    tr = [-sum(t[9 + k] * rt[3 * k + i] for k in range(3)) for i in range(3)]
    return rt + tr + [1.0]


def parse_numbers(text: str, name: str) -> list | None:
    """A JSON array of numbers, or None for an empty string."""
    if not text or not text.strip():
        return None
    try:
        v = json.loads(text)
    except json.JSONDecodeError as exc:
        raise ValueError(f"{name}: not JSON ({exc})") from exc
    if not isinstance(v, list) or not all(isinstance(x, (int, float)) for x in v):
        raise ValueError(f"{name}: expected a JSON array of numbers")
    return [float(x) for x in v]


def target_world(
    current, world_transform: list | None, world_offset: list | None
) -> list:
    """The requested world transform: 13 numbers as given, 3 numbers = translation with the
    current rotation, world_offset = current moved by that vector (m)."""
    if world_transform is not None:
        if len(world_transform) == 13:
            target = list(world_transform)
        elif len(world_transform) == 3:
            target = list(current[:9]) + list(world_transform) + [1.0]
        else:
            raise ValueError(
                "world_transform: 13 numbers (rotation + translation + scale) or 3 (translation)"
            )
    else:
        target = list(current)
    if world_offset is not None:
        if len(world_offset) != 3:
            raise ValueError("world_offset: 3 numbers (m)")
        target = target[:9] + add(target[9:12], world_offset) + [1.0]
    return target


def max_dev_mm(a, b) -> float:
    """Largest difference between two transforms: translation in mm, rotation entries x 1000."""
    return round(max(abs(a[i] - b[i]) for i in range(12)) * 1000.0, 4)


# ---------------------------------------------------------------------------
# Faces (analyze_assembly faces payload)
# ---------------------------------------------------------------------------
def face_kind(f: dict) -> str:
    if f.get("planar"):
        return "plane"
    return f.get("kind") or "other"


def faces_to_world(faces: list, t) -> list:
    """Copies of the faces with point / origin / normal / axis mapped by t."""
    out = []
    for f in faces:
        g = dict(f)
        for k in ("point", "origin"):
            if isinstance(f.get(k), list):
                g[k] = [round(v, 7) for v in xf_point(t, f[k])]
        for k in ("normal", "axis"):
            if isinstance(f.get(k), list):
                g[k] = [round(v, 7) for v in unit(xf_dir(t, f[k]))]
        out.append(g)
    return out


def filter_faces(
    faces: list,
    kind: str = "",
    near=None,
    within: float = 0.0,
    axis=None,
    axis_tol_deg: float = 2.0,
    radius_min: float = 0.0,
    radius_max: float = 0.0,
) -> list:
    """Faces matching every given filter (all in one frame, m). axis: a cylinder's axis parallel
    either way, a plane's normal pointing along it. near: within `within` of the face's point;
    adds distance_mm (and axis_distance_mm for cylinders, plane_distance_mm for planes), sorted
    nearest first."""
    cos_tol = math.cos(math.radians(axis_tol_deg))
    a = unit(axis) if axis else None
    out = []
    for f in faces:
        k = face_kind(f)
        if kind and k != kind:
            continue
        r = f.get("radius")
        if (radius_min or radius_max) and (
            r is None or r < radius_min or (radius_max and r > radius_max)
        ):
            continue
        if a is not None:
            if k == "plane" and isinstance(f.get("normal"), list):
                if dot(unit(f["normal"]), a) < cos_tol:
                    continue
            elif k == "cylinder" and isinstance(f.get("axis"), list):
                if abs(dot(unit(f["axis"]), a)) < cos_tol:
                    continue
            else:
                continue
        g = dict(f)
        if near is not None:
            p = f.get("point") or f.get("origin")
            if not isinstance(p, list):
                continue
            d = norm(sub(p, near))
            if within and d > within:
                continue
            g["distance_mm"] = round(d * 1000.0, 3)
            if (
                k == "cylinder"
                and isinstance(f.get("axis"), list)
                and isinstance(f.get("origin"), list)
            ):
                g["axis_distance_mm"] = round(
                    line_distance(near, f["origin"], f["axis"]) * 1000.0, 3
                )
            if (
                k == "plane"
                and isinstance(f.get("normal"), list)
                and isinstance(f.get("point"), list)
            ):
                g["plane_distance_mm"] = round(
                    dot(sub(near, f["point"]), unit(f["normal"])) * 1000.0, 3
                )
        out.append(g)
    if near is not None:
        out.sort(key=lambda g: g["distance_mm"])
    return out


def line_distance(p, origin, direction) -> float:
    """Distance from p to the infinite line through origin along direction."""
    v = sub(p, origin)
    d = unit(direction)
    return norm(sub(v, scale(d, dot(v, d))))


def seat_on_stud(
    lug_faces: list,
    stud_faces: list,
    stud_component: str,
    face_faces: list,
    face_component: str,
    stud_radius_min: float = 0.0,
    stud_radius_max: float = 0.0,
    face_index: int = -1,
    gap: float = 0.0,
    search: float = 0.015,
    parallel_tol_deg: float = 3.0,
    face_reach: float = 0.04,
) -> dict:
    """Where a ring terminal (lug) has to move to sit on a stud: its hole coaxial with the stud and
    its contact face on the terminal face. All faces in WORLD coordinates (m). Rotation is kept:
    the hole axis must already be parallel to the stud (within parallel_tol_deg). Terminal face
    candidates: planes perpendicular to the stud, facing the lug, whose mid point lies within
    face_reach of the stud axis; the one nearest the lug's contact face (within search) is used.
    Returns {delta (m), stud, hole, face, contact_face, other_faces, ...} or raises ValueError."""
    cos_tol = math.cos(math.radians(parallel_tol_deg))
    planes = [
        f for f in lug_faces if f.get("planar") and isinstance(f.get("normal"), list)
    ]
    if not planes:
        raise ValueError("the component has no planar face (not a ring terminal?)")
    ring_n = unit(max(planes, key=lambda f: f.get("area") or 0)["normal"])
    holes = [
        f
        for f in lug_faces
        if f.get("kind") == "cylinder"
        and isinstance(f.get("axis"), list)
        and abs(dot(unit(f["axis"]), ring_n)) >= cos_tol
    ]
    if not holes:
        raise ValueError(
            "no cylindrical face perpendicular to the component's largest flat face (its hole)"
        )
    best = None
    for s in stud_faces:
        if s.get("kind") != "cylinder" or not isinstance(s.get("axis"), list):
            continue
        r = s.get("radius") or 0
        if stud_radius_min and r < stud_radius_min:
            continue
        if stud_radius_max and r > stud_radius_max:
            continue
        sa = unit(s["axis"])
        if abs(dot(sa, ring_n)) < cos_tol:
            continue
        for h in holes:
            hr = h.get("radius") or 0
            if hr < r or hr > 2.0 * r:
                continue
            d = line_distance(h["origin"], s["origin"], sa)
            if d > search:
                continue
            key = (round(d, 4), hr - r)
            if best is None or key < best[0]:
                best = (key, s, h)
    if best is None:
        raise ValueError(
            f"no stud (cylinder parallel to the hole, radius below the hole's, axis within {search * 1000:.1f} mm) "
            f"on {stud_component}; set stud_radius_min / max or search"
        )
    _, stud, hole = best
    a = unit(stud["axis"])
    # Terminal faces: planes perpendicular to the stud, facing the lug, near the stud axis. Each
    # is paired with the lug face looking back at it (normal opposite), the one furthest toward it.
    ring = [f for f in planes if abs(dot(unit(f["normal"]), a)) >= cos_tol]
    h_mid = sum(dot(f["point"], a) for f in ring) / len(ring)
    cands = []
    for f in face_faces:
        if (
            not f.get("planar")
            or not isinstance(f.get("normal"), list)
            or not isinstance(f.get("point"), list)
        ):
            continue
        n = unit(f["normal"])
        if abs(dot(n, a)) < cos_tol:
            continue
        if dot(n, a) * (h_mid - dot(f["point"], a)) <= 0:
            continue  # faces away from the lug
        axis_d = line_distance(f["point"], stud["origin"], a)
        if face_index < 0 and axis_d > face_reach:
            continue  # another terminal's face (its point = mid of the face)
        back = [g for g in ring if dot(unit(g["normal"]), n) <= -cos_tol]
        if not back:
            continue
        contact_f = min(back, key=lambda g: dot(g["point"], n))
        move = dot(f["point"], n) - dot(contact_f["point"], n)
        cands.append(
            {"f": f, "n": n, "contact": contact_f, "move": move, "axis_d": axis_d}
        )
    if not cands:
        raise ValueError(
            f"no planar face of {face_component} perpendicular to the stud and facing the component"
        )
    listed = [
        {
            "i": c["f"].get("i"),
            "side": "+axis" if dot(c["n"], a) > 0 else "-axis",
            "move_mm": round(c["move"] * 1000.0, 3),
            "area_mm2": round((c["f"].get("area") or 0) * 1e6, 2),
            "axis_distance_mm": round(c["axis_d"] * 1000.0, 3),
        }
        for c in cands
    ]
    if face_index >= 0:
        chosen = next((c for c in cands if c["f"].get("i") == face_index), None)
        if chosen is None:
            raise ValueError(
                f"face {face_index} of {face_component} is not a candidate terminal face; candidates: {listed}"
            )
    else:
        near_ones = [c for c in cands if abs(c["move"]) <= search]
        if not near_ones:
            raise ValueError(
                f"no terminal face within {search * 1000:.1f} mm of the component's contact face; candidates: {listed}"
            )
        chosen = min(near_ones, key=lambda c: (round(abs(c["move"]), 5), c["axis_d"]))
    n = chosen["n"]
    v = sub(stud["origin"], hole["origin"])
    v_perp = sub(v, scale(a, dot(v, a)))
    delta = add(v_perp, scale(n, chosen["move"] + gap))
    return {
        "delta": [round(x, 7) for x in delta],
        "delta_mm": [round(x * 1000.0, 3) for x in delta],
        "move_mm": round(norm(delta) * 1000.0, 3),
        "stud": {
            "component": stud_component,
            "i": stud.get("i"),
            "radius_mm": round(stud["radius"] * 1000.0, 3),
            "axis": [round(x, 6) for x in a],
            "axis_point_mm": [round(x * 1000.0, 3) for x in stud["origin"]],
        },
        "hole": {
            "i": hole.get("i"),
            "radius_mm": round(hole["radius"] * 1000.0, 3),
            "axis_offset_mm": round(norm(v_perp) * 1000.0, 3),
        },
        "face": {
            "component": face_component,
            "i": chosen["f"].get("i"),
            "move_mm": round(chosen["move"] * 1000.0, 3),
            "area_mm2": round((chosen["f"].get("area") or 0) * 1e6, 2),
            "axis_distance_mm": round(chosen["axis_d"] * 1000.0, 3),
            "normal": [round(x, 6) for x in n],
        },
        "contact_face": {
            "i": chosen["contact"].get("i"),
            "area_mm2": round((chosen["contact"].get("area") or 0) * 1e6, 2),
        },
        "gap_mm": round(gap * 1000.0, 3),
        "other_faces": sorted(
            [c for c in listed if c["i"] != chosen["f"].get("i")],
            key=lambda c: abs(c["move_mm"]),
        )[:8],
    }


# ---------------------------------------------------------------------------
# Route plans (route_follow)
# ---------------------------------------------------------------------------
def load_json_arg(text: str, name: str):
    """A JSON value given inline or as a path to a .json file."""
    s = (text or "").strip()
    if not s:
        return None
    if s[0] in "[{":
        return json.loads(s)
    if not os.path.isfile(s):
        raise ValueError(f"{name}: neither JSON nor an existing file: {s}")
    with open(s, encoding="utf-8") as fh:
        return json.load(fh)


def _key(p, digits: int = 5) -> tuple:
    return tuple(round(v, digits) for v in p)


def follow_plan(
    plan: dict,
    candidates: list,
    lead_points: dict | None = None,
    max_move: float = 0.02,
    ambiguity: float = 0.6,
) -> tuple[dict, list]:
    """Move each FREE end of the plan's paths onto its nearest candidate (a connector stub end or a
    fan join, [(label, [x,y,z] m)]), and its lead point(s) by the same vector.
    A free end is a path end that coincides (10 um) with no point of another path: breakouts and
    junction nodes stay. lead_points: {"<path>.start" | "<path>.end": n} moves n interior points
    next to that end (default 1, never the other end). Ends with no candidate within max_move stay
    and are reported. Returns (new plan, moves)."""
    lead_points = lead_points or {}
    paths = plan.get("paths") or []
    owners: dict = {}
    for pi, p in enumerate(paths):
        for q in p.get("points") or []:
            owners.setdefault(_key(q), set()).add(pi)
    new_paths, moves = [], []
    for pi, p in enumerate(paths):
        q = json.loads(json.dumps(p))
        pts = q.get("points") or []
        name = q.get("name") or f"path{pi}"
        for end, k in (("start", 0), ("end", len(pts) - 1)):
            if len(pts) < 2:
                break
            pt = pts[k]
            if owners.get(_key(pt), set()) - {pi}:
                continue  # shared with another path: a node
            ranked = sorted(candidates, key=lambda c: norm(sub(c[1], pt)))
            if not ranked:
                break
            label, target = ranked[0]
            d = norm(sub(target, pt))
            d2 = norm(sub(ranked[1][1], pt)) if len(ranked) > 1 else float("inf")
            mv = {
                "path": name,
                "end": end,
                "to": label,
                "move_mm": round(d * 1000.0, 3),
                "next_mm": None if d2 == float("inf") else round(d2 * 1000.0, 3),
            }
            if d > max_move:
                mv["skipped"] = (
                    f"nearest candidate beyond max_move ({max_move * 1000:.1f} mm)"
                )
                moves.append(mv)
                continue
            if d2 != float("inf") and d >= ambiguity * d2 and d > 1e-6:
                mv["ambiguous"] = True
            dv = sub(target, pt)
            pts[k] = [round(v, 7) for v in target]
            n = int(lead_points.get(f"{name}.{end}", 1))
            n = max(0, min(n, len(pts) - 2))
            for m in range(1, n + 1):
                idx = k + m if k == 0 else k - m
                pts[idx] = [round(v, 7) for v in add(pts[idx], dv)]
            if n:
                mv["lead_points"] = n
            moves.append(mv)
        new_paths.append(q)
    out = dict(plan)
    out["paths"] = new_paths
    return out, moves


def group_interferences(items: list, limit: int) -> list:
    """Interferences grouped by component pair: count, total and max volume, largest total first."""
    groups: dict = {}
    for it in items:
        comps = tuple(sorted(it.get("components") or []))
        v = float(it.get("volume_mm3") or 0.0)
        g = groups.setdefault(
            comps,
            {"components": list(comps), "count": 0, "total_mm3": 0.0, "max_mm3": 0.0},
        )
        g["count"] += 1
        g["total_mm3"] += v
        g["max_mm3"] = max(g["max_mm3"], v)
    out = sorted(groups.values(), key=lambda g: -g["total_mm3"])
    for g in out:
        g["total_mm3"] = round(g["total_mm3"], 3)
        g["max_mm3"] = round(g["max_mm3"], 3)
    return out[:limit]
