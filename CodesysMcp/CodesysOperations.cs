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
        public string text { get; set; }
        public string severity { get; set; }
        public int number { get; set; }
        public string prefix { get; set; }
        public string objectName { get; set; }
        public string positionText { get; set; }
        public int line { get; set; }
        public int column { get; set; }
    }

    public class CompileResult
    {
        public bool success { get; set; }
        public int errorCount { get; set; }
        public int warningCount { get; set; }
        public List<CompilerMessageResult> messages { get; set; } = new List<CompilerMessageResult>();
    }

    public static class CodesysOperations
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

        public static void ClearSnapshotsForTesting()
        {
            _snapshots.Clear();
        }

        public static ProjectInfoResult GetProjectInfo()
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var proj = CodesysContext.PrimaryProject;
                if (proj == null)
                {
                    return new ProjectInfoResult { isOpen = false };
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

                return new ProjectInfoResult
                {
                    isOpen = true,
                    path = path,
                    name = name,
                    isDirty = proj.dirty,
                    activeApplication = appName
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
                return GetProjectInfo();
            });
        }

        public static bool CloseProject()
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var proj = CodesysContext.PrimaryProject;
                if (proj == null) return false;
                proj.close();
                _snapshots.Clear();
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

        public static PouContentResult ReadPou(string name)
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var extObj = FindObjectByName(name);
                if (extObj == null)
                {
                    throw new FileNotFoundException("POU/Object not found: " + name);
                }

                var obj = GetExtension<IScriptObject>(extObj) ?? extObj as IScriptObject;
                if (obj == null) throw new InvalidOperationException("Could not resolve IScriptObject for POU: " + name);
                var marker = GetExtension<IScriptTextualObjectMarker>(extObj);
                bool hasDecl = marker != null && marker.has_textual_declaration;
                bool hasImpl = marker != null && marker.has_textual_implementation;
                string declText = string.Empty;
                string implText = string.Empty;

                if (hasDecl)
                {
                    var declExt = GetExtension<IScriptObjectWithTextualDeclaration>(extObj);
                    var doc = declExt?.textual_declaration;
                    if (doc != null) declText = doc.text ?? string.Empty;
                }

                if (hasImpl)
                {
                    var implExt = GetExtension<IScriptObjectWithTextualImplementation>(extObj);
                    var doc = implExt?.textual_implementation;
                    if (doc != null) implText = doc.text ?? string.Empty;
                }

                return new PouContentResult
                {
                    name = obj.get_name(),
                    guid = obj.guid.ToString(),
                    type = obj.type.ToString(),
                    declaration = declText,
                    implementation = implText,
                    hasDeclaration = hasDecl,
                    hasImplementation = hasImpl
                };
            });
        }

        public static PouContentResult WritePou(string name, string declaration, string implementation, string pouTypeStr = "Program")
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var extObj = FindObjectByName(name);

                // Capture baseline snapshot before first modification in session
                if (!_snapshots.ContainsKey(name))
                {
                    if (extObj != null)
                    {
                        var current = ReadPou(name);
                        _snapshots[name] = new PouSnapshot
                        {
                            Name = name,
                            PouType = current.type,
                            OriginalDeclaration = current.declaration,
                            OriginalImplementation = current.implementation,
                            ExistedBefore = true,
                            IsDeleted = false,
                            CreatedAt = DateTime.UtcNow
                        };
                    }
                    else
                    {
                        _snapshots[name] = new PouSnapshot
                        {
                            Name = name,
                            PouType = pouTypeStr,
                            OriginalDeclaration = string.Empty,
                            OriginalImplementation = string.Empty,
                            ExistedBefore = false,
                            IsDeleted = false,
                            CreatedAt = DateTime.UtcNow
                        };
                    }
                }

                if (extObj == null)
                {
                    var projExt = CodesysContext.PrimaryProjectExtended;
                    if (projExt == null) throw new InvalidOperationException("No active project open.");

                    var appExt = GetActiveApplicationObject(projExt.BaseObject);
                    var container = GetExtension<IScriptIecLanguageObjectContainer>(appExt)
                                 ?? GetExtension<IScriptIecLanguageObjectContainer>(projExt);

                    if (container == null)
                    {
                        throw new InvalidOperationException("Could not resolve IScriptIecLanguageObjectContainer on active application or project.");
                    }

                    PouType pouType = PouType.Program;
                    string returnType = null;
                    if (string.Equals(pouTypeStr, "FunctionBlock", StringComparison.OrdinalIgnoreCase))
                    {
                        pouType = PouType.FunctionBlock;
                    }
                    else if (string.Equals(pouTypeStr, "Function", StringComparison.OrdinalIgnoreCase))
                    {
                        pouType = PouType.Function;
                        returnType = "BOOL";
                    }

                    extObj = container.create_pou(name, pouType, null, returnType, null, null);
                }

                if (extObj == null) throw new Exception("Failed to find or create POU: " + name);

                if (declaration != null)
                {
                    var declExt = GetExtension<IScriptObjectWithTextualDeclaration>(extObj);
                    declExt?.textual_declaration?.replace(declaration);
                }

                if (implementation != null)
                {
                    var implExt = GetExtension<IScriptObjectWithTextualImplementation>(extObj);
                    implExt?.textual_implementation?.replace(implementation);
                }

                return ReadPou(name);
            });
        }

        public static bool DeletePou(string name)
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var extObj = FindObjectByName(name);
                if (extObj == null) return false;

                // Capture baseline snapshot before deletion
                if (!_snapshots.ContainsKey(name))
                {
                    var current = ReadPou(name);
                    _snapshots[name] = new PouSnapshot
                    {
                        Name = name,
                        PouType = current.type,
                        OriginalDeclaration = current.declaration,
                        OriginalImplementation = current.implementation,
                        ExistedBefore = true,
                        IsDeleted = true,
                        CreatedAt = DateTime.UtcNow
                    };
                }

                var obj = GetExtension<IScriptObject>(extObj) ?? extObj as IScriptObject;
                obj?.remove();
                return true;
            });
        }

        public static CompileResult CompileProject(bool clean = false, bool rebuild = false)
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var proj = CodesysContext.PrimaryProject;
                if (proj == null) throw new InvalidOperationException("No active project open.");

                var appExt = GetActiveApplicationObject(proj);
                var app = GetExtension<IScriptApplication>(appExt);
                if (app == null) throw new InvalidOperationException("No active application found in project.");

                if (clean)
                {
                    app.clean();
                }

                if (rebuild)
                {
                    app.rebuild();
                }
                else
                {
                    app.build();
                }

                return CollectCompilerMessages();
            });
        }

        public static CompileResult CollectCompilerMessages()
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var sys3 = CodesysContext.System as ISystem3;
                var res = new CompileResult();

                if (sys3 != null)
                {
                    var messages = sys3.get_message_objects();
                    if (messages != null)
                    {
                        foreach (var msg in messages)
                        {
                            var scriptMsg = msg as IScriptMessage;
                            if (scriptMsg == null) continue;

                            string severity = scriptMsg.severity.ToString();
                            string text = scriptMsg.text ?? string.Empty;
                            int number = (int)(scriptMsg.number ?? 0);
                            string prefix = scriptMsg.prefix ?? string.Empty;
                            string posText = scriptMsg.position_text ?? string.Empty;

                            var targetObj = scriptMsg.@object;
                            string objName = null;
                            if (targetObj != null)
                            {
                                var extObj = targetObj as IExtendedObject<IScriptObject>;
                                if (extObj != null) objName = extObj.BaseObject.get_name();
                                else if (targetObj is IScriptObject so) objName = so.get_name();
                            }

                            if (severity.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                severity.IndexOf("Fatal", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                res.errorCount++;
                            }
                            else if (severity.IndexOf("Warning", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                res.warningCount++;
                            }

                            res.messages.Add(new CompilerMessageResult
                            {
                                text = text,
                                severity = severity,
                                number = number,
                                prefix = prefix,
                                objectName = objName,
                                positionText = posText
                            });
                        }
                    }
                }

                res.success = res.errorCount == 0;
                return res;
            });
        }

        public static List<string> GetIdeMessages(string category = null)
        {
            return CodesysContext.GetIdeMessages(category);
        }

        public static List<SearchMatchItem> SearchInText(
            string text,
            string objName,
            string objType,
            string section,
            Regex regex,
            int maxSlots)
        {
            var list = new List<SearchMatchItem>();
            if (string.IsNullOrEmpty(text) || regex == null || maxSlots <= 0)
                return list;

            using (var reader = new StringReader(text))
            {
                string line;
                int lineNum = 1;
                while ((line = reader.ReadLine()) != null)
                {
                    var match = regex.Match(line);
                    if (match.Success)
                    {
                        list.Add(new SearchMatchItem
                        {
                            objectName = objName,
                            objectType = objType,
                            section = section,
                            lineNumber = lineNum,
                            lineContent = line,
                            matchedText = match.Value
                        });

                        if (list.Count >= maxSlots)
                            break;
                    }
                    lineNum++;
                }
            }
            return list;
        }

        public static SearchCodeResult SearchCode(
            string query,
            bool isRegex = false,
            bool caseSensitive = false,
            string scope = "all",
            int maxResults = 100)
        {
            if (string.IsNullOrEmpty(query))
                throw new ArgumentException("Parameter 'query' cannot be empty.");

            if (maxResults <= 0) maxResults = 100;
            if (string.IsNullOrEmpty(scope)) scope = "all";

            RegexOptions options = RegexOptions.None;
            if (!caseSensitive)
            {
                options |= RegexOptions.IgnoreCase;
            }

            string pattern = isRegex ? query : Regex.Escape(query);
            Regex regex = new Regex(pattern, options);

            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var proj = CodesysContext.PrimaryProject;
                var res = new SearchCodeResult
                {
                    query = query,
                    isRegex = isRegex,
                    caseSensitive = caseSensitive,
                    scope = scope,
                    matches = new List<SearchMatchItem>()
                };

                if (proj == null) return res;

                var treeObj = proj as IScriptTreeObject ?? GetExtension<IScriptTreeObject>(CodesysContext.PrimaryProjectExtended);
                if (treeObj == null) return res;

                var allObjects = new List<IExtendedObject<IScriptObject>>();
                CollectTextualObjects(treeObj, allObjects);

                foreach (var extObj in allObjects)
                {
                    if (res.matches.Count >= maxResults)
                    {
                        res.truncated = true;
                        break;
                    }

                    var baseObj = extObj.BaseObject;
                    string objName = baseObj.get_name();
                    string friendlyType = DetermineFriendlyType(baseObj);

                    if (!MatchesScope(friendlyType, scope))
                        continue;

                    var marker = GetExtension<IScriptTextualObjectMarker>(extObj);
                    if (marker == null) continue;

                    // 1. Search declaration
                    if (marker.has_textual_declaration)
                    {
                        var declExt = GetExtension<IScriptObjectWithTextualDeclaration>(extObj);
                        var doc = declExt?.textual_declaration;
                        if (doc != null && !string.IsNullOrEmpty(doc.text))
                        {
                            int remaining = maxResults - res.matches.Count;
                            var declMatches = SearchInText(doc.text, objName, friendlyType, "declaration", regex, remaining);
                            res.matches.AddRange(declMatches);
                        }
                    }

                    if (res.matches.Count >= maxResults)
                    {
                        res.truncated = true;
                        break;
                    }

                    // 2. Search implementation
                    if (marker.has_textual_implementation)
                    {
                        var implExt = GetExtension<IScriptObjectWithTextualImplementation>(extObj);
                        var doc = implExt?.textual_implementation;
                        if (doc != null && !string.IsNullOrEmpty(doc.text))
                        {
                            int remaining = maxResults - res.matches.Count;
                            var implMatches = SearchInText(doc.text, objName, friendlyType, "implementation", regex, remaining);
                            res.matches.AddRange(implMatches);
                        }
                    }
                }

                res.totalMatches = res.matches.Count;
                if (res.matches.Count >= maxResults)
                {
                    res.truncated = true;
                }
                return res;
            });
        }

        private static void CollectTextualObjects(IScriptTreeObject root, List<IExtendedObject<IScriptObject>> result)
        {
            if (root == null) return;
            var children = root.get_children(false);
            if (children == null) return;

            foreach (var child in children)
            {
                var marker = GetExtension<IScriptTextualObjectMarker>(child);
                if (marker != null && (marker.has_textual_declaration || marker.has_textual_implementation))
                {
                    result.Add(child);
                }

                var treeChild = child.BaseObject as IScriptTreeObject ?? GetExtension<IScriptTreeObject>(child);
                if (treeChild != null)
                {
                    CollectTextualObjects(treeChild, result);
                }
            }
        }

        private static string DetermineFriendlyType(IScriptObject baseObj)
        {
            if (baseObj == null) return "Unknown";
            string typeStr = baseObj.type.ToString();
            if (typeStr.IndexOf("6f9dac99", StringComparison.OrdinalIgnoreCase) >= 0) return "POU";
            if (typeStr.IndexOf("225bfe47", StringComparison.OrdinalIgnoreCase) >= 0) return "GVL";
            if (typeStr.IndexOf("2db5746d", StringComparison.OrdinalIgnoreCase) >= 0) return "DUT";
            return typeStr;
        }

        private static bool MatchesScope(string friendlyType, string scope)
        {
            if (string.IsNullOrEmpty(scope) || scope.Equals("all", StringComparison.OrdinalIgnoreCase))
                return true;

            string s = scope.ToLowerInvariant();
            string t = friendlyType.ToLowerInvariant();

            if (s.Contains("pou"))
            {
                return t.Contains("pou") || t.Contains("program") || t.Contains("function");
            }
            if (s.Contains("gvl"))
            {
                return t.Contains("gvl") || t.Contains("global");
            }
            if (s.Contains("dut"))
            {
                return t.Contains("dut") || t.Contains("type") || t.Contains("struct") || t.Contains("enum");
            }

            return true;
        }

        public static bool SaveProject()
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var proj = CodesysContext.PrimaryProject;
                if (proj == null) return false;
                proj.save();
                return true;
            });
        }

        public static ProjectDiffResult GetDiff(string name = null, string section = "all")
        {
            if (string.IsNullOrEmpty(section)) section = "all";

            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var result = new ProjectDiffResult();
                var targets = new List<string>();

                if (!string.IsNullOrEmpty(name))
                {
                    if (_snapshots.ContainsKey(name))
                    {
                        targets.Add(name);
                    }
                    else
                    {
                        return result;
                    }
                }
                else
                {
                    targets.AddRange(_snapshots.Keys);
                }

                var fullSb = new StringBuilder();

                foreach (var targetName in targets)
                {
                    var snap = _snapshots[targetName];
                    var diffItem = new PouDiffItem
                    {
                        name = targetName
                    };

                    string currentDecl = string.Empty;
                    string currentImpl = string.Empty;
                    bool currentExists = false;

                    try
                    {
                        var live = ReadPou(targetName);
                        if (live != null)
                        {
                            currentDecl = live.declaration ?? string.Empty;
                            currentImpl = live.implementation ?? string.Empty;
                            currentExists = true;
                        }
                    }
                    catch
                    {
                        currentExists = false;
                    }

                    if (!snap.ExistedBefore && currentExists)
                    {
                        diffItem.status = "created";
                    }
                    else if (snap.ExistedBefore && !currentExists)
                    {
                        diffItem.status = "deleted";
                    }
                    else if (snap.ExistedBefore && currentExists)
                    {
                        diffItem.status = "modified";
                    }
                    else
                    {
                        diffItem.status = "unchanged";
                    }

                    string declDiff = string.Empty;
                    string implDiff = string.Empty;

                    bool includeDecl = section.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                                       section.Equals("declaration", StringComparison.OrdinalIgnoreCase);
                    bool includeImpl = section.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                                       section.Equals("implementation", StringComparison.OrdinalIgnoreCase);

                    if (includeDecl)
                    {
                        declDiff = DiffHelper.GenerateUnifiedDiff(
                            snap.OriginalDeclaration,
                            currentDecl,
                            $"a/{targetName} (declaration)",
                            $"b/{targetName} (declaration)");
                        diffItem.declarationDiff = declDiff;
                    }

                    if (includeImpl)
                    {
                        implDiff = DiffHelper.GenerateUnifiedDiff(
                            snap.OriginalImplementation,
                            currentImpl,
                            $"a/{targetName} (implementation)",
                            $"b/{targetName} (implementation)");
                        diffItem.implementationDiff = implDiff;
                    }

                    var itemUnifiedSb = new StringBuilder();
                    if (!string.IsNullOrEmpty(declDiff))
                    {
                        itemUnifiedSb.AppendLine(declDiff);
                    }
                    if (!string.IsNullOrEmpty(implDiff))
                    {
                        if (itemUnifiedSb.Length > 0) itemUnifiedSb.AppendLine();
                        itemUnifiedSb.AppendLine(implDiff);
                    }

                    diffItem.unifiedDiff = itemUnifiedSb.ToString().TrimEnd();

                    if (!string.IsNullOrEmpty(diffItem.unifiedDiff) || diffItem.status != "unchanged")
                    {
                        result.diffs.Add(diffItem);
                        if (!string.IsNullOrEmpty(diffItem.unifiedDiff))
                        {
                            if (fullSb.Length > 0) fullSb.AppendLine("\n");
                            fullSb.Append(diffItem.unifiedDiff);
                        }
                    }
                }

                result.modifiedCount = result.diffs.Count;
                result.fullUnifiedDiff = fullSb.ToString();
                return result;
            });
        }

        public static RollbackResult RollbackPou(string name)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Parameter 'name' is required.");

            return CodesysContext.RunOnPrimaryThread(() =>
            {
                if (!_snapshots.ContainsKey(name))
                {
                    return new RollbackResult
                    {
                        name = name,
                        success = false,
                        action = "none",
                        message = $"No baseline snapshot found for '{name}'. It has not been modified in this session."
                    };
                }

                var snap = _snapshots[name];

                if (!snap.ExistedBefore)
                {
                    DeletePou(name);
                    _snapshots.Remove(name);
                    CodesysContext.LogToIde($"Rollback: Deleted newly created POU '{name}'");
                    return new RollbackResult
                    {
                        name = name,
                        success = true,
                        action = "deleted",
                        message = $"POU '{name}' was created in this session and has now been deleted."
                    };
                }

                WritePou(name, snap.OriginalDeclaration, snap.OriginalImplementation, snap.PouType);
                _snapshots.Remove(name);
                CodesysContext.LogToIde($"Rollback: Reverted POU '{name}' to baseline snapshot");
                return new RollbackResult
                {
                    name = name,
                    success = true,
                    action = snap.IsDeleted ? "restored" : "reverted",
                    message = $"POU '{name}' has been successfully restored to its baseline snapshot."
                };
            });
        }

        public static PreviewDiffResult PreviewDiff(string name = null, string viewer = "vscode", string outputDir = null)
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var diffResult = GetDiff(name);
                var res = new PreviewDiffResult
                {
                    modifiedCount = diffResult.modifiedCount,
                    viewer = viewer ?? "vscode"
                };

                if (diffResult.modifiedCount == 0)
                {
                    res.success = true;
                    res.message = "No modified POUs in the current session to preview.";
                    return res;
                }

                if (string.IsNullOrEmpty(outputDir))
                {
                    string projDir = null;
                    if (CodesysContext.PrimaryProject != null && !string.IsNullOrEmpty(CodesysContext.PrimaryProject.path))
                    {
                        projDir = Path.GetDirectoryName(CodesysContext.PrimaryProject.path);
                    }
                    if (string.IsNullOrEmpty(projDir))
                    {
                        projDir = Environment.CurrentDirectory;
                    }
                    outputDir = Path.Combine(projDir, ".diff_preview");
                }

                res.exportDir = outputDir;

                var exportedFiles = DiffViewerHelper.ExportDiffFiles(outputDir, _snapshots, pouName =>
                {
                    try
                    {
                        var pouData = ReadPou(pouName);
                        return (pouData.declaration, pouData.implementation);
                    }
                    catch
                    {
                        return (null, null);
                    }
                }, name);

                foreach (var exp in exportedFiles)
                {
                    res.exportedPous.Add(exp.PouName);
                }

                res.reportHtml = DiffViewerHelper.GenerateHtmlDiffReport(outputDir, diffResult.diffs);

                bool vscodeOk = false;
                string cmd = null;

                if (exportedFiles.Count > 0 && (viewer == "vscode" || viewer == "auto"))
                {
                    var primary = exportedFiles[0];
                    vscodeOk = DiffViewerHelper.LaunchVsCodeDiff(primary.BeforeFilePath, primary.AfterFilePath, out cmd);
                    if (vscodeOk)
                    {
                        res.launchedCommand = cmd;
                    }
                }

                if (viewer == "browser" || !vscodeOk || exportedFiles.Count > 1)
                {
                    DiffViewerHelper.LaunchBrowserReport(res.reportHtml);
                }

                res.success = true;
                res.message = vscodeOk
                    ? $"Exported {exportedFiles.Count} POU(s) to '{outputDir}'. Launched VS Code diff ({res.launchedCommand})."
                    : $"Exported {exportedFiles.Count} POU(s) to '{outputDir}'. Opened HTML diff report.";

                CodesysContext.LogToIde($"preview_diff: Exported {exportedFiles.Count} POU(s) to '{outputDir}'");
                return res;
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

        public static OnlineStatusResult PlcLogin(string mode = "try_online_change", bool deleteForeignApps = true, string appName = null)
        {
            return CodesysOnlineHelper.PlcLogin(mode, deleteForeignApps, appName);
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

        public static OnlineStatusResult UnforceVariables(List<string> expressions = null)
        {
            return CodesysOnlineHelper.UnforceVariables(expressions);
        }
    }
}
