using System;

namespace CodesysMcp
{
    public partial class McpServer
    {
        public static int RegisteredToolCount => GetToolDefinitions().Length;
        public static object[] GetToolDefinitions()
        {
            return new object[]
            {
                new
                {
                    name = "open_project",
                    description = "Open a CODESYS project file (.project).",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Absolute file path to the .project file" }
                        },
                        required = new[] { "path" }
                    }
                },
                new
                {
                    name = "close_project",
                    description = "Close the currently open CODESYS project.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new { }
                    }
                },
                new
                {
                    name = "get_project_info",
                    description = "Get status and details of current active CODESYS project, including active .st mirror metadata (mirrorEnabled, mirrorDir, mirrorMode, mirrorGitEnabled).",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new { }
                    }
                },
                new
                {
                    name = "get_project_tree",
                    description = "Get project tree including all POUs, devices, and tasks.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            full = new { type = "boolean", description = "Whether to fetch full tree details" }
                        }
                    }
                },
                new
                {
                    name = "read_pou",
                    description = "Read declaration and implementation (Structured Text) of a POU.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            name = new { type = "string", description = "Name of the POU to read" }
                        },
                        required = new[] { "name" }
                    }
                },
                new
                {
                    name = "write_pou",
                    description = "Create or update declaration and implementation of a POU in CODESYS.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            name = new { type = "string", description = "Name of the POU" },
                            declaration = new { type = "string", description = "Declaration code (VAR...END_VAR)" },
                            implementation = new { type = "string", description = "Implementation code (ST code)" },
                            type = new { type = "string", description = "Type of POU: Program, FunctionBlock, Function, Interface, Method, Property, Folder, DUT, Structure, Enumeration, or GVL" }
                        },
                        required = new[] { "name" }
                    }
                },
                new
                {
                    name = "create_folder",
                    description = "Create a folder under active application or target parent folder in CODESYS project tree.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            name = new { type = "string", description = "Folder name" },
                            parent = new { type = "string", description = "Parent folder or application path (optional)" }
                        },
                        required = new[] { "name" }
                    }
                },
                new
                {
                    name = "create_interface",
                    description = "Create an IEC 61131-3 Interface in CODESYS project tree.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            name = new { type = "string", description = "Interface name (e.g. 'ITF_TestCase')" },
                            parent = new { type = "string", description = "Parent folder or application path (optional)" },
                            base_interfaces = new { type = "string", description = "Base interfaces to extend (optional)" }
                        },
                        required = new[] { "name" }
                    }
                },
                new
                {
                    name = "create_method",
                    description = "Create a Method on a POU or Interface in CODESYS.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            parent = new { type = "string", description = "Parent POU or Interface name (e.g. 'FB_TestCase')" },
                            name = new { type = "string", description = "Method name (e.g. 'AssertTrue')" },
                            return_type = new { type = "string", description = "Return type (e.g. 'BOOL', optional)" },
                            declaration = new { type = "string", description = "Declaration header and variables (optional)" },
                            implementation = new { type = "string", description = "Method implementation code (optional)" }
                        },
                        required = new[] { "parent", "name" }
                    }
                },
                new
                {
                    name = "create_property",
                    description = "Create a Property on a POU or Interface in CODESYS.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            parent = new { type = "string", description = "Parent POU or Interface name" },
                            name = new { type = "string", description = "Property name" },
                            return_type = new { type = "string", description = "Property return type (e.g. 'STRING(80)')" }
                        },
                        required = new[] { "parent", "name" }
                    }
                },
                new
                {
                    name = "delete_pou",
                    description = "Delete a POU or object from the project.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            name = new { type = "string", description = "Name of the POU to delete" }
                        },
                        required = new[] { "name" }
                    }
                },
                new
                {
                    name = "compile_project",
                    description = "Compile the active CODESYS application and retrieve compilation errors/warnings.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            clean = new { type = "boolean", description = "Clean before build" },
                            rebuild = new { type = "boolean", description = "Rebuild all components" }
                        }
                    }
                },
                new
                {
                    name = "get_compiler_messages",
                    description = "Get compiler errors, warnings and messages from CODESYS IDE (defaults to 'Build' category).",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            category = new
                            {
                                type = "string",
                                description = "Message category to query: 'Build' (default, compiler/code generator), 'Precompile', 'Script Messages', or 'all'",
                                @default = "Build"
                            },
                            min_severity = new
                            {
                                type = "string",
                                description = "Minimum severity level: 'Error', 'Warning' (default), 'Information', 'Text'",
                                @default = "Warning"
                            }
                        }
                    }
                },
                new
                {
                    name = "get_message_categories",
                    description = "Lists all message categories in CODESYS IDE (Build, Precompile, Script Messages, etc.) with error and warning counts.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new { }
                    }
                },
                new
                {
                    name = "save_project",
                    description = "Save the currently active CODESYS project.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new { }
                    }
                },
                new
                {
                    name = "sync_sources_to_disk",
                    description = "Incrementally mirror all project POUs, DUTs, GVLs, and methods to readable .st files (one-way CODESYS -> .st, default <project_dir>/.cds_mcp/src with auto-git commit on save).",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            mirror_dir = new { type = "string", description = "Target directory for .st mirror files. Optional if CODESYS_MCP_MIRROR_DIR is set or already bound in this session." },
                            persist_for_session = new { type = "boolean", description = "Bind mirror_dir as the active session mirror for write-through auto-refresh (default: true)." },
                            disable = new { type = "boolean", description = "Clear the active session mirror binding and stop watching without deleting existing files (default: false)." },
                            root_node = new { type = "string", description = "Optional tree node name (e.g. 'Application') to mirror relative to that subtree instead of project root." },
                            watch = new { type = "boolean", description = "Watch mirror_dir for external .st file saves and auto-sync changes back into CODESYS (default: true)." }
                        }
                    }
                },
                new
                {
                    name = "search_code",
                    description = "Search for text or regular expressions across all POUs, GVLs, and DUTs in the active CODESYS project.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new { type = "string", description = "Text or regular expression pattern to search for" },
                            is_regex = new { type = "boolean", description = "Whether query is a regex pattern (default: false)" },
                            case_sensitive = new { type = "boolean", description = "Whether search is case-sensitive (default: false)" },
                            scope = new { type = "string", description = "Search scope: all, pou, gvl, or dut (default: all)", @enum = new[] { "all", "pou", "gvl", "dut" } },
                            max_results = new { type = "integer", description = "Maximum number of matched lines to return (default: 100)" }
                        },
                        required = new[] { "query" }
                    }
                },
                new
                {
                    name = "get_diff",
                    description = "Get unified diff (git-like diff) showing modifications made to POUs during the current session.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            name = new { type = "string", description = "Optional POU name to get diff for. If omitted, returns diffs for all modified POUs." },
                            section = new { type = "string", description = "Section to diff: all, declaration, or implementation (default: all)", @enum = new[] { "all", "declaration", "implementation" } }
                        }
                    }
                },
                new
                {
                    name = "rollback_pou",
                    description = "Roll back a POU to its baseline snapshot before session modifications, or delete it if it was newly created.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            name = new { type = "string", description = "Name of the POU to roll back" }
                        },
                        required = new[] { "name" }
                    }
                },
                new
                {
                    name = "get_ide_messages",
                    description = "Get messages from the CODESYS IDE Messages window (e.g. 'script message' category).",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            category = new { type = "string", description = "Optional message category GUID or name (defaults to 'script message' / '{194B48A9-AB51-43ae-B9A9-51D3EDAADDF3}')" }
                        }
                    }
                },
                new
                {
                    name = "preview_diff",
                    description = "Export modified POUs to .diff_preview/ and launch a visual diff viewer (e.g. VS Code 'code --diff' or standalone HTML report).",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            name = new { type = "string", description = "Optional POU name to preview. If omitted, exports all modified POUs in the current session." },
                            viewer = new { type = "string", description = "Diff viewer to launch: 'vscode' (default), 'browser', 'auto', or 'none'", @enum = new[] { "vscode", "browser", "auto", "none" } },
                            output_dir = new { type = "string", description = "Optional custom output directory to export before/after files (defaults to <project>/.diff_preview)" }
                        }
                    }
                },
                new
                {
                    name = "create_boot_application",
                    description = "Create and save an offline boot application (boot project) so the PLC/SoftPLC can run autonomously upon boot/power cycle.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            app_name = new { type = "string", description = "Optional application name. If omitted, uses active application." }
                        }
                    }
                },
                new
                {
                    name = "get_online_status",
                    description = "Get current online connection status to PLC/SoftPLC, including application state (RUN, STOP, HALTED) and forced expressions.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new { }
                    }
                },
                new
                {
                    name = "inspect_online",
                    description = "Inspect live online methods and device communication settings for diagnosis.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            target = new { type = "string", description = "Target name to inspect (default 'Device')" }
                        }
                    }
                },
                new
                {
                    name = "plc_login",
                    description = "Log in to PLC or SoftPLC instance with seamless headless parameter support. Defaults to 'try_online_change' and delete_foreign_apps=true to eliminate modal dialogs.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            mode = new { type = "string", description = "Login mode: 'try_online_change' (default, safe hot-update), 'download' / 'never' (full download/stop), 'force_online_change', or 'login_only' / 'keep' (monitor only)", @enum = new[] { "try_online_change", "download", "force_online_change", "login_only" } },
                            delete_foreign_apps = new { type = "boolean", description = "If true (default), automatically replaces conflicting applications on the PLC without modal confirmation dialogs." },
                            app_name = new { type = "string", description = "Optional target application name." },
                            username = new { type = "string", description = "Optional PLC Device User Logon username (calls online.set_default_credentials to avoid Device User Logon modal popup)." },
                            password = new { type = "string", description = "Optional PLC Device User Logon password." }
                        }
                    }
                },
                new
                {
                    name = "plc_logout",
                    description = "Safely disconnect and log out from the PLC or SoftPLC instance.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new { }
                    }
                },
                new
                {
                    name = "plc_control",
                    description = "Control PLC state: 'start' (RUN), 'stop' (STOP), or 'reset'.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            action = new { type = "string", description = "Control action to execute", @enum = new[] { "start", "stop", "reset" } }
                        },
                        required = new[] { "action" }
                    }
                },
                new
                {
                    name = "read_variables",
                    description = "Batch read real-time variable values from the running PLC/SoftPLC (e.g. 'GVL_Data.rCurrentPressure', 'MOTION_PRG.eDispState').",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            expressions = new
                            {
                                type = "array",
                                items = new { type = "string" },
                                description = "List of variable expressions to read"
                            }
                        },
                        required = new[] { "expressions" }
                    }
                },
                new
                {
                    name = "write_variable",
                    description = "Write a value or force a variable in the running PLC/SoftPLC (useful for triggering inputs or injecting test signals).",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            name = new { type = "string", description = "Variable expression/name (e.g. 'GVL_Data.bStartCycle')" },
                            value = new { type = "string", description = "Value to write (e.g. 'TRUE', '12.5', 'E_PressState.STATE_IDLE')" },
                            force = new { type = "boolean", description = "If true, permanently forces the variable; if false, performs a single standard write." }
                        },
                        required = new[] { "name", "value" }
                    }
                },
                new
                {
                    name = "unforce_variables",
                    description = "Unforce (release) forced variables on the running PLC/SoftPLC.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            expressions = new
                            {
                                type = "array",
                                items = new { type = "string" },
                                description = "Optional list of expressions to unforce. If omitted, unforces all currently forced expressions."
                            }
                        }
                    }
                },
                new
                {
                    name = "add_trace_variable",
                    description = "Add a variable expression to a CODESYS Trace object and diagram (e.g. 'MOTION_PRG.PressRecipe.rTargetForce').",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            variable_name = new { type = "string", description = "IEC variable expression to trace (e.g. 'MOTION_PRG.PressRecipe.rTargetForce', 'MOTION_PRG.rDispPos')" },
                            trace_name = new { type = "string", description = "Name of the Trace object in the project (defaults to 'Trace')" }
                        },
                        required = new[] { "variable_name" }
                    }
                },
                new
                {
                    name = "get_trace_info",
                    description = "Get variable list, trigger settings, and recording configuration from a CODESYS Trace object.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            trace_name = new { type = "string", description = "Name of the Trace object in the project (defaults to 'Trace')" }
                        }
                    }
                },
                new
                {
                    name = "batch_write_variables",
                    description = "Write multiple PLC variables in a single scan cycle atomically, with optional readback verification.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            variables = new { type = "object", description = "Key-value map of variable expressions to target values (e.g. {'PLC_PRG.PressRecipe.rTargetForce': '5500.0', 'PLC_PRG.PressRecipe.rFastSpeed': '45.0'})" },
                            verify = new { type = "boolean", description = "Whether to read back and verify each variable (defaults to true)" },
                            force = new { type = "boolean", description = "Whether to force instead of write (defaults to false)" }
                        },
                        required = new[] { "variables" }
                    }
                },
                new
                {
                    name = "write_struct",
                    description = "Write a complete JSON struct or recipe object into a PLC STRUCT instance atomically in a single cycle.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            struct_name = new { type = "string", description = "Base variable path of the struct instance (e.g. 'PLC_PRG.PressRecipe')" },
                            values = new { type = "object", description = "Object containing struct members and sub-members to write" },
                            verify = new { type = "boolean", description = "Whether to read back and verify written members (defaults to true)" },
                            force = new { type = "boolean", description = "Whether to force instead of write (defaults to false)" }
                        },
                        required = new[] { "struct_name", "values" }
                    }
                },
                new
                {
                    name = "read_struct",
                    description = "Read all members of a PLC STRUCT instance, either via direct tuple parsing or automatic DUT member discovery.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            struct_name = new { type = "string", description = "Base variable path of the struct instance (e.g. 'PLC_PRG.PressRecipe')" },
                            members = new { type = "array", items = new { type = "string" }, description = "Optional explicit list of member names. If omitted, members are automatically discovered from project DUT definitions." }
                        },
                        required = new[] { "struct_name" }
                    }
                },
                new
                {
                    name = "read_array",
                    description = "Batch read a slice of elements from a PLC array variable efficiently.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            variable_name = new { type = "string", description = "Name of the array variable (e.g. 'PLC_PRG.aPositionBuffer')" },
                            start_index = new { type = "integer", description = "Starting 0-based index (defaults to 0)" },
                            count = new { type = "integer", description = "Number of elements to read (defaults to 50, max 1000)" }
                        },
                        required = new[] { "variable_name" }
                    }
                },
                new
                {
                    name = "get_ui_indicator_status",
                    description = "Query the current visual status, color, and diagnostic details of the CODESYS IDE status bar indicator.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new { }
                    }
                },
                new
                {
                    name = "capture_ide_screenshot",
                    description = "Capture a PNG screenshot of the running CODESYS IDE main window itself (via Win32 PrintWindow on the UI thread, never capturing external desktop windows) for Before/After visual verification.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            output_path = new { type = "string", description = "Optional destination .png file path (defaults to %TEMP%\\codesys_ide_YYYYMMDD_HHmmss.png)" },
                            max_width = new { type = "integer", description = "Optional maximum output width in pixels for proportional downscaling (0 keeps native resolution)" }
                        }
                    }
                },
                new
                {
                    name = "control_trace",
                    description = "Control and configure a CODESYS Trace object: query state ('status'), update trigger/recording configuration ('configure'), download config to PLC ('download', optionally updating trigger settings first), start recording ('start'), stop recording ('stop'), or re-arm trigger ('reset_trigger').",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            trace_name = new { type = "string", description = "Trace object name in the project (defaults to 'Trace')" },
                            action = new { type = "string", description = "Trace action: 'status', 'configure', 'download', 'start', 'stop', or 'reset_trigger'" },
                            trigger_enabled = new { type = "boolean", description = "Optional: enable or disable hardware/runtime trigger (auto-enabled when trigger_variable is set unless explicitly false)" },
                            trigger_variable = new { type = "string", description = "Optional: IEC variable expression for trigger (e.g. 'MOTION_PRG.bDispDone' or 'MOTION_PRG.rDispForce'); pass empty string to clear" },
                            trigger_edge = new { type = "string", description = "Optional: trigger edge ('positive', 'negative', 'both', or 'undefined')" },
                            trigger_level = new { type = "string", description = "Optional: trigger threshold/level (e.g. '4500' for analog variables; omit for boolean edge triggers)" },
                            post_trigger_samples = new { type = "integer", description = "Optional: number of samples to record after the trigger fires before stopping (e.g. 50)" },
                            record_condition = new { type = "string", description = "Optional: IEC boolean recording condition expression (e.g. 'MOTION_PRG.bDispBusy'); pass empty string to clear" },
                            every_n_cycles = new { type = "integer", description = "Optional: record one sample every N task cycles (must be >= 1)" },
                            task_name = new { type = "string", description = "Optional: IEC task name to bind Trace sampling to (e.g. 'Task')" },
                            auto_start = new { type = "boolean", description = "Optional: enable or disable automatic Trace restart" }
                        },
                        required = new[] { "action" }
                    }
                },
                new
                {
                    name = "analyze_trace",
                    description = "Analyze exported CODESYS Trace file (.trace XML or .csv) or export an active project Trace to extract channel statistics, signal transitions, condition matches, and optional downsampled JSON waveform series.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            file_path = new { type = "string", description = "Path to exported .trace (XML) or .csv file on disk (optional if trace_name is provided for live export)" },
                            trace_name = new { type = "string", description = "Optional CODESYS Trace object name (e.g. 'Trace') to export from the open editor when file_path is omitted or not yet on disk" },
                            filter_condition = new { type = "string", description = "Optional condition expression to search in trace (e.g. 'rDispForce >= 5000' or 'bHmiStop == 1')" },
                            deadband = new { type = "number", description = "Deadband threshold for continuous analog value change detection (defaults to 0.01)" },
                            include_series = new { type = "boolean", description = "When true, includes JSON timestamps and per-channel waveform value arrays in the response (default: false)" },
                            start_ms = new { type = "number", description = "Optional inclusive window start timestamp in milliseconds" },
                            end_ms = new { type = "number", description = "Optional inclusive window end timestamp in milliseconds" },
                            max_points = new { type = "integer", description = "Optional maximum number of waveform points to return when include_series is true (0 returns all points; >0 applies peak-preserving downsampling)" }
                        }
                    }
                },
                new
                {
                    name = "setup_plc_logger",
                    description = "Deploy zero-dependency In-PLC Ring-Buffer Logger (ST_McpLogEntry, FB_McpLogger, GVL_McpLogger) into active project for cycle-accurate debugging.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            buffer_size = new { type = "integer", description = "Ring buffer slot capacity (defaults to 64)" },
                            auto_inject_pou = new { type = "string", description = "Optional POU name to inject GVL_McpLogger.McpLogger.TickCycle() call into implementation" }
                        }
                    }
                },
                new
                {
                    name = "read_plc_logs",
                    description = "Read and parse cycle-accurate structured logs recorded by the PLC runtime, with optional tag filtering and buffer clearing.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            tag_filter = new { type = "string", description = "Optional tag filter (e.g. 'STATE', 'ERROR', 'PRESS')" },
                            last_n = new { type = "integer", description = "Maximum number of most recent log entries to return (defaults to 32)" },
                            clear_after_read = new { type = "boolean", description = "Whether to reset the PLC log ring buffer after reading" },
            }
                    }
                },
new
                {
                    name = "setup_device_logger",
                    description = "Ensure CmpLog library is referenced and deploy standard FC_LogMsg function into active project for native CODESYS event logging.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new { }
                    }
                },
                new
                {
                    name = "inject_code_log",
                    description = "Inject an FC_LogMsg code logging call before or after a target pattern line in a POU implementation.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            pou_name = new { type = "string", description = "Target POU name (e.g. 'MOTION_PRG')" },
                            target_pattern = new { type = "string", description = "Target code snippet or comment to locate line" },
                            log_message = new { type = "string", description = "Log message string to record" },
                            level = new { type = "integer", description = "Severity level: 1=Info, 2=Warning, 3=Error (defaults to 1)" },
                            insert_after = new { type = "boolean", description = "Whether to insert after the line (true, default) or before (false)" }
                        },
                        required = new[] { "pou_name", "target_pattern", "log_message" }
                    }
                },
                new
                {
                    name = "read_device_log",
                    description = "Read real-time PLC Device / System Event logs from CODESYS runtime (CmpLog/Logger) or parse an exported log XML file with optional severity, component, and keyword filtering.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            file_path = new { type = "string", description = "Optional path to an exported log XML file (e.g. 'C:\\Users\\vboxuser\\Desktop\\log.xml'). If omitted, attempts to read online logs directly from active PLC." },
                            severity = new { type = "string", description = "Optional severity filter (e.g. 'information', 'warning', 'error', 'exception')" },
                            keyword = new { type = "string", description = "Optional keyword to match in message text or component" },
                            component = new { type = "string", description = "Optional component filter (e.g. 'CM', 'CmpSupervisor', 'CmpApp')" },
                            max_entries = new { type = "integer", description = "Maximum number of recent entries to return (defaults to 50, 0 for all)" },
                            save_to_path = new { type = "string", description = "Optional output file path to save/export log entries as XML" }
                        }
                    }
                }
            };
        }
    }
}
