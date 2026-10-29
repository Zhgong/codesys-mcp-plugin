using System;

using System.Collections.Generic;

using System.Globalization;

using System.IO;

using System.Linq;

using System.Text;

using System.Text.RegularExpressions;

using System.Threading;

using System.Xml;

namespace CodesysMcp

{

    public class TraceChannelStats

    {

        public string variable { get; set; }

        public double initialValue { get; set; }

        public double finalValue { get; set; }

        public double minValue { get; set; }

        public double maxValue { get; set; }

        public int changeCount { get; set; }

    }

    public class TraceTransition

    {

        public double timestampMs { get; set; }

        public string variable { get; set; }

        public string oldValue { get; set; }

        public string newValue { get; set; }

    }

    public class TraceConditionMatch

    {

        public double timestampMs { get; set; }

        public Dictionary<string, string> snapshot { get; set; } = new Dictionary<string, string>();

    }

    public class TraceAnalysisResult

    {

        public bool success { get; set; }

        public int totalCyclesRecorded { get; set; }

        public Dictionary<string, TraceChannelStats> channelStats { get; set; } = new Dictionary<string, TraceChannelStats>();

        public List<TraceTransition> transitions { get; set; } = new List<TraceTransition>();

        public List<TraceConditionMatch> conditionMatches { get; set; } = new List<TraceConditionMatch>();

        public string message { get; set; }

    }

    public class PlcLogEntryDto

    {

        public long seq { get; set; }

        public long cycle { get; set; }

        public string tag { get; set; }

        public string msg { get; set; }

        public float val1 { get; set; }

        public float val2 { get; set; }

    }

    public class PlcLoggerSetupResult

    {

        public bool success { get; set; }

        public bool dutCreated { get; set; }

        public bool fbCreated { get; set; }

        public bool gvlCreated { get; set; }

        public string sampleCode { get; set; }

        public string message { get; set; }

    }

    public class PlcLogsResult

    {

        public bool success { get; set; }

        public long currentPlcCycle { get; set; }

        public long totalLogsWritten { get; set; }

        public List<PlcLogEntryDto> entries { get; set; } = new List<PlcLogEntryDto>();

        public string message { get; set; }

    }

    public static class PlcTraceAndLoggerHelper

    {

        public static string GenerateLogEntryDutSource()

        {

            return @"TYPE ST_McpLogEntry :

STRUCT

    nSeq      : DINT;        // Global monotonic sequence number

    nCycle    : UDINT;       // Precise PLC scan cycle counter

    sTag      : STRING(31);  // Category tag (e.g. 'STATE', 'ERROR', 'PRESS')

    sMsg      : STRING(79);  // Debug message description

    rVal1     : REAL;        // Diagnostic value 1 (e.g. rDispPos)

    rVal2     : REAL;        // Diagnostic value 2 (e.g. rForce)

END_STRUCT

END_TYPE";

        }

        public static (string declaration, string implementation) GenerateFbLoggerSource(int bufferSize = 64)

        {

            if (bufferSize <= 0) bufferSize = 64;

            int maxIdx = bufferSize - 1;

            string decl = $@"FUNCTION_BLOCK FB_McpLogger

VAR_INPUT

    bExecute       : BOOL := TRUE;

    sTag           : STRING(31) := '';

    sMsg           : STRING(79) := '';

    rVal1          : REAL := 0.0;

    rVal2          : REAL := 0.0;

END_VAR

VAR

    aBuffer        : ARRAY[0..{maxIdx}] OF ST_McpLogEntry;

    nWriteIdx      : INT := 0;

    nTotalCount    : DINT := 0;

    nCycleCounter  : UDINT := 0;

    bClear         : BOOL := FALSE;

    sLastTag       : STRING(31) := '';

    sLastMsg       : STRING(79) := '';

END_VAR";

            string impl = $@"IF bClear THEN

    nWriteIdx := 0;

    nTotalCount := 0;

    sLastTag := '';

    sLastMsg := '';

    bClear := FALSE;

END_IF;

nCycleCounter := nCycleCounter + 1;

IF bExecute AND (sTag <> '' OR sMsg <> '') THEN

    IF sTag <> sLastTag OR sMsg <> sLastMsg THEN

        nTotalCount := nTotalCount + 1;

        aBuffer[nWriteIdx].nSeq := nTotalCount;

        aBuffer[nWriteIdx].nCycle := nCycleCounter;

        aBuffer[nWriteIdx].sTag := sTag;

        aBuffer[nWriteIdx].sMsg := sMsg;

        aBuffer[nWriteIdx].rVal1 := rVal1;

        aBuffer[nWriteIdx].rVal2 := rVal2;

        nWriteIdx := (nWriteIdx + 1) MOD {bufferSize};

        sLastTag := sTag;

        sLastMsg := sMsg;

    END_IF;

END_IF;";

            return (decl, impl);

        }

        public static string GenerateGvlLoggerSource()

        {

            return @"VAR_GLOBAL

    McpLogger : FB_McpLogger;

END_VAR";

        }

        public static List<PlcLogEntryDto> ReconstructRingBuffer(List<PlcLogEntryDto> rawEntries, long totalCount, string tagFilter = null, int lastN = 32)

        {

            if (rawEntries == null || rawEntries.Count == 0) return new List<PlcLogEntryDto>();

            var valid = rawEntries.Where(e => e != null && e.seq > 0).OrderBy(e => e.seq).ToList();

            if (!string.IsNullOrEmpty(tagFilter))

            {

                valid = valid.Where(e => string.Equals(e.tag, tagFilter, StringComparison.OrdinalIgnoreCase)).ToList();

            }

            if (lastN > 0 && valid.Count > lastN)

            {

                valid = valid.Skip(valid.Count - lastN).ToList();

            }

            return valid;

        }

        public static TraceAnalysisResult AnalyzeTraceCsvContent(string csvContent, double deadband = 0.01, string filterCondition = null)

        {

            var res = new TraceAnalysisResult { success = true };

            if (string.IsNullOrWhiteSpace(csvContent))

            {

                res.message = "Empty CSV content.";

                return res;

            }


            using (var reader = new StringReader(csvContent))

            {

                string line;

                string headerLine = null;

                char sep = ';';

                string[] rawHeaders = null;

                while ((line = reader.ReadLine()) != null)

                {

                    if (string.IsNullOrWhiteSpace(line)) continue;

                    char candidateSep = line.Contains(";") ? ';' : ',';

                    string[] parts = line.Split(candidateSep);

                    if (parts.Length >= 2)

                    {

                        string p0 = parts[0].Trim().Trim('"', '\'');

                        bool isTime = p0.Equals("Time", StringComparison.OrdinalIgnoreCase) || p0.Equals("t", StringComparison.OrdinalIgnoreCase) || p0.Equals("Timestamp", StringComparison.OrdinalIgnoreCase);

                        bool hasVars = parts.Skip(1).Any(p => p.Contains(".") || Regex.IsMatch(p.Trim(), @"^[a-zA-Z_]\w*"));

                        if (isTime || (hasVars && !p0.StartsWith("0x")))

                        {

                            headerLine = line;

                            sep = candidateSep;

                            rawHeaders = parts;

                            break;

                        }

                    }

                }

                if (headerLine == null || rawHeaders == null || rawHeaders.Length < 2)

                {

                    res.message = "CSV must have at least Time and one signal column.";

                    return res;

                }

                var headers = rawHeaders.Select(h => h.Trim().Trim('"', '\'')).ToArray();

                var channelCols = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                for (int i = 1; i < headers.Length; i++)

                {

                    string name = headers[i];

                    if (!string.IsNullOrEmpty(name))

                    {

                        channelCols[name] = i;

                        res.channelStats[name] = new TraceChannelStats

                        {

                            variable = name,

                            minValue = double.MaxValue,

                            maxValue = double.MinValue

                        };

                    }

                }

                Dictionary<string, string> prevSnapshot = null;

                int cycleIdx = 0;

                while ((line = reader.ReadLine()) != null)

                {

                    if (string.IsNullOrWhiteSpace(line)) continue;

                    string[] parts = line.Split(sep);

                    if (parts.Length < rawHeaders.Length) continue;

                    string timeStr = parts[0].Trim().Trim('"', '\'');

                    double timestampMs = 0.0;

                    double.TryParse(timeStr, NumberStyles.Any, CultureInfo.InvariantCulture, out timestampMs);

                    var currentSnapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var kvp in channelCols)

                    {

                        string chName = kvp.Key;

                        int colIdx = kvp.Value;

                        string rawVal = parts[colIdx].Trim().Trim('"', '\'');

                        currentSnapshot[chName] = rawVal;

                        var stats = res.channelStats[chName];

                        if (double.TryParse(rawVal, NumberStyles.Any, CultureInfo.InvariantCulture, out double numVal))

                        {

                            if (cycleIdx == 0) stats.initialValue = numVal;

                            stats.finalValue = numVal;

                            if (numVal < stats.minValue) stats.minValue = numVal;

                            if (numVal > stats.maxValue) stats.maxValue = numVal;

                        }

                    }

                    // Compute transitions

                    if (prevSnapshot != null)

                    {

                        foreach (var kvp in channelCols)

                        {

                            string chName = kvp.Key;

                            string oldVal = prevSnapshot[chName];

                            string newVal = currentSnapshot[chName];

                            bool changed = false;

                            if (double.TryParse(oldVal, NumberStyles.Any, CultureInfo.InvariantCulture, out double dOld) &&

                                double.TryParse(newVal, NumberStyles.Any, CultureInfo.InvariantCulture, out double dNew))

                            {

                                if (Math.Abs(dOld - dNew) > deadband)

                                {

                                    changed = true;

                                }

                            }

                            else if (!string.Equals(oldVal, newVal, StringComparison.OrdinalIgnoreCase))

                            {

                                changed = true;

                            }

                            if (changed)

                            {

                                res.channelStats[chName].changeCount++;

                                res.transitions.Add(new TraceTransition

                                {

                                    timestampMs = timestampMs,

                                    variable = chName,

                                    oldValue = oldVal,

                                    newValue = newVal

                                });

                            }

                        }

                    }

                    // Condition matching

                    if (!string.IsNullOrEmpty(filterCondition) && EvaluateFilterCondition(currentSnapshot, filterCondition))

                    {

                        res.conditionMatches.Add(new TraceConditionMatch

                        {

                            timestampMs = timestampMs,

                            snapshot = new Dictionary<string, string>(currentSnapshot)

                        });

                    }

                    prevSnapshot = currentSnapshot;

                    cycleIdx++;

                }

                res.totalCyclesRecorded = cycleIdx;

                res.message = $"Successfully analyzed {cycleIdx} Trace cycles with {res.transitions.Count} transition(s).";

            }

            return res;

        }

        public static TraceAnalysisResult AnalyzeTraceXmlContent(string xmlContent, double deadband = 0.01, string filterCondition = null)

        {

            if (string.IsNullOrWhiteSpace(xmlContent))

            {

                return new TraceAnalysisResult { success = true, message = "Empty XML content." };

            }

            var doc = new XmlDocument();

            doc.LoadXml(xmlContent);

            return AnalyzeTraceXmlDocument(doc, deadband, filterCondition);

        }

        public static TraceAnalysisResult AnalyzeTraceXmlDocument(XmlDocument doc, double deadband = 0.01, string filterCondition = null)

        {

            var res = new TraceAnalysisResult { success = true };

            if (doc == null)

            {

                res.message = "Null XML document.";

                return res;

            }

            var varNodes = doc.SelectNodes("//TraceVariable");

            if (varNodes == null || varNodes.Count == 0)

            {

                res.message = "No TraceVariable nodes found in XML.";

                return res;

            }

            var timeline = new SortedDictionary<double, Dictionary<string, string>>();

            foreach (XmlNode vNode in varNodes)

            {

                string varName = vNode.Attributes?["VarName"]?.Value ?? "Var";

                var tsNode = vNode.SelectSingleNode("Timestamps");

                var valNode = vNode.SelectSingleNode("Values");

                if (tsNode == null || valNode == null) continue;

                string[] tsParts = tsNode.InnerText.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

                string[] valParts = valNode.InnerText.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

                res.channelStats[varName] = new TraceChannelStats

                {

                    variable = varName,

                    minValue = double.MaxValue,

                    maxValue = double.MinValue

                };

                bool isMicroseconds = false;
                if (tsParts.Length >= 2 &&
                    double.TryParse(tsParts[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double t0) &&
                    double.TryParse(tsParts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double t1))
                {
                    if (Math.Abs(t1 - t0) >= 500.0) isMicroseconds = true;
                }
                int count = Math.Min(tsParts.Length, valParts.Length);

                for (int i = 0; i < count; i++)

                {

                    double timeMs = 0.0;

                    if (double.TryParse(tsParts[i].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double rawTime))

                    {

                        timeMs = isMicroseconds ? (rawTime / 1000.0) : rawTime;

                    }

                    string valStr = valParts[i].Trim();

                    if (!timeline.TryGetValue(timeMs, out var snap))

                    {

                        snap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                        timeline[timeMs] = snap;

                    }

                    snap[varName] = valStr;

                    var stats = res.channelStats[varName];

                    if (double.TryParse(valStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double numVal))

                    {

                        if (i == 0) stats.initialValue = numVal;

                        stats.finalValue = numVal;

                        if (numVal < stats.minValue) stats.minValue = numVal;

                        if (numVal > stats.maxValue) stats.maxValue = numVal;

                    }

                }

            }

            // Iterate timeline in chronological order

            Dictionary<string, string> prevSnap = null;

            int cycleCount = 0;

            foreach (var kvp in timeline)

            {

                double timeMs = kvp.Key;

                var currSnap = kvp.Value;

                if (prevSnap != null)

                {

                    foreach (var ch in res.channelStats.Keys)

                    {

                        if (prevSnap.TryGetValue(ch, out string oldVal) && currSnap.TryGetValue(ch, out string newVal))

                        {

                            bool changed = false;

                            if (double.TryParse(oldVal, NumberStyles.Any, CultureInfo.InvariantCulture, out double dOld) &&

                                double.TryParse(newVal, NumberStyles.Any, CultureInfo.InvariantCulture, out double dNew))

                            {

                                if (Math.Abs(dOld - dNew) > deadband) changed = true;

                            }

                            else if (!string.Equals(oldVal, newVal, StringComparison.OrdinalIgnoreCase))

                            {

                                changed = true;

                            }

                            if (changed)

                            {

                                res.channelStats[ch].changeCount++;

                                res.transitions.Add(new TraceTransition

                                {

                                    timestampMs = timeMs,

                                    variable = ch,

                                    oldValue = oldVal,

                                    newValue = newVal

                                });

                            }

                        }

                    }

                }

                if (!string.IsNullOrEmpty(filterCondition) && EvaluateFilterCondition(currSnap, filterCondition))

                {

                    res.conditionMatches.Add(new TraceConditionMatch

                    {

                        timestampMs = timeMs,

                        snapshot = new Dictionary<string, string>(currSnap)

                    });

                }

                prevSnap = currSnap;

                cycleCount++;

            }

            res.totalCyclesRecorded = cycleCount;

            res.message = $"Successfully analyzed {cycleCount} XML cycles with {res.transitions.Count} transition(s).";

            return res;

        }

        public static TraceAnalysisResult AnalyzeTraceFile(string filePath, double deadband = 0.01, string filterCondition = null)

        {

            var res = new TraceAnalysisResult { success = false };

            if (string.IsNullOrWhiteSpace(filePath))

            {

                res.message = "File path cannot be null or empty.";

                return res;

            }

            if (!File.Exists(filePath))

            {

                res.message = $"Trace file not found: {filePath}";

                return res;

            }

            try

            {

                string ext = Path.GetExtension(filePath)?.ToLowerInvariant();

                if (ext == ".trace" || ext == ".xml")

                {

                    var doc = new XmlDocument();

                    doc.Load(filePath);

                    return AnalyzeTraceXmlDocument(doc, deadband, filterCondition);

                }

                else if (ext == ".csv")

                {

                    string csv = File.ReadAllText(filePath);

                    return AnalyzeTraceCsvContent(csv, deadband, filterCondition);

                }

                else

                {

                    // Auto-detect format by checking leading character

                    using (var reader = new StreamReader(filePath))

                    {

                        int ch;

                        while ((ch = reader.Read()) != -1 && char.IsWhiteSpace((char)ch)) { }

                        if (ch == '<')

                        {

                            reader.BaseStream.Seek(0, SeekOrigin.Begin);

                            var doc = new XmlDocument();

                            doc.Load(reader);

                            return AnalyzeTraceXmlDocument(doc, deadband, filterCondition);

                        }

                    }

                    string csv = File.ReadAllText(filePath);

                    return AnalyzeTraceCsvContent(csv, deadband, filterCondition);

                }

            }

            catch (Exception ex)

            {

                return new TraceAnalysisResult

                {

                    success = false,

                    message = $"Failed to parse trace file '{filePath}': {ex.Message}"

                };

            }

        }

        private static bool EvaluateFilterCondition(Dictionary<string, string> snapshot, string condition)

        {

            if (string.IsNullOrWhiteSpace(condition)) return false;

            // Matches e.g. "rForce >= 5000" or "eState == 20"

            var m = Regex.Match(condition.Trim(), @"^([a-zA-Z0-9_\.]+)\s*(>=|<=|>|<|==|!=|=)\s*(.+)$");

            if (!m.Success) return false;

            string key = m.Groups[1].Value.Trim();

            string op = m.Groups[2].Value.Trim();

            string targetRaw = m.Groups[3].Value.Trim();

            string actualVal = null;

            foreach (var snapKvp in snapshot)

            {

                if (snapKvp.Key.EndsWith(key, StringComparison.OrdinalIgnoreCase) ||

                    string.Equals(snapKvp.Key, key, StringComparison.OrdinalIgnoreCase))

                {

                    actualVal = snapKvp.Value;

                    break;

                }

            }

            if (actualVal == null) return false;

            bool isActualNum = double.TryParse(actualVal, NumberStyles.Any, CultureInfo.InvariantCulture, out double dActual);

            bool isTargetNum = double.TryParse(targetRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out double dTarget);

            if (isActualNum && isTargetNum)

            {

                switch (op)

                {

                    case ">=": return dActual >= dTarget;

                    case "<=": return dActual <= dTarget;

                    case ">":  return dActual > dTarget;

                    case "<":  return dActual < dTarget;

                    case "==":

                    case "=":  return Math.Abs(dActual - dTarget) < 0.0001;

                    case "!=": return Math.Abs(dActual - dTarget) >= 0.0001;

                }

            }

            else

            {

                bool eq = string.Equals(actualVal, targetRaw, StringComparison.OrdinalIgnoreCase);

                if (op == "==" || op == "=") return eq;

                if (op == "!=") return !eq;

            }

            return false;

        }

        public static TraceAnalysisResult AnalyzeTraceOfflineSafe()

        {

            return new TraceAnalysisResult

            {

                success = false,

                message = "Trace file path must be provided for offline analysis."

            };

        }

        public static PlcLoggerSetupResult SetupPlcLoggerOfflineSafe()
        {
            return new PlcLoggerSetupResult
            {
                success = false,
                message = "Cannot setup PLC logger when no CODESYS project is open."
            };
        }

        public static PlcLogsResult ReadPlcLogsOfflineSafe()
        {
            return new PlcLogsResult
            {
                success = false,
                message = "Cannot read PLC logs when no CODESYS project or online session is active."
            };
        }

        public static PlcLoggerSetupResult SetupPlcLogger(int bufferSize = 64, string autoInjectPou = null)

        {

            return CodesysContext.RunOnPrimaryThread(() =>

            {

                var proj = CodesysContext.PrimaryProject;

                if (proj == null) return SetupPlcLoggerOfflineSafe();

                var res = new PlcLoggerSetupResult();

                // 1. Ensure ST_McpLogEntry DUT exists

                var existingDut = CodesysOperations.FindObjectByName("ST_McpLogEntry");

                if (existingDut == null)

                {

                    CodesysOperations.WritePou("ST_McpLogEntry", GenerateLogEntryDutSource(), null, "DUT");

                    res.dutCreated = true;

                }

                // 2. Ensure FB_McpLogger FB exists

                var existingFb = CodesysOperations.FindObjectByName("FB_McpLogger");

                if (existingFb == null)

                {

                    var (decl, impl) = GenerateFbLoggerSource(bufferSize);

                    CodesysOperations.WritePou("FB_McpLogger", decl, impl, "FunctionBlock");

                    res.fbCreated = true;

                }

                // 3. Ensure GVL_McpLogger GVL exists

                var existingGvl = CodesysOperations.FindObjectByName("GVL_McpLogger");

                if (existingGvl == null)

                {

                    CodesysOperations.WritePou("GVL_McpLogger", GenerateGvlLoggerSource(), null, "GVL");

                    res.gvlCreated = true;

                }

                // 4. Optionally inject TickCycle into specified POU

                if (!string.IsNullOrEmpty(autoInjectPou))

                {

                    try

                    {

                        var pouContent = CodesysOperations.ReadPou(autoInjectPou);

                        string impl = pouContent.implementation ?? "";

                        if (!impl.Contains("GVL_McpLogger.McpLogger.TickCycle"))

                        {

                            string newImpl = "GVL_McpLogger.McpLogger.TickCycle();\r\n" + impl;

                            CodesysOperations.WritePou(autoInjectPou, pouContent.declaration, newImpl, "Program");

                        }

                    }

                    catch { }

                }

                res.success = true;

                res.sampleCode = "GVL_McpLogger.McpLogger.Log(sTag := 'STATE_20', sMsg := 'Workpiece contact reached', rVal1 := rDispPos, rVal2 := rForce);";

                res.message = "PLC In-Memory Logger components (ST_McpLogEntry, FB_McpLogger, GVL_McpLogger) are ready.";

                return res;

            });

        }

        public static PlcLogsResult ReadPlcLogs(string tagFilter = null, int lastN = 32, bool clearAfterRead = false)

        {

            return CodesysContext.RunOnPrimaryThread(() =>

            {

                var proj = CodesysContext.PrimaryProject;

                if (proj == null) return ReadPlcLogsOfflineSafe();

                var res = new PlcLogsResult();

                // Read logger meta properties

                var metaVars = new List<string>

                {

                    "GVL_McpLogger.McpLogger.nTotalCount",

                    "GVL_McpLogger.McpLogger.nWriteIdx",

                    "GVL_McpLogger.McpLogger.nCycleCounter"

                };

                var metaRead = CodesysOnlineHelper.ReadVariables(metaVars);

                long totalCount = 0;

                long writeIdx = 0;

                long cycleCounter = 0;

                if (metaRead.values != null)

                {

                    if (metaRead.values.TryGetValue("GVL_McpLogger.McpLogger.nTotalCount", out string sTot))

                        long.TryParse(CodesysOnlineHelper.NormalizeIecValue(sTot), out totalCount);

                    if (metaRead.values.TryGetValue("GVL_McpLogger.McpLogger.nWriteIdx", out string sIdx))

                        long.TryParse(CodesysOnlineHelper.NormalizeIecValue(sIdx), out writeIdx);

                    if (metaRead.values.TryGetValue("GVL_McpLogger.McpLogger.nCycleCounter", out string sCyc))

                        long.TryParse(CodesysOnlineHelper.NormalizeIecValue(sCyc), out cycleCounter);

                }

                res.currentPlcCycle = cycleCounter;

                res.totalLogsWritten = totalCount;

                if (totalCount <= 0)

                {

                    res.success = true;

                    res.message = "No PLC logs have been written yet.";

                    return res;

                }

                // Read buffered entries

                int countToRead = (int)Math.Min(totalCount, Math.Min(64, lastN > 0 ? lastN : 32));

                var rawEntries = new List<PlcLogEntryDto>();

                for (int i = 0; i < countToRead; i++)

                {

                    var entryRead = CodesysOnlineHelper.ReadStruct($"GVL_McpLogger.McpLogger.aBuffer[{i}]");

                    if (entryRead != null && entryRead.values != null && entryRead.values.Count > 0)

                    {

                        var dto = new PlcLogEntryDto();

                        if (entryRead.values.TryGetValue("nSeq", out var vSeq)) dto.seq = Convert.ToInt64(vSeq);

                        if (entryRead.values.TryGetValue("nCycle", out var vCyc)) dto.cycle = Convert.ToInt64(vCyc);

                        if (entryRead.values.TryGetValue("sTag", out var vTag)) dto.tag = vTag?.ToString()?.Trim('\'');

                        if (entryRead.values.TryGetValue("sMsg", out var vMsg)) dto.msg = vMsg?.ToString()?.Trim('\'');

                        if (entryRead.values.TryGetValue("rVal1", out var v1)) { float.TryParse(v1?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out float f1); dto.val1 = f1; }

                        if (entryRead.values.TryGetValue("rVal2", out var v2)) { float.TryParse(v2?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out float f2); dto.val2 = f2; }

                        rawEntries.Add(dto);

                    }

                }

                res.entries = ReconstructRingBuffer(rawEntries, totalCount, tagFilter, lastN);

                if (clearAfterRead)

                {

                    try

                    {

                        CodesysOnlineHelper.WriteVariable("GVL_McpLogger.McpLogger.bClear", "TRUE");

                    }

                    catch { }

                }

                res.success = true;

                res.message = $"Read {res.entries.Count} log entry(s) from PLC runtime at cycle {cycleCounter}.";

                return res;

            });

        }

    }

}
