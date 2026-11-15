import os
import sys
import time
import json
import subprocess

CLI_EXE = r"C:\Users\vboxuser\Desktop\Repos\codesys-plugin\bin\Release\CodesysMcpCli.exe"
CODESYS_EXE = r"C:\Program Files\CODESYS 3.5.20.50\CODESYS\Common\CODESYS.exe"
PROFILE = "CODESYS V3.5 SP20 Patch 5"
SCRIPT = r"C:\Users\vboxuser\Desktop\Repos\codesys-plugin\codesys_mcp_session.py"
PROJECT = r"C:\Users\vboxuser\Desktop\Repos\agy-codesys\projects\servo_press\ServoPress_VirtualAxis.project"

os.environ["CODESYS_MCP_PROJECT"] = PROJECT

cmd = f'"{CODESYS_EXE}" --profile="{PROFILE}" --runscript="{SCRIPT}" "{PROJECT}"'
print("Launching CODESYS...")
codesys_proc = subprocess.Popen(cmd, shell=True)

def is_pipe_ready():
    try:
        proc = subprocess.Popen(
            [CLI_EXE],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True
        )
        payload = {"jsonrpc": "2.0", "id": 1, "method": "ping"}
        stdout, _ = proc.communicate(json.dumps(payload) + "\n", timeout=2)
        return "result" in stdout
    except Exception:
        return False

print("Waiting for MCP server named pipe to be ready...")
for i in range(45):
    time.sleep(1)
    if is_pipe_ready():
        print(f"\n>>> MCP Pipe server is LIVE and responding after {i+1} seconds! <<<\n")
        break
    print(f"Waiting for pipe... ({i+1}/45)")
else:
    print("Timed out waiting for MCP pipe!")
    sys.exit(1)

# Now run the complete live test flow!
print("=" * 60)
print("RUNNING LIVE SOFTPLE ONLINE TEST FLOW")
print("=" * 60)

mcp = subprocess.Popen(
    [CLI_EXE],
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    stderr=subprocess.PIPE,
    text=True,
    bufsize=1
)

req_id = 1
def call_mcp(name, args):
    global req_id
    payload = {"jsonrpc": "2.0", "id": req_id, "method": "tools/call", "params": {"name": name, "arguments": args}}
    req_id += 1
    mcp.stdin.write(json.dumps(payload) + "\n")
    mcp.stdin.flush()
    line = mcp.stdout.readline()
    if not line:
        return "No response"
    res = json.loads(line.strip())
    content = res.get("result", {}).get("content", [])
    if content:
        return content[0].get("text", "")
    return json.dumps(res)

try:
    print("\n[Step 1] Querying project info:")
    print(">>", call_mcp("get_project_info", {}))

    print("\n[Step 2] Querying initial online status:")
    print(">>", call_mcp("get_online_status", {}))

    print("\n[Step 3] Logging in to SoftPLC (mode='try_online_change'):")
    print(">>", call_mcp("plc_login", {"mode": "try_online_change"}))

    print("\n[Step 4] Querying online status after login:")
    print(">>", call_mcp("get_online_status", {}))

    print("\n[Step 5] Reading live variables from MOTION_PRG:")
    vars_to_read = [
        "MOTION_PRG.eDispState",
        "MOTION_PRG.lrTargetPosition",
        "MOTION_PRG.lrTargetPressForce"
    ]
    print(">>", call_mcp("read_variables", {"expressions": vars_to_read}))

    print("\n[Step 6] Writing variable 'MOTION_PRG.lrTargetPosition' := '150.0':")
    print(">>", call_mcp("write_variable", {"name": "MOTION_PRG.lrTargetPosition", "value": "150.0", "force": False}))

    print("\n[Step 7] Re-reading 'MOTION_PRG.lrTargetPosition' to verify writeback:")
    print(">>", call_mcp("read_variables", {"expressions": ["MOTION_PRG.lrTargetPosition"]}))

    print("\n[Step 8] Reading IDE script messages:")
    print(">>", call_mcp("get_ide_messages", {}))

    print("\n" + "=" * 60)
    print("LIVE VERIFICATION COMPLETE!")
    print("=" * 60)
finally:
    mcp.stdin.close()
    try:
        mcp.wait(timeout=2)
    except:
        mcp.kill()
