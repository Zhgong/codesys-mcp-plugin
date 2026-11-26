using System;
using System.Collections;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using CodesysMcp;

namespace CodesysMcpTests
{
    public class IdeVersionMatrixTests
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
        public void TestGetIdeEnvironmentInfo_ParsesProfileVersionAndUiMode()
        {
            var info = CodesysIdeUiHelper.GetIdeEnvironmentInfo(
                cmdArgsOverride: new[] { "CODESYS.exe", "--profile=CODESYS V3.5 SP20 Patch 5" },
                exePathOverride: @"C:\Program Files\CODESYS 3.5.20.50\CODESYS\Common\CODESYS.exe");

            Assert.IsNotNull(info);
            Assert.AreEqual("3.5.20.50", info.ideVersion, "Should extract CODESYS version from path or file version");
            Assert.AreEqual("CODESYS V3.5 SP20 Patch 5", info.ideProfile, "Should parse --profile argument");
            Assert.IsNotNull(info.ideExePath);
            Assert.IsTrue(info.ideExePath.EndsWith("CODESYS.exe", StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(CodesysIdeUiHelper.GetServerVersion(), info.mcpServerVersion);
            Assert.AreEqual("GUI", info.uiMode);

            var headlessInfo = CodesysIdeUiHelper.GetIdeEnvironmentInfo(
                cmdArgsOverride: new[] { "CODESYS.exe", "--profile", "\"CODESYS V3.5 SP20 Patch 5\"", "--noUI" },
                exePathOverride: @"C:\Program Files\CODESYS 3.5.20.50\CODESYS\Common\CODESYS.exe");

            Assert.AreEqual("CODESYS V3.5 SP20 Patch 5", headlessInfo.ideProfile);
            Assert.AreEqual("Headless", headlessInfo.uiMode);
        }

        [TestMethod]
        public void TestGetProjectInfoOffline_ReturnsIdeMetadata()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 601, \"method\": \"tools/call\", \"params\": {\"name\": \"get_project_info\", \"arguments\": {}}}");
            Assert.IsNotNull(res);
            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsFalse(Convert.ToBoolean(result["isError"]));

            var content = result["content"] as ArrayList;
            var firstItem = content[0] as Dictionary<string, object>;
            var payload = _json.Deserialize<Dictionary<string, object>>(firstItem["text"]?.ToString() ?? "{}");

            Assert.IsTrue(payload.ContainsKey("ideVersion") && !string.IsNullOrEmpty(payload["ideVersion"]?.ToString()), "get_project_info must include ideVersion");
            Assert.IsTrue(payload.ContainsKey("ideProfile") && !string.IsNullOrEmpty(payload["ideProfile"]?.ToString()), "get_project_info must include ideProfile");
            Assert.IsTrue(payload.ContainsKey("uiMode") && !string.IsNullOrEmpty(payload["uiMode"]?.ToString()), "get_project_info must include uiMode");
            Assert.IsTrue(payload.ContainsKey("mcpServerVersion") && !string.IsNullOrEmpty(payload["mcpServerVersion"]?.ToString()), "get_project_info must include mcpServerVersion");
        }

        [TestMethod]
        public void TestGetUiIndicatorStatus_ReturnsIdeMetadata()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 602, \"method\": \"tools/call\", \"params\": {\"name\": \"get_ui_indicator_status\", \"arguments\": {}}}");
            Assert.IsNotNull(res);
            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsFalse(Convert.ToBoolean(result["isError"]));

            var content = result["content"] as ArrayList;
            var firstItem = content[0] as Dictionary<string, object>;
            var payload = _json.Deserialize<Dictionary<string, object>>(firstItem["text"]?.ToString() ?? "{}");

            Assert.IsTrue(payload.ContainsKey("ideVersion") && !string.IsNullOrEmpty(payload["ideVersion"]?.ToString()), "get_ui_indicator_status must include ideVersion");
            Assert.IsTrue(payload.ContainsKey("ideProfile") && !string.IsNullOrEmpty(payload["ideProfile"]?.ToString()), "get_ui_indicator_status must include ideProfile");
            Assert.IsTrue(payload.ContainsKey("uiMode") && !string.IsNullOrEmpty(payload["uiMode"]?.ToString()), "get_ui_indicator_status must include uiMode");
        }
    }
}
