"""
Demonstrates interacting with CodesysMcpCli.exe via stdin / stdout pipes
exactly as Claude Desktop or Cursor does.
"""
import subprocess
import json
import os
import sys

ROOT_DIR = os.path.dirname(os.path.abspath(__file__))
CLI_PATH = os.path.join(ROOT_DIR, "bin", "Release", "CodesysMcpCli.exe")
if not os.path.exists(CLI_PATH):
    CLI_PATH = os.path.join(ROOT_DIR, "CodesysMcp", "bin", "Release", "CodesysMcpCli.exe")

PROJECT_PATH = os.path.join(os.path.dirname(ROOT_DIR), "codesys-api", "Verify002.project")

def run_cli_session():
    print(f"[TEST-CLI] Spawning: {CLI_PATH}")
    proc = subprocess.Popen(
        [CLI_PATH],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        bufsize=1
    )

    def send_rpc(method, params=None, req_id=1):
        payload = {
            "jsonrpc": "2.0",
            "id": req_id,
            "method": method,
            "params": params or {}
        }
        line = json.dumps(payload)
        print(f"\n---> [STDIN] Sending: {method}")
        proc.stdin.write(line + "\n")
        proc.stdin.flush()

        resp_line = proc.stdout.readline().strip()
        print(f"<--- [STDOUT] Response:")
        try:
            resp_obj = json.loads(resp_line)
            print(json.dumps(resp_obj, indent=2, ensure_ascii=False))
            return resp_obj
        except Exception:
            print(resp_line)
            return None

    # 1. Initialize
    send_rpc("initialize", req_id=1)

    # 2. List tools
    send_rpc("tools/list", req_id=2)

    # 3. Check current project status
    send_rpc("tools/call", {"name": "get_project_info", "arguments": {}}, req_id=3)

    # 4. Open Verify002.project if found
    if os.path.exists(PROJECT_PATH):
        print(f"\n[TEST-CLI] Opening existing project: {PROJECT_PATH}")
        send_rpc("tools/call", {"name": "open_project", "arguments": {"path": PROJECT_PATH}}, req_id=4)
        
        # 5. Get project tree
        send_rpc("tools/call", {"name": "get_project_tree", "arguments": {"full": False}}, req_id=5)

        # 6. Read PLC_PRG
        send_rpc("tools/call", {"name": "read_pou", "arguments": {"name": "PLC_PRG"}}, req_id=6)

        # 7. Write a new POU called "McpDemoPRG"
        demo_decl = "PROGRAM McpDemoPRG\nVAR\n    nCounter : DINT := 0;\n    bTrigger : BOOL := TRUE;\nEND_VAR"
        demo_impl = "IF bTrigger THEN\n    nCounter := nCounter + 1;\nEND_IF;"
        send_rpc("tools/call", {
            "name": "write_pou",
            "arguments": {
                "name": "McpDemoPRG",
                "declaration": demo_decl,
                "implementation": demo_impl,
                "type": "Program"
            }
        }, req_id=7)

        # 8. Compile project
        send_rpc("tools/call", {"name": "compile_project", "arguments": {"clean": False, "rebuild": False}}, req_id=8)
    else:
        print(f"[TEST-CLI] Sample project not found at: {PROJECT_PATH}")

    # Terminate CLI
    proc.stdin.close()
    proc.terminate()
    print("\n[TEST-CLI] All Stdio work completed successfully!")

if __name__ == "__main__":
    run_cli_session()
