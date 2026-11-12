import os
import sys
import json
import time
import unittest
import threading
import subprocess

ROOT_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CLI_EXE = os.path.join(ROOT_DIR, "bin", "Release", "CodesysMcpCli.exe")

class TestCliProtocol(unittest.TestCase):

    def setUp(self):
        self.assertTrue(os.path.exists(CLI_EXE), f"CodesysMcpCli.exe not found at {CLI_EXE}")

    def test_version_flag_long(self):
        with subprocess.Popen([CLI_EXE, "--version"], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True) as p:
            stdout, _ = p.communicate()
            self.assertEqual(p.returncode, 0)
            self.assertIn("CodesysMcpCli v", stdout)

    def test_version_flag_short(self):
        with subprocess.Popen([CLI_EXE, "-v"], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True) as p:
            stdout, _ = p.communicate()
            self.assertEqual(p.returncode, 0)
            self.assertIn("CodesysMcpCli v", stdout)

    def test_missing_pipe_exits_with_error(self):
        pipe_name = "test_non_existent_pipe_12345"
        with subprocess.Popen([CLI_EXE, "--pipe", pipe_name, "--attempts", "1"], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True) as p:
            _, stderr = p.communicate()
            self.assertEqual(p.returncode, 1)
            self.assertIn("Could not connect to CODESYS pipe", stderr)

    def test_mock_named_pipe_roundtrip(self):
        try:
            import win32pipe
            import win32file
        except ImportError:
            self.skipTest("pywin32 not available for mock named pipe test")

        mock_pipe = "test_cli_mock_roundtrip_pipe"
        pipe_path = rf"\\.\pipe\{mock_pipe}"

        server_errors = []

        def mock_server_loop():
            try:
                h = win32pipe.CreateNamedPipe(
                    pipe_path,
                    win32pipe.PIPE_ACCESS_DUPLEX,
                    win32pipe.PIPE_TYPE_BYTE | win32pipe.PIPE_READMODE_BYTE | win32pipe.PIPE_WAIT,
                    1, 65536, 65536, 0, None
                )
                win32pipe.ConnectNamedPipe(h, None)

                # Read line byte by byte
                buf = b""
                while not buf.endswith(b"\n"):
                    hr, chunk = win32file.ReadFile(h, 1)
                    if hr != 0 or not chunk:
                        break
                    buf += chunk

                req_str = buf.decode("utf-8").strip()
                req_json = json.loads(req_str)

                # Reply with ping response
                resp = {
                    "jsonrpc": "2.0",
                    "id": req_json.get("id"),
                    "result": {"status": "ok", "mockEcho": req_json.get("method")}
                }
                resp_bytes = (json.dumps(resp) + "\n").encode("utf-8")
                win32file.WriteFile(h, resp_bytes)
                win32file.FlushFileBuffers(h)

                time.sleep(0.1)
                win32pipe.DisconnectNamedPipe(h)
                win32file.CloseHandle(h)
            except Exception as e:
                server_errors.append(str(e))

        server_thread = threading.Thread(target=mock_server_loop)
        server_thread.daemon = True
        server_thread.start()
        time.sleep(0.2)

        proc = subprocess.Popen(
            [CLI_EXE, "--pipe", mock_pipe],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True
        )

        ping_req = json.dumps({"jsonrpc": "2.0", "id": 99, "method": "ping"})
        proc.stdin.write(ping_req + "\n")
        proc.stdin.flush()

        resp_line = proc.stdout.readline()
        self.assertTrue(resp_line, "Expected response from CLI stdout")

        resp_obj = json.loads(resp_line.strip())
        self.assertEqual(resp_obj.get("id"), 99)
        self.assertEqual(resp_obj.get("result", {}).get("mockEcho"), "ping")

        proc.stdin.close()
        if proc.stdout:
            proc.stdout.close()
        if proc.stderr:
            proc.stderr.close()
        proc.wait(timeout=3)
        server_thread.join(timeout=3)

        self.assertEqual(proc.returncode, 0)
        self.assertEqual(len(server_errors), 0, f"Server encountered errors: {server_errors}")

    def _read_line_with_timeout(self, stream, timeout=2.0):
        result = [None]
        def _reader():
            result[0] = stream.readline()
        t = threading.Thread(target=_reader, daemon=True)
        t.start()
        t.join(timeout)
        if t.is_alive():
            return None
        return result[0]

    def test_notification_and_params_with_id_do_not_deadlock(self):
        try:
            import win32pipe
            import win32file
        except ImportError:
            self.skipTest("pywin32 not available for mock named pipe test")

        mock_pipe = "test_cli_notifications_pipe"
        pipe_path = rf"\\.\pipe\{mock_pipe}"
        server_errors = []
        received_methods = []

        def read_pipe_line(h):
            buf = b""
            while not buf.endswith(b"\n"):
                hr, chunk = win32file.ReadFile(h, 1)
                if hr != 0 or not chunk:
                    return None
                buf += chunk
            return buf.decode("utf-8").strip()

        def mock_server_loop():
            try:
                h = win32pipe.CreateNamedPipe(
                    pipe_path,
                    win32pipe.PIPE_ACCESS_DUPLEX,
                    win32pipe.PIPE_TYPE_BYTE | win32pipe.PIPE_READMODE_BYTE | win32pipe.PIPE_WAIT,
                    1, 65536, 65536, 0, None
                )
                win32pipe.ConnectNamedPipe(h, None)

                while True:
                    line = read_pipe_line(h)
                    if not line:
                        break
                    req = json.loads(line)
                    method = req.get("method")
                    received_methods.append(method)
                    if "id" in req and req["id"] is not None:
                        resp = {
                            "jsonrpc": "2.0",
                            "id": req["id"],
                            "result": {"echo": method}
                        }
                        win32file.WriteFile(h, (json.dumps(resp) + "\n").encode("utf-8"))
                        win32file.FlushFileBuffers(h)
                        if method == "tools/list":
                            break

                time.sleep(0.1)
                win32pipe.DisconnectNamedPipe(h)
                win32file.CloseHandle(h)
            except Exception as e:
                server_errors.append(str(e))

        server_thread = threading.Thread(target=mock_server_loop, daemon=True)
        server_thread.start()
        time.sleep(0.2)

        proc = subprocess.Popen(
            [CLI_EXE, "--pipe", mock_pipe],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True
        )
        try:
            # 1. initialize (request with id)
            proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize"}) + "\n")
            proc.stdin.flush()
            init_line = self._read_line_with_timeout(proc.stdout, timeout=2.0)
            self.assertIsNotNone(init_line, "Timed out waiting for initialize response")
            self.assertEqual(json.loads(init_line)["id"], 1)

            # 2. notifications/initialized (no id -> no server response)
            proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
            # 3. notifications/cancelled with "id" inside params (no top-level id -> no server response)
            proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/cancelled", "params": {"id": "req-99"}}) + "\n")
            # 4. tools/list (request with id=2)
            proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 2, "method": "tools/list"}) + "\n")
            proc.stdin.flush()

            tools_line = self._read_line_with_timeout(proc.stdout, timeout=2.0)
            self.assertIsNotNone(tools_line, "Timed out waiting for tools/list response (CLI deadlocked on notification)")
            tools_resp = json.loads(tools_line)
            self.assertEqual(tools_resp.get("id"), 2)
            self.assertEqual(tools_resp.get("result", {}).get("echo"), "tools/list")
        finally:
            try:
                proc.stdin.close()
            except Exception:
                pass
            try:
                proc.wait(timeout=2)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait()
            if proc.stdout:
                proc.stdout.close()
            if proc.stderr:
                proc.stderr.close()
            server_thread.join(timeout=2)

        self.assertEqual(received_methods, ["initialize", "notifications/initialized", "notifications/cancelled", "tools/list"])
        self.assertEqual(len(server_errors), 0, f"Server encountered errors: {server_errors}")

    def test_server_initiated_message_forwarded_full_duplex(self):
        try:
            import win32pipe
            import win32file
        except ImportError:
            self.skipTest("pywin32 not available for mock named pipe test")

        mock_pipe = "test_cli_full_duplex_pipe"
        pipe_path = rf"\\.\pipe\{mock_pipe}"
        server_errors = []

        def mock_server_loop():
            try:
                h = win32pipe.CreateNamedPipe(
                    pipe_path,
                    win32pipe.PIPE_ACCESS_DUPLEX,
                    win32pipe.PIPE_TYPE_BYTE | win32pipe.PIPE_READMODE_BYTE | win32pipe.PIPE_WAIT,
                    1, 65536, 65536, 0, None
                )
                win32pipe.ConnectNamedPipe(h, None)

                # Server proactively pushes a notification without any client request
                notif = {"jsonrpc": "2.0", "method": "notifications/tools/list_changed"}
                win32file.WriteFile(h, (json.dumps(notif) + "\n").encode("utf-8"))
                win32file.FlushFileBuffers(h)

                time.sleep(0.2)
                win32pipe.DisconnectNamedPipe(h)
                win32file.CloseHandle(h)
            except Exception as e:
                server_errors.append(str(e))

        server_thread = threading.Thread(target=mock_server_loop, daemon=True)
        server_thread.start()
        time.sleep(0.2)

        proc = subprocess.Popen(
            [CLI_EXE, "--pipe", mock_pipe],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True
        )
        try:
            pushed_line = self._read_line_with_timeout(proc.stdout, timeout=2.0)
            self.assertIsNotNone(pushed_line, "Timed out waiting for server-initiated notification")
            pushed_obj = json.loads(pushed_line)
            self.assertEqual(pushed_obj.get("method"), "notifications/tools/list_changed")
        finally:
            try:
                proc.stdin.close()
            except Exception:
                pass
            try:
                proc.wait(timeout=2)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait()
            if proc.stdout:
                proc.stdout.close()
            if proc.stderr:
                proc.stderr.close()
            server_thread.join(timeout=2)

        self.assertEqual(len(server_errors), 0, f"Server encountered errors: {server_errors}")

if __name__ == "__main__":
    unittest.main()
