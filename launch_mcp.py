"""
Reliable launcher for CODESYS Native MCP Server using Pure Named Pipe (No HTTP).
"""
import subprocess
import os
import sys
import time
import ctypes

kernel32 = ctypes.windll.kernel32
kernel32.WaitNamedPipeW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32]
kernel32.WaitNamedPipeW.restype = ctypes.c_int

ROOT_DIR = os.path.dirname(os.path.abspath(__file__))
CODESYS_EXE = r"C:\Program Files\CODESYS 3.5.20.50\CODESYS\Common\CODESYS.exe"
PROFILE = "CODESYS V3.5 SP20 Patch 5"
SCRIPT_PATH = os.path.join(ROOT_DIR, "codesys_mcp_session.py")
PIPE_PATH = r"\\.\pipe\codesys_mcp_pipe"

cmd_str = f'"{CODESYS_EXE}" --profile="{PROFILE}" --runscript="{SCRIPT_PATH}" --noUI'

print("[LAUNCHER] Launching CODESYS with C# Pure Stdio/Pipe MCP session...")
print("[LAUNCHER] Command:", cmd_str)

proc = subprocess.Popen(cmd_str, shell=False)
print(f"[LAUNCHER] CODESYS started (PID: {proc.pid}). Waiting for pipe: {PIPE_PATH}...")

connected = False
for i in range(90):
    time.sleep(1)
    if proc.poll() is not None:
        print(f"[LAUNCHER] CODESYS process exited prematurely with code {proc.returncode}!", flush=True)
        break
    if kernel32.WaitNamedPipeW(PIPE_PATH, 500):
        print(f"[LAUNCHER] SUCCESS! Named pipe is ready after {i+1}s: {PIPE_PATH}", flush=True)
        connected = True
        break
    if (i + 1) % 5 == 0:
        print(f"[LAUNCHER] Still waiting for CODESYS initialization... ({i+1}s)", flush=True)

if not connected:
    print("[LAUNCHER] Timed out waiting for named pipe.")
    sys.exit(1)

print("[LAUNCHER] CODESYS Stdio/Pipe MCP Server is running! Press Ctrl+C to terminate.")
try:
    proc.wait()
except KeyboardInterrupt:
    print("[LAUNCHER] Stopping CODESYS...")
    proc.terminate()
