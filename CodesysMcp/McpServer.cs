using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Web.Script.Serialization;

namespace CodesysMcp
{
    public partial class McpServer
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
            if (!_isRunning) return;
            _isRunning = false;
            try
            {
                using (var wake = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut))

                {
                    wake.Connect(100);
                }
            }
            catch { }
            Console.WriteLine("[CodesysMcp] Pipe Server stopped.");
        }

        private void PipeListenLoop()
        {
            while (_isRunning)
            {
                NamedPipeServerStream server = null;
                try
                {
                    PipeSecurity pipeSecurity = null;
                    try
                    {
                        pipeSecurity = new PipeSecurity();
                        pipeSecurity.AddAccessRule(new PipeAccessRule(
                            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                            PipeAccessRights.ReadWrite,
                            AccessControlType.Allow));
                    }
                    catch { }
                    if (pipeSecurity != null)
                    {
                        server = new NamedPipeServerStream(
                            _pipeName,
                            PipeDirection.InOut,
                            NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte,
                            PipeOptions.None,
                            4096,
                            4096,
                            pipeSecurity);
                    }
                    else
                    {
                        server = new NamedPipeServerStream(
                            _pipeName,
                            PipeDirection.InOut,
                            NamedPipeServerStream.MaxAllowedServerInstances,
                            PipeTransmissionMode.Byte,
                            PipeOptions.None);
                    }

                    server.WaitForConnection();
                    if (!_isRunning)
                    {
                        try { server.Dispose(); } catch { }
                        break;
                    }

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

            bool hasId = req.ContainsKey("id");
            object id = hasId ? req["id"] : null;
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

            bool isNotification = !hasId || (id == null && method.StartsWith("notifications/", StringComparison.Ordinal));
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
                            version = CodesysIdeUiHelper.GetServerVersion()
                        },
                        instructions = "Open CODESYS projects are automatically mirrored one-way (CODESYS -> .st) into <project_dir>/.cds_mcp/src with a git repository that auto-commits on every save (save_project or IDE Ctrl+S). Call get_project_info to get the exact active mirrorDir. You can read .cds_mcp/src files and use git log/git diff to track changes; always use MCP tools (write_pou, create_method, delete_pou, etc.) to modify the CODESYS project (editing .st files directly does not write back)."
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
                    CodesysIdeUiHelper.UpdateStatus(McpServerStatus.Busy, toolName);
                    var toolOutput = ExecuteTool(toolName, arguments ?? new Dictionary<string, object>());
                    CodesysIdeUiHelper.UpdateStatus(McpServerStatus.Online);
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
                    CodesysIdeUiHelper.UpdateStatus(McpServerStatus.Error, toolName, ex.Message);
                    while (ex is TargetInvocationException && ex.InnerException != null)
                    {
                        ex = ex.InnerException;
                    }
                    string stackDiag = CodesysOnlineHelper.EnsureExecutionStack();
                    CodesysContext.LogToIde($"[ERROR] MCP Tool '{toolName}' failed: {ex.Message} | StackDiag: {stackDiag} | Trace: {ex.StackTrace}");
                    return new
                    {
                        jsonrpc = "2.0",
                        id,
                        result = new
                        {
                            content = new[]
                            {
                                new { type = "text", text = $"Error executing tool: {ex.Message} [Diag: {stackDiag}] [StackTrace: {ex.StackTrace}]" }
                            },
                            isError = true
                        }
                    };
                }
            }

            if (isNotification)
            {
                return null;
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
                    string compCat = "Build";
                    if (args.ContainsKey("category") && args["category"] != null)
                        compCat = args["category"].ToString();
                    string minSev = "Warning";
                    if (args.ContainsKey("min_severity") && args["min_severity"] != null)
                        minSev = args["min_severity"].ToString();
                    return CodesysOperations.CollectCompilerMessages(compCat, minSev);

                case "get_message_categories":
                    return CodesysOperations.GetMessageCategories();

                case "save_project":
                    return new { saved = CodesysOperations.SaveProject() };

                case "sync_sources_to_disk":
                    string mirrorDirArg = args.ContainsKey("mirror_dir") ? args["mirror_dir"]?.ToString() : null;
                    bool persistSession = !args.ContainsKey("persist_for_session") || Convert.ToBoolean(args["persist_for_session"]);
                    bool disableMirror = args.ContainsKey("disable") && Convert.ToBoolean(args["disable"]);
                    string rootNodeArg = args.ContainsKey("root_node") ? args["root_node"]?.ToString() : null;
                    bool watchMirror = !args.ContainsKey("watch") || Convert.ToBoolean(args["watch"]);
                    return CodesysOperations.SyncSourcesToDisk(mirrorDirArg, persistSession, disableMirror, rootNodeArg, watchMirror);

                case "sync_disk_to_project":
                    return new
                    {
                        disabled = true,
                        message = "Disk-to-project sync is temporarily disabled. The .st mirror is one-way (CODESYS -> .cds_mcp/src) for agent change tracking."
                    };

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
                case "create_folder":
                    string fName = args.ContainsKey("name") ? args["name"]?.ToString() : null;
                    string fParent = args.ContainsKey("parent") ? args["parent"]?.ToString() : null;
                    var createdFolder = CodesysOperations.CreateFolder(fName, fParent);
                    return new { success = createdFolder != null, name = fName, parent = fParent };
                case "create_interface":
                    string itfName = args.ContainsKey("name") ? args["name"]?.ToString() : null;
                    string itfParent = args.ContainsKey("parent") ? args["parent"]?.ToString() : null;
                    string itfBase = args.ContainsKey("base_interfaces") ? args["base_interfaces"]?.ToString() : null;
                    var createdItf = CodesysOperations.CreateInterface(itfName, itfParent, itfBase);
                    return new { success = createdItf != null, name = itfName };
                case "create_method":
                    string mParent = args.ContainsKey("parent") ? args["parent"]?.ToString() : null;
                    string mName = args.ContainsKey("name") ? args["name"]?.ToString() : null;
                    string mRet = args.ContainsKey("return_type") ? args["return_type"]?.ToString() : null;
                    string mDecl = args.ContainsKey("declaration") ? args["declaration"]?.ToString() : null;
                    string mImpl = args.ContainsKey("implementation") ? args["implementation"]?.ToString() : null;
                    return CodesysOperations.CreateMethod(mParent, mName, mRet, mDecl, mImpl);
                case "create_property":
                    string propParent = args.ContainsKey("parent") ? args["parent"]?.ToString() : null;
                    string propName = args.ContainsKey("name") ? args["name"]?.ToString() : null;
                    string propRet = args.ContainsKey("return_type") ? args["return_type"]?.ToString() : null;
                    var createdProp = CodesysOperations.CreateProperty(propParent, propName, propRet);
                    return new { success = createdProp != null, name = propName, parent = propParent };
                case "create_boot_application":
                    string bootApp = args.ContainsKey("app_name") ? args["app_name"]?.ToString() : null;
                    return new { success = true, message = CodesysOperations.CreateBootApplication(bootApp) };

                case "get_online_status":
                    return CodesysOperations.GetOnlineStatus();

                case "plc_login":
                    string loginMode = args.ContainsKey("mode") ? args["mode"]?.ToString() : "try_online_change";
                    string loginApp = args.ContainsKey("app_name") ? args["app_name"]?.ToString() : null;
                    bool delForeign = !args.ContainsKey("delete_foreign_apps") || Convert.ToBoolean(args["delete_foreign_apps"]);
                    string devUser = args.ContainsKey("username") ? args["username"]?.ToString() : null;
                    string devPass = args.ContainsKey("password") ? args["password"]?.ToString() : null;
                    return CodesysOperations.PlcLogin(loginMode, delForeign, loginApp, devUser, devPass);
                case "inspect_online":
                    string inspectTarget = args.ContainsKey("target") ? args["target"]?.ToString() : "Device";
                    return CodesysOperations.InspectOnline(inspectTarget);
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

                case "add_trace_variable":
                    if (!args.ContainsKey("variable_name") || string.IsNullOrEmpty(args["variable_name"]?.ToString()))
                        throw new ArgumentException("Parameter 'variable_name' is required.");
                    string tVarName = args["variable_name"].ToString();
                    string tTraceName = args.ContainsKey("trace_name") ? args["trace_name"]?.ToString() : "Trace";
                    return CodesysOperations.AddTraceVariable(tVarName, tTraceName);

                case "get_trace_info":
                    string getTraceName = args.ContainsKey("trace_name") ? args["trace_name"]?.ToString() : "Trace";
                    return CodesysOperations.GetTraceInfo(getTraceName);

                case "batch_write_variables":
                    if (!args.ContainsKey("variables") || args["variables"] == null)
                        throw new ArgumentException("Parameter 'variables' (Dictionary/Object) is required.");
                    var varMap = new Dictionary<string, string>();
                    CodesysOnlineHelper.FlattenObject("", args["variables"], varMap);
                    bool bVerify = !args.ContainsKey("verify") || Convert.ToBoolean(args["verify"]);
                    bool bForce = args.ContainsKey("force") && Convert.ToBoolean(args["force"]);
                    return CodesysOperations.BatchWriteVariables(varMap, bVerify, bForce);

                case "write_struct":
                    if (!args.ContainsKey("struct_name") || string.IsNullOrEmpty(args["struct_name"]?.ToString()))
                        throw new ArgumentException("Parameter 'struct_name' is required.");
                    if (!args.ContainsKey("values") || args["values"] == null)
                        throw new ArgumentException("Parameter 'values' is required.");
                    string wsStructName = args["struct_name"].ToString();
                    bool wsVerify = !args.ContainsKey("verify") || Convert.ToBoolean(args["verify"]);
                    bool wsForce = args.ContainsKey("force") && Convert.ToBoolean(args["force"]);
                    return CodesysOperations.WriteStruct(wsStructName, args["values"], wsVerify, wsForce);

                case "read_struct":
                    if (!args.ContainsKey("struct_name") || string.IsNullOrEmpty(args["struct_name"]?.ToString()))
                        throw new ArgumentException("Parameter 'struct_name' is required.");
                    string rsStructName = args["struct_name"].ToString();
                    var rsMembers = new List<string>();
                    if (args.ContainsKey("members"))
                    {
                        var arr = args["members"] as System.Collections.IEnumerable;
                        if (arr != null)
                        {
                            foreach (var item in arr) if (item != null) rsMembers.Add(item.ToString());
                        }
                    }
                    return CodesysOperations.ReadStruct(rsStructName, rsMembers.Count > 0 ? rsMembers : null);

                case "read_array":
                    if (!args.ContainsKey("variable_name") || string.IsNullOrEmpty(args["variable_name"]?.ToString()))
                        throw new ArgumentException("Parameter 'variable_name' is required.");
                    string raVarName = args["variable_name"].ToString();
                    int raStart = args.ContainsKey("start_index") ? Convert.ToInt32(args["start_index"]) : 0;
                    int raCount = args.ContainsKey("count") ? Convert.ToInt32(args["count"]) : 50;
                    return CodesysOperations.ReadArray(raVarName, raStart, raCount);

                case "get_ui_indicator_status":
                    return CodesysOperations.GetUiIndicatorStatus();

                case "analyze_trace":
                    string atPath = args.ContainsKey("file_path") ? args["file_path"]?.ToString() : null;
                    string atCond = args.ContainsKey("filter_condition") ? args["filter_condition"]?.ToString() : null;
                    double atDeadband = args.ContainsKey("deadband") ? Convert.ToDouble(args["deadband"]) : 0.01;
                    return CodesysOperations.AnalyzeTrace(atPath, atDeadband, atCond);

                case "setup_plc_logger":
                    int slBuf = args.ContainsKey("buffer_size") ? Convert.ToInt32(args["buffer_size"]) : 64;
                    string slPou = args.ContainsKey("auto_inject_pou") ? args["auto_inject_pou"]?.ToString() : null;
                    return CodesysOperations.SetupPlcLogger(slBuf, slPou);

                case "read_plc_logs":
                    string rlTag = args.ContainsKey("tag_filter") ? args["tag_filter"]?.ToString() : null;
                    int rlLastN = args.ContainsKey("last_n") ? Convert.ToInt32(args["last_n"]) : 32;
                    bool rlClear = args.ContainsKey("clear_after_read") && Convert.ToBoolean(args["clear_after_read"]);
                    return CodesysOperations.ReadPlcLogs(rlTag, rlLastN, rlClear);
                case "setup_device_logger":
                    return CodesysOperations.SetupDeviceLogger();
                case "inject_code_log":
                    string icPou = args.ContainsKey("pou_name") ? args["pou_name"]?.ToString() : null;
                    string icPattern = args.ContainsKey("target_pattern") ? args["target_pattern"]?.ToString() : null;
                    string icMsg = args.ContainsKey("log_message") ? args["log_message"]?.ToString() : null;
                    int icLevel = args.ContainsKey("level") ? Convert.ToInt32(args["level"]) : 1;
                    bool icAfter = !args.ContainsKey("insert_after") || Convert.ToBoolean(args["insert_after"]);
                    return CodesysOperations.InjectCodeLog(icPou, icPattern, icMsg, icLevel, icAfter);
                case "read_device_log":
                    string rdFile = args.ContainsKey("file_path") ? args["file_path"]?.ToString() : null;
                    string rdSev = args.ContainsKey("severity") ? args["severity"]?.ToString() : null;
                    string rdKey = args.ContainsKey("keyword") ? args["keyword"]?.ToString() : null;
                    string rdComp = args.ContainsKey("component") ? args["component"]?.ToString() : null;
                    int rdMax = args.ContainsKey("max_entries") ? Convert.ToInt32(args["max_entries"]) : 50;
                    string rdSave = args.ContainsKey("save_to_path") ? args["save_to_path"]?.ToString() : null;
                    return CodesysOperations.ReadDeviceLog(rdFile, rdSev, rdKey, rdComp, rdMax, rdSave);

                case "diag_online":
                    var formsInfo = new List<string>();
                    try
                    {
                        foreach (System.Windows.Forms.Form f in System.Windows.Forms.Application.OpenForms)
                        {
                            var childTexts = new List<string>();
                            void WalkControls(System.Windows.Forms.Control parent)
                            {
                                foreach (System.Windows.Forms.Control c in parent.Controls)
                                {
                                    childTexts.Add($"{c.GetType().Name}(Name='{c.Name}', Text='{c.Text}')");
                                    if (c.HasChildren) WalkControls(c);
                                }
                            }
                            if (f.GetType().Name != "MainForm")
                            {
                                try { WalkControls(f); } catch { }
                                if (args.ContainsKey("cancel_dialog") && Convert.ToBoolean(args["cancel_dialog"]))
                                {
                                    f.BeginInvoke(new Action(() =>
                                    {
                                        try { f.DialogResult = System.Windows.Forms.DialogResult.Cancel; f.Close(); } catch { }
                                    }));
                                }
                            }
                            formsInfo.Add($"{f.GetType().FullName} (Text='{f.Text}', Visible={f.Visible}, Handle={f.IsHandleCreated}, InvokeReq={f.InvokeRequired}, Children=[{string.Join("; ", childTexts)}])");
                        }
                    }
                    catch (Exception ex) { formsInfo.Add("OpenForms error: " + ex.Message); }

                    var onlineMethods = new List<string>();
                    if (CodesysContext.Online != null)
                    {
                        foreach (var m in CodesysContext.Online.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                        {
                            var ps = new List<string>();
                            foreach (var p in m.GetParameters()) ps.Add($"{p.ParameterType.Name} {p.Name}");
                            onlineMethods.Add($"{m.Name}({string.Join(", ", ps)})");
                        }
                    }

                    string ffInfo = "null";
                    try
                    {
                        var ff = _3S.CoDeSys.Core.SystemInstances.FrameForm;
                        if (ff != null)
                        {
                            var props = new List<string>();
                            foreach (var p in ff.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                            {
                                try { props.Add($"{p.Name}={p.GetValue(ff, null)}"); } catch { }
                            }
                            ffInfo = $"{ff.GetType().FullName} [{string.Join(", ", props)}]";
                        }
                    }
                    catch (Exception ex) { ffInfo = "FrameForm error: " + ex.Message; }

                    var traceDiag = new List<string>();
                    try
                    {
                        var tObj = CodesysOperations.FindObjectByName("Trace");
                        if (tObj != null)
                        {
                            traceDiag.Add("TraceObj Type: " + tObj.GetType().FullName);
                            foreach (var p in tObj.GetType().GetProperties())
                            {
                                try { traceDiag.Add($"Prop {p.Name} = {p.GetValue(tObj, null)}"); } catch { }
                            }
                        }
                        else
                        {
                            traceDiag.Add("Trace object not found");
                        }
                    }
                    catch (Exception ex) { traceDiag.Add("TraceDiag Error: " + ex.Message); }

                    var devDiag = new List<string>();
                    try
                    {
                        var dev = CodesysOperations.FindObjectByName("Device");
                        if (dev != null && CodesysContext.Online != null)
                        {
                            var createOnlineDev = CodesysContext.Online.GetType().GetMethod("create_online_device");
                            if (createOnlineDev != null)
                            {
                                var oDev = createOnlineDev.Invoke(CodesysContext.Online, new object[] { dev });
                                if (oDev != null)
                                {
                                    devDiag.Add("oDev Type: " + oDev.GetType().FullName);
                                    try {
                                        var getConn = oDev.GetType().GetMethod("get_connected");
                                        devDiag.Add("oDev.get_connected() = " + getConn?.Invoke(oDev, null));
                                        var conn = oDev.GetType().GetMethod("connect");
                                        conn?.Invoke(oDev, null);
                                        devDiag.Add("oDev.connect() invoked -> now get_connected() = " + getConn?.Invoke(oDev, null));
                                    } catch (Exception ex) { devDiag.Add("connect err: " + (ex.InnerException?.Message ?? ex.Message)); }
                                    foreach (var m in oDev.GetType().GetMethods())
                                    {
                                        if (m.DeclaringType != typeof(object))
                                        {
                                            var pList = new List<string>();
                                            foreach (var p in m.GetParameters()) pList.Add(p.ParameterType.Name + " " + p.Name);
                                            devDiag.Add($"Method: {m.Name}({string.Join(", ", pList)})");
                                        }
                                    }

                                    if (args.ContainsKey("set_initial_user") && Convert.ToBoolean(args["set_initial_user"]))
                                    {
                                        try
                                        {
                                            var setInitUser = oDev.GetType().GetMethod("set_credentials_for_initial_user");
                                            setInitUser?.Invoke(oDev, new object[] { "admin", "Admin123", true, false });
                                            devDiag.Add("set_credentials_for_initial_user SUCCESS!");
                                        }
                                        catch (Exception uex)
                                        {
                                            devDiag.Add("set_credentials_for_initial_user ERR: " + uex.Message);
                                        }
                                    }
                                }
                            }

                            foreach (var m in dev.GetType().GetMethods())
                            {
                                if (m.Name.Contains("communication"))
                                {
                                    devDiag.Add($"DevCommMethod: {m.Name}");
                                    try
                                    {
                                        var comm = m.Invoke(dev, null);
                                        if (comm != null)
                                        {
                                            foreach (var p in comm.GetType().GetProperties())
                                            {
                                                try { devDiag.Add($"CommProp: {p.Name} = {p.GetValue(comm, null)}"); } catch { }
                                            }
                                        }
                                    }
                                    catch (Exception cex) { devDiag.Add($"DevCommCallErr: {cex.Message}"); }
                                }
                            }
                        }
                    }
                    catch (Exception ex) { devDiag.Add("DevDiag Error: " + ex.Message); }

                    var appDiag = new List<string>();
                    try
                    {
                        var onlineApp = CodesysOnlineHelper.GetCachedOnlineApp();
                        if (onlineApp != null)
                        {
                            appDiag.Add("onlineApp Type: " + onlineApp.GetType().FullName);
                            foreach (var p in onlineApp.GetType().GetProperties())
                            {
                                try { appDiag.Add($"Prop {p.Name} = {p.GetValue(onlineApp, null)}"); } catch (Exception pex) { appDiag.Add($"Prop {p.Name} Err = {pex.Message}"); }
                            }
                            foreach (var f in onlineApp.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                            {
                                try { appDiag.Add($"Field {f.Name} = {f.GetValue(onlineApp)}"); } catch (Exception fex) { appDiag.Add($"Field {f.Name} Err = {fex.Message}"); }
                            }
                        }
                    }
                    catch (Exception aex) { appDiag.Add("AppDiag Err: " + aex.Message); }

                    return new
                    {
                        pipeThreadId = Thread.CurrentThread.ManagedThreadId,
                        pipeApartment = Thread.CurrentThread.GetApartmentState().ToString(),
                        uiPresent = CodesysContext.System?.ui_present,
                        openFormsCount = System.Windows.Forms.Application.OpenForms.Count,
                        openForms = formsInfo,
                        onlineMethods,
                        traceDiag,
                        devDiag,
                        appDiag,
                        frameForm = ffInfo,
                        stackDiag = CodesysOnlineHelper.EnsureExecutionStack()
                    };

                case "hot_reload_dll":
                    string dllPath = args.ContainsKey("dll_path") && !string.IsNullOrEmpty(args["dll_path"]?.ToString())
                        ? args["dll_path"].ToString()
                        : @"C:\Users\vboxuser\Desktop\Repos\codesys-plugin\bin\Release\CodesysMcp.dll";
                    if (!File.Exists(dllPath))
                        throw new FileNotFoundException($"DLL not found at '{dllPath}'");
                    byte[] rawBytes = File.ReadAllBytes(dllPath);
                    var newAsm = Assembly.Load(rawBytes);
                    var bootstrapType = newAsm.GetType("CodesysMcp.ServerBootstrap");
                    if (bootstrapType == null)
                        throw new InvalidOperationException("CodesysMcp.ServerBootstrap not found in loaded assembly.");
                    var startMethod = bootstrapType.GetMethod("Start", new Type[] { typeof(object), typeof(object), typeof(object), typeof(string) });
                    if (startMethod == null)
                        throw new InvalidOperationException("ServerBootstrap.Start(projects, system, online, pipeName) not found.");

                    var projs = CodesysContext.Projects;
                    var sys = CodesysContext.RawSystem;
                    var onl = CodesysContext.Online;
                    var pipe = _pipeName;

                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        Thread.Sleep(150);
                        try
                        {
                            ServerBootstrap.Stop();
                            startMethod.Invoke(null, new object[] { projs, sys, onl, pipe });
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("[CodesysMcp] Hot reload failed: " + ex);
                        }
                    });
                    return new
                    {
                        reloaded = true,
                        dllPath,
                        bytes = rawBytes.Length,
                        timestamp = File.GetLastWriteTime(dllPath).ToString("yyyy-MM-dd HH:mm:ss")
                    };

                default:
                    throw new NotSupportedException($"Tool '{name}' is not recognized.");
            }
        }
    }
}
