import os
import sys
import json
import unittest
import subprocess

ROOT_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CLI_EXE = os.path.join(ROOT_DIR, "bin", "Release", "CodesysMcpCli.exe")
PIPE_PATH = r"\\.\pipe\codesys_mcp_pipe"

def is_codesys_pipe_active():
    try:
        import win32file
        h = win32file.CreateFile(
            PIPE_PATH,
            win32file.GENERIC_READ | win32file.GENERIC_WRITE,
            0,
            None,
            win32file.OPEN_EXISTING,
            0,
            None
        )
        win32file.CloseHandle(h)
        return True
    except Exception:
        return False

class TestCodesysLiveE2E(unittest.TestCase):

    @classmethod
    def setUpClass(cls):
        if not is_codesys_pipe_active():
            raise unittest.SkipTest("CODESYS IDE / MCP pipe is not currently running. Skipping live E2E tests.")

    def setUp(self):
        self.proc = subprocess.Popen(
            [CLI_EXE],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            bufsize=1
        )

    def tearDown(self):
        if self.proc and self.proc.poll() is None:
            self.proc.stdin.close()
            try:
                self.proc.wait(timeout=2)
            except subprocess.TimeoutExpired:
                self.proc.kill()

    def rpc(self, method, params=None, req_id=1):
        payload = {"jsonrpc": "2.0", "id": req_id, "method": method, "params": params or {}}
        self.proc.stdin.write(json.dumps(payload) + "\n")
        self.proc.stdin.flush()
        line = self.proc.stdout.readline()
        self.assertTrue(line, f"Empty response received for {method}")
        return json.loads(line.strip())

    def tool(self, name, args=None, req_id=1):
        resp = self.rpc("tools/call", {"name": name, "arguments": args or {}}, req_id=req_id)
        self.assertIn("result", resp, f"Tool call {name} failed: {resp}")
        result = resp["result"]
        self.assertFalse(result.get("isError", False), f"Tool {name} returned isError=True: {result}")
        content = result.get("content", [])
        self.assertTrue(len(content) > 0)
        return json.loads(content[0]["text"])

    def test_01_initialize_and_tools_list(self):
        init_resp = self.rpc("initialize", req_id=10)
        self.assertEqual(init_resp["result"]["protocolVersion"], "2024-11-05")

        list_resp = self.rpc("tools/list", req_id=11)
        tools = list_resp["result"]["tools"]
        tool_names = [t["name"] for t in tools]
        self.assertIn("read_pou", tool_names)
        self.assertIn("write_pou", tool_names)
        self.assertIn("compile_project", tool_names)

    def test_02_project_status_and_tree(self):
        info = self.tool("get_project_info", req_id=20)
        self.assertIn("isOpen", info)
        if not info["isOpen"]:
            self.skipTest("No project is currently open in CODESYS.")

        tree = self.tool("get_project_tree", {"full": False}, req_id=21)
        self.assertIn("project_name", tree)
        self.assertIn("nodes", tree)

    def test_03_pou_crud_and_compile(self):
        info = self.tool("get_project_info", req_id=30)
        if not info.get("isOpen", False):
            self.skipTest("No project is currently open in CODESYS.")

        test_pou = "__AutoUnitTestPRG"
        decl = "PROGRAM __AutoUnitTestPRG\nVAR\n    nStep : INT := 0;\nEND_VAR"
        impl = "nStep := nStep + 1;"

        # 1. Write POU
        write_res = self.tool("write_pou", {
            "name": test_pou,
            "declaration": decl,
            "implementation": impl,
            "type": "Program"
        }, req_id=31)
        self.assertTrue(write_res.get("success", False))

        # 2. Read POU
        read_res = self.tool("read_pou", {"name": test_pou}, req_id=32)
        self.assertIn("nStep : INT", read_res.get("declaration", ""))
        self.assertIn("nStep + 1", read_res.get("implementation", ""))

        # 3. Compile
        compile_res = self.tool("compile_project", {"clean": False, "rebuild": False}, req_id=33)
        self.assertIn("success", compile_res)

        # 4. Delete POU cleanup
        delete_res = self.tool("delete_pou", {"name": test_pou}, req_id=34)
        self.assertTrue(delete_res.get("deleted", False))

if __name__ == "__main__":
    unittest.main()
