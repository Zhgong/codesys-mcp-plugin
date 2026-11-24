using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using CodesysMcp;

namespace CodesysMcpTests
{
    public class OnlineTests
    {
        private readonly McpServer _server = new McpServer();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        private Dictionary<string, object> Call(string requestJson)
        {
            object res = _server.ProcessJsonRpc(requestJson);
            if (res == null) return null;
            string serialized = _json.Serialize(res);
            return _json.Deserialize<Dictionary<string, object>>(serialized);
        }

        [TestMethod]
        public void TestGetOnlineStatusWhenOffline()
        {
            CodesysOnlineHelper.ClearSession();
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 1, \"method\": \"tools/call\", \"params\": {\"name\": \"get_online_status\", \"arguments\": {}}}");
            Assert.IsNotNull(res);

            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsFalse(Convert.ToBoolean(result["isError"]));

            var content = result["content"] as ArrayList;
            var firstItem = content[0] as Dictionary<string, object>;
            var status = _json.Deserialize<Dictionary<string, object>>(firstItem["text"]?.ToString());

            Assert.IsFalse(Convert.ToBoolean(status["isLoggedIn"]));
            Assert.AreEqual("Offline", status["applicationState"]?.ToString());
        }

        [TestMethod]
        public void TestPlcControlWithoutActionFails()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 2, \"method\": \"tools/call\", \"params\": {\"name\": \"plc_control\", \"arguments\": {}}}");
            Assert.IsNotNull(res);

            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsTrue(Convert.ToBoolean(result["isError"]));
        }

        [TestMethod]
        public void TestWriteVariableWithoutNameFails()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 3, \"method\": \"tools/call\", \"params\": {\"name\": \"write_variable\", \"arguments\": {\"value\": \"123\"}}}");
            Assert.IsNotNull(res);

            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsTrue(Convert.ToBoolean(result["isError"]));
        }

        [TestMethod]
        public void TestReadVariablesEmptyReturnsZero()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 4, \"method\": \"tools/call\", \"params\": {\"name\": \"read_variables\", \"arguments\": {\"expressions\": []}}}");
            Assert.IsNotNull(res);

            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsFalse(Convert.ToBoolean(result["isError"]));

            var content = result["content"] as ArrayList;
            var firstItem = content[0] as Dictionary<string, object>;
            var readRes = _json.Deserialize<Dictionary<string, object>>(firstItem["text"]?.ToString());
            Assert.IsTrue(Convert.ToBoolean(readRes["success"]));
        }

        [TestMethod]
        public void TestPlcLogoutWhenAlreadyOfflineSucceeds()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 5, \"method\": \"tools/call\", \"params\": {\"name\": \"plc_logout\", \"arguments\": {}}}");
            Assert.IsNotNull(res);

            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsFalse(Convert.ToBoolean(result["isError"]));
        }

        [TestMethod]
        public void TestPlcLoginWithParametersAndCredentialsWhenOfflineFailsGracefully()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 6, \"method\": \"tools/call\", \"params\": {\"name\": \"plc_login\", \"arguments\": {\"mode\": \"try_online_change\", \"delete_foreign_apps\": true, \"username\": \"admin\", \"password\": \"<PASSWORD>\"}}}");
            Assert.IsNotNull(res);

            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsTrue(Convert.ToBoolean(result["isError"]), "isError should be true when calling plc_login without active project");
            var content = result["content"] as ArrayList;
            var firstItem = content[0] as Dictionary<string, object>;
            Assert.IsTrue(firstItem["text"]?.ToString().Length > 0);
        }

        [TestMethod]
        public void TestEnsureExecutionStackWhenOnlineIsNull()
        {
            string msg = CodesysOnlineHelper.EnsureExecutionStack();
            Assert.IsNotNull(msg);
            Assert.IsTrue(msg.Contains("null") || msg.Contains("Count"));
        }

        [TestMethod]
        public void TestNormalizeIecValueHandlesVariousPrefixes()
        {
            Assert.AreEqual("5250", CodesysOnlineHelper.NormalizeIecValue("REAL#5250"));
            Assert.AreEqual("123.456", CodesysOnlineHelper.NormalizeIecValue("LREAL#123.456"));
            Assert.AreEqual("42", CodesysOnlineHelper.NormalizeIecValue("INT#42"));
            Assert.AreEqual("TRUE", CodesysOnlineHelper.NormalizeIecValue("BOOL#TRUE"));
            Assert.AreEqual("-999", CodesysOnlineHelper.NormalizeIecValue("DINT#-999"));
            Assert.AreEqual("100", CodesysOnlineHelper.NormalizeIecValue("100"));
            Assert.AreEqual("", CodesysOnlineHelper.NormalizeIecValue(""));
            Assert.AreEqual("", CodesysOnlineHelper.NormalizeIecValue(null));
        }

        [TestMethod]
        public void TestValuesMatchSupportsTypeEquivalenceAndTolerance()
        {
            // Numeric and IEC type prefix matching
            Assert.IsTrue(CodesysOnlineHelper.ValuesMatch("REAL#5250", "5250.0"));
            Assert.IsTrue(CodesysOnlineHelper.ValuesMatch("5250", "REAL#5250.0"));
            Assert.IsTrue(CodesysOnlineHelper.ValuesMatch("INT#10", "10"));
            Assert.IsTrue(CodesysOnlineHelper.ValuesMatch("12.345", "12.3454")); // delta < 0.001
            Assert.IsFalse(CodesysOnlineHelper.ValuesMatch("12.345", "12.355"));

            // Boolean matching
            Assert.IsTrue(CodesysOnlineHelper.ValuesMatch("BOOL#TRUE", "true"));
            Assert.IsTrue(CodesysOnlineHelper.ValuesMatch("BOOL#FALSE", "False"));
            Assert.IsFalse(CodesysOnlineHelper.ValuesMatch("BOOL#TRUE", "false"));

            // String matching
            Assert.IsTrue(CodesysOnlineHelper.ValuesMatch("'hello'", "'hello'"));
            Assert.IsFalse(CodesysOnlineHelper.ValuesMatch("'hello'", "'world'"));
        }

        [TestMethod]
        public void TestAddTraceVariableWhenNoProjectOpenFailsGracefully()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 7, \"method\": \"tools/call\", \"params\": {\"name\": \"add_trace_variable\", \"arguments\": {\"variable_name\": \"MOTION_PRG.rDispPos\"}}}");
            Assert.IsNotNull(res);

            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsTrue(Convert.ToBoolean(result["isError"]));
            var content = result["content"] as ArrayList;
            var firstItem = content[0] as Dictionary<string, object>;
            Assert.IsTrue(firstItem["text"]?.ToString().Length > 0);
        }

        [TestMethod]
        public void TestGetTraceInfoWhenNoProjectOpenFailsGracefully()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 8, \"method\": \"tools/call\", \"params\": {\"name\": \"get_trace_info\", \"arguments\": {}}}");
            Assert.IsNotNull(res);

            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsTrue(Convert.ToBoolean(result["isError"]));
            var content = result["content"] as ArrayList;
            var firstItem = content[0] as Dictionary<string, object>;
            Assert.IsTrue(firstItem["text"]?.ToString().Length > 0);
        }

        [TestMethod]
        public void TestCodesysScriptDriverOnlineContractIfInstalled()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (string.IsNullOrEmpty(programFiles) || !Directory.Exists(programFiles)) return;

            string codesysRoot = Directory.GetDirectories(programFiles, "CODESYS*")
                .Select(d => Path.Combine(d, "CODESYS"))
                .FirstOrDefault(Directory.Exists);
            if (codesysRoot == null) return;

            string onlinePlugin = Directory.GetFiles(codesysRoot, "ScriptDriverOnline.plugin.dll", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (onlinePlugin == null) return;

            string commonDir = Path.Combine(codesysRoot, "Common");
            string pluginsDir = Path.Combine(codesysRoot, "PlugIns");
            string additionalDir = Path.Combine(codesysRoot, "AdditionalFolders");
            string lacDir = Path.Combine(codesysRoot, "LacBinaries");
            var resolvedCache = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
            var resolving = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            ResolveEventHandler resolver = (sender, args) =>
            {
                try
                {
                    if (resolvedCache.TryGetValue(args.Name, out var cached)) return cached;
                    var reqName = new AssemblyName(args.Name);
                    if (reqName.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) return null;
                    if (resolving.Contains(reqName.Name)) return null;
                    resolving.Add(reqName.Name);
                    try
                    {
                        string fileName = reqName.Name + ".dll";
                        var candidates = new List<string>();
                        string inCommon = Path.Combine(commonDir, fileName);
                        if (File.Exists(inCommon)) candidates.Add(inCommon);
                        if (Directory.Exists(lacDir)) candidates.AddRange(Directory.GetFiles(lacDir, fileName, SearchOption.AllDirectories));
                        if (Directory.Exists(pluginsDir)) candidates.AddRange(Directory.GetFiles(pluginsDir, fileName, SearchOption.AllDirectories));
                        if (Directory.Exists(additionalDir)) candidates.AddRange(Directory.GetFiles(additionalDir, fileName, SearchOption.AllDirectories));

                        string best = null;
                        if (reqName.Version != null)
                        {
                            best = candidates.FirstOrDefault(path =>
                            {
                                try { return AssemblyName.GetAssemblyName(path).Version == reqName.Version; }
                                catch { return false; }
                            });
                        }
                        best = best ?? candidates.FirstOrDefault();
                        if (best != null)
                        {
                            Assembly loaded = Assembly.LoadFrom(best);
                            resolvedCache[args.Name] = loaded;
                            return loaded;
                        }
                    }
                    finally
                    {
                        resolving.Remove(reqName.Name);
                    }
                }
                catch { }
                return null;
            };

            AppDomain.CurrentDomain.AssemblyResolve += resolver;
            try
            {
                var asm = Assembly.LoadFrom(onlinePlugin);
                Type onlineAppType = asm.GetType("_3S.CoDeSys.ScriptDriverOnline.OnlineApplication", false);
                Assert.IsNotNull(onlineAppType, "OnlineApplication must exist in ScriptDriverOnline.plugin.dll");
                Assert.IsNotNull(onlineAppType.GetMethod("read_value", new[] { typeof(string) }));
                Assert.IsNotNull(onlineAppType.GetMethod("read_values", new[] { typeof(string[]) }), "OnlineApplication.read_values(string[]) must exist");
                Assert.IsNotNull(onlineAppType.GetMethod("set_prepared_value", new[] { typeof(string), typeof(string) }));
                Assert.IsNotNull(onlineAppType.GetMethod("write_prepared_values"));
                Assert.IsNotNull(onlineAppType.GetMethod("force_prepared_values"));
                Assert.IsNotNull(onlineAppType.GetMethod("login"));
                Assert.IsNotNull(onlineAppType.GetMethod("logout"));
                Assert.IsNotNull(
                    onlineAppType.GetField("_onlineVarRefs", BindingFlags.Instance | BindingFlags.NonPublic),
                    "OnlineApplication._onlineVarRefs must exist for stale handle eviction");

                Type scriptOnlineType = asm.GetType("_3S.CoDeSys.ScriptDriverOnline.ScriptOnline", false);
                Assert.IsNotNull(scriptOnlineType, "ScriptOnline must exist in ScriptDriverOnline.plugin.dll");
                Assert.IsNotNull(scriptOnlineType.GetMethod("create_online_device"), "ScriptOnline.create_online_device must exist");

                Type scriptOnlineDevType = asm.GetType("_3S.CoDeSys.ScriptDriverOnline.ScriptOnlineDevice", false);
                Assert.IsNotNull(scriptOnlineDevType, "ScriptOnlineDevice must exist in ScriptDriverOnline.plugin.dll");
                Assert.IsNotNull(
                    scriptOnlineDevType.GetField("OnlineDevice", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
                    "ScriptOnlineDevice.OnlineDevice field must exist");
            }
            finally
            {
                AppDomain.CurrentDomain.AssemblyResolve -= resolver;
            }
        }

        private class FakeVarRef
        {
            public int ReleaseCalls;
            public void Release() { ReleaseCalls++; }
        }

        private class FakeOnlineAppWithStaleRefs
        {
            private readonly Dictionary<string, object> _onlineVarRefs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            public bool is_logged_in => true;
            public string application_state => "run";
            public string operation_state => "1";

            public void SeedVarRef(string expr, FakeVarRef r) => _onlineVarRefs[expr] = r;
            public bool ContainsVarRef(string expr) => _onlineVarRefs.ContainsKey(expr);

            public IList<string> read_values(string[] expressions)
            {
                var list = new List<string>(expressions.Length);
                foreach (var e in expressions)
                {
                    list.Add(_onlineVarRefs.ContainsKey(e)
                        ? "<Error: 内部错误:无效的变量引用>"
                        : "REAL#4500");
                }
                return list;
            }

            public string read_value(string expr)
            {
                return _onlineVarRefs.ContainsKey(expr)
                    ? "<Error: 内部错误:无效的变量引用>"
                    : "REAL#4500";
            }

            public void set_prepared_value(string expr, string val) { }
            public void write_prepared_values() { }
        }

        [TestMethod]
        public void TestStaleOnlineVarRefsAutoEvictedAndRetriedOnReadAndWrite()
        {
            var fakeApp = new FakeOnlineAppWithStaleRefs();
            var r1 = new FakeVarRef();
            var r2 = new FakeVarRef();
            var r3 = new FakeVarRef();

            CodesysOnlineHelper.SetCachedOnlineAppForTesting(fakeApp);
            try
            {
                fakeApp.SeedVarRef("MOTION_PRG.rDispPos", r1);
                fakeApp.SeedVarRef("MOTION_PRG.rDispForce", r2);

                var readRes = CodesysOnlineHelper.ReadVariables(new List<string> { "MOTION_PRG.rDispPos", "MOTION_PRG.rDispForce" });
                Assert.IsTrue(readRes.success);
                Assert.AreEqual("REAL#4500", readRes.values["MOTION_PRG.rDispPos"]);
                Assert.AreEqual("REAL#4500", readRes.values["MOTION_PRG.rDispForce"]);
                Assert.AreEqual(1, r1.ReleaseCalls, "Stale handle r1 must be released");
                Assert.AreEqual(1, r2.ReleaseCalls, "Stale handle r2 must be released");
                Assert.IsFalse(fakeApp.ContainsVarRef("MOTION_PRG.rDispPos"));
                Assert.IsFalse(fakeApp.ContainsVarRef("MOTION_PRG.rDispForce"));

                fakeApp.SeedVarRef("MOTION_PRG.PressRecipe.rTargetForce", r3);
                var batchRes = CodesysOnlineHelper.BatchWriteVariables(
                    new Dictionary<string, string> { { "MOTION_PRG.PressRecipe.rTargetForce", "4500.0" } },
                    verify: true);
                Assert.IsTrue(batchRes.allVerified, "Batch write verification should retry after evicting stale var ref");
                Assert.AreEqual(1, r3.ReleaseCalls, "Stale handle r3 must be released on batch verify");
                Assert.IsFalse(fakeApp.ContainsVarRef("MOTION_PRG.PressRecipe.rTargetForce"));
            }
            finally
            {
                CodesysOnlineHelper.SetCachedOnlineAppForTesting(null);
            }
        }
    }
}
