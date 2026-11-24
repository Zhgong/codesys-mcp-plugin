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
        private static string GetObjectRelativePath(object extOrObj)
        {
            if (extOrObj == null) return null;
            var proj = CodesysContext.PrimaryProject;
            var projObj = GetExtension<IScriptObject>(CodesysContext.PrimaryProjectExtended) ?? proj as IScriptObject;
            Guid projGuid = projObj != null ? projObj.guid : Guid.Empty;
            string rootNode = SourceMirrorHelper.ActiveRootNode;

            var parts = new List<string>();
            object current = extOrObj;
            int guard = 0;

            while (current != null && guard++ < 64)
            {
                var scriptObj = GetExtension<IScriptObject>(current) ?? current as IScriptObject;
                if (scriptObj == null) break;
                if (projGuid != Guid.Empty && scriptObj.guid == projGuid) break;

                string seg = scriptObj.get_name();
                if (!string.IsNullOrWhiteSpace(rootNode) && string.Equals(seg, rootNode, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(seg))
                {
                    parts.Add(seg);
                }

                current = scriptObj.parent;
            }

            if (parts.Count == 0) return null;
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static void TryMirrorWrittenObject(object extOrObj, PouContentResult content)
        {
            if (!SourceMirrorHelper.HasActiveMirror || extOrObj == null) return;
            try
            {
                string mirrorDir = SourceMirrorHelper.ActiveMirrorDirectory;
                if (string.IsNullOrWhiteSpace(mirrorDir)) return;

                var obj = GetExtension<IScriptObject>(extOrObj) ?? extOrObj as IScriptObject;
                string relPath = GetObjectRelativePath(extOrObj);
                if (string.IsNullOrWhiteSpace(relPath)) return;

                string name = content?.name ?? obj?.get_name() ?? Path.GetFileName(relPath);
                string type = content?.type ?? obj?.type.ToString() ?? "Unknown";
                string decl = content?.declaration;
                string impl = content?.implementation;

                if (content == null)
                {
                    var marker = GetExtension<IScriptTextualObjectMarker>(extOrObj);
                    bool hasDecl = marker != null && marker.has_textual_declaration;
                    bool hasImpl = marker != null && marker.has_textual_implementation;
                    if (!hasDecl && !hasImpl) return;

                    if (hasDecl)
                    {
                        var declExt = GetExtension<IScriptObjectWithTextualDeclaration>(extOrObj);
                        decl = declExt?.textual_declaration?.text ?? string.Empty;
                    }
                    if (hasImpl)
                    {
                        var implExt = GetExtension<IScriptObjectWithTextualImplementation>(extOrObj);
                        impl = implExt?.textual_implementation?.text ?? string.Empty;
                    }
                }

                var item = new MirrorSourceItem
                {
                    Name = name,
                    Type = type,
                    RelativePath = relPath,
                    Declaration = decl ?? string.Empty,
                    Implementation = impl ?? string.Empty
                };

                string targetFile = SourceMirrorHelper.ResolveSafeStPath(mirrorDir, relPath);
                string beforeText = File.Exists(targetFile) ? File.ReadAllText(targetFile, new System.Text.UTF8Encoding(false)) : string.Empty;
                string afterText = SourceMirrorHelper.FormatStContent(item);

                var outcome = SourceMirrorHelper.SyncSingleNode(mirrorDir, item);
                SourceMirrorHelper.RecordIdeContentHash(relPath, afterText);
                if (outcome == MirrorWriteOutcome.Created || outcome == MirrorWriteOutcome.Updated)
                {
                    string diff = SourceMirrorHelper.FormatCompactDiff(beforeText, afterText);
                    CodesysContext.LogToIde($"[Mirror → Disk] {outcome}: {relPath} {diff}");
                }
            }
            catch (Exception ex)
            {
                CodesysContext.LogToIde($"[Mirror → Disk] Write-through failed: {ex.Message}");
            }
        }

        private static void CollectMirrorItems(IScriptTreeObject treeObj, string parentPath, List<MirrorSourceItem> items, Dictionary<string, LiveEditorSnapshot> liveBuffers = null)
        {
            if (treeObj == null) return;
            var children = treeObj.get_children(false);
            if (children == null) return;

            foreach (var child in children)
            {
                var obj = child.BaseObject;
                string name = obj.get_name();
                if (string.IsNullOrWhiteSpace(name)) continue;

                string relPath = string.IsNullOrEmpty(parentPath) ? name : (parentPath + "/" + name);
                var marker = GetExtension<IScriptTextualObjectMarker>(child);
                bool hasDecl = marker != null && marker.has_textual_declaration;
                bool hasImpl = marker != null && marker.has_textual_implementation;

                if (hasDecl || hasImpl)
                {
                    string declText = string.Empty;
                    string implText = string.Empty;

                    if (hasDecl)
                    {
                        var declExt = GetExtension<IScriptObjectWithTextualDeclaration>(child);
                        declText = declExt?.textual_declaration?.text ?? string.Empty;
                    }
                    if (hasImpl)
                    {
                        var implExt = GetExtension<IScriptObjectWithTextualImplementation>(child);
                        implText = implExt?.textual_implementation?.text ?? string.Empty;
                    }

                    CodesysEditorBufferHelper.TryApplyLiveSnapshot(
                        liveBuffers,
                        obj.guid,
                        name,
                        hasDecl,
                        hasImpl,
                        ref declText,
                        ref implText);

                    items.Add(new MirrorSourceItem
                    {
                        Name = name,
                        Type = obj.type.ToString(),
                        RelativePath = relPath,
                        Declaration = declText,
                        Implementation = implText
                    });
                }

                var childTree = obj as IScriptTreeObject ?? GetExtension<IScriptTreeObject>(child);
                if (childTree != null)
                {
                    CollectMirrorItems(childTree, relPath, items, liveBuffers);
                }
            }
        }

        private static readonly object _ideSyncLock = new object();
        private static System.Threading.Timer _ideSyncTimer;
        private static int _ideSyncRunning;
        private static int _ideSyncIntervalMs = 2000;
        private static volatile bool _ideSyncEnabled;
        private static string _autoBoundProjectPath;
        private static bool _lastProjectDirty;
        private static DateTime _lastProjectFileWriteUtc = DateTime.MinValue;
        private static readonly HashSet<string> _mirrorBindFailedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void EnsureDefaultMirrorForOpenProject()
        {
            CodesysContext.RunOnPrimaryThread(() =>
            {
                EnsureDefaultMirrorForOpenProjectInternal();
            });
        }

        private static void EnsureDefaultMirrorForOpenProjectInternal()
        {
            var proj = CodesysContext.PrimaryProject;
            if (proj == null)
            {
                if (_autoBoundProjectPath != null)
                {
                    _autoBoundProjectPath = null;
                    _lastProjectDirty = false;
                    _lastProjectFileWriteUtc = DateTime.MinValue;
                    _projectBaselines.Clear();
                    SourceMirrorHelper.ClearIdeContentHashes();
                    SourceMirrorHelper.ClearMirrorDirectory();
                    CodesysIdeUiHelper.RefreshMirrorIndicator();
                }
                return;
            }

            if (SourceMirrorHelper.IsExplicitlyDisabled)
            {
                return;
            }

            string projPath = proj.path ?? string.Empty;
            if (string.IsNullOrWhiteSpace(projPath))
            {
                return;
            }

            string fullProjPath = Path.GetFullPath(projPath);
            if (string.Equals(_autoBoundProjectPath, fullProjPath, StringComparison.OrdinalIgnoreCase) &&
                SourceMirrorHelper.HasActiveMirror)
            {
                return;
            }

            string envMirror = Environment.GetEnvironmentVariable(SourceMirrorHelper.EnvMirrorDir);
            if (!string.IsNullOrWhiteSpace(envMirror) && SourceMirrorHelper.HasActiveMirror)
            {
                if (!string.Equals(_autoBoundProjectPath, fullProjPath, StringComparison.OrdinalIgnoreCase))
                {
                    _projectBaselines.Clear();
                }
                _autoBoundProjectPath = fullProjPath;
                _lastProjectDirty = proj.dirty;
                _lastProjectFileWriteUtc = File.Exists(fullProjPath) ? File.GetLastWriteTimeUtc(fullProjPath) : DateTime.MinValue;
                SourceMirrorHelper.EnsureGitInitialized(SourceMirrorHelper.ActiveMirrorDirectory);
                SyncSourcesToDiskInternal(SourceMirrorHelper.ActiveMirrorDirectory);
                CodesysIdeUiHelper.RefreshMirrorIndicator();
                return;
            }

            // If user manually bound a custom folder for this project before auto-bind ran, keep it.
            if (_autoBoundProjectPath == null && SourceMirrorHelper.HasActiveMirror)
            {
                _autoBoundProjectPath = fullProjPath;
                _lastProjectDirty = proj.dirty;
                _lastProjectFileWriteUtc = File.Exists(fullProjPath) ? File.GetLastWriteTimeUtc(fullProjPath) : DateTime.MinValue;
                CodesysIdeUiHelper.RefreshMirrorIndicator();
                return;
            }

            if (_mirrorBindFailedPaths.Contains(fullProjPath))
            {
                return;
            }

            string defaultMirrorDir = SourceMirrorHelper.GetDefaultProjectMirrorDirectory(fullProjPath);
            try
            {
                if (!string.Equals(_autoBoundProjectPath, fullProjPath, StringComparison.OrdinalIgnoreCase))
                {
                    _projectBaselines.Clear();
                }
                SourceMirrorHelper.SetMirrorDirectory(defaultMirrorDir);
                SourceMirrorHelper.SetRootNode(null);
                SourceMirrorHelper.EnsureGitInitialized(defaultMirrorDir);
                SyncSourcesToDiskInternal(defaultMirrorDir);
                CommitMirrorWithLog(defaultMirrorDir, $"chore(mirror): initial .st snapshot ({Path.GetFileName(fullProjPath)})");

                _autoBoundProjectPath = fullProjPath;
                _lastProjectDirty = proj.dirty;
                _lastProjectFileWriteUtc = File.Exists(fullProjPath) ? File.GetLastWriteTimeUtc(fullProjPath) : DateTime.MinValue;
                CodesysIdeUiHelper.RefreshMirrorIndicator();
                CodesysContext.LogToIde($"[Mirror] Auto-bound project .st mirror: {defaultMirrorDir}");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                _mirrorBindFailedPaths.Add(fullProjPath);
                SourceMirrorHelper.ClearMirrorDirectory();
                CodesysIdeUiHelper.RefreshMirrorIndicator();
                CodesysContext.LogToIde($"[Mirror] Skipped auto-mirror for read-only/protected path '{defaultMirrorDir}': {ex.Message}");
            }
        }

        private static void CommitMirrorWithLog(string mirrorDir, string message)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var gitRes = SourceMirrorHelper.CommitMirrorChanges(mirrorDir, message);
                    if (gitRes.committed)
                    {
                        CodesysContext.LogToIde($"[Mirror → Git] Committed ({gitRes.commitHash}): {gitRes.message}");
                    }
                    else if (!gitRes.gitAvailable && !string.IsNullOrWhiteSpace(gitRes.message))
                    {
                        CodesysContext.LogToIde($"[Mirror → Git] Skipped: {gitRes.message}");
                    }
                }
                catch { }
            });
        }

        public static void StartIdeToDiskPolling()
        {
            lock (_ideSyncLock)
            {
                _ideSyncEnabled = true;
                _ideSyncIntervalMs = 2000;
                if (_ideSyncTimer != null)
                {
                    try { _ideSyncTimer.Dispose(); } catch { }
                    _ideSyncTimer = null;
                }
                _ideSyncTimer = new System.Threading.Timer(_ => OnIdeSyncTimerTick(), null, _ideSyncIntervalMs, System.Threading.Timeout.Infinite);
            }
        }

        public static void StopIdeToDiskPolling()
        {
            lock (_ideSyncLock)
            {
                _ideSyncEnabled = false;
                if (_ideSyncTimer != null)
                {
                    try { _ideSyncTimer.Dispose(); } catch { }
                    _ideSyncTimer = null;
                }
            }
        }

        private static void OnIdeSyncTimerTick()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _ideSyncRunning, 1, 0) != 0)
                return;

            try
            {
                if (!_ideSyncEnabled)
                    return;

                var sw = new System.Diagnostics.Stopwatch();
                CodesysContext.RunOnPrimaryThread(() =>
                {
                    EnsureDefaultMirrorForOpenProjectInternal();
                    sw.Start();

                    var proj = CodesysContext.PrimaryProject;
                    if (proj == null || !SourceMirrorHelper.HasActiveMirror || SourceMirrorHelper.IsExplicitlyDisabled)
                        return;

                    string mirrorDir = SourceMirrorHelper.ActiveMirrorDirectory;
                    var treeObj = ResolveMirrorRootTreeObject(SourceMirrorHelper.ActiveRootNode);
                    if (treeObj == null) return;

                    var liveBuffers = CodesysEditorBufferHelper.CollectLiveEditorSnapshots();
                    var items = new List<MirrorSourceItem>();
                    CollectMirrorItems(treeObj, string.Empty, items, liveBuffers);

                    var currentPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in items)
                    {
                        if (!string.IsNullOrWhiteSpace(item.RelativePath))
                        {
                            currentPaths.Add(item.RelativePath.Replace('\\', '/'));
                        }

                        string afterText = SourceMirrorHelper.FormatStContent(item);
                        string newHash = SourceMirrorHelper.ComputeContentHash(afterText);
                        string prevHash = SourceMirrorHelper.GetIdeContentHash(item.RelativePath);
                        if (string.Equals(newHash, prevHash, StringComparison.Ordinal))
                            continue;

                        string targetFile = SourceMirrorHelper.ResolveSafeStPath(mirrorDir, item.RelativePath);
                        string beforeText = File.Exists(targetFile) ? File.ReadAllText(targetFile, new System.Text.UTF8Encoding(false)) : string.Empty;
                        var outcome = SourceMirrorHelper.SyncSingleNode(mirrorDir, item);
                        SourceMirrorHelper.RecordIdeContentHash(item.RelativePath, afterText);

                        if (outcome == MirrorWriteOutcome.Created || outcome == MirrorWriteOutcome.Updated)
                        {
                            string diff = SourceMirrorHelper.FormatCompactDiff(beforeText, afterText);
                            CodesysContext.LogToIde($"[Mirror → .st] {outcome}: {item.RelativePath} {diff}");
                        }
                    }

                    // Detect manual deletions or renames in CODESYS IDE
                    foreach (string trackedRel in SourceMirrorHelper.GetTrackedIdePaths())
                    {
                        if (!currentPaths.Contains(trackedRel))
                        {
                            if (SourceMirrorHelper.DeleteSingleNode(mirrorDir, trackedRel))
                            {
                                CodesysContext.LogToIde($"[Mirror → .st] Deleted: {trackedRel}");
                            }
                            SourceMirrorHelper.RemoveIdeContentHash(trackedRel);
                        }
                    }

                    // Detect manual IDE save (Ctrl+S / File -> Save)
                    bool currentDirty = proj.dirty;
                    string projPath = proj.path;
                    DateTime currentWriteUtc = (!string.IsNullOrWhiteSpace(projPath) && File.Exists(projPath))
                        ? File.GetLastWriteTimeUtc(projPath)
                        : DateTime.MinValue;

                    bool savedInIde = (_lastProjectDirty && !currentDirty) ||
                        (_lastProjectFileWriteUtc != DateTime.MinValue && currentWriteUtc > _lastProjectFileWriteUtc);

                    _lastProjectDirty = currentDirty;
                    if (currentWriteUtc != DateTime.MinValue)
                    {
                        _lastProjectFileWriteUtc = currentWriteUtc;
                    }

                    if (savedInIde)
                    {
                        foreach (var savedItem in items)
                        {
                            if (!string.IsNullOrWhiteSpace(savedItem.Name))
                            {
                                _projectBaselines[savedItem.Name] = new PouSnapshot
                                {
                                    Name = savedItem.Name,
                                    PouType = savedItem.Type,
                                    OriginalDeclaration = savedItem.Declaration ?? string.Empty,
                                    OriginalImplementation = savedItem.Implementation ?? string.Empty,
                                    ExistedBefore = true,
                                    IsDeleted = false,
                                    CreatedAt = DateTime.UtcNow
                                };
                            }
                        }
                        string projName = !string.IsNullOrWhiteSpace(projPath) ? Path.GetFileName(projPath) : "project";
                        CommitMirrorWithLog(mirrorDir, $"chore(save): sync .st on IDE save ({projName})");
                    }
                });
                sw.Stop();

                lock (_ideSyncLock)
                {
                    _ideSyncIntervalMs = Math.Max(2000, Math.Min(10000, (int)(sw.ElapsedMilliseconds * 8)));
                }
            }
            catch { }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _ideSyncRunning, 0);
                lock (_ideSyncLock)
                {
                    if (_ideSyncTimer != null && _ideSyncEnabled)
                    {
                        try { _ideSyncTimer.Change(_ideSyncIntervalMs, System.Threading.Timeout.Infinite); } catch { }
                    }
                }
            }
        }

        private static IScriptTreeObject ResolveMirrorRootTreeObject(string rootNode)
        {
            var proj = CodesysContext.PrimaryProject;
            if (proj == null) return null;

            string effectiveRoot = !string.IsNullOrWhiteSpace(rootNode) ? rootNode.Trim() : SourceMirrorHelper.ActiveRootNode;
            if (!string.IsNullOrWhiteSpace(effectiveRoot))
            {
                var rootExt = FindObjectByName(effectiveRoot);
                if (rootExt == null) throw new InvalidOperationException($"Root node '{effectiveRoot}' not found in project.");
                return rootExt as IScriptTreeObject ?? GetExtension<IScriptTreeObject>(rootExt);
            }

            return proj as IScriptTreeObject ?? GetExtension<IScriptTreeObject>(CodesysContext.PrimaryProjectExtended);
        }

        private static MirrorSyncResult SyncSourcesToDiskInternal(string targetDir, string rootNode = null, bool overwriteBaselines = false)
        {
            var treeObj = ResolveMirrorRootTreeObject(rootNode);
            if (treeObj == null) throw new InvalidOperationException("No active project open or could not resolve project tree.");

            var items = new List<MirrorSourceItem>();
            CollectMirrorItems(treeObj, string.Empty, items);
            foreach (var item in items)
            {
                SourceMirrorHelper.RecordIdeContentHash(item.RelativePath, SourceMirrorHelper.FormatStContent(item));
                if (!string.IsNullOrWhiteSpace(item.Name) && (overwriteBaselines || !_projectBaselines.ContainsKey(item.Name)))
                {
                    _projectBaselines[item.Name] = new PouSnapshot
                    {
                        Name = item.Name,
                        PouType = item.Type,
                        OriginalDeclaration = item.Declaration ?? string.Empty,
                        OriginalImplementation = item.Implementation ?? string.Empty,
                        ExistedBefore = true,
                        IsDeleted = false,
                        CreatedAt = DateTime.UtcNow
                    };
                }
            }
            var res = SourceMirrorHelper.SyncAll(targetDir, items);
            CodesysContext.LogToIde($"[Mirror → Disk] Sync completed: created={res.created}, updated={res.updated}, deleted={res.deleted}, unchanged={res.unchanged}");
            return res;
        }

        public static object SyncSourcesToDisk(
            string mirrorDir = null,
            bool persistForSession = true,
            bool disable = false,
            string rootNode = null,
            bool watch = true)
        {
            if (disable)
            {
                SourceMirrorHelper.SetExplicitlyDisabled(true);
                StopIdeToDiskPolling();
                SourceMirrorHelper.ClearIdeContentHashes();
                SourceMirrorHelper.ClearMirrorDirectory();
                CodesysIdeUiHelper.RefreshMirrorIndicator();
                return new
                {
                    disabled = true,
                    mirrorDir = (string)null
                };
            }

            SourceMirrorHelper.SetExplicitlyDisabled(false);

            string targetDir = !string.IsNullOrWhiteSpace(mirrorDir)
                ? mirrorDir.Trim()
                : SourceMirrorHelper.ActiveMirrorDirectory;

            if (string.IsNullOrWhiteSpace(targetDir))
            {
                var proj = CodesysContext.RunOnPrimaryThread(() => CodesysContext.PrimaryProject);
                if (proj != null && !string.IsNullOrWhiteSpace(proj.path))
                {
                    targetDir = SourceMirrorHelper.GetDefaultProjectMirrorDirectory(proj.path);
                }
            }

            if (string.IsNullOrWhiteSpace(targetDir))
            {
                throw new ArgumentException("Parameter 'mirror_dir' is required (or open a saved project to use .cds_mcp/src).");
            }

            string fullTargetDir = Path.GetFullPath(targetDir);
            if (persistForSession)
            {
                SourceMirrorHelper.SetMirrorDirectory(fullTargetDir);
                SourceMirrorHelper.SetRootNode(rootNode);
                SourceMirrorHelper.EnsureGitInitialized(fullTargetDir);
            }

            var res = CodesysContext.RunOnPrimaryThread(() =>
                SyncSourcesToDiskInternal(fullTargetDir, rootNode));

            if (persistForSession && watch)
            {
                // One-way mode (CODESYS -> .st): do not start disk->CODESYS FileSystemWatcher
                SourceMirrorHelper.StopWatching();
                StartIdeToDiskPolling();
            }
            else if (!watch)
            {
                StopIdeToDiskPolling();
                SourceMirrorHelper.StopWatching();
            }

            CodesysIdeUiHelper.RefreshMirrorIndicator();
            return res;
        }

        private static object FindObjectByMirrorItem(MirrorSourceItem item)
        {
            if (item == null) return null;

            if (!string.IsNullOrWhiteSpace(item.RelativePath))
            {
                var proj = CodesysContext.PrimaryProject;
                IScriptTreeObject currentTree = null;
                string rootNode = SourceMirrorHelper.ActiveRootNode;

                if (!string.IsNullOrWhiteSpace(rootNode))
                {
                    var rootExt = FindObjectByName(rootNode);
                    currentTree = rootExt as IScriptTreeObject ?? GetExtension<IScriptTreeObject>(rootExt);
                }
                else if (proj != null)
                {
                    currentTree = proj as IScriptTreeObject ?? GetExtension<IScriptTreeObject>(CodesysContext.PrimaryProjectExtended);
                }

                if (currentTree != null)
                {
                    string[] parts = item.RelativePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                    object matchObj = null;
                    for (int i = 0; i < parts.Length; i++)
                    {
                        var found = currentTree.find(parts[i], false);
                        if (found == null || found.Count == 0)
                        {
                            matchObj = null;
                            break;
                        }
                        matchObj = found[0];
                        if (i < parts.Length - 1)
                        {
                            var baseObj = (matchObj as IExtendedObject<IScriptObject>)?.BaseObject ?? matchObj;
                            currentTree = baseObj as IScriptTreeObject ?? GetExtension<IScriptTreeObject>(matchObj);
                            if (currentTree == null)
                            {
                                matchObj = null;
                                break;
                            }
                        }
                    }

                    if (matchObj != null) return matchObj;
                }

                // Fallback: Parent.Child dot-notation if path has 2+ segments
                string[] segs = item.RelativePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (segs.Length >= 2)
                {
                    var dotMatch = FindObjectByName(segs[segs.Length - 2] + "." + segs[segs.Length - 1]);
                    if (dotMatch != null) return dotMatch;
                }
            }

            if (!string.IsNullOrWhiteSpace(item.Name))
            {
                return FindObjectByName(item.Name);
            }
            return null;
        }

        private static string ImportMirrorItemInternal(MirrorSourceItem item, out string diffSummary)
        {
            diffSummary = null;
            var extObj = FindObjectByMirrorItem(item);
            if (extObj == null) return "skipped";

            var marker = GetExtension<IScriptTextualObjectMarker>(extObj);
            bool hasDecl = marker != null && marker.has_textual_declaration;
            bool hasImpl = marker != null && marker.has_textual_implementation;
            if (!hasDecl && !hasImpl) return "skipped";

            string effectiveDecl = item.Declaration ?? string.Empty;
            string effectiveImpl = item.Implementation ?? string.Empty;
            if (!hasDecl && hasImpl && string.IsNullOrEmpty(effectiveImpl) && !string.IsNullOrEmpty(effectiveDecl))
            {
                effectiveImpl = effectiveDecl;
            }
            else if (hasDecl && !hasImpl && string.IsNullOrEmpty(effectiveDecl) && !string.IsNullOrEmpty(effectiveImpl))
            {
                effectiveDecl = effectiveImpl;
            }

            string beforeCombined = string.Empty;
            string afterCombined = string.Empty;
            bool changed = false;

            if (hasDecl)
            {
                var declExt = GetExtension<IScriptObjectWithTextualDeclaration>(extObj);
                var doc = declExt?.textual_declaration;
                string liveDecl = (doc?.text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
                string diskDecl = effectiveDecl.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
                beforeCombined += liveDecl;
                afterCombined += diskDecl;
                if (doc != null && !string.Equals(liveDecl, diskDecl, StringComparison.Ordinal))
                {
                    doc.replace(effectiveDecl);
                    changed = true;
                }
            }

            if (hasImpl)
            {
                var implExt = GetExtension<IScriptObjectWithTextualImplementation>(extObj);
                var doc = implExt?.textual_implementation;
                string liveImpl = (doc?.text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
                string diskImpl = effectiveImpl.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
                if (beforeCombined.Length > 0 && liveImpl.Length > 0) beforeCombined += "\n";
                if (afterCombined.Length > 0 && diskImpl.Length > 0) afterCombined += "\n";
                beforeCombined += liveImpl;
                afterCombined += diskImpl;
                if (doc != null && !string.Equals(liveImpl, diskImpl, StringComparison.Ordinal))
                {
                    doc.replace(effectiveImpl);
                    changed = true;
                }
            }

            if (changed)
            {
                var obj = GetExtension<IScriptObject>(extObj) ?? extObj as IScriptObject;
                string rel = !string.IsNullOrWhiteSpace(item.RelativePath) ? item.RelativePath : GetObjectRelativePath(extObj);
                var updatedSnapshot = new MirrorSourceItem
                {
                    Name = item.Name ?? obj?.get_name() ?? string.Empty,
                    Type = !string.IsNullOrWhiteSpace(item.Type) ? item.Type : (obj?.type.ToString() ?? string.Empty),
                    RelativePath = rel ?? string.Empty,
                    Declaration = hasDecl ? effectiveDecl : string.Empty,
                    Implementation = hasImpl ? effectiveImpl : string.Empty
                };
                SourceMirrorHelper.RecordIdeContentHash(rel, SourceMirrorHelper.FormatStContent(updatedSnapshot));
                diffSummary = SourceMirrorHelper.FormatCompactDiff(beforeCombined, afterCombined);
                return "updated";
            }

            return "unchanged";
        }

        private static void OnExternalStFileChanged(string fullStPath)
        {
            try
            {
                if (!File.Exists(fullStPath)) return;
                string mirrorDir = SourceMirrorHelper.ActiveMirrorDirectory;
                string raw = File.ReadAllText(fullStPath, new System.Text.UTF8Encoding(false));

                string fallbackRel = null;
                if (!string.IsNullOrWhiteSpace(mirrorDir))
                {
                    string prefix = Path.GetFullPath(mirrorDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    string full = Path.GetFullPath(fullStPath);
                    if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        fallbackRel = full.Substring(prefix.Length).Replace('\\', '/');
                        if (fallbackRel.EndsWith(".st", StringComparison.OrdinalIgnoreCase))
                        {
                            fallbackRel = fallbackRel.Substring(0, fallbackRel.Length - 3);
                        }
                    }
                }

                var item = SourceMirrorHelper.ParseStContent(raw, fallbackRel);
                string diffSummary = null;
                string outcome = CodesysContext.RunOnPrimaryThread(() =>
                {
                    if (CodesysContext.PrimaryProject == null) return "skipped";
                    return ImportMirrorItemInternal(item, out diffSummary);
                });

                SourceMirrorHelper.RecordDiskWriteFromText(fullStPath, raw);
                if (outcome == "updated")
                {
                    CodesysContext.LogToIde($"[Mirror ← Disk] {item.RelativePath} {diffSummary}");
                }
            }
            catch (Exception ex)
            {
                CodesysContext.LogToIde($"[Mirror ← Disk] Auto-sync failed for '{fullStPath}': {ex.Message}");
            }
        }

        public static object SyncDiskToProject(string mirrorDir = null, List<string> files = null)
        {
            string targetDir = !string.IsNullOrWhiteSpace(mirrorDir)
                ? mirrorDir.Trim()
                : SourceMirrorHelper.ActiveMirrorDirectory;

            if (string.IsNullOrWhiteSpace(targetDir))
            {
                throw new ArgumentException("Parameter 'mirror_dir' is required (or bind a session mirror first).");
            }

            string fullDir = Path.GetFullPath(targetDir);
            var diskItems = SourceMirrorHelper.LoadMirrorItemsFromDisk(fullDir);

            HashSet<string> filter = null;
            if (files != null && files.Count > 0)
            {
                filter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string f in files)
                {
                    if (string.IsNullOrWhiteSpace(f)) continue;
                    string norm = f.Replace('\\', '/').Trim();
                    if (norm.EndsWith(".st", StringComparison.OrdinalIgnoreCase))
                        norm = norm.Substring(0, norm.Length - 3);
                    filter.Add(norm);
                }
            }

            return CodesysContext.RunOnPrimaryThread(() =>
            {
                if (CodesysContext.PrimaryProject == null)
                    throw new InvalidOperationException("No active project open.");

                int updated = 0;
                int unchanged = 0;
                int skipped = 0;
                var updatedFiles = new List<string>();
                var errors = new List<string>();

                foreach (var item in diskItems)
                {
                    if (filter != null &&
                        !filter.Contains(item.RelativePath) &&
                        !filter.Contains(item.Name))
                    {
                        continue;
                    }

                    try
                    {
                        string status = ImportMirrorItemInternal(item, out string diffSummary);
                        if (status == "updated")
                        {
                            updated++;
                            updatedFiles.Add(item.RelativePath);
                            CodesysContext.LogToIde($"[Mirror ← Disk] {item.RelativePath} {diffSummary}");
                        }
                        else if (status == "unchanged")
                        {
                            unchanged++;
                        }
                        else
                        {
                            skipped++;
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{item.RelativePath}: {ex.Message}");
                    }
                }

                return new
                {
                    mirrorDir = fullDir,
                    updated,
                    unchanged,
                    skipped,
                    updatedFiles,
                    errors
                };
            });
        }
    }
}
