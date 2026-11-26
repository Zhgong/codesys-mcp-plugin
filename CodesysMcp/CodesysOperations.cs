using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using _3S.CoDeSys.ScriptEngine;
using _3S.CoDeSys.ScriptEngine.BasicFunctionality;

namespace CodesysMcp
{
    public class SearchMatchItem
    {
        public string objectName { get; set; }
        public string objectType { get; set; }
        public string section { get; set; } // "declaration" or "implementation"
        public int lineNumber { get; set; }
        public string lineContent { get; set; }
        public string matchedText { get; set; }
    }

    public class SearchCodeResult
    {
        public string query { get; set; }
        public bool isRegex { get; set; }
        public bool caseSensitive { get; set; }
        public string scope { get; set; }
        public int totalMatches { get; set; }
        public bool truncated { get; set; }
        public List<SearchMatchItem> matches { get; set; } = new List<SearchMatchItem>();
    }

    public class ProjectInfoResult
    {
        public bool isOpen { get; set; }
        public string path { get; set; }
        public string name { get; set; }
        public bool isDirty { get; set; }
        public string activeApplication { get; set; }
        public bool mirrorEnabled { get; set; }
        public string mirrorDir { get; set; }
        public string mirrorMode { get; set; }
        public bool mirrorGitEnabled { get; set; }
        public string ideVersion { get; set; }
        public string ideProfile { get; set; }
        public string ideExePath { get; set; }
        public string uiMode { get; set; }
        public string mcpServerVersion { get; set; }
    }

    public class TreeNodeResult
    {
        public string name { get; set; }
        public string guid { get; set; }
        public string type { get; set; }
        public bool hasDeclaration { get; set; }
        public bool hasImplementation { get; set; }
        public List<TreeNodeResult> children { get; set; } = new List<TreeNodeResult>();
    }

    public class PouContentResult
    {
        public string name { get; set; }
        public string guid { get; set; }
        public string type { get; set; }
        public string declaration { get; set; }
        public string implementation { get; set; }
        public bool hasDeclaration { get; set; }
        public bool hasImplementation { get; set; }
    }

    public class PouSnapshot
    {
        public string Name { get; set; }
        public string PouType { get; set; }
        public string OriginalDeclaration { get; set; }
        public string OriginalImplementation { get; set; }
        public bool ExistedBefore { get; set; }
        public bool IsNew => !ExistedBefore;
        public bool IsDeleted { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class PouDiffItem
    {
        public string name { get; set; }
        public string status { get; set; } // "modified", "created", "deleted", "unchanged"
        public string declarationDiff { get; set; }
        public string implementationDiff { get; set; }
        public string unifiedDiff { get; set; }
    }

    public class ProjectDiffResult
    {
        public int modifiedCount { get; set; }
        public List<PouDiffItem> diffs { get; set; } = new List<PouDiffItem>();
        public string fullUnifiedDiff { get; set; } = string.Empty;
    }

    public class RollbackResult
    {
        public string name { get; set; }
        public bool success { get; set; }
        public string action { get; set; } // "reverted", "deleted", "restored", "none"
        public string message { get; set; }
    }

    public class PreviewDiffResult
    {
        public bool success { get; set; }
        public int modifiedCount { get; set; }
        public string viewer { get; set; }
        public string exportDir { get; set; }
        public string reportHtml { get; set; }
        public List<string> exportedPous { get; set; } = new List<string>();
        public string launchedCommand { get; set; }
        public string message { get; set; }
    }

    public class CompilerMessageResult
    {
        public string category { get; set; }
        public string text { get; set; }
        public string severity { get; set; }
        public int number { get; set; }
        public string prefix { get; set; }
        public string objectName { get; set; }
        public string positionText { get; set; }
        public int line { get; set; }
        public int column { get; set; }
    }

    public class MessageCategoryInfo
    {
        public string name { get; set; }
        public string guid { get; set; }
        public int totalMessages { get; set; }
        public int errors { get; set; }
        public int warnings { get; set; }
    }

    public class MessageCategoriesResult
    {
        public List<MessageCategoryInfo> categories { get; set; } = new List<MessageCategoryInfo>();
    }

    public class CompileResult
    {
        public bool success { get; set; }
        public int errorCount { get; set; }
        public int warningCount { get; set; }
        public List<CompilerMessageResult> messages { get; set; } = new List<CompilerMessageResult>();
    }

    public static partial class CodesysOperations
    {
        public static TExt GetExtension<TExt>(object obj) where TExt : class
        {
            if (obj == null) return null;
            if (obj is TExt direct) return direct;

            if (obj is IExtendedObject<IScriptObject> extObj)
            {
                if (extObj.BaseObject is TExt b1) return b1;
                if (extObj.Extensions != null)
                {
                    foreach (var ext in extObj.Extensions)
                    {
                        if (ext is TExt match) return match;
                    }
                }
            }

            if (obj is IExtendedObject<IScriptProject> extProj)
            {
                if (extProj.BaseObject is TExt b2) return b2;
                if (extProj.Extensions != null)
                {
                    foreach (var ext in extProj.Extensions)
                    {
                        if (ext is TExt match) return match;
                    }
                }
            }

            try
            {
                var baseProp = obj.GetType().GetProperty("BaseObject");
                if (baseProp != null)
                {
                    var baseVal = baseProp.GetValue(obj, null);
                    if (baseVal is TExt baseMatch) return baseMatch;
                }

                var extsProp = obj.GetType().GetProperty("Extensions");
                if (extsProp != null)
                {
                    var exts = extsProp.GetValue(obj, null) as System.Collections.IEnumerable;
                    if (exts != null)
                    {
                        foreach (var ext in exts)
                        {
                            if (ext is TExt match) return match;
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        private static readonly Dictionary<string, PouSnapshot> _snapshots = new Dictionary<string, PouSnapshot>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, PouSnapshot> _projectBaselines = new Dictionary<string, PouSnapshot>(StringComparer.OrdinalIgnoreCase);

        public static void ClearSnapshotsForTesting()
        {
            _snapshots.Clear();
            _projectBaselines.Clear();
            CodesysOnlineHelper.ClearDutMembersCache();
        }

        public static ProjectInfoResult GetProjectInfo()
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var env = CodesysIdeUiHelper.GetIdeEnvironmentInfo();
                var proj = CodesysContext.PrimaryProject;
                if (proj == null)
                {
                    return new ProjectInfoResult
                    {
                        isOpen = false,
                        ideVersion = env.ideVersion,
                        ideProfile = env.ideProfile,
                        ideExePath = env.ideExePath,
                        uiMode = env.uiMode,
                        mcpServerVersion = env.mcpServerVersion
                    };
                }

                string path = proj.path ?? string.Empty;
                string name = !string.IsNullOrEmpty(path) ? Path.GetFileName(path) : "Untitled";
                var activeAppExt = proj.active_application;
                string appName = null;
                if (activeAppExt != null)
                {
                    var scriptObj = activeAppExt.BaseObject;
                    appName = scriptObj.get_name();
                }

                bool mirrorActive = SourceMirrorHelper.HasActiveMirror;
                string activeMirrorDir = mirrorActive ? SourceMirrorHelper.ActiveMirrorDirectory : null;
                bool mirrorGit = mirrorActive && !string.IsNullOrWhiteSpace(activeMirrorDir) && Directory.Exists(Path.Combine(activeMirrorDir, ".git"));
                return new ProjectInfoResult
                {
                    isOpen = true,
                    path = path,
                    name = name,
                    isDirty = proj.dirty,
                    activeApplication = appName,
                    mirrorEnabled = mirrorActive,
                    mirrorDir = activeMirrorDir,
                    mirrorMode = mirrorActive ? "one-way (CODESYS -> .st)" : "disabled",
                    mirrorGitEnabled = mirrorGit,
                    ideVersion = env.ideVersion,
                    ideProfile = env.ideProfile,
                    ideExePath = env.ideExePath,
                    uiMode = env.uiMode,
                    mcpServerVersion = env.mcpServerVersion
                };
            });
        }

        public static ProjectInfoResult OpenProject(string projectPath)
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                if (string.IsNullOrEmpty(projectPath))
                    throw new ArgumentException("Parameter 'projectPath' cannot be empty.");

                if (!File.Exists(projectPath))
                    throw new FileNotFoundException("Project file not found: " + projectPath);

                if (CodesysContext.Projects == null)
                    throw new InvalidOperationException("CodesysContext.Projects is not initialized.");

                CodesysContext.Projects.open(projectPath);
                _snapshots.Clear();
                _projectBaselines.Clear();
                CodesysOnlineHelper.ClearDutMembersCache();
                _autoBoundProjectPath = null;
                var info = GetProjectInfo();
                try
                {
                    EnsureDefaultMirrorForOpenProjectInternal();
                }
                catch (Exception ex)
                {
                    CodesysContext.LogToIde($"[CodesysMcp] Auto-mirror on open failed: {ex.Message}");
                }
                CodesysIdeUiHelper.EnsureIndicatorVisible();
                return info;
            }, 120000);
        }

        public static bool CloseProject()
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var proj = CodesysContext.PrimaryProject;
                if (proj == null) return false;
                proj.close();
                _snapshots.Clear();
                _projectBaselines.Clear();
                CodesysOnlineHelper.ClearDutMembersCache();
                _autoBoundProjectPath = null;
                _lastProjectDirty = false;
                _lastProjectFileWriteUtc = DateTime.MinValue;
                SourceMirrorHelper.ClearIdeContentHashes();
                SourceMirrorHelper.ClearMirrorDirectory();
                CodesysIdeUiHelper.RefreshMirrorIndicator();
                CodesysIdeUiHelper.EnsureIndicatorVisible();
                return true;
            });
        }

        public static List<TreeNodeResult> GetProjectTree(bool full = false)
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var proj = CodesysContext.PrimaryProject;
                if (proj == null) return new List<TreeNodeResult>();

                var treeObj = proj as IScriptTreeObject ?? GetExtension<IScriptTreeObject>(CodesysContext.PrimaryProjectExtended);
                if (treeObj == null) return new List<TreeNodeResult>();

                var children = treeObj.get_children(false);
                var result = new List<TreeNodeResult>();
                if (children != null)
                {
                    foreach (var child in children)
                    {
                        result.Add(BuildTreeNode(child, full));
                    }
                }
                return result;
            });
        }

        private static TreeNodeResult BuildTreeNode(IExtendedObject<IScriptObject> extObj, bool full)
        {
            var obj = extObj.BaseObject;
            string name = obj.get_name();
            string guid = obj.guid.ToString();
            string type = obj.type.ToString();

            var marker = GetExtension<IScriptTextualObjectMarker>(extObj);
            bool hasDecl = marker != null && marker.has_textual_declaration;
            bool hasImpl = marker != null && marker.has_textual_implementation;

            var node = new TreeNodeResult
            {
                name = name,
                guid = guid,
                type = type,
                hasDeclaration = hasDecl,
                hasImplementation = hasImpl
            };

            var treeObj = obj as IScriptTreeObject ?? GetExtension<IScriptTreeObject>(extObj);
            if (treeObj != null)
            {
                var children = treeObj.get_children(false);
                if (children != null)
                {
                    foreach (var child in children)
                    {
                        node.children.Add(BuildTreeNode(child, full));
                    }
                }
            }

            return node;
        }

        public static object FindObjectByName(string name)
        {
            var proj = CodesysContext.PrimaryProject;
            if (proj == null) return null;
            var treeObj = proj as IScriptTreeObject ?? GetExtension<IScriptTreeObject>(CodesysContext.PrimaryProjectExtended);
            if (treeObj == null) return null;
            if (string.IsNullOrEmpty(name)) return null;
            // Support dot or slash notation: e.g. "FB_Test_ForceSensor.Reset" or "Tests/Framework/FB_TestCase"
            if (name.Contains(".") || name.Contains("/"))
            {
                char sep = name.Contains("/") ? '/' : '.';
                var dotIdx = name.LastIndexOf(sep);
                var parentName = name.Substring(0, dotIdx);
                var childName = name.Substring(dotIdx + 1);
                var parent = FindObjectByName(parentName);
                if (parent != null)
                {
                    var parentTree = parent as IScriptTreeObject ?? GetExtension<IScriptTreeObject>(parent);
                    if (parentTree != null)
                    {
                        var childMatches = parentTree.find(childName, true);
                        if (childMatches != null && childMatches.Count > 0)
                        {
                            foreach (var match in childMatches)
                            {
                                var marker = GetExtension<IScriptTextualObjectMarker>(match);
                                if (marker != null && (marker.has_textual_declaration || marker.has_textual_implementation))
                                {
                                    return match;
                                }
                            }
                            return childMatches[0];
                        }
                    }
                }
            }
            var matches = treeObj.find(name, true);
            if (matches != null && matches.Count > 0)
            {
                foreach (var match in matches)
                {
                    var marker = GetExtension<IScriptTextualObjectMarker>(match);
                    if (marker != null && (marker.has_textual_declaration || marker.has_textual_implementation))
                    {
                        return match;
                    }
                }
                return matches[0];
            }
            return null;
        }

        public static object GetActiveApplicationObject(IScriptProject proj)
        {
            if (proj == null) return null;
            var activeAppExt = proj.active_application;
            if (activeAppExt != null) return activeAppExt;

            var treeObj = proj as IScriptTreeObject;
            if (treeObj != null)
            {
                var matches = treeObj.find("Application", true);
                if (matches != null && matches.Count > 0)
                {
                    return matches[0];
                }
            }
            return null;
        }

        public static bool SaveProject()
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var proj = CodesysContext.PrimaryProject;
                if (proj == null) return false;
                proj.save();
                try
                {
                    _lastProjectDirty = proj.dirty;
                    string savedPath = proj.path;
                    _lastProjectFileWriteUtc = (!string.IsNullOrWhiteSpace(savedPath) && File.Exists(savedPath))
                        ? File.GetLastWriteTimeUtc(savedPath)
                        : DateTime.MinValue;
                    EnsureDefaultMirrorForOpenProjectInternal();
                    if (SourceMirrorHelper.HasActiveMirror)
                    {
                        string mirrorDir = SourceMirrorHelper.ActiveMirrorDirectory;
                        SyncSourcesToDiskInternal(mirrorDir, overwriteBaselines: true);
                        string projName = !string.IsNullOrWhiteSpace(savedPath) ? Path.GetFileName(savedPath) : "project";
                        CommitMirrorWithLog(mirrorDir, $"chore(save): sync .st on project save ({projName})");
                    }
                }
                catch (Exception ex)
                {
                    CodesysContext.LogToIde($"[CodesysMcp] Mirror sync/commit on save failed: {ex.Message}");
                }
                return true;
            });
        }

        public static string CreateBootApplication(string appName = null)
        {
            return CodesysOnlineHelper.CreateBootApplication(appName);
        }

        public static OnlineStatusResult GetOnlineStatus()
        {
            return CodesysOnlineHelper.GetOnlineStatus();
        }

        public static OnlineStatusResult PlcLogin(
            string mode = "try_online_change",
            bool deleteForeignApps = true,
            string appName = null,
            string username = null,
            string password = null,
            bool ensureRunning = false,
            bool autoRecoverException = false)
        {
            return CodesysOnlineHelper.PlcLogin(mode, deleteForeignApps, appName, username, password, ensureRunning, autoRecoverException);
        }

        public static object InspectOnline(string targetName = "Device")
        {
            return CodesysOnlineHelper.InspectOnline(targetName);
        }

        public static OnlineStatusResult PlcLogout()
        {
            return CodesysOnlineHelper.PlcLogout();
        }

        public static OnlineStatusResult PlcControl(string action)
        {
            return CodesysOnlineHelper.PlcControl(action);
        }

        public static ReadVariablesResult ReadVariables(List<string> expressions)
        {
            return CodesysOnlineHelper.ReadVariables(expressions);
        }

        public static WriteVariableResult WriteVariable(string name, string value, bool force = false)
        {
            return CodesysOnlineHelper.WriteVariable(name, value, force);
        }

        public static PulseVariableResult PulseVariable(
            string name,
            int pulseMs = 100,
            string activeValue = "TRUE",
            string inactiveValue = "FALSE",
            bool preClear = false,
            bool force = false)
        {
            return CodesysOnlineHelper.PulseVariable(name, pulseMs, activeValue, inactiveValue, preClear, force);
        }

        public static WaitForVariableResult WaitForVariable(
            string name,
            string targetValue,
            string op = "==",
            int timeoutMs = 5000,
            int pollMs = 50)
        {
            return CodesysOnlineHelper.WaitForVariable(name, targetValue, op, timeoutMs, pollMs);
        }

        public static OnlineStatusResult UnforceVariables(List<string> expressions = null)
        {
            return CodesysOnlineHelper.UnforceVariables(expressions);
        }

        public static BatchWriteResult BatchWriteVariables(Dictionary<string, string> vars, bool verify = true, bool force = false)
        {
            return CodesysOnlineHelper.BatchWriteVariables(vars, verify, force);
        }

        public static BatchWriteResult WriteStruct(string structName, object values, bool verify = true, bool force = false)
        {
            return CodesysOnlineHelper.WriteStruct(structName, values, verify, force);
        }

        public static ReadArrayResult ReadArray(string arrayName, int startIndex, int count)
        {
            return CodesysOnlineHelper.ReadArray(arrayName, startIndex, count);
        }

        public static ReadStructResult ReadStruct(string structName, List<string> memberNames = null)
        {
            return CodesysOnlineHelper.ReadStruct(structName, memberNames);
        }

        public static List<string> GetDutMembers(string dutName)
        {
            return CodesysOnlineHelper.GetDutMembers(dutName);
        }

        public static object AddTraceVariable(string variableName, string traceName = "Trace")
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                if (string.IsNullOrEmpty(variableName))
                    throw new ArgumentException("Parameter 'variable_name' is required.");

                var tObj = FindObjectByName(traceName ?? "Trace");
                if (tObj == null)
                    throw new FileNotFoundException($"Trace object '{traceName}' not found in active project.");

                object traceExt = null;
                var extProp = tObj.GetType().GetProperty("Extensions");
                if (extProp != null)
                {
                    var exts = extProp.GetValue(tObj, null) as System.Collections.IEnumerable;
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
                }

                if (traceExt == null)
                    throw new InvalidOperationException("ScriptTraceObject extension not found on Trace object.");

                var varListProp = traceExt.GetType().GetProperty("variable_list");
                var varList = varListProp?.GetValue(traceExt, null);
                if (varList == null)
                    throw new InvalidOperationException("variable_list property not found on ScriptTraceObject.");

                var getEnumeratorMethod = varList.GetType().GetMethod("GetEnumerator");
                var enumerator = getEnumeratorMethod?.Invoke(varList, null) as System.Collections.IEnumerator;
                object existingVar = null;
                if (enumerator != null)
                {
                    while (enumerator.MoveNext())
                    {
                        var cur = enumerator.Current;
                        if (cur != null)
                        {
                            var nameProp = cur.GetType().GetProperty("variable_name");
                            string curName = nameProp?.GetValue(cur, null)?.ToString();
                            if (string.Equals(curName, variableName, StringComparison.OrdinalIgnoreCase))
                            {
                                existingVar = cur;
                                break;
                            }
                        }
                    }
                }

                object targetVar = existingVar;
                bool addedNew = false;
                if (targetVar == null)
                {
                    var addMethod = varList.GetType().GetMethod("add", Type.EmptyTypes);
                    if (addMethod == null)
                        throw new InvalidOperationException("add() method not found on variable_list.");
                    targetVar = addMethod.Invoke(varList, null);
                    var varNameProp = targetVar.GetType().GetProperty("variable_name");
                    varNameProp?.SetValue(targetVar, variableName, null);
                    addedNew = true;
                }

                var diagramsProp = traceExt.GetType().GetProperty("diagrams");
                var diagrams = diagramsProp?.GetValue(traceExt, null) as System.Collections.IEnumerable;
                int diagCount = 0;
                if (diagrams != null)
                {
                    foreach (var diag in diagrams)
                    {
                        diagCount++;
                        try
                        {
                            var addDiagVar = diag.GetType().GetMethod("add_diagram_variable");
                            if (addDiagVar != null)
                            {
                                addDiagVar.Invoke(diag, new object[] { targetVar, false });
                            }
                        }
                        catch { }
                    }
                }

                CodesysContext.LogToIde($"[SUCCESS] add_trace_variable: Added '{variableName}' to Trace '{traceName}' (Diagrams updated: {diagCount})");
                return new
                {
                    success = true,
                    traceName = traceName ?? "Trace",
                    variableName,
                    addedNew,
                    diagramsUpdated = diagCount,
                    message = $"Successfully added '{variableName}' to Trace '{traceName}'."
                };
            });
        }

        public static object GetTraceInfo(string traceName = "Trace")
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var tObj = FindObjectByName(traceName ?? "Trace");
                if (tObj == null)
                    throw new FileNotFoundException($"Trace object '{traceName}' not found in active project.");

                object traceExt = null;
                var extProp = tObj.GetType().GetProperty("Extensions");
                if (extProp != null)
                {
                    var exts = extProp.GetValue(tObj, null) as System.Collections.IEnumerable;
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
                }

                if (traceExt == null)
                    throw new InvalidOperationException("ScriptTraceObject extension not found on Trace object.");

                var varListProp = traceExt.GetType().GetProperty("variable_list");
                var varList = varListProp?.GetValue(traceExt, null);
                var variables = new List<string>();
                if (varList != null)
                {
                    var getEnumeratorMethod = varList.GetType().GetMethod("GetEnumerator");
                    var enumerator = getEnumeratorMethod?.Invoke(varList, null) as System.Collections.IEnumerator;
                    if (enumerator != null)
                    {
                        while (enumerator.MoveNext())
                        {
                            var cur = enumerator.Current;
                            if (cur != null)
                            {
                                var nameProp = cur.GetType().GetProperty("variable_name");
                                string curName = nameProp?.GetValue(cur, null)?.ToString();
                                if (!string.IsNullOrEmpty(curName))
                                    variables.Add(curName);
                            }
                        }
                    }
                }

                var diagramsProp = traceExt.GetType().GetProperty("diagrams");
                var diagrams = diagramsProp?.GetValue(traceExt, null) as System.Collections.IEnumerable;
                int diagCount = 0;
                if (diagrams != null)
                {
                    foreach (var diag in diagrams) diagCount++;
                }

                var cfg = new TraceControlResult();
                PlcTraceAndLoggerHelper.PopulateTraceConfiguration(traceExt, cfg);

                return new
                {
                    success = true,
                    traceName = traceName ?? "Trace",
                    variableCount = variables.Count,
                    variables,
                    diagramCount = diagCount,
                    cfg.taskName,
                    cfg.triggerEnabled,
                    cfg.triggerVariable,
                    cfg.triggerEdge,
                    cfg.triggerLevel,
                    cfg.postTriggerSamples,
                    cfg.recordCondition,
                    cfg.everyNCycles,
                    cfg.autoStart
                };
            });
        }

        public static UiIndicatorInfo GetUiIndicatorStatus()
        {
            return CodesysIdeUiHelper.GetIndicatorInfo();
        }

        public static IdeScreenshotResult CaptureIdeScreenshot(string outputPath = null, int maxWidth = 0)
        {
            return CodesysIdeUiHelper.CaptureIdeScreenshot(outputPath, maxWidth);
        }

        public static TraceControlResult ControlTrace(
            string traceName = "Trace",
            string action = "status",
            TraceTriggerConfigOptions triggerOptions = null)
        {
            return PlcTraceAndLoggerHelper.ControlTrace(traceName, action, triggerOptions);
        }

        public static TraceAnalysisResult AnalyzeTrace(
            string filePath,
            double deadband = 0.01,
            string filterCondition = null,
            bool includeSeries = false,
            double? startMs = null,
            double? endMs = null,
            int maxPoints = 0,
            string traceName = null)
        {
            return PlcTraceAndLoggerHelper.AnalyzeTraceFile(
                filePath,
                deadband,
                filterCondition,
                includeSeries,
                startMs,
                endMs,
                maxPoints,
                traceName);
        }

        public static PlcLoggerSetupResult SetupPlcLogger(int bufferSize = 64, string autoInjectPou = null)
        {
            return PlcTraceAndLoggerHelper.SetupPlcLogger(bufferSize, autoInjectPou);
        }

        public static PlcLogsResult ReadPlcLogs(string tagFilter = null, int lastN = 32, bool clearAfterRead = false)
        {
            return PlcTraceAndLoggerHelper.ReadPlcLogs(tagFilter, lastN, clearAfterRead);
        }

        public static SetupDeviceLoggerResult SetupDeviceLogger()
        {
            return PlcTraceAndLoggerHelper.SetupDeviceLogger();
        }

        public static PouContentResult InjectCodeLog(string pouName, string targetPattern, string logMessage, int level = 1, bool insertAfter = true)
        {
            return PlcTraceAndLoggerHelper.InjectCodeLog(pouName, targetPattern, logMessage, level, insertAfter);
        }

        public static DeviceLogResult ReadDeviceLog(string filePath = null, string severityFilter = null, string keyword = null, string componentFilter = null, int maxEntries = 50, string saveToPath = null)
        {
            return PlcTraceAndLoggerHelper.ReadDeviceLog(filePath, severityFilter, keyword, componentFilter, maxEntries, saveToPath);
        }
    }
}
