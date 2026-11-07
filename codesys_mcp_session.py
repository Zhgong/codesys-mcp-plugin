"""
CODESYS Native MCP Session Bootstrap (IronPython 2.7)
One-shot (<50ms) bootstrap loader that starts the C# NamedPipe MCP Server
and exits immediately in GUI mode so the CODESYS IDE remains 100% interactive.
"""
import clr
import os
import shutil
import sys
import time
import traceback
import warnings

warnings.filterwarnings("ignore")

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))

# Standard unified log file: ~/.codesys-mcp-plugin/session_debug.log (or overridden by CODESYS_MCP_LOG_FILE)
USER_HOME = os.path.expanduser("~")
DEFAULT_LOG_DIR = os.path.join(USER_HOME, ".codesys-mcp-plugin")
if not os.path.exists(DEFAULT_LOG_DIR):
    try:
        os.makedirs(DEFAULT_LOG_DIR)
    except:
        pass

LOG_FILE = os.environ.get("CODESYS_MCP_LOG_FILE")
if not LOG_FILE:
    LOG_FILE = os.path.join(DEFAULT_LOG_DIR, "session_debug.log")
os.environ["CODESYS_MCP_LOG_FILE"] = LOG_FILE

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

    # Load from a shadow copy so bin/Release/CodesysMcp.dll is NEVER locked on disk by CODESYS.exe
    SHADOW_DIR = os.path.join(os.path.dirname(DLL_PATH), "shadow")
    if not os.path.exists(SHADOW_DIR):
        try:
            os.makedirs(SHADOW_DIR)
        except:
            pass

    SHADOW_DLL = os.path.join(SHADOW_DIR, "CodesysMcp_%d.dll" % int(time.time() * 1000))
    try:
        shutil.copyfile(DLL_PATH, SHADOW_DLL)
        LOAD_PATH = SHADOW_DLL
    except Exception:
        LOAD_PATH = DLL_PATH

    log("Loading CodesysMcp.dll from shadow copy: " + LOAD_PATH)
    clr.AddReferenceToFileAndPath(LOAD_PATH)

    pipe_name = os.environ.get("CODESYS_MCP_PIPE", "codesys_mcp_pipe")

    online_obj = None
    try:
        online_obj = online
    except NameError:
        pass

    import System
    import System.Reflection
    asm = System.Reflection.Assembly.LoadFrom(LOAD_PATH)
    server_version = asm.GetName().Version.ToString(3)
    tool_count = 36
    try:
        mcp_server_type = asm.GetType("CodesysMcp.McpServer")
        if mcp_server_type:
            tool_count = int(mcp_server_type.GetProperty("RegisteredToolCount").GetValue(None, None))
    except:
        pass

    log("Starting C# Stdio/Pipe MCP Server v%s on pipe: %s via direct assembly load..." % (server_version, pipe_name))
    bootstrap_type = asm.GetType("CodesysMcp.ServerBootstrap")
    param_types = System.Array[System.Type]([System.Object, System.Object, System.Object, System.String])
    start_method = bootstrap_type.GetMethod("Start", param_types)
    param_values = System.Array[System.Object]([projects, system, online_obj, pipe_name])
    start_method.Invoke(None, param_values)
    log("C# Native Pipe MCP Server v%s is live!" % server_version)

    ui_present = False
    try:
        ui_present = bool(system.ui_present)
    except:
        pass

    log("system.ui_present = %s" % ui_present)

    # Suppress modal prompt dialogs during automated operations
    try:
        from _3S.CoDeSys.ScriptEngine.BasicFunctionality import PromptHandling
        system.prompt_handling = PromptHandling.NONE
    except:
        pass

    # Open project if passed via arguments or environment variable
    proj_arg = os.environ.get("CODESYS_MCP_PROJECT")
    if not proj_arg:
        for arg in sys.argv[1:]:
            if arg.lower().endswith(".project") and os.path.exists(arg):
                proj_arg = arg
                break

    if proj_arg and os.path.exists(proj_arg):
        try:
            if projects.primary is None:
                log("Opening project on startup: %s" % proj_arg)
                projects.open(proj_arg)
                log("Project opened successfully!")
        except Exception as e:
            log("Warning: could not open project on startup: %s" % str(e))

    banner_msg = "[CODESYS MCP] Native Stdio/Pipe MCP Server v%s started on \\\\.\\pipe\\%s (GUI Unlocked, %s Tools Available)" % (server_version, pipe_name, tool_count)
    log(banner_msg)
    try:
        system.write_message(
            Severity.Information,
            banner_msg
        )
    except:
        pass

    if ui_present:
        log("GUI mode active: Exiting bootstrap script immediately so CODESYS IDE remains 100% unlocked!")
    else:
        log("Headless (--noUI) mode active: Entering primary thread queue loop...")
        while True:
            try:
                CodesysMcp.CodesysContext.ProcessQueue(20)
                system.delay(50)
            except Exception:
                time.sleep(0.05)
except Exception as e:
    log("FATAL ERROR: " + str(e))
    log(traceback.format_exc())
    raise
