"""assembly_math: transforms, face filters, stud seating, plan following (no SolidWorks)."""

import json
import math
import os
import sys

import pytest

_ADAPTER_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _ADAPTER_DIR)
import assembly_math as am

# 90 deg about Z (row vectors: x -> y), then translated.
ROT_Z = [0.0, 1.0, 0.0, -1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.1, 0.2, 0.3, 1.0]


def close(a, b, tol=1e-9):
    return all(abs(x - y) <= tol for x, y in zip(a, b))


def test_transforms_compose_and_invert():
    assert close(am.xf_point(ROT_Z, [1, 0, 0]), [0.1, 1.2, 0.3])
    assert close(am.xf_mul(ROT_Z, am.xf_inv(ROT_Z)), am.IDENTITY)
    parent = [1, 0, 0, 0, 1, 0, 0, 0, 1, 1.0, 0, 0, 1]
    local = am.xf_mul(
        ROT_Z, am.xf_inv(parent)
    )  # the child's pose in the parent's frame
    assert close(am.xf_mul(local, parent), ROT_Z)
    assert close(local[9:12], [-0.9, 0.2, 0.3])


def test_target_world_variants():
    cur = list(ROT_Z)
    assert am.target_world(cur, [1, 2, 3], None)[9:12] == [1, 2, 3]
    assert am.target_world(cur, [1, 2, 3], None)[:9] == cur[:9]
    assert close(am.target_world(cur, None, [0.001, 0, 0])[9:12], [0.101, 0.2, 0.3])
    with pytest.raises(ValueError):
        am.target_world(cur, [1, 2], None)


def _stud_device():
    # A stud of radius 4 mm along +Z at (0.05, 0, z 0..0.02), terminal top face at z 0, another
    # terminal face (same height) 60 mm away, and a lower face at z -0.003.
    return [
        {
            "i": 1,
            "kind": "cylinder",
            "origin": [0.05, 0, 0.01],
            "axis": [0, 0, -1],
            "radius": 0.004,
        },
        {
            "i": 2,
            "planar": True,
            "normal": [0, 0, 1],
            "point": [0.05, 0.002, 0.0],
            "area": 4e-4,
        },
        {
            "i": 3,
            "planar": True,
            "normal": [0, 0, 1],
            "point": [0.11, 0, 0.0],
            "area": 4e-4,
        },
        {
            "i": 4,
            "planar": True,
            "normal": [0, 0, 1],
            "point": [0.05, 0, -0.003],
            "area": 9e-4,
        },
        {
            "i": 5,
            "planar": True,
            "normal": [0, 0, -1],
            "point": [0.05, 0, -0.01],
            "area": 9e-4,
        },
    ]


def _lug(x, y, z):
    # Ring lug: ring faces 1.5 mm apart, hole radius 4.2 mm, a crimp barrel along X.
    return [
        {
            "i": 0,
            "planar": True,
            "normal": [0, 0, 1],
            "point": [x + 0.006, y, z + 0.0015],
            "area": 2e-4,
        },
        {
            "i": 1,
            "planar": True,
            "normal": [0, 0, -1],
            "point": [x + 0.006, y, z],
            "area": 2e-4,
        },
        {
            "i": 2,
            "kind": "cylinder",
            "origin": [x, y, z],
            "axis": [0, 0, 1],
            "radius": 0.0042,
        },
        {
            "i": 3,
            "kind": "cylinder",
            "origin": [x + 0.02, y, z],
            "axis": [1, 0, 0],
            "radius": 0.003,
        },
    ]


def _seat(lug, dev, **kw):
    return am.seat_on_stud(lug, dev, "Stud-1", dev, "Stud-1", **kw)


def test_seat_on_stud_moves_hole_onto_stud_and_face():
    lug = _lug(0.0515, 0.001, 0.0018)
    res = _seat(lug, _stud_device())
    assert close(res["delta_mm"], [-1.5, -1.0, -1.8], 1e-3)
    assert res["face"]["i"] == 2 and res["contact_face"]["i"] == 1
    assert res["stud"]["radius_mm"] == 4.0 and res["hole"]["radius_mm"] == 4.2
    assert {f["i"] for f in res["other_faces"]} == {4}  # face 3 is another terminal's
    assert abs(_seat(lug, _stud_device(), gap=0.001)["delta_mm"][2] - (-0.8)) < 1e-6
    assert abs(_seat(lug, _stud_device(), face_index=4)["delta_mm"][2] - (-4.8)) < 1e-6
    with pytest.raises(ValueError, match="no stud"):
        _seat(lug, _stud_device(), stud_radius_min=0.005)


def test_seat_on_stud_takes_each_face_with_its_own_side():
    # A nut underside 0.3 mm above the lug's top face, the terminal face 2 mm under the lug:
    # the nearest face wins, and the lug face that looks at it (the top face) is the contact.
    dev = _stud_device() + [
        {
            "i": 6,
            "planar": True,
            "normal": [0, 0, -1],
            "point": [0.05, 0, 0.0036],
            "area": 1e-4,
        }
    ]
    lug = _lug(0.05, 0.0, 0.002)
    res = _seat(lug, dev)
    assert res["face"]["i"] == 6 and res["contact_face"]["i"] == 0
    assert close(res["delta_mm"], [0.0, 0.0, 0.1], 1e-6)
    sides = {f["i"]: f["side"] for f in res["other_faces"]}
    assert sides[2] == "-axis" and res["face"]["normal"] == [0.0, 0.0, -1.0]  # stud axis is -Z
    below = _seat(lug, dev, face_index=2)
    assert below["contact_face"]["i"] == 1 and close(
        below["delta_mm"], [0, 0, -2.0], 1e-6
    )


def test_filter_faces_kind_axis_near_radius():
    faces = _stud_device()
    planes_up = am.filter_faces(faces, kind="plane", axis=[0, 0, 1])
    assert [f["i"] for f in planes_up] == [2, 3, 4]
    near = am.filter_faces(faces, near=[0.05, 0, 0.001], within=0.005)
    assert [f["i"] for f in near] == [2, 4] and near[0]["plane_distance_mm"] == 1.0
    cyl = am.filter_faces(
        faces, axis=[0, 0, 1], radius_min=0.003, radius_max=0.005, near=[0.06, 0, 0.01]
    )
    assert [f["i"] for f in cyl] == [1] and cyl[0]["axis_distance_mm"] == 10.0
    world = am.faces_to_world(faces, ROT_Z)
    assert close(world[0]["origin"], [0.1, 0.25, 0.31], 1e-7)


def test_follow_plan_moves_free_ends_and_leads_only():
    plan = {
        "fan_out": [{"connector": "Plug-1"}],
        "paths": [
            {"name": "B1", "points": [[0, 0, 0], [0.01, 0, 0], [0.05, 0, 0]]},
            {
                "name": "B2",
                "points": [
                    [0.05, 0, 0],
                    [0.06, 0.01, 0],
                    [0.07, 0.02, 0],
                    [0.08, 0.03, 0],
                ],
            },
            {"name": "B3", "points": [[0.05, 0, 0], [0.05, -0.02, 0]]},
        ],
    }
    cands = [
        ("fan Plug-1", [0.0, 0.002, 0.0]),
        ("J1.1", [0.081, 0.03, 0.0]),
        ("J2.1", [0.5, 0, 0]),
    ]
    out, moves = am.follow_plan(plan, cands, {"B2.end": 2}, max_move=0.02)
    b1, b2, b3 = out["paths"]
    assert b1["points"][0] == [0.0, 0.002, 0.0] and close(
        b1["points"][1], [0.01, 0.002, 0.0]
    )
    assert b1["points"][2] == [0.05, 0, 0]  # breakout stays
    assert b2["points"][3] == [0.081, 0.03, 0.0]
    assert close(b2["points"][2], [0.071, 0.02, 0.0]) and close(
        b2["points"][1], [0.061, 0.01, 0.0]
    )
    skipped = [m for m in moves if m.get("skipped")]
    assert [m["path"] for m in skipped] == ["B3"]  # nothing within 20 mm
    assert b3["points"][1] == [0.05, -0.02, 0]
    assert plan["paths"][0]["points"][0] == [0, 0, 0]  # input untouched
    json.dumps(out)


def test_group_interferences():
    items = [
        {"components": ["A", "B"], "volume_mm3": 2.0},
        {"components": ["B", "A"], "volume_mm3": 3.0},
        {"components": ["A", "C"], "volume_mm3": 4.0},
    ]
    g = am.group_interferences(items, 10)
    assert g[0] == {
        "components": ["A", "B"],
        "count": 2,
        "total_mm3": 5.0,
        "max_mm3": 3.0,
    }
    assert len(am.group_interferences(items, 1)) == 1
    assert math.isclose(sum(x["total_mm3"] for x in g), 9.0)


def test_hole_centre_is_the_ring_hole():
    lug = _lug(0.05, 0.002, 0.003)
    assert am.hole_centre(lug) == [0.05, 0.002, 0.003]  # not the barrel along X
    assert am.hole_centre([lug[3]]) is None
