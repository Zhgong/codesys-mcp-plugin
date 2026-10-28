using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodesysMcp
{
    public class McpServer
    {
        public const string DefaultPipeName = "codesys_mcp_pipe";
        private readonly string _pipeName;
        private readonly JavaScriptSerializer _json;
        private Thread _listenerThread;
        private volatile bool _isRunning;

        public McpServer(string pipeName = DefaultPipeName)
        {
            _pipeName = pipeName;
            _json = new JavaScriptSerializer();
            _json.MaxJsonLength = int.MaxValue;
        }

        public void Start()
        {
            if (_isRunning) return;

            _isRunning = true;
            _listenerThread = new Thread(PipeListenLoop)
            {
                IsBackground = true,
                Name = "CodesysMcpPipeListener"
            };
            _listenerThread.Start();
            Console.WriteLine($"[CodesysMcp] Pure Stdio/Named-Pipe Server started on pipe: {_pipeName}");
        }

        public void Stop()
        {
            _isRunning = false;
            Console.WriteLine("[CodesysMcp] Pipe Server stopped.");
        }

        private void PipeListenLoop()
        {
            while (_isRunning)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.None);

                    server.WaitForConnection();

                    var clientThread = new Thread(ProcessClientConnection)
                    {
                        IsBackground = true
                    };
                    clientThread.Start(server);
                }
                catch (Exception ex)
                {
                    if (!_isRunning) break;
                    Console.WriteLine("[CodesysMcp] Pipe listener error: " + ex.Message);
                    Thread.Sleep(200);
                }
            }
        }

        public static string ReadLineFromPipe(Stream stream)
        {
            var bytes = new List<byte>();
            int b;
            while ((b = stream.ReadByte()) != -1)
            {
                if (b == '\n') break;
                if (b != '\r') bytes.Add((byte)b);
            }
            if (b == -1 && bytes.Count == 0) return null;
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        public static void WriteLineToPipe(Stream stream, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        private void ProcessClientConnection(object state)
        {
            var server = (NamedPipeServerStream)state;
            try
            {
                using (server)
                {
                    string line;
                    while (_isRunning && (line = ReadLineFromPipe(server)) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        object resultObj = ProcessJsonRpc(line);
                        if (resultObj != null)
                        {
                            string responseJson = _json.Serialize(resultObj);
                            WriteLineToPipe(server, responseJson);
                        }
                    }
                }
            }
            catch
            {
                // Client disconnected
            }
        }

        public object ProcessJsonRpc(string requestJson)
        {
            Dictionary<string, object> req;
            try
            {
                req = _json.Deserialize<Dictionary<string, object>>(requestJson);
            }
            catch (Exception ex)
            {
                return new
                {
                    jsonrpc = "2.0",
                    id = (object)null,
                    error = new { code = -32700, message = "Parse error: " + ex.Message }
                };
            }

            if (req == null) return null;

            object id = req.ContainsKey("id") ? req["id"] : null;
            string method = req.ContainsKey("method") ? req["method"]?.ToString() : null;
            var parameters = req.ContainsKey("params") ? req["params"] as Dictionary<string, object> : null;

            if (string.IsNullOrEmpty(method))
            {
                return new
                {
                    jsonrpc = "2.0",
                    id,
                    error = new { code = -32600, message = "Invalid Request" }
                };
            }

            if (method == "notifications/initialized")
            {
                return null;
            }

            if (method == "ping")
            {
                return new { jsonrpc = "2.0", id, result = new object() };
            }

            if (method == "initialize")
            {
                return new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new
                    {
                        protocolVersion = "2024-11-05",
                        capabilities = new
                        {
                            tools = new { listChanged = false }
                        },
                        serverInfo = new
                        {
                            name = "codesys-native-mcp",
                            version = typeof(McpServer).Assembly.GetName().Version.ToString(3)
                        }
                    }
                };
            }

            if (method == "tools/list")
            {
                return new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new
                    {
                        tools = GetToolDefinitions()
                    }
                };
            }

            if (method == "tools/call")
            {
                string toolName = parameters != null && parameters.ContainsKey("name") ? parameters["name"]?.ToString() : null;
                var arguments = parameters != null && parameters.ContainsKey("arguments") ? parameters["arguments"] as Dictionary<string, object> : new Dictionary<string, object>();

                try
                {
                    var toolOutput = ExecuteTool(toolName, arguments ?? new Dictionary<string, object>());
                    return new
                    {
                        jsonrpc = "2.0",
                        id,
                        result = new
                        {
                            content = new[]
                            {
                                new { type = "text", text = _json.Serialize(toolOutput) }
                            },
                            isError = false
                        }
                    };
                }
                catch (Exception ex)
                {
                    while (ex is TargetInvocationException && ex.InnerException != null)
                    {
                        ex = ex.InnerException;
                    }
                    CodesysContext.LogToIde($"[ERROR] MCP Tool '{toolName}' failed: {ex.Message}");
                    return new
                    {
                        jsonrpc = "2.0",
                        id,
                        result = new
                        {
                            content = new[]
                            {
                                new { type = "text", text = "Error executing tool: " + ex.Message }
                            },
                            isError = true
                        }
                    };
                }
            }

            return new
            {
                jsonrpc = "2.0",
                id,
                error = new { code = -32601, message = $"Method not found: {method}" }
            };
        }

        private object ExecuteTool(string name, Dictionary<string, object> args)
        {
            CodesysContext.LogToIde($"[REQUEST] MCP Tool '{name}' invoked");

            switch (name)
            {
                case "open_project":
                    if (!args.ContainsKey("path")) throw new ArgumentException("Parameter 'path' is required.");
                    return CodesysOperations.OpenProject(args["path"].ToString());

                case "close_project":
                    return new { closed = CodesysOperations.CloseProject() };

                case "get_project_info":
                    return CodesysOperations.GetProjectInfo();

                case "get_project_tree":
                    bool full = args.ContainsKey("full") && Convert.ToBoolean(args["full"]);
                    return CodesysOperations.GetProjectTree(full);

                case "read_pou":
                    if (!args.ContainsKey("name")) throw new ArgumentException("Parameter 'name' is required.");
                    return CodesysOperations.ReadPou(args["name"].ToString());

                case "write_pou":
                    if (!args.ContainsKey("name")) throw new ArgumentException("Parameter 'name' is required.");
                    string pouName = args["name"].ToString();
                    string decl = args.ContainsKey("declaration") ? args["declaration"]?.ToString() : null;
                    string impl = args.ContainsKey("implementation") ? args["implementation"]?.ToString() : null;
                    string type = args.ContainsKey("type") ? args["type"]?.ToString() : "Program";
                    return CodesysOperations.WritePou(pouName, decl, impl, type);

                case "delete_pou":
                    if (!args.ContainsKey("name")) throw new ArgumentException("Parameter 'name' is required.");
                    return new { deleted = CodesysOperations.DeletePou(args["name"].ToString()) };

                case "compile_project":
                    bool clean = args.ContainsKey("clean") && Convert.ToBoolean(args["clean"]);
                    bool rebuild = args.ContainsKey("rebuild") && Convert.ToBoolean(args["rebuild"]);
                    return CodesysOperations.CompileProject(clean, rebuild);

                case "get_compiler_messages":
                    return CodesysOperations.CollectCompilerMessages();

                case "save_project":
                    return new { saved = CodesysOperations.SaveProject() };

                case "search_code":
                    if (!args.ContainsKey("query") || string.IsNullOrEmpty(args["query"]?.ToString()))
                        throw new ArgumentException("Parameter 'query' is required.");
                    string searchQuery = args["query"].ToString();
                    bool isRegex = args.ContainsKey("is_regex") && Convert.ToBoolean(args["is_regex"]);
                    bool caseSensitive = args.ContainsKey("case_sensitive") && Convert.ToBoolean(args["case_sensitive"]);
                    string searchScope = args.ContainsKey("scope") ? args["scope"]?.ToString() : "all";
                    int maxResults = args.ContainsKey("max_results") ? Convert.ToInt32(args["max_results"]) : 100;
                    return CodesysOperations.SearchCode(searchQuery, isRegex, caseSensitive, searchScope, maxResults);

                case "get_diff":
                    string diffTarget = args.ContainsKey("name") ? args["name"]?.ToString() : null;
                    string diffSection = args.ContainsKey("section") ? args["section"]?.ToString() : "all";
                    return CodesysOperations.GetDiff(diffTarget, diffSection);

                case "rollback_pou":
                    if (!args.ContainsKey("name") || string.IsNullOrEmpty(args["name"]?.ToString()))
                        throw new ArgumentException("Parameter 'name' is required.");
                    return CodesysOperations.RollbackPou(args["name"].ToString());

                case "get_ide_messages":
                    string catParam = args.ContainsKey("category") ? args["category"]?.ToString() : null;
                    return new { messages = CodesysOperations.GetIdeMessages(catParam) };

                case "preview_diff":
                case "open_diff_viewer":
                    string pTarget = args.ContainsKey("name") ? args["name"]?.ToString() : null;
                    string pViewer = args.ContainsKey("viewer") ? args["viewer"]?.ToString() : "vscode";
                    string pOutDir = args.ContainsKey("output_dir") ? args["output_dir"]?.ToString() : null;
                    return CodesysOperations.PreviewDiff(pTarget, pViewer, pOutDir);

                case "create_boot_application":
                    string bootApp = args.ContainsKey("app_name") ? args["app_name"]?.ToString() : null;
                    return new { success = true, message = CodesysOperations.CreateBootApplication(bootApp) };

                case "get_online_status":
                    return CodesysOperations.GetOnlineStatus();

                case "plc_login":
                    string loginMode = args.ContainsKey("mode") ? args["mode"]?.ToString() : "try_online_change";
                    string loginApp = args.ContainsKey("app_name") ? args["app_name"]?.ToString() : null;
                    bool delForeign = !args.ContainsKey("delete_foreign_apps") || Convert.ToBoolean(args["delete_foreign_apps"]);
                    return CodesysOperations.PlcLogin(loginMode, delForeign, loginApp);

                case "plc_logout":
                    return CodesysOperations.PlcLogout();

                case "plc_control":
                    if (!args.ContainsKey("action") || string.IsNullOrEmpty(args["action"]?.ToString()))
                        throw new ArgumentException("Parameter 'action' is required ('start', 'stop', 'reset').");
                    return CodesysOperations.PlcControl(args["action"].ToString());

                case "read_variables":
                    var exprList = new List<string>();
                    if (args.ContainsKey("expressions"))
                    {
                        var arr = args["expressions"] as System.Collections.IEnumerable;
                        if (arr != null)
                        {
                            foreach (var item in arr) if (item != null) exprList.Add(item.ToString());
                        }
                    }
                    else if (args.ContainsKey("expression"))
                    {
                        exprList.Add(args["expression"].ToString());
                    }
                    return CodesysOperations.ReadVariables(exprList);

                case "write_variable":
                    if (!args.ContainsKey("name") || string.IsNullOrEmpty(args["name"]?.ToString()))
                        throw new ArgumentException("Parameter 'name' is required.");
                    string varName = args["name"].ToString();
                    string varVal = args.ContainsKey("value") ? args["value"]?.ToString() : "";
                    bool isForce = args.ContainsKey("force") && Convert.ToBoolean(args["force"]);
                    return CodesysOperations.WriteVariable(varName, varVal, isForce);

                case "unforce_variables":
                    var unforceList = new List<string>();
                    if (args.ContainsKey("expressions"))
                    {
                        var arr = args["expressions"] as System.Collections.IEnumerable;
                        if (arr != null)
                        {
                            foreach (var item in arr) if (item != null) unforceList.Add(item.ToString());
                        }
                    }
                    return CodesysOperations.UnforceVariables(unforceList);

                default:
                    throw new NotSupportedException($"Tool '{name}' is not recognized.");
            }
        }

        private object[] GetToolDefinitions()
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
                    description = "Get status and details of current active CODESYS project.",
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
                            type = new { type = "string", description = "Type of POU: Program, FunctionBlock, or Function", @enum = new[] { "Program", "FunctionBlock", "Function" } }
                        },
                        required = new[] { "name" }
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
                    description = "Get all current compiler error and warning messages without re-triggering build.",
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
                    name = "plc_login",
                    description = "Log in to PLC or SoftPLC instance with seamless headless parameter support. Defaults to 'try_online_change' and delete_foreign_apps=true to eliminate modal dialogs.",
                    inputSchema = new
                    {
                        type = "object",
                        properties = new
                        {
                            mode = new { type = "string", description = "Login mode: 'try_online_change' (default, safe hot-update), 'download' / 'never' (full download/stop), 'force_online_change', or 'login_only' / 'keep' (monitor only)", @enum = new[] { "try_online_change", "download", "force_online_change", "login_only" } },
                            delete_foreign_apps = new { type = "boolean", description = "If true (default), automatically replaces conflicting applications on the PLC without modal confirmation dialogs." },
                            app_name = new { type = "string", description = "Optional target application name." }
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
                }
            };
        }
    }
}
