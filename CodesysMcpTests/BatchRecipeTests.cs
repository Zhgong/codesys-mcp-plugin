using System;
using System.Collections;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using CodesysMcp;

namespace CodesysMcpTests
{
    public class BatchRecipeTests
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
        public void TestFlattenObjectShallowDictionary()
        {
            var dict = new Dictionary<string, object>
            {
                { "rTargetForce", 5500.0 },
                { "rFastSpeed", 45.0 }
            };
            var output = new Dictionary<string, string>();
            CodesysOnlineHelper.FlattenObject("PLC_PRG.PressRecipe", dict, output);

            Assert.AreEqual("5500", output["PLC_PRG.PressRecipe.rTargetForce"]);
            Assert.AreEqual("45", output["PLC_PRG.PressRecipe.rFastSpeed"]);
            Assert.AreEqual(2, output.Count);
        }

        [TestMethod]
        public void TestFlattenObjectNestedDictionary()
        {
            var inner = new Dictionary<string, object>
            {
                { "rMaxAllowedForce", 6500.0 },
                { "bSafetyLimit", true }
            };
            var root = new Dictionary<string, object>
            {
                { "Limits", inner },
                { "rFastSpeed", 50.0 }
            };

            var output = new Dictionary<string, string>();
            CodesysOnlineHelper.FlattenObject("PLC_PRG.Recipe", root, output);

            Assert.AreEqual("6500", output["PLC_PRG.Recipe.Limits.rMaxAllowedForce"]);
            Assert.AreEqual("TRUE", output["PLC_PRG.Recipe.Limits.bSafetyLimit"]);
            Assert.AreEqual("50", output["PLC_PRG.Recipe.rFastSpeed"]);
            Assert.AreEqual(3, output.Count);
        }

        [TestMethod]
        public void TestFlattenObjectListAndArrays()
        {
            var list = new ArrayList { 10.5, 20.5, 30.5 };
            var root = new Dictionary<string, object>
            {
                { "Buffer", list }
            };

            var output = new Dictionary<string, string>();
            CodesysOnlineHelper.FlattenObject("PLC_PRG", root, output);

            Assert.AreEqual("10.5", output["PLC_PRG.Buffer[0]"]);
            Assert.AreEqual("20.5", output["PLC_PRG.Buffer[1]"]);
            Assert.AreEqual("30.5", output["PLC_PRG.Buffer[2]"]);
            Assert.AreEqual(3, output.Count);
        }

        [TestMethod]
        public void TestFlattenObjectBooleans()
        {
            var root = new Dictionary<string, object>
            {
                { "bStart", true },
                { "bStop", false }
            };

            var output = new Dictionary<string, string>();
            CodesysOnlineHelper.FlattenObject("MOTION", root, output);

            Assert.AreEqual("TRUE", output["MOTION.bStart"]);
            Assert.AreEqual("FALSE", output["MOTION.bStop"]);
        }

        [TestMethod]
        public void TestParseIecStructStringSingle()
        {
            string raw = "(rHomePos:=0.0, rWorkpieceContactZ:=80.0, rTargetForce:=5000.0)";
            var dict = CodesysOnlineHelper.ParseIecStructString(raw);

            Assert.AreEqual(3, dict.Count);
            Assert.AreEqual("0.0", dict["rHomePos"]?.ToString());
            Assert.AreEqual("80.0", dict["rWorkpieceContactZ"]?.ToString());
            Assert.AreEqual("5000.0", dict["rTargetForce"]?.ToString());
        }

        [TestMethod]
        public void TestParseIecStructStringWithPrefixes()
        {
            string raw = "(rHomePos:=REAL#0.0, tHold:=TIME#2s, bFlag:=BOOL#1)";
            var dict = CodesysOnlineHelper.ParseIecStructString(raw);

            Assert.AreEqual(3, dict.Count);
            Assert.AreEqual("0.0", dict["rHomePos"]?.ToString());
            Assert.AreEqual("2s", dict["tHold"]?.ToString());
            Assert.AreEqual("1", dict["bFlag"]?.ToString());
        }

        [TestMethod]
        public void TestParseIecStructStringEmptyOrNull()
        {
            var dict1 = CodesysOnlineHelper.ParseIecStructString(null);
            Assert.AreEqual(0, dict1.Count);

            var dict2 = CodesysOnlineHelper.ParseIecStructString("   ");
            Assert.AreEqual(0, dict2.Count);
        }

        [TestMethod]
        public void TestBatchWriteVariablesValidationWithoutVarsFails()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 1, \"method\": \"tools/call\", \"params\": {\"name\": \"batch_write_variables\", \"arguments\": {}}}");
            Assert.IsNotNull(res);
            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsTrue(Convert.ToBoolean(result["isError"]));
        }

        [TestMethod]
        public void TestWriteStructValidationWithoutNameFails()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 2, \"method\": \"tools/call\", \"params\": {\"name\": \"write_struct\", \"arguments\": {\"values\": {\"rTargetForce\": 5000}}}}");
            Assert.IsNotNull(res);
            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsTrue(Convert.ToBoolean(result["isError"]));
        }

        [TestMethod]
        public void TestWriteStructValidationWithoutValuesFails()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 3, \"method\": \"tools/call\", \"params\": {\"name\": \"write_struct\", \"arguments\": {\"struct_name\": \"PLC_PRG.PressRecipe\"}}}");
            Assert.IsNotNull(res);
            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsTrue(Convert.ToBoolean(result["isError"]));
        }

        [TestMethod]
        public void TestReadStructValidationWithoutNameFails()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 4, \"method\": \"tools/call\", \"params\": {\"name\": \"read_struct\", \"arguments\": {}}}");
            Assert.IsNotNull(res);
            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsTrue(Convert.ToBoolean(result["isError"]));
        }

        [TestMethod]
        public void TestReadArrayValidationWithoutNameFails()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 5, \"method\": \"tools/call\", \"params\": {\"name\": \"read_array\", \"arguments\": {}}}");
            Assert.IsNotNull(res);
            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsTrue(Convert.ToBoolean(result["isError"]));
        }

        [TestMethod]
        public void TestToolsListReturnsAllThirtyThreeTools()
        {
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 6, \"method\": \"tools/list\", \"params\": {}}");
            Assert.IsNotNull(res);
            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            var tools = result["tools"] as ArrayList;
            Assert.IsNotNull(tools);
            Assert.AreEqual(33, tools.Count);

            var names = new List<string>();
            foreach (Dictionary<string, object> t in tools)
            {
                names.Add(t["name"]?.ToString());
            }

            Assert.IsTrue(names.Contains("batch_write_variables"));
            Assert.IsTrue(names.Contains("write_struct"));
            Assert.IsTrue(names.Contains("read_struct"));
            Assert.IsTrue(names.Contains("read_array"));
            Assert.IsTrue(names.Contains("get_ui_indicator_status"));
            Assert.IsTrue(names.Contains("analyze_trace"));
            Assert.IsTrue(names.Contains("setup_plc_logger"));
            Assert.IsTrue(names.Contains("read_plc_logs"));
        }

        [TestMethod]
        public void TestBatchWriteVariablesOfflineFailsGracefully()
        {
            CodesysOnlineHelper.ClearSession();
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 7, \"method\": \"tools/call\", \"params\": {\"name\": \"batch_write_variables\", \"arguments\": {\"variables\": {\"PLC_PRG.rTemp\": \"50.0\"}}}}");
            Assert.IsNotNull(res);
            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsTrue(Convert.ToBoolean(result["isError"]));
        }

        [TestMethod]
        public void TestReadStructOfflineFailsGracefully()
        {
            CodesysOnlineHelper.ClearSession();
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 8, \"method\": \"tools/call\", \"params\": {\"name\": \"read_struct\", \"arguments\": {\"struct_name\": \"PLC_PRG.PressRecipe\"}}}");
            Assert.IsNotNull(res);
            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsTrue(Convert.ToBoolean(result["isError"]));
        }

        [TestMethod]
        public void TestReadArrayOfflineFailsGracefully()
        {
            CodesysOnlineHelper.ClearSession();
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 9, \"method\": \"tools/call\", \"params\": {\"name\": \"read_array\", \"arguments\": {\"variable_name\": \"PLC_PRG.aBuffer\", \"count\": 5}}}");
            Assert.IsNotNull(res);
            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsTrue(Convert.ToBoolean(result["isError"]));
        }

        [TestMethod]
        public void TestWriteStructOfflineFailsGracefully()
        {
            CodesysOnlineHelper.ClearSession();
            var res = Call("{\"jsonrpc\": \"2.0\", \"id\": 10, \"method\": \"tools/call\", \"params\": {\"name\": \"write_struct\", \"arguments\": {\"struct_name\": \"PLC_PRG.PressRecipe\", \"values\": {\"rTargetForce\": 5000.0}}}}");
            Assert.IsNotNull(res);
            var result = res["result"] as Dictionary<string, object>;
            Assert.IsNotNull(result);
            Assert.IsTrue(Convert.ToBoolean(result["isError"]));
        }
    }
}
