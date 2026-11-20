# Changelog

All notable changes to the `codesys-mcp-plugin` (CODESYS Native C# Stdio MCP Server) project will be documented in this file.

## [1.4.0] - 2026-11-20

### Added
- **Trace Execution Control & Hardware/Runtime Signal Triggering (`control_trace`, `get_trace_info`)**:
  - Added `control_trace` MCP tool supporting `status`, `configure`, `download`, `start`, `stop`, and `reset_trigger`.
  - Added full hardware/runtime trigger & recording configuration via `ScriptTraceObject` reflection: `trigger_enabled`, `trigger_variable`, `trigger_edge` (`positive`/`negative`/`both`/`undefined`), `trigger_level`, `post_trigger_samples`, `record_condition`, `every_n_cycles`, `task_name`, and `auto_start`, plus readback in `get_trace_info`.
  - Implemented 3-layer forced overwrite on `download` (`TraceManager.DeletePacket` pre-cleanup, `ISystem.prompt_answers["Strings.OverwriteExistingOnlineTrace"]`, and `DeviceLogonWatchdog` `#32770` modal prompt auto-confirmation) so re-downloading an existing online Trace never blocks the UI thread.
- **IDE Main Window Screenshot Capture (`capture_ide_screenshot`)**:
  - Added `capture_ide_screenshot(output_path?, max_width?)` using Win32 `PrintWindow(PW_RENDERFULLCONTENT)` strictly on the CODESYS IDE main form for automated visual acceptance.
- **Direct Trace Waveform Export & Peak-Preserving Downsampling (`analyze_trace`)** (#8):
  - Added live `trace_name` export via `ScriptTraceEditorObject.save()`, time-window filtering (`start_ms`/`end_ms`), and Min-Max peak-preserving downsampling (`include_series`, `max_points`).
- **Native Batch Variable Reading (`read_variables`)** (#10):
  - Switched `read_variables` to `OnlineApplication.read_values(string[])` single-roundtrip batch reads and added DUT member caching for `read_struct`.

### Fixed
- **Stale `_onlineVarRefs` Handle Auto-Eviction (`CodesysOnlineHelper`)**:
  - Automatically releases and evicts stale `IOnlineVarRef` handles on `plc_login`, `plc_control("reset")`, and `"无效的变量引用"` / `"Invalid variable reference"` errors with transparent retry.
- **Online `read_device_log` Reflection & UI-Thread Deadlock (`PlcTraceAndLoggerHelper.Logger`)**:
  - Replaced `dynamic` calls on internal `ScriptOnline`/`ScriptOnlineDevice` with reflection and added WinForms message-pump waiting (`Application.DoEvents()`) around `BeginGetLoggerNames`/`BeginGetLoggerEntries`.
- **Cross-Assembly Hot-Reload & PLC Logger Alignment (`ServerBootstrap`, `PlcTraceAndLoggerHelper`)** (#16, #17):
  - Added `AppDomain` stop-action handoff in `ServerBootstrap` so `hot_reload_dll` cleanly stops prior pipe listeners, deterministic Roslyn builds, and aligned `FB_McpLogger` (`TickCycle`/`WriteLog`).

## [1.3.0] - 2026-11-15

### Added
- **Real-Time One-Way `.st` Source Mirror (`CODESYS → .cds_mcp/src`) & Auto-Git on Save** (#12):
  - Added `sync_sources_to_disk` with IEC-comment YAML metadata headers (`name`, `type`, `path`), SHA-256 Content-Diff Guard (skip unchanged writes), orphan `.st` cleanup, and O(1) write-through hooks on `write_pou`, `delete_pou`, `rollback_pou`, `create_*`, and `save_project`.
  - Auto-enabled one-way `.st` mirroring at startup and project open/switch, defaulting to `<project_dir>\.cds_mcp\src` with automatic `git init` and background `git commit` on `save_project` or manual IDE `Ctrl+S` saves.
  - Added adaptive IDE polling (`OnIdeSyncTimerTick`) with deletion/rename tracking and IDE status strip mirror status indicator & quick controls.
- **Live Unsaved IDE Editor Buffer Unification (`CodesysEditorBufferHelper`, `CodesysOperations`)** (#15):
  - Unified `read_pou`, `search_code`, `get_diff`, `preview_diff`, and `.st` mirror sync (`OnIdeSyncTimerTick`) with live unsaved CODESYS ST editor buffers (`_3S.CoDeSys.TextEditor.SynEdControl.Buffer` & `_3S.CoDeSys.SwitchableDeclarationEditor.DeclarationEditor`).
  - Added non-empty content guard to prevent uninitialized/unloaded UI editor panes from overwriting valid persisted POU code.
  - Captured initial project baselines (`_projectBaselines`) on mirror attach/save so `get_diff` accurately diffs manual unsaved IDE keystrokes before `write_pou` or `Ctrl+S`.
- **CLI Pipe Reliability, In-Flight EOF Drain & Auto-Reconnect (`CodesysMcpCli`)** (#9, #14):
  - Added in-flight request ID tracking (`PendingRequestIds`) and a 15s EOF drain window so one-shot piped CLI calls never exit before receiving their JSON-RPC response.
  - Added automatic Named Pipe reconnection when `CODESYS.exe` restarts and 15s idle heartbeat probes.

### Changed
- **Core Architecture & Schema Cleanup (`McpServer`, `CodesysOperations`)** (#13):
  - Split `McpServer` (`McpServer.cs`, `McpServer.Schemas.cs`) and `CodesysOperations` (`CodesysOperations.cs`, `CodesysOperations.Pou.cs`, `CodesysOperations.Compiler.cs`, `CodesysOperations.SearchAndDiff.cs`, `CodesysOperations.Mirror.cs`) into focused `partial class` files and added `.gitattributes` line-ending normalization.
  - Removed deprecated `sync_disk_to_project` from `tools/list` (`43` active MCP tools) while keeping graceful `tools/call` no-op compatibility.

## [1.2.5] - 2026-11-12

### Fixed
- **MCP Protocol & CLI Full-Duplex Bridge (`CodesysMcpCli` & `McpServer`)**:
  - Replaced the synchronous 1-in-1-out Named Pipe loop in `CodesysMcpCli` with a full-duplex asynchronous bridge (`PipeOptions.Asynchronous`, concurrent `stdin -> pipe` and `pipe -> stdout` pumps), eliminating handshake deadlocks on JSON-RPC 2.0 notifications (such as `notifications/initialized` and `notifications/cancelled` in OpenCode).
  - Updated `McpServer.ProcessJsonRpc` to strictly comply with JSON-RPC 2.0 §4.1 by returning `null` (no response) for any notification without an `"id"`, preventing `-32601 Method not found` responses from desynchronizing the pipe.

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
