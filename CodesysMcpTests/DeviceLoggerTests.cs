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
            Assert.IsTrue(impl.Contains("CmpLog.LogAdd2"));
            Assert.IsTrue(impl.Contains("LOG_STD_LOGGER"));
            Assert.IsTrue(impl.Contains("pszInfo    := sMsg"));
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

        [TestMethod]
        public void TestParseDeviceLogXmlContent()
        {
            string sampleXml = @"<?xml version=""1.0"" encoding=""iso-8859-1""?>
<LoggerEntries>
  <Logger name=""PlcLog"">
    <Entry severity=""information"" component=""CM"" user=""nobody"" timestamp=""2026/10/30 8:04:35"" infoId=""0"">HMI Start Command Received: Starting press cycle</Entry>
    <Entry severity=""error"" component=""CmpSupervisor"" user=""nobody"" timestamp=""2026/10/30 8:04:34"" infoId=""0"">Description=Scheduler Tick</Entry>
    <Entry severity=""exception"" component=""CmpIecTask"" user=""nobody"" timestamp=""2026/10/29 22:19:50"" infoId=""3"">*EXCEPTION* [AccessViolation] occurred: App=[Application], Task=[Task]</Entry>
  </Logger>
</LoggerEntries>";

            var all = PlcTraceAndLoggerHelper.ParseDeviceLogXml(sampleXml);
            Assert.IsTrue(all.success);
            Assert.AreEqual(3, all.totalEntries);
            Assert.AreEqual(1, all.infoCount);
            Assert.AreEqual(1, all.errorCount);
            Assert.AreEqual(1, all.exceptionCount);
            Assert.AreEqual(3, all.entries.Count);

            // Filter by keyword
            var filterKw = PlcTraceAndLoggerHelper.ParseDeviceLogXml(sampleXml, null, "Starting press cycle");
            Assert.IsTrue(filterKw.success);
            Assert.AreEqual(1, filterKw.entries.Count);
            Assert.AreEqual("CM", filterKw.entries[0].component);
            Assert.AreEqual("information", filterKw.entries[0].severity);
            Assert.IsTrue(filterKw.entries[0].message.Contains("Starting press cycle"));

            // Filter by component
            var filterComp = PlcTraceAndLoggerHelper.ParseDeviceLogXml(sampleXml, null, null, "CmpSupervisor");
            Assert.IsTrue(filterComp.success);
            Assert.AreEqual(1, filterComp.entries.Count);
            Assert.AreEqual("error", filterComp.entries[0].severity);

            // Filter by severity
            var filterSev = PlcTraceAndLoggerHelper.ParseDeviceLogXml(sampleXml, "exception");
            Assert.IsTrue(filterSev.success);
            Assert.AreEqual(1, filterSev.entries.Count);
            Assert.AreEqual("CmpIecTask", filterSev.entries[0].component);
        }

        [TestMethod]
        public void TestMcpReadDeviceLogWithXmlFile()
        {
            string tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test_device_log_" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                string sampleXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<LoggerEntries>
  <Logger name=""PlcLog"">
    <Entry severity=""information"" component=""CM"" user=""nobody"" timestamp=""2026/10/30 8:04:35"" infoId=""0"">HMI Start Command Received: Starting press cycle</Entry>
    <Entry severity=""information"" component=""CM"" user=""nobody"" timestamp=""2026/10/30 8:04:36"" infoId=""0"">HMI Reset Command Received: Fault cleared</Entry>
    <Entry severity=""error"" component=""CmpSupervisor"" user=""nobody"" timestamp=""2026/10/30 8:05:00"" infoId=""0"">Watchdog Warning</Entry>
  </Logger>
</LoggerEntries>";
                System.IO.File.WriteAllText(tempFile, sampleXml);

                string escapedPath = tempFile.Replace(@"\", @"\\");
                string req = "{\"jsonrpc\": \"2.0\", \"id\": 10, \"method\": \"tools/call\", \"params\": {\"name\": \"read_device_log\", \"arguments\": {\"file_path\": \"" + escapedPath + "\", \"keyword\": \"HMI Reset\"}}}";
                var res = Call(req);
                Assert.IsNotNull(res);

                var result = res["result"] as Dictionary<string, object>;
                Assert.IsNotNull(result);
                var content = result["content"] as ArrayList;
                Assert.IsNotNull(content);
                var textObj = content[0] as Dictionary<string, object>;
                string text = textObj["text"].ToString();

                Assert.IsTrue(text.Contains("Fault cleared"));
                Assert.IsTrue(text.Contains("HMI Reset"));
            }
            finally
            {
                if (System.IO.File.Exists(tempFile))
                {
                    try { System.IO.File.Delete(tempFile); } catch { }
                }
            }
        }

        [TestMethod]
        public void TestParseActualDesktopLogXmlIfPresent()
        {
            string desktopLog = @"C:\Users\vboxuser\Desktop\log.xml";
            if (!System.IO.File.Exists(desktopLog)) return;

            // 1. Keyword search
            var resKw = PlcTraceAndLoggerHelper.ParseDeviceLogXml(desktopLog, null, "HMI Start");
            Assert.IsTrue(resKw.success);
            Assert.IsTrue(resKw.entries.Count > 0);
            Assert.IsTrue(resKw.entries[0].message.Contains("HMI Start Command Received"));
            Assert.AreEqual("CM", resKw.entries[0].component);

            // 2. Statistics check
            Assert.IsTrue(resKw.totalEntries > 1000);
            Assert.IsTrue(resKw.errorCount > 0);
            Assert.IsTrue(resKw.exceptionCount >= 2);

            // 3. MCP tool call with file_path
            string escapedPath = desktopLog.Replace(@"\", @"\\");
            string req = "{\"jsonrpc\": \"2.0\", \"id\": 20, \"method\": \"tools/call\", \"params\": {\"name\": \"read_device_log\", \"arguments\": {\"file_path\": \"" + escapedPath + "\", \"keyword\": \"HMI Start\"}}}";
            var mcpRes = Call(req);
            Assert.IsNotNull(mcpRes);
            var result = mcpRes["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            var content = result["content"] as ArrayList;
            Assert.IsNotNull(content);
            var textObj = content[0] as Dictionary<string, object>;
            string text = textObj["text"].ToString();
            Assert.IsTrue(text.Contains("HMI Start Command Received: Starting press cycle"));
        }
    }
}
