using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using CodesysMcp;

namespace CodesysMcpTests
{
    public class ProtocolTests
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
        public void TestMalformedJsonReturnsParseError()
        {
            var res = Call("{ this is invalid json !!!");
            Assert.IsNotNull(res, "Response should not be null for parse error");
            Assert.AreEqual("2.0", res["jsonrpc"]?.ToString());
            Assert.IsTrue(res.ContainsKey("error"), "Response should have 'error'");

            var err = res["error"] as Dictionary<string, object>;
            Assert.IsNotNull(err, "Error should be a dictionary");
            Assert.AreEqual(-32700, Convert.ToInt32(err["code"]), "Parse error code should be -32700");
        }

        [TestMethod]
        public void TestMissingMethodReturnsInvalidRequest()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 10}");
            Assert.IsNotNull(res);
            Assert.AreEqual("10", res["id"]?.ToString());
            Assert.IsTrue(res.ContainsKey("error"));

            var err = res["error"] as Dictionary<string, object>;
            Assert.AreEqual(-32600, Convert.ToInt32(err["code"]), "Invalid request code should be -32600");
        }

        [TestMethod]
        public void TestUnknownMethodReturnsMethodNotFound()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 11, \"method\": \"codesys/nonExistentMethod\"}");
            Assert.IsNotNull(res);
            Assert.AreEqual("11", res["id"]?.ToString());
            Assert.IsTrue(res.ContainsKey("error"));

            var err = res["error"] as Dictionary<string, object>;
            Assert.AreEqual(-32601, Convert.ToInt32(err["code"]), "Method not found code should be -32601");
            Assert.Contains("nonExistentMethod", err["message"]?.ToString());
        }

        [TestMethod]
        public void TestNotificationsInitializedReturnsNull()
        {
            object res = _server.ProcessJsonRpc("{\"jsonrpc\": \"2.0\", \"method\": \"notifications/initialized\"}");
            Assert.IsNull(res, "Notification must return null without producing a response");
        }

        [TestMethod]
        public void TestPingReturnsEmptyResult()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 42, \"method\": \"ping\"}");
            Assert.IsNotNull(res);
            Assert.AreEqual("42", res["id"]?.ToString());
            Assert.IsTrue(res.ContainsKey("result"));
            Assert.IsFalse(res.ContainsKey("error"));
        }

        [TestMethod]
        public void TestInitializeReturnsProtocolAndServerInfo()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 1, \"method\": \"initialize\"}");
            Assert.IsNotNull(res);
            Assert.AreEqual("1", res["id"]?.ToString());

            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result, "Result should exist");
            Assert.AreEqual("2024-11-05", result["protocolVersion"]?.ToString());

            var serverInfo = result["serverInfo"] as Dictionary<string, object>;
            Assert.IsNotNull(serverInfo);
            Assert.AreEqual("codesys-native-mcp", serverInfo["name"]?.ToString());
            Assert.IsTrue(serverInfo.ContainsKey("version"), "ServerInfo should include version");

            var capabilities = result["capabilities"] as Dictionary<string, object>;
            Assert.IsNotNull(capabilities);
            Assert.IsTrue(capabilities.ContainsKey("tools"), "Capabilities should advertise tools");
        }
    }
}
