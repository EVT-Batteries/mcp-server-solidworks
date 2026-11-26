import contextlib
import contextvars
import csv
import functools
import hashlib
import inspect
import io
import json
import os
import sys
import threading
import time
import uuid
from datetime import datetime, timezone
from typing import Annotated, Literal, Optional
from pydantic import Field
from mcp.server import MCPServer
from mcp.server.caching import CacheHint
from mcp.server.mcpserver.exceptions import ResourceError, ToolError
from mcp.shared.exceptions import MCPError
from config import LONG_HTTP_TIMEOUT
from execution_client import call_tool, get_state, ensure_ready as _ensure_ready, ExecutionLayerError
from response_mapper import CadOperationError, map_response
import assembly_math as am
import corridor as _corridor
# NOTE: pycompiler is reached via `from ir_execution_port import run_feature_graph` imported
# LAZILY inside rebuild_from_ir and submit_feature_graph — a missing compiler tree degrades to a
# clean tool error instead of killing the whole MCP server at startup.

# P0.4 MCP hardening: discriminators are `Literal[...]` (invalid values rejected at the
# MCP schema level, before any REST/COM round-trip) and numeric params carry Pydantic
# `Field` constraints (units/ranges). Coordinates stay plain float (negative/zero are
# legitimate); open-ended selector strings (entity1_type, plane/material names) stay str
# so valid selectors are never rejected. Editing this file requires reconnecting the
# `solidworks` MCP server (no hot-reload — KNOWN-LIMITATIONS #4).

# Every tool is registered with `structured_output=False`. All 46 return a `str`, so the
# SDK's structured mirror is `{"result": "<the same string>"}` — the whole payload a second
# time, for zero added information (measured: exactly 2.00x on both SDKs). Switching it off
# halves the wire cost of every reader (the 33 KB `schema://feature-graph` read went over at
# 66 KB before this) and drops the per-tool `outputSchema` from the tools/list prompt. Restore
# it only for a tool that returns real structure, never for a `-> str` one.

# Cache hints for the resource surface (SEP-2549, protocol 2026-07-28). NOTE: `ttl_ms`/
# `cache_scope` are set PER METHOD at construction, NOT per resource — the high-level
# `@mcp.resource` decorator returns str/bytes, so an individual handler cannot carry its own TTL.
# One value therefore covers every resource. `scope="public"`: the recipe rules and contract
# schemas are byte-identical for every caller, with nothing authorization-dependent in them.
# ttl_ms=1h is deliberately shorter than it could be — recipe-usage.md is read FRESH from disk on
# every read so an edit goes live without a reconnect, and a long TTL would defeat that.
# MEASURED 2026-07-30, and it settles the matter: the host does NOT honor these hints. A marker
# appended to recipe-usage.md showed up in `recipe://usage/coverage` immediately, with no
# reconnect, on a URI already read minutes earlier in the same session — a TTL-honoring client
# would have served the stale copy. Good news (reads are genuinely fresh), but the caching win is
# ZERO. And no host setting can change the deeper limit: a cached body STILL enters the model's
# context on every read, so TTL caching can only save a round-trip, never tokens. The whole
# measured saving of this migration is the per-turn `tools/list` shrinkage. Hints stay declared —
# spec-correct, free, and a future host may use them.
_RECIPE_CACHE = CacheHint(ttl_ms=3_600_000, scope="public")  # 1 h
mcp = MCPServer(
    "solidworks-execution-adapter",
    cache_hints={
        "resources/read": _RECIPE_CACHE,
        "resources/list": _RECIPE_CACHE,
        "resources/templates/list": _RECIPE_CACHE,
    },
)

# Error passing. The SDK shows the model the message of a ToolError only: any other exception
# (RuntimeError, httpx errors, a JSON decode error, ...) reaches it as a bare "Error executing tool
# <name>" with the reason only in the server log. Every tool is therefore registered through this
# wrapper, which turns whatever it raises into a ToolError carrying the exception's type and text.
_register_tool = mcp.tool
# Every registered tool by name (start_job runs them in the background).
_TOOL_FUNCS: dict = {}


def _tool_with_errors(*dargs, **dkwargs):
    register = _register_tool(*dargs, **dkwargs)

    def decorate(fn):
        @functools.wraps(fn)
        def wrapper(*args, **kwargs):
            try:
                return fn(*args, **kwargs)
            except (ToolError, MCPError):
                raise
            except Exception as exc:  # noqa: BLE001 - the point is to pass every error on
                text = str(exc) or repr(exc)
                if not isinstance(exc, RuntimeError):
                    text = f"{type(exc).__name__}: {text}"
                raise ToolError(text) from exc

        _TOOL_FUNCS[fn.__name__] = wrapper
        return register(wrapper)

    return decorate


class _JobTools:
    """The tools start_job may run: every registered tool except the job tools themselves."""

    def get(self, name: str):
        return None if name in ("start_job", "job_status") else _TOOL_FUNCS.get(name)

    def __iter__(self):
        return (n for n in _TOOL_FUNCS if n not in ("start_job", "job_status"))


_JOB_TOOLS = _JobTools()


mcp.tool = _tool_with_errors

# Tools that can run for minutes on a large assembly: they wait LONG_HTTP_TIMEOUT instead of
# HTTP_TIMEOUT for the execution server's answer.
LONG_TOOLS = {
    "analyze_assembly", "check_interference", "check_rebuild", "component_status",
    "set_component_state", "import_step", "measure_curves", "manage_configurations",
    "open_document", "save_document", "compare_assemblies", "insert_component", "add_mate",
    "routing_status", "import_from_to", "route_path", "route_guidelines", "start_route",
    "replace_component", "insert_part", "edit_bodies", "check_path_clearance",
    "analyze_model", "connector_cavities", "add_connector_pins", "file_dependencies",
    "occupancy_grid", "find_corridor",
}


def _timeout_for(tool_name: str) -> float | None:
    job_timeout = _JOB_TIMEOUT.get()
    if job_timeout is not None:
        return job_timeout
    return LONG_HTTP_TIMEOUT if tool_name in LONG_TOOLS else None


# Background jobs (start_job / job_status): a tool call run in a thread of the adapter, so a call
# that takes longer than LONG_HTTP_TIMEOUT (a route draw on a large assembly: 15-25 min) neither
# times out nor blocks the conversation. Inside a job every HTTP wait is JOB_HTTP_TIMEOUT.
JOB_HTTP_TIMEOUT = float(os.getenv("JOB_HTTP_TIMEOUT", "14400"))
_JOB_TIMEOUT: contextvars.ContextVar = contextvars.ContextVar("solidpilot_job_timeout", default=None)
_jobs: dict = {}
_jobs_lock = threading.Lock()


# Tracks state_version in memory. Starts at 0, updated after every response.
# Auto-resyncs from the execution layer on an INVALID_STATE_VERSION mismatch
# (e.g. the execution server was restarted after a rebuild).
_state_version: int = 0


def _next_operation_id() -> str:
    return str(uuid.uuid4())


def _update_state_version(response: dict) -> None:
    global _state_version
    sv = response.get("stateVersion")
    if sv is not None:
        _state_version = sv


def _is_state_mismatch(response: dict) -> bool:
    if response.get("status") != "FAILED":
        return False
    return (response.get("error") or {}).get("code") == "INVALID_STATE_VERSION"


def _call(tool_name: str, params: dict) -> str:
    """Send a tool call to the execution layer.

    If the server reports an INVALID_STATE_VERSION mismatch (it was restarted and
    its state_version reset while this adapter kept its old value), fetch the
    authoritative state_version and retry once with a fresh operation_id. This
    removes the need to restart the adapter after every execution-layer rebuild.
    """
    global _state_version
    timeout = _timeout_for(tool_name)
    response = call_tool(tool_name, _next_operation_id(),
                         _state_version, params, timeout)
    if _is_state_mismatch(response):
        _state_version = get_state()
        response = call_tool(
            tool_name, _next_operation_id(), _state_version, params, timeout)
    _update_state_version(response)
    return map_response(response)


def _payload_of(response: dict) -> dict:
    """The JSON object a tool returns as its first cadState feature ({} when there is none)."""
    feats = (response.get("cadState") or {}).get("features") or []
    if feats and isinstance(feats[0], str):
        try:
            value = json.loads(feats[0])
        except ValueError:
            return {}
        return value if isinstance(value, dict) else {}
    return {}


def _call_json(tool_name: str, params: dict) -> tuple:
    """(raw response, payload dict); raises like _call on a FAILED response."""
    raw = _call_raw(tool_name, params)
    map_response(raw)
    return raw, _payload_of(raw)


def _text_with(raw: dict, payload: dict) -> str:
    """The usual result text of `raw` with its payload replaced (compact / filtered results)."""
    out = dict(raw)
    state = dict(out.get("cadState") or {})
    state["features"] = [json.dumps(payload, separators=(",", ":"))]
    out["cadState"] = state
    return map_response(out)


def _keep_problems(payload: dict, key: str, ok) -> None:
    """Compact a list in place: `<key>_count` + only the entries `ok` rejects."""
    items = payload.get(key)
    if not isinstance(items, list):
        return
    payload[f"{key}_count"] = len(items)
    bad = [x for x in items if not ok(x)]
    if bad:
        payload[key] = bad
    else:
        payload.pop(key, None)


def _component_transforms(names: list) -> dict:
    """{instance path: component_status entry} for the named components of the active assembly."""
    _, pl = _call_json("component_status", {"components": json.dumps(names), "recursive": True,
                                            "limit": max(10, len(names)), "include_mates": False,
                                            "boxes": False, "time_budget_s": 60.0})
    listed = {c.get("component"): c for c in pl.get("components") or []}
    out = {}
    for n in names:
        hit = listed.get(n) or next((c for p, c in listed.items() if p and p.lower() == n.lower()), None)
        if hit is not None:
            out[n] = hit
    missing = [n for n in names if n not in out or not isinstance(out[n].get("transform"), list)]
    if missing:
        raise ToolError(f"component(s) not found or without transform: {', '.join(missing)} "
                        f"({pl.get('not_found_hint') or 'check the instance paths'})")
    return out


# ---------------------------------------------------------------------------
# Tool: ensure_ready  (lifecycle / bootstrap — not a state-versioned CAD op)
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def ensure_ready() -> str:
    """Bring the SolidWorks environment up and confirm it is ready to use.

    Starts the execution server if it isn't running, and launches SolidWorks if it is closed
    (just attaches if it's already open). Does NOT open or create any document — use
    open_new_part / create_drawing for that (so assembly/drawing workflows aren't forced into a
    part). Safe to call anytime; idempotent. Call this first at the start of a session, or
    whenever a tool fails with a connection / COM-attach error. Reports attached_pid (the
    SolidWorks process the tools drive) and a WARNING when several SolidWorks processes run:
    tools drive the instance the user started last (registered in the ROT as
    SolidWorks_PID_<pid>), never a stale one that only holds the COM class registration."""
    global _state_version
    body = _ensure_ready()
    if not body.get("comAttached"):
        raise RuntimeError(
            "SolidWorks is not ready | "
            f"launch_error={body.get('launchError') or body.get('ensureError')}"
        )
    sv = body.get("stateVersion")
    if sv is not None:
        _state_version = sv
    return (
        f"READY | server=UP | "
        f"com_attached={body.get('comAttached')} | "
        f"sw_launched={body.get('swLaunched')} | "
        f"active_document={body.get('activeDocument')} | "
        f"sw_version={body.get('swVersion')} | "
        f"attached_pid={body.get('attachedPid')} | "
        f"state_version={body.get('stateVersion')}"
        + (f" | WARNING {body['instanceWarning']}" if body.get('instanceWarning') else "")
        + (f" | {body['attachNote']}" if body.get('attachNote') else "")
    )


# ---------------------------------------------------------------------------
# Tools: start_job / job_status  (adapter-side background calls)
# ---------------------------------------------------------------------------
def _run_job(job_id: str, fn, kwargs: dict) -> None:
    _JOB_TIMEOUT.set(JOB_HTTP_TIMEOUT)
    job = _jobs[job_id]
    try:
        job["result"] = fn(**kwargs)
        job["status"] = "done"
    except Exception as exc:  # noqa: BLE001 - reported through job_status
        job["error"] = str(exc) or repr(exc)
        job["status"] = "failed"
    job["finished"] = time.time()


@mcp.tool(structured_output=False)
def start_job(tool: str, args: str = "{}") -> str:
    """Run another SolidPilot tool in the background and return at once with a job id; read its
    result with job_status. For calls that run longer than the normal wait (LONG_HTTP_TIMEOUT,
    default 900 s), e.g. import_from_to + route_path on a large assembly, or when other work can
    go on meanwhile. Inside the job the HTTP wait is JOB_HTTP_TIMEOUT (default 4 h).
    tool: the tool's name (e.g. 'route_path'); args: JSON object of its arguments, exactly as the
        tool takes them (strings stay strings: '{"route": "...", "paths": "[...]"}').
    SolidWorks runs one call at a time: while a job runs, other calls that need SolidWorks fail
    with SOLIDWORKS_BUSY after 20 s (read-only adapter tools such as job_status still answer).
    Jobs live in this adapter process: an MCP restart loses them (the SolidWorks call itself
    goes on)."""
    fn = _JOB_TOOLS.get(tool)
    if fn is None:
        raise ToolError(f"start_job: unknown tool '{tool}'. Tools: {', '.join(sorted(_JOB_TOOLS))}")
    try:
        kwargs = json.loads(args or "{}")
    except json.JSONDecodeError as exc:
        raise ToolError(f"start_job: args is not a JSON object: {exc}") from exc
    if not isinstance(kwargs, dict):
        raise ToolError("start_job: args must be a JSON object of the tool's arguments.")
    try:
        inspect.signature(fn).bind(**kwargs)
    except TypeError as exc:
        raise ToolError(f"start_job: {tool}{inspect.signature(fn)}: {exc}") from exc
    job_id = uuid.uuid4().hex[:8]
    with _jobs_lock:
        running = [j for j, v in _jobs.items() if v["status"] == "running"]
        _jobs[job_id] = {"tool": tool, "status": "running", "started": time.time(),
                         "finished": None, "result": None, "error": None}
    threading.Thread(target=_run_job, args=(job_id, fn, kwargs), daemon=True,
                     name=f"solidpilot-job-{job_id}").start()
    note = f" | WARNING jobs still running: {', '.join(running)} (SolidWorks takes one call at a time)" if running else ""
    return f"STARTED | job_id={job_id} | tool={tool}{note}"


@mcp.tool(structured_output=False)
def job_status(job_id: str = "", wait_s: Annotated[float, Field(ge=0, le=600)] = 0.0) -> str:
    """Status of a background job from start_job (adapter-side, never calls SolidWorks).
    job_id: the job; empty = list every job of this adapter (id, tool, status, elapsed).
    wait_s: wait up to this many seconds for the job to finish before answering (0 = answer now).
    Returns RUNNING with the elapsed time, or DONE with the tool's own result text, or FAILED
    with its error."""
    if not job_id:
        if not _jobs:
            return "NO JOBS"
        now = time.time()
        return "\n".join(
            f"{j} | {v['tool']} | {v['status'].upper()} | {round((v['finished'] or now) - v['started'])} s"
            for j, v in _jobs.items())
    job = _jobs.get(job_id)
    if job is None:
        raise ToolError(f"job_status: no job '{job_id}' (jobs: {', '.join(_jobs) or 'none'}).")
    deadline = time.time() + wait_s
    while job["status"] == "running" and time.time() < deadline:
        time.sleep(min(1.0, max(0.0, deadline - time.time())))
    elapsed = round((job["finished"] or time.time()) - job["started"])
    if job["status"] == "running":
        return f"RUNNING | job_id={job_id} | tool={job['tool']} | elapsed={elapsed} s"
    if job["status"] == "failed":
        raise ToolError(f"FAILED | job_id={job_id} | tool={job['tool']} | elapsed={elapsed} s | {job['error']}")
    return f"DONE | job_id={job_id} | tool={job['tool']} | elapsed={elapsed} s | {job['result']}"


# ---------------------------------------------------------------------------
# Tool: open_new_part
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def open_new_part(template_path: str = "") -> str:
    """Open a new SolidWorks part document."""
    params = {}
    if template_path:
        params["template_path"] = template_path
    return _call("open_new_part", params)


# ---------------------------------------------------------------------------
# Tool: open_document
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def open_document(file_path: str, as_assembly: bool = False,
                  lightweight: bool | None = None) -> str:
    """Open an EXISTING document from disk (counterpart to open_new_part, which makes a BLANK doc).
    file_path: full path to the file.
      - Native .sldprt / .sldasm / .slddrw open directly.
      - Foreign .step / .iges / .x_t / NX .prt / .ipt / .CATPart import via a STAGED path: the
        CLASSIC translator (LoadFile4) first — proven for STEP/IGES and NX .prt — with a
        3D-Interconnect retry as the last resort. A clear OPEN_FAILED means no usable translator:
        convert to STEP or defer that file.
      - as_assembly=True (foreign files only): the file is an ASSEMBLY STEP — the classic import
        would flatten it to a MULTIBODY PART; this asks for a real assembly import instead.
      - To turn a STEP into a native part, use import_step: after open_document the active
        document stays the import, on which part queries fail.
    lightweight (assemblies only): True opens every component lightweight (fast, no geometry
    loaded), False fully resolved; left out, SolidWorks' own setting decides. The result
    reports `lightweight_components=N` when some are lightweight. Tools that need a component's
    geometry (analyze_assembly faces/edges, add_mate, check_interference) resolve the components
    they are given; check_rebuild and measure_curves skip lightweight ones unless
    resolve_lightweight=True.
    Makes the opened document active and bumps state_version."""
    params: dict = {"file_path": file_path}
    if as_assembly:
        params["as_assembly"] = True
    if lightweight is not None:
        params["lightweight"] = lightweight
    return _call("open_document", params)


# ---------------------------------------------------------------------------
# Tool: import_step
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def import_step(file_path: str, save_as: str = "", overwrite: bool = False) -> str:
    """Import a neutral CAD file (.step / .stp / .iges / .igs / .x_t / .x_b / .sat) as a NATIVE
    PART and leave that part open and active, ready for analyze_model, faces and insert_component.
    It imports the file (as open_document does), saves the import as a .SLDPRT (an assembly STEP
    becomes one multi-body part), closes the import document and reopens the saved part.
    save_as: the .SLDPRT path (default: next to the source, same name). Fails with FILE_EXISTS
    when it exists, unless overwrite=True. Never point save_as at shared or released CAD (see
    SOLIDPILOT_READONLY_PATHS under save_document).
    Returns source, part (path), imported_as (PART / ASSEMBLY), solid_bodies, surface_bodies,
    box_min / box_max / size (m) and a warning when the import has no solid body. Bumps
    state_version."""
    params: dict = {"file_path": file_path, "overwrite": overwrite}
    if save_as:
        params["save_as"] = save_as
    return _call("import_step", params)


# ---------------------------------------------------------------------------
# Tool: open_new_assembly  (Phase B, ADR-047)
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def open_new_assembly(template_path: str = "") -> str:
    """Open a new blank SolidWorks ASSEMBLY document (the twin of open_new_part).
    Use before insert_component / add_mate. template_path: optional .asmdot template
    (default: the user-configured assembly template)."""
    params = {}
    if template_path:
        params["template_path"] = template_path
    return _call("open_new_assembly", params)


# ---------------------------------------------------------------------------
# Tool: analyze_assembly  (Phase B read tool, ADR-047 B1)
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def analyze_assembly(
    analysis_type: Literal["components", "components_flat", "mates", "faces", "edges"],
    component: str = "",
    world: bool = False,
    kind: Literal["", "plane", "cylinder", "cone", "sphere", "other"] = "",
    near: str = "",
    within: Annotated[float, Field(ge=0)] = 0.0,
    axis: str = "",
    axis_tol_deg: Annotated[float, Field(ge=0, le=90)] = 2.0,
    radius_min: Annotated[float, Field(ge=0)] = 0.0,
    radius_max: Annotated[float, Field(ge=0)] = 0.0,
) -> str:
    """Analyze the active ASSEMBLY document (read-only, does NOT change state).
    analysis_type='components': top-level component occurrences in FEATURE-TREE order — per
        instance: name (instance-numbered, e.g. 'Shaft-1'), source file path, config, fixed /
        suppressed flags, children count (a subassembly — out of scope, recorded as a gap), and
        the FULL transform (13 numbers: 3x3 rotation row-major + translation in meters + scale)
        — the position ground truth for round-trip verification.
    analysis_type='components_flat': LEAF occurrences across ALL nesting levels (depth-first
        tree order, transforms in ROOT space, each with its parent's name) — flattens a
        wrapper/subassembly level for ground-truth acquisition; the IR itself stays flat.
    analysis_type='mates': all mates in CREATION order — type and alignment from the SolidWorks
        ENUMS (locale-proof), the SI value for distance/angle mates, and per mate entity: owning
        component, entity kind (plane/cylinder/circle/...), and its geometric params in ASSEMBLY
        space (location + direction + radii).
    analysis_type='faces' / 'edges': ONE component's face/edge list with stable indices — feeds
        add_mate's index-based entity selection. Requires `component` (the Name2 from
        'components', or an instance path 'Sub-1/Part-1'). Coordinates are COMPONENT-LOCAL (the
        payload carries the transform). A lightweight component (and its lightweight parents) is
        resolved first and listed in resolved_from_lightweight; resolving can rename instances.
    Components report `lightweight: true` when they are lightweight. An unknown name fails with
    the closest instance paths.
    Face filters (faces only, applied by the adapter; a large part has thousands of faces):
    world=True reports point / origin / normal / axis in the ACTIVE assembly's coordinates
    (frame='world'); near / axis are then given in those coordinates too, else component-local.
    kind: 'plane' | 'cylinder' | 'cone' | 'sphere' | 'other'. near: JSON [x,y,z] (m): faces whose
    point lies within `within` (m, 0 = any), nearest first, each with distance_mm, and
    axis_distance_mm (cylinder: near to its axis line) or plane_distance_mm (plane: signed, along
    its normal). axis: JSON [dx,dy,dz]: cylinders whose axis is parallel (either sense) and planes
    whose normal points along it, within axis_tol_deg. radius_min / radius_max (m): cylinders by
    radius. The result keeps faces_total and filters. Ring terminal on a stud: seat_on_stud.
    Speed: faces are cached per file and configuration (face_cache: hit / listed). The first
    read of a large part lists its faces (~30 ms each: 1.5 min for 2,900 faces); give near +
    within (and kind / radius) so only the faces there get full records. Later reads of any
    instance of that file are fast."""
    params = {"analysis_type": analysis_type}
    if component:
        params["component"] = component
    filtered = world or kind or near.strip() or axis.strip() or radius_min or radius_max
    if analysis_type != "faces" or not filtered:
        return _call("analyze_assembly", params)
    near_p = am.parse_numbers(near, "near")
    axis_v = am.parse_numbers(axis, "axis")
    for name, v in (("near", near_p), ("axis", axis_v)):
        if v is not None and len(v) != 3:
            raise ToolError(f"analyze_assembly: {name} needs 3 numbers")
    pre: dict = {}
    if near_p is not None and within:
        pre.update(near=near_p, within=within, frame="assembly" if world else "component")
    if kind:
        pre["kind"] = kind
    if radius_min:
        pre["radius_min"] = radius_min
    if radius_max:
        pre["radius_max"] = radius_max
    if pre:
        params["prefilter"] = json.dumps(pre)   # cheap server-side pass; exact filter below
    raw, pl = _call_json("analyze_assembly", params)
    faces = pl.get("faces") or []
    if world:
        t = pl.get("transform")
        if not isinstance(t, list) or len(t) < 12:
            raise ToolError("analyze_assembly: the faces payload has no component transform to map to world")
        faces = am.faces_to_world(faces, t)
        pl["frame"] = "world"
    kept = am.filter_faces(faces, kind=kind, near=near_p, within=within, axis=axis_v,
                           axis_tol_deg=axis_tol_deg, radius_min=radius_min, radius_max=radius_max)
    pl["faces_total"] = len(pl.get("faces") or [])
    pl["faces"] = kept
    pl["filters"] = {k: v for k, v in (("kind", kind), ("near", near_p), ("within", within or None),
                                       ("axis", axis_v), ("radius_min", radius_min or None),
                                       ("radius_max", radius_max or None)) if v}
    return _text_with(raw, pl)


# ---------------------------------------------------------------------------
# Tool: insert_component  (Phase B build tool, ADR-047 B3)
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def insert_component(
    file_path: str,
    x: float = 0.0,
    y: float = 0.0,
    z: float = 0.0,
    config: str = "",
    fixed: bool = False,
    transform_json: str = "[]",
) -> str:
    """Insert a part or sub-assembly file as a component of the ACTIVE assembly (open_new_assembly first).
    file_path: the .SLDPRT or .SLDASM to insert (meters for all coordinates).
    x/y/z: insertion point — used when transform_json is not given. SolidWorks puts the part's
        BOUNDING-BOX CENTRE there, not its origin; to place the part origin exactly (e.g. parts
        modelled in the assembly's coordinates), pass transform_json='[1,0,0,0,1,0,0,0,1,0,0,0,1]'.
    transform_json: OPTIONAL full placement as a JSON array STRING of 13 numbers
        (3x3 rotation row-major + translation xyz + scale — exactly the layout
        analyze_assembly(components) reports). Needed for rotated placements.
    fixed: True fixes the occurrence (its transform becomes authoritative); False leaves it
        floating for mates to position. The state is applied explicitly either way.
    Returns the runtime component name (e.g. 'Shaft-2') + fixed/transform readbacks."""
    params = {"file_path": file_path, "x": x, "y": y, "z": z, "fixed": fixed}
    if config:
        params["config"] = config
    if transform_json and transform_json != "[]":
        params["transform_json"] = transform_json
    return _call("insert_component", params)


# ---------------------------------------------------------------------------
# Tool: check_rebuild
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def check_rebuild(
    rebuild: bool = True,
    recursive: bool = True,
    include_warnings: bool = True,
    resolve_lightweight: bool = False,
    force: bool = False,
    verbose: bool = False,
) -> str:
    """SolidWorks' "What's Wrong" list for the active document (does NOT change state_version;
    never saves). rebuild=True rebuilds what is out of date first (EditRebuild3), so the list is
    current. force=True rebuilds every feature on every level (ForceRebuild3): on a large assembly
    that marks every loaded reference modified, and the next save rewrites them all with no change;
    use it only when a plain rebuild leaves stale results.
    In an assembly with recursive=True, every loaded sub-assembly and part document is checked too
    (each distinct file once, with `first_instance` = one instance path that uses it).
    Each problem gives severity (error / warning), the feature or mate name and type, the
    swFeatureError_e code and its meaning, and for a mate `mate_components`: the instance path of
    each component it references (null = that reference is gone). `components_not_loaded` lists
    unsuppressed components whose file could not be loaded, as counts per reason (lightweight /
    not loaded / file missing), plus `components_not_loaded_list` for the missing / not-loaded
    ones (verbose=True lists the lightweight ones too: ~240 kB on 1000 components). Lightweight
    components are not checked (no model loaded):
    resolve_lightweight=True resolves every lightweight component first (slow on a large assembly;
    reported as lightweight_resolved). `clean` is true when nothing is wrong and every component
    was checked. Run it after every structural change (deleted features, moved or replaced
    components) and before saving an assembly."""
    return _call("check_rebuild", {"rebuild": rebuild, "recursive": recursive,
                                   "include_warnings": include_warnings,
                                   "resolve_lightweight": resolve_lightweight,
                                   "force": force, "verbose": verbose})


# ---------------------------------------------------------------------------
# Tool: check_interference
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def check_interference(
    components: str = "[]",
    clearance: Annotated[float, Field(ge=0)] = 0.0,
    treat_coincidence_as_interference: bool = False,
    treat_subassemblies_as_components: bool = False,
    include_hidden: bool = False,
    resolve_lightweight: bool = False,
    limit: Annotated[int, Field(ge=1)] = 100,
    compact: bool = False,
    ignore_same_parent: bool = False,
    boxes: bool = False,
    group_by_pair: bool = False,
) -> str:
    """Interference detection in the ACTIVE assembly (read-only: does NOT change state_version,
    never saves).
    components: JSON list of component names or instance paths ("Sub-1/Part-1") to check against
    the parts around them, e.g. a cable bundle or a plug. The neighbours are the unsuppressed,
    visible leaf parts whose bounding box comes within `clearance` of a named component; only
    interferences involving a named component are reported. Empty: SolidWorks checks the whole
    assembly (slow on a large one).
    clearance (m, needs `components`): also report every neighbour closer than this to a named
    component (minimum distance, SolidWorks Measure), e.g. 0.005 for 5 mm.
    treat_coincidence_as_interference: touching faces count (default: they don't, so a plug
    seated on its header is not an interference). treat_subassemblies_as_components: a
    sub-assembly counts as one body. include_hidden: also check hidden components.
    Lightweight components are resolved when named or near a named component (reported as
    resolved_from_lightweight); for a whole-assembly check pass resolve_lightweight=True.
    Returns interference_count, interferences (largest first: components, volume_mm3,
    box_min / box_max of the overlap, m), clearance {required_m, violations [{a, b,
    distance_mm}], nearest_m, nearest_pair}, neighbours_checked, warning when something could not
    be checked, and `clear` (true = no interference, no clearance violation, nothing skipped).
    surface_only_neighbours / surface_only_warning (and clear=false): neighbours with no solid
    body, invisible to Interference Detection; the warning names their solid configuration when
    the model has one (switch with set_component_state(referenced_configuration=...), re-check).
    compact: on a large assembly, report counts instead of the long lists: not_measured_count +
    not_measured_neighbours (pairs Measure could not size, usually surface-only models) and
    resolved_from_lightweight_count. Not compact: also `neighbours` (the instance paths checked)
    and clearance.distances_mm (every pair, null = not measured). A component with several
    solid bodies (e.g. a SOLIDWORKS Routing tube, one body per segment) is measured body to body.
    ignore_same_parent: leave out interferences and clearance violations whose components all
    sit in the same sub-assembly (e.g. a harness's own bundles meeting at their split points);
    reported as ignored_same_parent. boxes=True: `neighbours` as [{component, box_mm}] (world
    bounding boxes, mm: rough keep-out zones when looking for free space).
    group_by_pair=True: `pairs` instead of `interferences`: one entry per component pair (count,
    total_mm3, max_mm3), largest total first, at most `limit` pairs (all interferences are
    counted; a rigid harness touching a tie mount in 40 places is one line). Use it for reviews of
    a whole harness; then look at one pair's interferences without it.
    To test a path BEFORE drawing it, use check_path_clearance."""
    params = {
        "components": components, "clearance": clearance,
        "treat_coincidence_as_interference": treat_coincidence_as_interference,
        "treat_subassemblies_as_components": treat_subassemblies_as_components,
        "include_hidden": include_hidden, "resolve_lightweight": resolve_lightweight,
        "limit": limit, "compact": compact, "ignore_same_parent": ignore_same_parent,
        "boxes": boxes}
    if not group_by_pair:
        return _call("check_interference", params)
    params["limit"] = max(limit, 100000)
    raw, pl = _call_json("check_interference", params)
    items = pl.pop("interferences", None) or []
    pl.pop("interferences_truncated_at", None)
    pl["pairs"] = am.group_interferences(items, limit)
    pl["pair_count"] = len(am.group_interferences(items, len(items) or 1))
    pl["total_mm3"] = round(sum(float(i.get("volume_mm3") or 0) for i in items), 3)
    return _text_with(raw, pl)


# ---------------------------------------------------------------------------
# Tool: component_status
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def component_status(
    components: str = "[]",
    match: str = "",
    state: str = "",
    recursive: bool = True,
    include_mates: bool = False,
    limit: int = 500,
    boxes: bool = False,
    time_budget_s: Annotated[float, Field(ge=0)] = 60.0,
    fields: str = "",
    region: str = "",
    region_frame_component: str = "",
    leaves_only: bool = False,
) -> str:
    """Read-only status of the active assembly's components (does NOT change state_version).
    components: JSON list of component names or instance paths ("Sub-1/Part-1", nested ones
    included); when empty, every component (recursive=False: top level only) is listed, filtered by
    `match` (substring of the instance path or file path) and `state` ('suppressed', 'lightweight',
    'resolved', 'hidden' or 'missing' = file not found). Each entry: component (instance path),
    suppression (suppressed / lightweight / resolved / fully_resolved), file, file_missing,
    configuration, reference (the reference designator a routing from-to import matches),
    hidden, fixed, envelope, parent_suppressed (a suppressed sub-assembly holding it),
    excluded_from_bom, transform (13 numbers, m), with boxes=True box_mm (world bounding box, mm),
    and with include_mates=True its mates: name, type, suppressed, severity / code / meaning when the
    mate has a rebuild error, and the instance paths of the components it references (null =
    reference gone); broken_mates counts the errors. The mate scan reads every mate of the assembly
    and of each parent sub-assembly per component: minutes on a large assembly, so it is off by
    default. time_budget_s (0 = none) stops the listing and the mate scans after that many seconds
    and returns what it has with truncated=true (a named-component mate scan once held SolidWorks
    for 25 minutes; nothing can cancel a running call). counts_by_state summarizes a full listing. Use it to find
    suppressed parts and what they are mated to before set_component_state.
    Limits: from the top assembly, mates are found reliably only when held by the ACTIVE
    assembly (e.g. top-level mates to nested parts); to see the mates a sub-assembly holds, open
    that sub-assembly and run component_status there. `fixed` is reliable for components of the
    active assembly itself (open the sub-assembly for its own components).
    fields: comma-separated keys to keep per component (component is always kept), e.g.
    'transform' or 'suppression,file': a short answer when only positions or states matter.
    region: JSON [x0,y0,z0,x1,y1,z1] (m): keep only components whose world bounding box overlaps
    it (in region_frame_component's coordinates when given, e.g. a sub-assembly 'Sub-1'; its 8
    corners are mapped to the top assembly and their box used). Filtered on the server while
    walking down the tree: sub-assemblies outside the region are not descended, and components
    outside cost one GetBox, so a region over a 1,500-component assembly answers in seconds
    instead of minutes. With region, box_mm is always returned, counts_by_state covers the kept
    components, and the result has region {region, frame, world_box, leaves_only, outside};
    leaves_only=True drops components that have children (sub-assemblies). Component boxes are
    cached per assembly while its update stamp is unchanged (boxes=True or region): box_cache
    'hit' or 'built'. Boxes are approximate (SOLIDWORKS GetBox) and an L-shaped bracket fills
    little of its box: use occupancy_grid for the real free space."""
    params = {
        "components": components, "match": match, "state": state, "recursive": recursive,
        "limit": limit, "include_mates": include_mates, "boxes": boxes,
        "time_budget_s": time_budget_s}
    if region.strip() not in ("", "[]"):
        params["region"] = region
        if region_frame_component.strip():
            params["region_frame_component"] = region_frame_component.strip()
        if leaves_only:
            params["leaves_only"] = True
    keep =[f.strip() for f in fields.replace(";", ",").split(",") if f.strip()]
    if not keep:
        return _call("component_status", params)
    raw, pl = _call_json("component_status", params)
    pl["components"] = [{k: v for k, v in c.items() if k == "component" or k in keep}
                        for c in pl.get("components") or []]
    pl["fields"] = keep
    return _text_with(raw, pl)


# ---------------------------------------------------------------------------
# Tool: occupancy_grid
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def occupancy_grid(
    region: str,
    resolution: Annotated[float, Field(gt=0)] = 0.002,
    frame_component: str = "",
    ignore: str = "[]",
    include_hidden: bool = False,
    dilate: Annotated[float, Field(ge=0)] = 0.0,
    time_budget_s: Annotated[float, Field(ge=0)] = 300.0,
    out_path: str = "",
) -> str:
    """Read-only voxel occupancy of a box region of the active assembly, from the components'
    real tessellated geometry (does NOT change state_version; it may resolve lightweight
    components in the region, listed in resolved_from_lightweight). For route / harness planning:
    bounding boxes overstate sheet-metal brackets, the voxels do not.
    region: JSON [x0,y0,z0,x1,y1,z1] (m), in frame_component's coordinates when given (an
    instance path such as 'Sub-1'; the output is in that frame too), else in the top assembly's.
    resolution: voxel edge (m); more than 8,000,000 voxels fails with GRID_TOO_LARGE.
    ignore: JSON list of instance-path fragments to leave out (e.g. '["Sub-1/"]' for the
    sub-assembly being planned). Hidden components are left out unless include_hidden; suppressed
    ones always. dilate (m): also mark every voxel whose centre lies within this distance of an
    occupied voxel's centre (Euclidean). Leaf parts whose box misses the region are skipped;
    solid AND surface bodies count. A closed solid gives a shell of occupied voxels (flood-fill
    from a free start point). time_budget_s: components left when it runs out are listed in
    not_processed (truncated=true) and show as free.
    Result: {frame ('world' or frame_component), origin [x0,y0,z0] (the region's min corner),
    resolution, dims [nx,ny,nz], occupied_rle [[start, count], ...] sorted runs over the linear
    index i = ix + nx*(iy + ny*iz) (voxel ix spans origin_x + [ix, ix+1]*resolution),
    occupied_count, components [{component, voxels}] (most voxels first, at most 200),
    skipped_lightweight (no box or could not be resolved: their space shows free), triangles,
    elapsed_s}, plus occupied_before_dilate, no_tessellation, tessellation_sources, box_cache.
    out_path: write that full JSON to this file and return it without occupied_rle (the run list
    can be large): use it when a script reads the grid."""
    params = {
        "region": region, "resolution": resolution, "frame_component": frame_component.strip(),
        "ignore": ignore, "include_hidden": include_hidden, "dilate": dilate,
        "time_budget_s": time_budget_s}
    if not out_path.strip():
        return _call("occupancy_grid", params)
    raw, pl = _call_json("occupancy_grid", params)
    path = os.path.abspath(out_path.strip())
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(pl, fh, separators=(",", ":"))
    summary = {k: v for k, v in pl.items() if k != "occupied_rle"}
    summary["runs"] = len(pl.get("occupied_rle") or [])
    summary["components"] = (pl.get("components") or [])[:30]
    summary["out_path"] = path
    return _text_with(raw, summary)


# ---------------------------------------------------------------------------
# Tool: file_dependencies
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def file_dependencies(
    path: str = "",
    recursive: bool = True,
    include_drawings: bool = True,
    read_cloud_only: bool = False,
    show: str = "all",
    limit: int = 1000,
    time_budget_s: Annotated[float, Field(ge=0)] = 120.0,
) -> str:
    """Read-only: the files a part, assembly or drawing references, down the whole tree, WITHOUT
    opening it in SOLIDWORKS (does NOT change state_version; nothing is loaded or written). path:
    the .sldprt / .sldasm / .slddrw file (default: the active document's file). Each file comes from
    SOLIDWORKS' own reference list (GetDocumentDependencies2, resolved with its search rules) and is
    reported with: sync = local | always_keep (pinned in a cloud-synced folder) | cloud_only
    (OneDrive / SharePoint placeholder, downloaded when first read) | missing; read_only_attribute;
    read_only_location (matches SOLIDPILOT_READONLY_PATHS); referenced_by (the documents that
    reference it); depth (0 = path). include_drawings adds the .slddrw with the same name in each
    model's folder (the drawings Pack and Go includes). Cloud-only files are listed but not read
    (their own references stay unknown, counted in unread_cloud_only) unless read_cloud_only=True,
    which downloads them. show: 'all', 'problems' (missing, cloud-only, unread) or 'none' (counts
    and folders only). The result also gives counts, a per-folder summary and the missing paths.
    Use it before opening a large assembly, before Pack and Go, or to check that a copied tree is
    complete; component_status reports the components of an assembly that is already open."""
    return _call("file_dependencies", {
        "path": path, "recursive": recursive, "include_drawings": include_drawings,
        "read_cloud_only": read_cloud_only, "show": show, "limit": limit,
        "time_budget_s": time_budget_s})


# ---------------------------------------------------------------------------
# Tool: set_component_state
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def set_component_state(
    components: str,
    suppression: str = "",
    visible: bool | None = None,
    referenced_configuration: str = "",
    include_mates: bool = False,
    time_budget_s: Annotated[float, Field(ge=0)] = 60.0,
    compact: bool = False,
) -> str:
    """Suppresses, resolves (unsuppresses) or makes lightweight components of the active assembly,
    shows or hides them, and / or sets which configuration of their file they use
    (IComponent2.SetSuppression2, Visible, ReferencedConfiguration). All of it applies to the
    assembly's ACTIVE configuration only (manage_configurations switches it). Increments
    state_version; does NOT save. components: JSON list of names or instance paths
    ("Sub-1/Part-1"); if any is not found nothing changes. suppression: 'suppressed', 'resolved'
    (= unsuppress), 'lightweight' or 'fully_resolved'; visible: true shows, false hides;
    referenced_configuration: a configuration name of the component's own file. A component inside
    a suppressed sub-assembly fails until that sub-assembly is resolved (resolve the parent first).
    Changing a nested component edits its sub-assembly's active configuration, so that
    sub-assembly file is modified too: work on local copies only. Rebuilds, then returns per
    component the change result (ok / failed / bad_state) and its status after the rebuild (as
    component_status). Run check_rebuild before saving: it reports broken mates of the whole tree.
    include_mates=True adds each component's mates to that report: on an assembly of ~1000
    components the mate scan takes minutes per component, bounded by time_budget_s.
    compact=True: counts instead of the per-component lists (changes_count, components_count),
    keeping only the changes that did not succeed (result other than ok, or an error)."""
    params: dict = {"components": components, "suppression": suppression,
                    "include_mates": include_mates, "time_budget_s": time_budget_s}
    if visible is not None:
        params["visible"] = visible
    if referenced_configuration:
        params["referenced_configuration"] = referenced_configuration
    if not compact:
        return _call("set_component_state", params)
    raw, pl = _call_json("set_component_state", params)
    _keep_problems(pl, "changes", lambda c: c.get("result", "ok") == "ok"
                   and not any(k.endswith("error") for k in c))
    pl["components_count"] = len(pl.pop("components", None) or [])
    pl.pop("note", None)
    return _text_with(raw, pl)


# ---------------------------------------------------------------------------
# Tool: manage_configurations
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def manage_configurations(
    action: str = "list",
    name: str = "",
    new_name: str = "",
    description: str = "",
    activate: bool = True,
    suppress_new_components: bool | None = None,
) -> str:
    """Lists or changes the configurations of the active part or assembly. action: 'list'
    (read-only, does NOT change state_version), 'add' (a copy of the active configuration, made
    active unless activate=False), 'activate', 'rename' (name -> new_name), 'delete' (not the active
    one) or 'set' (description / suppress_new_components of `name`, default the active one).
    suppress_new_components=True makes components inserted later in another configuration
    arrive suppressed in this one. Changing actions increment state_version and do NOT save.
    Returns the configurations (name, active, description, parent, suppress_new_components).
    Variants of one assembly (e.g. left / right hand) are configurations: switch with 'activate',
    then suppress or resolve components with set_component_state, and point a sub-assembly
    instance at a configuration with set_component_state(referenced_configuration)."""
    params: dict = {"action": action, "name": name, "new_name": new_name,
                    "activate": activate}
    if description:
        params["description"] = description
    if suppress_new_components is not None:
        params["suppress_new_components"] = suppress_new_components
    return _call("manage_configurations", params)


# ---------------------------------------------------------------------------
# Tool: place_component
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def place_component(
    component: str,
    transform_json: str = "[]",
    fixed: bool | None = None,
    world_transform: str = "",
    world_offset: str = "",
    configurations: str = "",
) -> str:
    """Set the transform and/or the fixed state of an EXISTING component of the active assembly
    (insert_component does this for a component it inserts). Bumps state_version.
    component: the instance name (Name2) from analyze_assembly(components), e.g. 'Cable-1', or an
        instance path ('Sub-1/Part-1'); a suppressed component fails with COMPONENT_SUPPRESSED.
    transform_json: optional full placement, a JSON array STRING of 13 numbers (3x3 rotation
        row-major + translation xyz in meters + scale), as analyze_assembly reports it;
        '[1,0,0,0,1,0,0,0,1,0,0,0,1]' puts the part's origin on the assembly's. A fixed component
        is unfixed to move it and fixed again unless fixed=False.
    fixed: True fixes the component where it is (or where transform_json puts it), False floats
        it; omit to keep its state.
    Mates driving a component still win on rebuild: delete or suppress them to place it freely.
    Returns the component's fixed state and transform.
    Placing in the ACTIVE assembly's frame, also inside a sub-assembly (adapter-side):
    world_transform: JSON 13 numbers, or 3 = new translation (m) keeping the rotation;
    world_offset: JSON [dx,dy,dz] (m): move by this vector (after world_transform if both).
    For a nested component ('Sub-1/Part-1') the target is converted to the parent's frame and set
    in the parent's own document (activated, then the active document again).
    configurations: '' = the parent's active configuration only; 'all' or a JSON list: set in
    each of them. A fixed component keeps one position PER CONFIGURATION, so a move in one
    configuration reverts when another one rebuilds: set every configuration that shows it.
    With configurations alone, transform_json is taken as world_transform.
    Each configuration is read back after all are set (dev_mm: largest difference, 0 = held),
    the parent's active configuration is restored, and world_check_mm compares the position seen
    from the active assembly with the target."""
    use_world = world_transform.strip() or world_offset.strip() or configurations.strip()
    if not use_world:
        params: dict = {"component": component, "transform_json": transform_json}
        if fixed is not None:
            params["fixed"] = fixed
        return _call("place_component", params)
    if not world_transform.strip() and transform_json.strip() not in ("", "[]"):
        world_transform = transform_json
    report = _place_world(component, am.parse_numbers(world_transform, "world_transform"),
                          am.parse_numbers(world_offset, "world_offset"), configurations, fixed)
    return "COMPLETED | place_component | " + json.dumps(report, separators=(",", ":"))


def _config_names(configurations: str) -> list:
    """[None] (active only), every configuration of the active document ('all'), or the list."""
    c = configurations.strip()
    if not c:
        return [None]
    if c.lower() == "all":
        _, pl = _call_json("manage_configurations", {"action": "list", "name": "", "new_name": "",
                                                     "activate": True})
        return [x["name"] for x in pl.get("configurations") or []]
    names = json.loads(c)
    if not isinstance(names, list) or not all(isinstance(n, str) for n in names):
        raise ToolError("configurations: 'all' or a JSON list of configuration names")
    return names


def _activate_config(name: str) -> None:
    _call_json("manage_configurations", {"action": "activate", "name": name, "new_name": "",
                                         "activate": True})


def _place_world(component: str, world_transform, world_offset, configurations: str,
                 fixed) -> dict:
    """Place `component` (instance path of the active assembly) at a world target, in its parent's
    document and in each configuration asked; verify per configuration and from the top."""
    parts = component.split("/")
    parent = "/".join(parts[:-1])
    st = _component_transforms([component] + ([parent] if parent else []))
    current = st[component]["transform"]
    target = am.target_world(current, world_transform, world_offset)
    top_title = (_call_raw("verify_state", {}).get("cadState") or {}).get("activeDocument")
    if parent:
        local = am.xf_mul(target, am.xf_inv(st[parent]["transform"]))
        sub_file = st[parent].get("file") or ""
        if not sub_file:
            raise ToolError(f"place_component: no file for parent '{parent}'")
        _call_json("activate_document", {"title": os.path.basename(sub_file)})
        leaf = parts[-1]
    else:
        local, leaf = target, component
    report: dict = {"component": component, "placed_in": None, "leaf": leaf,
                    "move_mm": round(am.norm(am.sub(target[9:12], current[9:12])) * 1000.0, 4),
                    "target_world": [round(v, 7) for v in target],
                    "local": [round(v, 7) for v in local], "configurations": []}
    try:
        report["placed_in"] = (_call_raw("verify_state", {}).get("cadState") or {}).get("activeDocument")
        cfgs = _config_names(configurations)
        active_before = None
        if cfgs != [None]:
            _, pl = _call_json("manage_configurations", {"action": "list", "name": "",
                                                         "new_name": "", "activate": True})
            active_before = pl.get("active")
        for cfg in cfgs:
            if cfg:
                _activate_config(cfg)
            params = {"component": leaf, "transform_json": json.dumps(local)}
            if fixed is not None:
                params["fixed"] = fixed
            _call_json("place_component", params)
        for cfg in cfgs:
            if cfg:
                _activate_config(cfg)
            got = _component_transforms([leaf])[leaf]["transform"]
            report["configurations"].append({"configuration": cfg or "(active)",
                                             "dev_mm": am.max_dev_mm(got, local)})
        if active_before and cfgs[-1] != active_before:
            _activate_config(active_before)
        report["active_configuration"] = active_before
    finally:
        if parent and top_title:
            _call_json("activate_document", {"title": top_title})
    if parent:
        got = _component_transforms([component])[component]["transform"]
        dev = am.max_dev_mm(got, target)
        if dev > 0.001:
            _call_json("check_rebuild", {"rebuild": True, "recursive": False,
                                         "include_warnings": False, "resolve_lightweight": False,
                                         "force": False, "verbose": False})
            got = _component_transforms([component])[component]["transform"]
            dev = am.max_dev_mm(got, target)
            report["rebuilt_top"] = True
        report["world_check_mm"] = dev
    report["held"] = all(c["dev_mm"] <= 0.001 for c in report["configurations"]) and \
        report.get("world_check_mm", 0) <= 0.001
    return report


def _world_faces(component: str, prefilter: dict | None = None) -> tuple:
    """(faces in the active assembly's coordinates, component transform). prefilter: the
    execution server's cheap face filter (near / within in the active assembly's frame, kind,
    radius_min / max), so a part with thousands of faces answers in seconds."""
    params = {"analysis_type": "faces", "component": component}
    if prefilter:
        params["prefilter"] = json.dumps(dict(prefilter, frame="assembly"))
    _, pl = _call_json("analyze_assembly", params)
    t = pl.get("transform")
    if not isinstance(t, list) or len(t) < 12:
        raise ToolError(f"analyze_assembly gave no transform for '{component}'")
    return am.faces_to_world(pl.get("faces") or [], t), t


@mcp.tool(structured_output=False)
def seat_on_stud(
    component: str,
    stud_component: str,
    face_component: str = "",
    stud_radius_min: Annotated[float, Field(ge=0)] = 0.0,
    stud_radius_max: Annotated[float, Field(ge=0)] = 0.0,
    face_index: int = -1,
    gap: float = 0.0,
    search: Annotated[float, Field(gt=0)] = 0.015,
    face_reach: Annotated[float, Field(gt=0)] = 0.04,
    apply: bool = False,
    configurations: str = "",
) -> str:
    """Seat a ring terminal (or washer, any part with a hole) on a stud of the ACTIVE assembly: its
    hole coaxial with the stud and its contact face on the terminal face. Read-only unless apply.
    component: the ring part (instance path, may be nested, e.g. 'Harness-1/Lug-1');
    stud_component: the part carrying the stud (bolt, terminal); face_component: the part whose
    face the ring sits on (default stud_component; e.g. a busbar under a bolt).
    The hole is the ring's cylinder perpendicular to its largest flat face; the stud is the
    cylinder of stud_component parallel to it with a radius below the hole's (stud_radius_min /
    max in m to choose, e.g. 0.0019-0.0021 for M4), whose axis passes nearest the hole (within
    search, m). The terminal face: a plane of face_component perpendicular to the stud, facing
    the ring, its mid point within face_reach (m) of the stud axis, nearest the ring's contact
    face along the axis (within search); face_index picks another (other_faces lists them with
    offset_mm along the axis and area). gap (m): leave this distance (e.g. a washer under the
    ring: its thickness). Rotation is kept: the hole must already be parallel to the stud.
    Returns delta_mm (the move), stud, hole, face, contact_face, other_faces; apply=True moves it
    (place_component world_offset, in each of `configurations` as there) and adds `placed`."""
    try:
        lug_faces, _ = _world_faces(component)
        centre = am.hole_centre(lug_faces)
        if centre is None:
            raise ValueError("no hole found in the component (cylinder perpendicular to its largest flat face)")
        # Only faces whose box comes within reach of the hole are read in full (a relay model has
        # 2,890 faces: reading them all took 6-10 min per part).
        reach = search + 0.005
        stud_pre = {"near": centre, "within": reach, "kind": "cylinder"}
        if stud_radius_min:
            stud_pre["radius_min"] = stud_radius_min
        if stud_radius_max:
            stud_pre["radius_max"] = stud_radius_max
        stud_faces, _ = _world_faces(stud_component, stud_pre)
        fc = face_component.strip() or stud_component
        face_faces, _ = _world_faces(fc, {"near": centre, "within": reach, "kind": "plane"})
        res = am.seat_on_stud(lug_faces, stud_faces, stud_component, face_faces, fc,
                              stud_radius_min=stud_radius_min, stud_radius_max=stud_radius_max,
                              face_index=face_index, gap=gap, search=search, face_reach=face_reach)
    except ValueError as exc:
        raise ToolError(f"seat_on_stud: {exc}") from exc
    res["component"] = component
    if apply:
        res["placed"] = _place_world(component, None, res["delta"], configurations, None)
    return "COMPLETED | seat_on_stud | " + json.dumps(res, separators=(",", ":"))


# ---------------------------------------------------------------------------
# Tool: replace_component
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def replace_component(
    component: str,
    file_path: str,
    all_instances: bool = True,
    configuration: str = "",
    reattach_mates: bool = True,
) -> str:
    """Replace the file a component of the ACTIVE assembly uses with another part or assembly
    (IAssemblyDoc.ReplaceComponents2), keeping each instance's transform, e.g. a surface-only
    supplier model by a solid one. Bumps state_version; does NOT save (local copies only).
    component: a TOP-LEVEL instance name of the active assembly (Name2, e.g. 'Module-3'); for a
        component inside a sub-assembly, open that sub-assembly (open_document) and replace it
        there, then save it.
    file_path: the replacement .SLDPRT / .SLDASM.
    all_instances: also replace every other instance of the same file at this level.
    configuration: configuration of the new file ('' = matched by name, else its default).
    reattach_mates: let SOLIDWORKS re-attach mates to the new geometry (mates on faces that do
        not exist in the new file break: check_rebuild).
    Mates are re-attached by SOLIDWORKS only where the new file has matching geometry: replacing a
    surface-only model by a re-imported solid one breaks every mate on it (found 2026-11-12: 57
    broken mates). To make such a model solid, use insert_part on the model itself instead.
    Returns replaced instances (with their largest transform change, which should be 0),
    still_old_file (instances not replaced) and moved_count."""
    return _call("replace_component", {
        "component": component, "file_path": file_path, "all_instances": all_instances,
        "configuration": configuration, "reattach_mates": reattach_mates})


# ---------------------------------------------------------------------------
# Tool: insert_part
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def insert_part(
    file_path: str,
    solids: bool = True,
    surfaces: bool = False,
    break_link: bool = True,
    hide_surface_bodies: bool = False,
    configuration: str = "",
) -> str:
    """Insert another part's bodies into the ACTIVE PART at its origin (IPartDoc.InsertPart3;
    local copies only). Bumps state_version; does NOT save.
    Typical use: a supplier model saved as surfaces ('exterior faces') is mated in assemblies by
    those surfaces, but interference checks and face queries see only solids. Insert the solid
    bodies of the same model (open its STEP as an assembly, save_document(assembly_as_part=
    'exterior_components'), check analyze_model(geometry) box_m matches) into it and set
    hide_surface_bodies=True: the mates keep their surfaces, the solids do the checking.
    break_link: the inserted bodies become plain geometry (no reference to file_path).
    Returns the feature, solid / surface body counts before and after, and box_m."""
    return _call("insert_part", {
        "file_path": file_path, "solids": solids, "surfaces": surfaces, "break_link": break_link,
        "hide_surface_bodies": hide_surface_bodies, "configuration": configuration})


# ---------------------------------------------------------------------------
# Tool: edit_bodies
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def edit_bodies(
    action: Literal["delete", "keep", "hide", "show"],
    body_type: Literal["solid", "surface"] = "solid",
    indices: list[int] | None = None,
    names: list[str] | None = None,
    all: bool = False,
    feature_name: str = "",
) -> str:
    """Delete, keep, hide or show bodies of the ACTIVE PART (local copies only). Bumps
    state_version; does NOT save.
    Choose bodies by the indices or names analyze_model(analysis_type='bodies') reports (solid:
    visible solid bodies; surface: all surface bodies, its surface_bodies list), or all=True.
    Re-read the indices after any change: deleting or hiding a solid renumbers the list.
    action='delete' / 'keep': one Delete/Keep Body feature removes the chosen bodies (or all
        others), named feature_name if given. Like any feature it can be suppressed in one
        configuration (edit_feature) to bring the bodies back there, e.g. a light display
        configuration of a heavy supplier model.
    action='hide' / 'show': body visibility only (hidden bodies keep their mates and geometry).
    A body not found fails the call with nothing changed.
    Returns body counts (all and visible) after the change and the active configuration."""
    params: dict = {"action": action, "body_type": body_type, "all": all, "feature_name": feature_name}
    if indices:
        params["indices"] = indices
    if names:
        params["names"] = names
    return _call("edit_bodies", params)


# ---------------------------------------------------------------------------
# Tool: add_mate  (Phase B build tool, ADR-047 B3)
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def add_mate(
    mate_type: Literal["coincident", "concentric", "perpendicular", "parallel",
                       "tangent", "distance", "angle", "lock"],
    a_component: str,
    a_index: int,
    b_component: str,
    b_index: int,
    a_kind: Literal["face", "edge"] = "face",
    b_kind: Literal["face", "edge"] = "face",
    alignment: Literal["aligned", "anti_aligned", "closest"] = "closest",
    flip: bool = False,
    value: float = 0.0,
    keep_on_error: bool = False,
) -> str:
    """Add a mate between two component entities in the ACTIVE assembly.
    Entity selection is INDEX-based (robust — coordinate picks miss real geometry): each side is
    (component name from analyze_assembly(components), kind 'face'|'edge', index from
    analyze_assembly(faces|edges, component=...)).
    value: METERS for distance mates, DEGREES for angle mates (ignored otherwise).
    alignment 'closest' lets SolidWorks pick the nearer solution; the mates reader reports the
    RESOLVED alignment afterwards.
    Components may be nested (instance paths); lightweight ones are resolved first.
    Returns the mate name + both components' post-rebuild transforms — compare against the
    original's readback after EVERY mate (the stop-check discipline).
    Failure: SolidWorks can create a mate and still reject it (e.g. over-defined assembly, AddMate5
    error 5), or a new mate can turn other mates into errors. The tool compares What's Wrong
    before and after: on any such problem it DELETES the new mate, rebuilds and fails with
    MATE_OVER_DEFINED / ADD_MATE_ERROR / MATE_REBUILD_ERROR naming the problems, so the assembly
    is unchanged. A component already located by other mates (or a rigid sub-assembly located
    elsewhere) cannot take another mate: place it with place_component instead.
    keep_on_error=True keeps such a mate and reports it (error, mate_problem, new_problems)."""
    params = {"mate_type": mate_type, "alignment": alignment,
              "a_component": a_component, "a_kind": a_kind, "a_index": a_index,
              "b_component": b_component, "b_kind": b_kind, "b_index": b_index}
    if keep_on_error:
        params["keep_on_error"] = True
    if flip:
        params["flip"] = True
    if mate_type in ("distance", "angle"):
        params["value"] = value
    return _call("add_mate", params)


# ---------------------------------------------------------------------------
# Tool: save_body_as_part  (Phase B — flattened-assembly reconstruction, ADR-048)
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def save_body_as_part(body_index: int, file_path: str) -> str:
    """Extract ONE solid body of the ACTIVE multibody part into its own .SLDPRT file.
    body_index: from analyze_model(analysis_type='bodies'). The body's geometry stays in the
    SOURCE coordinates (recover the instance transform separately); the new part is saved to
    file_path and closed, and the source document is re-activated. Use for flattened assembly
    imports whose per-part files don't exist: one file per DISTINCT body (fingerprint-deduped),
    then instances reference it with recovered transforms."""
    return _call("save_body_as_part", {"body_index": body_index, "file_path": file_path})


# ---------------------------------------------------------------------------
# Tool: create_sketch
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def create_sketch(
    plane: str = "",
    on_face: bool = False,
    face_index: int = -1,
    face_x: float = 0.0,
    face_y: float = 0.0,
    face_z: float = 0.0,
) -> str:
    """Create a new sketch on a plane OR on an existing model face.
    Default (on_face=False): provide plane (e.g. 'Front Plane', 'Top Plane', or a named ref plane like 'Plane1').
    On a model face (on_face=True), pick the face one of two ways:
      • face_index (PREFERRED) — the integer index of the target face from analyze_model(analysis_type='faces').
        Robust: it selects the face directly, so it works on a revolve end-cap / shaft end where a coordinate
        pick is ambiguous (the flat circular face's centre lies on the revolve axis → FACE_NOT_FOUND).
      • face_x/face_y/face_z — a point in METERS lying ON the target planar face (interior, NOT on an edge).
    Use on_face for features on existing geometry (recess/hub/keyway on a part face, a bore on a shaft end);
    plane names cannot reach model faces. face_index takes priority over face_x/y/z when >= 0."""
    params = {"plane": plane, "on_face": on_face,
              "face_x": face_x, "face_y": face_y, "face_z": face_z}
    if face_index >= 0:
        params["face_index"] = face_index
    return _call("create_sketch", params)


# ---------------------------------------------------------------------------
# Tool: add_sketch_entity
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def add_sketch_entity(
    entity_type: Literal["rectangle", "circle", "line", "arc", "arc_center", "ellipse", "spline", "fillet", "chamfer"],
    x1: float = 0.0,
    y1: float = 0.0,
    x2: float = 0.0,
    y2: float = 0.0,
    xm: float = 0.0,
    ym: float = 0.0,
    cx: float = 0.0,
    cy: float = 0.0,
    radius: float = 0.0,
    vx: float = 0.0,
    vy: float = 0.0,
    distance: float = 0.0,
    direction: float = 0.0,
    points: str = "[]",
    construction: bool = False,
) -> str:
    """Add a sketch entity to the active sketch.
    entity_type='rectangle':  uses x1,y1 (first corner) and x2,y2 (opposite corner).
    entity_type='circle':     uses cx,cy (center) and radius.
    entity_type='line':       uses x1,y1 (start) and x2,y2 (end).
    entity_type='arc':        uses x1,y1 (start), x2,y2 (end), xm,ym (mid-arc point) — a 3-point arc.
    entity_type='arc_center': uses cx,cy (exact center), x1,y1 (start), x2,y2 (end) and optional
                              direction. PREFER this over 'arc' whenever the exact centre/radius are
                              known: it guarantees the radius, and the 3-point fit is unreliable on
                              shallow or near-collinear arcs.
    entity_type='ellipse':    uses cx,cy (center), x1,y1 (a point on the MAJOR axis), x2,y2 (a point
                              on the MINOR axis).
    entity_type='spline':     uses points — a JSON array STRING of flat through-points
                              '[x1,y1,x2,y2,...]' (>= 2 points), round-tripped exactly.
    entity_type='fillet':     uses vx,vy (vertex to round) and radius.
    entity_type='chamfer':    uses vx,vy (vertex to cut) and distance.
    direction: only for 'arc_center' — sweep sense from start to end. OMIT IT (or pass 0) for the
               MINOR (<=180°) arc, which a corner round virtually always is; a wrong explicit sign
               SILENTLY draws the >180° complement. Pass +1 (CCW) or -1 (CW) only for a deliberate
               major sweep (a round-trip replays analyze's 'dir' verbatim).
    construction: True makes the entity CONSTRUCTION/reference geometry — it guides the profile but
               adds no edge. Mirrors analyze_model's per-segment 'construction' flag.

    This tool MIRRORS analyze_model 1:1 — every segment it emits (line, arc/circle, ellipse, spline,
    with cx/cy/x1/y1/x2/y2/radius/points/construction, and 'dir' on partial arcs) maps onto an
    entity_type here, so an analyzed sketch rebuilds without dropping or simplifying a curve.
    On COMPLETED the result carries result_geometry: what SolidWorks actually created, READ BACK
    rather than echoed — check the radius/endpoints against your intent before moving on.
    Rebuilding from exact coordinates: do NOT add coincident constraints between segments. Shared
    endpoint coordinates already close the profile, and a constraint that fails rolls the sketch back.
    All coordinates in METERS."""
    return _call(
        "add_sketch_entity",
        {
            "entity_type": entity_type,
            "x1": x1, "y1": y1, "x2": x2, "y2": y2,
            "xm": xm, "ym": ym,
            "cx": cx, "cy": cy, "radius": radius,
            "vx": vx, "vy": vy,
            "distance": distance,
            "direction": direction,
            "points": json.loads(points) if points else [],
            "construction": construction,
        },
    )


# ---------------------------------------------------------------------------
# Tool: add_sketch_entities (batch)
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def add_sketch_entities(segments: str) -> str:
    """Add MANY sketch entities to the active sketch in ONE call (the batch form of
    add_sketch_entity — prefer this whenever a profile has more than ~3 segments).
    segments: a JSON array STRING of entity records, each record = add_sketch_entity's
    parameters for one entity, e.g.
      '[{"entity_type":"line","x1":0,"y1":0,"x2":0.05,"y2":0},
        {"entity_type":"arc_center","cx":0.05,"cy":0.005,"x1":0.05,"y1":0,"x2":0.055,"y2":0.005},
        {"entity_type":"circle","cx":0.02,"cy":0.02,"radius":0.004,"construction":true}]'
    Corner rounds / fillet arcs: use arc_center WITHOUT "direction" — the tool then draws the
    MINOR (<=180°) arc deterministically (an explicit wrong sign silently draws the 270°
    complement); pass direction ±1 only for a deliberate major arc.
    Supported entity_type values and their parameters are IDENTICAL to add_sketch_entity
    (rectangle/circle/line/arc/arc_center/ellipse/spline/fillet/chamfer; per-record
    'construction': true supported; one difference: a 'spline' record's "points" is a plain
    JSON array [x1,y1,x2,y2,...], not a nested string). Segments are created in array order —
    list a profile's segments in contour order with EXACT shared endpoints (no coincident
    constraints needed).
    NOT transactional: on the first failing record the call returns FAILED naming segments[i]
    and how many earlier records were created — those remain in the sketch.
    On COMPLETED, result_geometry echoes {segment_count, counts per entity_type} (compact —
    NO per-segment geometry echo; read back with analyze_model(sketch, name=...) if needed).
    All coordinates in document units (meters)."""
    return _call("add_sketch_entities", {"segments": json.loads(segments) if segments else []})


# ---------------------------------------------------------------------------
# Tool: add_sketch_constraint
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def add_sketch_constraint(
    constraint_type: Literal[
        "horizontal", "vertical", "coincident", "parallel",
        "perpendicular", "tangent", "equal", "midpoint",
    ],
    px1: float,
    py1: float,
    px2: float = 0.0,
    py2: float = 0.0,
    entity_type1: Literal["SKETCHSEGMENT", "SKETCHPOINT"] = "SKETCHSEGMENT",
    entity_type2: Literal["SKETCHSEGMENT", "SKETCHPOINT"] = "SKETCHSEGMENT",
) -> str:
    """Apply a geometric constraint to sketch entities in the active sketch.
    constraint_type: 'horizontal', 'vertical' — single entity (px1, py1 only).
    constraint_type: 'coincident', 'parallel', 'perpendicular', 'tangent', 'equal', 'midpoint' — requires two entities (px2, py2).
    px1/py1: point on the first entity. px2/py2: point on the second entity (two-entity constraints).
    entity_type1/2: 'SKETCHSEGMENT' (default) or 'SKETCHPOINT' for point-based constraints like coincident/midpoint.
    All coordinates in document units (meters)."""
    return _call(
        "add_sketch_constraint",
        {
            "constraint_type": constraint_type,
            "px1": px1, "py1": py1,
            "px2": px2, "py2": py2,
            "entity_type1": entity_type1,
            "entity_type2": entity_type2,
        },
    )


# ---------------------------------------------------------------------------
# Tool: add_dimension
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def add_dimension(px: float, py: float, value: Annotated[float, Field(gt=0, description="Dimension value in METERS (e.g. 0.05 = 50mm)")], label_offset_x: float = 0.0, label_offset_y: float = -0.015) -> str:
    """Apply a smart dimension to the sketch segment nearest to point (px, py).
    px/py should be on or very near the target segment — e.g. the midpoint of a line.
    label_offset_x/y control where the dimension label is placed relative to the segment point.
    Works for any sketch geometry (rectangles, polygons, complex profiles)."""
    return _call(
        "add_dimension",
        {"px": px, "py": py, "value": value, "label_offset_x": label_offset_x,
            "label_offset_y": label_offset_y},
    )


# ---------------------------------------------------------------------------
# Tool: extrude_feature
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def extrude_feature(
    depth: Annotated[float, Field(
        ge=0, description="Extrusion depth in METERS (required > 0 for boss/cut)")] = 0.0,
    feature_type: Literal["boss", "cut", "revolve", "sweep", "loft"] = "boss",
    angle: Annotated[float, Field(
        gt=0, le=360, description="Revolve angle in DEGREES")] = 360.0,
    axis_x1: float = 0.0,
    axis_y1: float = 0.0,
    axis_x2: float = 0.0,
    axis_y2: float = 0.001,
    path_sketch: str = "",
    profiles: str = "[]",
    reverse: bool = False,
    through: bool = False,
    up_to_face_index: int = -1,
    mid_plane: bool = False,
) -> str:
    """Extrude/feature the active sketch profile.
    feature_type='boss' (default): solid extrusion, requires depth.
    feature_type='cut': material removal, requires depth and existing solid body.
    feature_type='revolve': solid of revolution — angle in DEGREES (default 360 = full revolve); axis defined by axis_x1/y1 to axis_x2/y2 (midpoint of that segment selects the centerline).
    feature_type='sweep': sweeps profile along a path — requires path_sketch (name of the path sketch).
    feature_type='loft': lofts through multiple profiles — requires profiles (JSON array of sketch names, e.g. '[\"Sketch1\",\"Sketch2\"]').
    END CONDITION (boss/cut) — pick ONE; depth is required only for BLIND or mid_plane:
      through=True          through-all; depth ignored. Use it instead of guessing a depth.
      up_to_face_index >= 0 terminates exactly ON that model face (index from
                            analyze_model('faces'), same indexing as create_sketch face_index);
                            depth ignored. -1 (default) = off.
      mid_plane=True        SYMMETRIC about the sketch plane; depth is the TOTAL width.
    reverse (boss/cut): flip the direction — needed when a cut/boss sketched on a part FACE has its
        material on the opposite side from the default. DIRECTION IS THE SILENT ONE: a feature built
        on the wrong side leaves volume, area and topology identical (recipe R6b/R14), so bind the
        sketch plane to a face explicitly and read one coordinate back afterwards.
    Sheet metal: a 'cut' on a sheet-metal body (holes after a bend included) is handled
        automatically — Normal Cut, inserted before the Flat-Pattern; no special params.
    On COMPLETED the result carries result_geometry {volume, faces, edges} after the feature — verify
        the step from that rather than a separate analyze_model.
    Exits sketch mode automatically before executing."""
    return _call(
        "extrude_feature",
        {
            "depth": depth,
            "feature_type": feature_type,
            "angle": angle,
            "axis_x1": axis_x1, "axis_y1": axis_y1,
            "axis_x2": axis_x2, "axis_y2": axis_y2,
            "path_sketch": path_sketch,
            "profiles": profiles,
            "reverse": reverse,
            "through": through,
            "up_to_face_index": up_to_face_index,
            "mid_plane": mid_plane,
        },
    )


# ---------------------------------------------------------------------------
# Tool: create_rib
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def create_rib(
    thickness: Annotated[float, Field(gt=0, description="Rib thickness in METERS (e.g. 0.005 = 5mm)")],
    two_sided: bool = True,
    reverse_thickness_dir: bool = False,
    reverse_material_dir: bool = False,
    is_norm_to_sketch: bool = False,
) -> str:
    """Create a RIB from the ACTIVE sketch (consumes it, like extrude_feature). The rib profile
    is OPEN geometry — typically a single line (e.g. the diagonal between an L-bracket's legs);
    SolidWorks extends it to the surrounding walls and thickens it.
    thickness: rib thickness in METERS.
    two_sided: True (default) thickens symmetrically on both sides of the sketch; False = single
        side (then reverse_thickness_dir picks which side).
    reverse_material_dir: flip which side of the profile the rib material FILLS toward. Wrong
        direction is auto-recovered: if no feature results, the tool retries flipped (your
        explicit value is tried first).
    is_norm_to_sketch: False (default) = extrusion parallel to the sketch (the classic line-rib
        on a mid/symmetry plane); True = normal to the sketch.
    Draft is deliberately not exposed (grow on demand). On COMPLETED the result includes
    result_geometry {volume, faces, edges} — verify the step from this. Requires an existing
    solid body for the rib to attach to."""
    return _call(
        "create_rib",
        {
            "thickness": thickness,
            "two_sided": two_sided,
            "reverse_thickness_dir": reverse_thickness_dir,
            "reverse_material_dir": reverse_material_dir,
            "is_norm_to_sketch": is_norm_to_sketch,
        },
    )


# ---------------------------------------------------------------------------
# Tool: add_edge_feature
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def add_edge_feature(
    feature_type: Literal["fillet", "chamfer"],
    radius_or_distance: Annotated[float, Field(gt=0, description="Fillet radius / chamfer FIRST-face setback (D1) in METERS (e.g. 0.01 = 10mm)")],
    edge_indices: str = "",
    ex: float = 0.0,
    ey: float = 0.0,
    ez: float = 0.0,
    edges_json: str = "[]",
    chamfer_type: Literal["distance_angle", "distance_distance"] = "distance_angle",
    angle: Annotated[float, Field(gt=0, lt=90, description="Chamfer angle in DEGREES (distance_angle mode only; default 45)")] = 45.0,
    distance2: Annotated[float, Field(ge=0, description="Chamfer SECOND-face setback (D2) in METERS (distance_distance mode only; must be > 0 there)")] = 0.0,
    chamfer_flip: bool = False,
) -> str:
    """Apply a 3D edge modifier to solid body edges (post-extrusion). Distinct from sketch fillet/chamfer.
    feature_type='fillet': rounds selected edge(s) with given radius_or_distance.
    feature_type='chamfer': cuts a chamfer on selected edge(s). Two modes via chamfer_type:
      - 'distance_angle' (default): radius_or_distance = setback (D1), angle = the chamfer angle in DEGREES
        (default 45 → the classic equal 45° chamfer). angle is ignored in the other mode.
      - 'distance_distance': radius_or_distance = first-face setback (D1), distance2 = second-face setback
        (D2, METERS, required > 0). A distance-distance chamfer is DIRECTIONAL — if D1/D2 land on the wrong
        faces (the chamfer leans the wrong way), set chamfer_flip=True to swap the sides (FlipDirection).
    PREFERRED edge selection: edge_indices — a JSON array of integer indices from analyze_model(analysis_type='edges'),
    e.g. '[3,5]'. This selects edges directly (no coordinate pick), so it works on crowded or CONCAVE edges
    (inner-corner / small-radius step edges) that a coordinate pick can't disambiguate. Takes priority over
    edges_json / ex-ey-ez.
    Fallback — single edge: ex/ey/ez (3D point on the edge); multiple edges: edges_json e.g. '[{\"ex\":0.05,\"ey\":0.05,\"ez\":0.05}]'.
    All coordinates/distances in METERS. Requires an active part document with an existing solid body."""
    params = {
        "feature_type": feature_type,
        "radius_or_distance": radius_or_distance,
        "ex": ex, "ey": ey, "ez": ez,
        "edges_json": edges_json,
    }
    if edge_indices:
        params["edge_indices"] = edge_indices
    if feature_type == "chamfer":
        params["chamfer_type"] = chamfer_type
        params["angle"] = angle
        params["distance2"] = distance2
        params["chamfer_flip"] = chamfer_flip
    return _call("add_edge_feature", params)


# ---------------------------------------------------------------------------
# Tool: create_drawing
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def create_drawing(model_path: str = "") -> str:
    """Open a new SolidWorks drawing document (A3 sheet, 1:1 scale).
    model_path: optional path to the part/assembly to reference. If omitted, an empty drawing is created.
    Note: add_drawing_view requires the referenced part to be saved to disk."""
    return _call("create_drawing", {"model_path": model_path})


# ---------------------------------------------------------------------------
# Tool: add_drawing_view
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def add_drawing_view(
    view_type: Literal["front", "top", "right", "isometric", "back", "bottom", "left"],
    pos_x: float = 0.1,
    pos_y: float = 0.1,
    scale: Annotated[float, Field(
        gt=0, description="View scale (1.0 = 1:1)")] = 1.0,
    model_path: str = "",
    display_mode: Literal["", "hlv", "hlr", "wireframe", "shaded", "shaded_edges", "default"] = "",
) -> str:
    """Add a model view to the active drawing sheet.
    view_type: 'front', 'top', 'right', 'isometric', 'back', 'bottom', 'left'.
    pos_x/pos_y: position on the drawing sheet in meters.
    scale: view scale (default 1.0 = 1:1).
    model_path: path to the part file; if omitted, uses the first open part document.
    display_mode: view display style — 'hlv' (Hidden Lines Visible), 'hlr' (Hidden Lines Removed),
        'wireframe', 'shaded', 'shaded_edges', or 'default' (document default). Leave '' to apply the
        DRAFTING-CONVENTION default: orthographic views (front/top/right/back/bottom/left) become HLV
        (hidden lines shown for reference — but never dimension to them), isometric keeps the document
        default. Section views (add_section_view) are separate and show the cut, not hidden lines.
    Requires the part to be saved to disk (in-memory parts cannot be projected)."""
    return _call(
        "add_drawing_view",
        {"view_type": view_type, "pos_x": pos_x, "pos_y": pos_y,
            "scale": scale, "model_path": model_path, "display_mode": display_mode},
    )


# ---------------------------------------------------------------------------
# Tool: add_flat_pattern_view
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def add_flat_pattern_view(
    pos_x: float = 0.1,
    pos_y: float = 0.1,
    scale: Annotated[float, Field(
        gt=0, description="View scale (1.0 = 1:1)")] = 1.0,
    model_path: str = "",
    config_name: str = "",
    hide_bend_lines: bool = False,
    flip_view: bool = False,
) -> str:
    """Add a FLAT PATTERN view of a SHEET-METAL part to the active drawing — the unfolded blank with
    bend lines/notes. Sheet metal is dimensioned on its FLAT PATTERN (blank size, hole positions,
    bend lines), not on the folded orthographic views, so use this rather than them; then call
    auto_dimension_drawing.

    pos_x/pos_y: position on the drawing sheet in meters.
    scale: view scale (default 1.0 = 1:1).
    model_path: path to the sheet-metal part; if omitted, uses the first open part document.
    config_name: configuration to flatten; if omitted, the part's active configuration is used
        (falling back to 'Default').
    hide_bend_lines: hide the bend lines in the flat pattern (default False).
    flip_view: flip the flat pattern view (default False).

    The part must be sheet metal (have a Flat-Pattern feature) and saved to disk, else returns
    FLAT_PATTERN_VIEW_FAILED. Bumps state_version; result_geometry echoes {view_name, config}."""
    return _call(
        "add_flat_pattern_view",
        {"pos_x": pos_x, "pos_y": pos_y, "scale": scale, "model_path": model_path,
            "config_name": config_name, "hide_bend_lines": hide_bend_lines,
            "flip_view": flip_view},
    )


# ---------------------------------------------------------------------------
# Tool: add_drawing_dimension
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def add_drawing_dimension(
    px: float,
    py: float,
    value: Annotated[float, Field(gt=0, description="Dimension value in METERS")],
    label_offset_x: float = 0.0,
    label_offset_y: float = -0.015,
) -> str:
    """Add a smart dimension to a drawing view segment nearest to point (px, py).
    Works the same way as add_dimension but operates in a drawing document context.
    px/py should be on or very near the target segment in drawing sheet coordinates (meters).
    Requires an active drawing document."""
    return _call(
        "add_drawing_dimension",
        {"px": px, "py": py, "value": value, "label_offset_x": label_offset_x,
            "label_offset_y": label_offset_y},
    )


# ---------------------------------------------------------------------------
# Tool: auto_dimension_drawing
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def auto_dimension_drawing(
    all_views: bool = True,
    include_unmarked: bool = False,
    eliminate_duplicates: bool = True,
) -> str:
    """Transfer the MODEL's driving dimensions into the active drawing's views (SolidWorks 'Insert
    Model Items > Dimensions'). PREFER this over add_drawing_dimension's coordinate pick — the
    values come from the model's own parametric dimensions and are placed for you. Call it AFTER
    create_drawing + add_drawing_view(s), then verify with analyze_slddrw_test (dimension_count > 0,
    values matching the model).

    all_views: insert into all drawing views (True) or only the currently selected view (False). Default True.
    include_unmarked: also insert driving dimensions NOT marked for drawing (i.e. ALL driving dims), not just
        those flagged 'marked for drawing'. More complete but noisier. Default False — if a marked-only pass
        inserts 0 dimensions (the part's dims weren't marked for drawing), re-call with include_unmarked=True.
    eliminate_duplicates: avoid inserting the same model dimension into more than one view. Default True.

    Returns COMPLETED with result_geometry.inserted_count (the number of dimensions placed; 0 is a valid
    outcome). Requires an active drawing with at least one model view. Bumps state_version."""
    return _call(
        "auto_dimension_drawing",
        {"all_views": all_views, "include_unmarked": include_unmarked,
            "eliminate_duplicates": eliminate_duplicates},
    )


# ---------------------------------------------------------------------------
# Tool: auto_center_marks
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def auto_center_marks(
    include_slots: bool = True,
    extended_lines: bool = True,
) -> str:
    """Automatically place center marks (with extended centerlines) on every hole/slot in every
    model view of the active drawing (SolidWorks 'Auto Insert > Center Marks'). This is the robust,
    automatic way to add centerlines to a holed part's drawing — no coordinate picking. Call it
    after the views exist (create_drawing + add_drawing_view), typically alongside
    auto_dimension_drawing, for a difficulty-2-grade drawing of a bracket/flange with holes.

    include_slots: also add slot center marks/centerlines for linear & arc slots (not just round holes). Default True.
    extended_lines: draw the extended centerlines through each hole rather than a small cross. Default True.

    Returns COMPLETED with result_geometry.center_marks (total marks placed across all views; 0 if the
    part has no holes/slots). Bumps state_version. Requires an active drawing with model views."""
    return _call(
        "auto_center_marks",
        {"include_slots": include_slots, "extended_lines": extended_lines},
    )


# ---------------------------------------------------------------------------
# Tool: add_hole_callout
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def add_hole_callout(px: float, py: float) -> str:
    """Insert a hole callout (e.g. '4× Ø8 THRU') on the hole edge nearest the sheet point (px, py)
    in the active drawing. Coordinate-based selection — same fragile point pick as add_drawing_dimension
    (KNOWN-LIMITATIONS #6), so place px/py right on the hole's projected circular edge. For centerlines
    prefer auto_center_marks (robust/automatic); use this for explicit per-hole callouts where wanted.
    px/py: a point on the target hole's projected circle, in drawing sheet coordinates (meters).
    Requires an active drawing document. Bumps state_version."""
    return _call("add_hole_callout", {"px": px, "py": py})


# ---------------------------------------------------------------------------
# Tool: add_section_view
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def add_section_view(
    px: float,
    py: float,
    edge_x: Optional[float] = None,
    edge_y: Optional[float] = None,
    x1: Optional[float] = None,
    y1: Optional[float] = None,
    x2: Optional[float] = None,
    y2: Optional[float] = None,
    label: str = "A",
    flip: bool = False,
    scale: float = 0.0,
) -> str:
    """Create a SECTION VIEW that cuts the model so internal/blind features become visible, dimensionable
    edges. Use this when a feature's size can't be shown in a projected view — most importantly a BLIND
    POCKET's DEPTH (its floor is a hidden edge in an orthographic view, so auto_dimension_drawing can't
    place the depth).

    Two ways to define the cut (provide ONE pair-set):
    - EDGE mode (edge_x, edge_y): cut collinear with an existing straight edge already projected in
      a view — point at it.
    - LINE mode (x1,y1 → x2,y2): draw the cut line, for cutting THROUGH a feature's interior where
      no edge exists. It must fully cross the view through the feature.
    All coordinates are drawing sheet coordinates in meters (KNOWN-LIMITATIONS #6 — coordinate-based).

    px,py: where to place the section view (meters) — also picks which side the section projects toward.
    label: section label (default 'A' → 'SECTION A-A'). flip: reverse the cut/viewing direction.
    scale: optional section view scale; inherits the parent view's scale if 0/omitted.

    After the section lands, call auto_dimension_drawing or add_drawing_dimension to dimension the
    now-visible depth. Bumps state_version. Requires an active drawing with a parent model view. If it
    returns SECTION_VIEW_FAILED, the drawing state may be wedged by prior failed attempts — a fresh
    server/drawing state clears it."""
    params: dict = {"px": px, "py": py, "label": label, "flip": flip}
    for k, v in (("edge_x", edge_x), ("edge_y", edge_y),
                 ("x1", x1), ("y1", y1), ("x2", x2), ("y2", y2)):
        if v is not None:
            params[k] = v
    if scale and scale > 0:
        params["scale"] = scale
    return _call("add_section_view", params)


# ---------------------------------------------------------------------------
# Tool: export_document
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def export_document(format: Literal["STEP", "IGES", "STL", "PDF", "DWG", "DXF"], file_path: str) -> str:
    """Export the active SolidWorks document to a file. The document remains open after export.
    format: 'STEP', 'IGES', 'STL', 'PDF', 'DWG', 'DXF'.
    file_path: full output path including filename and extension (e.g. 'C:/output/part.step').
    Note: DWG and DXF require an active drawing document (use create_drawing first).
    PDF uses SolidWorks PDF export options. STEP/IGES/STL work with part or assembly documents.
    IGES must use a .igs extension (.iges is auto-corrected to .igs)."""
    # export does NOT change CAD state; the execution layer returns the same state_version.
    return _call("export_document", {"format": format, "file_path": file_path})


# ---------------------------------------------------------------------------
# Tool: batch_export
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def batch_export(file_path_base: str, formats_json: str) -> str:
    """Export the active document to multiple formats in one call.
    file_path_base: output path without extension (e.g. 'C:/output/mypart').
    formats_json: JSON array of format strings (e.g. '[\"STEP\",\"STL\",\"PDF\"]').
    Each format is exported as file_path_base.<ext>. DWG/DXF skipped if active doc is not a drawing.
    Partial success is reported in the response Features list."""
    return _call("batch_export", {"file_path_base": file_path_base, "formats_json": formats_json})


# ---------------------------------------------------------------------------
# Tool: verify_state
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def verify_state() -> str:
    """Read and return the current CAD state without modifying it."""
    return _call("verify_state", {})


# ---------------------------------------------------------------------------
# Tool: close_document
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def close_document(save: bool = False, close_all: bool = False) -> str:
    """Close the active SolidWorks document. Set save=True to save before closing.
    close_all=True closes ALL open documents (incl. invisibly-loaded component docs that a
    per-document close loop can never reach), DISCARDING unsaved changes — use to fully reset
    the document space between assembly jobs."""
    params = {"save": save}
    if close_all:
        params["close_all"] = True
    return _call("close_document", params)


# ---------------------------------------------------------------------------
# Tool: save_document
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def save_document(
    file_path: str = "",
    assembly_as_part: Literal["", "all_components", "exterior_components", "exterior_faces"] = "",
    save_references: bool = True,
    reference_root: str = "",
    force: bool = False,
    documents: str = "[]",
    verbose: bool = False,
) -> str:
    """Save the active SolidWorks document to disk.
    file_path: full output path including the document extension
    (.sldprt for parts, .sldasm for assemblies, .slddrw for drawings).
    If omitted, saves in place (only works if the document was saved before).
    A part must be saved to disk before it can be referenced by a drawing view.
    save_references (in-place save of an ASSEMBLY, default True): first save every modified
    document it references (parts, then sub-assemblies): route sub-assemblies and connector
    parts after a routing import, components rebuilt or resolved by checks. SOLIDWORKS does not
    write them with the assembly. Only files under reference_root (default: the assembly's
    folder tree); files outside it are listed in skipped_references, never saved.
    Read-only locations (env SOLIDPILOT_READONLY_PATHS, ';'-separated path fragments, e.g.
    released CAD in a synced library) are never written: SAVE_REFUSED.
    An in-place save of a document SOLIDWORKS does not mark as modified writes nothing
    (unchanged=true; the file would otherwise show as changed in version control); force=True
    writes it anyway.
    documents: JSON list of open documents (full paths, titles or file names) to save INSTEAD:
    exactly those, parts before assemblies, nothing else (not the active document unless listed,
    not the other modified references; unmodified ones are skipped unless force). Use it when a
    rebuild or a check marked many references modified that the task did not change (a forced
    rebuild of a large assembly made one save rewrite ~140 unchanged files). Returns {saved,
    skipped [{file, reason}], not_open, unsaved_open_documents (count), unsaved_open_documents_list}.
    Returns the saved path, then JSON {saved, unchanged, saved_references (count),
    saved_references_list, skipped_references, unsaved_open_documents (count),
    unsaved_open_documents_list}: lists of more than 20 are cut to 20 unless verbose=True. Documents
    still modified in the session (e.g. a top assembly whose components were resolved by checks):
    save or knowingly leave them, never ignore.
    assembly_as_part (active ASSEMBLY, file_path ending .sldprt): save a COPY of the assembly as
    one part (the assembly stays active and unchanged): 'exterior_components' = solid bodies of
    the components visible from outside (light and still solid: suppress internal components
    such as cells first), 'all_components' = solid bodies of every resolved component,
    'exterior_faces' = surfaces only (lightest, but interference checks, face queries and mates
    cannot use them). Returns saved_part and its size."""
    return _call("save_document", {"file_path": file_path, "assembly_as_part": assembly_as_part,
                                   "save_references": save_references,
                                   "reference_root": reference_root, "force": force, "documents": documents, "verbose": verbose})


# ---------------------------------------------------------------------------
# Tool: analyze_model
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def analyze_model(
    analysis_type: Literal["mass_properties", "geometry", "bodies", "edges", "faces", "features", "sketch", "feature_map"],
    name: str = "",
    from_feature: str = "",
    to_feature: str = "",
    near: str = "",
    k: int = 0,
    axis: str = "",
) -> str:
    """Analyze the active SolidWorks part document (read-only, does NOT change state).
    analysis_type='mass_properties': returns volume, surface_area, center of gravity (cx, cy, cz) in document units.
    analysis_type='geometry': returns bodies, faces, edges, vertices counts of all solid bodies,
    surface_bodies (count; surface bodies are invisible to faces / edges / interference) and
    box_m (part bounding box incl. surfaces, [xmin,ymin,zmin,xmax,ymax,zmax]: compare frames).
    analysis_type='bodies': PER-BODY fingerprints for multibody parts (e.g. a flattened assembly
        STEP import): volume, area, centroid, edge/vertex counts, and the body's face-index RANGE
        in the same enumeration 'faces' walks — segment the faces list per body with it — and box
        ([xmin,ymin,zmin,xmax,ymax,zmax], m). Surface bodies follow in surface_bodies (name,
        visible, face_count, area, box; no face range): in a model saved as "exterior faces"
        they show which components reach the outside and how many of their faces are visible.
    analysis_type='edges': returns a JSON object listing EVERY solid edge with its start/end/MIDPOINT 3D coords
        in meters (rounded to 6 decimals; closed/circular edges also report length) and a stable index `i`. Use
        the index `i` for add_edge_feature(edge_indices=...) — robust for crowded/concave edges — or a MIDPOINT
        for coordinate-based selection (e.g. edge_flange's ex/ey/ez). On a very large part (many hundreds of edges)
        this is the slowest mode; prefer 'features' for a general understanding. TARGETED (recommended when you
        already know roughly WHERE): pass near='[x,y,z]' (+ optional k, axis) to get only the k nearest edges to
        a point instead of the full dump — see near/k/axis below.
    analysis_type='faces': returns a JSON object listing EVERY solid face with a stable index `i`; planar faces
        also carry normal, area, and a representative on-plane point (meters, rounded to 6 decimals). Use the
        index `i` for create_sketch(on_face=True, face_index=i) — robust where a coordinate pick is ambiguous
        (e.g. a revolve end-cap / shaft end whose centre lies on the axis). TARGETED: pass near='[x,y,z]'
        (+ optional k, axis) for only the k nearest faces to a point — see near/k/axis below.
    analysis_type='features': the part's COMPACT RECIPE — this is the default "understand the part" read. Returns
        the ordered feature tree (name/type; suppressed shown only when true), each feature's driving dimensions
        (deduped, SI meters/radians, rounded to 6 decimals), a per-sketch SUMMARY (segment counts + an inline
        {cx,cy,r} for single-full-circle profiles), pattern semantics (instances / spacing_deg / distinct_instances
        / wraps), and equations / globals. Each sketch reports its plane as {ref, offset} where ref is the
        canonical English default plane ('Front Plane'/'Top Plane'/'Right Plane') and offset is the signed
        distance along its normal (offset 0 = that default plane itself; offset != 0 = a parallel face/plane
        at that height — sketch on the face there). Each extrude/cut reports extrude:{end (blind/through_all/
        ...), depth (blind only), reversed (present only when the direction is flipped)} so you know HOW it
        was built; for direction use the sketch's plane.offset (offset != 0 ⇒ a face, so a cut goes into the
        part). Features are listed in tree (history) ORDER — reproduce them in that exact order (order
        matters wherever features overlap). It does NOT dump every sketch segment's coordinates — that keeps the payload small
        and is enough to understand the part and build dimension/pattern variants. Use this first.
    analysis_type='sketch' (requires name=...): ONE sketch's FULL geometry — every segment's coordinates (rounded
        to 6 decimals) plus its plane. Use this only when you actually need exact sketch geometry (e.g. to reproduce
        an irregular profile); get the sketch name from the 'features' read first. name = the sketch feature name,
        e.g. 'Sketch2'.
    analysis_type='feature_map': per-feature geometry ATTRIBUTION — deterministically answers "which edges/faces
        did each feature act on?" (e.g. WHICH edge a fillet/chamfer was applied to — never guess an anchor). Walks
        the tree base→end with the rollback bar and diffs the topology between stops INSIDE the tool; returns one
        compact JSON {feature_count, map:[{feature, type, delta:{faces,edges,vertices}, consumed_edges:[{mid,len}],
        created_faces:[{point, normal?, planar, area}]}]} (6-decimal meters). consumed_edges = edges that existed
        BEFORE the feature and were consumed by it (both endpoints gone; merely trimmed neighbours excluded) — use
        a consumed edge's `mid` as the fillet/chamfer anchor `near` point (it references the PRE-feature geometry,
        exactly what the IR anchor needs). created_faces = genuinely new surfaces (trimmed existing planes excluded).
        Sketches/planes are listed without a delta; suppressed features are skipped. Optional from_feature/to_feature
        (tree names) limit the walk to a range. NON-DESTRUCTIVE: the rollback bar is restored to the end, nothing is
        saved — but it does rebuild the model feature-by-feature, so it is the slowest mode on big trees.

    near / k / axis (edges and faces modes ONLY — a TARGETED read instead of the full dump): when you already
        know approximately where the geometry is (the common build-time case — you almost always do), pass
        near='[x,y,z]' (a JSON string, meters) to return only the nearest entities to that point, each annotated
        with `dist` (meters) and still carrying its STABLE full-enumeration index `i` (so selection is unchanged).
        k (default 0 = no cap): keep only the k nearest. axis='[x,y,z]' (optional JSON string): keep only entities
        whose direction is ~parallel to axis — for faces that's the planar-face NORMAL (e.g. axis='[0,0,1]' → only
        faces on +Z planes), for edges the chord direction. The response adds total_edge_count/total_face_count so
        you know how many were filtered out. Omitting near preserves the exact historical full-dump behavior.
    Does NOT increment state_version."""
    return _call("analyze_model", {"analysis_type": analysis_type, "name": name,
                                   "from_feature": from_feature, "to_feature": to_feature,
                                   "near": near, "k": k, "axis": axis})


# ---------------------------------------------------------------------------
# Tool: measure_curves
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def measure_curves(
    name: str = "",
    include_2d: bool = False,
    include_construction: bool = False,
    per_segment: bool = False,
    include_geometry: bool = False,
    resolve_lightweight: bool = False,
    as_route: bool = False,
) -> str:
    """Measure curve LENGTHS in the active part or assembly (read-only, does NOT change state).
    Reports every SWEEP (path sketch or edges, path length, profile sketch and, for a single-circle
    profile such as a wire or cable, profile_diameter_m) and every 3D SKETCH (total length of its
    non-construction segments, with count and length per segment kind: line / arc / spline /
    ellipse / parabola). Lengths are true 3D arc lengths in meters (a spline is measured along the
    curve), rounded to 6 decimals.
    Active ASSEMBLY: measures its own sketches/sweeps (`root`) and every unsuppressed, resolved
    component at every nesting level, one entry per distinct file + configuration with its
    `instances`; `total_sweep_path_length_m` sums the sweep paths over all instances. Suppressed or
    lightweight components are listed under `skipped`; an entry carries `warning` when the
    component's document was measured in a configuration other than the referenced one.
    name: only this sketch or sweep feature (exact tree name, e.g. '3DSketch1' or 'Sweep1');
        a named 2D sketch is measured even without include_2d.
    include_2d: also report 2D sketches (default: 3D sketches and sweeps only).
    include_construction: count construction geometry in the totals (default: reported apart
        as construction_length_m).
    per_segment: also list each segment's kind and length.
    include_geometry: per segment (implies per_segment) also its 3D geometry in meters: start /
        end, center + radius for arcs, through-points for splines. Coordinates are the part's
        model coordinates, or ASSEMBLY coordinates (component transform applied) when the active
        document is an assembly — read a route here, edit it, and redraw it with create_3d_path.
    resolve_lightweight: in an assembly, resolve every lightweight component first so it is
        measured instead of skipped (reported as lightweight_resolved).
    as_route: per sketch and sweep path, also return route_points (the corner points: where each
        arc's two tangents meet, and junctions of lines meeting without an arc) and
        route_bend_radii (one per corner, 0 = sharp), in the same coordinates as include_geometry:
        exactly the points / bend_radii create_3d_path takes, so a route can be read, edited and
        redrawn. route_warning when the sketch has splines or is not one connected chain."""
    return _call("measure_curves", {"name": name, "include_2d": include_2d,
                                    "resolve_lightweight": resolve_lightweight,
                                    "include_construction": include_construction,
                                    "per_segment": per_segment,
                                    "include_geometry": include_geometry,
                                    "as_route": as_route})


# ---------------------------------------------------------------------------
# Tool: create_3d_path
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def create_3d_path(
    points: str,
    bend_radius: Annotated[float, Field(ge=0)] = 0.0,
    kind: Literal["polyline", "spline"] = "polyline",
    sketch_name: str = "",
    bend_radii: str = "[]",
) -> str:
    """Create or REDRAW a 3D sketch path in the active PART from XYZ points (cable, wire bundle,
    tube or rod routes). Bumps state_version.
    points: JSON array STRING of [x,y,z] points in METERS, in the part's model coordinates
        (for a part placed at an assembly's origin with no rotation, these are the assembly's
        coordinates), e.g. '[[0,0,0],[0.1,0,0],[0.1,0.05,0.02]]'.
    kind='polyline': straight legs between the points; with bend_radius > 0 every corner is
        rounded with an exact tangent arc of that radius (where two bends share a leg too short
        for both, both are reduced, with a warning). kind='spline': a smooth spline through the
        points (radii ignored).
    bend_radii: optional JSON array STRING with one radius per corner (len(points) - 2 values,
        meters, 0 = sharp), overriding bend_radius corner by corner, e.g. '[0.04,0.025,0.025]'.
    sketch_name: the name of an EXISTING 3D sketch to redraw in place (its old segments, with
        their dimensions and relations, are deleted and the new route drawn in the same feature,
        so a sweep built on it follows after the rebuild) — use this to update a route when the
        parts it connects move. A name that does not exist yet names the new sketch.
    Points are entered exactly (no snapping, no automatic relations). Returns the sketch name,
    its length and each segment's geometry; sweep it with sweep_circular."""
    return _call("create_3d_path", {"points": points, "bend_radius": bend_radius,
                                    "kind": kind, "sketch_name": sketch_name,
                                    "bend_radii": bend_radii})


# ---------------------------------------------------------------------------
# Tool: sweep_circular
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def sweep_circular(
    diameter: Annotated[float, Field(gt=0)],
    path_sketch: str = "",
    name: str = "",
) -> str:
    """Sweep a CIRCULAR profile of `diameter` (METERS) along a path sketch in the active PART
    (SolidWorks' circular-profile sweep: no profile sketch or plane needed, any 3D start
    direction works). Bumps state_version.
    path_sketch: the 2D or 3D sketch holding the path (e.g. from create_3d_path); required to
        create a new sweep.
    name: names the new sweep; if a circular-profile sweep with this name already exists, its
        diameter is changed instead (path_sketch ignored). To move the route, redraw its path
        sketch with create_3d_path(sketch_name=...) — the sweep follows.
    Returns the sweep name, diameter and path length."""
    return _call("sweep_circular", {"diameter": diameter, "path_sketch": path_sketch, "name": name})


# ---------------------------------------------------------------------------
# SOLIDWORKS Routing (electrical): needs the SOLIDWORKS Routing add-in (Premium) loaded.
# Workflow for a wire harness: add_routing_point on each connector part (CPoint at the cable
# exit) -> import_from_to (wires from a from-to list; sets the connectors' reference designators;
# makes a route sub-assembly with a stub per connector) -> route_path once per bundle (the path
# between connection points; shared end points join into branches) -> wires are routed, each with
# its cut length. routing_status reads everything back.
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def routing_status(route: str = "", verbose: bool = False, connectors: str = "",
                   terminations: str = "",
                   max_kink_deg: Annotated[float, Field(ge=0, le=180)] = 15.0,
                   csv_path: str = "", compare_to: str = "", csv_note: str = "",
                   check_stubs: bool = True,
                   stub_tolerance_mm: Annotated[float, Field(gt=0)] = 0.05,
                   compact: bool = False) -> str:
    """Read SOLIDWORKS Routing state (read-only, does NOT change state_version).
    Always: whether the Routing add-in is loaded and its cable / component library files;
    verbose=True: every library file location and the from-to header definitions (an EMPTY one
    makes every from-to import fail).
    Active PART: its routing points (ConnectionPoint / RoutePoint features) and Routing
    component type (2 = connector, 14 = clip, 27 = splice; route type 3 = electrical).
    Active ASSEMBLY: route_assemblies (the route sub-assemblies, e.g. 'Harness_1-<top>-1') and,
    with route=<one of them>, its wires (name, from/to as 'REF.pin', cut_length_mm, the route
    segments they run through, errors such as not_routed / violates_bend_radius, wire part
    number, colour, diameter_mm) and segments (id, length_mm, bend_radius_mm,
    bundle_diameter_mm).
    Bends SOLIDWORKS does not check: its violates_bend_radius compares only the ARCS of the route
    with the wire's minimum bend radius. Where route segments meet (stub end, fan-out join,
    breakout, one bundle continuing into the next, a covering boundary) there is no arc: a wire
    turning there kinks with radius 0 and nothing flags it. Each wire therefore also gets
    max_kink_deg (its sharpest turn at a node of its own path), min_arc_radius_mm and
    sharp_bends [{at_mm, kink_deg, where}] (turns over max_kink_deg, default 15); route.bends
    lists those nodes with their wires (at_mm in the active assembly's coordinates).
    connectors: JSON list of component names / instance paths, or a name fragment (matched
        against the instance names of loaded parts): each one's reference designator and its
        connection points (name, pin, location_mm, direction, stub_mm, stub_end_mm) in the
        ACTIVE assembly's coordinates, as they are now. Start a route branch on stub_end_mm.
    terminations: JSON {"<REF>.<pin>" | "<REF>" | "*": allowance_mm | {"allowance_mm": a,
        "strip_mm": s, ...}} (most specific key wins). The allowance is the wire length beyond
        the route end at the connection point (housing rear face -> conductor end in the crimp).
        Each wire then gets termination {from, to} (the matching entries, extra keys kept) and
        total_cut_length_mm = cut_length_mm + both allowances: the length to cut, strip
        included. Wires with an end lacking an entry: terminations_missing.
    csv_path (with route): also write the wire table to this CSV (UTF-8): Wire, From, To, Spec,
        CutLength_mm, MinArcRadius_mm, MaxKink_deg, Errors (+ TotalCutLength_mm with
        terminations), one row per wire in wire-number order, csv_note as a '# ' first line.
    compare_to: an earlier CSV of the same table (e.g. the file being replaced: it is read
        before it is overwritten): adds Previous_mm and Delta_mm per wire (its CutLength_mm), and
        the result reports the wires that changed by more than 1 mm.
    check_stubs (with route, default on): route.stub_check lists every connection point the
    route's wires use (end 'REF.pin', component, location_mm, stub_end_mm, direction, offset_mm =
    distance from the connection point to the nearest point of the route sketch) and `detached`:
    the ones off the route by more than stub_tolerance_mm. A connector moved after the from-to
    import keeps its OLD stub in the route sketch (no error anywhere; its wires stay at the old
    place): re-import (import_from_to replaces=..., or route_follow) to fix it. not_found: wire
    ends whose connector or pin was not found in the active assembly.
    compact=True: route.wires / segments as counts, keeping only wires with errors or sharp
    bends, and stub_check.stubs dropped (detached kept)."""
    params = {"route": route, "verbose": verbose}
    if max_kink_deg != 15.0:
        params["max_kink_deg"] = max_kink_deg
    if connectors.strip():
        params["connectors"] = connectors
    if terminations.strip():
        params["terminations"] = terminations
    if route.strip() and check_stubs:
        params["check_stubs"] = True
        params["stub_tolerance_mm"] = stub_tolerance_mm
    if compact and not csv_path.strip():
        raw, pl = _call_json("routing_status", params)
        _compact_status(pl)
        return _text_with(raw, pl)
    if not csv_path.strip():
        return _call("routing_status", params)
    if not route.strip():
        raise ToolError("routing_status: csv_path needs route (the route sub-assembly whose wires to write).")
    previous = _read_wire_csv(compare_to) if compare_to.strip() else {}
    raw = _call_raw("routing_status", params)
    text = map_response(raw)   # raises on FAILED
    features = (raw.get("cadState") or {}).get("features") or []
    try:
        wires = (json.loads(features[0]).get("route") or {}).get("wires") or []
    except (IndexError, ValueError, AttributeError) as exc:
        raise ToolError(f"routing_status: no wire table in the result to write ({exc}).") from exc
    rows, changed = _write_wire_csv(csv_path, wires, previous, csv_note)
    if compact:
        pl = _payload_of(raw)
        _compact_status(pl)
        text = _text_with(raw, pl)
    text += f" | csv={csv_path} rows={rows}"
    if compare_to.strip():
        text += f" | changed_over_1mm={len(changed)}: {', '.join(changed)}"
    return text


def _compact_status(pl: dict) -> None:
    """routing_status payload in short (in place): wires with errors or sharp bends only, segment
    and stub lists as counts (detached stubs kept)."""
    rj = pl.get("route")
    if not isinstance(rj, dict):
        return
    _keep_problems(rj, "wires", lambda w: not w.get("errors") and not w.get("sharp_bends"))
    if isinstance(rj.get("segments"), list):
        rj["segments_count"] = len(rj.pop("segments"))
    sc = rj.get("stub_check")
    if isinstance(sc, dict) and isinstance(sc.get("stubs"), list):
        sc["stubs_count"] = len(sc.pop("stubs"))


def _wire_sort_key(name: str):
    digits = "".join(ch for ch in name if ch.isdigit())
    return (name.rstrip("0123456789"), int(digits) if digits else 0, name)


def _read_wire_csv(path: str) -> dict:
    """{wire: CutLength_mm} from a wire table CSV ('#' lines skipped, columns by header name)."""
    with open(path, encoding="utf-8-sig", newline="") as fh:
        lines = [ln for ln in fh if not ln.startswith("#")]
    out = {}
    for row in csv.DictReader(lines):
        try:
            out[row["Wire"]] = float(row["CutLength_mm"])
        except (KeyError, TypeError, ValueError):
            continue
    return out


def _write_wire_csv(path: str, wires: list, previous: dict, note: str) -> tuple:
    with_total = any(w.get("total_cut_length_mm") is not None for w in wires)
    header = ["Wire", "From", "To", "Spec", "CutLength_mm", "MinArcRadius_mm", "MaxKink_deg", "Errors"]
    if with_total:
        header.append("TotalCutLength_mm")
    if previous:
        header += ["Previous_mm", "Delta_mm"]
    buf = io.StringIO()
    if note.strip():
        buf.write("# " + note.strip().replace("\n", " ") + "\n")
    wr = csv.writer(buf, lineterminator="\n")
    wr.writerow(header)
    changed = []
    for w in sorted(wires, key=lambda x: _wire_sort_key(str(x.get("name", "")))):
        cut = float(w.get("cut_length_mm") or 0)
        row = [w.get("name", ""), w.get("from", ""), w.get("to", ""), w.get("wire_name", ""),
               f"{cut:.2f}", f"{float(w.get('min_arc_radius_mm') or 0):.2f}",
               f"{float(w.get('max_kink_deg') or 0):.1f}", ";".join(w.get("errors") or [])]
        if with_total:
            t = w.get("total_cut_length_mm")
            row.append("" if t is None else f"{float(t):.2f}")
        if previous:
            p = previous.get(w.get("name", ""))
            row += ["", ""] if p is None else [f"{p:.2f}", f"{cut - p:+.2f}"]
            if p is None or abs(cut - p) > 1.0:
                changed.append(str(w.get("name", "")))
        wr.writerow(row)
    folder = os.path.dirname(os.path.abspath(path))
    os.makedirs(folder, exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="") as fh:
        fh.write(buf.getvalue())
    return len(wires), changed


@mcp.tool(structured_output=False)
def add_routing_point(
    kind: Literal["connection", "route", "none"] = "connection",
    face_index: int = -1,
    point: str = "",
    name: str = "",
    component_type: Literal["", "connector", "clip", "splice", "equipment"] = "",
    diameter: Annotated[float, Field(ge=0)] = 0.0,
    stub_length: Annotated[float, Field(ge=0)] = 0.0,
    sub_type: int = 2,
    reverse: bool = False,
    direction: str = "",
    exit_depth: Annotated[float, Field(ge=0)] = 0.001,
    verbose: bool = False,
) -> str:
    """Make the ACTIVE PART a SOLIDWORKS Routing component (local CAD copies only: it edits
    the part; save it with save_document). Bumps state_version.
    kind='connection': a connection point (CPoint, electrical) where the cable leaves the part:
        face_index = the face the cable exits through (analyze_model(faces)), point = JSON
        '[x, y, z]' in METERS, part coordinates, on that face (usually the cable exit centre).
        direction = JSON '[dx, dy, dz]' (part coordinates) INSTEAD of face_index: the cable exit is
        a planar face whose outward normal is that direction, within exit_depth (m, default 1 mm)
        of the outermost such face (terminal tips often stand a fraction of a mm proud of the
        housing): the one nearest to point (projected) when given, else the largest; the point
        goes onto it (its centre without point). The result's exit_face gives the face, its height,
        the candidates and body_extent_beyond_mm (solid reaching beyond the face, box estimate). A
        point picked by hand once sat on an internal face, so every route ended 8.7 mm inside the
        plug.
        The route leaves along the face normal; reverse=True flips it. stub_length (m) = the
        straight stub every route starts with (default SOLIDWORKS'), diameter (m) = nominal
        bundle diameter. sub_type: swElectricalConnectionPointType_e (2 = cable/wire, default).
    kind='route': a route point (RPoint) at `point` (not needed for connectors placed in the
        assembly before routing; may need the GUI).
    kind='none': only set component_type.
    component_type: 'connector' for harness plugs, 'clip' for tie mounts / clips.
    name: names the new feature (its point sketch becomes <name>_Sketch).
    Returns the feature created, its type, the part's routing points and component type
    (verbose=True: also every routing library location, as routing_status)."""
    return _call("add_routing_point", {
        "kind": kind, "face_index": face_index, "point": point, "name": name,
        "component_type": component_type, "diameter": diameter, "stub_length": stub_length,
        "sub_type": sub_type, "reverse": reverse, "direction": direction,
        "exit_depth": exit_depth, "verbose": verbose})


@mcp.tool(structured_output=False)
def connector_cavities(
    direction: str,
    up: str = "",
    numbering: str = "",
    exit_depth: Annotated[float, Field(ge=0)] = 0.001,
    min_size: Annotated[float, Field(ge=0)] = 0.0005,
    size_tolerance: Annotated[float, Field(ge=0, le=1)] = 0.2,
    expected_count: Annotated[int, Field(ge=0)] = 0,
) -> str:
    """Find the wire cavities of the ACTIVE PART (a connector housing) from its 3D model
    (read-only, does NOT change state_version): the openings (inner loops) of the wire-exit face,
    sorted into a grid as seen from the wire side.
    direction: JSON [x, y, z], the outward normal of the wire-exit (rear) face, part coordinates.
        Check which end is the rear first: terminals enter from the rear, so its openings are at
        least the terminal's cross-section; the mating face has the small pin entries.
    up: JSON [x, y, z], the 'up' of the view (e.g. the latch side, as the datasheet's cavity
        numbering view shows it); default: the part axis least aligned with direction.
    numbering: JSON {"first": "top_left"|"top_right"|"bottom_left"|"bottom_right", "along":
        "row"|"column", "start": 1, "count": "grid"|"cavities"}: pin `start` at that corner of
        the grid as seen from the wire side, counting along rows (or columns) over every grid
        position (count 'grid': a missing cavity skips a number) or over the cavities only
        ('cavities': staggered rows, where each cavity has a column of its own). Snake or
        counter-clockwise schemes (e.g. 1-3 down one column, 4-6 up the next) are not
        generated: pass add_connector_pins the pins in the datasheet's order. Take it from the
        datasheet's numbering view and confirm it on moulded cavity numbers when the model has
        them; a view drawn from the mating face must be mirrored. Without it, cavities only
        carry row / col.
    exit_depth (m): faces within this of the outermost one along direction are searched too.
    min_size (m): openings smaller than this in either direction are not cavities (moulded
        text, marks). The cavities are the openings of the most common size (within
        size_tolerance); the rest are listed in other_openings (latch windows, key slots).
    expected_count: warn when the number of cavities differs.
    Returns cavities (pin, row, col, point = centre on the exit face in part coordinates m,
    u_mm / v_mm in the view frame, width_mm / height_mm), rows, columns, row_pitch_mm,
    column_pitch_mm, cavity_size_mm, view_right / view_up, and with numbering `pins`: the input
    of add_connector_pins."""
    params = {"direction": direction, "exit_depth": exit_depth, "min_size": min_size,
              "size_tolerance": size_tolerance, "expected_count": expected_count}
    if up.strip():
        params["up"] = up
    if numbering.strip():
        params["numbering"] = numbering
    return _call("connector_cavities", params)


@mcp.tool(structured_output=False)
def add_connector_pins(
    pins: str,
    direction: str = "",
    stub_length: Annotated[float, Field(ge=0)] = 0.005,
    replace_existing: bool = False,
    name_prefix: str = "CPoint_",
    face_index: int = -1,
    exit_depth: Annotated[float, Field(ge=0)] = 0.001,
) -> str:
    """Give the ACTIVE PART (a connector housing; local CAD copies only: it edits the part) one
    SOLIDWORKS Routing connection point per pin, at its cavity on the wire-exit face, each
    carrying its pin number (ConnectionPointFeatureData.ElectricalPinID). A from-to import then
    attaches every wire to its own cavity (wires fan out from the cavities, and the wire-to-pin
    match is checked) instead of all wires sharing one point. Bumps state_version.
    pins: JSON [{"pin": "1", "point": [x, y, z], "direction"?: [x, y, z]}, ...] in METERS, part
        coordinates (the `pins` of connector_cavities); points are moved onto the exit face plane
        along their direction. A pin's own direction overrides the top-level one: a part with
        wire exits on several faces (a splice with one end each way) takes one call, one exit
        face and point sketch per direction.
    direction: JSON [x, y, z], outward normal of the wire-exit face (the route leaves along it;
        a point created the other way is recreated reversed); needed unless every pin has one.
    Needs the SOLIDWORKS Routing add-in loaded (fails with ROUTING_NOT_LOADED otherwise:
    without it SOLIDWORKS creates no connection point and gives no error).
    stub_length (m): straight wire out of each cavity before the route (default 5 mm).
    replace_existing: delete the part's connection points first (and the point sketches only
        they used). Without it, a part that already has a point without a pin, or with one of
        these pins, is refused: SOLIDWORKS would share wires between them.
    Also sets the part's Routing component type to connector (electrical). Save the part after.
    Returns connection_points (pin, feature, pin_id read back, location, direction, stub_mm),
    the exit face, deleted features and warnings."""
    params = {"pins": pins, "stub_length": stub_length,
              "replace_existing": replace_existing, "name_prefix": name_prefix,
              "face_index": face_index, "exit_depth": exit_depth}
    if direction.strip():
        params["direction"] = direction
    return _call("add_connector_pins", params)


@mcp.tool(structured_output=False)
def import_from_to(
    from_to: str,
    cable_library: str = "",
    component_library: str = "",
    references: str = "{}",
    convert_guidelines: bool = False,
    route_wires: bool = True,
    use_existing_assembly: bool = True,
    overwrite: bool = True,
    search_sub_assemblies: bool = True,
    press_ok: Annotated[int, Field(ge=0, le=10)] = 3,
    route: str = "",
    edit_route_first: bool = False,
    replaces: str = "",
    configurations: str = "[]",
    compact: bool = False,
) -> str:
    """Import wires from a from-to list into the ACTIVE ASSEMBLY (SOLIDWORKS Routing
    'Start by From/To', IRouteManager.ImportFromToList). Bumps state_version.
    from_to: the from-to .xlsx (first sheet; headers Wire, Spec, From Ref, Partno, Pin, To Ref,
        Partno, Pin, Color), typically exported from the electrical (ECAD) harness design.
    cable_library: the cable/wire library XML whose WireName = each Spec. Pass it explicitly.
    component_library: optional when the connectors are already placed (use_existing_assembly).
    references: JSON {component instance: reference designator}, set before the import
        (IComponent2.ComponentReference), e.g. '{"Plug-1": "P1"}': the list's From/To Ref
        are matched to these. Each connector needs a connection point (add_routing_point).
        Only resolved components count: for a harness used in several configurations, import
        once per configuration (activate it, the other instances suppressed) and suppress each
        route in the other configurations.
    The import returns False (imported=false, no reason given) when row 1 of the list holds any
    text or a Pin is empty. A connector whose Partno is empty is wired but gets NO stub: add it
    with route_path(add_cpoints=...).
    Empty from-to header definitions are filled first (cable, core, ...): the import refuses to
    run while one is empty ('other' does not matter). The PropertyManager pages the import opens are accepted
    with up to press_ok RunCommand(PmOK). The import makes a NEW route sub-assembly
    (Harness_<n>) with one stub per connector and a guideline per connector pair; it is returned
    as new_route. convert_guidelines=True turns the guidelines into (straight / auto) route
    geometry; leave it False and draw the real bundle paths with route_path instead.
    route_wires: route the wires through the route geometry and report them (cut lengths).
    route / edit_route_first: only for a route that is not created by this import.
    replaces: a route sub-assembly this import replaces (redrawing a harness): it is deleted
        before the import and its Harness_<n> files (.SLDASM, -fromToList.xml, -segmentList.xml)
        after it (deleted_files). Must be resolved in the active configuration.
    configurations: JSON list of configurations of the active assembly that show the new route;
        the first is activated for the import, and the new route is suppressed in every other
        configuration (suppressed_in): a component added in one configuration shows in all.
    The assembly is rebuilt out-of-date-only afterwards (no ForceRebuild3: that marked every
    loaded reference modified).
    Returns imported, new_route, the route's wires and segments (see routing_status), steps.
    compact=True: wires_count / segments_count / steps_count instead of the lists, keeping only
    the wires with errors (with route_wires=False they are all not_routed: only counted)."""
    params = {
        "from_to": from_to, "cable_library": cable_library, "component_library": component_library,
        "references": references, "convert_guidelines": convert_guidelines,
        "route_wires": route_wires, "use_existing_assembly": use_existing_assembly,
        "overwrite": overwrite, "search_sub_assemblies": search_sub_assemblies,
        "press_ok": press_ok, "route": route, "edit_route_first": edit_route_first,
        "replaces": replaces, "configurations": configurations}
    if not compact:
        return _call("import_from_to", params)
    raw, pl = _call_json("import_from_to", params)
    _compact_route_lists(pl, routed=route_wires)
    return _text_with(raw, pl)


def _compact_route_lists(pl: dict, routed: bool = True) -> None:
    """Counts instead of the wire / segment / step lists of a route result (in place); wires with
    errors are kept when the wires were routed."""
    for holder in (pl, pl.get("route") if isinstance(pl.get("route"), dict) else None):
        if holder is None:
            continue
        if routed:
            _keep_problems(holder, "wires", lambda w: not w.get("errors"))
        elif isinstance(holder.get("wires"), list):
            holder["wires_count"] = len(holder.pop("wires"))
        for key in ("segments", "steps"):
            if isinstance(holder.get(key), list):
                holder[f"{key}_count"] = len(holder.pop(key))


@mcp.tool(structured_output=False)
def route_path(
    route: str = "",
    points: str = "[]",
    bend_radius: Annotated[float, Field(ge=0)] = 0.0,
    bend_radii: str = "[]",
    add_cpoints: str = "[]",
    exit_route: bool = True,
    route_wires: bool = True,
    snap_tolerance: Annotated[float, Field(ge=0)] = 0.0005,
    merge_tolerance: Annotated[float, Field(ge=0)] = 1e-5,
    dry_run: bool = False,
    allow_free_ends: bool = False,
    free_end_tolerance: Annotated[float, Field(ge=0)] = 0.0005,
    min_corner_deg: Annotated[float, Field(ge=0, le=90)] = 10.0,
    wire_paths: str = "",
    paths: str = "[]",
    fan_out: str = "[]",
    max_kink_deg: Annotated[float, Field(ge=0, le=180)] = 15.0,
    allow_sharp_junctions: bool = False,
    allow_cycles: bool = False,
) -> str:
    """Draw route geometry in a SOLIDWORKS Routing route of the ACTIVE ASSEMBLY (one bundle per
    call with points, or many with paths / fan_out). Bumps state_version. To redraw a bundle, delete the route
    (edit_feature(action='delete') on its component) and import again.
    route: the route sub-assembly (routing_status route_assemblies / import_from_to new_route);
        ignored while a route is already being edited.
    points: JSON [[x,y,z], ...] in METERS, top-assembly coordinates (the route sub-assembly sits
        at the identity), from one connection point to the next: start / end exactly on a
        connector stub end (the open_ends of the result) or on another bundle's end point (a
        breakout: shared end points join into a branch). Corners get tangent arcs of
        bend_radius (m) or bend_radii (JSON, one per corner), as create_3d_path does.
    add_cpoints: JSON [[component, cpoint feature], ...] connectors to add to the route first
        (each gets its stub), e.g. '[["P2-1", "CPoint1"]]'.
    paths: JSON [{"name"?, "points": [[x,y,z], ...], "bend_radii"?: [...] | "bend_radius"?: r}, ...]
        more polylines drawn in the same route edit after points (a whole route in one call);
        each end snaps onto the route or onto an end of an earlier polyline of the call.
    fan_out: JSON [{"connector": instance, "pins"?: [...], "join"?: [x,y,z], "turn"?: [dx,dy,dz],
        "leg_radius"?: m, "max_leg_angle_deg"?: deg, "layer_step"?: m, "lead"?: m,
        "concentric"?: bool, "concentric_max_span"?: m, "converge"?: bool, "throat_radius"?: m,
        "converge_max_deg"?: deg, "straight_legs"?: bool, "join_distance"?: m}] for connectors with one connection point per
        cavity (add_connector_pins): one leg per used cavity (the stubs the from-to import put on
        the route, or the pins named) to a join node, drawn before points / paths so a branch can
        start on it. Each leg leaves its cavity straight along the cavity axis (no kink at the
        stub end), bends once with leg_radius (default 0.008; use the wire's minimum bend radius
        + its own radius or more) and runs straight to the join, arriving within
        max_leg_angle_deg (default max_kink_deg - 1) of the bundle direction. turn: the bundle
        direction leaving the join (default the mean exit direction: an axial fan with gentle
        bends; across it: a turn fan for a plug facing a wall, the wires going straight out,
        bending ~90 deg and converging). concentric (default auto: true when the used cavities
        span at most concentric_max_span = 0.008 m along the turn): every leg starts its bend
        right after its stub (lead), radius leg_radius + the cavity's offset from the inside of
        the turn, so the wires turn together as a ribbon close to the plug (legs report
        bend_starts_mm and radius_mm). Otherwise (a long row turning edgewise) or
        concentric=false: staggered, all legs at leg_radius, the cavities further upstream
        bending deeper by layer_step (default 0.0015 = one wire layer). A turn fan makes the
        wires further from the bundle side longer by their offset along the turn (a 40 mm row:
        up to 40 mm+ spread); for a wide row prefer converge and turn the bundle path instead.
        converge (no join; default false; axial or turn fans): two bends per outer leg, toward
        the axis (at most converge_max_deg, default 45) right after the stub, then back at a
        throat point (the cavity offset scaled to throat_radius, default 0.9 mm * sqrt(wires),
        min 2 mm, so the wires keep their order), arriving within max_leg_angle_deg: the bundle
        forms inline about 2 plug widths closer than a single-bend axial fan (whose join sits
        offset / tan(max_leg_angle) out), with leg lengths close together
        (leg_length_spread_mm). Draw the bundle's turn as a filleted corner of the path leaving
        the join. Legs near the axis keep one bend. With turn, each leg first turns out of its cavity
        (elbow lead + leg_radius out), then converges (a wide header facing a wall: raise lead on one
        fan to lift its elbows over a neighbour's legs). An axial fan without converge reports
        converge_hint when converge would join more than 5 mm closer.
        Without join it is placed past the corners, just far enough for the arrival angle and
        each bend. Start the bundle at the join along turn. straight_legs=true: the old straight
        legs (join_distance from the mean stub end, or from max_leg_angle_deg). Returns fan_out
        [{connector, pins, join, join_mm, shape, bundle_direction, leg_radius_mm, max_arrival_deg,
        max_bend_deg, longest_leg_mm, leg_length_spread_mm, legs[{bend_starts_mm, bend_deg,
        arrival_deg, ...}]}]; a dry run returns the joins and legs too.
    exit_route=False keeps the route open for the next bundle; the last call exits (True), and
        with route_wires routes the wires and returns them with cut lengths and errors
        (violates_bend_radius: SOLIDWORKS checks the innermost wire of the bundle, so a bend at
        exactly the library minimum is flagged; use minimum + bundle radius or more) and bends:
        the nodes where wires kink over max_kink_deg, which SOLIDWORKS never flags (see below).
    snap_tolerance (m): the first / last point is moved onto the nearest existing route point
        within this distance (the solver shifts earlier geometry by a few um when stubs or
        bundles are added, which would leave a gap at a breakout). 0 = off.
    merge_tolerance (m): coincident points are merged within this distance on every call; a
        call with points='[]' merges an existing gap.
    Checks before drawing (nothing is drawn when one fails):
    - END_NOT_ON_ROUTE: an end that does not snap and lies within free_end_tolerance (m) of a
      corner that a fillet of the route removed (where an arc's two tangents meet): a branch
      started at a corner of an earlier call is drawn 3.9 mm off the arc and its wires stay
      unrouted. Put branch nodes on straight legs, at least r*tan(deflection/2) from a corner.
      A new node anywhere else (a short bundle extending the route to the next breakout) is
      fine. allow_free_ends=True draws anyway.
    - Corners deflecting less than min_corner_deg are drawn sharp (warning): a filleted 4 deg
      corner split a route into disconnected segments.
    - SHARP_JUNCTION: only the corners INSIDE a polyline get arcs. Where a polyline end meets a
      node (a stub end, a fan-out join, a breakout, the end of another bundle) it is drawn
      sharp, and SOLIDWORKS checks arcs only: a branch leaving a trunk at 90 deg routes with no
      error and every wire in it kinks 90 deg with radius 0. Each polyline end on a node must
      leave within max_kink_deg (default 15) of the straightest segment already there (for a
      stub: its exit direction; at a breakout: the trunk the wires come from), and a corner
      with radius 0 must deflect less than that. Shape: first leg along the stub / trunk (a
      Y-shaped breakout, branches splitting by <= max_kink_deg each), then a filleted corner at
      least r*tan(deflection/2) further on. allow_sharp_junctions=True draws anyway (warnings).
      A dry run lists every junction (path end, kink_deg, the straightest neighbour).
    - ROUTE_CYCLE: SOLIDWORKS Routing's ExitRoute() / RouteWires() solver hangs indefinitely at
      100% CPU when the 3D route sketch contains a closed loop (e.g. two local Y-fillet branches
      plus a backbone bar sharing the same vertices). Keep the route sketch an acyclic tree:
      split bidirectional connectors into separate fan groups and run co-bundled through-traces
      parallel (> snap_tolerance apart) inside the outer sleeve without sharing loop-closing
      vertices. A dry run reports cycles / cycle_count. allow_cycles=True draws anyway.
    dry_run=True: snap and check, then return the points as they would be drawn and their
    geometry (length_mm, per corner deflection / radius / tangent, per leg length and straight
    rest, min_straight_mm < 0 = radii too big) without drawing. It reads the route sketch without
    entering route edit (leaving route edit rewrites the route's segmentList.xml): nothing changes.
    Wire length = the route segments it runs through + both stubs: add a dry run's length_mm to
    the existing segments to predict it before drawing.
    wire_paths: JSON {wire: [segment ids] | "<other wire>"}, applied on exit after the wires
        are routed (IWire.SetRoutePathForWire): a wire SOLIDWORKS attached to the wrong
        connector or left unrouted, although its from-to data are right, takes the path of a
        twin wire between the same connectors, e.g. '{"W5": "W24"}'. On an existing route:
        route_path(route=..., points='[]', wire_paths=..., route_wires=False) applies only them.
    Returns the route sketch (segments, total length_mm, open_ends), drawn_length_mm (all
    polylines; with several, paths lists each), and after exit the wires and segments; steps
    say whether each end snapped onto the route."""
    params = {
        "route": route, "points": points, "bend_radius": bend_radius, "bend_radii": bend_radii,
        "add_cpoints": add_cpoints, "exit_route": exit_route, "route_wires": route_wires,
        "snap_tolerance": snap_tolerance, "merge_tolerance": merge_tolerance,
        "dry_run": dry_run, "allow_free_ends": allow_free_ends,
        "free_end_tolerance": free_end_tolerance, "min_corner_deg": min_corner_deg}
    if max_kink_deg != 15.0:
        params["max_kink_deg"] = max_kink_deg
    if allow_sharp_junctions:
        params["allow_sharp_junctions"] = True
    if allow_cycles:
        params["allow_cycles"] = True
    if wire_paths and wire_paths.strip() not in ("", "{}"):
        params["wire_paths"] = wire_paths
    if paths and paths.strip() not in ("", "[]"):
        params["paths"] = paths
    if fan_out and fan_out.strip() not in ("", "[]"):
        params["fan_out"] = fan_out
    return _call("route_path", params)


@mcp.tool(structured_output=False)
def check_path_clearance(
    points: str = "[]",
    diameter: Annotated[float, Field(ge=0)] = 0.0,
    bend_radius: Annotated[float, Field(ge=0)] = 0.0,
    bend_radii: str = "[]",
    paths: str = "[]",
    clearance: Annotated[float, Field(ge=0)] = 0.0,
    ignore: str = "[]",
    include_hidden: bool = False,
    ignore_same_parent: bool = False,
    limit: Annotated[int, Field(ge=1)] = 50,
    keep_probe: bool = False,
    max_paths: Annotated[int, Field(ge=1)] = 24,
    frame_component: str = "",
    paths_file: str = "",
    names: str = "",
) -> str:
    """Is a candidate bundle path free in the ACTIVE ASSEMBLY, before any route or bundle part is
    drawn on it? Read-only for the model: builds the path as a circular-profile tube (same
    filleted polyline as create_3d_path / route_path) in a temporary part, inserts it at the
    assembly origin, runs check_interference on it, then deletes the component and the file
    (the assembly is left marked modified, geometry unchanged). Seconds instead of an import +
    draw + rebuild + check per candidate.
    points: JSON [[x,y,z], ...] in METERS, assembly coordinates; diameter (m): the bundle's;
    bend_radius / bend_radii (m) as in route_path. paths: JSON list of {points, bend_radii,
    bend_radius, diameter} to check a trunk and its branches together (instead of points).
    clearance (m): also report neighbours closer than this (slower: one Measure per neighbour).
    ignore: JSON list of name fragments to leave out of the verdict (e.g. the connectors the path
    ends in, which it touches by design). Their hits are still reported apart, located on the
    path (ignored_hits, ignored_violations): check that each is the expected end contact, not a
    real clash a broad fragment hid.
    Returns interference_count and interferences (largest first): component, volume_mm3, box_mm,
    and where on the path: path (when several), leg (points k -> k+1), at_mm_along_path,
    near_corner. clearance_violations, nearest_mm, path_length_mm, `clear`.
    keep_probe=True leaves the tube in the assembly (probe_component) to look at; delete it with
    edit_feature or close without saving.
    Each path is its own body; a path whose sweep fails is left out and listed in unswept_paths
    (then clear=false). Keep each probe small (30 fan legs meeting at one join once crashed
    SOLIDWORKS in the interference check): a selection of more than max_paths paths is checked
    in batches of max_paths, one probe each, and merged (interferences with the global path
    index and path_name, counts summed, clear = every batch clear, batches [{paths, clear,
    interference_count}]). Probe a fan's outer legs and its bundle, not every leg.
    paths_file: a JSON file whose "paths" are probed instead of points / paths: plan_probe_sizes'
    probe_out (each path sized by the wires it carries) or a route_path / route_follow plan
    ({name, points, bend_radii | bend_radius, diameter?}; a path without diameter takes
    `diameter`). names: JSON list of path names to probe only those (paths_file or paths).
    Interferences, ignored hits and unswept paths carry path_name when the paths have names.
    frame_component: the points are in THIS component's coordinates (an instance path of the
    active assembly, e.g. a sub-assembly holding a route: route plans are in its frame); they are
    mapped to the active assembly before the check (result frame_component, frame_transform).
    Reported boxes and positions are in the active assembly's coordinates."""
    try:
        plist = None
        if paths_file.strip():
            if (points or "[]").strip() != "[]" or (paths or "[]").strip() not in ("", "[]"):
                raise ValueError("give paths_file or points / paths, not both")
            loaded = am.load_json_arg(paths_file, "paths_file")
            plist = (loaded or {}).get("paths") if isinstance(loaded, dict) else loaded
            if not isinstance(plist, list) or not plist:
                raise ValueError(f"paths_file has no paths list: {paths_file}")
        elif (paths or "").strip() not in ("", "[]"):
            plist = json.loads(paths)
            if not isinstance(plist, list):
                raise ValueError("paths must be a JSON list")
        wanted = am.load_json_arg(names, "names") if names.strip() else None
    except (ValueError, json.JSONDecodeError) as exc:
        raise ToolError(f"check_path_clearance: {exc}") from exc
    if wanted is not None:
        if plist is None:
            raise ToolError("check_path_clearance: names selects among paths_file / paths")
        known = [str(p.get("name")) for p in plist if isinstance(p, dict)]
        missing = [n for n in wanted if str(n) not in known]
        if missing:
            raise ToolError(f"check_path_clearance: names not in the paths: {missing}; known: {known[:60]}")
        keep = {str(n) for n in wanted}
        plist = [p for p in plist if isinstance(p, dict) and str(p.get("name")) in keep]
    if plist is not None:
        # Only what the server reads (a probe_out also holds wire lists).
        plist = [{k: p[k] for k in ("points", "bend_radii", "bend_radius", "diameter") if k in p}
                 | ({"name": str(p["name"])} if p.get("name") is not None else {})
                 for p in plist if isinstance(p, dict)]
        for p in plist:
            if not p.get("diameter"):
                if diameter <= 0:
                    raise ToolError(f"check_path_clearance: path {p.get('name') or '?'} has no diameter: pass diameter")
                p["diameter"] = diameter
    if frame_component.strip():
        t = _component_transforms([frame_component])[frame_component]["transform"]
        try:
            pts = json.loads(points or "[]")
        except json.JSONDecodeError as exc:
            raise ToolError(f"check_path_clearance: points / paths not JSON: {exc}") from exc
        points = json.dumps([am.xf_point(t, q) for q in pts])
        for p in plist or []:
            p["points"] = [am.xf_point(t, q) for q in p.get("points") or []]
    params = {
        "points": points, "diameter": diameter, "bend_radius": bend_radius,
        "bend_radii": bend_radii, "paths": paths, "clearance": clearance, "ignore": ignore,
        "include_hidden": include_hidden, "ignore_same_parent": ignore_same_parent,
        "limit": limit, "keep_probe": keep_probe}
    if max_paths != 24:
        params["max_paths"] = max_paths
    frame_note = (f" | frame_component={frame_component} | frame_transform={json.dumps(t)}"
                  if frame_component.strip() else "")
    if plist is None:
        return _call("check_path_clearance", params) + frame_note
    path_names = [p.get("name") for p in plist]
    named = any(n is not None for n in path_names)
    server_paths = [{k: v for k, v in p.items() if k != "name"} for p in plist]
    groups = am.batches(server_paths, max_paths)
    if len(groups) == 1:
        params["paths"] = json.dumps(groups[0])
        if not named:
            return _call("check_path_clearance", params) + frame_note
        raw, pl = _call_json("check_path_clearance", params)
        am.label_hits(pl, 0, path_names)
        return _text_with(raw, pl) + frame_note
    results, raw, start = [], None, 0
    for group in groups:
        params["paths"] = json.dumps(group)
        try:
            raw, pl = _call_json("check_path_clearance", params)
        except CadOperationError as exc:   # e.g. no path of this batch could be swept
            pl = {"paths": len(group), "path_length_mm": [None] * len(group),
                  "interference_count": 0, "clear": False,
                  "unswept_paths": [{"path": i, "error": f"batch failed: {str(exc)[:300]}"}
                                    for i in range(len(group))]}
        results.append((start, pl))
        start += len(group)
    if raw is None:
        raise ToolError("check_path_clearance: every batch failed: "
                        + "; ".join(str(u.get("error")) for _, pl in results for u in pl["unswept_paths"][:1]))
    merged = am.merge_clearance_results(results, path_names, limit)
    return _text_with(raw, merged) + frame_note


def _route_path_params(route: str, *, dry_run: bool, fan_out: list, paths: list,
                       max_kink_deg: float = 15.0) -> dict:
    params = {"route": route, "points": "[]", "bend_radius": 0.0, "bend_radii": "[]",
              "add_cpoints": "[]", "exit_route": True, "route_wires": True,
              "snap_tolerance": 0.0005, "merge_tolerance": 1e-5, "dry_run": dry_run,
              "allow_free_ends": False, "free_end_tolerance": 0.0005, "min_corner_deg": 10.0}
    if max_kink_deg != 15.0:
        params["max_kink_deg"] = max_kink_deg
    if fan_out:
        params["fan_out"] = json.dumps(fan_out)
    if paths:
        params["paths"] = json.dumps(paths)
    return params


class _StepTimer:
    """Wall-clock seconds per named step of a composite tool (a repeated step adds up). A step
    that raises re-raises as ToolError with the timings so far appended."""

    def __init__(self, tool: str):
        self.tool = tool
        self.t0 = time.perf_counter()
        self.steps: dict = {}

    @contextlib.contextmanager
    def step(self, name: str):
        start = time.perf_counter()
        try:
            yield
        except Exception as exc:
            self._add(name, start)
            raise ToolError(f"{exc} | {self.tool} failed in step {name} | "
                            f"timings_s={json.dumps(self.result())}") from exc
        self._add(name, start)

    def _add(self, name: str, start: float) -> None:
        self.steps[name] = round(self.steps.get(name, 0.0) + time.perf_counter() - start, 2)

    def result(self) -> dict:
        return dict(self.steps, total=round(time.perf_counter() - self.t0, 2))


def _plan_base(plan: str, route: str, temp_folder: str) -> str:
    """Path stem for files derived from a plan: next to a plan file, else <TEMP>/<temp_folder>/<route>."""
    if os.path.isfile(plan.strip()):
        return os.path.splitext(os.path.abspath(plan.strip()))[0]
    safe = "".join(ch if ch.isalnum() or ch in "-_" else "_" for ch in route)
    folder = os.path.join(os.environ.get("TEMP") or os.getcwd(), temp_folder)
    os.makedirs(folder, exist_ok=True)
    return os.path.join(folder, safe)


def _route_wire_ends(route: str, fan_out: list, max_kink_deg: float = 15.0) -> tuple:
    """(routing_status payload with the stub check: route.wires, route.stub_check.stubs [{end
    'REF.pin', component, stub_end_mm}], the fan_out joins of a dry run [{connector, pins, join,
    join_mm}] (empty without fan_out)). Read-only."""
    _, st = _call_json("routing_status", {"route": route, "verbose": False, "check_stubs": True,
                                          "stub_tolerance_mm": 0.05})
    fans = []
    if fan_out:
        _, fd = _call_json("route_path", _route_path_params(route, dry_run=True, fan_out=fan_out,
                                                             paths=[], max_kink_deg=max_kink_deg))
        fans = [f for f in fd.get("fan_out") or [] if isinstance(f, dict)]
    return st, fans


@mcp.tool(structured_output=False)
def route_follow(
    route: str,
    plan: str,
    from_to: str = "",
    cable_library: str = "",
    reimport: bool = True,
    draw: bool = False,
    configurations: str = "[]",
    lead_points: str = "{}",
    max_move: Annotated[float, Field(gt=0)] = 0.02,
    coverings_path: str = "",
    plan_out: str = "",
    status_path: str = "",
    csv_path: str = "",
    compare_to: str = "",
    csv_note: str = "",
    max_kink_deg: Annotated[float, Field(ge=0, le=180)] = 15.0,
) -> str:
    """Redraw a route after its connectors moved, keeping its bundle plan (adapter-side; changes
    the model unless reimport=False and draw=False). The ACTIVE assembly holds the route.
    plan: JSON {fan_out: [...], paths: [{name, points (m), bend_radii | bend_radius}, ...]} as
    route_path takes them, inline or a .json file path (route_path's frame: the active assembly).
    1. reimport: export the coverings (coverings_path; an existing file is kept when the route
       has none), import_from_to(from_to, cable_library, replaces=route, route_wires=False,
       configurations): new stubs at the connectors' current places; the new route is used next.
    2. Follow: every FREE path end (one no other path shares: breakouts stay) moves onto the
       nearest stub end of the route's own wire ends (routing_status stub check; fan_out
       connectors excluded) or fan_out join (from a dry run), with its lead point(s) by the same
       vector. lead_points: {"<path>.start" | "<path>.end": n} (default 1). Ends with nothing
       within max_move (m) stay; `ambiguous` when the second candidate is almost as near.
       The followed plan is written to plan_out (default next to the plan file:
       <name>.followed.json; inline plans go to the temp folder).
    3. Dry run of the followed plan: sharp_junctions, cycle_count, no_room (paths whose
       min_straight_mm < 0.2: corners too close for their radii), junctions over max_kink_deg - 1.
    4. draw=True and the dry run clean: draw it, re-apply the coverings, routing_status (wires,
       errors, max kink, detached stubs); status_path: the full status JSON; csv_path /
       compare_to / csv_note: the wire table as routing_status writes it.
    Usual loop: reimport=True, draw=False; fix plan_out by hand where the summary shows a
    problem; then route=<new_route>, plan=<plan_out>, reimport=False, draw=True. Returns a short
    summary (moves, dry run, draw, coverings, status) and timings_s: the seconds each step took
    (export_coverings, import, follow, dry_run, draw, coverings_import, status, total; also
    appended to the error of a step that fails). A draw on a large assembly can take minutes:
    run it with start_job."""
    timer = _StepTimer("route_follow")
    try:
        plan_obj = am.load_json_arg(plan, "plan")
        leads = am.load_json_arg(lead_points, "lead_points") or {}
    except (ValueError, json.JSONDecodeError) as exc:
        raise ToolError(f"route_follow: {exc}") from exc
    if not isinstance(plan_obj, dict) or not plan_obj.get("paths"):
        raise ToolError("route_follow: plan needs {paths: [...]} (and fan_out if the route has fans)")
    base = _plan_base(plan, route, "solidpilot_route_follow")
    coverings_path = coverings_path.strip() or base + ".coverings.json"
    plan_out = plan_out.strip() or base.removesuffix(".followed") + ".followed.json"
    summary: dict = {"route": route}
    fan_out = plan_obj.get("fan_out") or []

    def done() -> str:
        summary["timings_s"] = timer.result()
        return "COMPLETED | route_follow | " + json.dumps(summary, separators=(",", ":"))

    if reimport:
        if not from_to.strip():
            raise ToolError("route_follow: reimport needs from_to (and cable_library)")
        tmp = coverings_path + ".new"
        with timer.step("export_coverings"):
            _, ex = _call_json("route_covering", {"route": route, "action": "export", "file_path": tmp,
                                                  "min_wires": 0, "name": "", "part_number": "",
                                                  "outer_diameter": 0.0})
        n = int(ex.get("exported") or 0)
        if n == 0 and os.path.isfile(coverings_path):
            os.remove(tmp)
            summary["coverings_exported"] = f"0 (kept {coverings_path})"
        else:
            os.replace(tmp, coverings_path)
            summary["coverings_exported"] = n
        with timer.step("import"):
            _, im = _call_json("import_from_to", {
                "from_to": from_to, "cable_library": cable_library, "component_library": "",
                "references": "{}", "convert_guidelines": False, "route_wires": False,
                "use_existing_assembly": True, "overwrite": True, "search_sub_assemblies": True,
                "press_ok": 3, "route": "", "edit_route_first": False, "replaces": route,
                "configurations": configurations})
        if not im.get("imported") or not im.get("new_route"):
            raise ToolError(f"route_follow: import failed: {json.dumps({k: im.get(k) for k in ('imported', 'warnings', 'steps')})[:1500]}"
                            f" | timings_s={json.dumps(timer.result())}")
        route = im["new_route"]
        summary["import"] = {"new_route": route, "deleted_files": len(im.get("deleted_files") or []),
                             "suppressed_in": im.get("suppressed_in"), "warnings": im.get("warnings")}

    with timer.step("follow"):
        st, fans = _route_wire_ends(route, fan_out, max_kink_deg)
    sc = (st.get("route") or {}).get("stub_check") or {}
    fan_names = {str(f.get("connector")) for f in fan_out}

    def is_fan(comp: str) -> bool:
        return comp in fan_names or comp.split("/")[-1] in fan_names

    candidates = [(s["end"], [v / 1000.0 for v in s["stub_end_mm"]]) for s in sc.get("stubs") or []
                  if isinstance(s.get("stub_end_mm"), list) and not is_fan(str(s.get("component", "")))]
    for f in fans:
        if isinstance(f.get("join"), list):
            candidates.append(("fan " + str(f.get("connector")), f["join"]))
    summary["candidates"] = len(candidates)
    if sc.get("detached_count"):
        summary["detached_stubs"] = sc.get("detached")
    if sc.get("not_found"):
        summary["stubs_not_found"] = sc.get("not_found")

    with timer.step("follow"):
        followed, moves = am.follow_plan(plan_obj, candidates, leads, max_move)
        with open(plan_out, "w", encoding="utf-8") as fh:
            json.dump(followed, fh, indent=1)
    summary["plan_out"] = plan_out
    summary["moves"] = [m for m in moves if m["move_mm"] > 0.001 or m.get("skipped") or m.get("ambiguous")]
    summary["ends_unchanged"] = len(moves) - len(summary["moves"])

    with timer.step("dry_run"):
        _, dry = _call_json("route_path", _route_path_params(route, dry_run=True, fan_out=fan_out,
                                                             paths=followed["paths"], max_kink_deg=max_kink_deg))
    no_room = [{"path": p.get("name"), "min_straight_mm": (p.get("geometry") or {}).get("min_straight_mm")}
               for p in dry.get("paths") or []
               if ((p.get("geometry") or {}).get("min_straight_mm") is not None
                   and (p.get("geometry") or {}).get("min_straight_mm") < 0.2)]
    near_limit = [j for j in dry.get("junctions") or [] if (j.get("kink_deg") or 0) > max_kink_deg - 1]
    summary["dry_run"] = {"sharp_junctions": dry.get("sharp_junctions"), "cycle_count": dry.get("cycle_count"),
                          "length_mm": dry.get("length_mm"), "no_room": no_room,
                          "junctions_near_limit": near_limit[:12]}
    clean = not dry.get("sharp_junctions") and not dry.get("cycle_count") and not no_room
    if not draw:
        summary["next"] = ("draw: route_follow(route=new route, plan=plan_out, reimport=False, draw=True)"
                           if clean else "fix plan_out (see dry_run), then draw")
        return done()
    if not clean:
        summary["drawn"] = False
        summary["reason"] = "dry run not clean: nothing drawn"
        return done()

    with timer.step("draw"):
        _, dr = _call_json("route_path", _route_path_params(route, dry_run=False, fan_out=fan_out,
                                                            paths=followed["paths"], max_kink_deg=max_kink_deg))
    wires = dr.get("wires") or []
    summary["drawn"] = {"wires": len(wires),
                        "errors": [{"wire": w.get("name"), "errors": w.get("errors")} for w in wires if w.get("errors")],
                        "sharp_bend_nodes": (dr.get("bends") or {}).get("sharp_bend_nodes"),
                        "max_kink_deg": max((w.get("max_kink_deg") or 0) for w in wires) if wires else None}
    if os.path.isfile(coverings_path):
        with timer.step("coverings_import"):
            _, cv = _call_json("route_covering", {"route": route, "action": "import", "file_path": coverings_path,
                                                  "min_wires": 0, "name": "", "part_number": "", "outer_diameter": 0.0})
        summary["coverings"] = {"applied_segments": cv.get("applied_segments"),
                                "unmatched_entries": cv.get("unmatched_entries"),
                                "uncovered_segments": [[s.get("id"), s.get("wire_count")] for s in cv.get("uncovered_segments") or []
                                                       if isinstance(s, dict)]}
    params = {"route": route, "verbose": False, "check_stubs": True, "stub_tolerance_mm": 0.05}
    if max_kink_deg != 15.0:
        params["max_kink_deg"] = max_kink_deg
    previous = _read_wire_csv(compare_to) if compare_to.strip() else {}
    with timer.step("status"):
        raw = _call_raw("routing_status", params)
        map_response(raw)
    full = _payload_of(raw)
    if status_path.strip():
        with open(status_path, "w", encoding="utf-8") as fh:
            json.dump(full, fh, indent=1)
    rw = (full.get("route") or {}).get("wires") or []
    summary["status"] = {"wires": len(rw), "with_errors": sum(1 for w in rw if w.get("errors")),
                         "max_kink_deg": max((w.get("max_kink_deg") or 0) for w in rw) if rw else None,
                         "detached_stubs": ((full.get("route") or {}).get("stub_check") or {}).get("detached_count")}
    if csv_path.strip():
        rows, changed = _write_wire_csv(csv_path, rw, previous, csv_note)
        summary["csv"] = {"path": csv_path, "rows": rows}
        if compare_to.strip():
            summary["csv"]["changed_over_1mm"] = changed
    return done()


@mcp.tool(structured_output=False)
def plan_probe_sizes(
    route: str,
    plan: str,
    covering_rule: str = "[]",
    default_diameter: Annotated[float, Field(gt=0)] = 0.006,
    probe_out: str = "",
    snap_mm: Annotated[float, Field(gt=0)] = 1.0,
    max_kink_deg: Annotated[float, Field(ge=0, le=180)] = 15.0,
) -> str:
    """Size the clearance probes of a route plan like the route SOLIDWORKS will draw (adapter-side,
    read-only). The ACTIVE assembly holds the route (as for route_path / route_follow).
    A drawn bundle is thicker than its bare wires: the covering on each route segment depends on
    how many wires run there. This walks every from-to wire of the route through the plan and
    writes each path's diameter, so check_path_clearance(paths_file=probe_out) probes what will
    really be drawn.
    route: the route sub-assembly whose wires and stubs are read (routing_status with
        check_stubs; a fan_out dry run gives the fan joins: nothing is drawn).
    plan: JSON {fan_out, paths: [{name, points (m), bend_radii | bend_radius}]} inline or a .json
        file path, as route_follow takes it (active assembly coordinates).
    Wire walk: path ends within snap_mm (default 1 mm) are one node, each path an edge. A wire end
        'REF.pin' enters the plan at its connector's fan join when the plan fans that connector
        out, else at its stub end; the wire runs along the shortest chain of paths between its
        two ends.
    covering_rule: JSON [[min_wires, outside_diameter_m], ...], e.g. [[24,0.0115],[9,0.0093],
        [2,0.006]]: the entry with the largest min_wires <= the path's wire count applies. No
        match: one wire keeps its own OD; several wires get 1.15*sqrt(sum OD^2).
    default_diameter (m): a path that carries no wire.
    probe_out: the probe file (default next to the plan file: <name>.probe.json; inline plans go to
        the temp folder): {route, covering_rule_mm, paths: [{name, points, bend_radii |
        bend_radius, diameter (m), wire_count, wires, sized_by}]}.
    Returns a summary: paths, diameters_mm {diameter: path count}, path_sizes {name: [mm, wires]},
    wires, placed, outside (wires with no end on the plan: e.g. a later stage), problems (a wire
    with one end off the plan, no chain of paths between its ends, a path with no wire, a fan
    connector without a reference), probe_out."""
    try:
        plan_obj = am.load_json_arg(plan, "plan")
        rule_m = am.load_json_arg(covering_rule, "covering_rule") or []
        if not isinstance(rule_m, list) or not all(
                isinstance(r, (list, tuple)) and len(r) == 2 and all(isinstance(x, (int, float)) for x in r)
                for r in rule_m):
            raise ValueError("covering_rule: JSON [[min_wires, outside_diameter_m], ...]")
    except (ValueError, json.JSONDecodeError) as exc:
        raise ToolError(f"plan_probe_sizes: {exc}") from exc
    if not isinstance(plan_obj, dict) or not plan_obj.get("paths"):
        raise ToolError("plan_probe_sizes: plan needs {paths: [...]} (and fan_out if the route has fans)")
    rule_mm = [[int(n), float(d) * 1000.0] for n, d in rule_m]
    probe_out = probe_out.strip() or (
        _plan_base(plan, route, "solidpilot_route_follow").removesuffix(".followed") + ".probe.json")
    fan_out = plan_obj.get("fan_out") or []
    st, fans = _route_wire_ends(route, fan_out, max_kink_deg)
    rj = st.get("route") or {}
    wires = [w for w in rj.get("wires") or [] if isinstance(w, dict)]
    stubs = [s for s in (rj.get("stub_check") or {}).get("stubs") or [] if isinstance(s, dict)]
    ends, fan_problems = am.wire_end_points_mm(stubs, fans)
    plan_paths = [p for p in plan_obj["paths"] if isinstance(p, dict)]
    paths_mm = [{"name": p.get("name") or f"path{i}",
                 "points": [[v * 1000.0 for v in q] for q in p.get("points") or []]}
                for i, p in enumerate(plan_paths)]
    sized = am.size_paths_by_wire_sets(paths_mm, wires, ends, rule_mm, default_diameter * 1000.0, snap_mm)
    out_paths = []
    for p, s in zip(plan_paths, sized["paths"]):
        q = {"name": s["name"], "points": p.get("points")}
        for k in ("bend_radii", "bend_radius"):
            if k in p:
                q[k] = p[k]
        q.update(diameter=round(s["diameter_mm"] / 1000.0, 7), wire_count=s["wire_count"],
                 wires=s["wires"], sized_by=s["sized_by"])
        out_paths.append(q)
    folder = os.path.dirname(os.path.abspath(probe_out))
    os.makedirs(folder, exist_ok=True)
    with open(probe_out, "w", encoding="utf-8") as fh:
        json.dump({"route": route, "covering_rule_mm": rule_mm, "paths": out_paths}, fh, indent=1)
    diameters: dict = {}
    for s in sized["paths"]:
        key = f"{s['diameter_mm']:g}"
        diameters[key] = diameters.get(key, 0) + 1
    problems = fan_problems + sized["problems"]
    summary = {
        "route": route, "paths": len(out_paths), "diameters_mm": diameters,
        "path_sizes": {s["name"]: [round(s["diameter_mm"], 2), s["wire_count"]] for s in sized["paths"]},
        "wires": len(wires), "placed": len(sized["placed"]), "outside": len(sized["outside"]),
        "problems_count": len(problems), "problems": problems[:40], "probe_out": probe_out}
    if 0 < len(sized["outside"]) <= 20:
        summary["outside_wires"] = sized["outside"]
    nf = (rj.get("stub_check") or {}).get("not_found")
    if nf:
        summary["stubs_not_found"] = nf
    return "COMPLETED | plan_probe_sizes | " + json.dumps(summary, separators=(",", ":"))


@mcp.tool(structured_output=False)
def find_corridor(
    start: str,
    start_dir: str,
    end: str,
    end_dir: str,
    diameter: Annotated[float, Field(gt=0)],
    bend_radius: Annotated[float, Field(ge=0)],
    clearance: Annotated[float, Field(ge=0)] = 0.001,
    lead: float | None = None,
    region: str = "",
    margin: Annotated[float, Field(ge=0)] = 0.06,
    resolution: Annotated[float, Field(gt=0)] = 0.002,
    frame_component: str = "",
    ignore: str = "[]",
    max_candidates: Annotated[int, Field(ge=1)] = 3,
    grid_file: str = "",
    save_grid: str = "",
    time_budget_s: Annotated[float, Field(ge=0)] = 60.0,
) -> str:
    """Find feasible filleted bundle corridors from start to end through the active assembly
    (adapter-side, read-only; calls occupancy_grid unless grid_file is given).
    All lengths and coordinates are in METERS, in frame_component's coordinates (default: top
    assembly).
    start / end: JSON [x,y,z] (m) of the stub end or connector exit.
    start_dir: JSON [dx,dy,dz] direction the bundle LEAVES start.
    end_dir: JSON [dx,dy,dz] direction the bundle travels when it ARRIVES at end.
    diameter (m): bundle outer diameter; bend_radius (m): tangent fillet radius at every
        interior corner; clearance (m, default 0.001): extra inflation margin around obstacles.
    lead (m, optional): straight lead distance along start_dir / against end_dir before the first
        / after the last corner (default bend_radius).
    region: JSON [x0,y0,z0,x1,y1,z1] (m). When omitted, uses the start..end bounding box padded
        by max(margin, 2*bend_radius + diameter + clearance).
    resolution (m, default 0.002): occupancy voxel edge; ignore: JSON list of instance-path
        fragments passed to occupancy_grid (e.g. '["Harness-1/"]').
    grid_file: reuse a saved occupancy_grid JSON instead of calling SolidWorks.
    save_grid: write the fetched occupancy_grid JSON to this path for later reuse.
    Returns {frame, candidates [{points (m), bend_radii (m), length_mm, corners,
    deflections_deg, min_straight_mm, min_clearance_mm, ...}], probe_paths (JSON string ready
    for check_path_clearance(paths=...)), grid_stats, warnings, rejected}."""
    try:
        start_v = [float(v) for v in am.load_json_arg(start, "start")]
        start_dir_v = [float(v) for v in am.load_json_arg(start_dir, "start_dir")]
        end_v = [float(v) for v in am.load_json_arg(end, "end")]
        end_dir_v = [float(v) for v in am.load_json_arg(end_dir, "end_dir")]
        for name_v, vec in (("start", start_v), ("start_dir", start_dir_v),
                            ("end", end_v), ("end_dir", end_dir_v)):
            if len(vec) != 3:
                raise ValueError(f"{name_v} must have 3 numbers")
        if lead is not None and lead < 0:
            raise ValueError("lead must be >= 0")
        reg_v = None
        if region.strip() not in ("", "[]"):
            reg_v = [float(v) for v in am.load_json_arg(region, "region")]
            if len(reg_v) != 6:
                raise ValueError("region must have 6 numbers [x0,y0,z0,x1,y1,z1]")
    except (TypeError, ValueError, json.JSONDecodeError) as exc:
        raise ToolError(f"find_corridor: {exc}") from exc

    grid_s = 0.0
    saved_path = None
    used_grid_file = None
    if grid_file.strip():
        used_grid_file = os.path.abspath(grid_file.strip())
        try:
            grid = am.load_json_arg(used_grid_file, "grid_file")
        except (ValueError, json.JSONDecodeError) as exc:
            raise ToolError(f"find_corridor: {exc}") from exc
        if not isinstance(grid, dict):
            raise ToolError(f"find_corridor: grid_file must hold a JSON object: {used_grid_file}")
    else:
        if reg_v is None:
            pad = max(margin, 2.0 * bend_radius + diameter + clearance)
            reg_v = [min(start_v[i], end_v[i]) - pad for i in range(3)] + [
                max(start_v[i], end_v[i]) + pad for i in range(3)]
        t0 = time.perf_counter()
        _, grid = _call_json("occupancy_grid", {
            "region": json.dumps(reg_v),
            "resolution": resolution,
            "frame_component": frame_component.strip(),
            "ignore": ignore,
            "include_hidden": False,
            "dilate": 0.0,
            "time_budget_s": max(time_budget_s, 60.0),
        })
        grid_s = round(time.perf_counter() - t0, 3)
        if save_grid.strip():
            saved_path = os.path.abspath(save_grid.strip())
            os.makedirs(os.path.dirname(saved_path), exist_ok=True)
            with open(saved_path, "w", encoding="utf-8") as fh:
                json.dump(grid, fh, separators=(",", ":"))

    try:
        res = _corridor.plan_corridor(
            grid, start_v, start_dir_v, end_v, end_dir_v,
            diameter=diameter, bend_radius=bend_radius, clearance=clearance,
            lead=lead, max_candidates=max_candidates, time_budget_s=time_budget_s,
        )
    except _corridor.CorridorError as exc:
        raise ToolError(f"find_corridor: {exc}") from exc

    probe_list = [
        {"name": f"cand_{i + 1}", "points": c["points"],
         "bend_radii": c["bend_radii"], "diameter": diameter}
        for i, c in enumerate(res["candidates"])
    ]
    st = res["stats"]
    grid_stats = {
        "dims": st.get("dims"),
        "occupied_count": st.get("occupied_count"),
        "resolution_mm": st.get("resolution_mm"),
        "grid_s": grid_s,
        "search_s": st.get("search_s"),
        "inflate_s": st.get("inflate_s"),
        "flood_s": st.get("flood_s"),
        "elapsed_s": st.get("elapsed_s"),
        "coarse_voxels_per_cell": st.get("coarse_voxels_per_cell"),
        "expansions": st.get("expansions"),
    }
    payload = {
        "frame": grid.get("frame", frame_component.strip() or "world"),
        "candidates": res["candidates"],
        "probe_paths": json.dumps(probe_list, separators=(",", ":")),
        "grid_stats": grid_stats,
        "warnings": res["warnings"],
        "rejected": res["rejected"],
    }
    if saved_path is not None:
        payload["saved_grid"] = saved_path
    if used_grid_file is not None:
        payload["grid_file"] = used_grid_file
    return "COMPLETED | find_corridor | " + json.dumps(payload, separators=(",", ":"))


@mcp.tool(structured_output=False)
def flatten_route(
    route: str,
    type: Literal["manufacture", "annotation"] = "manufacture",
    connector_display: Literal["3d", "blocks"] = "3d",
    formboard_width: Annotated[float, Field(gt=0)] = 1.0,
    formboard_height: Annotated[float, Field(gt=0)] = 0.6,
    segment_orientation: Literal["horizontal", "vertical"] = "horizontal",
) -> str:
    """Flatten an electrical route (SOLIDWORKS Routing Flatten Route, IRouteManager.CreateFlattenRoute)
    for a 2D harness / formboard drawing. Bumps state_version.
    The ACTIVE document must be the assembly that holds the route sub-assembly (and its connectors).
    route: the route sub-assembly instance (routing_status route_assemblies).
    type: 'manufacture' (formboard layout: bundles straight, true lengths) or 'annotation'.
    connector_display / formboard_width / formboard_height (m) / segment_orientation: as in the
        Flatten Route PropertyManager (formboard and orientation apply to manufacture only).
    Adds a configuration '<Type>FlattenedRoute<n> - <config>' to the route assembly (planar 3D sketch,
    a FlattenedRoute feature, a virtual flattened cable part); the 3D route stays in its configuration.
    The route assembly is SAVED afterwards (the flattened cable is a virtual part that drawings only
    show once saved; SOLIDWORKS' own flatten saves it too); never on a read-only path.
    SOLIDWORKS' own drawing option is not used: with the connectors in the parent assembly it throws
    before its first table. Use route_drawing for the drawing.
    Returns flattened_route (feature name), configuration, route_saved, flat_box_mm, and per route
    segment its 3D and flat length (max_segment_length_diff_mm: the flat lengths must equal the 3D ones)."""
    return _call("flatten_route", {
        "route": route, "type": type, "connector_display": connector_display,
        "formboard_width": formboard_width, "formboard_height": formboard_height,
        "segment_orientation": segment_orientation})


@mcp.tool(structured_output=False)
def route_drawing(
    route: str,
    file_path: str,
    flattened_route: str = "",
    sheet: Literal["auto", "A4", "A3", "A2", "A1", "A0"] = "auto",
    scale: Annotated[float, Field(ge=0)] = 0.0,
    title: str = "",
    pdf_path: str = "",
    circuit_summary: bool = True,
    connector_tables: bool = True,
    segment_lengths: bool = True,
    drawing_template: str = "",
    circuit_summary_template: str = "",
) -> str:
    """2D harness drawing of a flattened route (run flatten_route first). Creates and saves a new
    drawing (it stays open and active). Bumps state_version.
    The ACTIVE document must be the assembly that holds the route sub-assembly and its connectors.
    route: the route sub-assembly instance; flattened_route: its FlattenedRoute feature (default the
        latest); file_path: the .SLDDRW to write (never a read-only location); pdf_path: also export a PDF.
    sheet / scale: 'auto' picks the smallest ISO landscape sheet that holds the flat view at 1:1 (else
        1:2, 1:5, 1:10) with the tables under it; scale 0 = auto (1.0 = 1:1). Sheet format from the
        SOLIDWORKS sheet format folders ('<size> - landscape.slddrt'), drawing template from the
        default (drawing_template overrides).
    Content: the flat view (*Front of the flattened configuration); a label (reference + part) at each
    connector's end of the flat harness; a note with the length on every route segment carrying two
    wires or more (segment_lengths); the circuit summary (BOM-type table from the route:
    bom-circuit-summary.sldbomtbt of the install, or circuit_summary_template); one table per connector
    (pin, wire, spec, other end, routed length), built from the route's wires, because SOLIDWORKS'
    connector tables need the connectors inside the route assembly. title: a note at the top left.
    Returns drawing, pdf, sheet, scale, flat_size_mm, view_outline_mm, connectors (flat end, label
    position), tables, bundle_length_notes, warnings."""
    return _call("route_drawing", {
        "route": route, "file_path": file_path, "flattened_route": flattened_route, "sheet": sheet,
        "scale": scale, "title": title, "pdf_path": pdf_path, "circuit_summary": circuit_summary,
        "connector_tables": connector_tables, "segment_lengths": segment_lengths,
        "drawing_template": drawing_template, "circuit_summary_template": circuit_summary_template})


@mcp.tool(structured_output=False)
def route_covering(
    route: str,
    action: Literal["list", "add", "remove", "export", "import"] = "list",
    segments: str = "",
    wires: str = "",
    min_wires: Annotated[int, Field(ge=0)] = 0,
    name: str = "",
    part_number: str = "",
    outer_diameter: Annotated[float, Field(ge=0)] = 0.0,
    color: str = "",
    file_path: str = "",
    compact: bool = False,
) -> str:
    """Coverings (sleeving, tape, conduit) on the route segments of an electrical route in the
    ACTIVE assembly (IRouteProperty.CreateCovering / AddCovering, ICovering). Bumps state_version
    for add / remove; list is read-only.
    route: the route sub-assembly (routing_status route_assemblies).
    action: 'list' every segment (id, length_mm, wires, has_covering, covering {name, part_number,
        outer_diameter_mm}); 'add' (replaces an existing covering) or 'remove' on the chosen ones.
    Choose segments by segments (JSON list of route segment ids, as routing_status reports them),
    wires (JSON list: the segments whose wire set is exactly these wires, e.g. a twisted pair's
    trunk) or min_wires (every segment carrying at least that many wires).
    name / part_number: the covering's identity; outer_diameter (m, required for add): the covered
    bundle's outside diameter; color: JSON [r, g, b].
    A covering applies to whole route segments, as the Covering PropertyManager does. A covering on
    part of a segment (a manufacturer's free zone at a housing) needs SOLIDWORKS' Split Route or
    Fixed Length Covering first, which the Routing API does not offer: do that in the GUI.
    Marks the route sub-assembly modified (SetSaveFlag) when segments change so save_document
    persists the coverings.
    Redrawing a route (import_from_to replaces=...) drops its coverings and renumbers its
    segments: 'export' first writes every covering, keyed by the exact wire set of its segment,
    to file_path (JSON {coverings: [{wires, name, part_number, outer_diameter_mm, color}]};
    read-only); after the redraw, 'import' (file_path) puts each covering back on every segment
    with the same wire set and reports applied_segments, unmatched_entries (wire sets the new
    route no longer has) and uncovered_segments (2+ wires, no covering: new wire sets to cover
    with 'add').
    compact=True: counts instead of lists (export: coverings_count; add / remove / import: the
    segment lists as counts, unmatched_entries and uncovered_segments kept: they need action)."""
    params = {"route": route, "action": action, "min_wires": min_wires, "name": name,
              "part_number": part_number, "outer_diameter": outer_diameter}
    if file_path.strip():
        params["file_path"] = file_path
    if segments.strip():
        params["segments"] = segments
    if wires.strip():
        params["wires"] = wires
    if color.strip():
        params["color"] = json.loads(color)
    if not compact or action == "list":
        return _call("route_covering", params)
    raw, pl = _call_json("route_covering", params)
    for key in ("coverings", "segments", "changed_segments", "steps"):
        if isinstance(pl.get(key), list):
            pl[f"{key}_count"] = len(pl.pop(key))
    if isinstance(pl.get("uncovered_segments"), list):
        pl["uncovered_segments"] = [
            {k: s[k] for k in ("id", "wire_count", "length_mm") if k in s} if isinstance(s, dict) else s
            for s in pl["uncovered_segments"]]
    pl.pop("note_import", None)
    pl.pop("note", None)
    return _text_with(raw, pl)


@mcp.tool(structured_output=False)
def route_guidelines(route: str = "", exit_route: bool = True) -> str:
    """Turn the guidelines of a SOLIDWORKS Routing route (the connector pairs a from-to import
    still has to wire) into route geometry (IAutoRoute ShowGuidelines / SelectGuidelines /
    ConvertGuidelinesToRoute) in the ACTIVE ASSEMBLY. Gives SOLIDWORKS' own straight / auto
    paths, not the harness's real bundle paths (use route_path for those). Bumps state_version.
    route: the route sub-assembly, unless a route is already being edited.
    Returns the route sketch and the steps."""
    return _call("route_guidelines", {"route": route, "exit_route": exit_route})


@mcp.tool(structured_output=False)
def start_route(component: str, cpoint: str = "", press_ok: bool = True, exit_route: bool = True) -> str:
    """Start a NEW electrical route at a connector's connection point in the ACTIVE ASSEMBLY
    (IRouteManager.StartRoute + RunCommand(PmOK) for the Route Properties page), without a
    from-to list (no wires; for a cable-only model). A harness with wires starts from
    import_from_to instead. Bumps state_version.
    component: the connector instance; cpoint: its connection point feature (default: the
        component itself is selected). exit_route=False stays in the route sketch for route_path.
    Returns the steps, where SolidWorks left the edit focus and the route sub-assemblies."""
    return _call("start_route", {"component": component, "cpoint": cpoint,
                                 "press_ok": press_ok, "exit_route": exit_route})


# ---------------------------------------------------------------------------
# Tool: screenshot
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def screenshot(
    file_path: str,
    view: Literal["current", "isometric", "trimetric", "dimetric", "front", "back",
                  "left", "right", "top", "bottom"] = "current",
    zoom_to_fit: bool = True,
    width: Annotated[int, Field(ge=0, le=8000)] = 0,
    height: Annotated[int, Field(ge=0, le=8000)] = 0,
    caption: str = "",
    zoom_box: str = "",
    focus: str = "[]",
    isolate: bool = False,
    margin: Annotated[float, Field(ge=0, le=2)] = 0.1,
    hide_sketches: bool | None = None,
    up: Literal["y", "z"] = "y",
) -> str:
    """Save the active document's graphics view as a PNG image (does NOT change state).
    Captures what the SolidWorks window shows (IModelDoc2.SaveBMP); the document file itself is
    never written. Only the on-screen view changes: `view` switches to a standard orientation
    first ('current' keeps the user's view), and zoom_to_fit fits the model in the window.
    up: 'y' = SolidWorks' standard views (Y up: a Z-up model is seen from above with 'front' and
        lies on its side in 'isometric'); 'z' = the same views for a Z-up model (Z up, 'front'
        looks along +Y).
    file_path: full output path ending in .png (folders are created).
    width / height: image size in pixels; 0 = the window's size.
    caption: optional text drawn in a white band under the image, one line per line break (a
        typed backslash-n works too); the first line is bold (e.g. the part name, then its
        measurements from measure_curves).
    zoom_box: JSON '[x1, y1, z1, x2, y2, z2]' in METERS, model coordinates: zoom on that 3D box
        (projected into the chosen view, plus `margin` x its size on each side) instead of fitting
        the whole model, e.g. to look at one connector area of a large assembly. The view is
        centred on the box (where it lands is measured and corrected).
    focus: JSON list of component names or instance paths (active assembly): zoom on their world
        bounding box (as zoom_box, with margin; returned as focus_box_mm). isolate=True shows only
        them for the capture (SolidWorks' Isolate, left with ExitIsolate afterwards; the display
        state is not changed).
    hide_sketches: hide sketches and curves for the capture (View > Hide/Show, restored after);
        default = isolate (Isolate hides components only: route sketches of other route
        sub-assemblies stayed in the image). marked_modified=true in the result if the capture
        left the document marked modified.
    The document's window must be visible: activate_document it first."""
    params: dict = {"file_path": file_path, "view": view, "zoom_to_fit": zoom_to_fit,
                    "width": width, "height": height, "caption": caption}
    if up == "z":
        params["up"] = "z"
    if zoom_box:
        params["zoom_box"] = json.loads(zoom_box)
    if focus and focus.strip() != "[]":
        params["focus"] = focus
        params["isolate"] = isolate
    params["margin"] = margin
    if hide_sketches is not None:
        params["hide_sketches"] = hide_sketches
    return _call("screenshot", params)


# ---------------------------------------------------------------------------
# Tool: analyze_drawing  (2026-07-27 — the SHIPPING drawing reader: DXF/DWG)
# ---------------------------------------------------------------------------
# The reader is a permanent module of the CAD-neutral planner layer (ADR-064): a drawing is design
# intent expressed in 2D, so it belongs beside the IR schema and the recipe, not in a research dir.
_CAD_PLANNER_PKG_DIR = os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..", "..", "cad-planner"))

# Imported EAGERLY, at startup, in the MAIN thread -- deliberately, and NOT lazily inside the tool
# like pycompiler is. ezdxf pulls in numpy, and loading numpy's C extensions from an MCP worker
# thread DEADLOCKS on Windows: the tool hung forever with no error and no traceback (diagnosed by
# faulthandler thread dump, 2026-07-27 -- stuck in importlib create_module for numpy._core.
# multiarray). A failure here must still never kill the server, so it degrades to a clean per-call
# error instead.
try:
    if _CAD_PLANNER_PKG_DIR not in sys.path:
        sys.path.insert(0, _CAD_PLANNER_PKG_DIR)
    import drawing as _draw
    _DXF_READER_IMPORT_ERROR = None
except Exception as _exc:                     # noqa: BLE001 - startup must survive anything
    _draw = None
    _DXF_READER_IMPORT_ERROR = f"{type(_exc).__name__}: {_exc} (pip install ezdxf)"


def _summarise_direct(art, summary, graph):
    """The DIRECT_BUILDABLE result: everything needed to JUDGE the build, and nothing else.

    The contour deliberately stays out. A real laser outline is scores of segments; showing it and
    then having the model echo it back into submit_feature_graph would cost the tokens twice over
    and invite a transcription error in between — which is the whole reason the lowering exists."""
    b, th = summary["blank"], summary["thickness"]
    cuts = ", ".join("%gx%g @ (%g,%g)"
                     % (c["bbox"][2] - c["bbox"][0], c["bbox"][3] - c["bbox"][1],
                        round((c["bbox"][0] + c["bbox"][2]) / 2, 2),
                        round((c["bbox"][1] + c["bbox"][3]) / 2, 2))
                     for c in b["cutouts"]) or "none"
    lines = [
        "DIRECT_BUILDABLE | sheet-metal flat pattern | %s | sha256 %s"
        % (art["source"]["file"], art["source"]["sha256"][:12]),
        "  blank      %g x %g mm | outer loop %d segments | cutouts: %s"
        % (b["size"][0], b["size"][1], b["outer_segments"], cuts),
        "  thickness  %g mm  (%s)   bend radius %g mm   K %g"
        % (th["value_mm"], th["source"], summary["bends"][0]["radius_mm"], summary["k_factor"]),
        "  bends      %d, each corroborated by its line's edge class:" % len(summary["bends"]),
    ]
    for r in summary["bends"]:
        x1, y1, x2, y2 = r["line"]
        lines.append("               %-4s %g deg R%g  line %s  midpoint (%g, %g)%s"
                     % (r["dir"], r["angle_deg"], r["radius_mm"], r["class"],
                        round((x1 + x2) / 2, 4), round((y1 + y2) / 2, 4),
                        "  [WARNING: in a closed loop]" if r["in_loop"] else ""))
    for r in summary["skipped_bends"]:
        lines.append("  SKIPPED    %s note at %s — %s (candidates %s). It will NOT be built."
                     % (r["dir"], r["at"], r["reason"], r["candidates"]))
    lines += [
        "  expected   blank area %g mm^2 -> V = %r m^3  (bending does not change it at K=0.5)"
        % (b["area_mm2"], summary["expected"]["volume_m3"]),
        "  graph      %d IR nodes: sketch + sheet_metal, then sketch + sketched_bend per direction"
        % len(graph["nodes"]),
        "  NEXT       mode='build' builds it | mode='ir' shows the graph first.",
        "             AFTER building, verify: analyze_model('mass_properties')",
        "             against the expected volume above, and read one bend face back — a mirrored",
        "             fold is invisible to volume, area and topology alike (recipe R14).",
    ]
    if summary["skipped_bends"]:
        lines.append("             A skipped bend is invisible to ALL of those (the blank volume is "
                     "unchanged): add it by hand or declare it as a gap (R15).")
    return "\n".join(lines)


# ---------------------------------------------------------------------------
# ADVISORIES — the third home (payload trim phase 5, 2026-09-07)
# ---------------------------------------------------------------------------
# Three channels carry what the model needs, and each costs differently: a tool docstring is paid
# EVERY session whatever the job, a recipe section once per job that fetches it, and a payload note
# only when the thing it warns about actually happens. So guidance that applies to ONE flag belongs
# on that flag: `reverse` used to spend a paragraph on "if you see printed_mismatch, do X" in every
# read, whether or not any dimension carried it.
#
# Computed HERE, in the adapter, deliberately NOT in the reader: the saved artifact stays pure DATA
# (the research pool is not polluted, ANALYSIS_VERSION does not move, the draw-dialect contract and
# gate 4 are untouched). Only the ANSWER gains a short advisory block.
#
# A closed set, one line each, emitted ONCE per condition with the places it fired.
def _advisories(art) -> str:
    """A short plain-text block naming the conditions this read actually raised. '' when none."""
    out = []
    views = art.get("views") or []

    scaled = [v["vid"] for v in views if v.get("scale_factor") is not None]
    if scaled:
        out.append("view scale_factor @ %s — this view is drawn at a DIFFERENT ratio from the "
                   "sheet; say which factor produced a number before you quote it."
                   % ", ".join(scaled))

    not_read = (art.get("sheet") or {}).get("not_read") or {}
    if not_read:
        out.append("not_read @ sheet (%s) — the reader saw these and did not emit them; a named "
                   "block you cannot account for is evidence that never reached you."
                   % ", ".join(sorted(not_read)))

    ang, lin = [], []
    for k, d in enumerate(art.get("dimensions") or []):
        if d.get("printed_mismatch"):
            (ang if d.get("kind") == "angular" else lin).append("dim[%d]" % k)
    if ang:
        out.append("printed_mismatch/ANGULAR @ %s — the value is a JUDGEMENT, not a reading; "
                   "corroborate it against the geometry or an ellipse ratio before building."
                   % ", ".join(ang))
    if lin:
        out.append("printed_mismatch @ %s — the scale, the arc match or the per-view factor is "
                   "wrong. Reading the flag is free; ignoring it is not." % ", ".join(lin))

    tier_b = ["%s/%s" % (v["vid"], lp.get("id"))
              for v in views for lp in (v.get("loops") or []) if lp.get("tier") == "B"]
    if tier_b:
        out.append("tier:B @ %s — this boundary was WALKED, not chained; check its area against a "
                   "stated length or width before you build on it." % ", ".join(tier_b))

    unpaired = [b for b in (art.get("bend_notes") or []) if b.get("unpaired")]
    if unpaired:
        out.append("unpaired bend x%d — the reader refused to guess, so those bends are NOT built. "
                   "Resolve them from the drawing or declare a gap; never split the difference."
                   % len(unpaired))
    in_loop = [b for b in (art.get("bend_notes") or [])
               if (b.get("bend_line") or {}).get("in_loop")]
    if in_loop:
        out.append("bend_line in_loop x%d — the match landed on OUTLINE geometry, and a bend line "
                   "belongs to no closed contour. Say so." % len(in_loop))

    vg = art.get("view_graph")
    if isinstance(vg, dict) and vg.get("solved") is False:
        out.append("view_graph.solved:false (%s) — the axes did not join; that joining is yours."
                   % vg.get("reason", "reason in the field"))

    if not out:
        return ""
    return "\nADVISORIES (%d) — only what actually fired in THIS read:\n  %s" % (
        len(out), "\n  ".join(out))


def _wire_json(obj) -> str:
    """The drawing payload, serialized as tightly as JSON allows.

    `json.dumps` defaults to ', ' and ': ' — two bytes of whitespace per comma and colon, which on
    an artifact made mostly of short numeric arrays is 18,854 B across the 10 samples (11 points of
    payload, measured). Nothing is lost: JSON whitespace is not data.
    """
    return json.dumps(obj, ensure_ascii=False, separators=(",", ":"))


@mcp.tool(structured_output=False)
def analyze_drawing(file_path: str, save_analysis: bool = True,
                    mode: Literal["auto", "ir", "build"] = "auto") -> str:
    """Read a 2D technical drawing — **.DXF or .DWG** — and either BUILD the part from it or hand
    you the evidence to build it yourself. This is the drawing→part front end: a real drawing
    arrives as DXF/DWG (a .SLDDRW is model-linked and nobody ships one), so start here for ANY
    "build the part from this drawing" job. A .DWG is converted automatically.

    **mode='auto' (default) returns ONE OF TWO SHAPES — the reader decides, not you:**

    (A) `DIRECT_BUILDABLE` — a short plain-text summary. Every decision this drawing needs is
        forced by the drawing itself (today: sheet-metal flat patterns), so the whole part has
        already been lowered to Feature Graph IR deterministically. You get the blank size, the
        thickness and its source, every bend with its edge-class corroboration, and the EXPECTED
        VOLUME — enough to judge the build — but NOT the contour, on purpose: a real outline is
        scores of segments and echoing it back would cost the tokens twice. Read it, then call
        `mode='build'`. Anything the reader could not attribute is listed as SKIPPED and will not
        be built; completing or declaring it is then yours (recipe R15).

    (B) `NOT_DIRECT | <reason>` followed by the WHOLE analysis — the drawing needs real reading
        (the normal path for machined parts); the reason names exactly which decision the drawing
        left open. You get sheet/frame, every view complete (boxes, size, geometry, loops with
        their `seq`, open chains), alignment, view_graph, every dimension, bend note and note, in
        one answer. Read it per get_recipe('reverse') and build with submit_feature_graph.

    mode='ir'    — the lowered Feature Graph IR, without building. For inspection or saving.
    mode='build' — lower and BUILD, through the same deterministic pycompiler as
                   submit_feature_graph. **CHANGES GEOMETRY and bumps state_version.** Opens a new
                   part. Re-derives from the same file, so it needs no cached graph — but verify
                   the sha256 in the summary is the file you looked at.

    ⮕ Before interpreting a single number below, call `get_recipe(section='reverse')` — the TOOL,
      not the `recipe://usage/reverse` resource, which most hosts give the model no way to read
      (ADR-077). It holds the reading discipline (projection standard, face binding, contour gaps,
      sheet-metal bend arithmetic) and the self-verification rules. When you then need the IR
      vocabulary, `get_recipe('feature_graph_schema', profile='part')` is the right shape for this
      job — it drops the assembly vocabulary and keeps every part and sheet-metal feature type.

    The artifact — WHAT EVERY FIELD MEANS. (The DECISIONS you make with them are the recipe's;
    read get_recipe('reverse') for those. Nothing is documented in both places.)
      sheet — dxf_version, units, **scale_factor** and the sheet box. `scale_factor` is ALREADY
        applied to every size/coordinate/dimension below, so all numbers are TRUE model mm and
        multiplying again builds a half- or double-size part. An ANGLE is never scaled (DIMLFAC is
        a length factor).
        `not_read` counts what the reader saw and did NOT emit, by entity class and — for an
        INSERT — by block name, e.g. {"HATCH": 5, "INSERT": {"WALZRICHTUNG": 1}}. Nothing vanishes
        silently: a named block you cannot account for is evidence that exists on the sheet and
        did not reach you.
      frame — {paper_box, title_block_top, primitives, circles[], note_count}: the border/title
        block cluster, which is never emitted as a view. A candidate is accepted ONLY when it
        CONTAINS every other cluster, so `paper_box: null` means this sheet has no frame and every
        cluster was a view candidate. A cluster in the title-block corner that no dimension points
        at arrives as `role: "frame_item"` (a projection symbol, a weld symbol) — furniture.
      frame_notes[] / notes[] — the sheet's text in reading order, `frame_notes` being the title
        block's. Text INSIDE inserted blocks (SW_NOTE, BIEGETEILE, a company stamp) is included,
        one entry per ROW, nested to any depth. The DWG route splits a row into per-glyph
        fragments that are re-joined left to right, so match on content, not on exact spacing.
      views[] — one per detected view: `vid`, `role`, `paper_box` (sheet coords, for reading the
        LAYOUT), `geom_box`, `size` [w,h] in true mm, `dropped_duplicates`? (exact duplicates the
        DWG route re-emitted), `scale_factor`? — PRESENT ONLY when this view is drawn at a ratio
        DIFFERENT from the sheet's (a detail or section labelled 'A-A 1 : 1' on a 1:10 sheet); its
        geometry and dimensions are already scaled with THAT factor.
        Then `geometry` {lines, arcs, circles, ellipses} in view-local
        true mm with the view's own bottom-left as origin. The EDGE CLASS is carried on every
        primitive as `c`: 'visible' (near-side edge) | 'hidden' (obscured — a real feature seen
        through material) | 'cut_line' (a section's cutting line, not part geometry) | 'center'
        (an axis).
        `lines` is the one array grouped BY that class instead of carrying it per record:
        {"visible": [[index, x1, y1, x2, y2], …], "hidden": […]}. THE FIRST NUMBER IS THE INDEX —
        the one a `seq` entry names — written in rather than implied by position, so ['l', 12, 1]
        is found by looking for the row starting with 12, in whichever group, and never by
        recomputing an offset. Indices run 0..n-1 across all the groups together.
        `ellipses` {cx, cy, rx, ry, rot (+ t1, t2, x1..y2 for an arc)} are PARAMETRIC — a circle
        on a plane tilted by θ projects with ry/rx = cos θ and rx its true radius, so a Ø10 hole
        on a 20° face arrives as rx 5 / ry 4.699, not as 48 line segments. A record carrying
        `n` + `fit` was recovered from an exploded fan of n segments (fit = max deviation, mm);
        one without is a real DXF ellipse.
        The sheet border/title block is NOT a view; a cluster INSIDE a view is that view's feature.
      views[].loops[] — the CLOSED contours, already chained: {id, class, role outer|inner, parent,
        area (mm^2, arc bulges exact), bbox, seq, tier?}. `seq` is [[code, index, dir], …]
        referencing that view's own geometry arrays — the contour IN ORDER, ready to transcribe as
        a sketch path profile. Traverse it as given; consecutive entries share an endpoint exactly.
        `tier` is PRESENT ONLY when it is "B", so the common case costs nothing. Absent = Tier A,
        strict chaining, which stops dead at any junction of three or more and therefore never
        guesses. "B" = planar face traversal, run ONLY on a view whose visible graph has no free
        end — with no dangling edge the figure is a closed planar subdivision and its faces are
        DEFINED by the angular order of the edges at each vertex, so walking it is a reading. It
        appears only where Tier A found nothing, and the direct-build path ignores it by design.
      views[].open_singles[] — [position, code, index] triples: the SINGLE-segment open chains,
        which are most of them. Nothing is abbreviated away — the id is 'O<position>', the class is
        that primitive's own `c`, the direction is 1 — and a chain whose id/class/direction are not
        all recoverable stays a full record in `open_chains` instead. Read the two together: a
        primitive named here is exactly as unattributed as one in `open_chains`.
      views[].open_chains[] — the MULTI-segment chains belonging to no contour: bend lines, centre
        lines, and (in an ortho view) silhouette fragments the chainer refused to guess through
        a T-junction.
      alignment[] — GRADED view pairs: shares='x' is a vertical projection pair, 'y' a horizontal
        one, and `grade` says how strong the evidence is. 'span' = the two share that axis's span
        edge-for-edge, the strongest. 'mid' = only the midpoints coincide — still a measurement,
        but silhouette ends may genuinely differ, and on a bend-note sheet a mid pair may relate
        the FLAT and the BENT state (two states of the part, not two projections). 'label' = a
        section caption ties the section view `b` to the view `a` carrying its cut line, with
        shares=null, so it survives a cross-scale section.
      view_graph — {solved, views:{vid: [h_axis, v_axis]}} when the pairs pin every view's two
        paper axes onto the part's three. TOPOLOGY ONLY, over ANONYMOUS axes ax0/ax1/ax2: which
        axis is depth and which view is "front" is the projection convention's SIGN, not a field.
        `solved: false` names why, and the joining is yours again.
      dimensions[] — `value` (TRUE), `kind` (linear|aligned|angular|diameter|radius|ordinate),
        `defpts` (the dimension's own reference points, sheet coords) and the owning `view`.
        `printed` is the string the CAD system actually DREW, taken from the dimension's own
        block, with `%%c`→Ø, `%%d`→°, `%%p`→± decoded (the symbol is evidence about what was
        dimensioned); `text` beside it is the raw undecoded override (e.g. '8x <>' = a count).
        The DWG→DXF route corrupts the stored measurement in two whole classes — an ANGULAR value
        comes back as 180+θ, a RADIUS loses its arc side — so the reader ARBITRATES: for radius and
        diameter the printed value wins outright, and for an angle it takes the candidate ≤ 180°.
        `printed_mismatch: true` marks what it could not settle.
        `kind_from: "printed"` marks a dimension whose TYPE the drawn symbol overruled — a plain
        linear dim drawn across a circle with a hand-typed Ø is re-emitted as `kind: "diameter"`.
        `measures` links a dimension to the primitive the reader matched it to.
      bend_notes[] — sheet metal: {dir UP|DOWN, angle_deg, radius, view} plus either `bend_line`
        (the line it annotates, that line's edge class, and `in_loop` — true would mean the match
        landed on outline geometry, which a bend line never is) or `unpaired` with the reason and
        the candidates. Direction comes from the NOTE; the class corroborates it.
      notes[] — other free text on the sheet (e.g. a bare '2 mm' thickness note, 'SECTION C-C'),
        text inside note blocks included.

    file_path: absolute path to the .DXF or .DWG.
    save_analysis (default True): also write `<name>.analysis-v<version>.json` beside the source —
        a research artifact for cross-part study, versioned by the analyzer. Same version
        overwrites. You never need to read it back: this answer already carries everything in it."""
    src = os.path.abspath(file_path)
    if not os.path.exists(src):
        return f"FAILED | FILE_NOT_FOUND | {src}"
    ext = os.path.splitext(src)[1].lower()
    if ext not in (".dxf", ".dwg"):
        return ("FAILED | UNSUPPORTED_TYPE | analyze_drawing reads .DXF or .DWG. "
                "A native .SLDDRW is a test-only path (analyze_slddrw_test).")
    # DWG is binary; the reader parses DXF only. SolidWorks opens the DWG as a drawing and exports
    # DXF — measured lossless for geometry, dimensions, DIMLFAC, linetypes and notes.
    dxf_path = src if ext == ".dxf" else os.path.splitext(src)[0] + ".dwg2dxf.dxf"

    def _load():
        """Convert (DWG) + read + save -> (art, cfg, None) | (None, None, error string)."""
        converted_from = None
        if ext == ".dwg":
            opened = _call_raw("open_document", {"file_path": src})
            if opened.get("status") != "COMPLETED":
                err = opened.get("error") or {}
                return None, None, f"FAILED | DWG_OPEN_FAILED | {err.get('code')}: {err.get('message')}"
            exported = _call_raw("export_document", {"format": "DXF", "file_path": dxf_path})
            _call_raw("close_document", {})      # the import is scratch — discard, never save
            if exported.get("status") != "COMPLETED":
                err = exported.get("error") or {}
                return None, None, f"FAILED | DWG_CONVERT_FAILED | {err.get('code')}: {err.get('message')}"
            converted_from = os.path.basename(src)
        if _draw is None:
            return None, None, f"FAILED | READER_UNAVAILABLE | {_DXF_READER_IMPORT_ERROR}"
        try:
            cfg = _draw.load_config()
            art = _draw.read(dxf_path, cfg)
        except Exception as exc:
            return None, None, f"FAILED | DXF_READ_FAILED | {type(exc).__name__}: {exc}"
        if converted_from:
            art["source"]["converted_from"] = converted_from
        if save_analysis:
            out = os.path.join(os.path.dirname(dxf_path),
                               "%s.analysis-v%s.json" % (os.path.splitext(os.path.basename(src))[0],
                                                         art["analysis_version"]))
            try:
                with open(out, "w", encoding="utf-8") as fh:
                    json.dump(_draw.wire_encode(art), fh, indent=1, ensure_ascii=False)
            except OSError:
                pass                              # the artifact is research-only, never the result
        return art, cfg, None

    art, cfg, err = _load()
    if err:
        return err
    full = _wire_json(_draw.wire_encode(art))

    # The GATE decides the result shape. The model must never be asked to choose between "give me
    # the analysis" and "just build it" BEFORE it has seen any geometry — that was the flaw in
    # making this an opt-in flag (ADR-064).
    try:
        verdict = _draw.assess(art, cfg)
    except Exception as exc:                      # noqa: BLE001 - a gate bug must not lose the read
        return f"NOT_DIRECT | GATE_ERROR | {type(exc).__name__}: {exc}\n{full}"

    if not verdict["direct"]:
        if mode in ("ir", "build"):
            return (f"FAILED | NOT_DIRECTLY_BUILDABLE | {verdict['reason']} | {verdict['detail']} — "
                    f"call analyze_drawing(mode='auto') and build with submit_feature_graph.")
        # ONE SHAPE, the whole analysis (payload phase 1, 2026-09-07). This used to be a SUMMARY
        # with the geometry pulled back per view, and the split was retired on its own measurement:
        # once the wire form landed, the complete artifact came out smaller on all 10 samples than
        # the summary plus the pulls it made necessary (f-3: 14.0 KB against 28.7).
        return (f"NOT_DIRECT | {verdict['reason']} | {verdict['detail']}\n"
                f"The whole analysis is below. Read it per get_recipe(section='reverse') and build "
                f"with submit_feature_graph.{_advisories(art)}\n{full}")

    try:
        graph = _draw.lower_flat_pattern(art, cfg, verdict)
    except Exception as exc:                      # noqa: BLE001
        return f"NOT_DIRECT | LOWERING_ERROR | {type(exc).__name__}: {exc}\n{full}"

    if mode == "ir":
        return _wire_json(graph)
    if mode == "auto":
        # The direct path reports its own skipped bends; the advisories add what the READ raised
        # (unread entities, a walked boundary, a mismatched dimension) — equally true when the
        # drawing turned out to be buildable without you.
        return _summarise_direct(art, verdict["summary"], graph) + _advisories(art)

    # mode == "build": the THIRD IR door — same _run_graph, same pycompiler (IR-ADR-005).
    s = verdict["summary"]
    text, ok, sv = _run_graph(graph, fresh_document=True)
    if not ok:
        return text
    tail = ["", "built from %s (sha256 %s) | state_version=%s"
            % (art["source"]["file"], art["source"]["sha256"][:12], sv),
            "VERIFY: analyze_model('mass_properties') should read V = %r m^3 "
            "(blank %g mm^2 x %g mm)." % (s["expected"]["volume_m3"], s["blank"]["area_mm2"],
                                          s["thickness"]["value_mm"]),
            "        then read one bend face back — volume, area and topology cannot see a "
            "mirrored fold (recipe R14)."]
    for r in s["skipped_bends"]:
        tail.append("NOT BUILT: %s bend at %s (%s). The blank volume is unchanged by it, so no "
                    "measurement will catch it — add it or declare it (R15)."
                    % (r["dir"], r["at"], r["reason"]))
    return text + "\n".join(tail)


# ---------------------------------------------------------------------------
# Tool: analyze_slddrw_test  (TEST/REFERENCE ONLY — was analyze_drawing until 2026-07-27)
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def analyze_slddrw_test(include_geometry: bool = False, include_relations: bool = False) -> str:
    """TEST/REFERENCE TOOL — reads a NATIVE SolidWorks drawing (.SLDDRW) through COM.

    ⚠ NOT the shipping drawing→part path. A .SLDDRW is SLDPRT-linked (its views go blank if the part
      is gone) and nobody ships one — real inputs are DXF/DWG (and PDF). This tool survives for two
      jobs ONLY: (a) checking a drawing YOU produced (do its dimensions match the model?), and
      (b) generating COM ground truth to compare a DXF reader against. For a DXF/DWG job do NOT use
      this tool and do NOT apply its discipline — the fields below (measures, section, normal_axis,
      relations, extent, center_marks) are COM constructs that DO NOT EXIST in a DXF.

    ⮕ Reconstructing a part from a NATIVE SLDDRW (test/comparison work only)? The FROZEN
      reverse-reading discipline is deliberately NOT on the MCP surface (ADR-069) — it is the repo
      file `cad-planner/slddrw-testing-recipe-usage.md`. Read it from disk when this tool is the
      job; port a rule out of it into the active recipe only as a conscious decision (ADR-063).

    Read-only, does NOT change state. Requires an active NATIVE drawing document. All lengths are
    METERS (angles radians), 6 decimals. Returns {view_count, dimension_count, views:[…]}, and the
    FIRST view is the drawing SHEET.

    view — {name, type (swDrawingViewTypes_e int), scale, pos [x,y], dimensions[], section?}
    dimension — {name, value_si, diametric?, attached?, anchors?}. `diametric` true means Ø is DATA,
      not a guess. `anchors` are the dim's reference points in MODEL space; an anchor beyond the base
      body's depth signals a feature that extends past it (offset-plane boss/loft), not bad data.
    section (SECTION views only) — {parent_view, cut_normal (the cutting plane's MODEL-space normal,
      i.e. the viewing direction), axis? (that normal snapped to X/Y/Z when axis-aligned), frame
      {origin, xdir, ydir}, label?}. A 2D coord maps to 3D as p = origin + u*xdir + v*ydir.

    include_geometry (default False) — each view's projected 2D geometry as clean primitives:
      {lines[{x1,y1,x2,y2}], curves[{n,x1,y1,xm,ym,x2,y2,cx?,cy?,r?}] (partial arcs as start/mid/end
      + fitted centre), circles[{cx,cy,r}] (FULL circles, true centre+radius), frame{origin,xdir,ydir,
      normal_axis}, extent? }. Source IView.GetPolylines7; heavier payload, so use it when you need
      the SHAPE, not for a dimension check. Two rules that fail silently if broken:
      · `normal_axis` is the view's viewing direction as a SIGNED principal axis. A circle is the
        cross-section of a feature whose axis runs along it, so circles in views with DIFFERENT
        normal_axis are DIFFERENT features — never chain them into one by radius alone.
      · `extent` {axis:[min,max]} is the SERVER-computed model-space span, frame signs applied. Read
        it before any "no material beyond X" claim; never re-derive a span from raw 2D coordinates,
        because a frame's direction components can be negative.

    include_relations (default False; forces include_geometry) — additive deterministic enrichment;
      pass BOTH for a reconstruction. Ids are positional per read: vid 'v<i>', and within a view
      c<i>/a<i>/l<i> for circles/curves/lines. Every relation carries `source` (closed enum) and
      `residual` (max deviation), so "why is this concentric?" is answerable by reading the field.
      · relations — concentric {members,center,radii} (TRUST these centres, never re-derive),
        equal_diameter {members,r,centers} (same Ø at distinct centres = twin bores), tangent.
      · measures — per dimension, the primitive id(s) it measures. `unattached:true` means it
        resolved to NO primitive: a loud gap to close from geometry, never to drop.
      · center_marks / centerlines — which circles carry marks (on:[ids]), which segments run
        through which (through:[ids]). A view that cannot expose its centerlines says so
        ({centerlines_reported, unreadable:true}) instead of guessing."""
    return _call("analyze_slddrw_test", {"include_geometry": include_geometry,
                                         "include_relations": include_relations})


# ---------------------------------------------------------------------------
# Tool: get_selection
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def get_selection() -> str:
    """Report what the USER currently has selected in the SolidWorks GUI — the inverse of index-based selection.
    When the user clicks geometry in the SolidWorks window (e.g. while telling you what to do — "put a hole on
    THIS face"), call this to learn exactly which entity they mean. Each selected item is mapped to the SAME
    stable index analyze_model(faces/edges) reports, so you can act on it immediately:
      • a FACE  → {type:'face', i, planar, normal?, area, point?} → create_sketch(on_face=True, face_index=i)
      • an EDGE → {type:'edge', i, start, end, mid}              → add_edge_feature(edge_indices='[i]')
      • a VERTEX → {type:'vertex', point:[x,y,z]};  a reference PLANE → {type:'plane', name}
    Returns {selected_count, selection:[...]}. i = -1 if a selected face/edge couldn't be matched to a
    solid-body index. Read-only: does NOT change state_version and does NOT clear the selection — but call it
    BEFORE any tool that clears the selection (create_sketch, add_edge_feature, extrude cut, etc.), otherwise
    the user's pick is gone. If selected_count is 0, ask the user to click the face/edge they mean."""
    return _call("get_selection", {})


# ---------------------------------------------------------------------------
# Tool: edit_sketch
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def edit_sketch(sketch_name: str) -> str:
    """Reopen an existing sketch for editing. Counterpart to create_sketch.
    sketch_name: exact name of the sketch feature in the feature tree (e.g. 'Sketch1').
    Returns COMPLETED with ActiveSketch set to sketch_name.
    After editing, call extrude_feature (or any feature) to exit the sketch."""
    return _call("edit_sketch", {"sketch_name": sketch_name})


# ---------------------------------------------------------------------------
# Tool: add_reference_geometry
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def add_reference_geometry(
    type: Literal["plane", "axis", "point"],
    ref_plane_name: str = "",
    offset: float = 0.0,
    entity1_name: str = "",
    entity1_type: str = "PLANE",
    entity2_name: str = "",
    entity2_type: str = "PLANE",
    px: float = 0.0,
    py: float = 0.0,
    pz: float = 0.0,
) -> str:
    """Create reference geometry (plane, axis, or point) in the active part.
    type='plane': offset plane from ref_plane_name by offset (meters). E.g. ref_plane_name='Front Plane', offset=0.05.
    type='axis': axis at intersection of two entities. Provide entity1_name, entity1_type, entity2_name, entity2_type (e.g. 'Top Plane'/'PLANE' and 'Right Plane'/'PLANE').
    type='point': reference point at vertex (px, py, pz). Coordinates must match an existing vertex exactly.
    Returns the created feature name (e.g. 'Plane1', 'Axis1', 'Point1')."""
    return _call(
        "add_reference_geometry",
        {
            "type": type,
            "ref_plane_name": ref_plane_name,
            "offset": offset,
            "entity1_name": entity1_name,
            "entity1_type": entity1_type,
            "entity2_name": entity2_name,
            "entity2_type": entity2_type,
            "px": px, "py": py, "pz": pz,
        },
    )


# ---------------------------------------------------------------------------
# Tool: create_pattern
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def create_pattern(
    pattern_type: Literal["linear", "circular", "mirror"],
    feature_name: str = "",
    spacing: Annotated[float, Field(
        gt=0, description="Linear pattern spacing in METERS")] = 0.01,
    count: Annotated[int, Field(
        ge=2, description="Total instances incl. seed (>= 2)")] = 2,
    direction: Literal["X", "Y", "Z"] = "X",
    count2: Annotated[int, Field(
        ge=1, description="Second-direction instances (1 = off)")] = 1,
    spacing2: Annotated[float, Field(
        gt=0, description="Second-direction spacing in METERS")] = 0.01,
    flip: bool = False,
    axis_name: str = "",
    angle: Annotated[float, Field(
        gt=0, le=360, description="Circular pattern angle in DEGREES")] = 90.0,
    equal_spacing: bool = True,
    features_json: str = "[]",
    plane: str = "",
    geometry_pattern: bool = False,
) -> str:
    """Create a linear, circular, or mirror feature pattern.
    pattern_type='linear': repeats feature_name along direction ('X', 'Y', or 'Z') with spacing (meters) and count instances.
        flip=True patterns toward the NEGATIVE axis (default False = positive).
        Direction maps to default planes: X→Right Plane normal, Y→Top Plane normal, Z→Front Plane normal.
        KNOWN LIMIT: count2/spacing2 are accepted but the second direction has no D2 entity wired —
        two-direction grids do NOT work in one call; compose a grid as a pattern OF a pattern instead.
    pattern_type='circular': repeats feature_name around axis_name (reference axis, e.g. 'Axis1') with count instances.
        Create the axis first with add_reference_geometry(type='axis', ...).
        equal_spacing=True (default): angle is the TOTAL spread (use 360 for a full ring); count instances are
            evenly divided across it and NEVER overlap (count distinct == count).
        equal_spacing=False: angle is the spacing BETWEEN adjacent instances. count*angle can exceed 360, in which
            case later instances WRAP and overlap earlier ones, so the number of DISTINCT instances < count.
        Round-trip: analyze_model reports a CirPattern's instances, equal_spacing, spacing_deg plus
        the EFFECTIVE distinct_instances and a `wraps` flag. Either replay the stored form verbatim,
        or — when it wraps — use the equivalent full ring (count=distinct_instances, angle=360,
        equal_spacing=True). Both give the same geometry.
    pattern_type='mirror': mirrors one or more FEATURES about a plane. features_json = a JSON array of
        feature tree names, e.g. '["Edge-Flange1","Sketched Bend2"]' (feature_name works for a single
        feature). plane = the mirror plane name ('Right Plane' default; canonical English default-plane
        names work on any localization, or a created reference plane's name). geometry_pattern=True
        mirrors the geometry without solving each feature (faster; default False = SW default).
        Round-trip: analyze_model(features) reports a MirrorPattern's mirror:{plane, features} — replay
        those values verbatim. feature_name/spacing/count/direction/axis params are ignored for mirror.
    On COMPLETED the result includes result_geometry {volume, faces, edges} after the pattern — verify from
        this instead of a separate analyze_model.
    Returns the created pattern feature name."""
    return _call(
        "create_pattern",
        {
            "pattern_type": pattern_type,
            "feature_name": feature_name,
            "spacing": spacing,
            "count": count,
            "direction": direction,
            "count2": count2,
            "spacing2": spacing2,
            "flip": flip,
            "axis_name": axis_name,
            "angle": angle,
            "equal_spacing": equal_spacing,
            "features_json": features_json,
            "plane": plane,
            "geometry_pattern": geometry_pattern,
        },
    )


# ---------------------------------------------------------------------------
# Tool: set_part_material
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def set_part_material(
    material_name: str,
    library: str = "SolidWorks Materials",
) -> str:
    """Assign a material to the active part document. Applied to all configurations.
    material_name: exact library name (e.g. '1060 Alloy', 'AISI 1020', 'ABS') — must match the SOLIDWORKS Materials database exactly (e.g. '1060 Alloy', NOT 'Aluminum 1060 Alloy').
    library: material library name (default 'SolidWorks Materials').
    The applied material is visible in Mass Properties and the feature tree.
    Requires an active part document — not a drawing."""
    return _call("set_part_material", {"material_name": material_name, "library": library})


# ---------------------------------------------------------------------------
# Tool: sheet_metal_feature
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def sheet_metal_feature(
    feature_type: Literal["base_flange", "edge_flange", "edge_flange_sketch", "edge_flange_finish",
                          "flat_pattern", "sketched_bend"],
    thickness: Annotated[float, Field(
        gt=0, description="Sheet thickness in METERS")] = 0.001,
    bend_radius: Annotated[float, Field(
        gt=0, description="Bend radius in METERS")] = 0.001,
    k_factor: Annotated[float, Field(
        gt=0, lt=1, description="Neutral-axis K-factor (0..1)")] = 0.5,
    ex: float = 0.0,
    ey: float = 0.0,
    ez: float = 0.0,
    flange_length: Annotated[float, Field(
        gt=0, description="Edge flange length in METERS; use >= 2*thickness+1mm (KNOWN-LIMITATIONS #11)")] = 0.02,
    angle: Annotated[float, Field(
        gt=0, le=180, description="Bend angle in DEGREES (edge_flange and sketched_bend; default 90)")] = 90.0,
    use_default_radius: bool = False,
    flip: bool = False,
    bend_position: Literal["centerline", "material_inside", "material_outside", "bend_outside"] = "centerline",
    fixed_face_index: int = -1,
    fixed_x: float = 0.0,
    fixed_y: float = 0.0,
    fixed_z: float = 0.0,
    reverse_thickness: bool = False,
    symmetric_thickness: bool = False,
    clear_profile: bool = True,
    edge_index: int = -1,
) -> str:
    """Create sheet metal features on the active part. Lengths METERS, angles DEGREES.

    'base_flange' — a sheet metal base from the ACTIVE sketch profile; exits sketch mode.
        thickness · bend_radius (default = thickness) · k_factor (0.5).
        THICKENING SIDE IS A BUILD DECISION, and it is silent when wrong: `reverse_thickness`
        thickens to the OPPOSITE side of the sketch plane, `symmetric_thickness` splits ±t/2.
        Rebuilding an analyzed part, replay the original's own flags (analyze_model(features)
        reports them on the SMBaseFlange) — they also set the sheet orientation later bends fold
        against, so a wrong side fails downstream and less clearly.
    'edge_flange' — a DEFAULT-profile (full-edge-width) flange on an existing sheet metal edge.
        Select by edge_index (from analyze_model(edges), PREFERRED — a coordinate pick can miss a
        real edge) or a point ex/ey/ez on it. flange_length · angle (90).
    'edge_flange_sketch' + 'edge_flange_finish' — the CUSTOM-profile flange, in two calls. The
        sketch call takes the attach edge (edge_index preferred, else ex/ey/ez), generates the
        edge-linked profile sketch (the API accepts ONLY a sketch it generated), clears it
        (clear_profile) and leaves it ACTIVE, echoing the sketch's MEASURED frame — express your
        profile in THAT frame. Draw with add_sketch_entity, then call the finish with the SAME
        edge (+ angle, bend_radius or use_default_radius, bend_position). The profile defines the
        outline, so flange_length is ignored.
    'sketched_bend' — bends the sheet about the bend LINE(S) in the ACTIVE sketch (draw them on a
        sheet face with create_sketch + add_sketch_entity first; the sketch must still be active).
        The side that stays PUT is the fixed face: fixed_face_index (from analyze_model(faces),
        PREFERRED) or fixed_x/y/z, a 3D point ON that face. angle (90) · bend_radius, or
        use_default_radius=True to take the sheet's default and ignore it · flip reverses the fold
        direction · bend_position 'centerline' (default) | 'material_inside' | 'material_outside' |
        'bend_outside', matching the `position` analyze_model(features) reports on an SM3dBend, so a
        recipe value replays as-is. ONE sketch with SEVERAL bend lines makes one feature with one
        bend per line.
    'flat_pattern' — unfolds every bend. No extra parameters; needs an existing base_flange.

    A wrong fold is expensive: there is no sketch-entity move/delete tool, so repairing one costs
    deleting the feature and re-creating it. And a mirrored fold is invisible to volume, area AND
    topology alike — read one bend face back after building (recipe R14)."""
    params = {
        "feature_type": feature_type,
        "thickness": thickness,
        "bend_radius": bend_radius,
        "k_factor": k_factor,
        "ex": ex, "ey": ey, "ez": ez,
        "flange_length": flange_length,
        "angle": angle,
        "use_default_radius": use_default_radius,
        "flip": flip,
        "bend_position": bend_position,
        "fixed_x": fixed_x, "fixed_y": fixed_y, "fixed_z": fixed_z,
        "reverse_thickness": reverse_thickness,
        "symmetric_thickness": symmetric_thickness,
        "clear_profile": clear_profile,
    }
    if fixed_face_index >= 0:
        params["fixed_face_index"] = fixed_face_index
    if edge_index >= 0:
        params["edge_index"] = edge_index
    return _call("sheet_metal_feature", params)


# ---------------------------------------------------------------------------
# Tool: modify_dimension
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def modify_dimension(
    name: str,
    value: float,
) -> str:
    """Change a named display dimension's value — the UNIVERSAL parametric edit (variant keystone).
    name: the dimension full-name exactly as analyze_model(features) reports it, e.g.
          'D1@Boss-Extrude1@Part.Part' or 'D1@Sketch1'.
    value: the new value in SI units — METERS for a length/distance, RADIANS for an angle
          (e.g. 0.036 = 36mm; 0.5236 = 30°). NOT mm/degrees.
    Workflow: analyze_model(analysis_type='features') → read a feature's dimension name + value (SI) →
          modify_dimension(name, new_value) → analyze again to confirm. Bumps state_version (geometry
          changes, unlike analyze). On COMPLETED the result carries result_geometry
          {dimension, requested, effective}: the value read back from SolidWorks AFTER rebuild — verify
          it matches your intent (an equation/relation may have driven it elsewhere). This is how you
          build variants (e.g. resize a boss, widen a slot, change a gear's tooth-spacing)."""
    return _call("modify_dimension", {"name": name, "value": value})


# ---------------------------------------------------------------------------
# Tool: edit_feature
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def edit_feature(
    feature_name: str,
    action: Literal["suppress", "unsuppress", "delete", "rename"],
    new_name: str = "",
) -> str:
    """Structurally edit an existing feature by name (suppress / unsuppress / delete / rename).
    feature_name: exact feature-tree name, e.g. 'Boss-Extrude1', 'Cut-Extrude1', 'CirPattern1'
          (get the names from analyze_model(analysis_type='features')).
    action='suppress':   removes the feature (and anything dependent on it) from the build WITHOUT deleting it.
    action='unsuppress': brings a suppressed feature back into the build.
    action='delete':     permanently removes the feature (works for ANY feature type, incl. sketches and reference geometry).
    action='rename':     renames the feature; requires new_name.
    new_name: required only for action='rename' (ignored otherwise).
    Bumps state_version. WARNING: suppress and delete CHANGE the model topology, so any coordinate /
    edge / face selection captured earlier may no longer resolve — re-run analyze_model
    (analysis_type='edges' or 'features') after a structural edit before selecting geometry again."""
    return _call(
        "edit_feature",
        {"feature_name": feature_name, "action": action, "new_name": new_name},
    )


# ---------------------------------------------------------------------------
# Tool: activate_document
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def activate_document(title: str) -> str:
    """Switch the ACTIVE SolidWorks document to an already-open one by its title (e.g. 'gear' or
    'gear.SLDPRT'). Use this to read/compare another open part — e.g. activate the original, analyze_model
    it, then activate your copy — without OS window switching. Does NOT change geometry or state_version.
    The document must already be OPEN. Returns the now-active document title."""
    return _call("activate_document", {"title": title})


# ---------------------------------------------------------------------------
# RESOURCE surface: the IR-generation recipe + the two cad-planner contract JSONs
#
# These were the `get_recipe(section=...)` tool until 2026-07-30 (ADR-069). They are now MCP
# *resources*: the content is large, near-static, identical for every caller and re-read
# constantly — exactly what `resources/read` + the 2026-07-28 `ttlMs`/`cacheScope` hints are for.
# MEASURED reason to move (not estimated): `get_recipe` cost 2,369 chars of `tools/list` on EVERY
# turn (2.97% of the 47-tool surface; 1,928 of it the docstring), paid whether or not any recipe
# was read. A resource read costs ~50 chars MORE than the equivalent tool result (the
# `uri`+`mimeType` wrapper), so the win is entirely the per-turn surface — which is why the tool
# had to GO, not sit beside the resources.
#
# Shape decided with the user (2026-07-30):
#   - EVERY section is its own STATIC resource. Probe finding: templated URIs are readable but
#     appear in NO listing (`resources/list` shows static entries only), so a template-only
#     surface would be undiscoverable. Static-per-section makes ONE `resources/list` the manifest.
#   - `recipe://usage/{section}` stays as a TEMPLATE ALIAS so a slug guessed from the index
#     still resolves. Concrete resources win over templates in the SDK's resolution order, so
#     the static entries above are never shadowed by it.
#   - Version lives in the BODY only (`Version: 0.21.0`), never in the URI: bumps must not
#     break the URI pointers embedded in the tool docstrings below.
#   - The FROZEN `slddrw-testing-recipe-usage.md` pair (ADR-063) is deliberately NOT served —
#     it stays a repo file; port from it consciously if a rule there is wanted again.
# The recipe's PRIVATE full edition (`recipe.md`) is NEVER served — it carries lesson history and
# provenance, is gitignored, and only the PUBLIC `-usage` twin appears on this surface.
# ---------------------------------------------------------------------------
_CAD_PLANNER_DIR = os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..", "..", "cad-planner"))

# The served section slugs, EXPLICIT (this replaces get_recipe's `Literal[...]`). The surface is
# under the user's control by decision: a new `## slug — title` in recipe-usage.md does NOT
# publish itself as a resource until it is added here, deliberately.
_RECIPE_SECTIONS = (
    "contract", "canonicalization", "forward", "mapping", "mapping_part",
    "mapping_sheet_metal", "mapping_assembly", "verification", "reverse", "coverage",
)


def _recipe_sections():
    """Parse cad-planner/recipe-usage.md into its version header + {slug: (title, body)} by the
    '## slug — title' section headers. Read fresh on every call — the file ships with the server
    and is small; no caching keeps edits live without a reconnect."""
    path = os.path.join(_CAD_PLANNER_DIR, "recipe-usage.md")
    with open(path, "r", encoding="utf-8-sig") as fh:
        text = fh.read()
    sections = {}
    current = None
    for line in text.splitlines():
        if line.startswith("## ") and " — " in line:
            slug, _, title = line[3:].partition(" — ")
            current = slug.strip()
            sections[current] = [title.strip(), []]
        elif current is not None:
            sections[current][1].append(line)
    header = text.split("\n## ", 1)[0]  # the version/preamble block above the first section
    return header, {k: (t, "\n".join(body).strip()) for k, (t, body) in sections.items()}


def _recipe_section_text(section: str) -> str:
    """One section, rendered with its own `## slug — title` header restored.

    Read FRESH from disk on every call (via _recipe_sections), so editing recipe-usage.md goes
    live with no reconnect — only the registration metadata below is snapshotted at import."""
    _header, sections = _recipe_sections()
    if section not in sections:
        # MUST be ResourceError: the SDK re-raises ResourceError/MCPError verbatim but replaces any
        # other exception's message with a generic "Error creating resource from template <uri>"
        # (templates.py). Verified live — a plain ValueError left the caller with no idea what the
        # valid slugs were, and a model that guesses (e.g. 'drawing', which was in the retired
        # get_recipe enum but is not a section) needs the list to self-correct in one step.
        raise ResourceError(
            f"'{section}' is not a served recipe section. Served: "
            f"{', '.join(_RECIPE_SECTIONS)}. Read recipe://usage/index for the full surface.")
    title, body = sections[section]
    return f"## {section} — {title}\n\n{body}"


def _contract_json(filename: str) -> str:
    with open(os.path.join(_CAD_PLANNER_DIR, "contracts", filename),
              "r", encoding="utf-8-sig") as fh:
        return fh.read()


# ---------------------------------------------------------------------------
# The IR schema, as the MODEL needs it (payload trim, 2026-09-07)
# ---------------------------------------------------------------------------
# `feature-graph.schema.json` serves two audiences at once and only one of them is the model.
# Gate 3 (`pycompiler/tests/test_ir_schema_contract.py`) machine-diffs every ARRAY under
# `covered_subset` against ir_schema.py's frozensets, and the prose beside those arrays documents
# that contract for whoever maintains it. So the FILE cannot be trimmed — the trim happens HERE,
# at serve time, on the copy the model receives.
#
# Dropped for every profile (measured; none of it is model-facing):
#   _meta.status                                2,135 B — the version narrative (IR-ADR references,
#                                                         "do NOT freeze before the MAT v1 set
#                                                         arrives"): a developer's changelog.
#   covered_subset._note/._grammar_note/.assumptions
#                                               1,529 B — half of it gate 3's own machine-diff
#                                                         instructions ("every ARRAY in this block
#                                                         is diffed against ir_schema.py").
# The covered_subset ARRAYS are left untouched: ~1,400 B carrying the entire capability list, the
# highest-value bytes in the file.
_SCHEMA_DEV_ONLY_SUBSET_KEYS = ("_note", "_grammar_note", "assumptions")

# What a PROFILE drops on top of that. Two values, because only two of them mean anything:
# `part` keeps every feature type ON PURPOSE — a drawing job does not know it is sheet metal until
# R21 decides, so the sheet-metal vocabulary must stay reachable. (A 'sheet_metal' profile would
# therefore be byte-identical to 'part', and an 'assembly' profile identical to 'all'; offering
# either would advertise a filter that does not exist.)
_SCHEMA_PROFILE_DROPS = {
    "all": (),
    "part": ("assembly_types_v06",),
}


def _feature_graph_schema_text(profile: str = "all") -> str:
    """`feature-graph.schema.json` with the developer-only blocks removed, re-serialized compactly.

    Falls back to the raw file if it will not parse: a malformed schema must still reach the caller
    (an unreadable capability registry is a loud failure, not a silent empty one).
    """
    raw = _contract_json("feature-graph.schema.json")
    try:
        doc = json.loads(raw)
    except ValueError:
        return raw
    meta = doc.get("_meta")
    if isinstance(meta, dict):
        meta.pop("status", None)
    subset = doc.get("covered_subset")
    if isinstance(subset, dict):
        for key in _SCHEMA_DEV_ONLY_SUBSET_KEYS:
            subset.pop(key, None)
    for key in _SCHEMA_PROFILE_DROPS.get(profile, ()):
        doc.pop(key, None)
    return json.dumps(doc, ensure_ascii=False, indent=1)


# ---------------------------------------------------------------------------
# Tool: get_recipe  (RESTORED 2026-09-03, ADR-077 — reverses ADR-069's retirement)
# ---------------------------------------------------------------------------
# ADR-069 moved the recipe from a TOOL to 13 MCP resources on a measured saving of 2,146 chars of
# `tools/list` per turn. The measurement was right; the assumption underneath it was not. MCP has
# three primitives and only ONE of them is in the MODEL's action space:
#
#     tool      tools/call       <- the model can call this
#     resource  resources/read   <- the CLIENT calls this; the model needs a host-provided bridge
#     prompt    prompts/get      <- the user invokes this
#
# ADR-069 probed the host and found it DID bridge resources for the model, so the migration looked
# safe. That bridge is gone: neither this host nor the one running the isolated tests exposes any
# `ReadMcpResource`-style tool, so the recipe left the model's action space entirely. Two isolated
# reconstructions of f-3 both reported they could not read `recipe://usage/reverse` and proceeded
# on their own judgement — which is exactly the "a resource no host reads is WORSE than a tool"
# risk ADR-069 named and accepted. A server cannot push a resource at a model; the only handle we
# can put in a model's hand is a TOOL. So the tool comes back.
#
# The RESOURCES STAY. They are spec-correct, the user can attach them by hand, and a host that
# does bridge them still benefits. Both channels read the same `recipe-usage.md` on every call, so
# there is exactly one copy of the rules and no sync burden.
@mcp.tool(structured_output=False)
def get_recipe(
    section: Literal["index", "contract", "canonicalization", "forward", "mapping",
                     "mapping_part", "mapping_sheet_metal", "mapping_assembly", "verification",
                     "reverse", "coverage", "feature_graph_schema",
                     "analysis_artifact_schema"] = "index",
    profile: Literal["all", "part"] = "all",
) -> str:
    """The IR-generation recipe — REQUIRED READING before writing any Feature Graph IR
    (an artifact's `ir.graph`), reconstructing a part from a drawing, or producing a drawing
    meant for reconstruction. Serves the rules section-by-section so token cost stays
    proportional to the task.

    Call order for the ARTIFACT→IR flow: 'contract' + 'canonicalization' + 'mapping' first,
    then the vocabulary section matching the document ('mapping_part' / 'mapping_sheet_metal' /
    'mapping_assembly'), then 'verification' before labeling anything. 'forward' holds the
    INTENT→IR authoring discipline (grammar cheat-sheet, anchor design without an original,
    computed-expectation self-verification) — read it FIRST when writing a graph for
    submit_feature_graph from design intent. 'reverse' holds the DXF/DWG drawing→part
    reconstruction discipline (scale/DIMLFAC, edge classes, view clustering, projection
    standard, contour tiers, sheet-metal bend arithmetic) — read it FIRST when rebuilding a part
    from a 2D drawing, before you interpret a single number of `analyze_drawing`'s answer.

    section='index' (default): the version header + a one-line table of contents.
    section='feature_graph_schema': the Feature Graph IR schema / capability registry JSON —
        the node types and params the compiler accepts (what is NOT in it cannot be built).
        `profile='part'` drops the ASSEMBLY vocabulary from the answer (~2.4 KB) and is what a
        part or drawing→part job wants; every feature type stays, sheet metal included, because a
        drawing is not known to be sheet metal until you decide it (R21). `profile='all'`
        (default) is the whole registry.
    section='analysis_artifact_schema': the persistent analysis-artifact contract JSON
        (identity/hash, recipe, parameters, ir block + the formal 'verified' definition).
    The same rules are also published as `recipe://usage/*` MCP resources; use whichever your
    host actually lets you reach. Read-only; does NOT touch SolidWorks or state_version."""
    if section == "feature_graph_schema":
        return _feature_graph_schema_text(profile)
    if section == "analysis_artifact_schema":
        return _contract_json("analysis-artifact.schema.json")
    header, sections = _recipe_sections()
    if section == "index":
        toc = "\n".join(f"- {slug} — {title}" for slug, (title, _b) in sections.items())
        return (f"{header.strip()}\n\nSections (pass as `section`):\n{toc}\n"
                "- feature_graph_schema — the IR schema / capability registry (JSON)\n"
                "- analysis_artifact_schema — the analysis-artifact contract (JSON)")
    return _recipe_section_text(section)


@mcp.resource("recipe://usage/index", name="recipe_index", mime_type="text/markdown",
              description="START HERE for IR work: the recipe version header + every section URI. "
                          "Rules for turning an analysis artifact or a 2D drawing into a Feature "
                          "Graph IR.")
def _recipe_index() -> str:
    """The table of contents, generated from the file so it can never drift from what is served."""
    header, sections = _recipe_sections()
    lines = []
    for slug in _RECIPE_SECTIONS:
        title = (sections.get(slug) or ("(missing from recipe-usage.md)",))[0]
        lines.append(f"- recipe://usage/{slug} — {title}")
    return (
        f"{header.strip()}\n\n"
        f"Sections (read by URI):\n" + "\n".join(lines) + "\n\n"
        "Contracts:\n"
        "- schema://feature-graph — the IR schema / capability registry (JSON). What is NOT in\n"
        "  it cannot be built; it doubles as the capability list.\n"
        "- schema://analysis-artifact — the persistent analysis-artifact contract (JSON):\n"
        "  identity/hash, recipe, parameter table, ir block + the formal 'verified' definition.\n\n"
        "Reading order — ARTIFACT→IR: contract + canonicalization + mapping, then the vocabulary\n"
        "section matching the document (mapping_part / mapping_sheet_metal / mapping_assembly),\n"
        "then verification BEFORE labeling anything. INTENT→IR (no original part): forward.\n"
        "DXF/DWG DRAWING→PART: reverse."
    )


def _register_recipe_sections() -> None:
    """Register one STATIC resource per served section.

    Static (not template-only) because a templated URI appears in NO listing — `resources/list`
    carries concrete resources only — so this is what makes the rule set DISCOVERABLE: one
    `resources/list` call returns every section with its title as the description.

    Titles are snapshotted at import for the descriptions (the listing metadata is fixed at
    registration time); the BODIES stay fresh per read. A missing/unreadable recipe-usage.md
    degrades to a generic description instead of killing the MCP server at startup — the same
    stance as the lazily-imported compiler.
    """
    try:
        _header, sections = _recipe_sections()
    except OSError:
        sections = {}
    for slug in _RECIPE_SECTIONS:
        title = (sections.get(slug) or ("",))[0]
        reader = (lambda s: lambda: _recipe_section_text(s))(slug)
        reader.__name__ = f"_recipe_{slug}"
        mcp.resource(
            f"recipe://usage/{slug}",
            name=f"recipe_{slug}",
            mime_type="text/markdown",
            description=(f"IR recipe — {title}" if title
                         else f"IR recipe section '{slug}' (see recipe://usage/index)"),
        )(reader)


_register_recipe_sections()


@mcp.resource("recipe://usage/{section}", name="recipe_section", mime_type="text/markdown",
              description="Alias: any recipe-usage.md section by slug. The per-section URIs above "
                          "are the discoverable form; this only catches a slug read off the index.")
def _recipe_section_alias(section: str) -> str:
    return _recipe_section_text(section)


@mcp.resource("schema://feature-graph", name="feature_graph_schema",
              mime_type="application/json",
              description="The CAD-neutral Feature Graph IR schema AND capability registry — the "
                          "node types and params the deterministic compiler accepts. What is not "
                          "in it cannot be built. Read before authoring any ir.graph.")
def _feature_graph_schema() -> str:
    # Same trimmed copy the tool serves at profile='all' — one shape, whichever channel reaches
    # the model. A resource URI takes no arguments, so the resource is always the full registry.
    return _feature_graph_schema_text("all")


@mcp.resource("schema://analysis-artifact", name="analysis_artifact_schema",
              mime_type="application/json",
              description="The persistent per-file analysis-artifact contract "
                          "(.solidpilot/*.analysis.json): identity/hash, recipe, lifted parameter "
                          "table, ir block + the formal 'verified' definition.")
def _analysis_artifact_schema() -> str:
    return _contract_json("analysis-artifact.schema.json")


# ---------------------------------------------------------------------------
# Tool: save_analysis  (Phase A analysis pipeline — ADAPTER-ONLY, no C#; ADR-040)
# ---------------------------------------------------------------------------
ANALYSIS_SCHEMA_VERSION = "0.1.0-draft"  # cad-planner/contracts/analysis-artifact.schema.json


def _call_raw(tool_name: str, params: dict) -> dict:
    """Like _call() but returns the RAW ExecutionResponse dict (no string mapping, no raise).

    Used by adapter-side orchestration (save_analysis) that needs the structured payloads the
    analyze tools carry in `result_geometry`. Same one-shot resync-retry on INVALID_STATE_VERSION."""
    global _state_version
    timeout = _timeout_for(tool_name)
    response = call_tool(tool_name, _next_operation_id(), _state_version, params, timeout)
    if _is_state_mismatch(response):
        _state_version = get_state()
        response = call_tool(tool_name, _next_operation_id(), _state_version, params, timeout)
    _update_state_version(response)
    return response


def _analysis_items(response: dict) -> list:
    """Return the `cadState.features` list an analyze_model call carries its payload in.

    Payload shapes (C#-owned): 'features' mode = ONE item holding the whole recipe as a JSON
    string; 'mass_properties'/'geometry' modes = 'key=value' strings. Raises RuntimeError on a
    FAILED response (mirrors map_response's error discipline)."""
    if response.get("status") != "COMPLETED":
        err = response.get("error") or {}
        raise RuntimeError(f"{err.get('code')}: {err.get('message')}")
    return (response.get("cadState") or {}).get("features") or []


def _kv_dict(items: list) -> dict:
    """Parse ['volume=4,22503E-06', 'faces=12', ...] into a dict with real numbers.

    Numeric values may use a COMMA decimal separator (localized SolidWorks formatting);
    normalize to float, and to int for plain integer counts."""
    out = {}
    for item in items:
        if not isinstance(item, str) or "=" not in item:
            continue
        key, _, raw = item.partition("=")
        raw = raw.strip()
        try:
            num = float(raw.replace(",", "."))
            is_plain_int = raw.isdigit() or (raw.startswith("-") and raw[1:].isdigit())
            out[key.strip()] = int(num) if is_plain_int else num
        except ValueError:
            out[key.strip()] = raw
    return out


def _collect_parameters(node, out: list, feature_name: str = "") -> None:
    """Walk the features recipe and lift dimension entries into the flat named-parameter table.

    A dimension entry is a dict whose 'name' is a FULL dimension name (contains '@', e.g.
    'D1@Boss-Extrude1@Part1.Part' — the modify_dimension target) with a numeric value under
    'value_si' or 'value'. Tolerant by design: the exact recipe keys are C#-owned; anything that
    doesn't match the shape is simply not lifted."""
    if isinstance(node, dict):
        owner = node.get("name") if node.get("type") else None
        value = node.get("value_si", node.get("value"))
        name = node.get("name")
        if isinstance(name, str) and "@" in name and isinstance(value, (int, float)):
            out.append({"name": name, "value_si": value, "feature": feature_name})
            return
        for child in node.values():
            _collect_parameters(child, out, owner or feature_name)
    elif isinstance(node, list):
        for child in node:
            _collect_parameters(child, out, feature_name)


@mcp.tool(structured_output=False)
def save_analysis(file_path: str) -> str:
    """Analyze a part FILE and persist its analysis ARTIFACT — the entry tool of the analysis
    pipeline. Opens the part (activates it if already open), runs the standard reads (features
    recipe + mass_properties + geometry), computes the file's sha256, and writes
    `<folder>/.solidpilot/<filename>.analysis.json` per
    cad-planner/contracts/analysis-artifact.schema.json.

    The artifact is a CACHE of the file's state at analysis time: consumers must compare
    identity.source_hash against the current file and re-analyze on a mismatch. The `ir` block
    is left null here — the AI/IR pass fills it later: BEFORE writing an ir.graph, read the MCP
    resource `recipe://usage/index` for the mapping rules. The part is left OPEN and
    ACTIVE for follow-up work.

    file_path: absolute path of the .SLDPRT part OR .SLDASM assembly to analyze (drawing
        artifacts arrive with later pipeline steps). An assembly artifact's recipe holds the
        component tree (tree order, full transforms) + mates (creation order, enum types,
        entity anchors) + assembly mass properties.
    Returns a token-frugal summary (artifact path + counts) — the artifact JSON stays on disk;
    read it from there when the full content is needed."""
    src = os.path.abspath(file_path)
    if not os.path.isfile(src):
        return f"FAILED | FILE_NOT_FOUND | {src}"
    is_assembly = src.lower().endswith(".sldasm")
    if not src.lower().endswith(".sldprt") and not is_assembly:
        return ("FAILED | UNSUPPORTED_TYPE | save_analysis analyzes .SLDPRT parts and .SLDASM "
                "assemblies (drawing artifacts come with later pipeline steps)")
    with open(src, "rb") as fh:
        sha = hashlib.sha256(fh.read()).hexdigest()

    # Open (or activate, if it is already open under its title).
    opened = _call_raw("open_document", {"file_path": src})
    if opened.get("status") == "FAILED":
        activated = _call_raw("activate_document", {"title": os.path.basename(src)})
        if activated.get("status") == "FAILED":
            err = opened.get("error") or {}
            return f"FAILED | OPEN_FAILED | {err.get('code')}: {err.get('message')}"

    if is_assembly:
        return _save_assembly_analysis(src, sha)

    try:
        features_items = _analysis_items(_call_raw("analyze_model", {"analysis_type": "features", "name": ""}))
        mass_kv = _kv_dict(_analysis_items(_call_raw("analyze_model", {"analysis_type": "mass_properties", "name": ""})))
        geometry = _kv_dict(_analysis_items(_call_raw("analyze_model", {"analysis_type": "geometry", "name": ""})))
    except RuntimeError as ex:
        return f"FAILED | ANALYZE_FAILED | {ex}"

    notes = []
    if not features_items:
        return "FAILED | NO_PAYLOAD | analyze_model(features) returned an empty payload"
    try:
        features = json.loads(features_items[0])
    except (ValueError, TypeError):
        features = {"raw": features_items}
        notes.append("features payload was not parseable JSON — stored raw (reader refinement pending)")

    mass = {
        "volume_m3": mass_kv.get("volume"),
        "surface_area_m2": mass_kv.get("surface_area"),
        "cg": {"x": mass_kv.get("cx"), "y": mass_kv.get("cy"), "z": mass_kv.get("cz")},
    }

    parameters: list = []
    _collect_parameters(features, parameters)

    # V1 relationships: drawing files next to the part whose stem is the part's stem, optionally
    # followed by a suffix word (e.g. 'part-1 drawing.SLDDRW' for 'part-1.SLDPRT').
    folder = os.path.dirname(src)
    stem = os.path.splitext(os.path.basename(src))[0].lower()
    drawings = sorted(
        os.path.join(folder, f) for f in os.listdir(folder)
        if f.lower().endswith(".slddrw")
        and (os.path.splitext(f)[0].lower() == stem
             or os.path.splitext(f)[0].lower().startswith(stem + " "))
    )

    artifact = {
        "identity": {
            "source_path": src,
            "source_filename": os.path.basename(src),
            "source_hash": "sha256:" + sha,
            "source_mtime": datetime.fromtimestamp(os.path.getmtime(src), timezone.utc).isoformat(),
            "schema_version": ANALYSIS_SCHEMA_VERSION,
            "analyzed_at": datetime.now(timezone.utc).isoformat(),
            "generator": {"kind": "deterministic"},
        },
        "document_type": "part",
        "recipe": {
            "features": features,
            "mass_properties": mass,
            "geometry": geometry,
            "bbox": None,
            "material": None,
            "equations": [],
        },
        "parameters": parameters,
        "ir": None,
        "relationships": {"drawings": drawings, "category": None, "cluster_signals": {}},
        "notes": notes + ["bbox/material/equations extraction pending refinement (A2 live pass)"],
    }

    out_dir = os.path.join(folder, ".solidpilot")
    os.makedirs(out_dir, exist_ok=True)
    out_path = os.path.join(out_dir, os.path.basename(src) + ".analysis.json")
    with open(out_path, "w", encoding="utf-8") as fh:
        json.dump(artifact, fh, ensure_ascii=False, indent=2)

    geo = geometry if isinstance(geometry, dict) else {}
    feature_count = len(features.get("features", [])) if isinstance(features, dict) else "?"
    return (f"COMPLETED | artifact={out_path} | features={feature_count} | "
            f"parameters={len(parameters)} | geometry={{bodies:{geo.get('bodies')},"
            f"faces:{geo.get('faces')},edges:{geo.get('edges')},vertices:{geo.get('vertices')}}} | "
            f"drawings_linked={len(drawings)} | hash=sha256:{sha[:12]}")


def _save_assembly_analysis(src: str, sha: str) -> str:
    """The .SLDASM branch of save_analysis (Phase B): components (tree order, full transforms)
    + mates (creation order, enum types, entity anchors) + assembly mass properties. Component
    source files get their own sha256 in relationships so staleness is loud per part
    (ADR-047b: the assembly references part FILES — their artifacts live separately)."""
    try:
        comp_items = _analysis_items(_call_raw("analyze_assembly", {"analysis_type": "components"}))
        mate_items = _analysis_items(_call_raw("analyze_assembly", {"analysis_type": "mates"}))
        mass_kv = _kv_dict(_analysis_items(_call_raw("analyze_model", {"analysis_type": "mass_properties", "name": ""})))
    except RuntimeError as ex:
        return f"FAILED | ANALYZE_FAILED | {ex}"
    try:
        components = json.loads(comp_items[0]) if comp_items else {}
        mates = json.loads(mate_items[0]) if mate_items else {}
    except (ValueError, TypeError) as ex:
        return f"FAILED | PAYLOAD_UNPARSEABLE | {ex}"

    part_files = []
    seen_paths = set()
    for c in components.get("components", []):
        p = c.get("path")
        if not p or p in seen_paths:
            continue
        seen_paths.add(p)
        entry = {"path": p}
        if os.path.isfile(p):
            with open(p, "rb") as fh:
                entry["hash"] = "sha256:" + hashlib.sha256(fh.read()).hexdigest()
        else:
            entry["missing"] = True
        part_files.append(entry)

    artifact = {
        "identity": {
            "source_path": src,
            "source_filename": os.path.basename(src),
            "source_hash": "sha256:" + sha,
            "source_mtime": datetime.fromtimestamp(os.path.getmtime(src), timezone.utc).isoformat(),
            "schema_version": ANALYSIS_SCHEMA_VERSION,
            "analyzed_at": datetime.now(timezone.utc).isoformat(),
            "generator": {"kind": "deterministic"},
        },
        "document_type": "assembly",
        "recipe": {
            "components": components,
            "mates": mates,
            "mass_properties": {
                "volume_m3": mass_kv.get("volume"),
                "surface_area_m2": mass_kv.get("surface_area"),
                "cg": {"x": mass_kv.get("cx"), "y": mass_kv.get("cy"), "z": mass_kv.get("cz")},
            },
        },
        "parameters": [],
        "ir": None,
        "relationships": {"part_files": part_files, "category": None, "cluster_signals": {}},
        "notes": [],
    }

    out_dir = os.path.join(os.path.dirname(src), ".solidpilot")
    os.makedirs(out_dir, exist_ok=True)
    out_path = os.path.join(out_dir, os.path.basename(src) + ".analysis.json")
    with open(out_path, "w", encoding="utf-8") as fh:
        json.dump(artifact, fh, ensure_ascii=False, indent=2)

    return (f"COMPLETED | artifact={out_path} | components={components.get('component_count')} | "
            f"mates={mates.get('mate_count')} | part_files={len(part_files)} | hash=sha256:{sha[:12]}")


# ---------------------------------------------------------------------------
# Tool: rebuild_from_ir  (adapter-only — the analysis pipeline's IR door, IR-ADR-005)
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def rebuild_from_ir(artifact_path: str, fresh_document: bool = True) -> str:
    """Rebuild a part from its analysis artifact's Feature Graph IR — the verification half of
    the round-trip ("the LLM proposes, the round-trip decides", IR-ADR-006). Reads
    `<...>.analysis.json`, takes `ir.graph`, and executes it through the SAME deterministic
    pycompiler as the forward door submit_feature_graph (two doors, ONE compiler — never forked).

    fresh_document (default True): open a NEW blank part first — the normal round-trip flow
        (rebuild fresh, then compare_parts against the original). Pass False only if you have
        already prepared the target document yourself.

    The rebuild reproduces the part AS-ANALYZED. If the source file changed since the artifact
    was written (hash mismatch) the result carries source_stale=true — re-run save_analysis and
    regenerate the IR rather than trusting a stale graph.

    Returns the compiler's per-node summary (COMPLETED n/n, or the feature-level error and how
    far it got — partial geometry may remain; CAD ops are not transactional). Afterwards, verify
    with compare_parts and only then label the artifact's ir.verification. If you are GENERATING
    the ir.graph yourself, read the MCP resource `recipe://usage/index` first — it indexes the
    mapping/canonicalization rules the compiler expects."""
    global _state_version
    path = os.path.abspath(artifact_path)
    if not os.path.isfile(path):
        return f"FAILED | ARTIFACT_NOT_FOUND | {path}"
    try:
        # utf-8-sig: tolerate a BOM — artifacts hand-edited or written by other Windows tools
        # (e.g. PowerShell 5.1 Out-File) may carry one; save_analysis itself writes without.
        with open(path, "r", encoding="utf-8-sig") as fh:
            artifact = json.load(fh)
    except (ValueError, OSError) as ex:
        return f"FAILED | ARTIFACT_UNREADABLE | {ex}"

    ir = artifact.get("ir") or {}
    graph = ir.get("graph")
    if not isinstance(graph, dict) or not graph.get("nodes"):
        status = (ir.get("verification") or {}).get("status")
        reason = ((ir.get("verification") or {}).get("detail") or {}).get("reason")
        return (f"FAILED | NO_IR_GRAPH | the artifact carries no executable ir.graph"
                f"{f' (verification: {status} | {reason})' if status else ''} — run the AI/IR "
                f"pass per cad-planner/recipe.md first")

    # Stale-source signal (cache discipline, ADR-040): informative, not blocking — the graph
    # legitimately rebuilds the part AS-ANALYZED.
    source_stale = ""
    src = (artifact.get("identity") or {}).get("source_path")
    recorded = (artifact.get("identity") or {}).get("source_hash")
    if src and recorded and os.path.isfile(src):
        with open(src, "rb") as fh:
            current = "sha256:" + hashlib.sha256(fh.read()).hexdigest()
        if current != recorded:
            source_stale = " | source_stale=true (file changed since analysis — regenerate the artifact)"

    text, ok, _sv = _run_graph(graph, fresh_document)
    return text + (f" | artifact={os.path.basename(path)}{source_stale}" if ok else "")


# ---------------------------------------------------------------------------
# Tool: compare_parts  (adapter-only — the objective round-trip verifier, ADR-040/A0)
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def compare_parts(doc_a: str, doc_b: str, detail: str = "") -> str:
    """Objectively diff two part documents — the round-trip verifier behind the artifact's
    `verified` label (and a general-purpose "are these the same part?" check).

    doc_a / doc_b: EITHER an absolute .SLDPRT path (opened/activated from disk) OR the TITLE of
        an already-open document (e.g. 'Part4' for an unsaved rebuild). doc_a is the REFERENCE
        (deltas are relative to it — normally the original; doc_b = the rebuild).

    For each doc it runs analyze_model(geometry + mass_properties) and reports topology
    (bodies/faces/edges/vertices), volume, surface area and CG side by side with deltas, plus
    the DECIDED verified-criteria verdict (analysis-artifact.schema.json): topology EXACT AND
    |ΔV| <= 1% AND |ΔA| <= 1%. The verdict is a REPORT — writing ir.verification into the
    artifact stays the caller's job. Read-only geometry-wise (activation may switch the active
    document; doc_b is left active). bbox comparison: pending (analyze doesn't expose it yet).

    detail='faces' (optional): ALSO run analyze_model(faces) on both and list the surfaces that
        are EXTRA (in doc_b only) or MISSING (in doc_a only), matched by supporting plane (normal
        + offset) for planar faces and by (axis, radius) for cylinders. This is the ONE diagnosis a
        point-query can't do — it turns "which face did my rebuild add/drop?" into one call instead
        of manually pairing two full face lists. Faces are reported with their key attributes + the
        stable index `i` in their own document."""
    def _read(doc: str, label: str, want_faces: bool):
        if os.path.isfile(doc):
            r = _call_raw("open_document", {"file_path": os.path.abspath(doc)})
            if r.get("status") != "COMPLETED":
                r = _call_raw("activate_document", {"title": os.path.splitext(os.path.basename(doc))[0]})
        else:
            r = _call_raw("activate_document", {"title": doc})
        if r.get("status") != "COMPLETED":
            err = r.get("error") or {}
            raise RuntimeError(f"DOC_{label}_UNAVAILABLE | {doc} | {err.get('code')}: {err.get('message')}")
        geo = _kv_dict(_analysis_items(_call_raw("analyze_model", {"analysis_type": "geometry", "name": ""})))
        mass = _kv_dict(_analysis_items(_call_raw("analyze_model", {"analysis_type": "mass_properties", "name": ""})))
        faces = None
        if want_faces:
            items = _analysis_items(_call_raw("analyze_model", {"analysis_type": "faces", "name": ""}))
            faces = json.loads(items[0]).get("faces", []) if items else []
        return geo, mass, faces

    want_faces = detail == "faces"
    try:
        geo_a, mass_a, faces_a = _read(doc_a, "A", want_faces)
        geo_b, mass_b, faces_b = _read(doc_b, "B", want_faces)
    except RuntimeError as ex:
        return f"FAILED | {ex}"

    topo_keys = ("bodies", "faces", "edges", "vertices")
    topo_a = [geo_a.get(k) for k in topo_keys]
    topo_b = [geo_b.get(k) for k in topo_keys]
    topology_exact = topo_a == topo_b and None not in topo_a

    def _delta_pct(a, b):
        if not isinstance(a, (int, float)) or not isinstance(b, (int, float)) or a == 0:
            return None
        return (b - a) / a * 100.0

    dv = _delta_pct(mass_a.get("volume"), mass_b.get("volume"))
    da = _delta_pct(mass_a.get("surface_area"), mass_b.get("surface_area"))
    cg_dist = None
    if all(isinstance(mass_x.get(k), (int, float)) for mass_x in (mass_a, mass_b) for k in ("cx", "cy", "cz")):
        cg_dist = ((mass_a["cx"] - mass_b["cx"]) ** 2 + (mass_a["cy"] - mass_b["cy"]) ** 2
                   + (mass_a["cz"] - mass_b["cz"]) ** 2) ** 0.5

    verified = (topology_exact and dv is not None and da is not None
                and abs(dv) <= 1.0 and abs(da) <= 1.0)

    fmt = lambda v, spec=".6g": ("?" if v is None else format(v, spec))  # noqa: E731
    line = (f"COMPLETED | verified_criteria={'PASS' if verified else 'FAIL'} | "
            f"topology A={'-'.join(str(t) for t in topo_a)} B={'-'.join(str(t) for t in topo_b)} "
            f"{'EXACT' if topology_exact else 'DIFFER'} | "
            f"volume A={fmt(mass_a.get('volume'))} B={fmt(mass_b.get('volume'))} dV={fmt(dv, '.4f')}% | "
            f"area A={fmt(mass_a.get('surface_area'))} B={fmt(mass_b.get('surface_area'))} dA={fmt(da, '.4f')}% | "
            f"cg_distance={fmt(cg_dist)} m | reference=A ({doc_a})")

    if want_faces:
        missing, extra = _diff_faces(faces_a, faces_b)
        line += (f"\nfaces: matched={len(faces_a) - len(missing)} "
                 f"MISSING(in A only)={len(missing)} EXTRA(in B only)={len(extra)}")

        def _list(faces, cap=20):
            shown = "; ".join(_face_desc(f) for f in faces[:cap])
            if len(faces) > cap:
                shown += f"; …(+{len(faces) - cap} more)"
            return shown
        if missing:
            line += "\n  MISSING: " + _list(missing)
        if extra:
            line += "\n  EXTRA:   " + _list(extra)
    return line


_FACE_POS_TOL = 5e-5   # 50µm — supporting plane offset / point match
_FACE_RAD_TOL = 5e-5   # 50µm — cylinder radius match


def _faces_compatible(fa: dict, fb: dict) -> bool:
    """True if two faces (from analyze_model(faces) JSON) are the SAME SURFACE within tolerance —
    matched by supporting geometry, not by trim/area. Planar → same unit normal (sign-canonicalized)
    AND same signed offset from origin (±50µm). Cylinder → parallel axis AND same radius (±50µm).
    Other → same kind AND representative point within 50µm. This is deliberately looser than the
    <=1% verified gate: a face-DIFF looks for surfaces one part has and the other doesn't, so
    sub-µm/µm parameter drift on a shared surface must PAIR, leaving only true add/drops unmatched."""
    ka, kb = _face_cat(fa), _face_cat(fb)
    if ka != kb:
        return False
    if ka == "plane":
        na, oa = _plane_no(fa)
        nb, ob = _plane_no(fb)
        dot = na[0] * nb[0] + na[1] * nb[1] + na[2] * nb[2]
        return dot > 0.999 and abs(oa - ob) <= _FACE_POS_TOL
    if ka == "cyl":
        aa = [abs(float(x)) for x in fa["axis"]]
        ab = [abs(float(x)) for x in fb["axis"]]
        parallel = sum(x * y for x, y in zip(aa, ab)) > 0.999
        return parallel and abs(float(fa.get("radius", 0)) - float(fb.get("radius", 0))) <= _FACE_RAD_TOL
    pa, pb = fa.get("point"), fb.get("point")
    if isinstance(pa, list) and isinstance(pb, list):
        return all(abs(float(x) - float(y)) <= _FACE_POS_TOL for x, y in zip(pa, pb))
    return False


def _face_cat(f: dict) -> str:
    if f.get("planar") and isinstance(f.get("normal"), list) and isinstance(f.get("point"), list):
        return "plane"
    if f.get("kind") == "cylinder" and isinstance(f.get("axis"), list):
        return "cyl"
    return f.get("kind", "surf")


def _plane_no(f: dict):
    """Unit normal (sign-canonicalized so the dominant axis is positive) + signed offset from origin."""
    n = [float(x) for x in f["normal"]]
    p = [float(x) for x in f["point"]]
    offset = n[0] * p[0] + n[1] * p[1] + n[2] * p[2]
    dom = max(range(3), key=lambda i: abs(n[i]))
    if n[dom] < 0:
        n = [-x for x in n]
        offset = -offset
    return n, offset


def _diff_faces(faces_a: list, faces_b: list):
    """Greedy tolerance set-difference of two face lists. Returns (missing, extra): missing = faces
    in A that pair with no face in B; extra = faces in B that pair with none in A. Same-surface faces
    pair through sub-µm/µm parameter drift (_faces_compatible), so only genuine add/drops remain."""
    used_b = [False] * len(faces_b)
    missing = []
    for fa in faces_a:
        hit = -1
        for j, fb in enumerate(faces_b):
            if not used_b[j] and _faces_compatible(fa, fb):
                hit = j
                break
        if hit >= 0:
            used_b[hit] = True
        else:
            missing.append(fa)
    extra = [fb for j, fb in enumerate(faces_b) if not used_b[j]]
    return missing, extra


def _face_desc(f: dict) -> str:
    """Compact human description of a face for the diff report."""
    i = f.get("i")
    if f.get("planar"):
        n = f.get("normal")
        area = f.get("area")
        nstr = ("[" + ",".join(format(float(x), ".3g") for x in n) + "]") if isinstance(n, list) else "?"
        return f"i={i} plane n={nstr} area={format(float(area), '.4g') if isinstance(area, (int, float)) else '?'}"
    if f.get("kind") == "cylinder":
        return f"i={i} cyl r={format(float(f.get('radius', 0)), '.4g')}"
    return f"i={i} {f.get('kind', 'surf')}"


# ---------------------------------------------------------------------------
# Tool: compare_assemblies  (adapter-only — the assembly round-trip verifier, ADR-047 B4)
# ---------------------------------------------------------------------------
@mcp.tool(structured_output=False)
def compare_assemblies(doc_a: str, doc_b: str) -> str:
    """Objectively diff two ASSEMBLY documents against the RATIFIED verified criteria
    (ADR-047): component set EXACT (source file + config + instance counts) AND every
    component transform within tolerance (position <= 1µm, rotation <= 1e-6) AND mate
    count + types match AND mass properties within the V1 thresholds (|dV| <= 1%, |dA| <= 1%).

    doc_a / doc_b: EITHER an absolute .SLDASM path OR the TITLE of an already-open document
        (e.g. 'Assem2' for an unsaved rebuild). doc_a is the REFERENCE (the original).

    Components are paired by (source basename, instance ordinal in tree order) — instance
    numbering may legitimately differ between original and rebuild. The verdict is a REPORT;
    writing ir.verification into the artifact stays the caller's job. doc_b is left active."""
    def _read(doc: str, label: str):
        if os.path.isfile(doc):
            r = _call_raw("open_document", {"file_path": os.path.abspath(doc)})
            if r.get("status") != "COMPLETED":
                r = _call_raw("activate_document", {"title": os.path.splitext(os.path.basename(doc))[0]})
        else:
            r = _call_raw("activate_document", {"title": doc})
        if r.get("status") != "COMPLETED":
            err = r.get("error") or {}
            raise RuntimeError(f"DOC_{label}_UNAVAILABLE | {doc} | {err.get('code')}: {err.get('message')}")
        comps = json.loads(_analysis_items(_call_raw("analyze_assembly", {"analysis_type": "components"}))[0])
        mates = json.loads(_analysis_items(_call_raw("analyze_assembly", {"analysis_type": "mates"}))[0])
        mass = _kv_dict(_analysis_items(_call_raw("analyze_model", {"analysis_type": "mass_properties", "name": ""})))
        return comps.get("components", []), mates.get("mates", []), mass

    try:
        comps_a, mates_a, mass_a = _read(doc_a, "A")
        comps_b, mates_b, mass_b = _read(doc_b, "B")
    except (RuntimeError, ValueError, IndexError) as ex:
        return f"FAILED | {ex}"

    def _key_seq(comps):
        counters: dict = {}
        out = []
        for c in comps:
            base = os.path.basename(c.get("path") or "").lower()
            cfg = c.get("config") or ""
            n = counters.get((base, cfg), 0) + 1
            counters[(base, cfg)] = n
            out.append(((base, cfg, n), c))
        return dict(out), sorted(k for k, _c in out)

    map_a, keys_a = _key_seq(comps_a)
    map_b, keys_b = _key_seq(comps_b)
    set_exact = keys_a == keys_b

    # Transform comparison over the paired components (rotation elements vs 1e-6; translation
    # meters vs 1µm). Only meaningful when the sets pair up.
    max_rot = max_pos = 0.0
    transforms_ok = set_exact
    if set_exact:
        for k in keys_a:
            ta, tb = map_a[k].get("transform"), map_b[k].get("transform")
            if not (isinstance(ta, list) and isinstance(tb, list) and len(ta) >= 12 and len(tb) >= 12):
                transforms_ok = False
                break
            max_rot = max(max_rot, max(abs(ta[i] - tb[i]) for i in range(9)))
            max_pos = max(max_pos, max(abs(ta[i] - tb[i]) for i in range(9, 12)))
        transforms_ok = transforms_ok and max_rot <= 1e-6 and max_pos <= 1e-6

    types_a = sorted(m.get("type") or "" for m in mates_a)
    types_b = sorted(m.get("type") or "" for m in mates_b)
    mates_ok = len(mates_a) == len(mates_b) and types_a == types_b

    def _delta_pct(a, b):
        if not isinstance(a, (int, float)) or not isinstance(b, (int, float)) or a == 0:
            return None
        return (b - a) / a * 100.0

    dv = _delta_pct(mass_a.get("volume"), mass_b.get("volume"))
    da = _delta_pct(mass_a.get("surface_area"), mass_b.get("surface_area"))
    mass_ok = dv is not None and da is not None and abs(dv) <= 1.0 and abs(da) <= 1.0

    verified = set_exact and transforms_ok and mates_ok and mass_ok
    fmt = lambda v, spec=".6g": ("?" if v is None else format(v, spec))  # noqa: E731
    return (f"COMPLETED | verified_criteria={'PASS' if verified else 'FAIL'} | "
            f"components A={len(comps_a)} B={len(comps_b)} set={'EXACT' if set_exact else 'DIFFER'} | "
            f"transforms max_rot={fmt(max_rot, '.3g')} max_pos={fmt(max_pos, '.3g')} m "
            f"{'OK' if transforms_ok else 'FAIL'} | "
            f"mates A={len(mates_a)} B={len(mates_b)} types={'MATCH' if types_a == types_b else 'DIFFER'} | "
            f"volume dV={fmt(dv, '.4f')}% area dA={fmt(da, '.4f')}% | reference=A ({doc_a})")


# ---------------------------------------------------------------------------
# Tool: submit_feature_graph  (the FORWARD IR door — intent → IR → pycompiler → SolidWorks.
# Re-enabled GATE-FREE 2026-07-16 for the forward vocabulary effort (IR-ADR-017); the old
# experimental gates were deleted by IR-ADR-005. Two doors, ONE pycompiler: rebuild_from_ir
# replays an artifact's stored graph, this tool takes a graph directly — never fork the compiler.)
# ---------------------------------------------------------------------------
def _resync_state_version() -> int:
    """Realign the adapter's local state_version with the authoritative GET /state.

    A submit_feature_graph run performs MANY execution ops (each bumping state_version) OUTSIDE the
    normal _call() path, so afterwards — success OR failure — we resync the local value, or the NEXT
    normal tool call would fail INVALID_STATE_VERSION (KNOWN-LIMITATIONS #5)."""
    global _state_version
    try:
        _state_version = get_state()
    except Exception:  # noqa: BLE001
        pass
    return _state_version


def _run_graph(graph_obj, fresh_document):
    """The ONE path from a Feature Graph to live geometry — shared by every IR door.

    IR-ADR-005 forbids FORKING the compiler, not having several doors into it. There are three
    (submit_feature_graph takes a graph, rebuild_from_ir replays an artifact's stored one,
    analyze_drawing(mode='build') lowers a drawing), and they all land here, so the document
    choice, the compiler entry point and the post-run resync cannot diverge between them.

    Returns (text, ok, state_version). On failure `text` is already a complete FAILED line."""
    if fresh_document:
        # The graph type picks the document: an assembly graph (component/mate nodes) needs an
        # assembly, a part graph a part.
        nodes = graph_obj.get("nodes") or [] if isinstance(graph_obj, dict) else []
        is_assembly_graph = any(isinstance(n, dict) and n.get("type") in ("component", "mate")
                                for n in nodes)
        new_tool = "open_new_assembly" if is_assembly_graph else "open_new_part"
        opened = _call_raw(new_tool, {})
        if opened.get("status") != "COMPLETED":
            err = opened.get("error") or {}
            return (f"FAILED | {new_tool.upper()}_FAILED | {err.get('code')}: {err.get('message')}",
                    False, _state_version)

    # Lazy import: a missing compiler tree degrades to a clean tool error instead of killing the
    # whole MCP server at startup.
    try:
        from ir_execution_port import run_feature_graph
    except Exception as ex:  # noqa: BLE001
        return f"FAILED | COMPILER_UNAVAILABLE | {ex}", False, _state_version

    # NEVER let an exception crash the MCP server; resync state_version regardless (IR-ADR-001) —
    # one run performs MANY state-bumping sub-ops outside _call() (KNOWN-LIMITATIONS #5).
    try:
        result = run_feature_graph(graph_obj)
    except Exception as ex:  # noqa: BLE001
        sv = _resync_state_version()
        return (f"FAILED | UNEXPECTED | {type(ex).__name__}: {ex} | state_version resynced to {sv}",
                False, sv)
    return result.summary(), True, _resync_state_version()


@mcp.tool(structured_output=False)
def submit_feature_graph(graph: str, fresh_document: bool = True) -> str:
    """Build a part (or assembly) from a Feature Graph IR in ONE call — the deterministic
    compiler lowers each IR node to the right low-level tool sequence and resolves references
    (geometric anchors, runtime feature names) against live geometry.

    ★ PRIMARY BUILD PATH — prefer this whenever you are CREATING a part/assembly (including a
    reverse reconstruction from a drawing). Assemble the WHOLE feature graph and submit it ONCE;
    do NOT hand-build feature-by-feature with the individual low-level tools
    (create_sketch/extrude_feature/add_edge_feature/…). One graph keeps tree order intact, lets
    the compiler resolve anchors + runtime feature names deterministically, and takes far fewer
    round-trips. The low-level tools are for RECOVERY (re-doing one failed node), a genuine
    one-off edit, or a feature the IR vocabulary cannot yet express — not for routine
    construction. If you truly cannot express the whole part in one graph yet, still submit the
    LARGEST coherent batches (fresh_document=True for the first, append with fresh_document=False),
    never one node at a time.

    graph: the Feature Graph as a JSON STRING. Authoring from DESIGN INTENT: read the MCP resource
        `recipe://usage/forward` FIRST (grammar, anchor design, self-verification), plus
        `schema://feature-graph` — the schema IS the capability registry.
        Replaying an ANALYZED part instead: `recipe://usage/canonicalization` +
        `recipe://usage/mapping_part` (…/mapping_sheet_metal, …/mapping_assembly).
        Essentials: units METERS, angles RADIANS (the compiler converts at tool boundaries);
        nodes build in array order (tree order is law); extrude/revolve/rib/sweep/sheet_metal/
        sketched_bend consume the IMMEDIATELY preceding sketch node; loft profiles, a sweep's
        path, pattern seeds and mirror features reference EARLIER nodes by id; an optional
        graph-level material {name, library?} applies after the last node.
    fresh_document (default True): open a new blank document first — part graphs a part,
        assembly graphs (component/mate nodes) an assembly. Pass False to build into the
        CURRENT active document instead.

    Returns the compiler's per-node report: COMPLETED n/n, or FAILED with a feature-level error
    and how far it got. CAD ops are NOT transactional — on failure partial geometry remains
    (reported, never hidden). The adapter resyncs state_version after every run, so subsequent
    normal tools keep working. Verify the result objectively (analyze_model / compare_parts) —
    never assume."""
    try:
        graph_obj = json.loads(graph)
    except Exception as ex:  # noqa: BLE001
        return f"FAILED | INVALID_JSON | the graph is not valid JSON: {ex}"

    text, ok, sv = _run_graph(graph_obj, fresh_document)
    return text + (f" | state_version={sv}" if ok else "")


# ---------------------------------------------------------------------------
# Tool-surface normalisation (prompt cost)
# ---------------------------------------------------------------------------
def _normalize_tool_surface() -> None:
    """Undo three SDK artefacts that ride in the tools/list prompt on every turn.

    1. Docstrings keep their source indentation (the SDK does not dedent them).
    2. Every parameter gets an auto-generated `title` that just re-spells its own name.
    3. `additionalProperties` is absent, so a typo'd param name is not rejected by the
       schema — the P0.4 stance is to reject at the MCP layer, before any REST/COM
       round-trip, so put it back.

    `Tool.description` / `Tool.parameters` are plain mutable fields that `list_tools()`
    reads at call time, so one pass after registration is enough. Without this the payload
    is ~9% larger than the FastMCP surface it replaced; with it, ~2% smaller.

    Touches `mcp._tool_manager` (private). The public alternative is the `middleware`
    hook, which the SDK marks provisional — revisit if this breaks on an SDK bump; the
    schema-contract test is what catches it.
    """
    for tool in mcp._tool_manager.list_tools():
        if tool.description:
            tool.description = inspect.cleandoc(tool.description)
        schema = tool.parameters
        if isinstance(schema, dict):
            schema.pop("title", None)
            for prop in (schema.get("properties") or {}).values():
                if isinstance(prop, dict):
                    prop.pop("title", None)
            schema.setdefault("additionalProperties", False)


_normalize_tool_surface()


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------
if __name__ == "__main__":
    mcp.run()
