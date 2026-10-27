# Changelog

All notable changes to the `codesys-plugin` (CODESYS Native C# Stdio MCP Server) project will be documented in this file.

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
