using System;
using System.Collections;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using CodesysMcp;

namespace CodesysMcpTests
{
    public class V150FeaturesTests
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

        private class MockOnlineAppV150
        {
            public Dictionary<string, string> CurrentValues { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> PreparedValues { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<KeyValuePair<string, string>> WriteHistory { get; } = new List<KeyValuePair<string, string>>();
            public Queue<string> ReadQueue { get; } = new Queue<string>();

            public string application_state { get; set; } = "run";
            public string operation_state { get; set; } = "run";
            public bool is_logged_in { get; set; } = true;
            public int StartCallCount { get; private set; }
            public int ResetCallCount { get; private set; }

            public void set_prepared_value(string name, string value)
            {
                PreparedValues[name] = value;
            }

            public void write_prepared_values()
            {
                foreach (var kvp in PreparedValues)
                {
                    CurrentValues[kvp.Key] = kvp.Value;
                    WriteHistory.Add(new KeyValuePair<string, string>(kvp.Key, kvp.Value));
                }
                PreparedValues.Clear();
            }

            public void force_prepared_values()
            {
                write_prepared_values();
            }

            public string read_value(string name)
            {
                if (ReadQueue.Count > 0)
                {
                    return ReadQueue.Dequeue();
                }
                return CurrentValues.TryGetValue(name, out string val) ? val : "0";
            }

            public IList<string> read_values(string[] expressions)
            {
                var list = new List<string>(expressions.Length);
                foreach (var expr in expressions)
                {
                    list.Add(read_value(expr));
                }
                return list;
            }

            public void start()
            {
                StartCallCount++;
                application_state = "run";
                operation_state = "run";
            }

            public void reset()
            {
                ResetCallCount++;
            }
        }

        // ============================================================
        // Feature 1: pulse_variable & wait_for_variable
        // ============================================================

        [TestMethod]
        public void TestPulseVariable_WritesActiveThenInactiveAndTracksHistory()
        {
            var mock = new MockOnlineAppV150();
            mock.CurrentValues["MOTION_PRG.bHmiStart"] = "TRUE";
            try
            {
                CodesysOnlineHelper.SetCachedOnlineAppForTesting(mock);
                var res = CodesysOnlineHelper.PulseVariable(
                    "MOTION_PRG.bHmiStart",
                    pulseMs: 20,
                    activeValue: "TRUE",
                    inactiveValue: "FALSE",
                    preClear: true);

                Assert.IsTrue(res.success, "PulseVariable should succeed");
                Assert.IsTrue(res.verified, "Final inactive value should be verified");
                Assert.AreEqual("FALSE", res.finalValue);
                Assert.AreEqual(3, mock.WriteHistory.Count, "pre_clear + active + inactive = 3 writes");
                Assert.AreEqual("FALSE", mock.WriteHistory[0].Value);
                Assert.AreEqual("TRUE", mock.WriteHistory[1].Value);
                Assert.AreEqual("FALSE", mock.WriteHistory[2].Value);
                Assert.IsTrue(res.elapsedMs >= 15, $"Expected elapsedMs >= 15, got {res.elapsedMs}");
            }
            finally
            {
                CodesysOnlineHelper.SetCachedOnlineAppForTesting(null);
            }
        }

        [TestMethod]
        public void TestWaitForVariable_MatchesOnNthPollAndHandlesTimeout()
        {
            var mock = new MockOnlineAppV150();
            mock.ReadQueue.Enqueue("E_PressState.STATE_FAST_APPROACH");
            mock.ReadQueue.Enqueue("E_PressState.STATE_PRESSING");
            mock.ReadQueue.Enqueue("E_PressState.STATE_IDLE");

            try
            {
                CodesysOnlineHelper.SetCachedOnlineAppForTesting(mock);
                var matchRes = CodesysOnlineHelper.WaitForVariable(
                    "MOTION_PRG.eDispState",
                    "STATE_IDLE",
                    op: "==",
                    timeoutMs: 1000,
                    pollMs: 10);

                Assert.IsTrue(matchRes.success, "WaitForVariable should succeed");
                Assert.IsTrue(matchRes.matched, "Should match STATE_IDLE on 3rd poll");
                Assert.IsFalse(matchRes.timedOut);
                Assert.AreEqual(3, matchRes.samplesCount);

                // Numeric >= test
                mock.ReadQueue.Enqueue("REAL#100.0");
                mock.ReadQueue.Enqueue("REAL#5000.5");
                var numRes = CodesysOnlineHelper.WaitForVariable(
                    "MOTION_PRG.rDispForce",
                    "5000",
                    op: ">=",
                    timeoutMs: 1000,
                    pollMs: 10);
                Assert.IsTrue(numRes.matched);
                Assert.AreEqual(2, numRes.samplesCount);

                // Timeout test
                mock.CurrentValues["MOTION_PRG.rDispPos"] = "10.0";
                var timeoutRes = CodesysOnlineHelper.WaitForVariable(
                    "MOTION_PRG.rDispPos",
                    "200.0",
                    op: ">=",
                    timeoutMs: 60,
                    pollMs: 15);
                Assert.IsFalse(timeoutRes.matched);
                Assert.IsTrue(timeoutRes.timedOut);
                Assert.IsFalse(timeoutRes.success);
                Assert.IsTrue(timeoutRes.samplesCount >= 2);
            }
            finally
            {
                CodesysOnlineHelper.SetCachedOnlineAppForTesting(null);
            }
        }

        [TestMethod]
        public void TestPulseAndWaitToolValidation()
        {
            var resPulse = Call("{\"jsonrpc\": \"2.0\", \"id\": 501, \"method\": \"tools/call\", \"params\": {\"name\": \"pulse_variable\", \"arguments\": {}}}");
            var pulseResult = resPulse["result"] as Dictionary<string, object>;
            Assert.IsTrue(Convert.ToBoolean(pulseResult["isError"]));

            var resWait = Call("{\"jsonrpc\": \"2.0\", \"id\": 502, \"method\": \"tools/call\", \"params\": {\"name\": \"wait_for_variable\", \"arguments\": {\"name\": \"MOTION_PRG.bDone\"}}}");
            var waitResult = resWait["result"] as Dictionary<string, object>;
            Assert.IsTrue(Convert.ToBoolean(waitResult["isError"]));
        }

        // ============================================================
        // Feature 2: Enum Symbol Resolution
        // ============================================================

        [TestMethod]
        public void TestParseEnumDeclaration_StandardAndExplicitValues()
        {
            string decl = @"
TYPE E_QualityResult :
(
    NONE := 0,
    OK := 1,
    NG_FORCE_HIGH := 2,
    NG_MISSING_PART := 11 // missing part
);
END_TYPE";
            var map = CodesysOnlineHelper.ParseEnumDeclaration(decl);
            Assert.AreEqual(4, map.Count);
            Assert.AreEqual(0, map["NONE"]);
            Assert.AreEqual(1, map["OK"]);
            Assert.AreEqual(2, map["NG_FORCE_HIGH"]);
            Assert.AreEqual(11, map["NG_MISSING_PART"]);

            string autoDecl = @"
TYPE E_PressProfileId :
(
    PROFILE_CUSTOM := 0,
    PROFILE_ALUMINUM_SOFT,
    PROFILE_STEEL_HEAVY,
    PROFILE_PRECISION_COINING
) INT;
END_TYPE";
            var autoMap = CodesysOnlineHelper.ParseEnumDeclaration(autoDecl);
            Assert.AreEqual(0, autoMap["PROFILE_CUSTOM"]);
            Assert.AreEqual(1, autoMap["PROFILE_ALUMINUM_SOFT"]);
            Assert.AreEqual(2, autoMap["PROFILE_STEEL_HEAVY"]);
            Assert.AreEqual(3, autoMap["PROFILE_PRECISION_COINING"]);
        }

        [TestMethod]
        public void TestWriteVariableAndStruct_ResolveQualifiedAndUnqualifiedEnumNames()
        {
            var mock = new MockOnlineAppV150();
            try
            {
                CodesysOnlineHelper.ClearEnumCache();
                CodesysOnlineHelper.SeedEnumDeclarationForTesting("E_PressProfileId", @"
TYPE E_PressProfileId :
(
    PROFILE_CUSTOM := 0,
    PROFILE_ALUMINUM_SOFT := 1,
    PROFILE_STEEL_HEAVY := 2
);
END_TYPE");
                CodesysOnlineHelper.SetCachedOnlineAppForTesting(mock);

                // Qualified enum write
                var w1 = CodesysOnlineHelper.WriteVariable("MOTION_PRG.eHmiProfileSelect", "E_PressProfileId.PROFILE_STEEL_HEAVY");
                Assert.IsTrue(w1.success);
                Assert.AreEqual("2", mock.CurrentValues["MOTION_PRG.eHmiProfileSelect"]);
                Assert.IsTrue(w1.verified);

                // Unqualified enum write via WriteStruct
                var values = new Dictionary<string, object>
                {
                    { "eProfile", "PROFILE_ALUMINUM_SOFT" },
                    { "rTargetForce", 3500.0 }
                };
                var ws = CodesysOnlineHelper.WriteStruct("MOTION_PRG.PressRecipe", values);
                Assert.IsTrue(ws.success);
                Assert.AreEqual("1", mock.CurrentValues["MOTION_PRG.PressRecipe.eProfile"]);

                // ValuesMatch bidirectional enum symbol comparison
                Assert.IsTrue(CodesysOnlineHelper.ValuesMatch("E_PressProfileId.PROFILE_STEEL_HEAVY", "2"));
                Assert.IsTrue(CodesysOnlineHelper.ValuesMatch("2", "PROFILE_STEEL_HEAVY"));
            }
            finally
            {
                CodesysOnlineHelper.ClearEnumCache();
                CodesysOnlineHelper.SetCachedOnlineAppForTesting(null);
            }
        }

        // ============================================================
        // Feature 3: edit_pou Search/Replace & Unified Diff
        // ============================================================

        [TestMethod]
        public void TestApplyTextBlockEdit_ExactNormalizedAndTrailingWhitespace()
        {
            string source = "IF bStart THEN\r\n    nState := 10;   \r\n    bBusy := TRUE;\r\nEND_IF";
            string target = "    nState := 10;\n    bBusy := TRUE;";
            string replacement = "    nState := 20;\n    bBusy := TRUE;\n    bLog := TRUE;";

            string updated = CodesysOperations.ApplyTextBlockEdit(source, target, replacement, false, "implementation", out int count);
            Assert.AreEqual(1, count);
            Assert.Contains("nState := 20;\r\n    bBusy := TRUE;\r\n    bLog := TRUE;", updated);

            string diff = DiffHelper.GenerateUnifiedDiff(source, updated, "MOTION_PRG (implementation)", "MOTION_PRG (implementation)");
            Assert.Contains("-    nState := 10;", diff);
            Assert.Contains("+    nState := 20;", diff);
            Assert.Contains("+    bLog := TRUE;", diff);
        }

        [TestMethod]
        public void TestApplyTextBlockEdit_AmbiguousAndMissingTargetThrow()
        {
            string source = "x := 1;\r\ny := 2;\r\nx := 1;\r\n";

            bool threwAmbiguous = false;
            try
            {
                CodesysOperations.ApplyTextBlockEdit(source, "x := 1;", "x := 9;", false, "implementation", out _);
            }
            catch (InvalidOperationException ex)
            {
                threwAmbiguous = true;
                Assert.Contains("2", ex.Message);
            }
            Assert.IsTrue(threwAmbiguous, "Ambiguous target should throw when allowMultiple=false");

            string replacedAll = CodesysOperations.ApplyTextBlockEdit(source, "x := 1;", "x := 9;", true, "implementation", out int multiCount);
            Assert.AreEqual(2, multiCount);
            Assert.AreEqual("x := 9;\r\ny := 2;\r\nx := 9;\r\n", replacedAll);

            bool threwMissing = false;
            try
            {
                CodesysOperations.ApplyTextBlockEdit(source, "z := 999;", "z := 0;", false, "implementation", out _);
            }
            catch (ArgumentException)
            {
                threwMissing = true;
            }
            Assert.IsTrue(threwMissing, "Missing target should throw ArgumentException");
        }

        [TestMethod]
        public void TestEditPouToolValidation()
        {
            var resMissingName = Call("{\"jsonrpc\": \"2.0\", \"id\": 503, \"method\": \"tools/call\", \"params\": {\"name\": \"edit_pou\", \"arguments\": {\"target\": \"a\", \"replacement\": \"b\"}}}");
            var r1 = resMissingName["result"] as Dictionary<string, object>;
            Assert.IsTrue(Convert.ToBoolean(r1["isError"]));

            var resMissingTarget = Call("{\"jsonrpc\": \"2.0\", \"id\": 504, \"method\": \"tools/call\", \"params\": {\"name\": \"edit_pou\", \"arguments\": {\"name\": \"MOTION_PRG\"}}}");
            var r2 = resMissingTarget["result"] as Dictionary<string, object>;
            Assert.IsTrue(Convert.ToBoolean(r2["isError"]));
        }

        // ============================================================
        // Feature 4: plc_login Auto-Recover Exception & Ensure Running
        // ============================================================

        [TestMethod]
        public void TestPlcLogin_AutoRecoverExceptionAndEnsureRunning()
        {
            var mock = new MockOnlineAppV150
            {
                application_state = "stop",
                operation_state = "exception"
            };
            var initialStatus = new OnlineStatusResult
            {
                isLoggedIn = true,
                applicationState = "stop",
                operationState = "exception",
                isException = true
            };

            bool downloadInvoked = false;
            var finalStatus = CodesysOnlineHelper.EnsureOnlineAppRunningAndHealthy(
                mock,
                initialStatus,
                ensureRunning: true,
                autoRecoverException: true,
                downloadRecoveryCallback: () =>
                {
                    downloadInvoked = true;
                    mock.operation_state = "stop";
                    return new OnlineStatusResult
                    {
                        isLoggedIn = true,
                        applicationState = "stop",
                        operationState = "stop",
                        isException = false
                    };
                },
                statusRefreshCallback: () => new OnlineStatusResult
                {
                    isLoggedIn = true,
                    applicationState = mock.application_state,
                    operationState = mock.operation_state,
                    isException = false
                });

            Assert.IsTrue(downloadInvoked, "Should invoke download recovery when PLC is in exception");
            Assert.AreEqual(1, mock.StartCallCount, "Should call start() when application is stopped and ensureRunning=true");
            Assert.IsTrue(finalStatus.recoveredFromException);
            Assert.IsTrue(finalStatus.autoStarted);
            Assert.AreEqual("run", finalStatus.applicationState);
        }
    }
}
