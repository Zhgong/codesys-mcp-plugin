import json
import subprocess

CLI_EXE = r"C:\Users\vboxuser\Desktop\Repos\codesys-plugin\bin\Release\CodesysMcpCli.exe"
PROJECT = r"C:\Users\vboxuser\Desktop\Repos\agy-codesys\projects\servo_press\ServoPress_VirtualAxis.project"

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
    print("[1] Opening project via MCP 'open_project'...")
    print(">> Open Project:", call_mcp("open_project", {"path": PROJECT}))

    print("\n[2] Checking project info:")
    print(">> Project Info:", call_mcp("get_project_info", {}))

    print("\n[3] Logging in to SoftPLC:")
    print(">> Login:", call_mcp("plc_login", {"mode": "try_online_change"}))

    print("\n[4] Online status:")
    print(">> Online Status:", call_mcp("get_online_status", {}))

    print("\n[5] Reading variables:")
    vars_to_read = [
        "MOTION_PRG.eDispState",
        "MOTION_PRG.lrTargetPosition",
        "MOTION_PRG.lrTargetPressForce"
    ]
    print(">> Read Variables:", call_mcp("read_variables", {"expressions": vars_to_read}))

    print("\n[6] Writing variable 'MOTION_PRG.lrTargetPosition' := '150.0':")
    print(">> Write Variable:", call_mcp("write_variable", {"name": "MOTION_PRG.lrTargetPosition", "value": "150.0"}))

    print("\n[7] Re-reading 'MOTION_PRG.lrTargetPosition':")
    print(">> Re-read:", call_mcp("read_variables", {"expressions": ["MOTION_PRG.lrTargetPosition"]}))
finally:
    mcp.stdin.close()
    try:
        mcp.wait(timeout=2)
    except:
        mcp.kill()
