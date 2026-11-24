using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using CodesysMcp;

namespace CodesysMcpTests
{
    public class CompilerMessagesCategoryTests
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
        public void TestToolsListContainsGetMessageCategories()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 1, \"method\": \"tools/list\"}");
            Assert.IsNotNull(res, "Response should not be null");
            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result, "Result should not be null");
            var tools = result["tools"] as ArrayList;
            Assert.IsNotNull(tools, "Tools should not be null");

            bool foundGetCategories = false;
            bool foundCompilerMessagesWithCategory = false;

            foreach (Dictionary<string, object> tool in tools)
            {
                string name = tool["name"] as string;
                if (name == "get_message_categories")
                {
                    foundGetCategories = true;
                }
                else if (name == "get_compiler_messages")
                {
                    var schema = tool["inputSchema"] as Dictionary<string, object>;
                    if (schema != null && schema.ContainsKey("properties"))
                    {
                        var props = schema["properties"] as Dictionary<string, object>;
                        if (props != null && props.ContainsKey("category") && props.ContainsKey("min_severity"))
                        {
                            foundCompilerMessagesWithCategory = true;
                        }
                    }
                }
            }

            Assert.IsTrue(foundGetCategories, "tools/list must contain 'get_message_categories'");
            Assert.IsTrue(foundCompilerMessagesWithCategory, "get_compiler_messages schema must contain 'category' and 'min_severity'");
        }

        [TestMethod]
        public void TestGetMessageCategoriesOfflineReturnsStructure()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 2, \"method\": \"tools/call\", \"params\": {\"name\": \"get_message_categories\", \"arguments\": {}}}");
            Assert.IsNotNull(res, "Response should not be null");
            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result, "Result should not be null");

            var content = result["content"] as ArrayList;
            Assert.IsNotNull(content, "Content should not be null");
            Assert.IsTrue(content.Count > 0, "Content should not be empty");

            var item = content[0] as Dictionary<string, object>;
            string text = item["text"] as string;
            Assert.IsNotNull(text, "Text payload should not be null");

            var data = _json.Deserialize<Dictionary<string, object>>(text);
            Assert.IsTrue(data.ContainsKey("categories"), "Result json must contain 'categories' property");
        }

        [TestMethod]
        public void TestGetCompilerMessagesWithCategoryParamOffline()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 3, \"method\": \"tools/call\", \"params\": {\"name\": \"get_compiler_messages\", \"arguments\": {\"category\": \"Build\", \"min_severity\": \"Warning\"}}}");
            Assert.IsNotNull(res, "Response should not be null");
            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result, "Result should not be null");

            var content = result["content"] as ArrayList;
            Assert.IsNotNull(content, "Content should not be null");
            var item = content[0] as Dictionary<string, object>;
            string text = item["text"] as string;
            Assert.IsNotNull(text, "Text payload should not be null");

            var data = _json.Deserialize<Dictionary<string, object>>(text);
            Assert.IsTrue(data.ContainsKey("messages"), "Result json must contain 'messages'");
            Assert.IsTrue(data.ContainsKey("errorCount"), "Result json must contain 'errorCount'");
        }

        [TestMethod]
        public void TestCompilerMessageResultHasCategoryProperty()
        {
            PropertyInfo prop = typeof(CompilerMessageResult).GetProperty("category");
            Assert.IsNotNull(prop, "CompilerMessageResult must have a 'category' property");
            Assert.AreEqual(typeof(string), prop.PropertyType, "'category' property must be of type string");
        }
    }
}
