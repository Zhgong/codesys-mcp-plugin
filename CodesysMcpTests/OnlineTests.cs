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
    }
}
