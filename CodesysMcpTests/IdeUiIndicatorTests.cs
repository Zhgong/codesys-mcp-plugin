using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using CodesysMcp;

namespace CodesysMcpTests
{
    public class IdeUiIndicatorTests
    {
        private readonly McpServer _server = new McpServer();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        [TestMethod]
        public void TestGetIndicatorInfoStructure()
        {
            var info = CodesysIdeUiHelper.GetIndicatorInfo();
            Assert.IsNotNull(info, "Indicator info should not be null");
            Assert.IsNotNull(info.status, "Status string should not be null");
            Assert.IsNotNull(info.color, "Color string should not be null");
            Assert.IsTrue(info.color.StartsWith("#"), "Color should be in hex format #RRGGBB");
            Assert.AreEqual(7, info.color.Length, "Hex color should be 7 characters long");
            Assert.IsNotNull(info.pipeName, "Pipe name should not be null");
        }

        [TestMethod]
        public void TestIndicatorStatusTransitions()
        {
            // Initial state check
            CodesysIdeUiHelper.UpdateStatus(McpServerStatus.Online);
            var info = CodesysIdeUiHelper.GetIndicatorInfo();
            Assert.AreEqual("Online", info.status);
            Assert.AreEqual(McpServerStatus.Online, CodesysIdeUiHelper.CurrentStatus);

            // Busy state with tool name
            int beforeRequests = CodesysIdeUiHelper.TotalRequests;
            CodesysIdeUiHelper.UpdateStatus(McpServerStatus.Busy, "read_pou");
            info = CodesysIdeUiHelper.GetIndicatorInfo();
            Assert.AreEqual("Busy", info.status);
            Assert.AreEqual("read_pou", info.activeTool);
            Assert.AreEqual(beforeRequests + 1, CodesysIdeUiHelper.TotalRequests);

            // Error state
            CodesysIdeUiHelper.UpdateStatus(McpServerStatus.Error, null, "Gateway disconnect timeout");
            info = CodesysIdeUiHelper.GetIndicatorInfo();
            Assert.AreEqual("Error", info.status);
            Assert.AreEqual("Gateway disconnect timeout", info.lastError);

            // Reset back to Online
            CodesysIdeUiHelper.UpdateStatus(McpServerStatus.Online);
            info = CodesysIdeUiHelper.GetIndicatorInfo();
            Assert.AreEqual("Online", info.status);
            Assert.IsNull(info.activeTool);
        }

        [TestMethod]
        public void TestMcpServerGetUiIndicatorStatusTool()
        {
            string reqJson = "{\"jsonrpc\": \"2.0\", \"id\": 99, \"method\": \"tools/call\", \"params\": {\"name\": \"get_ui_indicator_status\", \"arguments\": {}}}";
            object res = _server.ProcessJsonRpc(reqJson);
            Assert.IsNotNull(res, "Tool response should not be null");

            string serialized = _json.Serialize(res);
            var map = _json.Deserialize<Dictionary<string, object>>(serialized);

            Assert.IsTrue(map.ContainsKey("result"), "Response must contain 'result'");
            var result = map["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result, "Result should be a dictionary");

            var contentList = result["content"] as System.Collections.ArrayList;
            Assert.IsNotNull(contentList, "Result should have content array");
            Assert.IsTrue(contentList.Count > 0, "Content array should not be empty");

            var contentObj = contentList[0] as Dictionary<string, object>;
            string text = contentObj["text"]?.ToString();
            Assert.IsNotNull(text, "Content text should not be null");

            var parsed = _json.Deserialize<Dictionary<string, object>>(text);
            Assert.IsTrue(parsed.ContainsKey("status"), "Payload must contain status");
            Assert.IsTrue(parsed.ContainsKey("color"), "Payload must contain color");
            Assert.IsTrue(parsed.ContainsKey("totalRequests"), "Payload must contain totalRequests");
            Assert.IsTrue(parsed.ContainsKey("pipeName"), "Payload must contain pipeName");
        }

        [TestMethod]
        public void TestRemoveStatusIndicatorOffline()
        {
            CodesysIdeUiHelper.RemoveStatusIndicator();
            var info = CodesysIdeUiHelper.GetIndicatorInfo();
            Assert.AreEqual("Offline", info.status);
            Assert.AreEqual(McpServerStatus.Offline, CodesysIdeUiHelper.CurrentStatus);
        }
    }
}
