"""
Level 3: SoftPLC Hardware-In-The-Loop Live Integration Test
Tests Batch Variables, Struct Injection, and Array Reading against live SoftPLC:
  1. Login to SoftPLC (headless) and verify RUN state
  2. Auto-discover and read struct `PLC_PRG.PressRecipe`
  3. Batch write multiple parameters in single PLC scan cycle (`batch_write_variables`)
  4. Write entire recipe JSON object (`write_struct`)
  5. Verify readback matching on all struct members
  6. Read array slice (`read_array`)
"""
import json
import os
import sys
import time

PIPE_PATH = r"\\.\pipe\codesys_mcp_pipe"


def call_mcp_tool(tool_name, arguments=None, req_id=1):
    t0 = time.time()
    req = {
        "jsonrpc": "2.0",
        "id": req_id,
        "method": "tools/call",
        "params": {
            "name": tool_name,
            "arguments": arguments or {}
        }
    }
    payload = (json.dumps(req) + "\n").encode("utf-8")
    with open(PIPE_PATH, "r+b", buffering=0) as f:
        f.write(payload)
        f.flush()
        resp_bytes = bytearray()
        while True:
            ch = f.read(1)
            if not ch or ch == b"\n":
                break
            if ch != b"\r":
                resp_bytes.extend(ch)
    elapsed_ms = (time.time() - t0) * 1000.0
    raw = json.loads(resp_bytes.decode("utf-8"))
    res = raw.get("result", {})
    is_err = res.get("isError", False)
    content = res.get("content", [])
    text = content[0].get("text", "") if content and isinstance(content, list) else ""
    parsed = text
    if not is_err and text:
        try:
            parsed = json.loads(text)
        except Exception:
            pass
    return is_err, parsed, elapsed_ms


def main():
    print("==========================================================")
    print(" [LEVEL 3] SoftPLC Recipe Batch & Complex Data Live Test")
    print("==========================================================")
    t_start = time.time()

    # 0. Ensure Project is Open
    SERVO_PROJECT = r"C:\Users\vboxuser\Desktop\Repos\agy-codesys\projects\servo_press\ServoPress_VirtualAxis.project"
    print("\n[Step 0/5] Ensuring project is open (`get_project_info`)...")
    err, info_res, ms = call_mcp_tool("get_project_info")
    if err or not isinstance(info_res, dict) or not info_res.get("isOpen"):
        print(f"    Opening project: {SERVO_PROJECT}...")
        call_mcp_tool("open_project", {"path": SERVO_PROJECT})

    # 1. SoftPLC Login
    print("\n[Step 1/5] Connecting to SoftPLC (`plc_login`)...")
    err, login_res, ms = call_mcp_tool("plc_login", {
        "username": "admin",
        "password": "1111",
        "mode": "try_online_change",
        "delete_foreign_apps": True
    })
    print(f"    Result ({ms:.0f}ms): {login_res}")
    if err or not isinstance(login_res, dict) or not login_res.get("isLoggedIn"):
        print("    [FAIL] SoftPLC login failed!")
        sys.exit(1)

    app_state = str(login_res.get("applicationState", "")).lower()
    if app_state != "run":
        print(f"    Starting PLC execution from state '{app_state}'...")
        call_mcp_tool("plc_control", {"action": "start"})

    # 2. Read Struct (Auto-discovery from DUT)
    print("\n[Step 2/5] Reading initial recipe struct (`read_struct` -> `MOTION_PRG.PressRecipe`)...")
    err, struct_res, ms = call_mcp_tool("read_struct", {"struct_name": "MOTION_PRG.PressRecipe"})
    print(f"    Result ({ms:.0f}ms): {json.dumps(struct_res, ensure_ascii=False, indent=2)}")
    if err or not struct_res.get("success"):
        print("    [FAIL] read_struct failed!")
        sys.exit(1)

    initial_force = struct_res.get("values", {}).get("rTargetForce", "Unknown")
    print(f"    Initial rTargetForce: {initial_force}")

    # 3. Batch Write Variables (Single-Cycle Atomic Write)
    print("\n[Step 3/5] Atomic single-cycle batch write (`batch_write_variables`)...")
    batch_vars = {
        "MOTION_PRG.PressRecipe.rTargetForce": "5600.0",
        "MOTION_PRG.PressRecipe.rFastSpeed": "46.0",
        "MOTION_PRG.PressRecipe.rPressSpeed": "6.5",
        "MOTION_PRG.PressRecipe.rWorkpieceContactZ": "81.5"
    }
    err, batch_res, ms = call_mcp_tool("batch_write_variables", {
        "variables": batch_vars,
        "verify": True
    })
    print(f"    Result ({ms:.0f}ms): {json.dumps(batch_res, ensure_ascii=False, indent=2)}")
    if err or not batch_res.get("success") or not batch_res.get("allVerified"):
        print("    [FAIL] batch_write_variables failed or verification mismatch!")
        sys.exit(1)
    print(f"    [PASS] All {batch_res.get('succeededCount')} variables written atomically and verified!")

    # 4. Write Entire Struct Object (JSON Recipe Injection)
    print("\n[Step 4/5] Injecting entire new recipe object (`write_struct`)...")
    recipe_object = {
        "rTargetForce": 5850.0,
        "rFastSpeed": 49.0,
        "rPressSpeed": 7.2,
        "rWorkpieceContactZ": 83.0,
        "rRetractSpeed": 65.0
    }
    err, ws_res, ms = call_mcp_tool("write_struct", {
        "struct_name": "MOTION_PRG.PressRecipe",
        "values": recipe_object,
        "verify": True
    })
    print(f"    Result ({ms:.0f}ms): {json.dumps(ws_res, ensure_ascii=False, indent=2)}")
    if err or not ws_res.get("success") or not ws_res.get("allVerified"):
        print("    [FAIL] write_struct failed or verification mismatch!")
        sys.exit(1)
    print(f"    [PASS] Complete recipe object injected and verified!")

    # 5. Read Array Slice
    print("\n[Step 5/5] Reading array slice (`read_array`)...")
    err, arr_res, ms = call_mcp_tool("read_array", {
        "variable_name": "MOTION_PRG.aForceProfile",
        "start_index": 0,
        "count": 5
    })
    print(f"    Result ({ms:.0f}ms): {json.dumps(arr_res, ensure_ascii=False, indent=2)}")
    if err or not arr_res.get("success"):
        print("    [FAIL] read_array failed!")
        sys.exit(1)
    print(f"    [PASS] Successfully read {arr_res.get('count')} array elements: {arr_res.get('items')}")

    # Final Readback
    print("\n[Verification] Final struct readback (`read_struct`)...")
    err, final_struct, ms = call_mcp_tool("read_struct", {"struct_name": "MOTION_PRG.PressRecipe"})
    vals = final_struct.get("values", {})
    print(f"    Final rTargetForce: {vals.get('rTargetForce')} (Target: 5850.0)")
    print(f"    Final rFastSpeed  : {vals.get('rFastSpeed')} (Target: 49.0)")
    print(f"    Final rPressSpeed : {vals.get('rPressSpeed')} (Target: 7.2)")

    total_time = time.time() - t_start
    print("\n==========================================================")
    print(f" [ALL LEVEL 3 ACCEPTANCE TESTS PASSED in {total_time:.2f}s!]")
    print("==========================================================")


if __name__ == "__main__":
    main()
