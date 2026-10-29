using System;
using System.Collections;
using System.Collections.Generic;
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
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 6, \"method\": \"tools/call\", \"params\": {\"name\": \"plc_login\", \"arguments\": {\"mode\": \"try_online_change\", \"delete_foreign_apps\": true, \"username\": \"admin\", \"password\": \"1111\"}}}");
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
    }
}
