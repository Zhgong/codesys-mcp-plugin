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
        self.assertIn("search_code", tool_names)
        self.assertIn("get_diff", tool_names)
        self.assertIn("rollback_pou", tool_names)

    def test_02_project_status_and_tree(self):
        info = self.tool("get_project_info", req_id=20)
        self.assertIn("isOpen", info)
        if not info["isOpen"]:
            self.skipTest("No project is currently open in CODESYS.")

        tree = self.tool("get_project_tree", {"full": False}, req_id=21)
        self.assertTrue(isinstance(tree, list))
        self.assertGreater(len(tree), 0)

    def test_03_pou_crud_and_compile(self):
        info = self.tool("get_project_info", req_id=30)
        if not info.get("isOpen", False):
            self.skipTest("No project is currently open in CODESYS.")

        test_pou = "AutoUnitTestPRG"
        decl = "PROGRAM AutoUnitTestPRG\nVAR\n    nStep : INT := 0;\nEND_VAR"
        impl = "nStep := nStep + 1;"

        # 1. Write POU
        write_res = self.tool("write_pou", {
            "name": test_pou,
            "declaration": decl,
            "implementation": impl,
            "type": "Program"
        }, req_id=31)
        self.assertIn("declaration", write_res)

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

    def test_04_search_code(self):
        info = self.tool("get_project_info", req_id=40)
        if not info.get("isOpen", False):
            self.skipTest("No project is currently open in CODESYS.")

        search_res = self.tool("search_code", {"query": "PROGRAM", "is_regex": False}, req_id=41)
        self.assertIn("matches", search_res)
        self.assertIn("totalMatches", search_res)

    def test_05_diff_and_rollback(self):
        info = self.tool("get_project_info", req_id=50)
        if not info.get("isOpen", False):
            self.skipTest("No project is currently open in CODESYS.")

        test_pou = "DiffRollbackTestPRG"
        decl = "PROGRAM DiffRollbackTestPRG\nVAR\n    bFlag : BOOL;\nEND_VAR"
        impl = "bFlag := TRUE;"

        # 1. Create POU
        self.tool("write_pou", {
            "name": test_pou,
            "declaration": decl,
            "implementation": impl,
            "type": "Program"
        }, req_id=51)

        # 2. Check get_diff
        diff_res = self.tool("get_diff", {"name": test_pou}, req_id=52)
        self.assertGreaterEqual(diff_res.get("modifiedCount", 0), 1)
        self.assertIn(test_pou, diff_res.get("fullUnifiedDiff", ""))

        # 3. Rollback newly created POU (should delete it)
        rollback_res = self.tool("rollback_pou", {"name": test_pou}, req_id=53)
        self.assertTrue(rollback_res.get("success", False))
        self.assertEqual("deleted", rollback_res.get("action"))

if __name__ == "__main__":
    unittest.main()
