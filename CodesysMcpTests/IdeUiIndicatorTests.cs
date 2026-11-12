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

        [TestMethod]
        public void TestGetServerVersionReturnsAssemblyVersion()
        {
            string version = CodesysIdeUiHelper.GetServerVersion();
            Assert.IsNotNull(version, "Server version should not be null");
            Assert.AreEqual("1.2.5", version, "Version should match assembly and VERSION file (1.2.5)");
        }

        [TestMethod]
        public void TestTooltipTextContainsDynamicVersion()
        {
            string tooltip = CodesysIdeUiHelper.BuildTooltipText();
            Assert.IsNotNull(tooltip, "Tooltip text should not be null");
            Assert.IsTrue(tooltip.Contains("CODESYS MCP Server v" + CodesysIdeUiHelper.GetServerVersion()), "Tooltip should contain dynamic version");
            Assert.IsFalse(tooltip.Contains("v1.1.0"), "Tooltip should not contain stale hardcoded v1.1.0");
        }

        [TestMethod]
        public void TestDiagnosticsReportContainsDynamicVersionAndToolCount()
        {
            string diag = CodesysIdeUiHelper.BuildDiagnosticsText();
            Assert.IsNotNull(diag, "Diagnostics text should not be null");
            Assert.IsTrue(diag.Contains("CODESYS MCP Native Server v" + CodesysIdeUiHelper.GetServerVersion()), "Diagnostics should contain dynamic version");
            Assert.IsFalse(diag.Contains("v1.1.0"), "Diagnostics should not contain stale hardcoded v1.1.0");
            Assert.IsTrue(diag.Contains(McpServer.RegisteredToolCount + " MCP Tools Available"), "Diagnostics should contain dynamic registered tool count");
            Assert.IsFalse(diag.Contains("30 MCP Tools Available"), "Diagnostics should not contain stale hardcoded 30 tool count");
        }

        [TestMethod]
        public void TestResolveSessionLogPathWithDefaultAndEnvVar()
        {
            // Test 1: Env var override
            string customPath = @"C:\custom\test\mcp_session.log";
            Environment.SetEnvironmentVariable("CODESYS_MCP_LOG_FILE", customPath);
            try
            {
                string resolved = CodesysIdeUiHelper.ResolveSessionLogPath();
                Assert.AreEqual(customPath, resolved);
            }
            finally
            {
                Environment.SetEnvironmentVariable("CODESYS_MCP_LOG_FILE", null);
            }

            // Test 2: Standard user profile path
            string defaultResolved = CodesysIdeUiHelper.ResolveSessionLogPath();
            Assert.IsNotNull(defaultResolved);
            Assert.IsTrue(defaultResolved.EndsWith("session_debug.log", StringComparison.OrdinalIgnoreCase));
            string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userHome))
            {
                string expectedDir = System.IO.Path.Combine(userHome, ".codesys-mcp-plugin");
                Assert.IsTrue(defaultResolved.StartsWith(expectedDir, StringComparison.OrdinalIgnoreCase) || System.IO.File.Exists(defaultResolved));
            }
        }

        [TestMethod]
        public void TestServerBootstrapStartupBannerContainsVersionAndToolCount()
        {
            string banner = ServerBootstrap.BuildStartupBanner("codesys_mcp_pipe");
            Assert.IsNotNull(banner);
            Assert.IsTrue(banner.Contains("v" + CodesysIdeUiHelper.GetServerVersion()), "Banner must contain dynamic version");
            Assert.IsTrue(banner.Contains(McpServer.RegisteredToolCount + " Tools Available"), "Banner must contain live tool count");
            Assert.IsFalse(banner.Contains("v1.1.0"), "Banner must not contain stale v1.1.0");
        }

        [TestMethod]
        public void TestDiagnosticsDialogCloseButtonClosesForm()
        {
            using (var form = CodesysIdeUiHelper.CreateDiagnosticsDialog())
            {
                Assert.IsNotNull(form, "Diagnostics form must not be null");
                Assert.IsNotNull(form.AcceptButton, "Form should have AcceptButton configured");
                Assert.IsNotNull(form.CancelButton, "Form should have CancelButton configured for Escape key support");

                Control[] matching = form.Controls.Find("btnClose", true);
                Assert.IsTrue(matching != null && matching.Length > 0, "Form must contain button with Name 'btnClose'");

                var btnClose = matching[0] as Button;
                Assert.IsNotNull(btnClose, "btnClose must be a Button control");
                Assert.AreEqual("Close", btnClose.Text);

                bool closed = false;
                form.FormClosed += (s, e) => { closed = true; };

                form.Show();
                Assert.IsTrue(form.Visible, "Form should be visible after Show()");

                // Simulate user clicking Close button
                btnClose.PerformClick();
                Application.DoEvents();

                Assert.IsTrue(closed || !form.Visible || form.IsDisposed, "Clicking Close button must close form");
            }
        }

        [TestMethod]
        public void TestDiagnosticsDialogEscapeKeyClosesForm()
        {
            using (var form = CodesysIdeUiHelper.CreateDiagnosticsDialog())
            {
                Assert.IsTrue(form.KeyPreview, "KeyPreview must be true to capture Escape key");

                bool closed = false;
                form.FormClosed += (s, e) => { closed = true; };

                form.Show();
                Assert.IsTrue(form.Visible, "Form should be visible after Show()");

                // Simulate user triggering CancelButton (Esc)
                form.CancelButton.PerformClick();
                Application.DoEvents();

                Assert.IsTrue(closed || !form.Visible || form.IsDisposed, "Triggering CancelButton (Esc) must close form");
            }
        }
    }
}
