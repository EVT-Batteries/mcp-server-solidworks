"""Adapter-side helpers that need no execution server: the routing_status wire table CSV, the
details a FAILED response passes on, and the start_job / job_status background jobs."""
import ast
import json
import os
import sys
import time

import httpx
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


class _TimeoutThenResult:
    """httpx.Client stand-in: POST times out, GET result/<op> answers from a script."""

    def __init__(self, script):
        self.script = list(script)
        self.gets = 0

    def __call__(self, **kwargs):
        return self

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False

    def post(self, url, json=None):
        raise httpx.ReadTimeout("slow")

    def get(self, url):
        self.gets += 1
        status, body = self.script.pop(0) if len(self.script) > 1 else self.script[0]
        return _Resp(status, body)


class _Resp:
    def __init__(self, status, body):
        self.status_code = status
        self._body = body

    def json(self):
        return self._body


def test_timeout_fetches_the_real_result(monkeypatch):
    import execution_client as ec

    done = {"status": "COMPLETED", "stateVersion": 7, "cadState": {"features": ["x"]}}
    fake = _TimeoutThenResult([(200, {"state": "running"}), (200, {"state": "running"}),
                               (200, {"state": "done", "response": done})])
    monkeypatch.setattr(ec, "_http_client", fake)
    monkeypatch.setattr(ec, "RECOVER_POLL_S", 0.0)
    resp = ec.call_tool("place_component", "op-1", 6, {}, timeout=1)
    assert resp["status"] == "COMPLETED" and resp["stateVersion"] == 7 and fake.gets == 3
    assert "recovered_after_timeout_s=" in map_response(resp)


def test_timeout_without_result_endpoint_still_fails(monkeypatch):
    import execution_client as ec

    monkeypatch.setattr(ec, "_http_client", _TimeoutThenResult([(404, {})]))
    monkeypatch.setattr(ec, "RECOVER_POLL_S", 0.0)
    with pytest.raises(ec.ExecutionLayerError, match="older build"):
        ec.call_tool("place_component", "op-2", 6, {}, timeout=1)
    monkeypatch.setattr(ec, "_http_client", _TimeoutThenResult([(200, {"state": "unknown"})]))
    with pytest.raises(ec.ExecutionLayerError, match="does not know this call"):
        ec.call_tool("place_component", "op-3", 6, {}, timeout=1)
    monkeypatch.setattr(ec, "_http_client", _TimeoutThenResult([(200, {"state": "running"})]))
    monkeypatch.setattr(ec, "TIMEOUT_RECOVER_WAIT", 0.0)
    with pytest.raises(ec.ExecutionLayerError, match="still running"):
        ec.call_tool("place_component", "op-4", 6, {}, timeout=1)


# ---------------------------------------------------------------------------
# plan_probe_sizes / check_path_clearance(paths_file, names, batches) with a mocked server
# ---------------------------------------------------------------------------


def _completed(payload: dict) -> dict:
    return {"status": "COMPLETED", "stateVersion": 3,
            "cadState": {"features": [json.dumps(payload)]}}


def _payload(text: str) -> dict:
    rest = text.split("features=", 1)[1].split(" | frame_component", 1)[0]
    return json.loads(ast.literal_eval(rest)[0])


PLAN = {"fan_out": [{"connector": "Plug-1"}], "paths": [
    {"name": "T", "points": [[0, 0, 0], [0.1, 0, 0]], "bend_radii": []},
    {"name": "A", "points": [[0.1, 0, 0], [0.15, 0.05, 0]], "bend_radius": 0.01},
    {"name": "B", "points": [[0.1, 0, 0], [0.15, -0.05, 0]]},
]}
STATUS = {"route": {
    "wires": [{"name": "W1", "from": "P1.1", "to": "J1.1", "diameter_mm": 1.6},
              {"name": "W2", "from": "P1.2", "to": "J2.1", "diameter_mm": 1.6},
              {"name": "W3", "from": "Q1.1", "to": "Q2.1", "diameter_mm": 1.6}],
    "stub_check": {"stubs": [
        {"end": "P1.1", "component": "Plug-1", "stub_end_mm": [-20, 0, 0]},
        {"end": "P1.2", "component": "Plug-1", "stub_end_mm": [-20, 1, 0]},
        {"end": "J1.1", "component": "Rec-1", "stub_end_mm": [150, 50, 0]},
        {"end": "J2.1", "component": "Rec-2", "stub_end_mm": [150, -50, 0]}]}}}


def test_plan_probe_sizes_writes_sized_probe(monkeypatch, tmp_path):
    calls = []

    def fake_call_tool(tool, op, sv, params, timeout):
        calls.append((tool, params))
        if tool == "routing_status":
            assert params["check_stubs"] is True
            return _completed(STATUS)
        if tool == "route_path":
            assert params["dry_run"] is True and "paths" not in params
            return _completed({"fan_out": [{"connector": "Plug-1", "pins": ["1", "2"],
                                            "join": [0.0002, 0, 0], "join_mm": [0.2, 0, 0]}]})
        raise AssertionError(tool)

    monkeypatch.setattr(server, "call_tool", fake_call_tool)
    plan_file = tmp_path / "plan.json"
    plan_file.write_text(json.dumps(PLAN), encoding="utf-8")
    text = server._TOOL_FUNCS["plan_probe_sizes"](
        route="Harness_1-Top-1", plan=str(plan_file), covering_rule="[[2, 0.006]]",
        default_diameter=0.005)
    summary = json.loads(text.split("| plan_probe_sizes | ", 1)[1])
    assert summary["placed"] == 2 and summary["outside"] == 1 and summary["problems_count"] == 0
    assert summary["path_sizes"] == {"T": [6.0, 2], "A": [1.6, 1], "B": [1.6, 1]}
    assert summary["probe_out"] == str(tmp_path / "plan.probe.json")
    probe = json.loads((tmp_path / "plan.probe.json").read_text(encoding="utf-8"))
    t, a, _ = probe["paths"]
    assert t["diameter"] == 0.006 and t["wires"] == ["W1", "W2"] and t["bend_radii"] == []
    assert a["diameter"] == 0.0016 and a["bend_radius"] == 0.01
    assert [c[0] for c in calls] == ["routing_status", "route_path"]


def test_check_path_clearance_paths_file_names_and_batches(monkeypatch, tmp_path):
    sent = []

    def fake_call_tool(tool, op, sv, params, timeout):
        assert tool == "check_path_clearance"
        paths = json.loads(params["paths"])
        sent.append(paths)
        n = len(paths)
        hits = [{"component": "Box-1", "volume_mm3": 3.0}] if len(sent) == 2 else []
        if hits and n > 1:
            hits[0]["path"] = 1
        return _completed({"paths": n, "path_length_mm": [10.0] * n, "interference_count": len(hits),
                           "interferences": hits, "clear": not hits, "cleanup": []})

    monkeypatch.setattr(server, "call_tool", fake_call_tool)
    probe = {"paths": [{"name": f"P{i}", "points": [[0, 0, i * 0.01], [0.1, 0, i * 0.01]],
                        "diameter": 0.006, "wires": ["W1"], "wire_count": 1} for i in range(5)]}
    probe["paths"][4].pop("diameter")
    f = tmp_path / "x.probe.json"
    f.write_text(json.dumps(probe), encoding="utf-8")
    fn = server._TOOL_FUNCS["check_path_clearance"]

    pl = _payload(fn(paths_file=str(f), max_paths=2, diameter=0.004))
    assert [len(s) for s in sent] == [2, 2, 1]
    assert all(set(p) <= {"points", "bend_radii", "bend_radius", "diameter"} for s in sent for p in s)
    assert sent[2][0]["diameter"] == 0.004            # no diameter in the file: the parameter
    assert pl["paths"] == 5 and pl["interference_count"] == 1 and pl["clear"] is False
    assert pl["interferences"][0]["path"] == 3 and pl["interferences"][0]["path_name"] == "P3"
    assert [b["clear"] for b in pl["batches"]] == [True, False, True]

    sent.clear()
    pl = _payload(fn(paths_file=str(f), names='["P2"]'))
    assert len(sent) == 1 and sent[0][0]["points"][0][2] == 0.02
    assert pl["paths"] == 1 and "batches" not in pl

    with pytest.raises(ToolError, match="names not in the paths"):
        fn(paths_file=str(f), names='["nope"]')
    with pytest.raises(ToolError, match="not both"):
        fn(paths_file=str(f), points="[[0,0,0],[1,0,0]]")


def test_route_follow_reports_step_timings(monkeypatch, tmp_path):
    def fake_call_tool(tool, op, sv, params, timeout):
        if tool == "routing_status":
            return _completed(STATUS)
        if tool == "route_path":
            if "paths" not in params:
                return _completed({"fan_out": [{"connector": "Plug-1", "pins": ["1", "2"],
                                                "join": [0.0, 0, 0], "join_mm": [0, 0, 0]}]})
            return _completed({"sharp_junctions": 0, "cycle_count": 0, "paths": []})
        raise AssertionError(tool)

    monkeypatch.setattr(server, "call_tool", fake_call_tool)
    plan_file = tmp_path / "plan.json"
    plan_file.write_text(json.dumps(PLAN), encoding="utf-8")
    text = server._TOOL_FUNCS["route_follow"](route="Harness_1-Top-1", plan=str(plan_file),
                                              reimport=False, draw=False)
    summary = json.loads(text.split("| route_follow | ", 1)[1])
    assert set(summary["timings_s"]) == {"follow", "dry_run", "total"}

    def failing(tool, op, sv, params, timeout):
        if tool == "route_path" and "paths" in params:
            return {"status": "FAILED", "error": {"code": "X", "message": "boom"}}
        return fake_call_tool(tool, op, sv, params, timeout)

    monkeypatch.setattr(server, "call_tool", failing)
    with pytest.raises(ToolError, match="failed in step dry_run .*timings_s="):
        server._TOOL_FUNCS["route_follow"](route="Harness_1-Top-1", plan=str(plan_file),
                                           reimport=False, draw=False)
