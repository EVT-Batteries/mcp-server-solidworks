"""Offline unit, geometry, and performance tests for the corridor planner and find_corridor."""

import json
import math
import os
import sys
import time

import pytest
from mcp.server.mcpserver.exceptions import ToolError

_ADAPTER_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _ADAPTER_DIR)
import corridor
import server


def _make_grid(
    dims, res=0.002, origin=(0.0, 0.0, 0.0), occupied_boxes=(), shell_boxes=()
):
    """Build a synthetic occupancy_grid dict from solid boxes and hollow shell boxes (voxel indices)."""
    nx, ny, nz = dims
    occ = bytearray(nx * ny * nz)

    def mark(ix, iy, iz):
        if 0 <= ix < nx and 0 <= iy < ny and 0 <= iz < nz:
            occ[ix + nx * (iy + ny * iz)] = 1

    for x0, y0, z0, x1, y1, z1 in occupied_boxes:
        for iz in range(max(0, z0), min(nz, z1 + 1)):
            for iy in range(max(0, y0), min(ny, y1 + 1)):
                for ix in range(max(0, x0), min(nx, x1 + 1)):
                    mark(ix, iy, iz)

    for x0, y0, z0, x1, y1, z1 in shell_boxes:
        for iz in range(max(0, z0), min(nz, z1 + 1)):
            for iy in range(max(0, y0), min(ny, y1 + 1)):
                for ix in range(max(0, x0), min(nx, x1 + 1)):
                    if ix in (x0, x1) or iy in (y0, y1) or iz in (z0, z1):
                        mark(ix, iy, iz)

    return {
        "frame": "world",
        "origin": list(origin),
        "resolution": res,
        "dims": [nx, ny, nz],
        "occupied_rle": corridor.encode_rle(occ),
        "occupied_count": occ.count(1),
    }


def _assert_filleted_ok(cand, bend_radius):
    pts = cand["points"]
    chk = corridor.fillet_check(pts, bend_radius)
    assert chk["ok"], f"fillet_check failed: {chk}"
    assert cand["min_straight_mm"] >= -1e-4


def test_fillet_check_and_tangents():
    # 90 deg corner: tangent = r * tan(45 deg) = r.
    pts = [[0.0, 0.0, 0.0], [0.02, 0.0, 0.0], [0.02, 0.02, 0.0]]
    assert corridor.fillet_check(pts, 0.015)["ok"] is True
    assert corridor.fillet_check(pts, 0.020)["ok"] is True
    chk_bad = corridor.fillet_check(pts, 0.025)
    assert chk_bad["ok"] is False
    assert chk_bad["worst_leg"] in (0, 1)

    # Collinear points: zero deflection and zero tangent.
    col = [[0.0, 0.0, 0.0], [0.01, 0.0, 0.0], [0.03, 0.0, 0.0]]
    tans = corridor.corner_tangents(col, 0.02)
    assert math.isclose(tans[0][0], 0.0, abs_tol=1e-9)
    assert math.isclose(tans[0][1], 0.0, abs_tol=1e-9)
    assert corridor.fillet_check(col, 0.02)["ok"] is True

    # 180 deg reversal: infinite tangent.
    rev = [[0.0, 0.0, 0.0], [0.02, 0.0, 0.0], [0.0, 0.0, 0.0]]
    tans_rev = corridor.corner_tangents(rev, 0.01)
    assert math.isinf(tans_rev[0][1])
    assert corridor.fillet_check(rev, 0.01)["ok"] is False


def test_straight_free_corridor():
    grid = _make_grid((50, 30, 30), res=0.002)
    res = corridor.plan_corridor(
        grid,
        start=[0.010, 0.030, 0.030],
        start_dir=[1.0, 0.0, 0.0],
        end=[0.090, 0.030, 0.030],
        end_dir=[1.0, 0.0, 0.0],
        diameter=0.006,
        bend_radius=0.015,
        clearance=0.002,
        max_candidates=1,
    )
    assert res["warnings"] == []
    assert len(res["candidates"]) == 1
    c = res["candidates"][0]
    assert c["corners"] == 0
    assert math.isclose(c["length_mm"], 80.0, abs_tol=1.0)
    assert "min_clearance_mm" in c
    _assert_filleted_ok(c, 0.015)


def test_l_shaped_wall_bend():
    # L-shaped wall blocking straight path and -y shortcut, forcing a +y bend around y=36.
    walls = [
        (30, 0, 0, 32, 35, 39),  # vertical wall from y=0..35
        (
            22,
            34,
            0,
            32,
            36,
            39,
        ),  # L-arm extending back toward start (leaving x=0..21 clear)
    ]
    grid = _make_grid((60, 60, 40), res=0.002, occupied_boxes=walls)
    res = corridor.plan_corridor(
        grid,
        start=[0.014, 0.040, 0.040],
        start_dir=[1.0, 0.0, 0.0],
        end=[0.106, 0.040, 0.040],
        end_dir=[1.0, 0.0, 0.0],
        diameter=0.006,
        bend_radius=0.010,
        clearance=0.002,
    )
    assert len(res["candidates"]) >= 1
    c = res["candidates"][0]
    assert c["corners"] >= 1
    assert c["min_clearance_mm"] >= 0.0
    _assert_filleted_ok(c, 0.010)
    # Every sample must clear the wall region (y > 36 voxels = 0.072 m when x in 30..32)
    for x, y, _z in c["points"]:
        assert not (0.056 < x < 0.068 and y < 0.074), f"point inside wall: {(x, y)}"


def test_u_turn_wide_and_narrow():
    # Baffle wall at y=20..22 from x=0..44 leaves open turn space on x=45..74 (60 mm).
    baffle = [(0, 20, 0, 44, 22, 29)]
    # Wide channels: y=6..19 (28 mm) and y=23..44 (44 mm), total axis-to-axis separation 44 mm.
    grid_wide = _make_grid((75, 50, 30), res=0.002, occupied_boxes=baffle)
    res_wide = corridor.plan_corridor(
        grid_wide,
        start=[0.020, 0.020, 0.030],
        start_dir=[1.0, 0.0, 0.0],
        end=[0.020, 0.072, 0.030],
        end_dir=[-1.0, 0.0, 0.0],
        diameter=0.006,
        bend_radius=0.012,
        clearance=0.002,
    )
    assert len(res_wide["candidates"]) >= 1
    _assert_filleted_ok(res_wide["candidates"][0], 0.012)

    # Narrow slot: outer walls squeeze both channels so the U-turn leg cannot fit 2*r = 40 mm.
    narrow_walls = [
        (0, 0, 0, 74, 15, 29),
        (0, 20, 0, 50, 22, 29),
        (0, 28, 0, 74, 49, 29),
    ]
    grid_narrow = _make_grid((75, 50, 30), res=0.002, occupied_boxes=narrow_walls)
    res_narrow = corridor.plan_corridor(
        grid_narrow,
        start=[0.020, 0.035, 0.030],
        start_dir=[1.0, 0.0, 0.0],
        end=[0.020, 0.050, 0.030],
        end_dir=[-1.0, 0.0, 0.0],
        diameter=0.006,
        bend_radius=0.020,
        clearance=0.002,
    )
    assert res_narrow["candidates"] == []


def test_closed_solid_shell_not_entered():
    # Hollow box shell occupying x=22..36, y=16..34, z=10..30 (interior is empty in RLE!)
    shell = [(22, 16, 10, 36, 34, 30)]
    grid = _make_grid((60, 50, 40), res=0.002, shell_boxes=shell)
    res = corridor.plan_corridor(
        grid,
        start=[0.016, 0.050, 0.040],
        start_dir=[1.0, 0.0, 0.0],
        end=[0.104, 0.050, 0.040],
        end_dir=[1.0, 0.0, 0.0],
        diameter=0.006,
        bend_radius=0.010,
        clearance=0.002,
    )
    assert len(res["candidates"]) >= 1
    c = res["candidates"][0]
    _assert_filleted_ok(c, 0.010)
    # Confirm no point lies inside the hollow box [0.044..0.072, 0.032..0.068, 0.020..0.060]
    for x, y, z in c["points"]:
        inside = 0.044 <= x <= 0.072 and 0.032 <= y <= 0.068 and 0.020 <= z <= 0.060
        assert not inside, f"path entered hollow solid shell at {(x, y, z)}"


def test_blocked_start_and_enclosed_end():
    # Blocked start: solid block covering start and its entire lead-search ray.
    grid_bs = _make_grid(
        (50, 40, 40), res=0.002, occupied_boxes=[(0, 0, 0, 49, 39, 39)]
    )
    res_bs = corridor.plan_corridor(
        grid_bs,
        start=[0.010, 0.040, 0.040],
        start_dir=[1.0, 0.0, 0.0],
        end=[0.090, 0.040, 0.040],
        end_dir=[1.0, 0.0, 0.0],
        diameter=0.006,
        bend_radius=0.010,
    )
    assert res_bs["candidates"] == []
    assert any("start blocked" in w for w in res_bs["warnings"])

    # Enclosed end: closed shell surrounding end (deep enough that -end_dir stays inside).
    grid_ee = _make_grid(
        (55, 50, 50), res=0.002, shell_boxes=[(12, 10, 10, 50, 40, 40)]
    )
    res_ee = corridor.plan_corridor(
        grid_ee,
        start=[0.014, 0.050, 0.050],
        start_dir=[1.0, 0.0, 0.0],
        end=[0.090, 0.050, 0.050],
        end_dir=[1.0, 0.0, 0.0],
        diameter=0.004,
        bend_radius=0.006,
        clearance=0.001,
    )
    assert res_ee["candidates"] == []
    assert any("not reachable" in w or "end blocked" in w for w in res_ee["warnings"])


def test_diverse_candidates_around_pillar():
    # Central pillar forces routing either +y or -y around it.
    pillar = [(26, 20, 0, 34, 30, 39)]
    grid = _make_grid((60, 50, 40), res=0.002, occupied_boxes=pillar)
    res = corridor.plan_corridor(
        grid,
        start=[0.016, 0.050, 0.040],
        start_dir=[1.0, 0.0, 0.0],
        end=[0.104, 0.050, 0.040],
        end_dir=[1.0, 0.0, 0.0],
        diameter=0.006,
        bend_radius=0.010,
        clearance=0.002,
        max_candidates=2,
    )
    assert len(res["candidates"]) == 2
    ys = [sum(p[1] for p in c["points"]) / len(c["points"]) for c in res["candidates"]]
    assert abs(ys[0] - ys[1]) > 0.01


def test_find_corridor_adapter_tool(monkeypatch, tmp_path):
    grid = _make_grid((50, 40, 30), res=0.002, occupied_boxes=[(22, 16, 0, 26, 24, 29)])
    calls = []

    def fake_call_tool(tool, op, sv, params, timeout):
        calls.append((tool, params))
        assert tool == "occupancy_grid"
        return {
            "status": "COMPLETED",
            "stateVersion": 1,
            "cadState": {"features": [json.dumps(grid)]},
        }

    monkeypatch.setattr(server, "call_tool", fake_call_tool)
    saved = tmp_path / "grid.json"
    fn = server._TOOL_FUNCS["find_corridor"]
    out = fn(
        start="[0.014, 0.040, 0.030]",
        start_dir="[1, 0, 0]",
        end="[0.086, 0.040, 0.030]",
        end_dir="[1, 0, 0]",
        diameter=0.006,
        bend_radius=0.010,
        clearance=0.002,
        save_grid=str(saved),
    )
    assert len(calls) == 1
    assert saved.is_file()
    payload = json.loads(out.split("COMPLETED | find_corridor | ", 1)[1])
    assert len(payload["candidates"]) >= 1
    probe = json.loads(payload["probe_paths"])
    assert len(probe) == len(payload["candidates"])
    assert probe[0]["diameter"] == 0.006

    # Replay via grid_file without calling the server again.
    calls.clear()
    out2 = fn(
        start="[0.014, 0.040, 0.030]",
        start_dir="[1, 0, 0]",
        end="[0.086, 0.040, 0.030]",
        end_dir="[1, 0, 0]",
        diameter=0.006,
        bend_radius=0.010,
        clearance=0.002,
        grid_file=str(saved),
    )
    assert calls == []
    payload2 = json.loads(out2.split("COMPLETED | find_corridor | ", 1)[1])
    assert len(payload2["candidates"]) == len(payload["candidates"])

    # Degenerate identical endpoints raise ToolError.
    with pytest.raises(ToolError, match="distinct"):
        fn(
            start="[0.02, 0.02, 0.02]",
            start_dir="[1, 0, 0]",
            end="[0.02, 0.02, 0.02]",
            end_dir="[1, 0, 0]",
            diameter=0.006,
            bend_radius=0.010,
            grid_file=str(saved),
        )


def test_performance_one_million_voxels():
    # 100 x 100 x 100 = 1,000,000 voxels (0.20 m cube at 2 mm resolution) with a central wall.
    grid = _make_grid(
        (100, 100, 100), res=0.002, occupied_boxes=[(45, 25, 0, 52, 75, 99)]
    )
    t0 = time.perf_counter()
    res = corridor.plan_corridor(
        grid,
        start=[0.020, 0.100, 0.100],
        start_dir=[1.0, 0.0, 0.0],
        end=[0.180, 0.100, 0.100],
        end_dir=[1.0, 0.0, 0.0],
        diameter=0.008,
        bend_radius=0.020,
        clearance=0.002,
        max_candidates=2,
    )
    elapsed = time.perf_counter() - t0
    assert len(res["candidates"]) >= 1
    assert elapsed < 15.0, f"1M voxel corridor took too long: {elapsed:.2f}s"
