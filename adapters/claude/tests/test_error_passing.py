"""Tool failures must reach the model with their reason.

The MCP SDK reduces any exception that is not a ToolError to a bare "Error executing tool
<name>" (UnexpectedToolError), keeping the reason in the server log only. server.py registers
every tool through a wrapper that turns exceptions into ToolErrors; these tests call tools
through the SDK with the execution layer stubbed out and check the message that comes back.

Runnable two ways:
  - standalone:  python tests/test_error_passing.py
  - pytest:      pytest tests/test_error_passing.py
"""

import asyncio
import os
import sys

import httpx

_HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(_HERE))
import execution_client
import server
from mcp.server.mcpserver.exceptions import ToolError, UnexpectedToolError


def _call(tool: str, args: dict) -> str:
    """Call a tool through the SDK; return the error text it raises (or fail)."""
    try:
        asyncio.run(server.mcp.call_tool(tool, args))
    except UnexpectedToolError as exc:  # the case this file guards against
        raise AssertionError(f"reason swallowed: {exc}") from exc
    except ToolError as exc:
        return str(exc)
    raise AssertionError("the tool did not fail")


def test_failed_response_keeps_code_and_message():
    def fake(tool, op, sv, params, timeout=None):
        return {
            "status": "FAILED",
            "stateVersion": sv,
            "error": {
                "code": "INSERT_FAILED",
                "message": "AddComponent5 returned null",
            },
        }

    original = server.call_tool
    server.call_tool = fake
    try:
        text = _call("verify_state", {})
    finally:
        server.call_tool = original
    assert "INSERT_FAILED" in text and "AddComponent5 returned null" in text, text


def test_timeout_is_reported():
    def fake(tool, op, sv, params, timeout=None):
        raise execution_client.ExecutionLayerError(
            f"TIMEOUT | {tool} got no answer within {timeout} s"
        )

    original = server.call_tool
    server.call_tool = fake
    try:
        text = _call("component_status", {})
    finally:
        server.call_tool = original
    assert "TIMEOUT" in text and "component_status" in text, text
    # Long assembly tools get the long budget.
    assert (
        str(server.LONG_HTTP_TIMEOUT) in text or f"{server.LONG_HTTP_TIMEOUT:g}" in text
    ), text


def test_unexpected_exception_keeps_its_text():
    def fake(tool, op, sv, params, timeout=None):
        raise httpx.ReadError("connection reset by peer")

    original = server.call_tool
    server.call_tool = fake
    try:
        text = _call("verify_state", {})
    finally:
        server.call_tool = original
    assert "ReadError" in text and "connection reset by peer" in text, text


def test_short_tools_use_the_default_timeout():
    assert server._timeout_for("verify_state") is None
    assert server._timeout_for("check_interference") == server.LONG_HTTP_TIMEOUT


if __name__ == "__main__":
    for name, fn in list(globals().items()):
        if name.startswith("test_"):
            fn()
            print("ok", name)
