"""
CODESYS Native MCP Session Bootstrap (IronPython 2.7)
Supports both Headless (--noUI) and Interactive CODESYS IDE modes.
"""
import clr
import os
import sys
import time
import traceback
import warnings

warnings.filterwarnings("ignore")

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
LOG_FILE = os.path.join(SCRIPT_DIR, "session_debug.log")

def log(msg):
    ts = time.strftime("%Y-%m-%d %H:%M:%S")
    line = "[%s] %s" % (ts, msg)
    print(line)
    try:
        with open(LOG_FILE, "a") as f:
            f.write(line + "\n")
    except:
        pass

try:
    log("Bootstrap starting...")
    DLL_PATH = os.path.join(SCRIPT_DIR, "bin", "Release", "CodesysMcp.dll")
    if not os.path.exists(DLL_PATH):
        DLL_PATH = os.path.join(SCRIPT_DIR, "CodesysMcp", "bin", "Release", "CodesysMcp.dll")

    if not os.path.exists(DLL_PATH):
        log("Error: DLL not found at " + DLL_PATH)
        sys.exit(1)

    log("Loading CodesysMcp.dll from: " + DLL_PATH)
    clr.AddReferenceToFileAndPath(DLL_PATH)

    import CodesysMcp

    pipe_name = os.environ.get("CODESYS_MCP_PIPE", "codesys_mcp_pipe")

    log("Starting C# Stdio/Pipe MCP Server on pipe: %s..." % pipe_name)
    CodesysMcp.ServerBootstrap.Start(projects, system, pipe_name)
    log("C# Native Pipe MCP Server is live!")

    # Check if UI is present (Interactive IDE mode vs Headless --noUI mode)
    ui_present = False
    try:
        ui_present = bool(system.ui_present)
    except:
        pass

    log("system.ui_present = %s" % ui_present)

    # Keep the session alive with main-thread pump if running headless (--noUI),
    # or if explicitly requested via CODESYS_MCP_BLOCKING=1
    if not ui_present or os.environ.get("CODESYS_MCP_BLOCKING", "0") == "1":
        log("Entering headless keep-alive loop...")
        while True:
            try:
                system.delay(100)
            except:
                time.sleep(0.1)
    else:
        log("Interactive IDE mode detected. Background Named Pipe server is active!")
        try:
            system.write_message(
                Severity.Information,
                "[CODESYS MCP] Native Stdio/Pipe MCP Server started on \\\\.\\pipe\\%s" % pipe_name
            )
        except:
            pass
except Exception as e:
    log("FATAL ERROR: " + str(e))
    log(traceback.format_exc())
    raise

