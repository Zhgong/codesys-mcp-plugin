using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
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
            var resTrace = PlcTraceAndLoggerHelper.AnalyzeTraceFile("");
            Assert.IsFalse(resTrace.success);

            var resMissing = PlcTraceAndLoggerHelper.AnalyzeTraceFile("C:\\NonExistentFile.trace");
            Assert.IsFalse(resMissing.success);

            var resOfflineLive = PlcTraceAndLoggerHelper.AnalyzeTraceFile(null, traceName: "Trace");
            Assert.IsFalse(resOfflineLive.success);
            Assert.Contains("no CODESYS project is open", resOfflineLive.message);

            var resSetup = PlcTraceAndLoggerHelper.SetupPlcLoggerOfflineSafe();
            Assert.IsFalse(resSetup.success);

            var resRead = PlcTraceAndLoggerHelper.ReadPlcLogsOfflineSafe();
            Assert.IsFalse(resRead.success);
        }

        [TestMethod]
        public void TestAnalyzeTraceFileWithTraceData()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "trace-data.trace");
            if (File.Exists(path))
            {
                var res = PlcTraceAndLoggerHelper.AnalyzeTraceFile(path, deadband: 0.05, filterCondition: "rDispForce >= 5000");
                Assert.IsTrue(res.success);
                Assert.AreEqual(1983, res.totalCyclesRecorded);
                Assert.IsTrue(res.channelStats.ContainsKey("MOTION_PRG.rDispForce"));
                Assert.IsTrue(res.conditionMatches.Count > 0);
            }
        }

        [TestMethod]
        public void TestGeneratePlcLoggerMethodsAndAutoInjectAlignment()
        {
            var tickMethod = PlcTraceAndLoggerHelper.GenerateFbLoggerTickCycleMethodSource();
            Assert.Contains("METHOD TickCycle : BOOL", tickMethod.declaration);
            Assert.Contains("nCycleCounter := nCycleCounter + 1;", tickMethod.implementation);
            Assert.Contains("TickCycle := TRUE;", tickMethod.implementation);

            var logMethod = PlcTraceAndLoggerHelper.GenerateFbLoggerLogMethodSource(32);
            Assert.Contains("METHOD WriteLog : BOOL", logMethod.declaration);
            Assert.Contains("sTag", logMethod.declaration);
            Assert.Contains("sMsg", logMethod.declaration);
            Assert.Contains("rVal1", logMethod.declaration);
            Assert.Contains("rVal2", logMethod.declaration);
            Assert.Contains("MOD 32", logMethod.implementation);
            Assert.Contains("WriteLog := TRUE;", logMethod.implementation);
        }

        [TestMethod]
        public void TestAnalyzeTraceSeriesWindowAndDownsampling()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Time (ms);Application.MOTION_PRG.rPos;Application.MOTION_PRG.rForce");
            for (int i = 0; i < 2000; i++)
            {
                double t = i;
                double pos = Math.Round(i * 0.1, 3);
                double force = i == 250 ? 9999.5 : (i == 375 ? -123.5 : 100.0 + (i % 25));
                sb.AppendLine($"{t.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)};{pos.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)};{force.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
            }

            var res = PlcTraceAndLoggerHelper.AnalyzeTraceCsvContent(
                sb.ToString(),
                deadband: 0.01,
                filterCondition: "rForce >= 9000",
                includeSeries: true,
                startMs: 100.0,
                endMs: 500.0,
                maxPoints: 50);

            Assert.IsTrue(res.success);
            Assert.AreEqual(2000, res.totalCyclesRecorded);
            Assert.AreEqual(50, res.returnedPointsCount);
            Assert.IsNotNull(res.timestamps);
            Assert.AreEqual(50, res.timestamps.Count);
            Assert.AreEqual(100.0, res.timestamps[0], 0.001);
            Assert.AreEqual(500.0, res.timestamps[49], 0.001);

            Assert.IsNotNull(res.series);
            var forceStats = res.channelStats["Application.MOTION_PRG.rForce"];
            Assert.AreEqual(-123.5, forceStats.minValue, 0.001);
            Assert.AreEqual(375.0, forceStats.minTimestampMs, 0.001);
            Assert.AreEqual(9999.5, forceStats.maxValue, 0.001);
            Assert.AreEqual(250.0, forceStats.maxTimestampMs, 0.001);

            var forceSeries = res.series["Application.MOTION_PRG.rForce"];
            Assert.AreEqual(50, forceSeries.Count);
            Assert.IsTrue(forceSeries.Contains(9999.5), "Downsampling must preserve max peak 9999.5");
            Assert.IsTrue(forceSeries.Contains(-123.5), "Downsampling must preserve min peak -123.5");
            Assert.AreEqual(1, res.conditionMatches.Count);

            var serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(res);
            Assert.IsTrue(json.Contains("timestamps"));
            Assert.IsTrue(json.Contains("9999.5"));

            // XML waveform export + downsampling
            string xmlContent =
@"<TraceData Version=""1.0.0.0"">
  <TraceRecord TriggerState=""0"" StartTime=""0"">
    <TraceVariable VarName=""Application.MOTION_PRG.rForce"" Type=""REAL"">
      <Timestamps>0,1000,2000,3000,4000,5000</Timestamps>
      <Values>10.0,20.0,8888.0,30.0,40.0,50.0</Values>
    </TraceVariable>
  </TraceRecord>
</TraceData>";
            var xmlRes = PlcTraceAndLoggerHelper.AnalyzeTraceXmlContent(
                xmlContent,
                includeSeries: true,
                startMs: 1.0,
                endMs: 5.0,
                maxPoints: 3);
            Assert.IsTrue(xmlRes.success);
            Assert.AreEqual(6, xmlRes.totalCyclesRecorded);
            Assert.AreEqual(3, xmlRes.returnedPointsCount);
            Assert.IsTrue(xmlRes.series["Application.MOTION_PRG.rForce"].Contains(8888.0));
        }

        [TestMethod]
        public void TestCodesysScriptTraceObjectContractIfInstalled()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (string.IsNullOrEmpty(programFiles) || !Directory.Exists(programFiles)) return;

            string codesysRoot = Directory.GetDirectories(programFiles, "CODESYS*")
                .Select(d => Path.Combine(d, "CODESYS"))
                .FirstOrDefault(Directory.Exists);
            if (codesysRoot == null) return;

            string tracePlugin = Directory.GetFiles(codesysRoot, "ScriptDriverTraceObject.plugin.dll", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (tracePlugin == null) return;

            string commonDir = Path.Combine(codesysRoot, "Common");
            string pluginsDir = Path.Combine(codesysRoot, "PlugIns");
            string additionalDir = Path.Combine(codesysRoot, "AdditionalFolders");
            string lacDir = Path.Combine(codesysRoot, "LacBinaries");
            var resolvedCache = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

            ResolveEventHandler resolver = (sender, args) =>
            {
                try
                {
                    if (resolvedCache.TryGetValue(args.Name, out var cached)) return cached;
                    var reqName = new AssemblyName(args.Name);
                    if (reqName.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) return null;

                    string fileName = reqName.Name + ".dll";
                    var candidates = new List<string>();
                    string inCommon = Path.Combine(commonDir, fileName);
                    if (File.Exists(inCommon)) candidates.Add(inCommon);
                    if (Directory.Exists(lacDir)) candidates.AddRange(Directory.GetFiles(lacDir, fileName, SearchOption.AllDirectories));
                    if (Directory.Exists(pluginsDir)) candidates.AddRange(Directory.GetFiles(pluginsDir, fileName, SearchOption.AllDirectories));
                    if (Directory.Exists(additionalDir)) candidates.AddRange(Directory.GetFiles(additionalDir, fileName, SearchOption.AllDirectories));

                    string best = null;
                    if (reqName.Version != null)
                    {
                        best = candidates.FirstOrDefault(path =>
                        {
                            try { return AssemblyName.GetAssemblyName(path).Version == reqName.Version; }
                            catch { return false; }
                        });
                    }
                    best = best ?? candidates.FirstOrDefault();
                    if (best != null)
                    {
                        Assembly loaded = Assembly.LoadFrom(best);
                        resolvedCache[args.Name] = loaded;
                        return loaded;
                    }
                }
                catch { }
                return null;
            };

            AppDomain.CurrentDomain.AssemblyResolve += resolver;
            try
            {
                var asm = Assembly.LoadFrom(tracePlugin);
                Type traceObjType = asm.GetType("_3S.CoDeSys.ScriptDriverTraceObject.ScriptTraceObject", false);
                Assert.IsNotNull(traceObjType, "ScriptTraceObject type must exist in ScriptDriverTraceObject.plugin.dll");
                Assert.IsNotNull(traceObjType.GetProperty("variable_list"), "ScriptTraceObject.variable_list must exist");
                Assert.IsNotNull(traceObjType.GetProperty("diagrams"), "ScriptTraceObject.diagrams must exist");
                Assert.IsNotNull(traceObjType.GetMethod("open_editor"), "ScriptTraceObject.open_editor() must exist");

                string[] writableTriggerProps = new[]
                {
                    "trigger_enabled",
                    "trigger_variable",
                    "trigger_edge",
                    "trigger_level",
                    "post_trigger_samples",
                    "record_condition",
                    "task_name",
                    "every_n_cycles",
                    "auto_start"
                };
                foreach (string propName in writableTriggerProps)
                {
                    PropertyInfo prop = traceObjType.GetProperty(propName);
                    Assert.IsNotNull(prop, $"ScriptTraceObject.{propName} must exist");
                    Assert.IsTrue(prop.CanRead && prop.CanWrite, $"ScriptTraceObject.{propName} must be readable and writable");
                }

                Type triggerEdgeEnum = asm.GetType("_3S.CoDeSys.ScriptDriverTraceObject.ScriptTriggerEdge", false);
                Assert.IsNotNull(triggerEdgeEnum, "ScriptTriggerEdge enum must exist in ScriptDriverTraceObject.plugin.dll");
                Assert.IsTrue(triggerEdgeEnum.IsEnum, "ScriptTriggerEdge must be an enum");
                var edgeNames = Enum.GetNames(triggerEdgeEnum);
                Assert.IsTrue(edgeNames.Contains("Undefined"));
                Assert.IsTrue(edgeNames.Contains("Positive"));
                Assert.IsTrue(edgeNames.Contains("Negative"));
                Assert.IsTrue(edgeNames.Contains("Both"));

                Type editorObjType = asm.GetType("_3S.CoDeSys.ScriptDriverTraceObject.ScriptTraceEditorObject", false);
                Assert.IsNotNull(editorObjType, "ScriptTraceEditorObject type must exist in ScriptDriverTraceObject.plugin.dll");
                Assert.IsNotNull(editorObjType.GetProperty("is_online"));
                Assert.IsNotNull(editorObjType.GetProperty("is_running"));
                Assert.IsNotNull(editorObjType.GetMethod("download"));
                Assert.IsNotNull(editorObjType.GetMethod("start"));
                Assert.IsNotNull(editorObjType.GetMethod("stop"));
                Assert.IsNotNull(editorObjType.GetMethod("reset_trigger"));
                Assert.IsNotNull(editorObjType.GetMethod("get_packet_state"));
                Assert.IsNotNull(editorObjType.GetMethod("get_trigger_state"));
                Assert.IsNotNull(editorObjType.GetMethod("save", new[] { typeof(string) }), "ScriptTraceEditorObject.save(string) must exist");
                Assert.IsNotNull(editorObjType.GetField("_view", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public), "ScriptTraceEditorObject._view must exist");

                string traceEditorPlugin = Directory.GetFiles(codesysRoot, "TraceEditor.plugin.dll", SearchOption.AllDirectories)
                    .FirstOrDefault();
                Assert.IsNotNull(traceEditorPlugin, "TraceEditor.plugin.dll must exist when CODESYS is installed");
                var teAsm = Assembly.LoadFrom(traceEditorPlugin);
                Type traceEditorType = teAsm.GetType("_3S.CoDeSys.Trace.TraceEditor", false);
                Assert.IsNotNull(traceEditorType, "TraceEditor type must exist in TraceEditor.plugin.dll");
                Assert.IsTrue(
                    traceEditorType.GetProperty("Manager") != null ||
                    traceEditorType.GetMethod("get_Manager") != null ||
                    traceEditorType.GetField("_tmManager", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) != null,
                    "TraceEditor must expose Manager / _tmManager");

                Type traceManagerType = teAsm.GetType("_3S.CoDeSys.Trace.TraceManager", false);
                Assert.IsNotNull(traceManagerType, "TraceManager type must exist in TraceEditor.plugin.dll");
                Assert.IsNotNull(traceManagerType.GetMethod("IsPacketOnline", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null), "TraceManager.IsPacketOnline() must exist");
                Assert.IsTrue(
                    traceManagerType.GetProperty("PacketName") != null ||
                    traceManagerType.GetMethod("get_PacketName") != null,
                    "TraceManager must expose PacketName / get_PacketName()");
                Assert.IsNotNull(traceManagerType.GetMethod("DeletePacket", new[] { typeof(string) }), "TraceManager.DeletePacket(string) must exist");
            }
            finally
            {
                AppDomain.CurrentDomain.AssemblyResolve -= resolver;
            }
        }

        private class FakeTraceManager
        {
            public bool PacketOnline = true;
            public string PacketName => "Trace";
            public List<string> DeletedPackets { get; } = new List<string>();

            public bool IsPacketOnline() => PacketOnline;
            public void DeletePacket(string stPacketName)
            {
                DeletedPackets.Add(stPacketName);
                PacketOnline = false;
            }
        }

        private class FakeTraceEditorView
        {
            public FakeTraceManager Manager { get; } = new FakeTraceManager();
        }

        private class FakeTraceEditor
        {
            private readonly FakeTraceEditorView _view = new FakeTraceEditorView();
            public FakeTraceEditorView View => _view;
            public string PacketState = "Disabled";
            public string TriggerState = "Disabled";
            public int DownloadCalls;
            public int StartCalls;
            public int StopCalls;
            public int ResetTriggerCalls;

            public string get_packet_state() => PacketState;
            public string get_trigger_state() => TriggerState;
            public void download() { DownloadCalls++; PacketState = "Started"; TriggerState = "WaitForTrigger"; }
            public void start() { StartCalls++; PacketState = "Started"; }
            public void stop() { StopCalls++; PacketState = "Stopped"; }
            public void reset_trigger() { ResetTriggerCalls++; TriggerState = "WaitForTrigger"; }
        }

        private enum FakeTriggerEdge
        {
            Undefined,
            Positive,
            Negative,
            Both
        }

        private class FakeTraceObject
        {
            public bool trigger_enabled { get; set; }
            public string trigger_variable { get; set; }
            public FakeTriggerEdge trigger_edge { get; set; }
            public object trigger_level { get; set; }
            public uint post_trigger_samples { get; set; }
            public string record_condition { get; set; }
            public string task_name { get; set; } = "Task";
            public uint every_n_cycles { get; set; } = 1;
            public bool auto_start { get; set; }
        }

        [TestMethod]
        public void TestControlTraceActionsOnEditorAndOfflineSafe()
        {
            var fakeEditor = new FakeTraceEditor();

            var st = PlcTraceAndLoggerHelper.ExecuteTraceControlOnEditor(fakeEditor, "Trace", "status");
            Assert.IsTrue(st.success);
            Assert.AreEqual("Disabled", st.packetState);

            var dl = PlcTraceAndLoggerHelper.ExecuteTraceControlOnEditor(fakeEditor, "Trace", "download");
            Assert.IsTrue(dl.success);
            Assert.AreEqual(1, fakeEditor.DownloadCalls);
            Assert.AreEqual(1, fakeEditor.View.Manager.DeletedPackets.Count, "Download must pre-delete existing online trace packet to avoid blocking modal prompt");
            Assert.AreEqual("Trace", fakeEditor.View.Manager.DeletedPackets[0]);
            Assert.IsFalse(fakeEditor.View.Manager.PacketOnline);
            Assert.AreEqual("Disabled", dl.packetStateBefore);
            Assert.AreEqual("Started", dl.packetState);

            var sp = PlcTraceAndLoggerHelper.ExecuteTraceControlOnEditor(fakeEditor, "Trace", "stop");
            Assert.IsTrue(sp.success);
            Assert.AreEqual(1, fakeEditor.StopCalls);
            Assert.AreEqual("Stopped", sp.packetState);

            var go = PlcTraceAndLoggerHelper.ExecuteTraceControlOnEditor(fakeEditor, "Trace", "start");
            Assert.IsTrue(go.success);
            Assert.AreEqual(1, fakeEditor.StartCalls);
            Assert.AreEqual("Started", go.packetState);

            var rt = PlcTraceAndLoggerHelper.ExecuteTraceControlOnEditor(fakeEditor, "Trace", "reset_trigger");
            Assert.IsTrue(rt.success);
            Assert.AreEqual(1, fakeEditor.ResetTriggerCalls);
            Assert.AreEqual("WaitForTrigger", rt.triggerState);

            var bad = PlcTraceAndLoggerHelper.ExecuteTraceControlOnEditor(fakeEditor, "Trace", "invalid_action");
            Assert.IsFalse(bad.success);

            var offline = PlcTraceAndLoggerHelper.ControlTrace("Trace", "status");
            Assert.IsFalse(offline.success);
        }

        [TestMethod]
        public void TestConfigureTraceTriggerOnObjectAndEditor()
        {
            var fakeObj = new FakeTraceObject();
            var fakeEditor = new FakeTraceEditor();

            var cfgOpts = new TraceTriggerConfigOptions
            {
                triggerEnabled = true,
                triggerVariable = "MOTION_PRG.rDispForce",
                triggerEdge = "positive",
                triggerLevel = "4500",
                postTriggerSamples = 50,
                recordCondition = "MOTION_PRG.bDispBusy",
                everyNCycles = 2,
                taskName = "Task",
                autoStart = false
            };

            var cfgRes = PlcTraceAndLoggerHelper.ExecuteTraceControlOnEditor(fakeEditor, "Trace", "configure", fakeObj, cfgOpts);
            Assert.IsTrue(cfgRes.success);
            Assert.AreEqual(0, fakeEditor.DownloadCalls);
            Assert.IsTrue(fakeObj.trigger_enabled);
            Assert.AreEqual("MOTION_PRG.rDispForce", fakeObj.trigger_variable);
            Assert.AreEqual(FakeTriggerEdge.Positive, fakeObj.trigger_edge);
            Assert.AreEqual(4500, Convert.ToInt32(fakeObj.trigger_level));
            Assert.AreEqual(50u, fakeObj.post_trigger_samples);
            Assert.AreEqual("MOTION_PRG.bDispBusy", fakeObj.record_condition);
            Assert.AreEqual(2u, fakeObj.every_n_cycles);
            Assert.AreEqual("Task", fakeObj.task_name);
            Assert.IsFalse(fakeObj.auto_start);

            Assert.IsTrue(cfgRes.triggerEnabled);
            Assert.AreEqual("MOTION_PRG.rDispForce", cfgRes.triggerVariable);
            Assert.AreEqual("Positive", cfgRes.triggerEdge);
            Assert.AreEqual("4500", cfgRes.triggerLevel);
            Assert.AreEqual(50u, cfgRes.postTriggerSamples);
            Assert.AreEqual("MOTION_PRG.bDispBusy", cfgRes.recordCondition);
            Assert.AreEqual(2u, cfgRes.everyNCycles);

            // Download with trigger options should apply config first then call download
            var dlOpts = new TraceTriggerConfigOptions
            {
                triggerVariable = "MOTION_PRG.bDispDone",
                triggerEdge = "rising",
                postTriggerSamples = 25,
                recordCondition = ""
            };
            var dlRes = PlcTraceAndLoggerHelper.ExecuteTraceControlOnEditor(fakeEditor, "Trace", "download", fakeObj, dlOpts);
            Assert.IsTrue(dlRes.success);
            Assert.AreEqual(1, fakeEditor.DownloadCalls);
            Assert.IsTrue(fakeObj.trigger_enabled);
            Assert.AreEqual("MOTION_PRG.bDispDone", fakeObj.trigger_variable);
            Assert.AreEqual(FakeTriggerEdge.Positive, fakeObj.trigger_edge);
            Assert.AreEqual(25u, fakeObj.post_trigger_samples);
            Assert.AreEqual("", fakeObj.record_condition);
            Assert.AreEqual("Started", dlRes.packetState);
            Assert.AreEqual("WaitForTrigger", dlRes.triggerState);

            // Invalid trigger_edge fails cleanly
            var badEdge = PlcTraceAndLoggerHelper.ExecuteTraceControlOnEditor(
                fakeEditor,
                "Trace",
                "configure",
                fakeObj,
                new TraceTriggerConfigOptions { triggerEdge = "invalid_edge" });
            Assert.IsFalse(badEdge.success);
            Assert.Contains("trigger_edge", badEdge.message);
        }
    }
}
