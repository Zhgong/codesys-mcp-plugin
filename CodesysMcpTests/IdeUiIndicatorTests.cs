using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using System.Windows.Forms;
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
            Assert.IsTrue(parsed.ContainsKey("isPermanentlyVisible"), "Payload must contain isPermanentlyVisible");
            Assert.IsTrue(parsed.ContainsKey("hostControlVisible"), "Payload must contain hostControlVisible");
            Assert.IsTrue(parsed.ContainsKey("hasContextMenu"), "Payload must contain hasContextMenu");
        }

        [TestMethod]
        public void TestCreateContextMenuItems()
        {
            var menu = CodesysIdeUiHelper.CreateContextMenu();
            Assert.IsNotNull(menu, "Context menu should not be null");
            Assert.IsTrue(menu.Items.Count >= 4, "Context menu should have at least 4 items");

            var titles = new List<string>();
            foreach (ToolStripItem it in menu.Items)
            {
                if (!string.IsNullOrEmpty(it.Text))
                {
                    titles.Add(it.Text);
                }
            }

            Assert.IsTrue(titles.Exists(t => t.Contains("Copy")), "Menu must contain Copy item");
            Assert.IsTrue(titles.Exists(t => t.Contains("Diagnostics")), "Menu must contain Diagnostics item");
            Assert.IsTrue(titles.Exists(t => t.Contains("Log")), "Menu must contain Log item");
            Assert.IsTrue(titles.Exists(t => t.Contains("Re-anchor")), "Menu must contain Re-anchor item");
        }

        [TestMethod]
        public void TestEnsureIndicatorVisibleSafeInHeadless()
        {
            // Calling EnsureIndicatorVisible without WinForms host should not throw
            CodesysIdeUiHelper.EnsureIndicatorVisible();
            var info = CodesysIdeUiHelper.GetIndicatorInfo();
            Assert.IsNotNull(info);
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
