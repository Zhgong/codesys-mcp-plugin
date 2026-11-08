# Changelog

All notable changes to the `codesys-mcp-plugin` (CODESYS Native C# Stdio MCP Server) project will be documented in this file.

## [1.2.4] - 2026-11-08

### Added
- **Device Authentication & Headless Logon Safety**:
  - Implemented 3-layer protection against SoftPLC / device modal login dialogs (`#32770`).
  - Layer 1: Proactive default credentials (`admin`/`1111`) injected via `online.set_default_credentials` in IronPython bootstrap and C# `CodesysOnlineHelper.PlcLogin`.
  - Layer 2: Support for `username`/`password` parameters in `plc_login` and `CODESYS_DEVICE_USER`/`CODESYS_DEVICE_PASSWORD` environment variables.
  - Layer 3: Native Win32 background watchdog (`DeviceLogonWatchdog`) scanning primary UI thread to auto-fill or dismiss unexpected modal dialogs within 1 second, preventing UI deadlocks.

## [1.2.3] - 2026-11-07

### Added
- **Unified Session Log Synchronization**:
  - Mirrored all C# in-process MCP request, tool execution, PLC operation, and error logs directly to `~/.codesys-mcp-plugin/session_debug.log` with 10MB auto-rollover protection.
- **Diagnostics Card UX**:
  - Fixed Close button on "CODESYS MCP Server Diagnostics" card form and bound `Escape` key shortcut.

## [1.2.2] - 2026-11-06

### Added
- **IDE Message Categories & Multi-channel Sensing**:
  - Added `get_message_categories` to discover registered message categories in CODESYS IDE (Build, Scripting, etc.).
  - Added `get_ide_messages` for category-specific message filtering.

## [1.2.0] - 2026-11-05

### Added
- **Online PLC Control & Variable Suite**:
  - Added tools for online operations: `plc_login`, `plc_logout`, `plc_control`, `get_online_status`, `inspect_online`.
  - Added variable read/write tools: `read_variables`, `write_variable`, `batch_write_variables`, `unforce_variables`, `read_struct`, `write_struct`, `read_array`.
  - Added real-time trace support: `add_trace_variable`, `get_trace_info`, `analyze_trace`.
  - Added diagnostics and logging tools: `setup_plc_logger`, `read_plc_logs`, `setup_device_logger`, `read_device_log`, `inject_code_log`.
  - Added permanent IDE status indicator and diagnostics card (`get_ui_indicator_status`).

---

## [1.1.0] - 2026-10-27

### Added
- **Interactive CODESYS IDE Support**:
  - Added UI thread `SynchronizationContext` marshaling in `CodesysContext.RunOnPrimaryThread` so background Named Pipe requests execute thread-safely on the main CODESYS IDE UI thread.
  - Added `start_codesys_ide_with_mcp.bat` and updated `codesys_mcp_session.py` (`system.ui_present` detection) so launching in GUI IDE mode does not block the IDE foreground.
- **Version Management**:
  - Added `VERSION` file, automated version syncing in `build.ps1`, `--version` CLI flag on `CodesysMcpCli.exe`, and dynamic assembly version reporting in MCP `initialize` (`serverInfo.version`).

### Fixed
- **POU Resolution Priority (`FindObjectByName`)**:
  - When a POU name (e.g., `MOTION_PRG`) matches both a Task Configuration call reference and the actual POU object, `FindObjectByName` now prioritizes the object containing textual declaration or implementation (`IScriptTextualObjectMarker`).
- **Strongly-Typed ScriptEngine Extension Resolution (`GetExtension<TExt>`)**:
  - Resolved `IScriptTextualObjectMarker`, `IScriptObjectWithTextualDeclaration`, `IScriptObjectWithTextualImplementation`, `IScriptIecLanguageObjectContainer`, and `IScriptApplication` directly from `IExtendedObject<T>.Extensions`.

---

## [1.0.0] - 2026-10-22

### Added
- Initial standalone release of `codesys-plugin`.
- In-process C# MCP Server (`CodesysMcp.dll`) communicating over Windows Named Pipe (`\\.\pipe\codesys_mcp_pipe`).
- Pure Stdio bridge executable (`CodesysMcpCli.exe`) with unbuffered byte-level line streaming (`ReadLineFromPipe` / `WriteLineToPipe`).
- 10 core MCP tools: `open_project`, `close_project`, `save_project`, `get_project_info`, `get_project_tree`, `read_pou`, `write_pou`, `delete_pou`, `compile_project`, `get_compiler_messages`.
