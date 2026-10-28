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
            Assert.AreEqual(15, tools.Count, "Expected exactly 15 registered tools");

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
                "save_project",
                "search_code",
                "get_diff",
                "rollback_pou",
                "get_ide_messages",
                "preview_diff"
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

        [TestMethod]
        public void TestSearchCodeWithoutQueryFailsValidation()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 8, \"method\": \"tools/call\", \"params\": {\"name\": \"search_code\", \"arguments\": {}}}");
            Assert.IsNotNull(res);

            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsTrue(Convert.ToBoolean(result["isError"]), "isError should be true when missing 'query'");

            var content = result["content"] as ArrayList;
            var firstItem = content[0] as Dictionary<string, object>;
            Assert.Contains("query", firstItem["text"]?.ToString());
        }

        [TestMethod]
        public void TestSearchCodeInvalidRegexReturnsError()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 9, \"method\": \"tools/call\", \"params\": {\"name\": \"search_code\", \"arguments\": {\"query\": \"[unclosed-regex(\", \"is_regex\": true}}}");
            Assert.IsNotNull(res);

            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsTrue(Convert.ToBoolean(result["isError"]), "isError should be true on invalid regex pattern");
        }

        [TestMethod]
        public void TestRollbackWithoutNameFailsValidation()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 12, \"method\": \"tools/call\", \"params\": {\"name\": \"rollback_pou\", \"arguments\": {}}}");
            Assert.IsNotNull(res);

            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsTrue(Convert.ToBoolean(result["isError"]), "isError should be true when missing 'name'");

            var content = result["content"] as ArrayList;
            var firstItem = content[0] as Dictionary<string, object>;
            Assert.Contains("name", firstItem["text"]?.ToString());
        }

        [TestMethod]
        public void TestGetDiffWithoutModificationsReturnsZeroCount()
        {
            CodesysOperations.ClearSnapshotsForTesting();
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 13, \"method\": \"tools/call\", \"params\": {\"name\": \"get_diff\", \"arguments\": {}}}");
            Assert.IsNotNull(res);

            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsFalse(Convert.ToBoolean(result["isError"]));

            var content = result["content"] as ArrayList;
            var firstItem = content[0] as Dictionary<string, object>;
            var json = new System.Web.Script.Serialization.JavaScriptSerializer();
            var diffRes = json.Deserialize<Dictionary<string, object>>(firstItem["text"]?.ToString());
            Assert.AreEqual(0, Convert.ToInt32(diffRes["modifiedCount"]));
        }

        [TestMethod]
        public void TestRollbackNonExistentSnapshotReturnsCleanMessage()
        {
            CodesysOperations.ClearSnapshotsForTesting();
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 14, \"method\": \"tools/call\", \"params\": {\"name\": \"rollback_pou\", \"arguments\": {\"name\": \"NonExistentPOU\"}}}");
            Assert.IsNotNull(res);

            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsFalse(Convert.ToBoolean(result["isError"]));

            var content = result["content"] as ArrayList;
            var firstItem = content[0] as Dictionary<string, object>;
            var json = new System.Web.Script.Serialization.JavaScriptSerializer();
            var rollbackRes = json.Deserialize<Dictionary<string, object>>(firstItem["text"]?.ToString());
            Assert.IsFalse(Convert.ToBoolean(rollbackRes["success"]));
            Assert.AreEqual("none", rollbackRes["action"]?.ToString());
        }

        [TestMethod]
        public void TestGetIdeMessagesWithoutSystemReturnsEmptyList()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 15, \"method\": \"tools/call\", \"params\": {\"name\": \"get_ide_messages\", \"arguments\": {}}}");
            Assert.IsNotNull(res);

            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsFalse(Convert.ToBoolean(result["isError"]));

            var content = result["content"] as ArrayList;
            var firstItem = content[0] as Dictionary<string, object>;
            var json = new System.Web.Script.Serialization.JavaScriptSerializer();
            var msgRes = json.Deserialize<Dictionary<string, object>>(firstItem["text"]?.ToString());
            Assert.IsTrue(msgRes.ContainsKey("messages"));
            var messages = msgRes["messages"] as ArrayList;
            Assert.IsNotNull(messages);
            Assert.AreEqual(0, messages.Count);
        }

        [TestMethod]
        public void TestPreviewDiffWithoutModificationsReturnsCleanMessage()
        {
            CodesysOperations.ClearSnapshotsForTesting();
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 16, \"method\": \"tools/call\", \"params\": {\"name\": \"preview_diff\", \"arguments\": {\"viewer\": \"none\"}}}");
            Assert.IsNotNull(res);

            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsFalse(Convert.ToBoolean(result["isError"]));

            var content = result["content"] as ArrayList;
            var firstItem = content[0] as Dictionary<string, object>;
            var json = new System.Web.Script.Serialization.JavaScriptSerializer();
            var prevRes = json.Deserialize<Dictionary<string, object>>(firstItem["text"]?.ToString());
            Assert.IsTrue(Convert.ToBoolean(prevRes["success"]));
            Assert.AreEqual(0, Convert.ToInt32(prevRes["modifiedCount"]));
            Assert.IsTrue(prevRes["message"] != null && prevRes["message"].ToString().Contains("No modified POUs"));
        }
    }
}
