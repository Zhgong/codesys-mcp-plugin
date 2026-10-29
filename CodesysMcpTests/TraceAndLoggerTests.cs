using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using CodesysMcp;

namespace CodesysMcpTests
{
    public class TraceAndLoggerTests
    {
        [TestMethod]
        public void TestParseTraceCsvBasicSeriesAndStats()
        {
            // Sample CODESYS Trace CSV format simulation
            string csvContent = 
@"Time (ms);Application.MOTION_PRG.eState;Application.MOTION_PRG.rDispPos;Application.MOTION_PRG.rForce
0.0;0;0.0;0.0
4.0;0;0.0;0.0
8.0;10;0.0;0.0
12.0;10;20.5;0.0
16.0;20;80.1;1200.5
20.0;20;80.2;5050.0
24.0;99;80.2;5050.0";

            var analysis = PlcTraceAndLoggerHelper.AnalyzeTraceCsvContent(csvContent, deadband: 0.05, filterCondition: "rForce >= 5000");

            Assert.IsNotNull(analysis);
            Assert.AreEqual(7, analysis.totalCyclesRecorded);
            Assert.AreEqual(3, analysis.channelStats.Count);

            // Channel stats validation
            Assert.IsTrue(analysis.channelStats.ContainsKey("Application.MOTION_PRG.eState"));
            var stateStats = analysis.channelStats["Application.MOTION_PRG.eState"];
            Assert.AreEqual(0.0, stateStats.initialValue, 0.001);
            Assert.AreEqual(99.0, stateStats.finalValue, 0.001);
            Assert.AreEqual(99.0, stateStats.maxValue, 0.001);
            Assert.AreEqual(0.0, stateStats.minValue, 0.001);

            // Transitions validation (eState jumped 0->10, 10->20, 20->99)
            Assert.IsTrue(analysis.transitions.Count >= 3);
            bool foundStateTransition = false;
            foreach (var t in analysis.transitions)
            {
                if (t.variable.Contains("eState") && t.oldValue == "0" && t.newValue == "10")
                {
                    foundStateTransition = true;
                    Assert.AreEqual(8.0, t.timestampMs, 0.001);
                }
            }
            Assert.IsTrue(foundStateTransition, "Expected eState 0 -> 10 transition at t=8.0ms");

            // Filter condition matching (rForce >= 5000 at t=20.0ms)
            Assert.IsTrue(analysis.conditionMatches.Count >= 1);
            var firstMatch = analysis.conditionMatches[0];
            Assert.AreEqual(20.0, firstMatch.timestampMs, 0.001);
            Assert.IsTrue(firstMatch.snapshot.ContainsKey("Application.MOTION_PRG.rForce"));
            Assert.AreEqual("5050.0", firstMatch.snapshot["Application.MOTION_PRG.rForce"]);
        }

        [TestMethod]
        public void TestParseTraceXmlContent()
        {
            // Sample CODESYS Trace XML format simulation
            string xmlContent =
@"<TraceData Version=""1.0.0.0"">
  <TraceRecord TriggerState=""0"" StartTime=""0"">
    <TraceVariable VarName=""Application.MOTION_PRG.eState"" Type=""INT"">
      <Timestamps>0,4000,8000,12000</Timestamps>
      <Values>0,0,10,20</Values>
    </TraceVariable>
    <TraceVariable VarName=""Application.MOTION_PRG.rForce"" Type=""REAL"">
      <Timestamps>0,4000,8000,12000</Timestamps>
      <Values>0.0,0.0,150.0,600.0</Values>
    </TraceVariable>
  </TraceRecord>
</TraceData>";

            var analysis = PlcTraceAndLoggerHelper.AnalyzeTraceXmlContent(xmlContent, deadband: 0.01, filterCondition: "eState == 20");
            Assert.IsNotNull(analysis);
            Assert.AreEqual(4, analysis.totalCyclesRecorded);
            Assert.AreEqual(2, analysis.channelStats.Count);
            Assert.AreEqual(1, analysis.conditionMatches.Count);
            Assert.AreEqual(12.0, analysis.conditionMatches[0].timestampMs, 0.001);
        }

        [TestMethod]
        public void TestGeneratePlcLoggerIecSourceCode()
        {
            var stEntry = PlcTraceAndLoggerHelper.GenerateLogEntryDutSource();
            Assert.IsTrue(stEntry.Contains("TYPE ST_McpLogEntry :"));
            Assert.IsTrue(stEntry.Contains("nSeq"));
            Assert.IsTrue(stEntry.Contains("nCycle"));
            Assert.IsTrue(stEntry.Contains("sTag"));
            Assert.IsTrue(stEntry.Contains("sMsg"));
            Assert.IsTrue(stEntry.Contains("rVal1"));
            Assert.IsTrue(stEntry.Contains("rVal2"));

            var fbSource = PlcTraceAndLoggerHelper.GenerateFbLoggerSource(bufferSize: 64);
            Assert.IsTrue(fbSource.declaration.Contains("FUNCTION_BLOCK FB_McpLogger"));
            Assert.IsTrue(fbSource.declaration.Contains("aBuffer"));
            Assert.IsTrue(fbSource.declaration.Contains("ARRAY[0..63] OF ST_McpLogEntry;"));
            Assert.IsTrue(fbSource.implementation.Contains("nCycleCounter := nCycleCounter + 1;"));

            var gvlSource = PlcTraceAndLoggerHelper.GenerateGvlLoggerSource();
            Assert.IsTrue(gvlSource.Contains("VAR_GLOBAL"));
            Assert.IsTrue(gvlSource.Contains("McpLogger : FB_McpLogger;"));
        }

        [TestMethod]
        public void TestReconstructAndOrderRingBufferLogs()
        {
            // Simulating a ring buffer of capacity 4 that wrapped around
            // Buffer slots: [seq 4, seq 1, seq 2, seq 3] with writeIdx = 1, totalCount = 4
            var rawEntries = new List<PlcLogEntryDto>
            {
                new PlcLogEntryDto { seq = 4, cycle = 104, tag = "STEP", msg = "Step 4", val1 = 40.0f, val2 = 400.0f },
                new PlcLogEntryDto { seq = 1, cycle = 101, tag = "INIT", msg = "Step 1", val1 = 10.0f, val2 = 100.0f },
                new PlcLogEntryDto { seq = 2, cycle = 102, tag = "STEP", msg = "Step 2", val1 = 20.0f, val2 = 200.0f },
                new PlcLogEntryDto { seq = 3, cycle = 103, tag = "STEP", msg = "Step 3", val1 = 30.0f, val2 = 300.0f }
            };

            var ordered = PlcTraceAndLoggerHelper.ReconstructRingBuffer(rawEntries, totalCount: 4, tagFilter: null, lastN: 10);
            Assert.AreEqual(4, ordered.Count);
            Assert.AreEqual(1, ordered[0].seq);
            Assert.AreEqual(2, ordered[1].seq);
            Assert.AreEqual(3, ordered[2].seq);
            Assert.AreEqual(4, ordered[3].seq);

            // Test tag filter
            var filtered = PlcTraceAndLoggerHelper.ReconstructRingBuffer(rawEntries, totalCount: 4, tagFilter: "STEP", lastN: 2);
            Assert.AreEqual(2, filtered.Count);
            Assert.AreEqual(3, filtered[0].seq);
            Assert.AreEqual(4, filtered[1].seq);
        }

        [TestMethod]
        public void TestOfflineValidationFailsGracefully()
        {
            var resTrace = PlcTraceAndLoggerHelper.CaptureAndAnalyzeTraceOfflineSafe();
            Assert.IsFalse(resTrace.success);

            var resSetup = PlcTraceAndLoggerHelper.SetupPlcLoggerOfflineSafe();
            Assert.IsFalse(resSetup.success);

            var resRead = PlcTraceAndLoggerHelper.ReadPlcLogsOfflineSafe();
            Assert.IsFalse(resRead.success);
        }
    }
}
