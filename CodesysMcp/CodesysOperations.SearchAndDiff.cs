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
    public static partial class CodesysOperations
    {
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
                var liveBuffers = CodesysEditorBufferHelper.CollectLiveEditorSnapshots();

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

                    bool hasDecl = marker.has_textual_declaration;
                    bool hasImpl = marker.has_textual_implementation;
                    string declText = string.Empty;
                    string implText = string.Empty;
                    if (hasDecl)
                    {
                        var declExt = GetExtension<IScriptObjectWithTextualDeclaration>(extObj);
                        declText = declExt?.textual_declaration?.text ?? string.Empty;
                    }
                    if (hasImpl)
                    {
                        var implExt = GetExtension<IScriptObjectWithTextualImplementation>(extObj);
                        implText = implExt?.textual_implementation?.text ?? string.Empty;
                    }
                    CodesysEditorBufferHelper.TryApplyLiveSnapshot(
                        liveBuffers,
                        baseObj.guid,
                        objName,
                        hasDecl,
                        hasImpl,
                        ref declText,
                        ref implText);

                    // 1. Search declaration
                    if (hasDecl && !string.IsNullOrEmpty(declText))
                    {
                        int remaining = maxResults - res.matches.Count;
                        var declMatches = SearchInText(declText, objName, friendlyType, "declaration", regex, remaining);
                        res.matches.AddRange(declMatches);
                    }

                    if (res.matches.Count >= maxResults)
                    {
                        res.truncated = true;
                        break;
                    }

                    // 2. Search implementation
                    if (hasImpl && !string.IsNullOrEmpty(implText))
                    {
                        int remaining = maxResults - res.matches.Count;
                        var implMatches = SearchInText(implText, objName, friendlyType, "implementation", regex, remaining);
                        res.matches.AddRange(implMatches);
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

        private static string NormalizeLineEndings(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", "\n");
        }

        private static bool HasLiveBufferDifference(PouContentResult persisted, PouContentResult live)
        {
            if (persisted == null || live == null) return false;
            return !string.Equals(NormalizeLineEndings(persisted.declaration), NormalizeLineEndings(live.declaration), StringComparison.Ordinal) ||
                   !string.Equals(NormalizeLineEndings(persisted.implementation), NormalizeLineEndings(live.implementation), StringComparison.Ordinal);
        }

        private static Dictionary<string, PouSnapshot> BuildEffectiveSnapshots(string name)
        {
            var effectiveSnapshots = new Dictionary<string, PouSnapshot>(_snapshots, StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(name))
            {
                if (!effectiveSnapshots.ContainsKey(name))
                {
                    TryAddTransientLiveSnapshot(effectiveSnapshots, name, name);
                }
                return effectiveSnapshots;
            }
            var liveBuffers = CodesysEditorBufferHelper.CollectLiveEditorSnapshots();
            if (liveBuffers == null || liveBuffers.Count == 0)
            {
                return effectiveSnapshots;
            }
            var seenSnapshots = new HashSet<LiveEditorSnapshot>();
            foreach (var liveSnap in liveBuffers.Values)
            {
                if (liveSnap == null || !seenSnapshots.Add(liveSnap)) continue;
                if (string.IsNullOrWhiteSpace(liveSnap.ObjectName)) continue;
                if (effectiveSnapshots.ContainsKey(liveSnap.ObjectName)) continue;
                TryAddTransientLiveSnapshot(effectiveSnapshots, liveSnap.ObjectName);
            }
            return effectiveSnapshots;
        }

        private static void TryAddTransientLiveSnapshot(
            Dictionary<string, PouSnapshot> effectiveSnapshots,
            string candidateName,
            string snapshotKey = null)
        {
            if (string.IsNullOrWhiteSpace(candidateName)) return;
            try
            {
                PouContentResult baseline = null;
                if (_projectBaselines.TryGetValue(candidateName, out var projBase) && projBase != null)
                {
                    baseline = new PouContentResult
                    {
                        name = projBase.Name,
                        type = projBase.PouType,
                        declaration = projBase.OriginalDeclaration ?? string.Empty,
                        implementation = projBase.OriginalImplementation ?? string.Empty
                    };
                }
                else
                {
                    baseline = ReadPou(candidateName, includeLiveEditorOverlay: false);
                }
                var live = ReadPou(candidateName, includeLiveEditorOverlay: true);
                if (!HasLiveBufferDifference(baseline, live)) return;
                string key = !string.IsNullOrWhiteSpace(snapshotKey)
                    ? snapshotKey
                    : (!string.IsNullOrWhiteSpace(baseline.name) ? baseline.name : candidateName);
                if (effectiveSnapshots.ContainsKey(key)) return;
                effectiveSnapshots[key] = new PouSnapshot
                {
                    Name = key,
                    PouType = baseline.type,
                    OriginalDeclaration = baseline.declaration ?? string.Empty,
                    OriginalImplementation = baseline.implementation ?? string.Empty,
                    ExistedBefore = true,
                    IsDeleted = false,
                    CreatedAt = DateTime.UtcNow
                };
            }
            catch
            {
                // Ignore unresolvable or closed objects when probing live editor buffers
            }
        }

        public static ProjectDiffResult GetDiff(string name = null, string section = "all")
        {
            if (string.IsNullOrEmpty(section)) section = "all";

            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var result = new ProjectDiffResult();
                var effectiveSnapshots = BuildEffectiveSnapshots(name);
                var targets = new List<string>();

                if (!string.IsNullOrEmpty(name))
                {
                    if (effectiveSnapshots.ContainsKey(name))
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
                    targets.AddRange(effectiveSnapshots.Keys);
                }

                var fullSb = new StringBuilder();

                foreach (var targetName in targets)
                {
                    var snap = effectiveSnapshots[targetName];
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

                var effectiveSnapshots = BuildEffectiveSnapshots(name);
                var exportedFiles = DiffViewerHelper.ExportDiffFiles(outputDir, effectiveSnapshots, pouName =>
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
    }
}
