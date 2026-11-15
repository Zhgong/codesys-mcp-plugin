import os
import sys
import json
import time
import tempfile
import unittest
import subprocess

ROOT_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CLI_EXE = os.path.join(ROOT_DIR, "bin", "Release", "CodesysMcpCli.exe")
PIPE_NAME = os.environ.get("CODESYS_MCP_PIPE", "codesys_mcp_pipe")
PIPE_PATH = rf"\\.\pipe\{PIPE_NAME}"

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

    def test_01b_notifications_and_ping_without_deadlock(self):
        init_resp = self.rpc("initialize", req_id=12)
        self.assertEqual(init_resp["id"], 12)

        # Send JSON-RPC 2.0 notifications (no id -> server must not reply, CLI must not deadlock)
        notifications = [
            {"jsonrpc": "2.0", "method": "notifications/initialized"},
            {"jsonrpc": "2.0", "method": "notifications/cancelled", "params": {"id": "req-99"}},
            {"jsonrpc": "2.0", "method": "custom/smokeNotification"},
        ]
        for notif in notifications:
            self.proc.stdin.write(json.dumps(notif) + "\n")
        self.proc.stdin.flush()

        list_resp = self.rpc("tools/list", req_id=13)
        self.assertEqual(list_resp["id"], 13)
        self.assertIn("tools", list_resp["result"])

        ping_resp = self.rpc("ping", req_id=14)
        self.assertEqual(ping_resp["id"], 14)
        self.assertEqual(ping_resp["result"], {})

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

    def test_06_incremental_source_mirror_sync(self):
        info = self.tool("get_project_info", req_id=60)
        if not info.get("isOpen", False):
            self.skipTest("No project is currently open in CODESYS.")

        test_pou = "MirrorE2ETestPRG"
        with tempfile.TemporaryDirectory(prefix="codesys_mirror_e2e_") as mirror_dir:
            try:
                # 1. Dynamic binding + initial sync without restarting CODESYS.exe
                sync1 = self.tool("sync_sources_to_disk", {
                    "mirror_dir": mirror_dir,
                    "persist_for_session": True
                }, req_id=61)
                self.assertGreater(sync1.get("created", 0), 0)
                self.assertEqual(sync1.get("updated", -1), 0)

                st_files = []
                for root_dir, _, files in os.walk(mirror_dir):
                    for f in files:
                        if f.endswith(".st"):
                            st_files.append(os.path.join(root_dir, f))
                self.assertGreater(len(st_files), 0)

                sample_file = st_files[0]
                with open(sample_file, "r", encoding="utf-8") as fh:
                    sample_text = fh.read()
                self.assertIn("(*\n---\nname:", sample_text)
                self.assertIn("path:", sample_text)
                sample_mtime_ns = os.stat(sample_file).st_mtime_ns

                # 2. Second sync using persisted session binding -> Content-Diff Guard skips all writes
                sync2 = self.tool("sync_sources_to_disk", {}, req_id=62)
                self.assertEqual(sync2.get("created", -1), 0)
                self.assertEqual(sync2.get("updated", -1), 0)
                self.assertEqual(sync2.get("deleted", -1), 0)
                self.assertGreater(sync2.get("unchanged", 0), 0)
                self.assertEqual(sample_mtime_ns, os.stat(sample_file).st_mtime_ns)

                # 3. Write-through O(1) create via write_pou
                decl = "PROGRAM MirrorE2ETestPRG\nVAR\n    nVal : INT := 1;\nEND_VAR"
                self.tool("write_pou", {
                    "name": test_pou,
                    "declaration": decl,
                    "implementation": "nVal := nVal + 1;",
                    "type": "Program"
                }, req_id=63)

                mirrored_test_file = None
                for root_dir, _, files in os.walk(mirror_dir):
                    if f"{test_pou}.st" in files:
                        mirrored_test_file = os.path.join(root_dir, f"{test_pou}.st")
                        break
                self.assertIsNotNone(mirrored_test_file, "write_pou should auto-refresh MirrorE2ETestPRG.st")
                with open(mirrored_test_file, "r", encoding="utf-8") as fh:
                    content1 = fh.read()
                self.assertIn("nVal := nVal + 1;", content1)
                self.assertEqual(sample_mtime_ns, os.stat(sample_file).st_mtime_ns)

                # 4. Write-through O(1) update via write_pou
                self.tool("write_pou", {
                    "name": test_pou,
                    "declaration": decl,
                    "implementation": "nVal := nVal + 42;",
                    "type": "Program"
                }, req_id=64)
                with open(mirrored_test_file, "r", encoding="utf-8") as fh:
                    content2 = fh.read()
                self.assertIn("nVal := nVal + 42;", content2)

                # 5. Write-through delete via delete_pou
                del_res = self.tool("delete_pou", {"name": test_pou}, req_id=65)
                self.assertTrue(del_res.get("deleted", False))
                self.assertFalse(os.path.exists(mirrored_test_file))
            finally:
                try:
                    self.tool("delete_pou", {"name": test_pou}, req_id=66)
                except Exception:
                    pass
                try:
                    self.tool("sync_sources_to_disk", {"disable": True}, req_id=67)
                except Exception:
                    pass

    def test_07_one_way_mirror_and_disabled_disk_to_project(self):
        info = self.tool("get_project_info", req_id=70)
        if not info.get("isOpen", False):
            self.skipTest("No project is currently open in CODESYS.")

        # 1. Verify default .cds_mcp/src and .git were auto-created on project open
        proj_path = info.get("path", "")
        if proj_path:
            default_mirror = os.path.join(os.path.dirname(proj_path), ".cds_mcp", "src")
            self.assertTrue(os.path.isdir(default_mirror), f"Default mirror {default_mirror} should exist")
            self.assertTrue(os.path.isdir(os.path.join(default_mirror, ".git")), f"Default git repo {default_mirror}/.git should exist")

        # 2. Verify sync_disk_to_project is disabled
        imp = self.tool("sync_disk_to_project", {}, req_id=71)
        self.assertTrue(imp.get("disabled", False), "sync_disk_to_project should report disabled=True")

        # 3. Verify external .st edit does NOT overwrite CODESYS (one-way only)
        test_pou = "OneWayMirrorE2EPRG"
        with tempfile.TemporaryDirectory(prefix="codesys_oneway_e2e_") as mirror_dir:
            try:
                decl = "PROGRAM OneWayMirrorE2EPRG\nVAR\n    nVal : INT := 0;\nEND_VAR"
                self.tool("write_pou", {
                    "name": test_pou,
                    "declaration": decl,
                    "implementation": "nVal := 1;",
                    "type": "Program"
                }, req_id=72)

                self.tool("sync_sources_to_disk", {
                    "mirror_dir": mirror_dir,
                    "persist_for_session": True,
                    "root_node": "Application",
                    "watch": True
                }, req_id=73)

                mirrored_file = None
                for root_dir, _, files in os.walk(mirror_dir):
                    if f"{test_pou}.st" in files:
                        mirrored_file = os.path.join(root_dir, f"{test_pou}.st")
                        break
                self.assertIsNotNone(mirrored_file)

                with open(mirrored_file, "r", encoding="utf-8") as fh:
                    text = fh.read()
                with open(mirrored_file, "w", encoding="utf-8", newline="\n") as fh:
                    fh.write(text.replace("nVal := 1;", "nVal := 999;"))

                time.sleep(1.0)
                read_res = self.tool("read_pou", {"name": test_pou}, req_id=74)
                self.assertIn("nVal := 1;", read_res.get("implementation", ""))
                self.assertNotIn("nVal := 999;", read_res.get("implementation", ""))
            finally:
                try:
                    self.tool("sync_sources_to_disk", {"disable": True}, req_id=75)
                except Exception:
                    pass
                try:
                    self.tool("delete_pou", {"name": test_pou}, req_id=76)
                except Exception:
                    pass

if __name__ == "__main__":
    unittest.main()
