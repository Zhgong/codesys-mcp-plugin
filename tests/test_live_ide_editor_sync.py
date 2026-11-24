"""Opt-in live GUI E2E test for unsaved CODESYS IDE editor buffer sync.

Verifies that typing into an open CODESYS ST editor (declaration or
implementation) syncs to `<project_dir>\\.cds_mcp\\src\\*.st` automatically
within a few seconds WITHOUT leaving focus and WITHOUT pressing Ctrl+S,
and that existing `.st` content is preserved (never wiped to empty).

Usage:
    python tests/test_live_ide_editor_sync.py
    python tests/test_live_ide_editor_sync.py --launch-if-needed --close-after
    python tests/test_live_ide_editor_sync.py --mirror-dir path\\to\\.cds_mcp\\src
"""

import argparse
import ctypes
from ctypes import wintypes
import glob
import json
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time


WM_KEYDOWN = 0x0100
WM_CHAR = 0x0102
VK_BACK = 0x08
VK_END = 0x23

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_CODESYS_EXE = r"C:\Program Files\CODESYS 3.5.20.50\CODESYS\Common\CODESYS.exe"
DEFAULT_CODESYS_PROFILE = "CODESYS V3.5 SP20 Patch 5"
DEFAULT_PROJECT_PATH = os.environ.get(
    "CODESYS_MCP_TEST_PROJECT",
    os.path.abspath(os.path.join(REPO_ROOT, "..", "agy-codesys", "projects", "servo_press", "ServoPress_VirtualAxis.project"))
)


user32 = ctypes.windll.user32
user32.SendMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
user32.SendMessageW.restype = wintypes.LPARAM
user32.GetParent.argtypes = [wintypes.HWND]
user32.GetParent.restype = wintypes.HWND
kernel32 = ctypes.windll.kernel32
kernel32.WaitNamedPipeW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32]
kernel32.WaitNamedPipeW.restype = ctypes.c_int


def find_codesys_hwnd():
    matches = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def enum_cb(hwnd, _lparam):
        if not user32.IsWindowVisible(hwnd):
            return True
        length = user32.GetWindowTextLengthW(hwnd)
        if length <= 0:
            return True
        buf = ctypes.create_unicode_buffer(length + 1)
        user32.GetWindowTextW(hwnd, buf, length + 1)
        title = buf.value
        if "CODESYS" in title and (".project" in title or "- CODESYS" in title):
            matches.append((hwnd, title))
        return True

    user32.EnumWindows(enum_cb, 0)
    return matches[0] if matches else (None, None)


def find_syned_textarea_hwnds(codesys_hwnd):
    children_by_parent = {}

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def enum_child_cb(h, _lparam):
        if not user32.IsWindowVisible(h):
            return True
        parent = user32.GetParent(h)
        rect = wintypes.RECT()
        if user32.GetWindowRect(h, ctypes.byref(rect)):
            w = rect.right - rect.left
            hgt = rect.bottom - rect.top
            children_by_parent.setdefault(parent, []).append((h, rect.left, rect.top, w, hgt))
        return True

    user32.EnumChildWindows(wintypes.HWND(codesys_hwnd), enum_child_cb, 0)
    matches = []
    for kids in children_by_parent.values():
        for h1, x1, y1, w1, hgt1 in kids:
            if 20 <= w1 <= 120 and hgt1 >= 40:
                for h2, x2, y2, w2, hgt2 in kids:
                    if (
                        h1 != h2
                        and abs(x2 - (x1 + w1)) <= 2
                        and abs(y1 - y2) <= 2
                        and abs(hgt1 - hgt2) <= 2
                        and w2 >= 100
                    ):
                        matches.append((y2, h2))

    seen = set()
    ordered = []
    for _y, h in sorted(matches, key=lambda item: item[0]):
        if h not in seen:
            seen.add(h)
            ordered.append(h)
    return ordered


def send_vk_message(hwnd: int, vk_code: int, count: int = 1):
    for _ in range(count):
        user32.SendMessageW(wintypes.HWND(hwnd), WM_KEYDOWN, vk_code, 1)


def send_char_sequence(hwnd: int, text: str):
    for ch in text:
        user32.SendMessageW(wintypes.HWND(hwnd), WM_CHAR, ord(ch), 1)


def _find_cli_exe() -> str:
    repo_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    cli_candidates = [
        os.path.join(repo_root, "bin", "Release", "CodesysMcpCli.exe"),
        os.path.join(repo_root, "bin", "Debug", "CodesysMcpCli.exe"),
        os.path.join(repo_root, "CodesysMcpCli", "bin", "Release", "CodesysMcpCli.exe"),
        os.path.join(repo_root, "CodesysMcpCli", "bin", "Debug", "CodesysMcpCli.exe"),
    ]
    return next((p for p in cli_candidates if os.path.isfile(p)), "")


def _call_cli_batch(reqs: list, target_ids: set, timeout_sec: float = 15.0) -> dict:
    cli_exe = _find_cli_exe()
    if not cli_exe:
        raise FileNotFoundError("CodesysMcpCli.exe not found under bin/Release or bin/Debug.")

    pipe_name = os.environ.get("CODESYS_MCP_PIPE", "codesys_mcp_pipe")
    proc = None
    timer = None
    responses = {}
    try:
        proc = subprocess.Popen(
            [cli_exe, "--pipe", pipe_name],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
            env=os.environ.copy(),
        )
        timer = threading.Timer(timeout_sec, proc.kill)
        timer.daemon = True
        timer.start()

        for req in reqs:
            proc.stdin.write(req + "\n")
        proc.stdin.flush()

        # Keep stdin open until all expected response IDs have been received.
        while len(responses) < len(target_ids):
            line = proc.stdout.readline()
            if not line:
                break
            try:
                msg = json.loads(line.strip())
                msg_id = msg.get("id")
                if msg_id in target_ids:
                    responses[msg_id] = msg
            except Exception:
                continue
    finally:
        if timer is not None:
            timer.cancel()
        if proc is not None and proc.poll() is None:
            try:
                proc.stdin.close()
            except Exception:
                pass
            try:
                proc.wait(timeout=2)
            except Exception:
                try:
                    proc.kill()
                except Exception:
                    pass
    return responses


def discover_mirror_dir_via_cli() -> str:
    if not _find_cli_exe():
        return ""

    reqs = [
        json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "live-e2e", "version": "1.0"}}}),
        json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}),
        json.dumps({"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {"name": "get_project_info", "arguments": {}}}),
    ]
    try:
        responses = _call_cli_batch(reqs, {2}, timeout_sec=10.0)
        msg = responses.get(2)
        if msg:
            text = msg["result"]["content"][0]["text"]
            info = json.loads(text)
            return info.get("mirrorDir") or info.get("mirrorDirectory", "")
    except Exception:
        return ""
    return ""


def verify_live_mcp_tools_via_cli(pou_name: str, token: str):
    reqs = [
        json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "live-e2e", "version": "1.0"}}}),
        json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}),
        json.dumps({"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {"name": "read_pou", "arguments": {"name": pou_name}}}),
        json.dumps({"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "search_code", "arguments": {"query": token}}}),
        json.dumps({"jsonrpc": "2.0", "id": 4, "method": "tools/call", "params": {"name": "get_diff", "arguments": {"name": pou_name}}}),
    ]
    responses = _call_cli_batch(reqs, {2, 3, 4}, timeout_sec=15.0)
    tool_names = {2: "read_pou", 3: "search_code", 4: "get_diff"}
    parsed = {}
    for req_id, tool_name in tool_names.items():
        msg = responses.get(req_id)
        if not msg:
            raise AssertionError(f"Missing response for {tool_name} (id={req_id})")
        if msg.get("error"):
            raise AssertionError(f"{tool_name} returned JSON-RPC error: {msg['error']}")
        result = msg.get("result") or {}
        if result.get("isError"):
            raise AssertionError(f"{tool_name} returned tool error: {result}")
        text = result["content"][0]["text"]
        if token not in text:
            raise AssertionError(f"{tool_name} response did not contain live unsaved token {token!r}: {text[:300]}")
        parsed[tool_name] = json.loads(text)

    if parsed["search_code"].get("totalMatches", 0) < 1:
        raise AssertionError(f"search_code returned totalMatches < 1: {parsed['search_code']}")
    if parsed["get_diff"].get("modifiedCount", 0) < 1:
        raise AssertionError(f"get_diff returned modifiedCount < 1: {parsed['get_diff']}")
    print(f"Verified read_pou('{pou_name}'), search_code('{token}'), and get_diff('{pou_name}') all see the live unsaved IDE editor buffer.")


def normalize_unc_drive_path(path: str) -> str:
    if not path:
        return path
    if (path.startswith("\\\\") or path.startswith("//")) and os.path.isdir("Y:\\"):
        parts = path.lstrip("\\/").replace("/", "\\").split("\\", 2)
        if len(parts) == 3:
            candidate = "Y:\\" + parts[2]
            if os.path.exists(candidate):
                return candidate
    return path


def prepare_isolated_project_if_locked(project_path: str):
    project_path = normalize_unc_drive_path(project_path)
    if not project_path or not os.path.isfile(project_path):
        return None, project_path

    proj_dir = os.path.dirname(project_path)
    stem = os.path.splitext(os.path.basename(project_path))[0]
    lock_path = os.path.join(proj_dir, f"{stem}.~u")
    if not os.path.exists(lock_path):
        return None, project_path

    tmp_dir = tempfile.TemporaryDirectory(prefix="codesys_live_e2e_")
    copied_proj = os.path.join(tmp_dir.name, os.path.basename(project_path))
    shutil.copy2(project_path, copied_proj)
    for opt_file in glob.glob(os.path.join(proj_dir, f"{stem}*.opt")):
        try:
            shutil.copy2(opt_file, os.path.join(tmp_dir.name, os.path.basename(opt_file)))
        except OSError:
            pass
    print(f"Detected project lock {lock_path}; copied project and .opt layout to isolated dir: {copied_proj}")
    return tmp_dir, copied_proj


def launch_codesys_with_project(project_path: str):
    repo_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    script_path = normalize_vbox_drive_path(os.path.join(repo_root, "codesys_mcp_session.py"))
    project_path = normalize_vbox_drive_path(project_path)
    if not os.path.isfile(DEFAULT_CODESYS_EXE):
        raise FileNotFoundError(f"CODESYS executable not found: {DEFAULT_CODESYS_EXE}")
    if not os.path.isfile(script_path):
        raise FileNotFoundError(f"Bootstrap script not found: {script_path}")

    env = os.environ.copy()
    if project_path and os.path.exists(project_path):
        env["CODESYS_MCP_PROJECT"] = project_path

    # CODESYS CLI parser expects `--flag="value with spaces"`, not `"--flag=value with spaces"`.
    cmd_str = f'"{DEFAULT_CODESYS_EXE}" --profile="{DEFAULT_CODESYS_PROFILE}" --runscript="{script_path}"'
    print(f"Launching CODESYS IDE: {cmd_str}")
    proc = subprocess.Popen(cmd_str, shell=False, env=env)

    pipe_name = env.get("CODESYS_MCP_PIPE", "codesys_mcp_pipe")
    pipe_path = rf"\\.\pipe\{pipe_name}"
    mirror_dir = ""
    deadline = time.time() + 90.0
    while time.time() < deadline:
        if proc.poll() is not None:
            raise RuntimeError(f"CODESYS exited early with code {proc.returncode}")
        if kernel32.WaitNamedPipeW(pipe_path, 500):
            mirror_dir = discover_mirror_dir_via_cli()
            if mirror_dir and os.path.isdir(mirror_dir):
                break
        time.sleep(1.0)

    if not mirror_dir or not os.path.isdir(mirror_dir):
        proc.terminate()
        raise TimeoutError(f"Timed out waiting for CODESYS pipe {pipe_path} and mirrorDir.")

    hwnd = None
    title = ""
    while time.time() < deadline:
        if proc.poll() is not None:
            raise RuntimeError(f"CODESYS exited early with code {proc.returncode}")
        hwnd, title = find_codesys_hwnd()
        if hwnd and title and ".project" in title:
            break
        time.sleep(0.5)

    if not hwnd or not title or ".project" not in title:
        proc.terminate()
        raise TimeoutError(f"Timed out waiting for CODESYS IDE window with '.project' title (last={title!r}).")

    textarea_hwnds = []
    while time.time() < deadline:
        if proc.poll() is not None:
            raise RuntimeError(f"CODESYS exited early with code {proc.returncode}")
        textarea_hwnds = find_syned_textarea_hwnds(hwnd)
        if textarea_hwnds:
            break
        time.sleep(0.5)

    if not textarea_hwnds:
        proc.terminate()
        raise TimeoutError(f"Timed out waiting for visible SynEdControl.TextArea child HWND inside {title!r}.")

    print(f"CODESYS connected ({title}); mirror={mirror_dir}; textAreas={len(textarea_hwnds)}. Waiting 2s for initial mirror attachment...")
    time.sleep(2.0)
    return proc, mirror_dir


def scan_st_files_for_token(mirror_dir: str, token: str) -> str:
    for path in glob.glob(os.path.join(mirror_dir, "**", "*.st"), recursive=True):
        try:
            with open(path, "r", encoding="utf-8", errors="ignore") as f:
                if token in f.read():
                    return path
        except OSError:
            continue
    return ""


def main():
    parser = argparse.ArgumentParser(description="Live GUI E2E test for unsaved CODESYS ST editor sync.")
    parser.add_argument("--mirror-dir", default="", help="Path to <project>\\.cds_mcp\\src (auto-discovered via MCP if omitted).")
    parser.add_argument("--timeout", type=float, default=12.0, help="Seconds to wait for mirror sync.")
    parser.add_argument("--launch-if-needed", action="store_true", help="Launch CODESYS IDE automatically if not currently running.")
    parser.add_argument("--project", default=DEFAULT_PROJECT_PATH, help="Project path to open when --launch-if-needed is used.")
    parser.add_argument("--close-after", action="store_true", help="Close CODESYS if launched by this script.")
    parser.add_argument("--keep-open", action="store_true", help="Keep CODESYS open after test even if launched by this script.")
    parser.add_argument("--run-mcp-e2e", action="store_true", help="Run tests/test_codesys_e2e.py against the active CODESYS MCP pipe after live GUI verification.")
    args = parser.parse_args()

    launched_proc = None
    temp_dir_obj = None
    try:
        hwnd, title = find_codesys_hwnd()
        mirror_dir = args.mirror_dir
        if not mirror_dir and hwnd:
            mirror_dir = discover_mirror_dir_via_cli()

        if hwnd is None and args.launch_if_needed:
            default_pipe = os.environ.get("CODESYS_MCP_PIPE", "codesys_mcp_pipe")
            if default_pipe == "codesys_mcp_pipe" and kernel32.WaitNamedPipeW(rf"\\.\pipe\{default_pipe}", 200):
                iso_pipe = f"codesys_mcp_e2e_{os.getpid()}"
                os.environ["CODESYS_MCP_PIPE"] = iso_pipe
                print(f"Pipe \\\\.\\pipe\\{default_pipe} is active without a window on this desktop; using isolated pipe {iso_pipe}.")
            temp_dir_obj, target_project = prepare_isolated_project_if_locked(args.project)
            launched_proc, mirror_dir = launch_codesys_with_project(target_project)
        elif not mirror_dir or not os.path.isdir(mirror_dir):
            mirror_dir = discover_mirror_dir_via_cli()

        if not mirror_dir or not os.path.isdir(mirror_dir):
            print(f"SKIP/FAIL: Mirror directory not found or CODESYS not connected: {mirror_dir!r}", file=sys.stderr)
            sys.exit(2)

        hwnd, title = find_codesys_hwnd()
        if not hwnd:
            print("FAIL: Could not find visible CODESYS IDE window.", file=sys.stderr)
            sys.exit(2)

        print(f"Found CODESYS window: {title}")
        print(f"Monitoring mirror dir: {mirror_dir}")

        textarea_hwnds = find_syned_textarea_hwnds(hwnd)
        if not textarea_hwnds:
            print(f"FAIL: Could not locate any visible SynEdControl.TextArea child HWND inside {title!r}.", file=sys.stderr)
            sys.exit(2)

        target_hwnd = textarea_hwnds[0]
        print(f"Located {len(textarea_hwnds)} SynEdControl.TextArea HWNDs; targeting top pane HWND={target_hwnd}")

        send_vk_message(target_hwnd, VK_END)
        token = f" //LIVE_E2E_{int(time.time())}"
        print(f"Sending probe token via WM_CHAR to active editor (without leaving focus or Ctrl+S): {token}")
        send_char_sequence(target_hwnd, token)

        deadline = time.time() + args.timeout
        matched_file = ""
        matched_content = ""
        try:
            while time.time() < deadline:
                matched_file = scan_st_files_for_token(mirror_dir, token)
                if matched_file:
                    with open(matched_file, "r", encoding="utf-8", errors="ignore") as f:
                        matched_content = f.read()
                    break
                time.sleep(0.25)

            if matched_file:
                pou_name = os.path.splitext(os.path.basename(matched_file))[0]
                verify_live_mcp_tools_via_cli(pou_name, token)
        finally:
            # Undo the typed probe cleanly via WM_KEYDOWN(VK_BACK) while keeping focus unchanged
            send_vk_message(target_hwnd, VK_BACK, count=len(token))
            cleanup_deadline = time.time() + 3.0
            while time.time() < cleanup_deadline:
                if not scan_st_files_for_token(mirror_dir, token):
                    break
                time.sleep(0.25)

        if not matched_file:
            print(f"FAIL: Token {token} did not appear in any .st file under {mirror_dir} within {args.timeout}s.", file=sys.stderr)
            sys.exit(1)

        line_count = len(matched_content.splitlines())
        if line_count < 30:
            print(f"FAIL: {matched_file} has only {line_count} lines (< 30); possible truncated/wiped content!", file=sys.stderr)
            sys.exit(1)
        if not ("PROGRAM " in matched_content or "FUNCTION_BLOCK " in matched_content or "VAR" in matched_content):
            print(f"FAIL: {matched_file} is missing IEC structural keywords (PROGRAM / FUNCTION_BLOCK / VAR).", file=sys.stderr)
            sys.exit(1)

        print(f"PASS: Unsaved live edit synced to {matched_file} ({line_count} lines preserved) without focus loss or Ctrl+S.")

        if args.run_mcp_e2e:
            print("Running full MCP E2E suite (tests/test_codesys_e2e.py) against live pipe...")
            repo_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
            e2e_proc = subprocess.run(
                [sys.executable, "-m", "unittest", "tests/test_codesys_e2e.py", "-v"],
                cwd=repo_root,
                env=os.environ.copy(),
            )
            if e2e_proc.returncode != 0:
                print(f"FAIL: tests/test_codesys_e2e.py exited with code {e2e_proc.returncode}", file=sys.stderr)
                sys.exit(e2e_proc.returncode)
    finally:
        if launched_proc is not None and args.close_after and not args.keep_open:
            print("Closing CODESYS process launched by E2E test...")
            try:
                launched_proc.terminate()
                launched_proc.wait(timeout=10)
            except Exception:
                try:
                    launched_proc.kill()
                    launched_proc.wait(timeout=5)
                except Exception:
                    pass
        if temp_dir_obj is not None:
            try:
                temp_dir_obj.cleanup()
            except Exception:
                pass


if __name__ == "__main__":
    main()
