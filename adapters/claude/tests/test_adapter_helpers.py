"""Adapter-side helpers that need no execution server: the routing_status wire table CSV, the
details a FAILED response passes on, and the start_job / job_status background jobs."""
import os
import sys
import time

import pytest
from mcp.server.mcpserver.exceptions import ToolError

_ADAPTER_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _ADAPTER_DIR)
import server  # noqa: E402
from response_mapper import FAILED_DETAILS_MAX, CadOperationError, map_response  # noqa: E402

WIRES = [
    {"name": "W10", "from": "A.1", "to": "B.2", "wire_name": "X 20", "cut_length_mm": 105.0,
     "min_arc_radius_mm": 8.0, "max_kink_deg": 4.0, "errors": []},
    {"name": "W2", "from": "A.2", "to": "C.1", "wire_name": "X 20", "cut_length_mm": 50.25,
     "min_arc_radius_mm": 7.5, "max_kink_deg": 12.0, "errors": ["violates_bend_radius"]},
]


def test_wire_csv_round_trip_and_compare(tmp_path):
    first = tmp_path / "wires.csv"
    rows, changed = server._write_wire_csv(str(first), WIRES, {}, "note line")
    assert rows == 2 and changed == []
    text = first.read_text(encoding="utf-8").splitlines()
    assert text[0] == "# note line"
    assert text[2].startswith("W2,")            # wire-number order, not text order
    assert server._read_wire_csv(str(first)) == {"W2": 50.25, "W10": 105.0}

    moved = [dict(WIRES[0], cut_length_mm=110.0), dict(WIRES[1], cut_length_mm=50.5)]
    second = tmp_path / "wires2.csv"
    _, changed = server._write_wire_csv(str(second), moved, server._read_wire_csv(str(first)), "")
    assert changed == ["W10"]                   # +5 mm; W2 moved 0.25 mm only
    lines = second.read_text(encoding="utf-8").splitlines()
    assert lines[0].endswith("Previous_mm,Delta_mm")
    assert lines[2].endswith("105.00,+5.00")


def test_failed_response_passes_details_on():
    response = {"status": "FAILED", "error": {"code": "PARTIAL", "message": "some failed"},
                "cadState": {"features": ['{"connection_points":[{"pin":"1","error":"why"}]}']}}
    with pytest.raises(CadOperationError) as err:
        map_response(response)
    assert "code=PARTIAL" in str(err.value) and '"error":"why"' in str(err.value)

    long = dict(response, cadState={"features": ["x" * (FAILED_DETAILS_MAX + 50)]})
    with pytest.raises(CadOperationError) as err:
        map_response(long)
    assert "[50 more characters]" in str(err.value)


def test_start_job_runs_a_tool_in_the_background(monkeypatch):
    seen = {}

    def fake_tool(route: str, wait: float = 0.0) -> str:
        seen["timeout"] = server._timeout_for("route_path")
        time.sleep(wait)
        return f"COMPLETED | {route}"

    monkeypatch.setitem(server._TOOL_FUNCS, "fake_tool", fake_tool)
    started = server.start_job("fake_tool", '{"route": "R-1", "wait": 0.2}')
    job_id = started.split("job_id=")[1].split(" |")[0]
    assert server.job_status(job_id).startswith(("RUNNING", "DONE"))
    done = server.job_status(job_id, wait_s=5)
    assert done.startswith("DONE") and "COMPLETED | R-1" in done
    assert seen["timeout"] == server.JOB_HTTP_TIMEOUT     # the job's long HTTP wait
    assert server._timeout_for("route_path") == server.LONG_HTTP_TIMEOUT   # outside the job
    assert job_id in server.job_status("")


def test_start_job_refuses_unknown_tools_and_bad_args(monkeypatch):
    with pytest.raises(ToolError, match="unknown tool"):
        server.start_job("no_such_tool", "{}")
    with pytest.raises(ToolError, match="unknown tool"):
        server.start_job("job_status", "{}")
    monkeypatch.setitem(server._TOOL_FUNCS, "fake_tool2", lambda route: route)
    with pytest.raises(ToolError, match="fake_tool2"):
        server.start_job("fake_tool2", '{"nope": 1}')


def test_failed_job_raises(monkeypatch):
    def boom() -> str:
        raise RuntimeError("SOLIDWORKS_BUSY")

    monkeypatch.setitem(server._TOOL_FUNCS, "boom", boom)
    job_id = server.start_job("boom").split("job_id=")[1].split(" |")[0]
    with pytest.raises(ToolError, match="SOLIDWORKS_BUSY"):
        server.job_status(job_id, wait_s=5)
