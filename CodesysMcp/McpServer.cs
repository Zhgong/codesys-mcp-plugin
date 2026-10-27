using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
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
                }
            };
        }
    }
}
