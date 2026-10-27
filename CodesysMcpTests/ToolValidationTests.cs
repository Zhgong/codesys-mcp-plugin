using System;
using System.Collections;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using CodesysMcp;

namespace CodesysMcpTests
{
    public class ToolValidationTests
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
        public void TestToolsListReturnsAllTenTools()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 2, \"method\": \"tools/list\"}");
            Assert.IsNotNull(res);

            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result, "tools/list result should not be null");

            var tools = result["tools"] as ArrayList;
            Assert.IsNotNull(tools, "tools array should not be null");
            Assert.AreEqual(10, tools.Count, "Expected exactly 10 registered tools");

            var expectedTools = new HashSet<string>
            {
                "open_project",
                "close_project",
                "get_project_info",
                "get_project_tree",
                "read_pou",
                "write_pou",
                "delete_pou",
                "compile_project",
                "get_compiler_messages",
                "save_project"
            };

            foreach (Dictionary<string, object> tool in tools)
            {
                Assert.IsTrue(tool.ContainsKey("name"), "Tool must contain name");
                Assert.IsTrue(tool.ContainsKey("description"), "Tool must contain description");
                Assert.IsTrue(tool.ContainsKey("inputSchema"), "Tool must contain inputSchema");

                string toolName = tool["name"]?.ToString();
                Assert.IsTrue(expectedTools.Contains(toolName), $"Unexpected tool: {toolName}");
                expectedTools.Remove(toolName);
            }

            Assert.AreEqual(0, expectedTools.Count, "All expected tools should have been present");
        }

        [TestMethod]
        public void TestUnknownToolCallReturnsIsError()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 3, \"method\": \"tools/call\", \"params\": {\"name\": \"unknown_magic_tool\", \"arguments\": {}}}");
            Assert.IsNotNull(res);

            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsTrue(Convert.ToBoolean(result["isError"]), "isError should be true for unknown tool");

            var content = result["content"] as ArrayList;
            Assert.IsNotNull(content);
            var firstItem = content[0] as Dictionary<string, object>;
            Assert.Contains("not recognized", firstItem["text"]?.ToString());
        }

        [TestMethod]
        public void TestOpenProjectWithoutPathFailsValidation()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 4, \"method\": \"tools/call\", \"params\": {\"name\": \"open_project\", \"arguments\": {}}}");
            Assert.IsNotNull(res);

            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsTrue(Convert.ToBoolean(result["isError"]), "isError should be true when missing 'path'");

            var content = result["content"] as ArrayList;
            var firstItem = content[0] as Dictionary<string, object>;
            Assert.Contains("path", firstItem["text"]?.ToString());
        }

        [TestMethod]
        public void TestReadPouWithoutNameFailsValidation()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 5, \"method\": \"tools/call\", \"params\": {\"name\": \"read_pou\", \"arguments\": {}}}");
            Assert.IsNotNull(res);

            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsTrue(Convert.ToBoolean(result["isError"]));

            var content = result["content"] as ArrayList;
            var firstItem = content[0] as Dictionary<string, object>;
            Assert.Contains("name", firstItem["text"]?.ToString());
        }

        [TestMethod]
        public void TestWritePouWithoutNameFailsValidation()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 6, \"method\": \"tools/call\", \"params\": {\"name\": \"write_pou\", \"arguments\": {}}}");
            Assert.IsNotNull(res);

            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsTrue(Convert.ToBoolean(result["isError"]));

            var content = result["content"] as ArrayList;
            var firstItem = content[0] as Dictionary<string, object>;
            Assert.Contains("name", firstItem["text"]?.ToString());
        }

        [TestMethod]
        public void TestDeletePouWithoutNameFailsValidation()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 7, \"method\": \"tools/call\", \"params\": {\"name\": \"delete_pou\", \"arguments\": {}}}");
            Assert.IsNotNull(res);

            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsTrue(Convert.ToBoolean(result["isError"]));

            var content = result["content"] as ArrayList;
            var firstItem = content[0] as Dictionary<string, object>;
            Assert.Contains("name", firstItem["text"]?.ToString());
        }
    }
}
