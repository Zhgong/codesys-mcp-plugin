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

    @staticmethod
    def _read_pipe_line(win32file_mod, handle):
        buf = b""
        while not buf.endswith(b"\n"):
            hr, chunk = win32file_mod.ReadFile(handle, 1)
            if hr != 0 or not chunk:
                return None
            buf += chunk
        return buf.decode("utf-8").strip()

    def test_stdin_eof_drains_inflight_responses(self):
        try:
            import win32pipe
            import win32file
        except ImportError:
            self.skipTest("pywin32 not available for mock named pipe test")

        mock_pipe = "test_cli_stdin_eof_drain_pipe"
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

                reqs = []
                for _ in range(3):
                    line = self._read_pipe_line(win32file, h)
                    if not line:
                        break
                    reqs.append(json.loads(line))

                # Deliberately sleep longer than immediate stdin EOF so CLI must drain in-flight IDs
                time.sleep(0.15)
                for req in reqs:
                    if "id" in req and req["id"] is not None:
                        resp = {"jsonrpc": "2.0", "id": req["id"], "result": {"echo": req.get("method")}}
                        win32file.WriteFile(h, (json.dumps(resp) + "\n").encode("utf-8"))
                win32file.FlushFileBuffers(h)

                time.sleep(0.05)
                win32pipe.DisconnectNamedPipe(h)
                win32file.CloseHandle(h)
            except Exception as e:
                server_errors.append(str(e))

        server_thread = threading.Thread(target=mock_server_loop, daemon=True)
        server_thread.start()
        time.sleep(0.15)

        payload = "\n".join([
            json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize"}),
            json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}),
            json.dumps({"jsonrpc": "2.0", "id": 2, "method": "tools/list"}),
        ]) + "\n"

        res = subprocess.run([CLI_EXE, "--pipe", mock_pipe], input=payload, text=True, capture_output=True, timeout=5)
        server_thread.join(timeout=2)

        self.assertEqual(res.returncode, 0, f"stderr={res.stderr}")
        self.assertEqual(len(server_errors), 0, f"Server errors: {server_errors}")

        msgs = [json.loads(l.strip()) for l in res.stdout.splitlines() if l.strip()]
        ids = [m.get("id") for m in msgs]
        self.assertEqual(ids, [1, 2])
        self.assertEqual(msgs[1]["result"]["echo"], "tools/list")

    def test_pipe_disconnect_returns_jsonrpc_error_and_auto_reconnects(self):
        try:
            import win32pipe
            import win32file
        except ImportError:
            self.skipTest("pywin32 not available for mock named pipe test")

        mock_pipe = "test_cli_reconnect_pipe"
        pipe_path = rf"\\.\pipe\{mock_pipe}"
        server_errors = []
        v1_closed = threading.Event()
        v2_listening = threading.Event()

        def server_v1():
            try:
                h = win32pipe.CreateNamedPipe(
                    pipe_path,
                    win32pipe.PIPE_ACCESS_DUPLEX,
                    win32pipe.PIPE_TYPE_BYTE | win32pipe.PIPE_READMODE_BYTE | win32pipe.PIPE_WAIT,
                    1, 65536, 65536, 0, None
                )
                win32pipe.ConnectNamedPipe(h, None)
                req = json.loads(self._read_pipe_line(win32file, h))
                resp = {"jsonrpc": "2.0", "id": req["id"], "result": {"v": 1}}
                win32file.WriteFile(h, (json.dumps(resp) + "\n").encode("utf-8"))
                win32file.FlushFileBuffers(h)
                win32pipe.DisconnectNamedPipe(h)
                win32file.CloseHandle(h)
            except Exception as e:
                server_errors.append(f"v1: {e}")
            finally:
                v1_closed.set()

        def server_v2():
            try:
                h = win32pipe.CreateNamedPipe(
                    pipe_path,
                    win32pipe.PIPE_ACCESS_DUPLEX,
                    win32pipe.PIPE_TYPE_BYTE | win32pipe.PIPE_READMODE_BYTE | win32pipe.PIPE_WAIT,
                    1, 65536, 65536, 0, None
                )
                v2_listening.set()
                win32pipe.ConnectNamedPipe(h, None)
                line = self._read_pipe_line(win32file, h)
                if line:
                    req = json.loads(line)
                    resp = {"jsonrpc": "2.0", "id": req["id"], "result": {"v": 2}}
                    win32file.WriteFile(h, (json.dumps(resp) + "\n").encode("utf-8"))
                    win32file.FlushFileBuffers(h)
                time.sleep(0.05)
                win32pipe.DisconnectNamedPipe(h)
                win32file.CloseHandle(h)
            except Exception as e:
                server_errors.append(f"v2: {e}")

        t1 = threading.Thread(target=server_v1, daemon=True)
        t1.start()
        time.sleep(0.15)

        proc = subprocess.Popen(
            [CLI_EXE, "--pipe", mock_pipe, "--reconnect-wait-ms", "250", "--heartbeat-ms", "0"],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True
        )
        t2 = None
        try:
            proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 1, "method": "ping"}) + "\n")
            proc.stdin.flush()
            r1 = self._read_line_with_timeout(proc.stdout, timeout=2.0)
            self.assertIsNotNone(r1)
            self.assertEqual(json.loads(r1)["result"]["v"], 1)

            self.assertTrue(v1_closed.wait(timeout=2.0))
            time.sleep(0.1)

            # While pipe is down, request should receive JSON-RPC -32002 without CLI exiting
            proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 2, "method": "ping"}) + "\n")
            proc.stdin.flush()
            r2 = self._read_line_with_timeout(proc.stdout, timeout=2.0)
            self.assertIsNotNone(r2, "Expected -32002 disconnect JSON-RPC error response")
            r2_obj = json.loads(r2)
            self.assertEqual(r2_obj.get("id"), 2)
            self.assertEqual(r2_obj.get("error", {}).get("code"), -32002)
            self.assertIsNone(proc.poll(), "CLI must stay alive during temporary pipe disconnect")

            # Bring up v2 and verify automatic reconnect
            t2 = threading.Thread(target=server_v2, daemon=True)
            t2.start()
            self.assertTrue(v2_listening.wait(timeout=2.0))
            time.sleep(0.35)

            proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 3, "method": "ping"}) + "\n")
            proc.stdin.flush()
            r3 = self._read_line_with_timeout(proc.stdout, timeout=3.0)
            self.assertIsNotNone(r3, "Expected response after auto-reconnect to v2")
            r3_obj = json.loads(r3)
            self.assertEqual(r3_obj.get("id"), 3)
            self.assertEqual(r3_obj.get("result", {}).get("v"), 2)
        finally:
            try:
                proc.stdin.close()
            except Exception:
                pass
            try:
                proc.wait(timeout=3)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait()
            if proc.stdout:
                proc.stdout.close()
            if proc.stderr:
                proc.stderr.close()
            t1.join(timeout=2)
            if t2 is not None:
                t2.join(timeout=2)

        self.assertEqual(len(server_errors), 0, f"Server errors: {server_errors}")

    def test_heartbeat_detects_unresponsive_pipe_and_reconnects(self):
        try:
            import win32pipe
            import win32file
        except ImportError:
            self.skipTest("pywin32 not available for mock named pipe test")

        mock_pipe = "test_cli_heartbeat_pipe"
        pipe_path = rf"\\.\pipe\{mock_pipe}"
        server_errors = []
        v1_ready = threading.Event()
        heartbeat_seen = threading.Event()
        stop_v1 = threading.Event()

        def server_v1():
            h = None
            try:
                h = win32pipe.CreateNamedPipe(
                    pipe_path,
                    win32pipe.PIPE_ACCESS_DUPLEX,
                    win32pipe.PIPE_TYPE_BYTE | win32pipe.PIPE_READMODE_BYTE | win32pipe.PIPE_WAIT,
                    2, 65536, 65536, 0, None
                )
                v1_ready.set()
                win32pipe.ConnectNamedPipe(h, None)

                req = json.loads(self._read_pipe_line(win32file, h))
                resp = {"jsonrpc": "2.0", "id": req["id"], "result": {"v": 1}}
                win32file.WriteFile(h, (json.dumps(resp) + "\n").encode("utf-8"))
                win32file.FlushFileBuffers(h)

                # Next message should be synthetic __cli_hb_* heartbeat; read it and intentionally stall without replying
                hb_line = self._read_pipe_line(win32file, h)
                if hb_line and "__cli_hb_" in hb_line:
                    heartbeat_seen.set()
                stop_v1.wait(timeout=4.0)
            except Exception:
                pass
            finally:
                if h is not None:
                    try:
                        win32pipe.DisconnectNamedPipe(h)
                    except Exception:
                        pass
                    try:
                        win32file.CloseHandle(h)
                    except Exception:
                        pass

        def server_v2():
            try:
                h = win32pipe.CreateNamedPipe(
                    pipe_path,
                    win32pipe.PIPE_ACCESS_DUPLEX,
                    win32pipe.PIPE_TYPE_BYTE | win32pipe.PIPE_READMODE_BYTE | win32pipe.PIPE_WAIT,
                    2, 65536, 65536, 0, None
                )
                win32pipe.ConnectNamedPipe(h, None)
                while True:
                    line = self._read_pipe_line(win32file, h)
                    if not line:
                        break
                    req = json.loads(line)
                    if req.get("method") == "ping" and str(req.get("id", "")).startswith("__cli_hb_"):
                        pong = {"jsonrpc": "2.0", "id": req["id"], "result": {}}
                        win32file.WriteFile(h, (json.dumps(pong) + "\n").encode("utf-8"))
                        win32file.FlushFileBuffers(h)
                        continue
                    resp = {"jsonrpc": "2.0", "id": req["id"], "result": {"v": 2}}
                    win32file.WriteFile(h, (json.dumps(resp) + "\n").encode("utf-8"))
                    win32file.FlushFileBuffers(h)
                    break
                time.sleep(0.05)
                win32pipe.DisconnectNamedPipe(h)
                win32file.CloseHandle(h)
            except Exception as e:
                server_errors.append(f"v2: {e}")

        t1 = threading.Thread(target=server_v1, daemon=True)
        t1.start()
        self.assertTrue(v1_ready.wait(timeout=2.0))
        t2 = threading.Thread(target=server_v2, daemon=True)
        t2.start()
        time.sleep(0.1)

        proc = subprocess.Popen(
            [CLI_EXE, "--pipe", mock_pipe, "--heartbeat-ms", "200", "--reconnect-wait-ms", "1500"],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True
        )
        try:
            proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 1, "method": "ping"}) + "\n")
            proc.stdin.flush()
            r1 = self._read_line_with_timeout(proc.stdout, timeout=2.0)
            self.assertIsNotNone(r1)
            self.assertNotIn("__cli_hb_", r1)
            self.assertEqual(json.loads(r1)["result"]["v"], 1)

            # Wait for idle heartbeat to hit stalled server v1 and fail over to v2
            self.assertTrue(heartbeat_seen.wait(timeout=2.0), "CLI did not send expected idle heartbeat probe")
            time.sleep(0.75)

            proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 2, "method": "ping"}) + "\n")
            proc.stdin.flush()
            r2 = self._read_line_with_timeout(proc.stdout, timeout=3.0)
            self.assertIsNotNone(r2, "Expected response from v2 after heartbeat-triggered reconnect")
            self.assertNotIn("__cli_hb_", r2)
            r2_obj = json.loads(r2)
            self.assertEqual(r2_obj.get("id"), 2)
            self.assertEqual(r2_obj.get("result", {}).get("v"), 2)
        finally:
            stop_v1.set()
            try:
                proc.stdin.close()
            except Exception:
                pass
            try:
                proc.wait(timeout=3)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait()
            if proc.stdout:
                proc.stdout.close()
            if proc.stderr:
                proc.stderr.close()
            t1.join(timeout=2)
            t2.join(timeout=2)

        self.assertEqual(len(server_errors), 0, f"Server errors: {server_errors}")

if __name__ == "__main__":
    unittest.main()
