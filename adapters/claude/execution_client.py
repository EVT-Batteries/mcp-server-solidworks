import os
import subprocess
import threading
import time

import httpx
from adapter_log import write as _log
from config import (
    CONNECT_TIMEOUT,
    ENSURE_ENDPOINT,
    ENSURE_TIMEOUT,
    EXECUTE_ENDPOINT,
    EXECUTION_EXE_PATH,
    HEALTH_ENDPOINT,
    HTTP_TIMEOUT,
    SERVER_SPAWN_TIMEOUT,
    STATE_ENDPOINT,
)
from mcp.server.mcpserver.exceptions import ToolError


class ExecutionLayerError(ToolError):
    """Raised when the execution layer cannot be reached, times out or answers an HTTP error.

    A ToolError, so the MCP SDK passes its message to the model (any other exception type is
    reduced to a bare "Error executing tool <name>")."""


# Serializes auto-start so two concurrent tool calls hitting a down server don't each
# spawn a duplicate exe.
_spawn_lock = threading.Lock()
_health_fail_logged = False


def _http_client(**kwargs) -> httpx.Client:
    """Talk to the local execution server: no system proxy, and a CONNECT phase of its own.

    1. `trust_env=False` — httpx trust_env=True reads HKCU Internet Settings and will send
       http://localhost:5000 through a LAN proxy (Clash/V2Ray), which hangs or returns
       HTTP 502. Windows ProxyOverride (localhost / <local>) is not honoured by httpx, so
       we never inherit env/registry proxies here.

    2. The connect phase gets CONNECT_TIMEOUT, not the caller's number. Which exception a
       down server raises is a RACE between that budget and the OS's refusal: win it and
       httpx raises ConnectError, lose it and ConnectTimeout. `_request_with_autostart`
       catches only ConnectError, so with a small caller timeout the auto-start would
       silently never fire. Splitting the phases makes the exception type a property of the
       code instead of a property of someone's .env (KNOWN-LIMITATIONS #31).
    """
    kwargs.setdefault("trust_env", False)
    timeout = kwargs.get("timeout")
    if isinstance(timeout, (int, float)):
        # A caller passing its own httpx.Timeout has said exactly what it wants — leave it.
        kwargs["timeout"] = httpx.Timeout(timeout, connect=CONNECT_TIMEOUT)
    return httpx.Client(**kwargs)


def _log_health_failure(detail: str) -> None:
    """Log the first failure of an outage, then stay quiet until /health answers again.

    _ensure_server_up polls the probe twice a second, so an unconditional log would bury
    adapter.log under one line per poll.
    """
    global _health_fail_logged
    if not _health_fail_logged:
        _log(f"!! /health probe failed: {detail}")
        _health_fail_logged = True


def _server_is_up() -> bool:
    """Cheap liveness probe — True if /health answers 200.

    Both failure shapes are logged (once per outage), because they are the two halves of
    the same bug: a system proxy that hijacks the localhost route either hangs — which
    surfaces as an exception — or answers 502 itself, which raises nothing at all.
    """
    global _health_fail_logged
    try:
        with _http_client(timeout=2.0) as client:
            status = client.get(HEALTH_ENDPOINT).status_code
    except Exception as ex:
        _log_health_failure(f"{type(ex).__name__}: {ex}")
        return False
    if status != 200:
        _log_health_failure(f"HTTP {status} from {HEALTH_ENDPOINT}")
        return False
    _health_fail_logged = False
    return True


def _ensure_server_up() -> None:
    """Start the execution-server exe if it isn't already answering /health.

    Guarded against duplicate spawns: re-checks /health inside the lock, spawns at most
    one headless/detached process, then polls /health until it's up. The server is meant
    to outlive this call (it persists while SolidWorks is open), so it is launched detached.
    Raises ExecutionLayerError if it can't be brought up (missing exe, or never came up).
    """
    with _spawn_lock:
        if _server_is_up():
            return
        if not os.path.isfile(EXECUTION_EXE_PATH):
            _log(f"!! cannot auto-start server — exe not found at {EXECUTION_EXE_PATH}")
            raise ExecutionLayerError(
                "solidworks-execution is not running and its exe was not found at "
                f"{EXECUTION_EXE_PATH}. Build the server or set EXECUTION_EXE_PATH."
            )
        _log(f"-> auto-starting execution server: {EXECUTION_EXE_PATH}")
        # DETACHED_PROCESS | CREATE_NO_WINDOW: survive the adapter, no console window flash.
        creationflags = 0x00000008 | 0x08000000
        try:
            subprocess.Popen(
                [EXECUTION_EXE_PATH],
                cwd=os.path.dirname(EXECUTION_EXE_PATH),
                creationflags=creationflags,
                close_fds=True,
            )
        except Exception as ex:
            _log(f"!! server spawn failed: {ex}")
            raise ExecutionLayerError(f"Failed to start solidworks-execution: {ex}")

        deadline = time.monotonic() + SERVER_SPAWN_TIMEOUT
        while time.monotonic() < deadline:
            if _server_is_up():
                _log("<- execution server is up")
                return
            time.sleep(0.5)
        _log("!! execution server did not answer /health within timeout")
        raise ExecutionLayerError(
            f"Started solidworks-execution but it did not answer {HEALTH_ENDPOINT} "
            f"within {SERVER_SPAWN_TIMEOUT}s."
        )


def _request_with_autostart(do_request, label: str):
    """Run an httpx request; on ConnectError, auto-start the server once and retry.

    Makes every tool call self-heal the "server is down" case transparently. Timeouts are
    NOT caught here (they propagate to each caller's own timeout handling).
    """
    try:
        return do_request()
    except httpx.ConnectError:
        _log(f"<- {label} CONNECT_ERROR — attempting server auto-start")
        _ensure_server_up()  # raises ExecutionLayerError if it can't bring the server up
        try:
            return do_request()
        except httpx.ConnectError:
            _log(f"<- {label} CONNECT_ERROR after auto-start")
            raise ExecutionLayerError(
                "Cannot connect to solidworks-execution even after auto-start. "
                f"Is {HEALTH_ENDPOINT} reachable?"
            )


def get_health() -> dict:
    """GET /health — server status + COM attach state (does not touch state_version)."""
    try:
        with _http_client(timeout=HTTP_TIMEOUT) as client:
            response = client.get(HEALTH_ENDPOINT)
    except httpx.ConnectError:
        _log("<- health CONNECT_ERROR (server down?)")
        raise ExecutionLayerError(
            f"Cannot connect to solidworks-execution. Is the server running on {HEALTH_ENDPOINT}?"
        )
    except httpx.TimeoutException:
        _log("<- health TIMEOUT")
        raise ExecutionLayerError(f"Health request timed out after {HTTP_TIMEOUT}s.")
    if response.status_code != 200:
        raise ExecutionLayerError(f"Unexpected HTTP {response.status_code} from /health: {response.text}")
    body = response.json()
    _log(f"<- health status={body.get('status')} comAttached={body.get('comAttached')} sv={body.get('stateVersion')}")
    return body


def get_state() -> int:
    """
    GET /api/tool/state — fetch the current authoritative state_version.

    Used to resync after a desync (e.g. execution server restarted on rebuild).
    Read-only on the server: no state_version check, no increment.
    """
    _log("-> get_state (resync)")

    def _do():
        with _http_client(timeout=HTTP_TIMEOUT) as client:
            return client.get(STATE_ENDPOINT)

    try:
        response = _request_with_autostart(_do, "get_state")
    except httpx.TimeoutException:
        _log("<- get_state TIMEOUT")
        raise ExecutionLayerError(
            f"Resync request to solidworks-execution timed out after {HTTP_TIMEOUT}s."
        )

    if response.status_code != 200:
        raise ExecutionLayerError(
            f"Unexpected HTTP {response.status_code} from /state: {response.text}"
        )

    sv = int(response.json().get("stateVersion", 0))
    _log(f"<- get_state sv={sv}")
    return sv


def call_tool(tool_name: str, operation_id: str, state_version: int, params: dict,
              timeout: float | None = None) -> dict:
    """
    POST /api/tool/execute on the solidworks-execution layer.

    Returns the parsed JSON response body on HTTP 200.
    Raises ExecutionLayerError on HTTP 400 or unexpected status codes, and on a timeout
    (`timeout` seconds, default HTTP_TIMEOUT).
    """
    budget = timeout or HTTP_TIMEOUT
    payload = {
        "operationId": operation_id,
        "tool": tool_name,
        "stateVersion": state_version,
        "params": params,
    }

    _log(f"-> {tool_name} op={operation_id} sv={state_version}")

    def _do():
        with _http_client(timeout=budget) as client:
            return client.post(EXECUTE_ENDPOINT, json=payload)

    try:
        response = _request_with_autostart(_do, tool_name)
    except httpx.TimeoutException:
        _log(f"<- {tool_name} TIMEOUT after {budget}s")
        raise ExecutionLayerError(
            f"TIMEOUT | {tool_name} got no answer from solidworks-execution within {budget:g} s. "
            "SolidWorks is probably still working on it (calls run one at a time, so the next "
            "call waits for it): wait, then check the result (verify_state, component_status, "
            "check_rebuild) before retrying, since a model-changing call may still complete. "
            "Raise HTTP_TIMEOUT / LONG_HTTP_TIMEOUT in adapters/claude/.env for long calls, or "
            "narrow the call (e.g. name the components)."
        )
    except httpx.HTTPError as ex:
        _log(f"<- {tool_name} HTTP_ERROR {type(ex).__name__}: {ex}")
        raise ExecutionLayerError(
            f"CONNECTION_LOST | {tool_name}: {type(ex).__name__}: {ex}. The execution server "
            "may have crashed or been restarted; call ensure_ready, then check the model state."
        )

    if response.status_code == 400:
        try:
            body = response.json()
        except ValueError:
            body = {}
        _log(f"<- {tool_name} HTTP_400 {body.get('error', '')}")
        raise ExecutionLayerError(f"Bad request: {body.get('error', response.text)}")

    if response.status_code != 200:
        _log(f"<- {tool_name} HTTP_{response.status_code}")
        raise ExecutionLayerError(
            f"Unexpected HTTP {response.status_code} from execution layer: {response.text}"
        )

    body = response.json()
    err = (body.get("error") or {}).get("code")
    _log(f"<- {tool_name} {body.get('status')}{(' ' + err) if err else ''} sv={body.get('stateVersion')}")
    return body


def ensure_ready() -> dict:
    """POST /ensure_ready — bring the whole stack up and report readiness.

    Two layers: (1) make sure the execution server itself is running (auto-spawn it if
    down — a down server can't answer /ensure_ready); (2) the server attaches to SolidWorks,
    launching it via COM if it's closed. Uses a long timeout because a cold SolidWorks launch
    can take tens of seconds. Returns the parsed readiness dict; does NOT open any document.
    """
    _log("-> ensure_ready")
    # A down server can't answer /ensure_ready, so spawn it up front (idempotent / guarded).
    if not _server_is_up():
        _ensure_server_up()

    def _do():
        with _http_client(timeout=ENSURE_TIMEOUT) as client:
            return client.post(ENSURE_ENDPOINT)

    try:
        response = _request_with_autostart(_do, "ensure_ready")
    except httpx.TimeoutException:
        _log("<- ensure_ready TIMEOUT")
        raise ExecutionLayerError(
            f"ensure_ready timed out after {ENSURE_TIMEOUT}s (a SolidWorks cold launch can be slow)."
        )

    if response.status_code != 200:
        _log(f"<- ensure_ready HTTP_{response.status_code}")
        raise ExecutionLayerError(
            f"Unexpected HTTP {response.status_code} from /ensure_ready: {response.text}"
        )

    body = response.json()
    _log(
        f"<- ensure_ready comAttached={body.get('comAttached')} "
        f"swLaunched={body.get('swLaunched')} doc={body.get('activeDocument')}"
    )
    return body
