import os
import sys
import json
import unittest
import subprocess

ROOT_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CLI_EXE = os.path.join(ROOT_DIR, "bin", "Release", "CodesysMcpCli.exe")

class TestMessageCategories(unittest.TestCase):
    def setUp(self):
        self.assertTrue(os.path.exists(CLI_EXE), f"CodesysMcpCli.exe not found at {CLI_EXE}")
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
            try:
                self.proc.stdin.close()
                self.proc.wait(timeout=2)
            except Exception:
                self.proc.kill()

    def rpc(self, method, params=None, req_id=1):
        payload = {"jsonrpc": "2.0", "id": req_id, "method": method, "params": params or {}}
        self.proc.stdin.write(json.dumps(payload) + "\n")
        self.proc.stdin.flush()
        line = self.proc.stdout.readline()
        self.assertTrue(line, f"Empty response received for {method}")
        return json.loads(line.strip())

    def test_message_categories_and_compiler_messages(self):
        # 1. Check tools/list has get_message_categories and schema
        resp = self.rpc("tools/list", {})
        self.assertIn("result", resp)
        tools = resp["result"].get("tools", [])
        tool_names = [t.get("name") for t in tools]
        self.assertIn("get_message_categories", tool_names, "get_message_categories must be listed in tools/list")

        compiler_tool = next((t for t in tools if t.get("name") == "get_compiler_messages"), None)
        self.assertIsNotNone(compiler_tool)
        props = compiler_tool.get("inputSchema", {}).get("properties", {})
        self.assertIn("category", props, "get_compiler_messages schema must include 'category'")
        self.assertIn("min_severity", props, "get_compiler_messages schema must include 'min_severity'")

        # 2. Call get_message_categories
        call_resp = self.rpc("tools/call", {"name": "get_message_categories", "arguments": {}}, req_id=2)
        self.assertIn("result", call_resp)
        result = call_resp["result"]
        self.assertFalse(result.get("isError", False), f"get_message_categories returned error: {result}")
        content = result.get("content", [])
        self.assertTrue(len(content) > 0)
        data = json.loads(content[0]["text"])
        self.assertIn("categories", data)
        categories = data["categories"]
        cat_names = [c.get("name") for c in categories]
        print(f"\n[Live CODESYS] Discovered message categories: {cat_names}")
        for c in categories:
            print(f"  Category: {c.get('name')} | messages: {c.get('totalMessages')} | errors: {c.get('errors')} | warnings: {c.get('warnings')}")

        # Check key categories are present
        self.assertTrue(any("Build" in name for name in cat_names), "Must discover 'Build' category")
        self.assertTrue(any("Script Messages" in name for name in cat_names), "Must discover 'Script Messages' category")

        # 3. Call get_compiler_messages with category='Build'
        build_resp = self.rpc("tools/call", {"name": "get_compiler_messages", "arguments": {"category": "Build"}}, req_id=3)
        self.assertIn("result", build_resp)
        build_result = build_resp["result"]
        self.assertFalse(build_result.get("isError", False))
        build_content = json.loads(build_result["content"][0]["text"])
        self.assertIn("messages", build_content)
        self.assertIn("errorCount", build_content)
        print(f"[Live CODESYS] Build category: errorCount={build_content['errorCount']}, warningCount={build_content['warningCount']}, total={len(build_content['messages'])}")

if __name__ == "__main__":
    unittest.main()
