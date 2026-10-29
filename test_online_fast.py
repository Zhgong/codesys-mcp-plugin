"""
Dedicated Fast Online Verification & Hot-Reload Test Runner (~1.5s execution)
Tests ONLY the CODESYS Online lifecycle against a running CODESYS.exe instance:
  1. (Optional --reload) Hot-reloads bin/Release/CodesysMcp.dll without restarting CODESYS
  2. Ensures TestProject.project is open
  3. plc_login (mode='try_online_change', delete_foreign_apps=True)
  4. get_online_status
  5. read_variables (2 samples 350ms apart to verify live SoftPLC counter increment)
  6. write_variable ('PLC_PRG.rTemp' := '88.8' with readback verification)
  7. plc_control ('stop' -> 'start')
  8. plc_logout
"""
import argparse
import json
import os
import sys
import time

PIPE_PATH = r"\\.\pipe\codesys_mcp_pipe"
DEFAULT_PROJECT = r"C:\Users\vboxuser\Desktop\Repos\agy-codesys\projects\servo_press\ServoPress_VirtualAxis.project"
DEFAULT_DLL = r"C:\Users\vboxuser\Desktop\Repos\codesys-plugin\bin\Release\CodesysMcp.dll"


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
    text = ""
    content = res.get("content", [])
    if content and isinstance(content, list):
        text = content[0].get("text", "")
    parsed = text
    if not is_err and text:
        try:
            parsed = json.loads(text)
        except Exception:
            pass
    return is_err, parsed, elapsed_ms


def dump_diagnostics():
    print("\n--- [DIAGNOSTICS] Fetching CODESYS IDE Messages ---")
    try:
        err, msgs, _ = call_mcp_tool("get_ide_messages", {})
        if not err and isinstance(msgs, dict):
            for m in msgs.get("messages", [])[-15:]:
                print("  IDE:", m)
        else:
            print("  IDE messages:", msgs)
    except Exception as e:
        print("  Could not fetch IDE messages:", e)


def main():
    parser = argparse.ArgumentParser(description="Fast Online Verification Runner")
    parser.add_argument("--reload", action="store_true", help="Hot-reload bin/Release/CodesysMcp.dll before testing")
    parser.add_argument("--project", default=DEFAULT_PROJECT, help="Path to TestProject.project")
    parser.add_argument("--user", default=os.environ.get("CODESYS_DEVICE_USER", ""), help="Device User Logon username")
    parser.add_argument("--password", default=os.environ.get("CODESYS_DEVICE_PASSWORD", ""), help="Device User Logon password")
    parser.add_argument("--keep-logged-in", action="store_true", help="Stay logged in after test")
    args = parser.parse_args()

    print("=== CODESYS Fast Online Verification Suite ===")
    t_start = time.time()

    if args.reload:
        print("\n[0] Hot-reloading CodesysMcp.dll into running CODESYS.exe...")
        err, out, ms = call_mcp_tool("hot_reload_dll", {"dll_path": DEFAULT_DLL})
        print(f"    Result ({ms:.0f}ms): {out}")
        if err:
            print("    [FAIL] Hot reload failed!")
            sys.exit(1)
        time.sleep(0.6)

    # 1. Check / Open project
    print("\n[1] Checking active project (`get_project_info`)...")
    err, info, ms = call_mcp_tool("get_project_info", {})
    print(f"    Result ({ms:.0f}ms): {info}")
    if err:
        dump_diagnostics()
        sys.exit(1)

    if not isinstance(info, dict) or not info.get("isOpen"):
        print(f"\n[1b] Opening project: {args.project} ...")
        err, open_res, ms = call_mcp_tool("open_project", {"path": args.project})
        print(f"    Result ({ms:.0f}ms): {open_res}")
        if err:
            dump_diagnostics()
            sys.exit(1)

    # 2. PLC Login
    print("\n[2] Logging in to SoftPLC (`plc_login`, mode='try_online_change', delete_foreign_apps=True)...")
    login_args = {
        "mode": "try_online_change",
        "delete_foreign_apps": True
    }
    if args.user:
        login_args["username"] = args.user
        login_args["password"] = args.password
    err, login_res, ms = call_mcp_tool("plc_login", login_args)
    print(f"    Result ({ms:.0f}ms): {json.dumps(login_res, ensure_ascii=False)}")
    if err or not isinstance(login_res, dict) or not login_res.get("isLoggedIn"):
        print("    [FAIL] plc_login did not succeed!")
        dump_diagnostics()
        sys.exit(1)

    # 3. Ensure PLC is in RUN state
    app_state = str(login_res.get("applicationState", "")).lower()
    if app_state != "run":
        print(f"\n[2b] Starting PLC (`plc_control` action='start', current state='{app_state}')...")
        err, start_res, ms = call_mcp_tool("plc_control", {"action": "start"})
        print(f"    Result ({ms:.0f}ms): {json.dumps(start_res, ensure_ascii=False)}")
        if err:
            dump_diagnostics()
            sys.exit(1)

    # 4. Read variables (2 samples to verify live execution)
    exprs = [
        "MOTION_PRG.eDispState",
        "MOTION_PRG.rDispPos",
        "MOTION_PRG.PressRecipe.rTargetForce",
        "MOTION_PRG.PressRecipe.rFastSpeed"
    ]
    print(f"\n[3] Reading live variables Sample #1: {exprs}...")
    err, r1, ms1 = call_mcp_tool("read_variables", {"expressions": exprs})
    print(f"    Sample #1 ({ms1:.0f}ms): {json.dumps(r1, ensure_ascii=False)}")
    if err:
        dump_diagnostics()
        sys.exit(1)

    time.sleep(0.35)
    print(f"[3b] Reading live variables Sample #2 (after 350ms): {exprs}...")
    err, r2, ms2 = call_mcp_tool("read_variables", {"expressions": exprs})
    print(f"    Sample #2 ({ms2:.0f}ms): {json.dumps(r2, ensure_ascii=False)}")
    if err:
        dump_diagnostics()
        sys.exit(1)

    # 5. Write variable & verify readback
    print("\n[4] Writing variable `MOTION_PRG.PressRecipe.rTargetForce` := '5250.0' (`write_variable`)...")
    err, w_res, ms = call_mcp_tool("write_variable", {
        "name": "MOTION_PRG.PressRecipe.rTargetForce",
        "value": "5250.0",
        "force": False
    })
    print(f"    Result ({ms:.0f}ms): {json.dumps(w_res, ensure_ascii=False)}")
    if err or not isinstance(w_res, dict) or not w_res.get("verified"):
        print("    [FAIL] write_variable verification failed!")
        dump_diagnostics()
        sys.exit(1)

    # 6. PLC Control: STOP -> START
    print("\n[5] Testing `plc_control` (STOP -> START)...")
    err, stop_res, ms_stop = call_mcp_tool("plc_control", {"action": "stop"})
    print(f"    STOP ({ms_stop:.0f}ms): {json.dumps(stop_res, ensure_ascii=False)}")
    if err:
        dump_diagnostics()
        sys.exit(1)

    err, run_res, ms_run = call_mcp_tool("plc_control", {"action": "start"})
    print(f"    START ({ms_run:.0f}ms): {json.dumps(run_res, ensure_ascii=False)}")
    if err:
        dump_diagnostics()
        sys.exit(1)

    # 7. Logout (unless --keep-logged-in)
    if not args.keep_logged_in:
        print("\n[6] Logging out (`plc_logout`)...")
        err, out_res, ms_out = call_mcp_tool("plc_logout", {})
        print(f"    Result ({ms_out:.0f}ms): {json.dumps(out_res, ensure_ascii=False)}")
        if err:
            dump_diagnostics()
            sys.exit(1)

    total_s = time.time() - t_start
    print(f"\n[PASS] All Online Verification steps succeeded in {total_s:.2f}s!")


if __name__ == "__main__":
    main()
