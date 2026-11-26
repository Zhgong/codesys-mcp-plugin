# CODESYS MCP Plugin — E2E Compatibility & Reliability Matrix

- **Last Updated (UTC)**: `2026-11-26T21:37:12Z`
- **MCP Server Version**: `1.5.0`
- **Tracked CODESYS IDE Versions**: `3.5.20.50`
- **Total Registered MCP Tools**: `48` across `8` categories

## 1. Functional Category × CODESYS Version Summary Matrix

| Functional Category | Tools | `3.5.20.50` (CODESYS V3.5 SP20 Patch 5) |
| :--- | :---: | :---: |
| 1. Project Management & Navigation | 10 | PASS (10/10, 1607.6ms, rel 100%) |
| 2. Code Editing, Search & Mirror | 9 | PASS (9/9, 79.9ms, rel 100%) |
| 3. Compilation & IDE Messages | 4 | PASS (4/4, 2040.4ms, rel 100%) |
| 4. Online Connection & PLC Control | 5 | PASS (5/5, 3182.9ms, rel 100%) |
| 5. Variable I/O, Primitives & Structs | 9 | PASS (9/9, 124.7ms, rel 100%) |
| 6. Real-time Trace Engine | 4 | PASS (4/4, 55.9ms, rel 100%) |
| 7. Logging & Diagnostics | 5 | PASS (5/5, 219.9ms, rel 100%) |
| 8. IDE UI Indicator & Visual | 2 | PASS (2/2, 90.8ms, rel 100%) |

## 2. Detailed Tool Verification Matrix (All 48 Registered Tools)

| Tool | Category | Scope | `3.5.20.50` Status / Latency / Reliability | Latest Verification Note |
| :--- | :--- | :---: | :---: | :--- |
| `open_project` | 1. Project Management & Navigation | Offline/IDE | PASS (11010.5ms, 100%) | Opened isolated ServoPress_VirtualAxis.project |
| `close_project` | 1. Project Management & Navigation | Offline/IDE | PASS (753.0ms, 100%) | Closed isolated project |
| `get_project_info` | 1. Project Management & Navigation | Offline/IDE | PASS (2440.4ms, 100%) | ideVersion=3.5.20.50, app=Application |
| `get_project_tree` | 1. Project Management & Navigation | Offline/IDE | PASS (44.1ms, 100%) | nodes=5 |
| `create_folder` | 1. Project Management & Navigation | Offline/IDE | PASS (48.9ms, 100%) | _MatrixFolder |
| `create_interface` | 1. Project Management & Navigation | Offline/IDE | PASS (39.0ms, 100%) | ITF_MatrixProbe |
| `create_method` | 1. Project Management & Navigation | Offline/IDE | PASS (51.4ms, 100%) | FB_MatrixProbe.GetVal |
| `create_property` | 1. Project Management & Navigation | Offline/IDE | PASS (50.8ms, 100%) | FB_MatrixProbe.PropVal |
| `save_project` | 1. Project Management & Navigation | Offline/IDE | PASS (906.6ms, 100%) | Saved temp project |
| `create_boot_application` | 1. Project Management & Navigation | Online/SoftPLC | PASS (731.7ms, 100%) | Boot app created |
| `read_pou` | 2. Code Editing, Search & Mirror | Offline/IDE | PASS (15.3ms, 100%) | PRG_MatrixProbe |
| `write_pou` | 2. Code Editing, Search & Mirror | Offline/IDE | PASS (111.2ms, 100%) | Created Enumeration, Structure, FB & Program |
| `edit_pou` | 2. Code Editing, Search & Mirror | Offline/IDE | PASS (50.2ms, 100%) | appliedEdits=1 |
| `delete_pou` | 2. Code Editing, Search & Mirror | Offline/IDE | PASS (18.8ms, 100%) | Deleted temporary POUs |
| `search_code` | 2. Code Editing, Search & Mirror | Offline/IDE | PASS (33.5ms, 100%) | matches=1 |
| `get_diff` | 2. Code Editing, Search & Mirror | Offline/IDE | PASS (10.7ms, 100%) | modifiedCount=1 |
| `preview_diff` | 2. Code Editing, Search & Mirror | Offline/IDE | PASS (55.4ms, 100%) | exported=1 |
| `rollback_pou` | 2. Code Editing, Search & Mirror | Offline/IDE | PASS (17.1ms, 100%) | action=deleted |
| `sync_sources_to_disk` | 2. Code Editing, Search & Mirror | Offline/IDE | PASS (407.2ms, 100%) | created=69 |
| `compile_project` | 3. Compilation & IDE Messages | Offline/IDE | PASS (7438.3ms, 100%) | errors=0, warnings=0 |
| `get_compiler_messages` | 3. Compilation & IDE Messages | Offline/IDE | PASS (667.6ms, 100%) | errors=0, messages=0 |
| `get_message_categories` | 3. Compilation & IDE Messages | Offline/IDE | PASS (40.6ms, 100%) | categories=91 |
| `get_ide_messages` | 3. Compilation & IDE Messages | Offline/IDE | PASS (14.9ms, 100%) | count=9 |
| `get_online_status` | 4. Online Connection & PLC Control | Offline/Online | PASS (2.4ms, 100%) | isLoggedIn=False |
| `plc_login` | 4. Online Connection & PLC Control | Online/SoftPLC | PASS (4875.2ms, 100%) | state=run, autoStarted=True |
| `inspect_online` | 4. Online Connection & PLC Control | Online/SoftPLC | PASS (10383.2ms, 100%) | Device inspected |
| `plc_control` | 4. Online Connection & PLC Control | Online/SoftPLC | PASS (402.4ms, 100%) | stop -> start verified |
| `plc_logout` | 4. Online Connection & PLC Control | Online/SoftPLC | PASS (251.2ms, 100%) | Logged out cleanly |
| `read_variables` | 5. Variable I/O, Primitives & Structs | Online/SoftPLC | PASS (125.8ms, 100%) | Read 2 variables |
| `write_variable` | 5. Variable I/O, Primitives & Structs | Online/SoftPLC | PASS (124.4ms, 100%) | Enum E_PressProfileId.PROFILE_HARD_PIN resolved & verified |
| `pulse_variable` | 5. Variable I/O, Primitives & Structs | Online/SoftPLC | PASS (295.4ms, 100%) | pulse_ms=30, elapsed=222ms |
| `wait_for_variable` | 5. Variable I/O, Primitives & Structs | Online/SoftPLC | PASS (10.6ms, 100%) | samples=1 |
| `unforce_variables` | 5. Variable I/O, Primitives & Structs | Online/SoftPLC | PASS (64.1ms, 100%) | Unforced test expression |
| `batch_write_variables` | 5. Variable I/O, Primitives & Structs | Online/SoftPLC | PASS (124.0ms, 100%) | 2 vars atomic batch verified |
| `write_struct` | 5. Variable I/O, Primitives & Structs | Online/SoftPLC | PASS (202.7ms, 100%) | PressRecipe struct verified |
| `read_struct` | 5. Variable I/O, Primitives & Structs | Online/SoftPLC | PASS (163.3ms, 100%) | fields=11 |
| `read_array` | 5. Variable I/O, Primitives & Structs | Online/SoftPLC | PASS (11.8ms, 100%) | items=4 |
| `get_trace_info` | 6. Real-time Trace Engine | Offline/IDE | PASS (32.0ms, 100%) | vars=11 |
| `add_trace_variable` | 6. Real-time Trace Engine | Offline/IDE | PASS (82.2ms, 100%) | MOTION_PRG.rDispPos |
| `control_trace` | 6. Real-time Trace Engine | Offline/IDE | PASS (102.0ms, 100%) | action=status |
| `analyze_trace` | 6. Real-time Trace Engine | Offline/CSV | PASS (7.4ms, 100%) | cycles=100 |
| `setup_plc_logger` | 7. Logging & Diagnostics | Offline/IDE | PASS (677.0ms, 100%) | GVL_McpLogger + FB_McpLogger ready |
| `read_plc_logs` | 7. Logging & Diagnostics | Online/SoftPLC | PASS (341.6ms, 100%) | entries=1, cycle=2963 |
| `setup_device_logger` | 7. Logging & Diagnostics | Offline/IDE | PASS (13.5ms, 100%) | fcCreated=False |
| `inject_code_log` | 7. Logging & Diagnostics | Offline/IDE | PASS (56.1ms, 100%) | Injected FC_LogMsg into PRG_MatrixProbe |
| `read_device_log` | 7. Logging & Diagnostics | Offline/XML | PASS (11.3ms, 100%) | XML log parse verified |
| `get_ui_indicator_status` | 8. IDE UI Indicator & Visual | Offline/IDE | PASS (3.9ms, 100%) | status=Busy, ui=GUI |
| `capture_ide_screenshot` | 8. IDE UI Indicator & Visual | GUI IDE | PASS (177.6ms, 100%) | 800x395 |
