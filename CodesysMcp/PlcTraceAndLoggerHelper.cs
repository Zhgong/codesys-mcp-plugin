using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
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
        public double minTimestampMs { get; set; }
        public double maxTimestampMs { get; set; }
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
        public int returnedPointsCount { get; set; }
        public Dictionary<string, TraceChannelStats> channelStats { get; set; } = new Dictionary<string, TraceChannelStats>();
        public List<TraceTransition> transitions { get; set; } = new List<TraceTransition>();
        public List<TraceConditionMatch> conditionMatches { get; set; } = new List<TraceConditionMatch>();
        public List<double> timestamps { get; set; }
        public Dictionary<string, List<double>> series { get; set; }
        public string exportedFilePath { get; set; }
        public string message { get; set; }
    }

    public class TraceTriggerConfigOptions
    {
        public bool? triggerEnabled { get; set; }
        public string triggerVariable { get; set; }
        public string triggerEdge { get; set; }
        public object triggerLevel { get; set; }
        public int? postTriggerSamples { get; set; }
        public string recordCondition { get; set; }
        public int? everyNCycles { get; set; }
        public string taskName { get; set; }
        public bool? autoStart { get; set; }

        public bool HasAnyValue =>
            triggerEnabled.HasValue ||
            triggerVariable != null ||
            !string.IsNullOrWhiteSpace(triggerEdge) ||
            triggerLevel != null ||
            postTriggerSamples.HasValue ||
            recordCondition != null ||
            everyNCycles.HasValue ||
            !string.IsNullOrWhiteSpace(taskName) ||
            autoStart.HasValue;
    }

    public class TraceControlResult
    {
        public bool success { get; set; }
        public string traceName { get; set; }
        public string action { get; set; }
        public string packetStateBefore { get; set; }
        public string triggerStateBefore { get; set; }
        public string packetState { get; set; }
        public string triggerState { get; set; }
        public string taskName { get; set; }
        public bool triggerEnabled { get; set; }
        public string triggerVariable { get; set; }
        public string triggerEdge { get; set; }
        public string triggerLevel { get; set; }
        public uint postTriggerSamples { get; set; }
        public string recordCondition { get; set; }
        public uint everyNCycles { get; set; }
        public bool autoStart { get; set; }
        public string message { get; set; }
    }

    public static partial class PlcTraceAndLoggerHelper
    {
        public static TraceAnalysisResult AnalyzeTraceCsvContent(
            string csvContent,
            double deadband = 0.01,
            string filterCondition = null,
            bool includeSeries = false,
            double? startMs = null,
            double? endMs = null,
            int maxPoints = 0)
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
                var channelNames = new List<string>();
                for (int i = 1; i < headers.Length; i++)
                {
                    string name = headers[i];
                    if (!string.IsNullOrEmpty(name) && !channelCols.ContainsKey(name))
                    {
                        channelCols[name] = i;
                        channelNames.Add(name);
                    }
                }

                var allTimes = new List<double>();
                var allRows = new List<Dictionary<string, string>>();
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
                        currentSnapshot[kvp.Key] = parts[kvp.Value].Trim().Trim('"', '\'');
                    }
                    allTimes.Add(timestampMs);
                    allRows.Add(currentSnapshot);
                }

                return BuildTraceAnalysisResult(
                    allTimes,
                    allRows,
                    channelNames,
                    deadband,
                    filterCondition,
                    includeSeries,
                    startMs,
                    endMs,
                    maxPoints,
                    sourceLabel: "Trace");
            }
        }

        public static TraceAnalysisResult AnalyzeTraceXmlContent(
            string xmlContent,
            double deadband = 0.01,
            string filterCondition = null,
            bool includeSeries = false,
            double? startMs = null,
            double? endMs = null,
            int maxPoints = 0)
        {
            if (string.IsNullOrWhiteSpace(xmlContent))
            {
                return new TraceAnalysisResult { success = true, message = "Empty XML content." };
            }
            var doc = new XmlDocument();
            doc.LoadXml(xmlContent);
            return AnalyzeTraceXmlDocument(doc, deadband, filterCondition, includeSeries, startMs, endMs, maxPoints);
        }

        public static TraceAnalysisResult AnalyzeTraceXmlDocument(
            XmlDocument doc,
            double deadband = 0.01,
            string filterCondition = null,
            bool includeSeries = false,
            double? startMs = null,
            double? endMs = null,
            int maxPoints = 0)
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

            var channelNames = new List<string>();
            var timeline = new SortedDictionary<double, Dictionary<string, string>>();
            foreach (XmlNode vNode in varNodes)
            {
                string varName = vNode.Attributes?["VarName"]?.Value ?? "Var";
                var tsNode = vNode.SelectSingleNode("Timestamps");
                var valNode = vNode.SelectSingleNode("Values");
                if (tsNode == null || valNode == null) continue;
                if (!channelNames.Contains(varName)) channelNames.Add(varName);

                string[] tsParts = tsNode.InnerText.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                string[] valParts = valNode.InnerText.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

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
                }
            }

            var allTimes = new List<double>(timeline.Count);
            var allRows = new List<Dictionary<string, string>>(timeline.Count);
            foreach (var kvp in timeline)
            {
                allTimes.Add(kvp.Key);
                allRows.Add(kvp.Value);
            }

            return BuildTraceAnalysisResult(
                allTimes,
                allRows,
                channelNames,
                deadband,
                filterCondition,
                includeSeries,
                startMs,
                endMs,
                maxPoints,
                sourceLabel: "XML");
        }

        private static bool TryParseNumericSample(string rawVal, out double numVal)
        {
            if (string.IsNullOrWhiteSpace(rawVal))
            {
                numVal = 0.0;
                return false;
            }
            string norm = CodesysOnlineHelper.NormalizeIecValue(rawVal);
            if (double.TryParse(norm, NumberStyles.Any, CultureInfo.InvariantCulture, out numVal))
            {
                return !double.IsNaN(numVal) && !double.IsInfinity(numVal);
            }
            if (bool.TryParse(norm, out bool bVal))
            {
                numVal = bVal ? 1.0 : 0.0;
                return true;
            }
            numVal = 0.0;
            return false;
        }

        private static List<int> SelectDownsampledIndices(int totalCount, int maxPoints, ICollection<int> peakIndices)
        {
            if (totalCount <= 0) return new List<int>();
            if (maxPoints <= 0 || totalCount <= maxPoints)
            {
                var all = new List<int>(totalCount);
                for (int i = 0; i < totalCount; i++) all.Add(i);
                return all;
            }

            if (maxPoints == 1)
            {
                return new List<int> { 0 };
            }

            var selected = new SortedSet<int> { 0, totalCount - 1 };
            if (peakIndices != null)
            {
                foreach (int idx in peakIndices)
                {
                    if (selected.Count >= maxPoints) break;
                    if (idx >= 0 && idx < totalCount)
                    {
                        selected.Add(idx);
                    }
                }
            }

            int remaining = maxPoints - selected.Count;
            if (remaining > 0)
            {
                double step = (double)(totalCount - 1) / (remaining + 1);
                for (int k = 1; k <= remaining; k++)
                {
                    int candidate = (int)Math.Round(k * step);
                    candidate = Math.Max(0, Math.Min(totalCount - 1, candidate));
                    selected.Add(candidate);
                }

                for (int idx = 1; idx < totalCount - 1 && selected.Count < maxPoints; idx++)
                {
                    selected.Add(idx);
                }
            }

            return new List<int>(selected);
        }

        private static TraceAnalysisResult BuildTraceAnalysisResult(
            List<double> allTimes,
            List<Dictionary<string, string>> allRows,
            List<string> channelNames,
            double deadband,
            string filterCondition,
            bool includeSeries,
            double? startMs,
            double? endMs,
            int maxPoints,
            string sourceLabel)
        {
            var res = new TraceAnalysisResult
            {
                success = true,
                totalCyclesRecorded = allRows.Count
            };

            var filteredTimes = new List<double>();
            var filteredRows = new List<Dictionary<string, string>>();
            for (int i = 0; i < allRows.Count; i++)
            {
                double t = allTimes[i];
                if (startMs.HasValue && t < startMs.Value) continue;
                if (endMs.HasValue && t > endMs.Value) continue;
                filteredTimes.Add(t);
                filteredRows.Add(allRows[i]);
            }

            var minValMap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var maxValMap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var minIdxMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var maxIdxMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var hasNumMap = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (var ch in channelNames)
            {
                res.channelStats[ch] = new TraceChannelStats { variable = ch };
                minValMap[ch] = double.MaxValue;
                maxValMap[ch] = double.MinValue;
                minIdxMap[ch] = -1;
                maxIdxMap[ch] = -1;
                hasNumMap[ch] = false;
            }

            var numericSeriesRows = includeSeries ? new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase) : null;
            if (includeSeries)
            {
                foreach (var ch in channelNames)
                {
                    numericSeriesRows[ch] = new List<double>(filteredRows.Count);
                }
            }

            Dictionary<string, string> prevSnap = null;
            for (int i = 0; i < filteredRows.Count; i++)
            {
                double timeMs = filteredTimes[i];
                var currSnap = filteredRows[i];

                foreach (var ch in channelNames)
                {
                    currSnap.TryGetValue(ch, out string rawVal);
                    bool parsedNum = TryParseNumericSample(rawVal, out double numVal);
                    if (parsedNum)
                    {
                        var stats = res.channelStats[ch];
                        if (!hasNumMap[ch])
                        {
                            stats.initialValue = numVal;
                            hasNumMap[ch] = true;
                        }
                        stats.finalValue = numVal;
                        if (numVal < minValMap[ch])
                        {
                            minValMap[ch] = numVal;
                            minIdxMap[ch] = i;
                        }
                        if (numVal > maxValMap[ch])
                        {
                            maxValMap[ch] = numVal;
                            maxIdxMap[ch] = i;
                        }
                    }

                    if (includeSeries)
                    {
                        numericSeriesRows[ch].Add(parsedNum ? numVal : 0.0);
                    }
                }

                if (prevSnap != null)
                {
                    foreach (var ch in channelNames)
                    {
                        if (prevSnap.TryGetValue(ch, out string oldVal) && currSnap.TryGetValue(ch, out string newVal))
                        {
                            bool changed = false;
                            if (TryParseNumericSample(oldVal, out double dOld) && TryParseNumericSample(newVal, out double dNew))
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
            }

            foreach (var ch in channelNames)
            {
                if (hasNumMap[ch])
                {
                    res.channelStats[ch].minValue = minValMap[ch];
                    res.channelStats[ch].maxValue = maxValMap[ch];
                    if (minIdxMap[ch] >= 0 && minIdxMap[ch] < filteredTimes.Count)
                    {
                        res.channelStats[ch].minTimestampMs = filteredTimes[minIdxMap[ch]];
                    }
                    if (maxIdxMap[ch] >= 0 && maxIdxMap[ch] < filteredTimes.Count)
                    {
                        res.channelStats[ch].maxTimestampMs = filteredTimes[maxIdxMap[ch]];
                    }
                }
            }

            if (includeSeries)
            {
                var peakIndices = new List<int>();
                foreach (var ch in channelNames)
                {
                    if (minIdxMap[ch] >= 0) peakIndices.Add(minIdxMap[ch]);
                    if (maxIdxMap[ch] >= 0) peakIndices.Add(maxIdxMap[ch]);
                }

                var selectedIndices = SelectDownsampledIndices(filteredTimes.Count, maxPoints, peakIndices);
                res.timestamps = new List<double>(selectedIndices.Count);
                res.series = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
                foreach (var ch in channelNames)
                {
                    res.series[ch] = new List<double>(selectedIndices.Count);
                }

                foreach (int idx in selectedIndices)
                {
                    res.timestamps.Add(filteredTimes[idx]);
                    foreach (var ch in channelNames)
                    {
                        res.series[ch].Add(numericSeriesRows[ch][idx]);
                    }
                }
                res.returnedPointsCount = res.timestamps.Count;
            }
            else
            {
                res.returnedPointsCount = filteredTimes.Count;
            }

            res.message = $"Successfully analyzed {filteredTimes.Count} {sourceLabel} cycles (of {allRows.Count} total) with {res.transitions.Count} transition(s).";
            return res;
        }

        private static object FindTraceExtension(string traceName)
        {
            string targetName = string.IsNullOrWhiteSpace(traceName) ? "Trace" : traceName.Trim();
            var tObj = CodesysOperations.FindObjectByName(targetName);
            if (tObj == null)
                throw new FileNotFoundException($"Trace object '{targetName}' not found in active project.");

            object traceExt = null;
            var extProp = tObj.GetType().GetProperty("Extensions");
            var exts = extProp?.GetValue(tObj, null) as System.Collections.IEnumerable;
            if (exts != null)
            {
                foreach (var item in exts)
                {
                    if (item != null && item.GetType().Name.Contains("TraceObject"))
                    {
                        traceExt = item;
                        break;
                    }
                }
            }
            if (traceExt == null)
                throw new InvalidOperationException($"ScriptTraceObject extension not found on Trace '{targetName}'.");

            return traceExt;
        }

        private static object OpenTraceEditor(string traceName, out object traceExt)
        {
            string targetName = string.IsNullOrWhiteSpace(traceName) ? "Trace" : traceName.Trim();
            traceExt = FindTraceExtension(targetName);

            var openEditorMethod = traceExt.GetType().GetMethod("open_editor", BindingFlags.Public | BindingFlags.Instance);
            if (openEditorMethod == null)
                throw new InvalidOperationException("Method 'open_editor' not found on ScriptTraceObject.");

            object editorObj = openEditorMethod.Invoke(traceExt, null);
            if (editorObj == null)
                throw new InvalidOperationException($"Failed to open trace editor for '{targetName}'.");

            return editorObj;
        }

        private static object OpenTraceEditor(string traceName)
        {
            return OpenTraceEditor(traceName, out _);
        }

        private static void SetTraceProperty(object traceExt, string propName, object value)
        {
            var prop = traceExt.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
            if (prop == null || !prop.CanWrite) return;
            prop.SetValue(traceExt, value, null);
        }

        private static object GetTraceProperty(object traceExt, string propName)
        {
            if (traceExt == null) return null;
            try
            {
                var prop = traceExt.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                return prop != null && prop.CanRead ? prop.GetValue(traceExt, null) : null;
            }
            catch
            {
                return null;
            }
        }

        public static void PopulateTraceConfiguration(object traceExt, TraceControlResult res)
        {
            if (traceExt == null || res == null) return;
            res.taskName = GetTraceProperty(traceExt, "task_name")?.ToString();
            object enVal = GetTraceProperty(traceExt, "trigger_enabled");
            if (enVal != null) res.triggerEnabled = Convert.ToBoolean(enVal);
            res.triggerVariable = GetTraceProperty(traceExt, "trigger_variable")?.ToString();
            res.triggerEdge = GetTraceProperty(traceExt, "trigger_edge")?.ToString();
            object lvlVal = GetTraceProperty(traceExt, "trigger_level");
            res.triggerLevel = lvlVal != null ? Convert.ToString(lvlVal, CultureInfo.InvariantCulture) : null;
            object postVal = GetTraceProperty(traceExt, "post_trigger_samples");
            if (postVal != null) res.postTriggerSamples = Convert.ToUInt32(postVal);
            res.recordCondition = GetTraceProperty(traceExt, "record_condition")?.ToString();
            object everyVal = GetTraceProperty(traceExt, "every_n_cycles");
            if (everyVal != null) res.everyNCycles = Convert.ToUInt32(everyVal);
            object autoVal = GetTraceProperty(traceExt, "auto_start");
            if (autoVal != null) res.autoStart = Convert.ToBoolean(autoVal);
        }

        public static void ApplyTraceTriggerConfiguration(object traceExt, TraceTriggerConfigOptions options)
        {
            if (traceExt == null || options == null) return;

            if (!string.IsNullOrWhiteSpace(options.taskName))
                SetTraceProperty(traceExt, "task_name", options.taskName.Trim());

            if (options.triggerVariable != null)
                SetTraceProperty(traceExt, "trigger_variable", options.triggerVariable.Trim());

            if (options.triggerEnabled.HasValue)
            {
                SetTraceProperty(traceExt, "trigger_enabled", options.triggerEnabled.Value);
            }
            else if (!string.IsNullOrWhiteSpace(options.triggerVariable))
            {
                SetTraceProperty(traceExt, "trigger_enabled", true);
            }

            if (!string.IsNullOrWhiteSpace(options.triggerEdge))
            {
                string normEdge = options.triggerEdge.Trim();
                if (normEdge.Equals("rising", StringComparison.OrdinalIgnoreCase)) normEdge = "Positive";
                else if (normEdge.Equals("falling", StringComparison.OrdinalIgnoreCase)) normEdge = "Negative";

                var edgeProp = traceExt.GetType().GetProperty("trigger_edge", BindingFlags.Public | BindingFlags.Instance);
                if (edgeProp != null && edgeProp.CanWrite)
                {
                    if (edgeProp.PropertyType.IsEnum)
                    {
                        try
                        {
                            object parsed = Enum.Parse(edgeProp.PropertyType, normEdge, ignoreCase: true);
                            if (!Enum.IsDefined(edgeProp.PropertyType, parsed))
                                throw new ArgumentException($"Undefined enum value '{options.triggerEdge}'.");
                            edgeProp.SetValue(traceExt, parsed, null);
                        }
                        catch (Exception ex) when (!(ex is TargetInvocationException))
                        {
                            throw new ArgumentException($"Invalid trigger_edge '{options.triggerEdge}'. Valid values: positive, negative, both, undefined.");
                        }
                    }
                    else
                    {
                        edgeProp.SetValue(traceExt, normEdge, null);
                    }
                }
            }

            if (options.triggerLevel != null)
            {
                var lvlProp = traceExt.GetType().GetProperty("trigger_level", BindingFlags.Public | BindingFlags.Instance);
                if (lvlProp != null && lvlProp.CanWrite)
                {
                    string rawStr = options.triggerLevel.ToString().Trim();
                    if (string.IsNullOrEmpty(rawStr))
                    {
                        lvlProp.SetValue(traceExt, null, null);
                    }
                    else
                    {
                        object typedVal = rawStr;
                        if (int.TryParse(rawStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iVal))
                            typedVal = iVal;
                        else if (double.TryParse(rawStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double dVal))
                            typedVal = dVal;
                        else if (bool.TryParse(rawStr, out bool bVal))
                            typedVal = bVal;

                        try
                        {
                            lvlProp.SetValue(traceExt, typedVal, null);
                        }
                        catch
                        {
                            lvlProp.SetValue(traceExt, rawStr, null);
                        }
                    }
                }
            }

            if (options.postTriggerSamples.HasValue)
                SetTraceProperty(traceExt, "post_trigger_samples", Convert.ToUInt32(Math.Max(0, options.postTriggerSamples.Value)));

            if (options.recordCondition != null)
                SetTraceProperty(traceExt, "record_condition", options.recordCondition.Trim());

            if (options.everyNCycles.HasValue && options.everyNCycles.Value > 0)
                SetTraceProperty(traceExt, "every_n_cycles", Convert.ToUInt32(options.everyNCycles.Value));

            if (options.autoStart.HasValue)
                SetTraceProperty(traceExt, "auto_start", options.autoStart.Value);
        }

        private static string ReadTraceEditorState(object editorObj, string methodName)
        {
            if (editorObj == null) return "Unknown";
            try
            {
                var m = editorObj.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                var val = m?.Invoke(editorObj, null);
                return val?.ToString() ?? "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }

        public static bool TryDeleteExistingOnlineTracePacket(object editorObj, string fallbackPacketName = null)
        {
            if (editorObj == null) return false;
            try
            {
                var viewField = editorObj.GetType().GetField("_view", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object view = viewField?.GetValue(editorObj);
                if (view == null) return false;

                object manager = null;
                var mgrProp = view.GetType().GetProperty("Manager", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (mgrProp != null) manager = mgrProp.GetValue(view, null);
                if (manager == null)
                {
                    var getMgr = view.GetType().GetMethod("get_Manager", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    if (getMgr != null) manager = getMgr.Invoke(view, null);
                }
                if (manager == null)
                {
                    var mgrField = view.GetType().GetField("_tmManager", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (mgrField != null) manager = mgrField.GetValue(view);
                }
                if (manager == null) return false;

                var isOnlineMethod = manager.GetType().GetMethod("IsPacketOnline", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (isOnlineMethod == null) return false;
                object isOnlineObj = isOnlineMethod.Invoke(manager, null);
                bool isOnline = isOnlineObj != null && Convert.ToBoolean(isOnlineObj);
                if (!isOnline) return false;

                string packetName = null;
                var pktProp = manager.GetType().GetProperty("PacketName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pktProp != null) packetName = pktProp.GetValue(manager, null)?.ToString();
                if (string.IsNullOrEmpty(packetName))
                {
                    var getPkt = manager.GetType().GetMethod("get_PacketName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    if (getPkt != null) packetName = getPkt.Invoke(manager, null)?.ToString();
                }
                if (string.IsNullOrEmpty(packetName)) packetName = fallbackPacketName;
                if (string.IsNullOrEmpty(packetName)) return false;

                var delMethod = manager.GetType().GetMethod("DeletePacket", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                if (delMethod == null) return false;
                delMethod.Invoke(manager, new object[] { packetName });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void EnsureTraceOverwritePromptSuppressed()
        {
            try
            {
                object sysObj = CodesysContext.RawSystem;
                if (sysObj == null) return;
                var answersProp = sysObj.GetType().GetProperty("prompt_answers", BindingFlags.Instance | BindingFlags.Public);
                var dict = answersProp?.GetValue(sysObj, null) as System.Collections.IDictionary;
                if (dict == null) return;

                Type promptResultType = null;
                if (answersProp.PropertyType.IsGenericType)
                {
                    var genArgs = answersProp.PropertyType.GetGenericArguments();
                    if (genArgs.Length == 2 && genArgs[1].IsEnum) promptResultType = genArgs[1];
                }
                if (promptResultType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        promptResultType = asm.GetType("_3S.CoDeSys.Core.PromptResult", false);
                        if (promptResultType != null && promptResultType.IsEnum) break;
                    }
                }
                if (promptResultType == null || !promptResultType.IsEnum) return;

                object okVal = null;
                if (Enum.IsDefined(promptResultType, "OK")) okVal = Enum.Parse(promptResultType, "OK");
                else if (Enum.IsDefined(promptResultType, "Yes")) okVal = Enum.Parse(promptResultType, "Yes");
                if (okVal != null)
                {
                    dict["Strings.OverwriteExistingOnlineTrace"] = okVal;
                }
            }
            catch { }
        }

        private static void PumpTraceUiEvents(object editorObj, string act, string packetBefore)
        {
            try
            {
                var forms = System.Windows.Forms.Application.OpenForms;
                if (forms != null && forms.Count > 0 && !forms[0].InvokeRequired)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        System.Windows.Forms.Application.DoEvents();
                        string curP = ReadTraceEditorState(editorObj, "get_packet_state");
                        if ((act == "stop" && string.Equals(curP, "Stopped", StringComparison.OrdinalIgnoreCase)) ||
                            ((act == "start" || act == "download") && string.Equals(curP, "Started", StringComparison.OrdinalIgnoreCase)) ||
                            (!string.Equals(curP, packetBefore, StringComparison.OrdinalIgnoreCase) && !string.Equals(curP, "NoConfig", StringComparison.OrdinalIgnoreCase)))
                        {
                            break;
                        }
                        System.Threading.Thread.Sleep(25);
                    }
                }
            }
            catch { }
        }

        public static TraceControlResult ExecuteTraceControlOnEditor(
            object editorObj,
            string traceName,
            string action,
            object traceExt = null,
            TraceTriggerConfigOptions triggerOptions = null)
        {
            string name = string.IsNullOrWhiteSpace(traceName) ? "Trace" : traceName.Trim();
            string act = (action ?? "status").Trim().ToLowerInvariant();
            if (editorObj == null)
            {
                return new TraceControlResult
                {
                    success = false,
                    traceName = name,
                    action = act,
                    message = "Trace editor object is null."
                };
            }

            string packetBefore = ReadTraceEditorState(editorObj, "get_packet_state");
            string triggerBefore = ReadTraceEditorState(editorObj, "get_trigger_state");

            string methodToInvoke = null;
            switch (act)
            {
                case "status":
                case "configure":
                    break;
                case "download":
                    methodToInvoke = "download";
                    break;
                case "start":
                    if (!string.Equals(packetBefore, "Started", StringComparison.OrdinalIgnoreCase))
                        methodToInvoke = "start";
                    break;
                case "stop":
                    if (!string.Equals(packetBefore, "Stopped", StringComparison.OrdinalIgnoreCase))
                        methodToInvoke = "stop";
                    break;
                case "reset_trigger":
                    methodToInvoke = "reset_trigger";
                    break;
                default:
                    var invalidRes = new TraceControlResult
                    {
                        success = false,
                        traceName = name,
                        action = act,
                        packetStateBefore = packetBefore,
                        triggerStateBefore = triggerBefore,
                        packetState = packetBefore,
                        triggerState = triggerBefore,
                        message = $"Unsupported trace action '{action}'. Valid actions: status, configure, download, start, stop, reset_trigger."
                    };
                    PopulateTraceConfiguration(traceExt, invalidRes);
                    return invalidRes;
            }

            if (triggerOptions != null && triggerOptions.HasAnyValue)
            {
                if (traceExt == null)
                {
                    return new TraceControlResult
                    {
                        success = false,
                        traceName = name,
                        action = act,
                        packetStateBefore = packetBefore,
                        triggerStateBefore = triggerBefore,
                        packetState = packetBefore,
                        triggerState = triggerBefore,
                        message = "ScriptTraceObject extension is required to apply trigger configuration."
                    };
                }

                // Stop an actively running trace before mutating trigger configuration to avoid runtime lock errors
                if (string.Equals(packetBefore, "Started", StringComparison.OrdinalIgnoreCase) &&
                    (act == "configure" || act == "download"))
                {
                    try
                    {
                        var stopMethod = editorObj.GetType().GetMethod("stop", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                        stopMethod?.Invoke(editorObj, null);
                        PumpTraceUiEvents(editorObj, "stop", packetBefore);
                    }
                    catch { }
                }

                try
                {
                    ApplyTraceTriggerConfiguration(traceExt, triggerOptions);
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    var cfgErr = new TraceControlResult
                    {
                        success = false,
                        traceName = name,
                        action = act,
                        packetStateBefore = packetBefore,
                        triggerStateBefore = triggerBefore,
                        packetState = ReadTraceEditorState(editorObj, "get_packet_state"),
                        triggerState = ReadTraceEditorState(editorObj, "get_trigger_state"),
                        message = $"Failed to apply trace configuration: {inner.Message}"
                    };
                    PopulateTraceConfiguration(traceExt, cfgErr);
                    return cfgErr;
                }
            }

            if (methodToInvoke != null)
            {
                var m = editorObj.GetType().GetMethod(methodToInvoke, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (m == null)
                {
                    var noMethodRes = new TraceControlResult
                    {
                        success = false,
                        traceName = name,
                        action = act,
                        packetStateBefore = packetBefore,
                        triggerStateBefore = triggerBefore,
                        packetState = packetBefore,
                        triggerState = triggerBefore,
                        message = $"Method '{methodToInvoke}' not found on ScriptTraceEditorObject."
                    };
                    PopulateTraceConfiguration(traceExt, noMethodRes);
                    return noMethodRes;
                }

                try
                {
                    if (act == "download")
                    {
                        EnsureTraceOverwritePromptSuppressed();
                        TryDeleteExistingOnlineTracePacket(editorObj, name);
                        using (CodesysContext.RawSystem != null
                            ? new DeviceLogonWatchdog(DeviceLogonWatchdog.GetCurrentThreadId(), null, null, confirmGenericPrompts: true)
                            : null)
                        {
                            m.Invoke(editorObj, null);
                        }
                    }
                    else
                    {
                        m.Invoke(editorObj, null);
                    }
                    PumpTraceUiEvents(editorObj, act, packetBefore);
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    string curPacket = ReadTraceEditorState(editorObj, "get_packet_state");
                    string curTrigger = ReadTraceEditorState(editorObj, "get_trigger_state");
                    var errRes = new TraceControlResult
                    {
                        success = false,
                        traceName = name,
                        action = act,
                        packetStateBefore = packetBefore,
                        triggerStateBefore = triggerBefore,
                        packetState = curPacket,
                        triggerState = curTrigger,
                        message = $"Trace action '{act}' failed (PacketState={curPacket}, TriggerState={curTrigger}): {inner.Message}"
                    };
                    PopulateTraceConfiguration(traceExt, errRes);
                    return errRes;
                }
            }

            string packetAfter = ReadTraceEditorState(editorObj, "get_packet_state");
            string triggerAfter = ReadTraceEditorState(editorObj, "get_trigger_state");
            var okRes = new TraceControlResult
            {
                success = true,
                traceName = name,
                action = act,
                packetStateBefore = packetBefore,
                triggerStateBefore = triggerBefore,
                packetState = packetAfter,
                triggerState = triggerAfter,
                message = $"Trace '{name}' action '{act}' completed (PacketState: {packetBefore} -> {packetAfter}, TriggerState: {triggerBefore} -> {triggerAfter})."
            };
            PopulateTraceConfiguration(traceExt, okRes);
            return okRes;
        }

        public static TraceControlResult ControlTrace(
            string traceName = "Trace",
            string action = "status",
            TraceTriggerConfigOptions triggerOptions = null)
        {
            string name = string.IsNullOrWhiteSpace(traceName) ? "Trace" : traceName.Trim();
            string act = (action ?? "status").Trim().ToLowerInvariant();
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                if (CodesysContext.PrimaryProject == null)
                {
                    return new TraceControlResult
                    {
                        success = false,
                        traceName = name,
                        action = act,
                        message = "Cannot control Trace when no CODESYS project is open."
                    };
                }

                try
                {
                    CodesysOnlineHelper.EnsureExecutionStack();
                    object traceExt;
                    object editorObj = OpenTraceEditor(name, out traceExt);
                    var res = ExecuteTraceControlOnEditor(editorObj, name, act, traceExt, triggerOptions);
                    CodesysContext.LogToIde($"[TRACE] control_trace({name}, {act}) -> success={res.success}, packet={res.packetState}, trigger={res.triggerState}");
                    return res;
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    return new TraceControlResult
                    {
                        success = false,
                        traceName = name,
                        action = act,
                        message = $"Failed to control trace '{name}': {inner.Message}"
                    };
                }
            });
        }

        private static string TryExportLiveTraceToFile(string traceName, string targetFilePath)
        {
            if (string.IsNullOrWhiteSpace(traceName)) return null;
            if (CodesysContext.PrimaryProject == null)
            {
                throw new InvalidOperationException($"Cannot export live trace '{traceName}' when no CODESYS project is open.");
            }
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                CodesysOnlineHelper.EnsureExecutionStack();
                object editorObj = OpenTraceEditor(traceName);

                var saveMethod = editorObj.GetType().GetMethod("save", new Type[] { typeof(string) });
                if (saveMethod == null)
                    throw new InvalidOperationException("Method 'save(string)' not found on ScriptTraceEditorObject.");

                string safeName = string.Join("_", traceName.Split(Path.GetInvalidFileNameChars()));
                string outPath = !string.IsNullOrWhiteSpace(targetFilePath)
                    ? targetFilePath
                    : Path.Combine(Path.GetTempPath(), $"codesys_trace_{safeName}.trace");

                string dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (File.Exists(outPath))
                {
                    try { File.Delete(outPath); } catch { }
                }

                saveMethod.Invoke(editorObj, new object[] { outPath });
                return outPath;
            });
        }

        public static TraceAnalysisResult AnalyzeTraceFile(
            string filePath,
            double deadband = 0.01,
            string filterCondition = null,
            bool includeSeries = false,
            double? startMs = null,
            double? endMs = null,
            int maxPoints = 0,
            string traceName = null)
        {
            string exportedPath = null;
            bool hasTraceName = !string.IsNullOrWhiteSpace(traceName);
            bool hasExistingFile = !string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath);
            bool shouldExportLive = hasTraceName && (CodesysContext.PrimaryProject != null || !hasExistingFile);
            if (shouldExportLive)
            {
                try
                {
                    exportedPath = TryExportLiveTraceToFile(traceName.Trim(), filePath);
                    if (!string.IsNullOrEmpty(exportedPath))
                    {
                        filePath = exportedPath;
                    }
                }
                catch (Exception ex)
                {
                    return new TraceAnalysisResult
                    {
                        success = false,
                        message = $"Failed to export live trace '{traceName}': {ex.InnerException?.Message ?? ex.Message}"
                    };
                }
            }

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
                TraceAnalysisResult analyzed;
                string ext = Path.GetExtension(filePath)?.ToLowerInvariant();
                if (ext == ".trace" || ext == ".xml")
                {
                    var doc = new XmlDocument();
                    doc.Load(filePath);
                    analyzed = AnalyzeTraceXmlDocument(doc, deadband, filterCondition, includeSeries, startMs, endMs, maxPoints);
                }
                else if (ext == ".csv")
                {
                    string csv = File.ReadAllText(filePath);
                    analyzed = AnalyzeTraceCsvContent(csv, deadband, filterCondition, includeSeries, startMs, endMs, maxPoints);
                }
                else
                {
                    bool looksXml = false;
                    using (var reader = new StreamReader(filePath))
                    {
                        int ch;
                        while ((ch = reader.Read()) != -1 && char.IsWhiteSpace((char)ch)) { }
                        if (ch == '<') looksXml = true;
                    }
                    if (looksXml)
                    {
                        var doc = new XmlDocument();
                        doc.Load(filePath);
                        analyzed = AnalyzeTraceXmlDocument(doc, deadband, filterCondition, includeSeries, startMs, endMs, maxPoints);
                    }
                    else
                    {
                        string csv = File.ReadAllText(filePath);
                        analyzed = AnalyzeTraceCsvContent(csv, deadband, filterCondition, includeSeries, startMs, endMs, maxPoints);
                    }
                }

                if (!string.IsNullOrEmpty(exportedPath))
                {
                    analyzed.exportedFilePath = exportedPath;
                }
                return analyzed;
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
                    case ">": return dActual > dTarget;
                    case "<": return dActual < dTarget;
                    case "==":
                    case "=": return Math.Abs(dActual - dTarget) < 0.0001;
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
    }
}
