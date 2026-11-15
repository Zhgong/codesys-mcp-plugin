using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace CodesysMcp
{
    public class MirrorGitCommitResult
    {
        public bool gitAvailable { get; set; }
        public bool initialized { get; set; }
        public bool committed { get; set; }
        public string commitHash { get; set; }
        public string message { get; set; }
    }
    public enum MirrorWriteOutcome
    {
        Created,
        Updated,
        Unchanged,
        Deleted
    }

    public class MirrorSourceItem
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public string RelativePath { get; set; }
        public string Declaration { get; set; }
        public string Implementation { get; set; }
    }

    public class MirrorSyncResult
    {
        public string mirrorDir { get; set; }
        public int created { get; set; }
        public int updated { get; set; }
        public int deleted { get; set; }
        public int unchanged { get; set; }
        public List<string> changedFiles { get; set; } = new List<string>();
    }

    /// <summary>
    /// Pure C# incremental .st source mirror engine with Content-Diff Guard,
    /// IEC-comment YAML metadata headers, and runtime session binding.
    /// </summary>
    public static class SourceMirrorHelper
    {
        public const string EnvMirrorDir = "CODESYS_MCP_MIRROR_DIR";
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly object _configLock = new object();
        private static bool _sessionOverrideSet;
        private static bool _explicitlyDisabled;
        private static string _sessionMirrorDir;
        private static string _lastBoundMirrorDir;
        private static string _sessionRootNode;
        private static string _lastCommitHash;
        private static DateTime _lastCommitTimeUtc = DateTime.MinValue;

        public static string LastCommitHash
        {
            get { lock (_configLock) { return _lastCommitHash; } }
        }

        public static DateTime LastCommitTimeUtc
        {
            get { lock (_configLock) { return _lastCommitTimeUtc; } }
        }

        private static readonly object _hashLock = new object();
        private static readonly Dictionary<string, string> _recentDiskWriteHashes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _lastKnownIdeHashes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly object _watcherLock = new object();
        private static FileSystemWatcher _watcher;
        private static string _watchedRoot;
        private static Action<string> _watchCallback;
        private static int _watchDebounceMs = 400;
        private static int _watchPollIntervalMs = 2000;
        private static Timer _pollTimer;
        private static int _pollRunning;
        private static readonly Dictionary<string, string> _watchFileSnapshot =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Timer> _debounceTimers =
            new Dictionary<string, Timer>(StringComparer.OrdinalIgnoreCase);

        public static string ActiveMirrorDirectory
        {
            get
            {
                lock (_configLock)
                {
                    if (_sessionOverrideSet)
                    {
                        return _sessionMirrorDir;
                    }
                }

                string env = Environment.GetEnvironmentVariable(EnvMirrorDir);
                if (string.IsNullOrWhiteSpace(env)) return null;
                return Path.GetFullPath(env.Trim());
            }
        }

        public static bool HasActiveMirror => !string.IsNullOrWhiteSpace(ActiveMirrorDirectory);

        public static string LastBoundMirrorDirectory
        {
            get
            {
                lock (_configLock)
                {
                    return _lastBoundMirrorDir;
                }
            }
        }

        public static bool IsWatching
        {
            get
            {
                lock (_watcherLock)
                {
                    return _watcher != null;
                }
            }
        }

        public static bool IsExplicitlyDisabled
        {
            get
            {
                lock (_configLock)
                {
                    return _explicitlyDisabled;
                }
            }
        }

        public static void SetExplicitlyDisabled(bool disabled)
        {
            lock (_configLock)
            {
                _explicitlyDisabled = disabled;
            }
        }

        public static string GetDefaultProjectMirrorDirectory(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                throw new ArgumentException("Project path cannot be empty.", nameof(projectPath));
            }

            string fullProj = Path.GetFullPath(projectPath.Trim());
            string projDir = Path.GetDirectoryName(fullProj);
            if (string.IsNullOrWhiteSpace(projDir))
            {
                throw new ArgumentException("Could not determine directory for project path.", nameof(projectPath));
            }

            return Path.Combine(projDir, ".cds_mcp", "src");
        }

        public static void SetMirrorDirectory(string mirrorDir)
        {
            if (string.IsNullOrWhiteSpace(mirrorDir))
            {
                throw new ArgumentException("Mirror directory cannot be empty.", nameof(mirrorDir));
            }

            string full = Path.GetFullPath(mirrorDir.Trim());
            lock (_configLock)
            {
                _sessionOverrideSet = true;
                _explicitlyDisabled = false;
                _sessionMirrorDir = full;
                _lastBoundMirrorDir = full;
            }
        }

        public static string ActiveRootNode
        {
            get
            {
                lock (_configLock)
                {
                    return _sessionRootNode;
                }
            }
        }

        public static void SetRootNode(string rootNode)
        {
            lock (_configLock)
            {
                _sessionRootNode = string.IsNullOrWhiteSpace(rootNode) ? null : rootNode.Trim();
            }
        }

        public static void ClearMirrorDirectory()
        {
            lock (_configLock)
            {
                _sessionOverrideSet = true;
                _sessionMirrorDir = null;
                _sessionRootNode = null;
            }
            StopWatching();
        }

        public static void ResetToEnvironmentDefault()
        {
            lock (_configLock)
            {
                _sessionOverrideSet = false;
                _explicitlyDisabled = false;
                _sessionMirrorDir = null;
                _lastBoundMirrorDir = null;
                _sessionRootNode = null;
            }
            ClearIdeContentHashes();
            StopWatching();
        }

        public static string FormatStContent(MirrorSourceItem item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));

            string safeName = SanitizeYamlScalar(item.Name);
            string safeType = SanitizeYamlScalar(item.Type);
            string safePath = SanitizeYamlScalar((item.RelativePath ?? item.Name ?? string.Empty).Replace('\\', '/'));

            var sb = new StringBuilder();
            sb.Append("(*\n---\n");
            sb.Append("name: ").Append(safeName).Append('\n');
            sb.Append("type: ").Append(safeType).Append('\n');
            sb.Append("path: ").Append(safePath).Append('\n');
            sb.Append("---\n*)\n");

            string decl = NormalizeLf(item.Declaration).TrimEnd('\n');
            string impl = NormalizeLf(item.Implementation).TrimEnd('\n');

            if (!string.IsNullOrEmpty(decl))
            {
                sb.Append(decl);
            }

            if (!string.IsNullOrEmpty(impl))
            {
                if (!string.IsNullOrEmpty(decl))
                {
                    sb.Append("\n\n// --- Implementation ---\n");
                }
                sb.Append(impl);
            }

            sb.Append('\n');
            return sb.ToString();
        }

        public static string ResolveSafeStPath(string mirrorRoot, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(mirrorRoot))
                throw new ArgumentException("Mirror root cannot be empty.", nameof(mirrorRoot));
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentException("Relative path cannot be empty.", nameof(relativePath));

            string fullRoot = Path.GetFullPath(mirrorRoot.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullRootWithSep = fullRoot + Path.DirectorySeparatorChar;

            string[] rawSegments = relativePath
                .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

            if (rawSegments.Length == 0)
                throw new ArgumentException("Relative path contains no valid segments.", nameof(relativePath));

            var cleanSegments = new string[rawSegments.Length];
            char[] invalidChars = Path.GetInvalidFileNameChars();

            for (int i = 0; i < rawSegments.Length; i++)
            {
                string seg = rawSegments[i].Trim();
                if (seg == "." || seg == ".." || string.IsNullOrEmpty(seg))
                {
                    throw new ArgumentException("Relative path contains forbidden traversal segment: " + seg, nameof(relativePath));
                }

                var segBuilder = new StringBuilder(seg.Length);
                foreach (char ch in seg)
                {
                    segBuilder.Append(Array.IndexOf(invalidChars, ch) >= 0 ? '_' : ch);
                }
                cleanSegments[i] = segBuilder.ToString();
            }

            int lastIdx = cleanSegments.Length - 1;
            if (!cleanSegments[lastIdx].EndsWith(".st", StringComparison.OrdinalIgnoreCase))
            {
                cleanSegments[lastIdx] += ".st";
            }

            string combined = fullRoot;
            for (int i = 0; i < cleanSegments.Length; i++)
            {
                combined = Path.Combine(combined, cleanSegments[i]);
            }

            string fullTarget = Path.GetFullPath(combined);
            if (!fullTarget.StartsWith(fullRootWithSep, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Resolved path escapes mirror root: " + relativePath, nameof(relativePath));
            }

            return fullTarget;
        }

        public static MirrorWriteOutcome SyncSingleNode(string mirrorRoot, MirrorSourceItem item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            string rel = string.IsNullOrWhiteSpace(item.RelativePath) ? item.Name : item.RelativePath;
            string targetPath = ResolveSafeStPath(mirrorRoot, rel);
            string content = FormatStContent(item);
            return WriteFileIfChanged(targetPath, content);
        }

        public static bool DeleteSingleNode(string mirrorRoot, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(mirrorRoot) || string.IsNullOrWhiteSpace(relativePath))
                return false;

            string fullRoot = Path.GetFullPath(mirrorRoot.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string targetPath = ResolveSafeStPath(fullRoot, relativePath);
            if (!File.Exists(targetPath))
                return false;

            File.Delete(targetPath);
            PruneEmptyParents(Path.GetDirectoryName(targetPath), fullRoot);
            return true;
        }

        public static MirrorSyncResult SyncAll(string mirrorRoot, IEnumerable<MirrorSourceItem> items)
        {
            if (string.IsNullOrWhiteSpace(mirrorRoot))
                throw new ArgumentException("Mirror root cannot be empty.", nameof(mirrorRoot));

            string fullRoot = Path.GetFullPath(mirrorRoot.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Directory.CreateDirectory(fullRoot);

            var result = new MirrorSyncResult
            {
                mirrorDir = fullRoot
            };

            var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (items != null)
            {
                foreach (var item in items)
                {
                    if (item == null) continue;
                    string rel = string.IsNullOrWhiteSpace(item.RelativePath) ? item.Name : item.RelativePath;
                    if (string.IsNullOrWhiteSpace(rel)) continue;

                    string targetPath = ResolveSafeStPath(fullRoot, rel);
                    expectedFiles.Add(targetPath);

                    string content = FormatStContent(item);
                    var outcome = WriteFileIfChanged(targetPath, content);
                    switch (outcome)
                    {
                        case MirrorWriteOutcome.Created:
                            result.created++;
                            result.changedFiles.Add(GetDisplayRelativePath(fullRoot, targetPath));
                            break;
                        case MirrorWriteOutcome.Updated:
                            result.updated++;
                            result.changedFiles.Add(GetDisplayRelativePath(fullRoot, targetPath));
                            break;
                        case MirrorWriteOutcome.Unchanged:
                            result.unchanged++;
                            break;
                    }
                }
            }

            PruneOrphanStFiles(fullRoot, fullRoot, expectedFiles, result);
            return result;
        }

        private static MirrorWriteOutcome WriteFileIfChanged(string targetPath, string content)
        {
            string fullTarget = Path.GetFullPath(targetPath);
            byte[] newBytes = Utf8NoBom.GetBytes(content);
            string newHash = ComputeSha256Base64(newBytes);

            if (File.Exists(fullTarget))
            {
                byte[] existingBytes = File.ReadAllBytes(fullTarget);
                if (string.Equals(ComputeSha256Base64(existingBytes), newHash, StringComparison.Ordinal))
                {
                    RecordDiskWriteHash(fullTarget, newHash);
                    return MirrorWriteOutcome.Unchanged;
                }

                RecordDiskWriteHash(fullTarget, newHash);
                File.WriteAllBytes(fullTarget, newBytes);
                return MirrorWriteOutcome.Updated;
            }

            string dir = Path.GetDirectoryName(fullTarget);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            RecordDiskWriteHash(fullTarget, newHash);
            File.WriteAllBytes(fullTarget, newBytes);
            return MirrorWriteOutcome.Created;
        }

        public static void RecordDiskWriteHash(string fullPath, string sha256Base64)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(sha256Base64)) return;
            lock (_hashLock)
            {
                _recentDiskWriteHashes[Path.GetFullPath(fullPath)] = sha256Base64;
            }
        }

        public static void RecordDiskWriteFromText(string fullPath, string content)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return;
            byte[] bytes = Utf8NoBom.GetBytes(content ?? string.Empty);
            RecordDiskWriteHash(fullPath, ComputeSha256Base64(bytes));
        }

        public static bool ShouldIgnoreDiskChangeEvent(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return true;
            string normalized = Path.GetFullPath(fullPath);
            if (!File.Exists(normalized)) return true;

            string expectedHash;
            lock (_hashLock)
            {
                if (!_recentDiskWriteHashes.TryGetValue(normalized, out expectedHash))
                {
                    return false;
                }
            }

            byte[] currentBytes = ReadAllBytesWithRetry(normalized);
            if (currentBytes == null) return true;
            string currentHash = ComputeSha256Base64(currentBytes);
            return string.Equals(expectedHash, currentHash, StringComparison.Ordinal);
        }

        public static MirrorSourceItem ParseStContent(string rawText, string fallbackRelativePath = null)
        {
            string normalized = NormalizeLf(rawText ?? string.Empty);
            string name = null;
            string type = null;
            string path = fallbackRelativePath;
            string body = normalized;

            if (normalized.StartsWith("(*\n---\n", StringComparison.Ordinal))
            {
                int endIdx = normalized.IndexOf("\n---\n*)\n", StringComparison.Ordinal);
                if (endIdx < 0)
                {
                    int altEnd = normalized.IndexOf("\n---\n*)", StringComparison.Ordinal);
                    if (altEnd >= 0)
                    {
                        endIdx = altEnd;
                        body = normalized.Substring(altEnd + "\n---\n*)".Length).TrimStart('\n');
                    }
                }
                else
                {
                    body = normalized.Substring(endIdx + "\n---\n*)\n".Length);
                }

                if (endIdx >= 0)
                {
                    string headerBlock = normalized.Substring("(*\n---\n".Length, endIdx - "(*\n---\n".Length);
                    foreach (string line in headerBlock.Split('\n'))
                    {
                        int colon = line.IndexOf(':');
                        if (colon <= 0) continue;
                        string key = line.Substring(0, colon).Trim().ToLowerInvariant();
                        string val = line.Substring(colon + 1).Trim();
                        if (key == "name") name = val;
                        else if (key == "type") type = val;
                        else if (key == "path" && !string.IsNullOrWhiteSpace(val)) path = val;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(path))
            {
                string cleanPath = path.Replace('\\', '/');
                int slash = cleanPath.LastIndexOf('/');
                name = slash >= 0 ? cleanPath.Substring(slash + 1) : cleanPath;
                if (name.EndsWith(".st", StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(0, name.Length - 3);
                }
            }

            string decl = string.Empty;
            string impl = string.Empty;

            string[] separators = new[]
            {
                "\n\n// --- Implementation ---\n",
                "\n// --- Implementation ---\n",
                "\n\n// === IMPLEMENTATION ===\n",
                "\n// === IMPLEMENTATION ===\n"
            };

            int sepIdx = -1;
            int sepLen = 0;
            foreach (string sep in separators)
            {
                int idx = body.IndexOf(sep, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    sepIdx = idx;
                    sepLen = sep.Length;
                    break;
                }
            }

            if (sepIdx >= 0)
            {
                decl = body.Substring(0, sepIdx).TrimEnd('\n');
                impl = body.Substring(sepIdx + sepLen).TrimEnd('\n');
            }
            else if (body.StartsWith("// --- Implementation ---\n", StringComparison.Ordinal))
            {
                impl = body.Substring("// --- Implementation ---\n".Length).TrimEnd('\n');
            }
            else if (body.StartsWith("// === IMPLEMENTATION ===\n", StringComparison.Ordinal))
            {
                impl = body.Substring("// === IMPLEMENTATION ===\n".Length).TrimEnd('\n');
            }
            else
            {
                string trimmed = body.TrimEnd('\n');
                bool isActionOrTransition =
                    (!string.IsNullOrWhiteSpace(type) &&
                     (type.IndexOf("Action", StringComparison.OrdinalIgnoreCase) >= 0 ||
                      type.IndexOf("Transition", StringComparison.OrdinalIgnoreCase) >= 0 ||
                      string.Equals(type.Trim(), "8ac092e5-3128-4e26-9e7e-11016c6684f2", StringComparison.OrdinalIgnoreCase))) ||
                    (!LooksLikeDeclarationBlock(trimmed) && !string.IsNullOrEmpty(trimmed));

                if (isActionOrTransition)
                {
                    impl = trimmed;
                }
                else
                {
                    decl = trimmed;
                }
            }

            return new MirrorSourceItem
            {
                Name = name ?? string.Empty,
                Type = type ?? string.Empty,
                RelativePath = path ?? string.Empty,
                Declaration = decl,
                Implementation = impl
            };
        }

        public static List<MirrorSourceItem> LoadMirrorItemsFromDisk(string mirrorRoot)
        {
            if (string.IsNullOrWhiteSpace(mirrorRoot))
                throw new ArgumentException("Mirror root cannot be empty.", nameof(mirrorRoot));

            string fullRoot = Path.GetFullPath(mirrorRoot.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var result = new List<MirrorSourceItem>();
            if (!Directory.Exists(fullRoot)) return result;

            CollectDiskStFiles(fullRoot, fullRoot, result);
            return result;
        }

        private static void CollectDiskStFiles(string currentDir, string fullRoot, List<MirrorSourceItem> result)
        {
            foreach (string subDir in Directory.GetDirectories(currentDir))
            {
                if (string.Equals(Path.GetFileName(subDir), ".git", StringComparison.OrdinalIgnoreCase))
                    continue;
                CollectDiskStFiles(subDir, fullRoot, result);
            }

            foreach (string file in Directory.GetFiles(currentDir, "*.st"))
            {
                string relWithExt = GetDisplayRelativePath(fullRoot, Path.GetFullPath(file));
                string relNoExt = relWithExt.EndsWith(".st", StringComparison.OrdinalIgnoreCase)
                    ? relWithExt.Substring(0, relWithExt.Length - 3)
                    : relWithExt;

                string raw = File.ReadAllText(file, Utf8NoBom);
                result.Add(ParseStContent(raw, relNoExt));
            }
        }

        public static void StartWatching(string mirrorRoot, Action<string> onExternalStChanged, int debounceMs = 400, int pollIntervalMs = 2000)
        {
            if (string.IsNullOrWhiteSpace(mirrorRoot))
                throw new ArgumentException("Mirror root cannot be empty.", nameof(mirrorRoot));
            if (onExternalStChanged == null)
                throw new ArgumentNullException(nameof(onExternalStChanged));

            string fullRoot = Path.GetFullPath(mirrorRoot.Trim());
            Directory.CreateDirectory(fullRoot);

            StopWatching();

            var initialSnapshot = CaptureFileStatSnapshot(fullRoot);

            lock (_watcherLock)
            {
                _watchedRoot = fullRoot;
                _watchCallback = onExternalStChanged;
                _watchDebounceMs = Math.Max(50, debounceMs);
                _watchPollIntervalMs = Math.Max(100, pollIntervalMs);

                _watchFileSnapshot.Clear();
                foreach (var kv in initialSnapshot)
                {
                    _watchFileSnapshot[kv.Key] = kv.Value;
                }

                var watcher = new FileSystemWatcher(fullRoot, "*.st")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime
                };

                watcher.Changed += (s, e) => ScheduleWatchEvent(e.FullPath);
                watcher.Created += (s, e) => ScheduleWatchEvent(e.FullPath);
                watcher.Renamed += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.FullPath) &&
                        e.FullPath.EndsWith(".st", StringComparison.OrdinalIgnoreCase))
                    {
                        ScheduleWatchEvent(e.FullPath);
                    }
                };

                watcher.EnableRaisingEvents = true;
                _watcher = watcher;

                _pollTimer = new Timer(_ => OnPollTimerTick(), null, _watchPollIntervalMs, Timeout.Infinite);
            }
        }

        public static void StopWatching()
        {
            lock (_watcherLock)
            {
                if (_pollTimer != null)
                {
                    try { _pollTimer.Dispose(); } catch { }
                    _pollTimer = null;
                }

                if (_watcher != null)
                {
                    try { _watcher.EnableRaisingEvents = false; } catch { }
                    try { _watcher.Dispose(); } catch { }
                    _watcher = null;
                }

                foreach (var timer in _debounceTimers.Values)
                {
                    try { timer.Dispose(); } catch { }
                }
                _debounceTimers.Clear();
                _watchFileSnapshot.Clear();
                _watchedRoot = null;
                _watchCallback = null;
            }
        }

        private static void OnPollTimerTick()
        {
            PollWatchedDirectoryOnce();

            lock (_watcherLock)
            {
                if (_pollTimer != null && _watchedRoot != null)
                {
                    try { _pollTimer.Change(_watchPollIntervalMs, Timeout.Infinite); } catch { }
                }
            }
        }

        public static void PollWatchedDirectoryOnce()
        {
            if (Interlocked.CompareExchange(ref _pollRunning, 1, 0) != 0)
                return;

            try
            {
                string root;
                lock (_watcherLock)
                {
                    root = _watchedRoot;
                    if (string.IsNullOrEmpty(root) || _watchCallback == null) return;
                }

                var current = CaptureFileStatSnapshot(root);
                var changed = new List<string>();

                lock (_watcherLock)
                {
                    if (!string.Equals(_watchedRoot, root, StringComparison.OrdinalIgnoreCase)) return;

                    foreach (var kv in current)
                    {
                        if (!_watchFileSnapshot.TryGetValue(kv.Key, out string prevSig) ||
                            !string.Equals(prevSig, kv.Value, StringComparison.Ordinal))
                        {
                            _watchFileSnapshot[kv.Key] = kv.Value;
                            changed.Add(kv.Key);
                        }
                    }

                    // Remove deleted entries from snapshot
                    var existingKeys = new List<string>(_watchFileSnapshot.Keys);
                    foreach (string k in existingKeys)
                    {
                        if (!current.ContainsKey(k))
                        {
                            _watchFileSnapshot.Remove(k);
                        }
                    }
                }

                foreach (string fullPath in changed)
                {
                    if (!ShouldIgnoreDiskChangeEvent(fullPath))
                    {
                        ScheduleWatchEvent(fullPath);
                    }
                }
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _pollRunning, 0);
            }
        }

        private static Dictionary<string, string> CaptureFileStatSnapshot(string rootDir)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(rootDir)) return map;
            CollectFileStatSnapshot(new DirectoryInfo(rootDir), map);
            return map;
        }

        private static void CollectFileStatSnapshot(DirectoryInfo dir, Dictionary<string, string> map)
        {
            try
            {
                foreach (var sub in dir.EnumerateDirectories())
                {
                    if (string.Equals(sub.Name, ".git", StringComparison.OrdinalIgnoreCase))
                        continue;
                    CollectFileStatSnapshot(sub, map);
                }

                foreach (var file in dir.EnumerateFiles("*.st"))
                {
                    map[file.FullName] = file.LastWriteTimeUtc.Ticks.ToString() + ":" + file.Length.ToString();
                }
            }
            catch { }
        }

        private static void ScheduleWatchEvent(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return;
            string normalized = Path.GetFullPath(fullPath);
            if (normalized.IndexOf(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0)
                return;

            lock (_watcherLock)
            {
                if (_watcher == null || _watchCallback == null) return;

                if (_debounceTimers.TryGetValue(normalized, out var existing))
                {
                    existing.Change(_watchDebounceMs, Timeout.Infinite);
                }
                else
                {
                    var timer = new Timer(_ => OnDebounceTimerFired(normalized), null, _watchDebounceMs, Timeout.Infinite);
                    _debounceTimers[normalized] = timer;
                }
            }
        }

        private static void OnDebounceTimerFired(string fullPath)
        {
            Action<string> cb;
            lock (_watcherLock)
            {
                if (_debounceTimers.TryGetValue(fullPath, out var t))
                {
                    try { t.Dispose(); } catch { }
                    _debounceTimers.Remove(fullPath);
                }
                cb = _watchCallback;
            }

            if (cb == null || !File.Exists(fullPath)) return;
            if (ShouldIgnoreDiskChangeEvent(fullPath)) return;

            try
            {
                cb(fullPath);
            }
            catch { }
        }

        private static string StripLeadingIecComments(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            int idx = 0;
            int len = text.Length;
            while (idx < len)
            {
                while (idx < len && char.IsWhiteSpace(text[idx])) idx++;
                if (idx + 1 < len && text[idx] == '(' && text[idx + 1] == '*')
                {
                    int close = text.IndexOf("*)", idx + 2, StringComparison.Ordinal);
                    if (close < 0) return string.Empty;
                    idx = close + 2;
                    continue;
                }
                if (idx + 1 < len && text[idx] == '/' && text[idx + 1] == '/')
                {
                    int nl = text.IndexOf('\n', idx + 2);
                    if (nl < 0) return string.Empty;
                    idx = nl + 1;
                    continue;
                }
                break;
            }
            return idx < len ? text.Substring(idx) : string.Empty;
        }

        private static bool LooksLikeDeclarationBlock(string text)
        {
            string codeOnly = StripLeadingIecComments(text);
            if (string.IsNullOrWhiteSpace(codeOnly)) return false;

            string upper = codeOnly.TrimStart().ToUpperInvariant();
            string[] declPrefixes = new[]
            {
                "PROGRAM ", "FUNCTION_BLOCK ", "FUNCTION ", "INTERFACE ",
                "METHOD ", "PROPERTY ", "VAR_GLOBAL", "TYPE ", "VAR ", "VAR\n", "VAR_INPUT", "VAR_OUTPUT", "VAR_IN_OUT"
            };
            foreach (string p in declPrefixes)
            {
                if (upper.StartsWith(p, StringComparison.Ordinal)) return true;
            }
            return upper.Contains("\nVAR ") || upper.Contains("\nVAR\n") || upper.Contains("\nVAR_INPUT") || upper.Contains("\nVAR_OUTPUT");
        }

        private static byte[] ReadAllBytesWithRetry(string path)
        {
            for (int i = 0; i < 4; i++)
            {
                try
                {
                    return File.ReadAllBytes(path);
                }
                catch (IOException)
                {
                    Thread.Sleep(40);
                }
            }
            return null;
        }

        private static string ComputeSha256Base64(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                return Convert.ToBase64String(sha.ComputeHash(bytes));
            }
        }

        public static string ComputeContentHash(string text)
        {
            return ComputeSha256Base64(Utf8NoBom.GetBytes(text ?? string.Empty));
        }

        public static void RecordIdeContentHash(string relativePath, string content)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return;
            string norm = relativePath.Replace('\\', '/');
            string hash = ComputeContentHash(content);
            lock (_hashLock)
            {
                _lastKnownIdeHashes[norm] = hash;
            }
        }

        public static string GetIdeContentHash(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return null;
            string norm = relativePath.Replace('\\', '/');
            lock (_hashLock)
            {
                return _lastKnownIdeHashes.TryGetValue(norm, out string h) ? h : null;
            }
        }

        public static void RemoveIdeContentHash(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return;
            string norm = relativePath.Replace('\\', '/');
            lock (_hashLock)
            {
                _lastKnownIdeHashes.Remove(norm);
            }
        }

        public static void ClearIdeContentHashes()
        {
            lock (_hashLock)
            {
                _lastKnownIdeHashes.Clear();
            }
        }

        public static List<string> GetTrackedIdePaths()
        {
            lock (_hashLock)
            {
                return _lastKnownIdeHashes.Keys.ToList();
            }
        }

        private static readonly object _gitLock = new object();

        public static bool EnsureGitInitialized(string mirrorDir)
        {
            if (string.IsNullOrWhiteSpace(mirrorDir)) return false;
            string fullRoot = Path.GetFullPath(mirrorDir.Trim());
            Directory.CreateDirectory(fullRoot);

            lock (_gitLock)
            {
                EnsureMirrorReadme(fullRoot);

                string gitDir = Path.Combine(fullRoot, ".git");
                if (Directory.Exists(gitDir))
                {
                    return false;
                }

                if (!RunGit(fullRoot, "init", out _, out _))
                {
                    return false;
                }

                EnsureLocalGitIdentity(fullRoot);
                return true;
            }
        }

        public static void EnsureMirrorReadme(string mirrorDir)
        {
            if (string.IsNullOrWhiteSpace(mirrorDir)) return;
            try
            {
                string fullRoot = Path.GetFullPath(mirrorDir.Trim())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string parentDir = Path.GetDirectoryName(fullRoot);
                if (string.IsNullOrWhiteSpace(parentDir)) return;

                if (!string.Equals(Path.GetFileName(parentDir), ".cds_mcp", StringComparison.OrdinalIgnoreCase))
                    return;

                string gitignorePath = Path.Combine(parentDir, ".gitignore");
                if (!File.Exists(gitignorePath))
                {
                    File.WriteAllText(gitignorePath, "*\n", Utf8NoBom);
                }

                string readmePath = Path.Combine(parentDir, "README.md");
                if (File.Exists(readmePath)) return;

                string content =
                    "# CODESYS MCP Auto-Mirror (`.cds_mcp/src`)\n\n" +
                    "- **Mode**: One-way (`CODESYS Project -> .cds_mcp/src`)\n" +
                    "- **Auto-Sync**: All MCP tool edits and manual CODESYS IDE edits are automatically mirrored here as `.st` files.\n" +
                    "- **Auto-Git**: `src/` is a git repository that automatically commits whenever the CODESYS project is saved (`save_project` or IDE `Ctrl+S`).\n" +
                    "- **For AI Agents**: Read `.st` files and use `git -C .cds_mcp/src log` / `git diff` to inspect code and track changes. **Do not edit `.st` files directly** — use CODESYS MCP tools (`write_pou`, `create_method`, `delete_pou`, etc.) to modify the project.\n";
                File.WriteAllText(readmePath, content, Utf8NoBom);
            }
            catch { }
        }

        public static MirrorGitCommitResult CommitMirrorChanges(string mirrorDir, string commitMessage)
        {
            var res = new MirrorGitCommitResult();
            if (string.IsNullOrWhiteSpace(mirrorDir))
            {
                res.message = "Mirror directory is empty.";
                return res;
            }

            string fullRoot = Path.GetFullPath(mirrorDir.Trim());
            if (!Directory.Exists(fullRoot))
            {
                res.message = "Mirror directory does not exist.";
                return res;
            }

            lock (_gitLock)
            {
                if (!Directory.Exists(Path.Combine(fullRoot, ".git")))
                {
                    res.initialized = EnsureGitInitialized(fullRoot);
                    if (!Directory.Exists(Path.Combine(fullRoot, ".git")))
                    {
                        res.gitAvailable = false;
                        res.message = "git not available or failed to initialize.";
                        return res;
                    }
                }

                res.gitAvailable = true;
                EnsureLocalGitIdentity(fullRoot);

                if (!RunGit(fullRoot, "status --porcelain", out string statusOut, out string statusErr))
                {
                    res.message = "git status failed: " + statusErr;
                    return res;
                }

                if (string.IsNullOrWhiteSpace(statusOut))
                {
                    res.committed = false;
                    res.message = "Working tree clean (no changes to commit).";
                    return res;
                }

                if (!RunGit(fullRoot, "add -A", out _, out string addErr))
                {
                    res.message = "git add failed: " + addErr;
                    return res;
                }

                string safeMsg = string.IsNullOrWhiteSpace(commitMessage)
                    ? "chore(mirror): sync .st sources"
                    : commitMessage.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ").Trim();

                if (!RunGit(fullRoot, $"commit -q -m \"{safeMsg}\"", out _, out string commitErr, 20000))
                {
                    res.message = "git commit failed: " + commitErr;
                    return res;
                }

                res.committed = true;
                if (RunGit(fullRoot, "rev-parse --short HEAD", out string hashOut, out _))
                {
                    res.commitHash = (hashOut ?? string.Empty).Trim();
                }
                lock (_configLock)
                {
                    _lastCommitHash = res.commitHash;
                    _lastCommitTimeUtc = DateTime.UtcNow;
                }
                res.message = safeMsg;
                return res;
            }
        }

        private static void EnsureLocalGitIdentity(string repoDir)
        {
            if (RunGit(repoDir, "config user.name", out string nameOut, out _) && !string.IsNullOrWhiteSpace(nameOut))
            {
                if (RunGit(repoDir, "config user.email", out string emailOut, out _) && !string.IsNullOrWhiteSpace(emailOut))
                {
                    return;
                }
            }

            RunGit(repoDir, "config user.name \"CODESYS MCP\"", out _, out _);
            RunGit(repoDir, "config user.email \"mcp@codesys.local\"", out _, out _);
        }

        private static bool RunGit(string workingDir, string arguments, out string stdout, out string stderr, int timeoutMs = 8000)
        {
            stdout = string.Empty;
            stderr = string.Empty;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = workingDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Utf8NoBom,
                    StandardErrorEncoding = Utf8NoBom
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return false;
                    var errSb = new StringBuilder();
                    proc.ErrorDataReceived += (s, e) => { if (e.Data != null) errSb.AppendLine(e.Data); };
                    proc.BeginErrorReadLine();
                    stdout = proc.StandardOutput.ReadToEnd();
                    if (!proc.WaitForExit(timeoutMs))
                    {
                        try { proc.Kill(); } catch { }
                        stderr = "git timed out";
                        return false;
                    }
                    stderr = errSb.ToString();
                    return proc.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                stderr = ex.Message;
                return false;
            }
        }

        public static string FormatCompactDiff(string beforeText, string afterText, int maxLines = 10)
        {
            string[] a = NormalizeLf(beforeText ?? string.Empty).TrimEnd('\n').Split('\n');
            string[] b = NormalizeLf(afterText ?? string.Empty).TrimEnd('\n').Split('\n');
            if (a.Length == 1 && a[0] == string.Empty) a = new string[0];
            if (b.Length == 1 && b[0] == string.Empty) b = new string[0];

            int m = a.Length;
            int n = b.Length;
            var dp = new int[m + 1, n + 1];
            for (int i = m - 1; i >= 0; i--)
            {
                for (int j = n - 1; j >= 0; j--)
                {
                    if (string.Equals(a[i], b[j], StringComparison.Ordinal))
                        dp[i, j] = dp[i + 1, j + 1] + 1;
                    else
                        dp[i, j] = Math.Max(dp[i + 1, j], dp[i, j + 1]);
                }
            }

            var diffLines = new List<string>();
            int added = 0;
            int removed = 0;
            int x = 0, y = 0;
            while (x < m && y < n)
            {
                if (string.Equals(a[x], b[y], StringComparison.Ordinal))
                {
                    x++;
                    y++;
                }
                else if (dp[x + 1, y] >= dp[x, y + 1])
                {
                    removed++;
                    diffLines.Add("- " + a[x]);
                    x++;
                }
                else
                {
                    added++;
                    diffLines.Add("+ " + b[y]);
                    y++;
                }
            }
            while (x < m)
            {
                removed++;
                diffLines.Add("- " + a[x]);
                x++;
            }
            while (y < n)
            {
                added++;
                diffLines.Add("+ " + b[y]);
                y++;
            }

            if (added == 0 && removed == 0)
                return "(+0/-0)";

            var sb = new StringBuilder();
            sb.Append($"(+{added}/-{removed})");
            int limit = Math.Max(1, maxLines);
            int shown = Math.Min(diffLines.Count, limit);
            for (int i = 0; i < shown; i++)
            {
                sb.Append('\n').Append("  ").Append(diffLines[i]);
            }
            if (diffLines.Count > limit)
            {
                sb.Append($"\n  ... (+{diffLines.Count - limit} more lines)");
            }
            return sb.ToString();
        }

        private static void PruneOrphanStFiles(
            string currentDir,
            string fullRoot,
            HashSet<string> expectedFiles,
            MirrorSyncResult result)
        {
            if (!Directory.Exists(currentDir)) return;

            foreach (string subDir in Directory.GetDirectories(currentDir))
            {
                string dirName = Path.GetFileName(subDir);
                if (string.Equals(dirName, ".git", StringComparison.OrdinalIgnoreCase))
                    continue;

                PruneOrphanStFiles(subDir, fullRoot, expectedFiles, result);

                try
                {
                    if (!Directory.EnumerateFileSystemEntries(subDir).Any())
                    {
                        Directory.Delete(subDir, false);
                    }
                }
                catch { }
            }

            foreach (string file in Directory.GetFiles(currentDir, "*.st"))
            {
                string fullFile = Path.GetFullPath(file);
                if (!expectedFiles.Contains(fullFile))
                {
                    File.Delete(fullFile);
                    result.deleted++;
                    result.changedFiles.Add(GetDisplayRelativePath(fullRoot, fullFile));
                }
            }
        }

        private static void PruneEmptyParents(string dir, string fullRoot)
        {
            while (!string.IsNullOrEmpty(dir))
            {
                string normalized = Path.GetFullPath(dir)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(normalized, fullRoot, StringComparison.OrdinalIgnoreCase))
                    break;

                try
                {
                    if (Directory.Exists(normalized) && !Directory.EnumerateFileSystemEntries(normalized).Any())
                    {
                        Directory.Delete(normalized, false);
                        dir = Path.GetDirectoryName(normalized);
                        continue;
                    }
                }
                catch { }
                break;
            }
        }

        private static string GetDisplayRelativePath(string fullRoot, string fullTarget)
        {
            string prefix = fullRoot + Path.DirectorySeparatorChar;
            if (fullTarget.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return fullTarget.Substring(prefix.Length).Replace('\\', '/');
            }
            return fullTarget.Replace('\\', '/');
        }

        private static string NormalizeLf(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private static string SanitizeYamlScalar(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }
}
