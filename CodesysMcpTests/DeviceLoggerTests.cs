using System;
using System.Collections;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using CodesysMcp;

namespace CodesysMcpTests
{
    public class DeviceLoggerTests
    {
        private McpServer _server = new McpServer();
        private JavaScriptSerializer _json = new JavaScriptSerializer();

        private Dictionary<string, object> Call(string requestJson)
        {
            object res = _server.ProcessJsonRpc(requestJson);
            if (res == null) return null;
            string serialized = _json.Serialize(res);
            return _json.Deserialize<Dictionary<string, object>>(serialized);
        }

        [TestMethod]
        public void TestGenerateLogMsgFunctionSource()
        {
            var (decl, impl) = PlcTraceAndLoggerHelper.GenerateLogMsgFunctionSource();
            
            Assert.IsNotNull(decl);
            Assert.IsNotNull(impl);
            Assert.IsTrue(decl.Contains("FUNCTION FC_LogMsg : BOOL"));
            Assert.IsTrue(decl.Contains("sMsg    : STRING(255);"));
            Assert.IsTrue(decl.Contains("nLevel  : INT := 1;"));
            Assert.IsTrue(impl.Contains("CmpLog.LogAdd"));
            Assert.IsTrue(impl.Contains("16#FFFFFFFF"));
            Assert.IsTrue(impl.Contains("ADR(sMsg)"));
            Assert.IsTrue(impl.Contains("FC_LogMsg := TRUE;"));
        }

        [TestMethod]
        public void TestOfflineValidationFailsGracefully()
        {
            var resSafe = PlcTraceAndLoggerHelper.ReadDeviceLogOfflineSafe();
            Assert.IsFalse(resSafe.success);
            Assert.IsTrue(resSafe.message.Contains("offline") || resSafe.message.Contains("active"));

            var setupSafe = PlcTraceAndLoggerHelper.SetupDeviceLogger();
            Assert.IsFalse(setupSafe.success);
        }

        [TestMethod]
        public void TestMcpDeviceLoggerToolValidation()
        {
            // Call inject_code_log without required arguments
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 1, \"method\": \"tools/call\", \"params\": {\"name\": \"inject_code_log\", \"arguments\": {}}}");
            Assert.IsNotNull(res);
            var result = res["result"] as Dictionary<string, object>;
            Assert.IsTrue(Convert.ToBoolean(result["isError"]));

            // Call read_device_log offline
            var resRead = Call("{\"jsonrpc\": \"2.0\", \"id\": 2, \"method\": \"tools/call\", \"params\": {\"name\": \"read_device_log\", \"arguments\": {}}}");
            Assert.IsNotNull(resRead);
            var resReadResult = resRead["result"] as Dictionary<string, object>;
            Assert.IsNotNull(resReadResult);
        }
    }
}
