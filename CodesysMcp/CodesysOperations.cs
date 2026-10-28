using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

            return null;
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

        public static IExtendedObject<IScriptObject> FindObjectByName(string name)
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

        public static IExtendedObject<IScriptObject> GetActiveApplicationObject(IScriptProject proj)
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

                var obj = extObj.BaseObject;
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
                var obj = extObj.BaseObject;
                obj.remove();
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
    }
}
