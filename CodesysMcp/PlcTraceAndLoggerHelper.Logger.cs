using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace CodesysMcp
{
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

    public class DeviceLogEntryDto
    {
        public DateTime timestamp { get; set; }
        public string severity { get; set; }
        public string component { get; set; }
        public string user { get; set; }
        public string infoId { get; set; }
        public string message { get; set; }
    }

    public class DeviceLogResult
    {
        public bool success { get; set; }
        public string source { get; set; }
        public int totalEntries { get; set; }
        public int infoCount { get; set; }
        public int warningCount { get; set; }
        public int errorCount { get; set; }
        public int exceptionCount { get; set; }
        public List<DeviceLogEntryDto> entries { get; set; } = new List<DeviceLogEntryDto>();
        public string message { get; set; }
    }

    public class SetupDeviceLoggerResult
    {
        public bool success { get; set; }
        public bool cmpLogReferenced { get; set; }
        public bool fcCreated { get; set; }
        public string sampleCall { get; set; }
        public string message { get; set; }
    }

    public static partial class PlcTraceAndLoggerHelper
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

        public static (string declaration, string implementation) GenerateFbLoggerTickCycleMethodSource()
        {
            string decl = "METHOD TickCycle : BOOL";
            string impl = @"IF bClear THEN
    nWriteIdx := 0;
    nTotalCount := 0;
    sLastTag := '';
    sLastMsg := '';
    bClear := FALSE;
END_IF;
nCycleCounter := nCycleCounter + 1;
TickCycle := TRUE;";
            return (decl, impl);
        }

        public static (string declaration, string implementation) GenerateFbLoggerLogMethodSource(int bufferSize = 64)
        {
            if (bufferSize <= 0) bufferSize = 64;
            string decl = @"METHOD WriteLog : BOOL
VAR_INPUT
    sTag  : STRING(31) := '';
    sMsg  : STRING(79) := '';
    rVal1 : REAL := 0.0;
    rVal2 : REAL := 0.0;
END_VAR";
            string impl = $@"IF sTag <> '' OR sMsg <> '' THEN
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
END_IF;
WriteLog := TRUE;";
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

                // 2. Ensure FB_McpLogger FB and its TickCycle / Log methods exist
                var existingFb = CodesysOperations.FindObjectByName("FB_McpLogger");
                if (existingFb == null)
                {
                    var (decl, impl) = GenerateFbLoggerSource(bufferSize);
                    CodesysOperations.WritePou("FB_McpLogger", decl, impl, "FunctionBlock");
                    res.fbCreated = true;
                }

                if (CodesysOperations.FindObjectByName("FB_McpLogger.TickCycle") == null)
                {
                    var (tickDecl, tickImpl) = GenerateFbLoggerTickCycleMethodSource();
                    CodesysOperations.CreateMethod("FB_McpLogger", "TickCycle", "BOOL", tickDecl, tickImpl);
                }
                if (CodesysOperations.FindObjectByName("FB_McpLogger.WriteLog") == null)
                {
                    var (logDecl, logImpl) = GenerateFbLoggerLogMethodSource(bufferSize);
                    CodesysOperations.CreateMethod("FB_McpLogger", "WriteLog", "BOOL", logDecl, logImpl);
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
                            CodesysOperations.WritePou(autoInjectPou, pouContent.declaration, newImpl, pouContent.type ?? "Program");
                        }
                    }
                    catch { }
                }

                res.success = true;
                res.sampleCode = "GVL_McpLogger.McpLogger.WriteLog(sTag := 'STATE_20', sMsg := 'Workpiece contact reached', rVal1 := rDispPos, rVal2 := rForce);";
                res.message = "PLC In-Memory Logger components (ST_McpLogEntry, FB_McpLogger, GVL_McpLogger) are ready.";
                return res;
            });
        }

        public static (string declaration, string implementation) GenerateLogMsgFunctionSource()
        {
            string decl = @"FUNCTION FC_LogMsg : BOOL
VAR_INPUT
    sMsg    : STRING(255);
    nLevel  : INT := 1; // 1: INFO, 2: WARNING, 3: ERROR
END_VAR
VAR
    udiClassID : UDINT;
END_VAR";
            string impl = @"CASE nLevel OF
    2: udiClassID := 16#00000002; // LOG_WARNING
    3: udiClassID := 16#00000004; // LOG_ERROR
ELSE
    udiClassID := 16#00000001; // LOG_INFO
END_CASE;
CmpLog.LogAdd2(
    hLogger    := CmpLog.LogConstants.LOG_STD_LOGGER,
    udiCmpID   := 0,
    udiClassID := udiClassID,
    udiErrorID := 0,
    udiInfoID  := 0,
    pszInfo    := sMsg
);
FC_LogMsg := TRUE;";
            return (decl, impl);
        }

        public static bool EnsureCmpLogLibraryReferenced()
        {
            try
            {
                var proj = CodesysContext.PrimaryProject;
                if (proj == null) return false;
                var libManObj = CodesysOperations.FindObjectByName("Library Manager");
                if (libManObj == null) return false;
                dynamic libManExt = CodesysOperations.GetExtension<dynamic>(libManObj);
                if (libManExt == null) return false;
                try
                {
                    var existingLibs = libManExt.get_libraries(false) as System.Collections.IEnumerable;
                    if (existingLibs != null)
                    {
                        foreach (var lib in existingLibs)
                        {
                            if (lib != null && lib.ToString().IndexOf("CmpLog", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                return true;
                            }
                        }
                    }
                }
                catch { }
                try
                {
                    libManExt.add_placeholder("CmpLog", "CmpLog");
                    return true;
                }
                catch
                {
                    try
                    {
                        libManExt.add_library("CmpLog");
                        return true;
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        public static SetupDeviceLoggerResult SetupDeviceLogger()
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var proj = CodesysContext.PrimaryProject;
                if (proj == null)
                {
                    return new SetupDeviceLoggerResult
                    {
                        success = false,
                        message = "Cannot setup device logger when no CODESYS project is open."
                    };
                }
                var res = new SetupDeviceLoggerResult();
                res.cmpLogReferenced = EnsureCmpLogLibraryReferenced();
                var existingFc = CodesysOperations.FindObjectByName("FC_LogMsg");
                if (existingFc == null)
                {
                    var (decl, impl) = GenerateLogMsgFunctionSource();
                    CodesysOperations.WritePou("FC_LogMsg", decl, impl, "Function");
                    res.fcCreated = true;
                }
                else
                {
                    res.fcCreated = false;
                }
                res.success = true;
                res.sampleCall = "FC_LogMsg('State 20: Workpiece contacted'); // Or with concat: FC_LogMsg(CONCAT('Force=', REAL_TO_STRING(rDispForce)));";
                res.message = "Device logger setup complete. CmpLog library and FC_LogMsg function are ready.";
                return res;
            });
        }

        public static PouContentResult InjectCodeLog(string pouName, string targetPattern, string logMessage, int level = 1, bool insertAfter = true)
        {
            if (string.IsNullOrEmpty(pouName)) throw new ArgumentException("Parameter 'pouName' is required.");
            if (string.IsNullOrEmpty(targetPattern)) throw new ArgumentException("Parameter 'targetPattern' is required.");
            if (string.IsNullOrEmpty(logMessage)) throw new ArgumentException("Parameter 'logMessage' is required.");
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var pou = CodesysOperations.ReadPou(pouName);
                string impl = pou.implementation ?? "";
                int idx = impl.IndexOf(targetPattern, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                {
                    throw new InvalidOperationException($"Target pattern '{targetPattern}' not found in implementation of POU '{pouName}'.");
                }
                string logCall = level > 1
                    ? $"FC_LogMsg('{logMessage}', {level});"
                    : $"FC_LogMsg('{logMessage}');";
                string newImpl;
                if (insertAfter)
                {
                    int eol = impl.IndexOf('\n', idx);
                    if (eol < 0) eol = impl.Length;
                    else eol++;
                    int lineStart = impl.LastIndexOf('\n', idx);
                    lineStart = lineStart < 0 ? 0 : lineStart + 1;
                    string indent = "";
                    for (int i = lineStart; i < idx && char.IsWhiteSpace(impl[i]); i++) indent += impl[i];
                    newImpl = impl.Substring(0, eol) + indent + logCall + "\r\n" + impl.Substring(eol);
                }
                else
                {
                    int lineStart = impl.LastIndexOf('\n', idx);
                    lineStart = lineStart < 0 ? 0 : lineStart + 1;
                    string indent = "";
                    for (int i = lineStart; i < idx && char.IsWhiteSpace(impl[i]); i++) indent += impl[i];
                    newImpl = impl.Substring(0, lineStart) + indent + logCall + "\r\n" + impl.Substring(lineStart);
                }
                return CodesysOperations.WritePou(pouName, pou.declaration, newImpl, pou.type ?? "Program");
            });
        }

        public static PlcLogsResult ReadPlcLogs(string tagFilter = null, int lastN = 32, bool clearAfterRead = false)
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var proj = CodesysContext.PrimaryProject;
                if (proj == null) return ReadPlcLogsOfflineSafe();
                var res = new PlcLogsResult();
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

                int countToRead = (int)Math.Min(totalCount, Math.Min(64, lastN > 0 ? lastN : 32));
                var slotIndices = new List<int>(countToRead);
                if (totalCount == writeIdx && writeIdx > 0)
                {
                    int startSlot = Math.Max(0, (int)writeIdx - countToRead);
                    for (int s = startSlot; s < (int)writeIdx; s++)
                    {
                        slotIndices.Add(s);
                    }
                }
                else
                {
                    for (int s = 0; s < countToRead; s++)
                    {
                        slotIndices.Add(s);
                    }
                }

                var allExprs = new List<string>(slotIndices.Count * 6);
                foreach (int i in slotIndices)
                {
                    string slot = $"GVL_McpLogger.McpLogger.aBuffer[{i}]";
                    allExprs.Add(slot + ".nSeq");
                    allExprs.Add(slot + ".nCycle");
                    allExprs.Add(slot + ".sTag");
                    allExprs.Add(slot + ".sMsg");
                    allExprs.Add(slot + ".rVal1");
                    allExprs.Add(slot + ".rVal2");
                }

                var slotRead = CodesysOnlineHelper.ReadVariables(allExprs);
                var rawEntries = new List<PlcLogEntryDto>(slotIndices.Count);
                foreach (int i in slotIndices)
                {
                    string slot = $"GVL_McpLogger.McpLogger.aBuffer[{i}]";
                    var vals = slotRead.values;
                    if (vals == null) continue;

                    vals.TryGetValue(slot + ".nSeq", out string rawSeq);
                    string normSeq = CodesysOnlineHelper.NormalizeIecValue(rawSeq);
                    if (!long.TryParse(normSeq, NumberStyles.Any, CultureInfo.InvariantCulture, out long seq) || seq <= 0)
                    {
                        continue;
                    }

                    vals.TryGetValue(slot + ".nCycle", out string rawCyc);
                    long.TryParse(CodesysOnlineHelper.NormalizeIecValue(rawCyc), NumberStyles.Any, CultureInfo.InvariantCulture, out long cyc);

                    vals.TryGetValue(slot + ".sTag", out string rawTag);
                    string tag = CodesysOnlineHelper.NormalizeIecValue(rawTag).Trim('\'');

                    vals.TryGetValue(slot + ".sMsg", out string rawMsg);
                    string msg = CodesysOnlineHelper.NormalizeIecValue(rawMsg).Trim('\'');

                    vals.TryGetValue(slot + ".rVal1", out string rawV1);
                    float.TryParse(CodesysOnlineHelper.NormalizeIecValue(rawV1), NumberStyles.Any, CultureInfo.InvariantCulture, out float f1);

                    vals.TryGetValue(slot + ".rVal2", out string rawV2);
                    float.TryParse(CodesysOnlineHelper.NormalizeIecValue(rawV2), NumberStyles.Any, CultureInfo.InvariantCulture, out float f2);

                    rawEntries.Add(new PlcLogEntryDto
                    {
                        seq = seq,
                        cycle = cyc,
                        tag = tag,
                        msg = msg,
                        val1 = f1,
                        val2 = f2
                    });
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

        public static DeviceLogResult ReadDeviceLogOfflineSafe()
        {
            return new DeviceLogResult
            {
                success = false,
                source = "offline",
                message = "Cannot read device logs when no online session is active. You can specify 'file_path' to parse an exported log XML file."
            };
        }

        public static DeviceLogResult ParseDeviceLogXml(string filePathOrXml, string severityFilter = null, string keyword = null, string componentFilter = null, int maxEntries = 50)
        {
            var res = new DeviceLogResult { source = "file" };
            try
            {
                if (string.IsNullOrEmpty(filePathOrXml))
                {
                    res.success = false;
                    res.message = "No XML file path or XML content provided.";
                    return res;
                }
                var doc = new System.Xml.XmlDocument();
                if (File.Exists(filePathOrXml))
                {
                    doc.Load(filePathOrXml);
                    res.source = "file: " + Path.GetFileName(filePathOrXml);
                }
                else
                {
                    doc.LoadXml(filePathOrXml);
                    res.source = "xml_content";
                }
                var entryNodes = doc.SelectNodes("//Entry");
                if (entryNodes == null || entryNodes.Count == 0)
                {
                    res.success = true;
                    res.message = "XML parsed successfully, but no <Entry> elements found.";
                    return res;
                }
                var allEntries = new List<DeviceLogEntryDto>();
                int totalInfo = 0, totalWarn = 0, totalErr = 0, totalExc = 0;
                foreach (System.Xml.XmlNode node in entryNodes)
                {
                    if (node == null) continue;
                    var dto = new DeviceLogEntryDto
                    {
                        severity = node.Attributes?["severity"]?.Value ?? "information",
                        component = node.Attributes?["component"]?.Value ?? "",
                        user = node.Attributes?["user"]?.Value ?? "nobody",
                        infoId = node.Attributes?["infoId"]?.Value ?? "0",
                        message = node.InnerText != null ? node.InnerText.Trim() : ""
                    };
                    string tsStr = node.Attributes?["timestamp"]?.Value;
                    dto.timestamp = DateTime.TryParse(tsStr, out DateTime dt) ? dt : DateTime.MinValue;

                    string sevLower = dto.severity.ToLowerInvariant();
                    if (sevLower.Contains("error")) totalErr++;
                    else if (sevLower.Contains("warn")) totalWarn++;
                    else if (sevLower.Contains("exception")) totalExc++;
                    else totalInfo++;

                    if (!string.IsNullOrEmpty(severityFilter) && !sevLower.Contains(severityFilter.Trim().ToLowerInvariant()))
                        continue;
                    if (!string.IsNullOrEmpty(componentFilter) && (dto.component == null || dto.component.IndexOf(componentFilter.Trim(), StringComparison.OrdinalIgnoreCase) < 0))
                        continue;
                    if (!string.IsNullOrEmpty(keyword) &&
                        (dto.message == null || dto.message.IndexOf(keyword.Trim(), StringComparison.OrdinalIgnoreCase) < 0) &&
                        (dto.component == null || dto.component.IndexOf(keyword.Trim(), StringComparison.OrdinalIgnoreCase) < 0))
                        continue;
                    allEntries.Add(dto);
                }
                res.totalEntries = entryNodes.Count;
                res.infoCount = totalInfo;
                res.warningCount = totalWarn;
                res.errorCount = totalErr;
                res.exceptionCount = totalExc;
                res.entries = (maxEntries > 0 && allEntries.Count > maxEntries)
                    ? allEntries.Take(maxEntries).ToList()
                    : allEntries;
                res.success = true;
                res.message = $"Successfully parsed {entryNodes.Count} total entries (matched {allEntries.Count}; returning {res.entries.Count}; Stats: {totalInfo} Info, {totalWarn} Warn, {totalErr} Error, {totalExc} Exception).";
                return res;
            }
            catch (Exception ex)
            {
                res.success = false;
                res.message = $"Failed to parse device log XML: {ex.Message}";
                return res;
            }
        }

        public static void ExportDeviceLogToXml(List<DeviceLogEntryDto> entries, string outputPath, string loggerName = "PlcLog")
        {
            var doc = new System.Xml.XmlDocument();
            var dec = doc.CreateXmlDeclaration("1.0", "utf-8", null);
            doc.AppendChild(dec);
            var root = doc.CreateElement("LoggerEntries");
            doc.AppendChild(root);
            var logger = doc.CreateElement("Logger");
            logger.SetAttribute("name", loggerName);
            root.AppendChild(logger);
            if (entries != null)
            {
                foreach (var e in entries)
                {
                    var entryNode = doc.CreateElement("Entry");
                    entryNode.SetAttribute("severity", e.severity ?? "information");
                    entryNode.SetAttribute("component", e.component ?? "");
                    entryNode.SetAttribute("user", e.user ?? "nobody");
                    entryNode.SetAttribute("timestamp", e.timestamp != DateTime.MinValue ? e.timestamp.ToString("yyyy/MM/dd HH:mm:ss") : DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss"));
                    entryNode.SetAttribute("infoId", e.infoId ?? "0");
                    entryNode.InnerText = e.message ?? "";
                    logger.AppendChild(entryNode);
                }
            }
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            doc.Save(outputPath);
        }

        private static MethodInfo FindMethodOnObjectAndInterfaces(object target, string methodName, int? paramCount = null)
        {
            if (target == null || string.IsNullOrEmpty(methodName)) return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            Type type = target.GetType();
            foreach (var m in type.GetMethods(flags))
            {
                if (m.Name == methodName && (!paramCount.HasValue || m.GetParameters().Length == paramCount.Value))
                    return m;
            }
            foreach (var iface in type.GetInterfaces())
            {
                foreach (var m in iface.GetMethods(flags))
                {
                    if (m.Name == methodName && (!paramCount.HasValue || m.GetParameters().Length == paramCount.Value))
                        return m;
                }
            }
            return null;
        }

        private static object GetPropOrFieldValue(object target, string memberName)
        {
            if (target == null || string.IsNullOrEmpty(memberName)) return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            Type type = target.GetType();
            var prop = type.GetProperty(memberName, flags);
            if (prop != null)
            {
                try { return prop.GetValue(target, null); } catch { }
            }
            var field = type.GetField(memberName, flags);
            if (field != null)
            {
                try { return field.GetValue(target); } catch { }
            }
            foreach (var iface in type.GetInterfaces())
            {
                var iprop = iface.GetProperty(memberName, flags);
                if (iprop != null)
                {
                    try { return iprop.GetValue(target, null); } catch { }
                }
            }
            return null;
        }

        private static DeviceLogEntryDto ParseLoggerEntryObject(object entry)
        {
            var dto = new DeviceLogEntryDto();
            object tsObj = GetPropOrFieldValue(entry, "TimeStamp");
            if (tsObj is DateTime dt) dto.timestamp = dt;
            else dto.timestamp = DateTime.Now;

            dto.message = GetPropOrFieldValue(entry, "Info")?.ToString() ?? string.Empty;
            dto.component = GetPropOrFieldValue(entry, "CmpID")?.ToString() ?? string.Empty;
            dto.infoId = GetPropOrFieldValue(entry, "InfoID")?.ToString() ?? "0";

            var sevMethod = FindMethodOnObjectAndInterfaces(entry, "GetSeverity", 0);
            if (sevMethod != null)
            {
                try
                {
                    var sev = sevMethod.Invoke(entry, null);
                    dto.severity = sev != null ? sev.ToString() : "Info";
                }
                catch
                {
                    dto.severity = "Info";
                }
            }
            else
            {
                dto.severity = "Info";
            }
            return dto;
        }

        public static int LoggerAsyncWaitTimeoutMs { get; set; } = 5000;

        private static bool WaitForAsyncResultWithMessagePump(object asyncResult, int timeoutMs)
        {
            if (asyncResult == null) return false;
            bool isUiThread = false;
            try
            {
                var forms = System.Windows.Forms.Application.OpenForms;
                isUiThread = forms != null && forms.Count > 0 && !forms[0].InvokeRequired;
            }
            catch { }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                object doneObj = GetPropOrFieldValue(asyncResult, "IsCompleted");
                if (doneObj != null && Convert.ToBoolean(doneObj))
                {
                    return true;
                }
                if (sw.ElapsedMilliseconds >= timeoutMs)
                {
                    return false;
                }
                if (isUiThread)
                {
                    try { System.Windows.Forms.Application.DoEvents(); } catch { }
                }
                System.Threading.Thread.Sleep(15);
            }
        }

        /// <summary>
        /// Reads runtime device logs from an internal ScriptOnlineDevice wrapper or IOnlineDevice3+ instance via reflection
        /// (avoiding C# dynamic binding failures on non-public ScriptDriverOnline types).
        /// </summary>
        public static DeviceLogResult ReadDeviceLogFromOnlineDeviceObject(
            object onlineDevOrWrapper,
            string severityFilter = null,
            string keyword = null,
            string componentFilter = null,
            int maxEntries = 50,
            string saveToPath = null)
        {
            var res = new DeviceLogResult { source = "online" };
            if (onlineDevOrWrapper == null)
            {
                res.success = false;
                res.message = "Online device object is null.";
                return res;
            }

            try
            {
                var getConnected = FindMethodOnObjectAndInterfaces(onlineDevOrWrapper, "get_connected", 0);
                var connectMethod = FindMethodOnObjectAndInterfaces(onlineDevOrWrapper, "connect", 0);
                bool isConnected = false;
                if (getConnected != null)
                {
                    try { isConnected = Convert.ToBoolean(getConnected.Invoke(onlineDevOrWrapper, null)); } catch { }
                }
                if (!isConnected && connectMethod != null)
                {
                    try { connectMethod.Invoke(onlineDevOrWrapper, null); } catch { }
                }
            }
            catch { }

            object targetDevice = onlineDevOrWrapper;
            var createLoggerMethod = FindMethodOnObjectAndInterfaces(targetDevice, "CreateLoggerServiceHandler", 0);
            if (createLoggerMethod == null)
            {
                object unwrapped = GetPropOrFieldValue(onlineDevOrWrapper, "OnlineDevice");
                if (unwrapped != null)
                {
                    targetDevice = unwrapped;
                    createLoggerMethod = FindMethodOnObjectAndInterfaces(targetDevice, "CreateLoggerServiceHandler", 0);
                }
            }

            if (createLoggerMethod == null)
            {
                res.success = false;
                res.message = "Logger service handler is not supported on this device (IOnlineDevice3.CreateLoggerServiceHandler not found).";
                return res;
            }

            object loggerService = null;
            try
            {
                loggerService = createLoggerMethod.Invoke(targetDevice, null);
            }
            catch (Exception ex)
            {
                res.success = false;
                res.message = "Failed to create logger service handler: " + (ex.InnerException?.Message ?? ex.Message);
                return res;
            }

            if (loggerService == null)
            {
                res.success = false;
                res.message = "Logger service handler is not supported on this device.";
                return res;
            }

            try
            {
                var beginNames = FindMethodOnObjectAndInterfaces(loggerService, "BeginGetLoggerNames", 2);
                var endNames = FindMethodOnObjectAndInterfaces(loggerService, "EndGetLoggerNames", 1);
                var beginEntries = FindMethodOnObjectAndInterfaces(loggerService, "BeginGetLoggerEntries", 5);
                var endEntries = FindMethodOnObjectAndInterfaces(loggerService, "EndGetLoggerEntries", 1);

                if (beginNames == null || endNames == null || beginEntries == null || endEntries == null)
                {
                    res.success = false;
                    res.message = "ILoggerServiceHandler methods not available on logger service object.";
                    return res;
                }

                var arNames = beginNames.Invoke(loggerService, new object[] { null, null });
                if (!WaitForAsyncResultWithMessagePump(arNames, LoggerAsyncWaitTimeoutMs))
                {
                    return new DeviceLogResult
                    {
                        success = false,
                        source = "online",
                        message = $"Timed out after {LoggerAsyncWaitTimeoutMs}ms waiting for PLC logger names."
                    };
                }
                var names = endNames.Invoke(loggerService, new object[] { arNames }) as string[];
                string targetName = (names != null && names.Length > 0) ? names[0] : "PlcLog";

                var arEntries = beginEntries.Invoke(loggerService, new object[] { null, null, (uint)0, DateTime.MinValue, targetName });
                if (!WaitForAsyncResultWithMessagePump(arEntries, LoggerAsyncWaitTimeoutMs))
                {
                    try
                    {
                        FindMethodOnObjectAndInterfaces(loggerService, "CancelGetLoggerEntries", 1)
                            ?.Invoke(loggerService, new object[] { arEntries });
                    }
                    catch { }
                    return new DeviceLogResult
                    {
                        success = false,
                        source = "online",
                        message = $"Timed out after {LoggerAsyncWaitTimeoutMs}ms waiting for PLC logger entries."
                    };
                }
                var entries = endEntries.Invoke(loggerService, new object[] { arEntries }) as IEnumerable;
                if (entries != null)
                {
                    var dtoList = new List<DeviceLogEntryDto>();
                    int totalInfo = 0, totalWarn = 0, totalErr = 0, totalExc = 0;
                    int totalRaw = 0;
                    foreach (var entry in entries)
                    {
                        if (entry == null) continue;
                        totalRaw++;
                        var dto = ParseLoggerEntryObject(entry);
                        string sevLower = dto.severity.ToLowerInvariant();
                        if (sevLower.Contains("error")) totalErr++;
                        else if (sevLower.Contains("warn")) totalWarn++;
                        else if (sevLower.Contains("exception")) totalExc++;
                        else totalInfo++;

                        if (!string.IsNullOrEmpty(severityFilter) && !sevLower.Contains(severityFilter.Trim().ToLowerInvariant()))
                            continue;
                        if (!string.IsNullOrEmpty(componentFilter) && (dto.component == null || dto.component.IndexOf(componentFilter.Trim(), StringComparison.OrdinalIgnoreCase) < 0))
                            continue;
                        if (!string.IsNullOrEmpty(keyword) && (dto.message == null || dto.message.IndexOf(keyword.Trim(), StringComparison.OrdinalIgnoreCase) < 0))
                            continue;
                        dtoList.Add(dto);
                    }
                    res.totalEntries = totalRaw;
                    res.infoCount = totalInfo;
                    res.warningCount = totalWarn;
                    res.errorCount = totalErr;
                    res.exceptionCount = totalExc;
                    if (maxEntries > 0 && dtoList.Count > maxEntries)
                    {
                        dtoList = dtoList.Skip(dtoList.Count - maxEntries).ToList();
                    }
                    res.entries = dtoList;
                    res.success = true;
                    res.message = $"Successfully read {totalRaw} device log entries (returning {dtoList.Count}; Stats: {totalInfo} Info, {totalWarn} Warn, {totalErr} Error, {totalExc} Exception).";
                    if (!string.IsNullOrEmpty(saveToPath))
                    {
                        try
                        {
                            ExportDeviceLogToXml(dtoList, saveToPath, targetName);
                            res.message += $" Exported entries to '{saveToPath}'.";
                        }
                        catch (Exception ex)
                        {
                            res.message += $" (Export failed: {ex.Message})";
                        }
                    }
                    return res;
                }
            }
            catch (Exception ex)
            {
                res.success = false;
                res.message = "Error fetching logger entries: " + (ex.InnerException?.Message ?? ex.Message);
                return res;
            }

            res.success = true;
            res.message = "No device log entries recorded.";
            return res;
        }

        public static DeviceLogResult ReadDeviceLog(string filePath = null, string severityFilter = null, string keyword = null, string componentFilter = null, int maxEntries = 50, string saveToPath = null)
        {
            if (!string.IsNullOrEmpty(filePath))
            {
                var fileRes = ParseDeviceLogXml(filePath, severityFilter, keyword, componentFilter, maxEntries);
                if (fileRes.success && !string.IsNullOrEmpty(saveToPath))
                {
                    try
                    {
                        ExportDeviceLogToXml(fileRes.entries, saveToPath);
                        fileRes.message += $" Exported matching entries to '{saveToPath}'.";
                    }
                    catch (Exception ex)
                    {
                        fileRes.message += $" (Export failed: {ex.Message})";
                    }
                }
                return fileRes;
            }
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var proj = CodesysContext.PrimaryProject;
                if (proj == null) return ReadDeviceLogOfflineSafe();

                CodesysOnlineHelper.EnsureExecutionStack();

                object cachedApp = CodesysOnlineHelper.GetCachedOnlineApp();
                if (cachedApp != null)
                {
                    try
                    {
                        object liVal = GetPropOrFieldValue(cachedApp, "is_logged_in");
                        bool loggedIn = liVal != null && Convert.ToBoolean(liVal);
                        if (loggedIn)
                        {
                            object internalApp = GetPropOrFieldValue(cachedApp, "Application");
                            object activeDev = internalApp != null ? GetPropOrFieldValue(internalApp, "OnlineDevice") : null;
                            if (activeDev != null)
                            {
                                return ReadDeviceLogFromOnlineDeviceObject(activeDev, severityFilter, keyword, componentFilter, maxEntries, saveToPath);
                            }
                        }
                    }
                    catch { }
                }

                object onlineModule = CodesysContext.Online;
                if (onlineModule == null)
                {
                    return new DeviceLogResult { success = false, source = "online", message = "Online engine is not available." };
                }

                var devObj = CodesysOperations.FindObjectByName("Device");
                if (devObj == null)
                {
                    return new DeviceLogResult { success = false, source = "online", message = "Device object not found in active project." };
                }

                var createOnlineDevMethod = FindMethodOnObjectAndInterfaces(onlineModule, "create_online_device", 1);
                if (createOnlineDevMethod == null)
                {
                    return new DeviceLogResult { success = false, source = "online", message = "Method 'create_online_device' not found on online engine." };
                }

                object onlineDevWrapper = null;
                try
                {
                    onlineDevWrapper = createOnlineDevMethod.Invoke(onlineModule, new object[] { devObj });
                }
                catch (Exception ex)
                {
                    return new DeviceLogResult
                    {
                        success = false,
                        source = "online",
                        message = "Failed to create online device: " + (ex.InnerException?.Message ?? ex.Message)
                    };
                }

                if (onlineDevWrapper == null)
                {
                    return new DeviceLogResult { success = false, source = "online", message = "create_online_device returned null." };
                }

                try
                {
                    return ReadDeviceLogFromOnlineDeviceObject(onlineDevWrapper, severityFilter, keyword, componentFilter, maxEntries, saveToPath);
                }
                finally
                {
                    try { (onlineDevWrapper as IDisposable)?.Dispose(); } catch { }
                }
            });
        }
    }
}
