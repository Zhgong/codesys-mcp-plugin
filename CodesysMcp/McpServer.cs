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

                    server = new NamedPipeServerStream(

                        _pipeName,

                        PipeDirection.InOut,

                        NamedPipeServerStream.MaxAllowedServerInstances,

                        PipeTransmissionMode.Byte,

                        PipeOptions.None);



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

                    string devUser = args.ContainsKey("username") ? args["username"]?.ToString() : null;

                    string devPass = args.ContainsKey("password") ? args["password"]?.ToString() : null;

                    return CodesysOperations.PlcLogin(loginMode, delForeign, loginApp, devUser, devPass);



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

                    description = "Get information and variable list from a CODESYS Trace object.",

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

                    name = "analyze_trace",

                    description = "Analyze exported CODESYS Trace file (.trace XML or .csv) offline to extract channel statistics, signal transitions, and condition matches without touching PLC runtime.",

                    inputSchema = new

                    {

                        type = "object",

                        properties = new

                        {

                            file_path = new { type = "string", description = "Path to exported .trace (XML) or .csv file on disk" },

                            filter_condition = new { type = "string", description = "Optional condition expression to search in trace (e.g. 'rDispForce >= 5000' or 'bHmiStop == 1')" },

                            deadband = new { type = "number", description = "Deadband threshold for continuous analog value change detection (defaults to 0.01)" }

                        },

                        required = new[] { "file_path" }

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
