import os
import sys
import json
import time
import subprocess

ROOT_DIR = r"C:\Users\vboxuser\Desktop\Repos\codesys-plugin"
CLI_EXE = os.path.join(ROOT_DIR, "bin", "Release", "CodesysMcpCli.exe")

def run_test():
    print("=" * 60)
    print("CODESYS MCP - SoftPLC Online Live Execution Flow")
    print("=" * 60)

    proc = subprocess.Popen(
        [CLI_EXE],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        bufsize=1
    )

    req_counter = 1

    def call_mcp(tool_name, arguments):
        nonlocal req_counter
        payload = {
            "jsonrpc": "2.0",
            "id": req_counter,
            "method": "tools/call",
            "params": {
                "name": tool_name,
                "arguments": arguments
            }
        }
        req_counter += 1
        proc.stdin.write(json.dumps(payload) + "\n")
        proc.stdin.flush()
        line = proc.stdout.readline()
        if not line:
            return None
        res = json.loads(line.strip())
        content = res.get("result", {}).get("content", [])
        if content and len(content) > 0:
            return content[0].get("text", "")
        return json.dumps(res, indent=2)

    try:
        # Step 1: Query online status
        print("\n[Step 1] Querying current online status via 'get_online_status'...")
        s1 = call_mcp("get_online_status", {})
        print(">> Status:", s1)

        time.sleep(1)

        # Step 2: Create boot application
        print("\n[Step 2] Testing 'create_boot_application'...")
        boot_res = call_mcp("create_boot_application", {})
        print(">> Boot Application Result:", boot_res)

        time.sleep(1)

        # Step 3: Attempt plc_login
        print("\n[Step 3] Attempting 'plc_login' (mode: try_online_change)...")
        login_res = call_mcp("plc_login", {"mode": "try_online_change"})
        print(">> Login Result:", login_res)

        time.sleep(1)

        # Step 4: Read variables
        print("\n[Step 4] Reading variables via 'read_variables'...")
        vars_to_read = [
            "MOTION_PRG.eDispState",
            "MOTION_PRG.lrTargetPosition",
            "MOTION_PRG.lrTargetPressForce"
        ]
        read_res = call_mcp("read_variables", {"expressions": vars_to_read})
        print(">> Read Variables Result:", read_res)

        time.sleep(1)

        # Step 5: Write variable test
        print("\n[Step 5] Writing variable 'MOTION_PRG.lrTargetPosition' := '150.0'...")
        write_res = call_mcp("write_variable", {
            "name": "MOTION_PRG.lrTargetPosition",
            "value": "150.0",
            "force": False
        })
        print(">> Write Variable Result:", write_res)

        time.sleep(1)

        # Step 6: Verify variable updated
        print("\n[Step 6] Re-reading 'MOTION_PRG.lrTargetPosition' to verify update...")
        re_read = call_mcp("read_variables", {"expressions": ["MOTION_PRG.lrTargetPosition"]})
        print(">> Re-read Result:", re_read)

        time.sleep(1)

        # Step 7: Check IDE Messages (script message)
        print("\n[Step 7] Checking Messages tab ('script message')...")
        msg_res = call_mcp("get_ide_messages", {})
        print(">> IDE Messages:", msg_res)

        print("\n" + "=" * 60)
        print("SoftPLC Online Live Execution Flow Completed!")
        print("=" * 60)

    finally:
        proc.stdin.close()
        try:
            proc.wait(timeout=2)
        except Exception:
            proc.kill()

if __name__ == "__main__":
    run_test()
