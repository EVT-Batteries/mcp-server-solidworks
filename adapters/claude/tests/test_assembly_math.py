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
    assert sides[2] == "-axis" and res["face"]["normal"] == [
        0.0,
        0.0,
        -1.0,
    ]  # stud axis is -Z
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


# A small plan (mm): trunk T from a fan join, branches A and B, a single-wire branch S after B,
# and an isolated path D. Plug-1 (REF P1) is fanned out at the trunk start.
PLAN_MM = [
    {"name": "T", "points": [[0, 0, 0], [50, 0, 0], [100, 0, 0]]},
    {"name": "A", "points": [[100, 0, 0], [150, 50, 0]]},
    {"name": "B", "points": [[100, 0, 0], [150, -50, 0]]},
    {"name": "S", "points": [[150, -50, 0], [200, -50, 0]]},
    {"name": "D", "points": [[300, 0, 0], [400, 0, 0]]},
]
STUBS = [
    {"end": f"P1.{k}", "component": "Sub-1/Plug-1", "stub_end_mm": [-20, k, 0]}
    for k in (1, 2, 3, 4)
] + [
    {"end": "J1.1", "component": "Sub-1/Rec-1", "stub_end_mm": [150.5, 50, 0]},
    {"end": "J1.2", "component": "Sub-1/Rec-1", "stub_end_mm": [150, 50.4, 0]},
    {"end": "J2.1", "component": "Rec-2", "stub_end_mm": [150, -50.3, 0]},
    {"end": "J2.2", "component": "Rec-2", "stub_end_mm": [150, -50, 0]},
    {"end": "J3.1", "component": "Rec-3", "stub_end_mm": [200, -50, 0]},
    {"end": "K1.1", "component": "Rec-4", "stub_end_mm": [300, 0, 0]},
]
FANS = [
    {"connector": "Plug-1", "pins": ["1", "2", "3", "4"], "join_mm": [0.3, 0, 0]},
    {"connector": "Plug-9", "pins": ["1"], "join_mm": [9, 9, 9]},
]
WIRES = [
    {"name": "W1", "from": "P1.1", "to": "J1.1", "diameter_mm": 1.6},
    {"name": "W2", "from": "J1.2", "to": "P1.2", "diameter_mm": 1.6},
    {"name": "W3", "from": "P1.3", "to": "J2.1", "diameter_mm": 1.6},
    {"name": "W4", "from": "J2.2", "to": "J3.1", "diameter_mm": 1.8},
    {"name": "W5", "from": "P1.4", "to": "X9.1", "diameter_mm": 1.6},  # one end unknown
    {"name": "W6", "from": "Q1.1", "to": "Q2.1", "diameter_mm": 1.6},  # another stage
    {"name": "W7", "from": "K1.1", "to": "J3.1", "diameter_mm": 1.6},  # no chain
]
RULE_MM = [[2, 6.0], [3, 9.3]]


def test_wire_end_points_use_fan_joins_and_map_instances_to_refs():
    ends, problems = am.wire_end_points_mm(STUBS, FANS)
    assert ends["P1.3"] == [0.3, 0.0, 0.0]  # fan join replaces the stub end
    assert ends["J1.1"] == [150.5, 50.0, 0.0]
    assert [p["fan"] for p in problems] == ["Plug-9"]
    # a join given in metres only
    ends2, _ = am.wire_end_points_mm(
        STUBS, [{"connector": "sub-1/plug-1", "pins": ["2"], "join": [0.001, 0, 0]}]
    )
    assert ends2["P1.2"] == [1.0, 0.0, 0.0] and ends2["P1.1"] == [-20.0, 1.0, 0.0]


def test_size_paths_by_wire_sets_counts_and_sizes():
    ends, _ = am.wire_end_points_mm(STUBS, FANS)
    r = am.size_paths_by_wire_sets(PLAN_MM, WIRES, ends, RULE_MM, 5.0, 1.0)
    by = {p["name"]: p for p in r["paths"]}
    assert by["T"]["wires"] == ["W1", "W2", "W3"] and by["T"]["diameter_mm"] == 9.3
    assert by["T"]["sized_by"] == "covering"
    assert by["A"]["wire_count"] == 2 and by["A"]["diameter_mm"] == 6.0
    assert by["B"]["wires"] == ["W3"] and by["B"]["diameter_mm"] == 1.6
    assert by["B"]["sized_by"] == "wire"
    assert by["S"]["wires"] == ["W4"] and by["S"]["diameter_mm"] == 1.8
    assert by["D"]["wire_count"] == 0 and by["D"]["diameter_mm"] == 5.0
    assert by["D"]["sized_by"] == "default"
    assert r["placed"] == ["W1", "W2", "W3", "W4"]
    assert r["outside"] == ["W6"]  # no end on these paths: not an error
    probs = {p.get("wire") or p.get("path"): p["problem"] for p in r["problems"]}
    assert set(probs) == {"W5", "W7", "D"}
    assert "X9.1" in probs["W5"] and "no position" in probs["W5"]
    assert "no chain" in probs["W7"]
    json.dumps(r)


def test_size_paths_snap_and_stage_subset():
    ends, _ = am.wire_end_points_mm(STUBS, FANS)
    # J1.1 sits 0.5 mm off A's end: off the plan with a 0.4 mm snap (the join 0.3 mm: on)
    r = am.size_paths_by_wire_sets(PLAN_MM, WIRES, ends, RULE_MM, 5.0, 0.4)
    probs = {p.get("wire"): p["problem"] for p in r["problems"] if p.get("wire")}
    assert "nearest path end 0.5 mm" in probs["W1"]
    # a stage holding only S: W4 placed, the trunk wires have no end on it
    r = am.size_paths_by_wire_sets([PLAN_MM[3]], WIRES, ends, RULE_MM, 5.0, 1.0)
    assert r["placed"] == ["W4"]
    assert set(r["outside"]) == {"W1", "W2", "W5", "W6"}
    assert {p["wire"] for p in r["problems"]} == {
        "W3",
        "W7",
    }  # one end each on S's ends


def test_covering_diameter_rule_and_estimates():
    assert am.covering_diameter_mm(30, [], [[24, 11.5], [9, 9.3]], 6.0) == (
        11.5,
        "covering",
    )
    assert am.covering_diameter_mm(9, [], [[24, 11.5], [9, 9.3]], 6.0)[0] == 9.3
    d, how = am.covering_diameter_mm(4, [1.0] * 4, [[24, 11.5]], 6.0)
    assert how == "bundle_estimate" and math.isclose(d, 2.3)
    assert am.covering_diameter_mm(1, [1.4], [[2, 6.0]], 6.0) == (1.4, "wire")
    assert am.covering_diameter_mm(1, [0.0], [], 6.0) == (6.0, "default")
    assert am.covering_diameter_mm(0, [], [[0, 3.0]], 6.0) == (6.0, "default")


def test_batches():
    assert am.batches([1, 2, 3, 4, 5], 2) == [[1, 2], [3, 4], [5]]
    assert am.batches([], 3) == [[]]


def test_merge_clearance_results_global_indices_and_names():
    names = ["T", "A", "B", "S", "D"]
    b1 = {
        "assembly": "Top",
        "paths": 2,
        "path_length_mm": [100.0, 70.7],
        "neighbours_checked": 4,
        "interference_count": 1,
        "interferences": [{"component": "Box-1", "volume_mm3": 2.0, "path": 1}],
        "path_warnings": ["path 1: corner 0 drawn sharp"],
        "clear": False,
        "cleanup": ["probe part closed"],
    }
    b2 = {
        "paths": 2,
        "path_length_mm": [70.7, 50.0],
        "neighbours_checked": 6,
        "interference_count": 1,
        "interferences": [{"component": "Box-2", "volume_mm3": 5.0, "path": 0}],
        "ignored": 1,
        "ignored_hits": [{"component": "Rec-1", "volume_mm3": 0.1, "path": 1}],
        "clear": False,
    }
    b3 = {
        "paths": 1,
        "path_length_mm": [100.0],
        "interference_count": 0,
        "interferences": [],
        "unswept_paths": [{"path": 0, "error": "sweep failed"}],
        "clear": False,
    }
    m = am.merge_clearance_results([(0, b1), (2, b2), (4, b3)], names, 50)
    assert m["paths"] == 5 and m["interference_count"] == 2
    assert len(m["path_length_mm"]) == 5 and m["neighbours_checked"] == 6
    assert [
        (h["component"], h["path"], h["path_name"]) for h in m["interferences"]
    ] == [
        ("Box-2", 2, "B"),
        ("Box-1", 1, "A"),
    ]
    assert m["ignored"] == 1 and m["ignored_hits"][0]["path_name"] == "S"
    assert m["unswept_paths"] == [
        {"path": 4, "error": "sweep failed", "path_name": "D"}
    ]
    assert m["path_warnings"] == ["path 1 (A): corner 0 drawn sharp"]
    assert m["clear"] is False and [b["paths"] for b in m["batches"]] == [
        ["T", "A"],
        ["B", "S"],
        ["D"],
    ]
    clear = {"paths": 1, "interference_count": 0, "interferences": [], "clear": True}
    assert am.merge_clearance_results(
        [(0, dict(clear)), (1, dict(clear))], ["a", "b"], 5
    )["clear"]
