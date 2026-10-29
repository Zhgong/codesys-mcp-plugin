import os
import sys
import time
import json
import win32file
import win32pipe

PIPE_NAME = r"\\.\pipe\codesys_mcp_pipe"

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
    print("=" * 60)
    print("CODESYS IDE Visual Status Indicator Acceptance Test (Phase 1)")
    print("=" * 60)
    
    pipe = wait_for_pipe()
    try:
        # Step 1: Initialize
        init_res = send_rpc(pipe, 1, "initialize", {
            "protocolVersion": "2024-11-05",
            "clientInfo": {"name": "test-indicator-client", "version": "1.0.0"}
        })
        print("\n[Step 1] Server Initialized:")
        server_info = init_res.get("result", {}).get("serverInfo", {})
        print(f"  Server Name   : {server_info.get('name')}")
        print(f"  Server Version: {server_info.get('version')}")

        # Step 2: Tools list check
        tools_res = send_rpc(pipe, 2, "tools/list", {})
        tools = tools_res.get("result", {}).get("tools", [])
        tool_names = [t.get("name") for t in tools]
        print(f"\n[Step 2] Registered Tools Count: {len(tools)}")
        assert "get_ui_indicator_status" in tool_names, "get_ui_indicator_status missing from tools/list!"
        print("  [PASS] 'get_ui_indicator_status' is registered.")

        # Step 3: Query UI indicator status
        status_res = send_rpc(pipe, 3, "tools/call", {
            "name": "get_ui_indicator_status",
            "arguments": {}
        })
        print("\n[Step 3] UI Indicator Status Result:")
        raw_text = status_res["result"]["content"][0]["text"]
        indicator = json.loads(raw_text)
        print(json.dumps(indicator, indent=2))

        assert indicator.get("installed") is True, "Indicator should be installed on StatusStrip!"
        assert indicator.get("uiPresent") is True, "CODESYS UI should be present!"
        assert indicator.get("status") in ["Online", "Busy"], f"Expected Online or Busy, got {indicator.get('status')}"
        
        # During tool execution, status is Busy (#FF8C00) and activeTool is the current tool
        if indicator.get("status") == "Busy":
            assert indicator.get("color") == "#FF8C00", f"Expected DarkOrange (#FF8C00) for Busy, got {indicator.get('color')}"
            assert indicator.get("activeTool") == "get_ui_indicator_status", f"Expected activeTool get_ui_indicator_status, got {indicator.get('activeTool')}"
            print("  [PASS] Status is 'Busy' with tool 'get_ui_indicator_status' and color #FF8C00 during tool execution.")
        else:
            assert indicator.get("color") == "#228B22", f"Expected ForestGreen (#228B22) for Online, got {indicator.get('color')}"
            print("  [PASS] Status is 'Online' with color #228B22.")

        assert indicator.get("pipeName") == "codesys_mcp_pipe", "Pipe name mismatch!"
        assert indicator.get("hostControlName") is not None, "Host status strip control name missing!"
        print(f"  [PASS] Visual indicator successfully installed on host control '{indicator.get('hostControlName')}'.")

        # Step 4: Execute a secondary tool and observe request counter increment
        print("\n[Step 4] Calling 'get_online_status' to trigger Busy state transition...")
        online_res = send_rpc(pipe, 4, "tools/call", {
            "name": "get_online_status",
            "arguments": {}
        })
        print(f"  get_online_status result: isError={online_res.get('result', {}).get('isError', False)}")

        status_res_2 = send_rpc(pipe, 5, "tools/call", {
            "name": "get_ui_indicator_status",
            "arguments": {}
        })
        indicator_2 = json.loads(status_res_2["result"]["content"][0]["text"])
        print(f"  Previous totalRequests: {indicator.get('totalRequests')}, New totalRequests: {indicator_2.get('totalRequests')}")
        assert indicator_2.get("totalRequests") > indicator.get("totalRequests"), "Request counter did not increment!"
        print("  [PASS] Request counter tracking verified.")

        print("\n" + "=" * 60)
        print("ALL ACCEPTANCE CRITERIA FOR PHASE 1 PASSED!")
        print("=" * 60)

    finally:
        win32file.CloseHandle(pipe)

if __name__ == "__main__":
    main()
