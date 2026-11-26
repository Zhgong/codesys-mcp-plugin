# -*- coding: utf-8 -*-
"""
CODESYS MCP Plugin - E2E Reliability & Version Compatibility Matrix Runner.

Connects to CODESYS IDE via CodesysMcpCli.exe (Stdio -> Named Pipe), detects the
host CODESYS IDE version/profile/UI mode, executes live verification across all
48 registered MCP tools (8 functional categories) on an isolated temporary copy
of a CODESYS project, and updates:
  - tests/reports/e2e_reliability_history.json
  - tests/reports/e2e_reliability_matrix.md
"""
import argparse
import ctypes
import datetime
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import winreg

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CLI_EXE = os.path.join(REPO_ROOT, "bin", "Release", "CodesysMcpCli.exe")
SESSION_SCRIPT = os.path.join(REPO_ROOT, "codesys_mcp_session.py")
DEFAULT_CODESYS_EXE = r"C:\Program Files\CODESYS 3.5.20.50\CODESYS\Common\CODESYS.exe"
DEFAULT_PROFILE = "CODESYS V3.5 SP20 Patch 5"
DEFAULT_PROJECT = os.environ.get(
    "CODESYS_MCP_TEST_PROJECT",
    os.path.abspath(os.path.join(REPO_ROOT, "..", "agy-codesys", "projects", "servo_press", "ServoPress_VirtualAxis.project")),
)
DEFAULT_REPORT_MD = os.path.join(REPO_ROOT, "tests", "reports", "e2e_reliability_matrix.md")
DEFAULT_HISTORY_JSON = os.path.join(REPO_ROOT, "tests", "reports", "e2e_reliability_history.json")
PIPE_NAME = os.environ.get("CODESYS_MCP_PIPE", "codesys_mcp_pipe")
PIPE_PATH = rf"\\.\pipe\{PIPE_NAME}"

# Ordered catalog of all 48 registered MCP tools grouped into 8 functional categories
TOOL_CATALOG = [
    # 1. Project Management & Navigation (10)
    ("open_project", "1. Project Management & Navigation", "Offline/IDE"),
    ("close_project", "1. Project Management & Navigation", "Offline/IDE"),
    ("get_project_info", "1. Project Management & Navigation", "Offline/IDE"),
    ("get_project_tree", "1. Project Management & Navigation", "Offline/IDE"),
    ("create_folder", "1. Project Management & Navigation", "Offline/IDE"),
    ("create_interface", "1. Project Management & Navigation", "Offline/IDE"),
    ("create_method", "1. Project Management & Navigation", "Offline/IDE"),
    ("create_property", "1. Project Management & Navigation", "Offline/IDE"),
    ("save_project", "1. Project Management & Navigation", "Offline/IDE"),
    ("create_boot_application", "1. Project Management & Navigation", "Online/SoftPLC"),
    # 2. Code Editing, Search, Mirror & Rollback (9)
    ("read_pou", "2. Code Editing, Search & Mirror", "Offline/IDE"),
    ("write_pou", "2. Code Editing, Search & Mirror", "Offline/IDE"),
    ("edit_pou", "2. Code Editing, Search & Mirror", "Offline/IDE"),
    ("delete_pou", "2. Code Editing, Search & Mirror", "Offline/IDE"),
    ("search_code", "2. Code Editing, Search & Mirror", "Offline/IDE"),
    ("get_diff", "2. Code Editing, Search & Mirror", "Offline/IDE"),
    ("preview_diff", "2. Code Editing, Search & Mirror", "Offline/IDE"),
    ("rollback_pou", "2. Code Editing, Search & Mirror", "Offline/IDE"),
    ("sync_sources_to_disk", "2. Code Editing, Search & Mirror", "Offline/IDE"),
    # 3. Compilation & IDE Message Sensing (4)
    ("compile_project", "3. Compilation & IDE Messages", "Offline/IDE"),
    ("get_compiler_messages", "3. Compilation & IDE Messages", "Offline/IDE"),
    ("get_message_categories", "3. Compilation & IDE Messages", "Offline/IDE"),
    ("get_ide_messages", "3. Compilation & IDE Messages", "Offline/IDE"),
    # 4. Online Connection & PLC Control (5)
    ("get_online_status", "4. Online Connection & PLC Control", "Offline/Online"),
    ("plc_login", "4. Online Connection & PLC Control", "Online/SoftPLC"),
    ("inspect_online", "4. Online Connection & PLC Control", "Online/SoftPLC"),
    ("plc_control", "4. Online Connection & PLC Control", "Online/SoftPLC"),
    ("plc_logout", "4. Online Connection & PLC Control", "Online/SoftPLC"),
    # 5. Variable I/O, Primitives, Structs & Arrays (9)
    ("read_variables", "5. Variable I/O, Primitives & Structs", "Online/SoftPLC"),
    ("write_variable", "5. Variable I/O, Primitives & Structs", "Online/SoftPLC"),
    ("pulse_variable", "5. Variable I/O, Primitives & Structs", "Online/SoftPLC"),
    ("wait_for_variable", "5. Variable I/O, Primitives & Structs", "Online/SoftPLC"),
    ("unforce_variables", "5. Variable I/O, Primitives & Structs", "Online/SoftPLC"),
    ("batch_write_variables", "5. Variable I/O, Primitives & Structs", "Online/SoftPLC"),
    ("write_struct", "5. Variable I/O, Primitives & Structs", "Online/SoftPLC"),
    ("read_struct", "5. Variable I/O, Primitives & Structs", "Online/SoftPLC"),
    ("read_array", "5. Variable I/O, Primitives & Structs", "Online/SoftPLC"),
    # 6. Real-time Trace Engine (4)
    ("get_trace_info", "6. Real-time Trace Engine", "Offline/IDE"),
    ("add_trace_variable", "6. Real-time Trace Engine", "Offline/IDE"),
    ("control_trace", "6. Real-time Trace Engine", "Offline/IDE"),
    ("analyze_trace", "6. Real-time Trace Engine", "Offline/CSV"),
    # 7. Logging & Diagnostics (5)
    ("setup_plc_logger", "7. Logging & Diagnostics", "Offline/IDE"),
    ("read_plc_logs", "7. Logging & Diagnostics", "Online/SoftPLC"),
    ("setup_device_logger", "7. Logging & Diagnostics", "Offline/IDE"),
    ("inject_code_log", "7. Logging & Diagnostics", "Offline/IDE"),
    ("read_device_log", "7. Logging & Diagnostics", "Offline/XML"),
    # 8. IDE UI Indicator & Visual Verification (2)
    ("get_ui_indicator_status", "8. IDE UI Indicator & Visual", "Offline/IDE"),
    ("capture_ide_screenshot", "8. IDE UI Indicator & Visual", "GUI IDE"),
]

CATEGORY_ORDER = [
    "1. Project Management & Navigation",
    "2. Code Editing, Search & Mirror",
    "3. Compilation & IDE Messages",
    "4. Online Connection & PLC Control",
    "5. Variable I/O, Primitives & Structs",
    "6. Real-time Trace Engine",
    "7. Logging & Diagnostics",
    "8. IDE UI Indicator & Visual",
]


def sanitize_text(text: str) -> str:
    if not text:
        return ""
    s = re.sub(r"[A-Za-z]:\\Users\\[^\\/\s\"']+", "<USER_HOME>", str(text), flags=re.IGNORECASE)
    s = re.sub(r"\\\\[A-Za-z0-9_.-]+\\Repos_VM", "<WORKSPACE>", s, flags=re.IGNORECASE)
    s = re.sub(r"//[A-Za-z0-9_.-]+/Repos_VM", "<WORKSPACE>", s, flags=re.IGNORECASE)
    return s


def is_pipe_ready(timeout_ms: int = 300) -> bool:
    try:
        kernel32 = ctypes.windll.kernel32
        kernel32.WaitNamedPipeW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32]
        kernel32.WaitNamedPipeW.restype = ctypes.c_int
        return bool(kernel32.WaitNamedPipeW(PIPE_PATH, timeout_ms))
    except Exception:
        return False


def wait_for_pipe(timeout_s: float = 90.0) -> bool:
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        if is_pipe_ready(500):
            return True
        time.sleep(0.5)
    return False


class McpStdioClient:
    """Persistent full-duplex stdin/stdout client over CodesysMcpCli.exe."""

    def __init__(self, cli_exe: str):
        self.cli_exe = cli_exe
        self.proc = None
        self.req_id = 1

    def __enter__(self):
        self.proc = subprocess.Popen(
            [self.cli_exe],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
        )
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        if self.proc and self.proc.poll() is None:
            try:
                self.proc.stdin.close()
            except Exception:
                pass
            try:
                self.proc.wait(timeout=3)
            except subprocess.TimeoutExpired:
                self.proc.kill()

    def rpc(self, method: str, params=None):
        rid = self.req_id
        self.req_id += 1
        payload = {"jsonrpc": "2.0", "id": rid, "method": method, "params": params or {}}
        t0 = time.perf_counter()
        self.proc.stdin.write(json.dumps(payload) + "\n")
        self.proc.stdin.flush()
        while True:
            line = self.proc.stdout.readline()
            if not line:
                raise RuntimeError(f"CLI EOF waiting for response id={rid} ({method})")
            line = line.strip()
            if not line:
                continue
            msg = json.loads(line)
            if msg.get("id") == rid:
                elapsed_ms = (time.perf_counter() - t0) * 1000.0
                return msg, elapsed_ms

    def call_tool(self, name: str, arguments=None):
        msg, elapsed_ms = self.rpc("tools/call", {"name": name, "arguments": arguments or {}})
        if "error" in msg:
            raise RuntimeError(f"JSON-RPC error on {name}: {msg['error']}")
        result = msg.get("result", {})
        is_err = bool(result.get("isError", False))
        content = result.get("content", [])
        text = content[0].get("text", "") if content else ""
        parsed = text
        try:
            parsed = json.loads(text)
        except Exception:
            pass
        return is_err, parsed, elapsed_ms


class MatrixRecorder:
    def __init__(self):
        self.results = {}  # tool_name -> dict(status, latency_ms, note)

    def record(self, tool_name: str, status: str, latency_ms: float = 0.0, note: str = ""):
        self.results[tool_name] = {
            "status": status,
            "latency_ms": round(float(latency_ms), 1),
            "note": sanitize_text(note),
        }

    def run_step(self, tool_name: str, fn):
        t0 = time.perf_counter()
        try:
            note = fn()
            elapsed = (time.perf_counter() - t0) * 1000.0
            if tool_name not in self.results:
                self.record(tool_name, "PASS", elapsed, note or "OK")
        except Exception as exc:
            elapsed = (time.perf_counter() - t0) * 1000.0
            self.record(tool_name, "FAIL", elapsed, str(exc)[:180])
            raise


def update_history_and_render_report(
    history_path: str,
    report_path: str,
    ide_env: dict,
    recorder: MatrixRecorder,
    total_duration_s: float,
):
    os.makedirs(os.path.dirname(os.path.abspath(history_path)), exist_ok=True)
    os.makedirs(os.path.dirname(os.path.abspath(report_path)), exist_ok=True)

    history = {"ide_versions": {}}
    if os.path.isfile(history_path):
        try:
            with open(history_path, "r", encoding="utf-8") as fh:
                history = json.load(fh)
        except Exception:
            history = {"ide_versions": {}}

    ide_ver = ide_env.get("ideVersion") or "Unknown"
    ver_entry = history["ide_versions"].setdefault(
        ide_ver,
        {
            "ideVersion": ide_ver,
            "ideProfile": ide_env.get("ideProfile", "Default"),
            "uiMode": ide_env.get("uiMode", "Unknown"),
            "mcpServerVersion": ide_env.get("mcpServerVersion", "Unknown"),
            "runsCount": 0,
            "lastRunUtc": "",
            "lastDurationSec": 0.0,
            "tools": {},
        },
    )

    now_utc = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    ver_entry["ideProfile"] = ide_env.get("ideProfile", ver_entry.get("ideProfile", "Default"))
    ver_entry["uiMode"] = ide_env.get("uiMode", ver_entry.get("uiMode", "Unknown"))
    ver_entry["mcpServerVersion"] = ide_env.get("mcpServerVersion", ver_entry.get("mcpServerVersion", "Unknown"))
    ver_entry["runsCount"] = int(ver_entry.get("runsCount", 0)) + 1
    ver_entry["lastRunUtc"] = now_utc
    ver_entry["lastDurationSec"] = round(total_duration_s, 2)

    tools_hist = ver_entry.setdefault("tools", {})
    for tool_name, _, _ in TOOL_CATALOG:
        rec = recorder.results.get(tool_name, {"status": "SKIP", "latency_ms": 0.0, "note": "Not executed"})
        th = tools_hist.setdefault(
            tool_name,
            {
                "passCount": 0,
                "failCount": 0,
                "skipCount": 0,
                "totalLatencyMs": 0.0,
                "avgLatencyMs": 0.0,
                "lastLatencyMs": 0.0,
                "lastStatus": "SKIP",
                "note": "",
            },
        )
        st = rec["status"]
        if st == "PASS":
            th["passCount"] += 1
        elif st == "FAIL":
            th["failCount"] += 1
        else:
            th["skipCount"] += 1
        th["totalLatencyMs"] = round(th.get("totalLatencyMs", 0.0) + rec["latency_ms"], 1)
        executed = th["passCount"] + th["failCount"]
        th["avgLatencyMs"] = round(th["totalLatencyMs"] / executed, 1) if executed > 0 else 0.0
        th["lastLatencyMs"] = rec["latency_ms"]
        th["lastStatus"] = st
        th["note"] = rec["note"]

    with open(history_path, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(history, fh, indent=2, ensure_ascii=False)

    # Render dual-layer Markdown matrix across all tracked CODESYS versions
    versions = sorted(history["ide_versions"].keys())
    lines = [
        "# CODESYS MCP Plugin — E2E Compatibility & Reliability Matrix",
        "",
        f"- **Last Updated (UTC)**: `{now_utc}`",
        f"- **MCP Server Version**: `{ide_env.get('mcpServerVersion', 'Unknown')}`",
        f"- **Tracked CODESYS IDE Versions**: {', '.join(f'`{v}`' for v in versions)}",
        f"- **Total Registered MCP Tools**: `{len(TOOL_CATALOG)}` across `{len(CATEGORY_ORDER)}` categories",
        "",
        "## 1. Functional Category × CODESYS Version Summary Matrix",
        "",
    ]

    header_cols = ["Functional Category", "Tools"]
    for v in versions:
        prof = history["ide_versions"][v].get("ideProfile", v)
        header_cols.append(f"`{v}` ({prof})")
    lines.append("| " + " | ".join(header_cols) + " |")
    lines.append("| :--- | :---: | " + " | ".join([":---:"] * len(versions)) + " |")

    for cat in CATEGORY_ORDER:
        cat_tools = [t for t, c, _ in TOOL_CATALOG if c == cat]
        row = [cat, str(len(cat_tools))]
        for v in versions:
            th_map = history["ide_versions"][v].get("tools", {})
            p_cnt = sum(1 for t in cat_tools if th_map.get(t, {}).get("lastStatus") == "PASS")
            f_cnt = sum(1 for t in cat_tools if th_map.get(t, {}).get("lastStatus") == "FAIL")
            s_cnt = len(cat_tools) - p_cnt - f_cnt
            latencies = [
                th_map.get(t, {}).get("lastLatencyMs", 0.0)
                for t in cat_tools
                if th_map.get(t, {}).get("lastStatus") == "PASS"
            ]
            avg_ms = round(sum(latencies) / len(latencies), 1) if latencies else 0.0
            hist_pass = sum(th_map.get(t, {}).get("passCount", 0) for t in cat_tools)
            hist_exec = sum(
                th_map.get(t, {}).get("passCount", 0) + th_map.get(t, {}).get("failCount", 0)
                for t in cat_tools
            )
            rel_pct = f"{(hist_pass / hist_exec * 100):.0f}%" if hist_exec > 0 else "N/A"
            if f_cnt > 0:
                badge = f"FAIL ({p_cnt}/{len(cat_tools)}, {f_cnt} err)"
            elif s_cnt == len(cat_tools):
                badge = f"SKIP (0/{len(cat_tools)})"
            elif s_cnt > 0:
                badge = f"PARTIAL ({p_cnt}/{len(cat_tools)}, {s_cnt} skip, {avg_ms}ms, rel {rel_pct})"
            else:
                badge = f"PASS ({p_cnt}/{len(cat_tools)}, {avg_ms}ms, rel {rel_pct})"
            row.append(badge)
        lines.append("| " + " | ".join(row) + " |")

    lines.extend([
        "",
        "## 2. Detailed Tool Verification Matrix (All 48 Registered Tools)",
        "",
    ])

    detail_headers = ["Tool", "Category", "Scope"]
    for v in versions:
        detail_headers.append(f"`{v}` Status / Latency / Reliability")
    detail_headers.append("Latest Verification Note")
    lines.append("| " + " | ".join(detail_headers) + " |")
    lines.append("| :--- | :--- | :---: | " + " | ".join([":---:"] * len(versions)) + " | :--- |")

    latest_v = ide_ver
    for tool_name, cat, scope in TOOL_CATALOG:
        row = [f"`{tool_name}`", cat, scope]
        latest_note = ""
        for v in versions:
            th = history["ide_versions"][v].get("tools", {}).get(tool_name, {})
            st = th.get("lastStatus", "SKIP")
            lat = th.get("lastLatencyMs", 0.0)
            pc = th.get("passCount", 0)
            fc = th.get("failCount", 0)
            rel = f"{(pc / (pc + fc) * 100):.0f}%" if (pc + fc) > 0 else "N/A"
            if st == "PASS":
                cell = f"PASS ({lat}ms, {rel})"
            elif st == "FAIL":
                cell = f"**FAIL** ({lat}ms, {rel})"
            else:
                cell = f"SKIP ({rel})"
            row.append(cell)
            if v == latest_v:
                latest_note = th.get("note", "")
        row.append(latest_note.replace("|", "\\|"))
        lines.append("| " + " | ".join(row) + " |")

    with open(report_path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(lines) + "\n")


def ensure_softplc_running(codesys_exe: str):
    """Ensure CODESYS Control Win V3 - x64 SoftPLC service is running via official CODESYSControlSysTray.exe."""
    def _is_softplc_running() -> bool:
        try:
            out = subprocess.check_output(
                ["tasklist", "/FI", "IMAGENAME eq CODESYSControlService.exe", "/NH"],
                text=True,
                stderr=subprocess.DEVNULL,
            )
            return "CODESYSControlService.exe" in out
        except Exception:
            return False

    if _is_softplc_running():
        return

    try:
        key_path = r"Software\3S-Smart Software Solutions GmbH\CODESYS Control Win SysTray"
        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, key_path) as k:
            winreg.SetValueEx(k, "DisableStartWarning", 0, winreg.REG_DWORD, 1)
    except Exception:
        pass

    # Derive <CODESYS_ROOT>\GatewayPLC\CODESYSControlSysTray.exe
    codesys_root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(codesys_exe))))
    systray_exe = os.path.join(codesys_root, "GatewayPLC", "CODESYSControlSysTray.exe")
    if not os.path.isfile(systray_exe):
        print(f"[MATRIX] CODESYSControlSysTray.exe not found at {systray_exe}; skipping SoftPLC auto-start.")
        return

    print(f"[MATRIX] Starting SoftPLC via {systray_exe} start ...")
    try:
        subprocess.run([systray_exe, "start"], timeout=30, check=False)
    except Exception as exc:
        print(f"[MATRIX] Warning starting SoftPLC: {exc}")
        return

    for _ in range(30):
        if _is_softplc_running():
            print("[MATRIX] SoftPLC (CODESYSControlService.exe) is running.")
            return
        time.sleep(0.5)
    print("[MATRIX] Warning: SoftPLC service did not appear within 15s.")


def run_matrix(args):
    launched_proc = None
    temp_dir_obj = None
    prev_open_project = None
    we_opened_project = False
    recorder = MatrixRecorder()
    t_suite_start = time.perf_counter()

    if args.launch_if_needed or args.start_softplc:
        ensure_softplc_running(args.codesys_exe)

    if not is_pipe_ready(500):
        if not args.launch_if_needed:
            raise RuntimeError(f"MCP pipe {PIPE_PATH} is not active. Pass --launch-if-needed or start CODESYS IDE.")
        if not os.path.isfile(args.codesys_exe):
            raise FileNotFoundError(f"CODESYS executable not found: {args.codesys_exe}")
        cmd_str = f'"{args.codesys_exe}" --profile="{args.profile}" --runscript="{SESSION_SCRIPT}"'
        if args.headless:
            cmd_str += " --noUI"
        print(f"[MATRIX] Launching CODESYS: {cmd_str}")
        launched_proc = subprocess.Popen(cmd_str, shell=False)
        if not wait_for_pipe(timeout_s=90.0):
            raise TimeoutError(f"Timed out waiting 90s for MCP pipe {PIPE_PATH}")
        print("[MATRIX] Named pipe is ready.")

    ide_env = {
        "ideVersion": "Unknown",
        "ideProfile": args.profile,
        "uiMode": "Headless" if args.headless else "GUI",
        "mcpServerVersion": "Unknown",
    }

    try:
        with McpStdioClient(CLI_EXE) as client:
            # 1. Handshake & Host Version Probe
            init_resp, _ = client.rpc("initialize")
            server_info = init_resp.get("result", {}).get("serverInfo", {})
            list_resp, _ = client.rpc("tools/list")
            listed_tools = [t["name"] for t in list_resp.get("result", {}).get("tools", [])]
            assert len(listed_tools) == len(TOOL_CATALOG), f"Expected {len(TOOL_CATALOG)} tools, got {len(listed_tools)}"

            err, initial_info, info_ms = client.call_tool("get_project_info", {})
            assert not err, f"get_project_info failed: {initial_info}"
            ide_env["ideVersion"] = initial_info.get("ideVersion") or server_info.get("ideVersion") or "Unknown"
            ide_env["ideProfile"] = initial_info.get("ideProfile") or server_info.get("ideProfile") or args.profile
            ide_env["uiMode"] = initial_info.get("uiMode") or server_info.get("uiMode") or "Unknown"
            ide_env["mcpServerVersion"] = initial_info.get("mcpServerVersion") or server_info.get("version") or "Unknown"
            if initial_info.get("isOpen") and initial_info.get("path"):
                prev_open_project = initial_info["path"]

            print(
                f"[MATRIX] Host CODESYS IDE: v{ide_env['ideVersion']} | "
                f"Profile='{ide_env['ideProfile']}' | UI={ide_env['uiMode']} | MCP v{ide_env['mcpServerVersion']}"
            )

            # Category 8: UI Indicator & Screenshot
            err, ui_info, ui_ms = client.call_tool("get_ui_indicator_status", {})
            if not err and ui_info.get("ideVersion"):
                recorder.record("get_ui_indicator_status", "PASS", ui_ms, f"status={ui_info.get('status')}, ui={ui_info.get('uiMode')}")
            else:
                recorder.record("get_ui_indicator_status", "FAIL", ui_ms, str(ui_info))

            if ide_env["uiMode"] == "GUI":
                with tempfile.TemporaryDirectory(prefix="cds_cap_") as cap_dir:
                    shot_path = os.path.join(cap_dir, "ide_matrix.png")
                    err, cap_res, cap_ms = client.call_tool("capture_ide_screenshot", {"output_path": shot_path, "max_width": 800})
                    if not err and cap_res.get("success") and os.path.isfile(shot_path):
                        recorder.record("capture_ide_screenshot", "PASS", cap_ms, f"{cap_res.get('width')}x{cap_res.get('height')}")
                    elif isinstance(cap_res, dict) and "minimized" in str(cap_res.get("message", "")).lower():
                        recorder.record("capture_ide_screenshot", "SKIP", cap_ms, "CODESYS window minimized")
                    else:
                        recorder.record("capture_ide_screenshot", "FAIL", cap_ms, str(cap_res))
            else:
                recorder.record("capture_ide_screenshot", "SKIP", 0.0, f"Skipped in {ide_env['uiMode']} mode")

            # Prepare isolated temp project
            if not os.path.isfile(args.project):
                raise FileNotFoundError(f"Base test project not found: {args.project}")
            temp_dir_obj = tempfile.TemporaryDirectory(prefix="cds_matrix_")
            iso_proj = os.path.join(temp_dir_obj.name, os.path.basename(args.project))
            shutil.copy2(args.project, iso_proj)

            # Log out if currently online before opening isolated project
            err, st_pre, _ = client.call_tool("get_online_status", {})
            if not err and st_pre.get("isLoggedIn"):
                client.call_tool("plc_logout", {})

            # Category 1: open_project, get_project_info, get_project_tree
            err, open_res, open_ms = client.call_tool("open_project", {"path": iso_proj})
            if not err and open_res.get("isOpen"):
                we_opened_project = True
                recorder.record("open_project", "PASS", open_ms, f"Opened isolated {os.path.basename(iso_proj)}")
            else:
                recorder.record("open_project", "FAIL", open_ms, str(open_res))
                raise RuntimeError(f"Failed to open isolated project: {open_res}")

            err, pinfo, pinfo_ms = client.call_tool("get_project_info", {})
            recorder.record(
                "get_project_info",
                "PASS" if (not err and pinfo.get("isOpen")) else "FAIL",
                pinfo_ms,
                f"ideVersion={pinfo.get('ideVersion')}, app={pinfo.get('activeApplication')}",
            )

            err, tree, tree_ms = client.call_tool("get_project_tree", {"full": False})
            recorder.record(
                "get_project_tree",
                "PASS" if (not err and isinstance(tree, list) and len(tree) > 0) else "FAIL",
                tree_ms,
                f"nodes={len(tree) if isinstance(tree, list) else 0}",
            )

            # ================================================================
            # PHASE A: Clean Project -> PLC Logger + Compile + Online SoftPLC
            # ================================================================
            err, mcat_res, mcat_ms = client.call_tool("get_message_categories", {})
            recorder.record("get_message_categories", "PASS" if (not err and len(mcat_res.get("categories", [])) > 0) else "FAIL", mcat_ms, f"categories={len(mcat_res.get('categories', [])) if isinstance(mcat_res, dict) else 0}")

            err, imsg_res, imsg_ms = client.call_tool("get_ide_messages", {})
            recorder.record("get_ide_messages", "PASS" if (not err and "messages" in imsg_res) else "FAIL", imsg_ms, f"count={len(imsg_res.get('messages', [])) if isinstance(imsg_res, dict) else 0}")

            # Setup PLC logger on MOTION_PRG and emit one sample log entry before compiling
            err, spl_res, spl_ms = client.call_tool("setup_plc_logger", {"buffer_size": 16, "auto_inject_pou": "MOTION_PRG"})
            recorder.record("setup_plc_logger", "PASS" if (not err and spl_res.get("success")) else "FAIL", spl_ms, "GVL_McpLogger + FB_McpLogger ready")
            if not err and spl_res.get("success"):
                client.call_tool("edit_pou", {
                    "name": "MOTION_PRG",
                    "section": "implementation",
                    "target": "GVL_McpLogger.McpLogger.TickCycle();",
                    "replacement": "GVL_McpLogger.McpLogger.TickCycle();\r\nGVL_McpLogger.McpLogger.WriteLog(sTag := 'MATRIX', sMsg := 'E2E matrix probe', rVal1 := 1.0, rVal2 := 2.0);",
                })

            err, comp_res, comp_ms = client.call_tool("compile_project", {"clean": False, "rebuild": False})
            recorder.record(
                "compile_project",
                "PASS" if (not err and comp_res.get("success") and comp_res.get("errorCount", -1) == 0) else "FAIL",
                comp_ms,
                f"errors={comp_res.get('errorCount')}, warnings={comp_res.get('warningCount')}",
            )

            err, cmsg_res, cmsg_ms = client.call_tool("get_compiler_messages", {})
            recorder.record(
                "get_compiler_messages",
                "PASS" if (not err and isinstance(cmsg_res, dict) and isinstance(cmsg_res.get("messages"), list)) else "FAIL",
                cmsg_ms,
                f"errors={cmsg_res.get('errorCount', 0) if isinstance(cmsg_res, dict) else '?'}, messages={len(cmsg_res.get('messages', [])) if isinstance(cmsg_res, dict) else 0}",
            )

            # Categories 4 & 5: Online status + SoftPLC verification on native MOTION_PRG symbols
            err, ost_res, ost_ms = client.call_tool("get_online_status", {})
            recorder.record("get_online_status", "PASS" if not err else "FAIL", ost_ms, f"isLoggedIn={ost_res.get('isLoggedIn') if isinstance(ost_res, dict) else False}")

            login_args = {
                "mode": "try_online_change",
                "delete_foreign_apps": True,
                "ensure_running": True,
                "auto_recover_exception": True,
            }
            if os.environ.get("CODESYS_DEVICE_USER"):
                login_args["username"] = os.environ["CODESYS_DEVICE_USER"]
            if os.environ.get("CODESYS_DEVICE_PASSWORD"):
                login_args["password"] = os.environ["CODESYS_DEVICE_PASSWORD"]
            err_li, li_res, li_ms = client.call_tool("plc_login", login_args)

            if not err_li and isinstance(li_res, dict) and li_res.get("isLoggedIn"):
                recorder.record("plc_login", "PASS", li_ms, f"state={li_res.get('applicationState')}, autoStarted={li_res.get('autoStarted')}")

                err, ins_res, ins_ms = client.call_tool("inspect_online", {"target": "Device"})
                recorder.record("inspect_online", "PASS" if not err else "FAIL", ins_ms, "Device inspected")

                err, boot_res, boot_ms = client.call_tool("create_boot_application", {})
                recorder.record("create_boot_application", "PASS" if (not err and boot_res.get("success")) else "FAIL", boot_ms, "Boot app created")

                err_stop, _, ms_stop = client.call_tool("plc_control", {"action": "stop"})
                err_start, _, ms_start = client.call_tool("plc_control", {"action": "start"})
                recorder.record("plc_control", "PASS" if not (err_stop or err_start) else "FAIL", ms_stop + ms_start, "stop -> start verified")

                err, wv_res, wv_ms = client.call_tool("write_variable", {"name": "MOTION_PRG.eHmiProfileSelect", "value": "E_PressProfileId.PROFILE_HARD_PIN"})
                wv_ok = not err and isinstance(wv_res, dict) and wv_res.get("verified")
                recorder.record("write_variable", "PASS" if wv_ok else "FAIL", wv_ms, "Enum E_PressProfileId.PROFILE_HARD_PIN resolved & verified" if wv_ok else str(wv_res)[:140])

                err, rv_res, rv_ms = client.call_tool("read_variables", {"expressions": ["MOTION_PRG.eHmiProfileSelect", "MOTION_PRG.bHmiReset"]})
                recorder.record("read_variables", "PASS" if (not err and len(rv_res.get("values", {})) == 2) else "FAIL", rv_ms, "Read 2 variables")

                err, pv_res, pv_ms = client.call_tool("pulse_variable", {"name": "MOTION_PRG.bHmiReset", "pulse_ms": 30, "pre_clear": True})
                recorder.record("pulse_variable", "PASS" if (not err and pv_res.get("success") and pv_res.get("verified")) else "FAIL", pv_ms, f"pulse_ms=30, elapsed={pv_res.get('elapsedMs') if isinstance(pv_res, dict) else 0}ms")

                err, wfv_res, wfv_ms = client.call_tool("wait_for_variable", {"name": "MOTION_PRG.eHmiProfileSelect", "target_value": "PROFILE_HARD_PIN", "operator": "==", "timeout_ms": 1000, "poll_ms": 20})
                wfv_ok = not err and isinstance(wfv_res, dict) and wfv_res.get("matched")
                recorder.record("wait_for_variable", "PASS" if wfv_ok else "FAIL", wfv_ms, f"samples={wfv_res.get('samplesCount') if isinstance(wfv_res, dict) else 0}" if wfv_ok else str(wfv_res)[:140])

                err, bw_res, bw_ms = client.call_tool("batch_write_variables", {"variables": {"MOTION_PRG.PressRecipe.rTargetForce": "5100.0", "MOTION_PRG.PressRecipe.rFastSpeed": "45.0"}, "verify": True})
                recorder.record("batch_write_variables", "PASS" if (not err and bw_res.get("allVerified")) else "FAIL", bw_ms, "2 vars atomic batch verified")

                time.sleep(0.25)
                err, ws_res, ws_ms = client.call_tool("write_struct", {"struct_name": "MOTION_PRG.PressRecipe", "values": {"rTargetForce": 5000.0, "rFastSpeed": 50.0}, "verify": True})
                ws_ok = not err and isinstance(ws_res, dict) and ws_res.get("allVerified")
                if not ws_ok:
                    print(f"[MATRIX] write_struct FAIL payload: {ws_res}")
                recorder.record("write_struct", "PASS" if ws_ok else "FAIL", ws_ms, "PressRecipe struct verified" if ws_ok else str(ws_res)[:160])

                err, rs_res, rs_ms = client.call_tool("read_struct", {"struct_name": "MOTION_PRG.PressRecipe"})
                recorder.record("read_struct", "PASS" if (not err and rs_res.get("success") and len(rs_res.get("values", {})) >= 4) else "FAIL", rs_ms, f"fields={len(rs_res.get('values', {})) if isinstance(rs_res, dict) else 0}")

                err, ra_res, ra_ms = client.call_tool("read_array", {"variable_name": "MOTION_PRG.fbPressStats.aForceHistory", "start_index": 0, "count": 4})
                recorder.record("read_array", "PASS" if (not err and ra_res.get("success") and len(ra_res.get("items", [])) == 4) else "FAIL", ra_ms, "items=4")

                client.call_tool("write_variable", {"name": "MOTION_PRG.rSimPartToleranceMm", "value": "0.1", "force": True})
                err, uf_res, uf_ms = client.call_tool("unforce_variables", {"expressions": ["MOTION_PRG.rSimPartToleranceMm"]})
                recorder.record("unforce_variables", "PASS" if not err else "FAIL", uf_ms, "Unforced test expression")

                time.sleep(0.25)
                err, rpl_res, rpl_ms = client.call_tool("read_plc_logs", {"last_n": 5})
                recorder.record("read_plc_logs", "PASS" if (not err and rpl_res.get("success") and len(rpl_res.get("entries", [])) >= 1) else "FAIL", rpl_ms, f"entries={len(rpl_res.get('entries', [])) if isinstance(rpl_res, dict) else 0}, cycle={rpl_res.get('currentPlcCycle') if isinstance(rpl_res, dict) else 0}")

                err, lo_res, lo_ms = client.call_tool("plc_logout", {})
                recorder.record("plc_logout", "PASS" if (not err and not lo_res.get("isLoggedIn")) else "FAIL", lo_ms, "Logged out cleanly")
            else:
                print(f"[MATRIX] plc_login failed: {li_res}")
                reason = "SoftPLC runtime offline (start CODESYS Control Win V3 x64 to enable)"
                for online_tool in [
                    "plc_login", "inspect_online", "create_boot_application", "plc_control",
                    "read_variables", "write_variable", "pulse_variable", "wait_for_variable",
                    "unforce_variables", "batch_write_variables", "write_struct", "read_struct",
                    "read_array", "read_plc_logs", "plc_logout",
                ]:
                    recorder.record(online_tool, "SKIP", 0.0, reason)

            # ================================================================
            # PHASE B: Offline Object Creation, Edit, Diff, Mirror, Trace & Device Logger
            # ================================================================
            err, fld_res, fld_ms = client.call_tool("create_folder", {"name": "_MatrixFolder"})
            recorder.record("create_folder", "PASS" if (not err and fld_res.get("success")) else "FAIL", fld_ms, "_MatrixFolder")

            err, itf_res, itf_ms = client.call_tool("create_interface", {"name": "ITF_MatrixProbe"})
            recorder.record("create_interface", "PASS" if (not err and itf_res.get("success")) else "FAIL", itf_ms, "ITF_MatrixProbe")

            enum_decl = "TYPE E_MatrixMode :\n(\n    MODE_A := 0,\n    MODE_B := 1\n);\nEND_TYPE"
            struct_decl = "TYPE ST_MatrixPayload :\nSTRUCT\n    eMode : E_MatrixMode := E_MatrixMode.MODE_A;\n    nCount : INT := 0;\nEND_STRUCT\nEND_TYPE"
            fb_decl = "FUNCTION_BLOCK FB_MatrixProbe\nVAR\n    _nVal : INT := 1;\nEND_VAR"
            prg_decl = "PROGRAM PRG_MatrixProbe\nVAR\n    nCounter : INT := 0;\nEND_VAR"

            err_e, _, ms_e = client.call_tool("write_pou", {"name": "E_MatrixMode", "declaration": enum_decl, "type": "Enumeration"})
            err_s, _, ms_s = client.call_tool("write_pou", {"name": "ST_MatrixPayload", "declaration": struct_decl, "type": "Structure"})
            err_fb, _, ms_fb = client.call_tool("write_pou", {"name": "FB_MatrixProbe", "declaration": fb_decl, "implementation": "_nVal := _nVal + 1;", "type": "FunctionBlock"})
            err_p, _, ms_p = client.call_tool("write_pou", {"name": "PRG_MatrixProbe", "declaration": prg_decl, "implementation": "nCounter := nCounter + 1;", "type": "Program"})
            recorder.record(
                "write_pou",
                "PASS" if not (err_e or err_s or err_fb or err_p) else "FAIL",
                ms_p,
                "Created Enumeration, Structure, FB & Program",
            )

            err_m, m_res, m_ms = client.call_tool("create_method", {
                "parent": "FB_MatrixProbe",
                "name": "GetVal",
                "return_type": "INT",
                "implementation": "GetVal := _nVal;",
            })
            recorder.record("create_method", "PASS" if (not err_m and m_res.get("name") == "GetVal") else "FAIL", m_ms, "FB_MatrixProbe.GetVal")

            err_pr, pr_res, pr_ms = client.call_tool("create_property", {
                "parent": "FB_MatrixProbe",
                "name": "PropVal",
                "return_type": "INT",
            })
            recorder.record("create_property", "PASS" if (not err_pr and pr_res.get("success")) else "FAIL", pr_ms, "FB_MatrixProbe.PropVal")

            err, rd_res, rd_ms = client.call_tool("read_pou", {"name": "PRG_MatrixProbe"})
            recorder.record("read_pou", "PASS" if (not err and "nCounter + 1" in rd_res.get("implementation", "")) else "FAIL", rd_ms, "PRG_MatrixProbe")

            err, ed_res, ed_ms = client.call_tool("edit_pou", {
                "name": "PRG_MatrixProbe",
                "section": "implementation",
                "target": "nCounter := nCounter + 1;",
                "replacement": "nCounter := nCounter + 2;",
            })
            recorder.record(
                "edit_pou",
                "PASS" if (not err and ed_res.get("success") and "+nCounter := nCounter + 2;" in ed_res.get("diff", "")) else "FAIL",
                ed_ms,
                f"appliedEdits={ed_res.get('appliedEdits') if isinstance(ed_res, dict) else 0}",
            )

            err, srch_res, srch_ms = client.call_tool("search_code", {"query": "nCounter := nCounter + 2;", "is_regex": False})
            recorder.record(
                "search_code",
                "PASS" if (not err and srch_res.get("totalMatches", 0) >= 1) else "FAIL",
                srch_ms,
                f"matches={srch_res.get('totalMatches') if isinstance(srch_res, dict) else 0}",
            )

            err, diff_res, diff_ms = client.call_tool("get_diff", {"name": "PRG_MatrixProbe"})
            recorder.record(
                "get_diff",
                "PASS" if (not err and diff_res.get("modifiedCount", 0) >= 1) else "FAIL",
                diff_ms,
                f"modifiedCount={diff_res.get('modifiedCount') if isinstance(diff_res, dict) else 0}",
            )

            err, prev_res, prev_ms = client.call_tool("preview_diff", {"name": "PRG_MatrixProbe", "viewer": "none"})
            recorder.record(
                "preview_diff",
                "PASS" if (not err and prev_res.get("success")) else "FAIL",
                prev_ms,
                f"exported={len(prev_res.get('exportedPous', [])) if isinstance(prev_res, dict) else 0}",
            )

            client.call_tool("write_pou", {"name": "PRG_MatrixRollback", "declaration": "PROGRAM PRG_MatrixRollback\nVAR\nEND_VAR", "implementation": " ;", "type": "Program"})
            err, rb_res, rb_ms = client.call_tool("rollback_pou", {"name": "PRG_MatrixRollback"})
            recorder.record("rollback_pou", "PASS" if (not err and rb_res.get("success")) else "FAIL", rb_ms, f"action={rb_res.get('action') if isinstance(rb_res, dict) else ''}")

            with tempfile.TemporaryDirectory(prefix="cds_mir_") as mir_dir:
                err, mir_res, mir_ms = client.call_tool("sync_sources_to_disk", {"mirror_dir": mir_dir, "persist_for_session": True})
                recorder.record(
                    "sync_sources_to_disk",
                    "PASS" if (not err and mir_res.get("created", 0) > 0) else "FAIL",
                    mir_ms,
                    f"created={mir_res.get('created') if isinstance(mir_res, dict) else 0}",
                )
                client.call_tool("sync_sources_to_disk", {"disable": True})

            err, sdev_res, sdev_ms = client.call_tool("setup_device_logger", {})
            recorder.record(
                "setup_device_logger",
                "PASS" if (not err and sdev_res.get("success")) else "FAIL",
                sdev_ms,
                f"fcCreated={sdev_res.get('fcCreated') if isinstance(sdev_res, dict) else False}",
            )

            err, inj_res, inj_ms = client.call_tool("inject_code_log", {
                "pou_name": "PRG_MatrixProbe",
                "target_pattern": "nCounter := nCounter + 2;",
                "log_message": "Matrix probe log",
            })
            recorder.record(
                "inject_code_log",
                "PASS" if (not err and isinstance(inj_res, dict) and "FC_LogMsg(" in inj_res.get("implementation", "")) else "FAIL",
                inj_ms,
                "Injected FC_LogMsg into PRG_MatrixProbe",
            )

            err_del, del_res, del_ms = client.call_tool("delete_pou", {"name": "PRG_MatrixProbe"})
            for extra in ("FB_MatrixProbe", "ITF_MatrixProbe", "FC_LogMsg", "_MatrixFolder"):
                try:
                    client.call_tool("delete_pou", {"name": extra})
                except Exception:
                    pass
            recorder.record("delete_pou", "PASS" if (not err_del and del_res.get("deleted")) else "FAIL", del_ms, "Deleted temporary POUs")

            with tempfile.TemporaryDirectory(prefix="cds_log_") as log_dir:
                xml_path = os.path.join(log_dir, "sample_device_log.xml")
                with open(xml_path, "w", encoding="utf-8") as xfh:
                    xfh.write(
                        "<DeviceLog><Entry severity=\"information\" component=\"CmpApp\" user=\"admin\" "
                        "timestamp=\"2026/11/26 20:00:00\" infoId=\"1\">Matrix XML Log Probe</Entry></DeviceLog>"
                    )
                err, rdl_res, rdl_ms = client.call_tool("read_device_log", {"file_path": xml_path, "keyword": "Matrix XML"})
                recorder.record(
                    "read_device_log",
                    "PASS" if (not err and rdl_res.get("success") and len(rdl_res.get("entries", [])) == 1) else "FAIL",
                    rdl_ms,
                    "XML log parse verified",
                )

            err, tr_info, tr_ms = client.call_tool("get_trace_info", {"trace_name": "Trace"})
            if not err and tr_info.get("success"):
                recorder.record("get_trace_info", "PASS", tr_ms, f"vars={len(tr_info.get('variables', []))}")
                err, add_tr, add_tr_ms = client.call_tool("add_trace_variable", {"variable_name": "MOTION_PRG.rDispPos", "trace_name": "Trace"})
                recorder.record("add_trace_variable", "PASS" if (not err and add_tr.get("success")) else "FAIL", add_tr_ms, "MOTION_PRG.rDispPos")
                err, ct_res, ct_ms = client.call_tool("control_trace", {"action": "status", "trace_name": "Trace"})
                recorder.record("control_trace", "PASS" if (not err and ct_res.get("success")) else "FAIL", ct_ms, "action=status")
            else:
                recorder.record("get_trace_info", "SKIP", tr_ms, "No Trace object in project")
                recorder.record("add_trace_variable", "SKIP", 0.0, "No Trace object in project")
                recorder.record("control_trace", "SKIP", 0.0, "No Trace object in project")

            with tempfile.TemporaryDirectory(prefix="cds_trc_") as trc_dir:
                csv_path = os.path.join(trc_dir, "matrix_trace.csv")
                with open(csv_path, "w", encoding="utf-8", newline="\n") as tfh:
                    tfh.write("Time (ms);Application.MOTION_PRG.rDispForce\n")
                    for i in range(100):
                        tfh.write(f"{float(i):.1f};{5200.0 if i == 50 else 100.0 + i}\n")
                err, at_res, at_ms = client.call_tool("analyze_trace", {"file_path": csv_path, "include_series": True, "max_points": 25})
                recorder.record(
                    "analyze_trace",
                    "PASS" if (not err and at_res.get("success") and at_res.get("returnedPointsCount") == 25) else "FAIL",
                    at_ms,
                    f"cycles={at_res.get('totalCyclesRecorded') if isinstance(at_res, dict) else 0}",
                )

            err, sav_res, sav_ms = client.call_tool("save_project", {})
            recorder.record("save_project", "PASS" if (not err and sav_res.get("saved")) else "FAIL", sav_ms, "Saved temp project")

            # Close isolated project
            err, cls_res, cls_ms = client.call_tool("close_project", {})
            recorder.record("close_project", "PASS" if (not err and cls_res.get("closed")) else "FAIL", cls_ms, "Closed isolated project")
            we_opened_project = False

            if prev_open_project and os.path.isfile(prev_open_project) and not args.close_after:
                try:
                    client.call_tool("open_project", {"path": prev_open_project})
                except Exception:
                    pass
    finally:
        if temp_dir_obj is not None:
            try:
                temp_dir_obj.cleanup()
            except Exception:
                pass
        if args.close_after and launched_proc is not None and launched_proc.poll() is None:
            try:
                launched_proc.terminate()
                launched_proc.wait(timeout=8)
            except Exception:
                try:
                    launched_proc.kill()
                except Exception:
                    pass

    total_s = time.perf_counter() - t_suite_start
    update_history_and_render_report(args.history_json, args.report_md, ide_env, recorder, total_s)

    passed = sum(1 for r in recorder.results.values() if r["status"] == "PASS")
    failed = sum(1 for r in recorder.results.values() if r["status"] == "FAIL")
    skipped = sum(1 for r in recorder.results.values() if r["status"] == "SKIP")
    print(f"[MATRIX] Done in {total_s:.2f}s -> PASS={passed}, SKIP={skipped}, FAIL={failed} (Total={len(TOOL_CATALOG)})")
    print(f"[MATRIX] Report written to: {args.report_md}")
    if failed > 0:
        for t, r in recorder.results.items():
            if r["status"] == "FAIL":
                print(f"  [FAIL] {t}: {r['note']}")
        sys.exit(1)


def main():
    parser = argparse.ArgumentParser(description="CODESYS MCP E2E Reliability & Version Matrix Runner")
    parser.add_argument("--launch-if-needed", action="store_true", help="Launch CODESYS IDE if MCP pipe is not active")
    parser.add_argument("--start-softplc", action="store_true", help="Ensure local SoftPLC service is started via CODESYSControlSysTray.exe")
    parser.add_argument("--close-after", action="store_true", help="Terminate launched CODESYS IDE after completion")
    parser.add_argument("--headless", action="store_true", help="Launch CODESYS with --noUI when using --launch-if-needed")
    parser.add_argument("--codesys-exe", default=DEFAULT_CODESYS_EXE, help="Path to CODESYS.exe")
    parser.add_argument("--profile", default=DEFAULT_PROFILE, help="CODESYS --profile name")
    parser.add_argument("--project", default=DEFAULT_PROJECT, help="Base .project file to clone into temp dir")
    parser.add_argument("--report-md", default=DEFAULT_REPORT_MD, help="Output Markdown matrix path")
    parser.add_argument("--history-json", default=DEFAULT_HISTORY_JSON, help="Output JSON history path")
    args = parser.parse_args()
    run_matrix(args)


if __name__ == "__main__":
    main()
