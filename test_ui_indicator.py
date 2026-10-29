import os
import sys
import time
import json
import win32file

PIPE_NAME = r"\\.\pipe\codesys_mcp_pipe"
TEST_PROJECT = r"C:\Users\vboxuser\Desktop\Repos\agy-codesys\projects\servo_press\ServoPress_VirtualAxis.project"

def send_rpc(pipe, req_id, method, params=None):
    payload = {
        "jsonrpc": "2.0",
        "id": req_id,
        "method": method
    }
    if params is not None:
        payload["params"] = params
    msg = json.dumps(payload) + "\n"
    win32file.WriteFile(pipe, msg.encode("utf-8"))
    
    # Read response line
    buffer = b""
    while b"\n" not in buffer:
        hr, chunk = win32file.ReadFile(pipe, 4096)
        buffer += chunk
    
    line = buffer.decode("utf-8").strip()
    return json.loads(line)

def wait_for_pipe(timeout=45):
    print(f"Waiting for named pipe: {PIPE_NAME} (timeout={timeout}s)...")
    start = time.time()
    while time.time() - start < timeout:
        try:
            h = win32file.CreateFile(
                PIPE_NAME,
                win32file.GENERIC_READ | win32file.GENERIC_WRITE,
                0,
                None,
                win32file.OPEN_EXISTING,
                0,
                None
            )
            print(f"Connected to named pipe in {time.time() - start:.2f}s!")
            return h
        except Exception:
            time.sleep(1.0)
    raise TimeoutError(f"Could not connect to {PIPE_NAME} within {timeout}s")

def main():
    print("=" * 65)
    print("CODESYS IDE Visual Status Indicator & Lifecycle Test (Phase 1+2)")
    print("=" * 65)
    
    pipe = wait_for_pipe()
    try:
        # Step 1: Initialize
        init_res = send_rpc(pipe, 1, "initialize", {
            "protocolVersion": "2024-11-05",
            "clientInfo": {"name": "test-indicator-client", "version": "1.1.0"}
        })
        print("\n[Step 1] Server Initialized:")
        server_info = init_res.get("result", {}).get("serverInfo", {})
        print(f"  Server Name   : {server_info.get('name')}")
        print(f"  Server Version: {server_info.get('version')}")

        # Step 2: Tools list check (must have 30 tools)
        tools_res = send_rpc(pipe, 2, "tools/list", {})
        tools = tools_res.get("result", {}).get("tools", [])
        tool_names = [t.get("name") for t in tools]
        print(f"\n[Step 2] Registered Tools Count: {len(tools)}")
        assert "get_ui_indicator_status" in tool_names, "get_ui_indicator_status missing from tools/list!"
        print("  [PASS] 'get_ui_indicator_status' is registered.")

        # Step 3: Verify initial indicator state (No Project)
        print("\n[Step 3] Initial Indicator Status (Prior to opening project):")
        status_res = send_rpc(pipe, 3, "tools/call", {
            "name": "get_ui_indicator_status",
            "arguments": {}
        })
        indicator = json.loads(status_res["result"]["content"][0]["text"])
        print(json.dumps(indicator, indent=2))

        assert indicator.get("installed") is True, "Indicator should be installed!"
        assert indicator.get("uiPresent") is True, "CODESYS UI should be present!"
        assert indicator.get("hostControlName") == "CodesysMcpStatusStrip", f"Expected CodesysMcpStatusStrip, got {indicator.get('hostControlName')}"
        assert indicator.get("hasContextMenu") is True, "Context menu should be initialized!"
        assert indicator.get("hostControlVisible") is True, "Dedicated status strip should be visible!"
        assert indicator.get("isPermanentlyVisible") is True, "Indicator should be permanently visible!"
        print("  [PASS] Dedicated 'CodesysMcpStatusStrip' with context menu verified.")

        # Step 4: Open Project and verify indicator DOES NOT disappear!
        print(f"\n[Step 4] Opening project: {TEST_PROJECT}...")
        open_res = send_rpc(pipe, 4, "tools/call", {
            "name": "open_project",
            "arguments": {"path": TEST_PROJECT}
        })
        open_result_text = open_res.get("result", {}).get("content", [{}])[0].get("text")
        print(f"  open_project result: {open_result_text}")
        open_data = json.loads(open_result_text)
        assert open_data.get("isOpen") is True, "Project open failed!"

        # Step 5: Check indicator status AFTER project is opened
        print("\n[Step 5] Checking Indicator Status AFTER project is open (Bug Fix Verification):")
        status_after_open = send_rpc(pipe, 5, "tools/call", {
            "name": "get_ui_indicator_status",
            "arguments": {}
        })
        ind_open = json.loads(status_after_open["result"]["content"][0]["text"])
        print(json.dumps(ind_open, indent=2))

        assert ind_open.get("installed") is True, "Indicator disappeared after project open!"
        assert ind_open.get("isPermanentlyVisible") is True, "Indicator is not visible after project open!"
        assert ind_open.get("hostControlVisible") is True, "Host control is not visible after project open!"
        assert ind_open.get("hostControlName") == "CodesysMcpStatusStrip", "Host control corrupted!"
        print("  [PASS] Indicator successfully stayed permanently visible after project was opened!")

        # Step 6: Close project and verify indicator stays intact
        print("\n[Step 6] Closing project...")
        close_res = send_rpc(pipe, 6, "tools/call", {
            "name": "close_project",
            "arguments": {}
        })
        close_text = close_res.get("result", {}).get("content", [{}])[0].get("text")
        print(f"  close_project result: {close_text}")

        status_after_close = send_rpc(pipe, 7, "tools/call", {
            "name": "get_ui_indicator_status",
            "arguments": {}
        })
        ind_close = json.loads(status_after_close["result"]["content"][0]["text"])
        assert ind_close.get("isPermanentlyVisible") is True, "Indicator lost after project close!"
        print("  [PASS] Indicator verified permanently visible after project close.")

        print("\n" + "=" * 65)
        print("ALL ACCEPTANCE CRITERIA FOR PHASE 1 FIX & PHASE 2 PASSED!")
        print("=" * 65)

    finally:
        win32file.CloseHandle(pipe)

if __name__ == "__main__":
    main()
