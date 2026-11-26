using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace CodesysMcp
{
    public enum McpServerStatus
    {
        Offline,
        Starting,
        Online,
        Busy,
        Error
    }

    public class IdeEnvironmentInfo
    {
        public string ideVersion { get; set; }
        public string ideProfile { get; set; }
        public string ideExePath { get; set; }
        public string uiMode { get; set; }
        public string mcpServerVersion { get; set; }
    }

    public class UiIndicatorInfo
    {
        public bool installed { get; set; }
        public string status { get; set; }
        public string text { get; set; }
        public string color { get; set; }
        public string pipeName { get; set; }
        public string activeTool { get; set; }
        public int totalRequests { get; set; }
        public string lastError { get; set; }
        public bool uiPresent { get; set; }
        public string hostControlName { get; set; }
        public bool isPermanentlyVisible { get; set; }
        public bool hostControlVisible { get; set; }
        public bool hasContextMenu { get; set; }
        public string version { get; set; }
        public string ideVersion { get; set; }
        public string ideProfile { get; set; }
        public string ideExePath { get; set; }
        public string uiMode { get; set; }
    }

    public class IdeScreenshotResult
    {
        public bool success { get; set; }
        public string filePath { get; set; }
        public int width { get; set; }
        public int height { get; set; }
        public long fileSizeBytes { get; set; }
        public string windowTitle { get; set; }
        public string captureMethod { get; set; }
        public string message { get; set; }
    }

    public static class CodesysIdeUiHelper
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
        private const uint PW_RENDERFULLCONTENT = 0x00000002;
        private static readonly object _syncLock = new object();
        private static ToolStripStatusLabel _statusBadge = null;
        private static StatusStrip _hostStatusStrip = null;
        private static ContextMenuStrip _contextMenu = null;
        private static McpServerStatus _currentStatus = McpServerStatus.Offline;
        private static string _currentPipeName = "codesys_mcp_pipe";
        private static string _activeTool = null;
        private static string _lastError = null;
        private static int _requestCounter = 0;

        public static McpServerStatus CurrentStatus => _currentStatus;
        public static string CurrentPipeName => _currentPipeName;
        public static int TotalRequests => _requestCounter;
        public static ContextMenuStrip ActiveContextMenu => _contextMenu;

        public static void InstallStatusIndicator(string pipeName)
        {
            _currentPipeName = pipeName ?? "codesys_mcp_pipe";
            _currentStatus = McpServerStatus.Starting;

            CodesysContext.RunOnPrimaryThread(() =>
            {
                try
                {
                    Control mainForm = FindMainCodesysForm();
                    if (mainForm == null)
                    {
                        CodesysContext.LogToIde("[INFO] CodesysIdeUiHelper: No WinForms main window found, skipping visual indicator (Headless mode).");
                        _currentStatus = McpServerStatus.Online;
                        return;
                    }

                    // Locate or create dedicated persistent CodesysMcpStatusStrip
                    StatusStrip ss = FindOrCreateDedicatedStatusStrip(mainForm);
                    _hostStatusStrip = ss;
                    ss.ShowItemToolTips = true;
                    ss.Visible = true;

                    // Remove existing badge if hot reloaded
                    if (_statusBadge != null)
                    {
                        try { ss.Items.Remove(_statusBadge); } catch { }
                        _statusBadge = null;
                    }

                    for (int i = ss.Items.Count - 1; i >= 0; i--)
                    {
                        if (ss.Items[i].Name == "CodesysMcpStatusBadge")
                        {
                            try { ss.Items.RemoveAt(i); } catch { }
                        }
                    }

                    // Create Context Menu (Phase 2)
                    _contextMenu = CreateContextMenu();

                    // Create Status Badge
                    _statusBadge = new ToolStripStatusLabel
                    {
                        Name = "CodesysMcpStatusBadge",
                        Text = "● MCP: Online",
                        ForeColor = Color.ForestGreen,
                        Font = new Font(ss.Font.FontFamily, Math.Max(ss.Font.Size, 9f), FontStyle.Bold),
                        BorderSides = ToolStripStatusLabelBorderSides.All,
                        BorderStyle = Border3DStyle.Flat,
                        Margin = new Padding(6, 2, 6, 2),
                        Alignment = ToolStripItemAlignment.Right,
                        ToolTipText = BuildTooltipText()
                    };

                    _statusBadge.MouseEnter += (s, e) =>
                    {
                        if (_hostStatusStrip != null && !_hostStatusStrip.IsDisposed)
                        {
                            _hostStatusStrip.Cursor = Cursors.Hand;
                        }
                    };
                    _statusBadge.MouseLeave += (s, e) =>
                    {
                        if (_hostStatusStrip != null && !_hostStatusStrip.IsDisposed)
                        {
                            _hostStatusStrip.Cursor = Cursors.Default;
                        }
                    };

                    // Bind click actions
                    _statusBadge.Click += (s, e) =>
                    {
                        if (_contextMenu != null && !_contextMenu.IsDisposed)
                        {
                            _contextMenu.Show(Cursor.Position);
                        }
                    };
                    _statusBadge.MouseUp += (s, e) =>
                    {
                        if (e.Button == MouseButtons.Right && _contextMenu != null && !_contextMenu.IsDisposed)
                        {
                            _contextMenu.Show(Cursor.Position);
                        }
                    };

                    ss.Items.Add(_statusBadge);
                    ss.SendToBack();

                    _currentStatus = McpServerStatus.Online;
                    CodesysContext.LogToIde($"[SUCCESS] CodesysIdeUiHelper: MCP visual status badge (v{GetServerVersion()}) installed on persistent strip '{ss.Name}'.");
                }
                catch (Exception ex)
                {
                    CodesysContext.LogToIde($"[WARNING] CodesysIdeUiHelper.InstallStatusIndicator warning: {ex.Message}");
                    _currentStatus = McpServerStatus.Online;
                }
            });
        }

        public static void EnsureIndicatorVisible()
        {
            CodesysContext.RunOnPrimaryThread(() =>
            {
                try
                {
                    Control mainForm = FindMainCodesysForm();
                    if (mainForm == null) return;

                    if (_hostStatusStrip == null || _hostStatusStrip.IsDisposed || !mainForm.Controls.Contains(_hostStatusStrip))
                    {
                        InstallStatusIndicator(_currentPipeName);
                        return;
                    }

                    if (!_hostStatusStrip.Visible)
                    {
                        _hostStatusStrip.Visible = true;
                    }

                    if (_statusBadge == null || _statusBadge.IsDisposed || !_hostStatusStrip.Items.Contains(_statusBadge))
                    {
                        InstallStatusIndicator(_currentPipeName);
                        return;
                    }

                    _hostStatusStrip.SendToBack();
                    ApplyStatusToBadge();
                }
                catch (Exception ex)
                {
                    CodesysContext.LogToIde($"[WARNING] CodesysIdeUiHelper.EnsureIndicatorVisible warning: {ex.Message}");
                }
            });
        }

        public static void UpdateStatus(McpServerStatus status, string activeTool = null, string detail = null)
        {
            lock (_syncLock)
            {
                _currentStatus = status;
                _activeTool = activeTool;
                if (status == McpServerStatus.Busy)
                {
                    _requestCounter++;
                }
                if (status == McpServerStatus.Error && !string.IsNullOrEmpty(detail))
                {
                    _lastError = detail;
                }
            }

            if (_statusBadge == null || _hostStatusStrip == null)
            {
                return;
            }

            try
            {
                if (_hostStatusStrip.InvokeRequired)
                {
                    _hostStatusStrip.BeginInvoke(new Action(ApplyStatusToBadge));
                }
                else
                {
                    ApplyStatusToBadge();
                }
            }
            catch { }
        }

        private static void ApplyStatusToBadge()
        {
            if (_statusBadge == null || _statusBadge.IsDisposed) return;

            switch (_currentStatus)
            {
                case McpServerStatus.Online:
                    _statusBadge.Text = SourceMirrorHelper.HasActiveMirror ? "● MCP: Online | Mirror: ON" : "● MCP: Online";
                    _statusBadge.ForeColor = Color.ForestGreen;
                    break;
                case McpServerStatus.Busy:
                    string toolName = !string.IsNullOrEmpty(_activeTool) ? _activeTool : "executing";
                    _statusBadge.Text = $"● MCP: Busy ({toolName})";
                    _statusBadge.ForeColor = Color.DarkOrange;
                    break;
                case McpServerStatus.Error:
                    _statusBadge.Text = "▲ MCP: Warning";
                    _statusBadge.ForeColor = Color.Crimson;
                    break;
                case McpServerStatus.Starting:
                    _statusBadge.Text = "◐ MCP: Starting...";
                    _statusBadge.ForeColor = Color.DodgerBlue;
                    break;
                case McpServerStatus.Offline:
                default:
                    _statusBadge.Text = "○ MCP: Offline";
                    _statusBadge.ForeColor = Color.Gray;
                    break;
            }

            _statusBadge.ToolTipText = BuildTooltipText();
        }

        public static void RefreshMirrorIndicator()
        {
            if (_statusBadge == null || _hostStatusStrip == null) return;
            try
            {
                if (_hostStatusStrip.InvokeRequired)
                {
                    _hostStatusStrip.BeginInvoke(new Action(ApplyStatusToBadge));
                }
                else
                {
                    ApplyStatusToBadge();
                }
            }
            catch { }
        }

        public static string GetServerVersion()
        {
            var ver = typeof(CodesysIdeUiHelper).Assembly.GetName().Version;
            if (ver == null) return "1.0.0";
            return ver.Revision > 0 ? ver.ToString(4) : ver.ToString(3);
        }

        public static IdeEnvironmentInfo GetIdeEnvironmentInfo(string[] cmdArgsOverride = null, string exePathOverride = null)
        {
            string exePath = exePathOverride;
            if (string.IsNullOrEmpty(exePath))
            {
                try
                {
                    exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                }
                catch { }
            }

            bool isCodesysHost = !string.IsNullOrEmpty(exePath) &&
                Path.GetFileName(exePath).StartsWith("CODESYS", StringComparison.OrdinalIgnoreCase);

            string ideVersion = null;
            if (isCodesysHost && File.Exists(exePath))
            {
                try
                {
                    var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
                    if (fvi.FileMajorPart > 0)
                    {
                        ideVersion = $"{fvi.FileMajorPart}.{fvi.FileMinorPart}.{fvi.FileBuildPart}.{fvi.FilePrivatePart}";
                    }
                    else if (!string.IsNullOrWhiteSpace(fvi.ProductVersion))
                    {
                        ideVersion = fvi.ProductVersion.Trim();
                    }
                }
                catch { }
            }

            if (string.IsNullOrEmpty(ideVersion) && !string.IsNullOrEmpty(exePath))
            {
                var pathMatch = System.Text.RegularExpressions.Regex.Match(
                    exePath,
                    @"CODESYS\s+(\d+\.\d+\.\d+\.\d+)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (pathMatch.Success)
                {
                    ideVersion = pathMatch.Groups[1].Value;
                }
            }

            if (string.IsNullOrEmpty(ideVersion))
            {
                try
                {
                    var coreVer = typeof(_3S.CoDeSys.ScriptEngine.BasicFunctionality.ISystem).Assembly.GetName().Version;
                    if (coreVer != null && coreVer.Major > 0)
                    {
                        ideVersion = coreVer.ToString(4);
                    }
                }
                catch { }
            }

            if (string.IsNullOrEmpty(ideVersion))
            {
                ideVersion = "OfflineHost";
            }

            string[] args = cmdArgsOverride;
            if (args == null)
            {
                try { args = Environment.GetCommandLineArgs(); } catch { args = new string[0]; }
            }

            string ideProfile = null;
            bool hasNoUiFlag = false;
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i] ?? string.Empty;
                    if (string.Equals(a, "--noUI", StringComparison.OrdinalIgnoreCase))
                    {
                        hasNoUiFlag = true;
                    }
                    else if (a.StartsWith("--profile=", StringComparison.OrdinalIgnoreCase))
                    {
                        ideProfile = a.Substring("--profile=".Length).Trim().Trim('"');
                    }
                    else if (string.Equals(a, "--profile", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        ideProfile = (args[i + 1] ?? string.Empty).Trim().Trim('"');
                    }
                }
            }

            if (string.IsNullOrEmpty(ideProfile))
            {
                try
                {
                    var mainForm = FindMainCodesysForm();
                    if (mainForm != null && !string.IsNullOrWhiteSpace(mainForm.Text))
                    {
                        ideProfile = mainForm.Text.Trim();
                    }
                }
                catch { }
            }

            if (string.IsNullOrEmpty(ideProfile))
            {
                ideProfile = char.IsDigit(ideVersion[0]) ? $"CODESYS V{ideVersion}" : "Default";
            }

            string uiMode;
            if (hasNoUiFlag)
            {
                uiMode = "Headless";
            }
            else if (CodesysContext.System != null)
            {
                bool uiPresent = false;
                try
                {
                    uiPresent = Convert.ToBoolean(CodesysContext.System.GetType().GetProperty("ui_present")?.GetValue(CodesysContext.System, null) ?? false);
                }
                catch { }
                uiMode = uiPresent ? "GUI" : "Headless";
            }
            else if (exePathOverride != null)
            {
                uiMode = "GUI";
            }
            else
            {
                uiMode = "OfflineHost";
            }

            return new IdeEnvironmentInfo
            {
                ideVersion = ideVersion,
                ideProfile = ideProfile,
                ideExePath = (isCodesysHost || exePathOverride != null) ? exePath : null,
                uiMode = uiMode,
                mcpServerVersion = GetServerVersion()
            };
        }

        public static string BuildMcpClientConfigJson(string pipeName = null)
        {
            string exeDir = Path.GetDirectoryName(typeof(CodesysIdeUiHelper).Assembly.Location) ?? string.Empty;
            if (string.Equals(Path.GetFileName(exeDir), "shadow", StringComparison.OrdinalIgnoreCase))
            {
                exeDir = Path.GetDirectoryName(exeDir) ?? exeDir;
            }
            string cliPath = Path.Combine(exeDir, "CodesysMcpCli.exe").Replace("\\", "\\\\");
            string pipe = string.IsNullOrWhiteSpace(pipeName) ? _currentPipeName : pipeName;
            return "{\n" +
                   "  \"mcpServers\": {\n" +
                   "    \"codesys\": {\n" +
                   $"      \"command\": \"{cliPath}\",\n" +
                   $"      \"args\": [\"--pipe\", \"{pipe}\"]\n" +
                   "    }\n" +
                   "  }\n" +
                   "}";
        }

        public static string BuildTooltipText()
        {
            string mirrorStatus = SourceMirrorHelper.HasActiveMirror
                ? $"ON ({SourceMirrorHelper.ActiveMirrorDirectory})"
                : "OFF";
            string lastCommitLine = !string.IsNullOrEmpty(SourceMirrorHelper.LastCommitHash)
                ? $"Last Commit: {SourceMirrorHelper.LastCommitHash} ({SourceMirrorHelper.LastCommitTimeUtc.ToLocalTime():HH:mm:ss})\n"
                : string.Empty;
            return $"CODESYS MCP Server v{GetServerVersion()}\n" +
                   $"Status: {_currentStatus}\n" +
                   $"Pipe: \\\\.\\pipe\\{_currentPipeName}\n" +
                   $"Mirror Sync: {mirrorStatus}\n" +
                   lastCommitLine +
                   $"Total Requests: {_requestCounter}\n" +
                   $"Active Tool: {_activeTool ?? "None"}\n" +
                   (!string.IsNullOrEmpty(_lastError) ? $"Last Error: {_lastError}\n" : "State: Healthy\n") +
                   "(Click for diagnostics and quick actions)";
        }

        public static void RemoveStatusIndicator()
        {
            _currentStatus = McpServerStatus.Offline;
            if (_statusBadge != null && _hostStatusStrip != null)
            {
                try
                {
                    CodesysContext.RunOnPrimaryThread(() =>
                    {
                        try
                        {
                            if (!_hostStatusStrip.IsDisposed && !_statusBadge.IsDisposed)
                            {
                                _hostStatusStrip.Items.Remove(_statusBadge);
                            }
                        }
                        catch { }
                        _statusBadge = null;
                        _hostStatusStrip = null;
                        if (_contextMenu != null)
                        {
                            try { _contextMenu.Dispose(); } catch { }
                            _contextMenu = null;
                        }
                    });
                }
                catch { }
            }
        }

        public static UiIndicatorInfo GetIndicatorInfo()
        {
            bool badgeVisible = _statusBadge != null && !_statusBadge.IsDisposed && _statusBadge.Visible;
            bool hostVisible = _hostStatusStrip != null && !_hostStatusStrip.IsDisposed && _hostStatusStrip.Visible;
            var env = GetIdeEnvironmentInfo();

            return new UiIndicatorInfo
            {
                installed = _statusBadge != null && !_statusBadge.IsDisposed,
                status = _currentStatus.ToString(),
                text = _statusBadge?.Text ?? $"○ MCP: {_currentStatus}",
                color = GetColorHex(_statusBadge?.ForeColor ?? Color.Gray),
                pipeName = _currentPipeName,
                activeTool = _activeTool,
                totalRequests = _requestCounter,
                lastError = _lastError,
                uiPresent = CodesysContext.System != null && Convert.ToBoolean(CodesysContext.System.GetType().GetProperty("ui_present")?.GetValue(CodesysContext.System, null) ?? false),
                hostControlName = _hostStatusStrip?.Name,
                isPermanentlyVisible = badgeVisible && hostVisible,
                hostControlVisible = hostVisible,
                hasContextMenu = _contextMenu != null && !_contextMenu.IsDisposed,
                version = GetServerVersion(),
                ideVersion = env.ideVersion,
                ideProfile = env.ideProfile,
                ideExePath = env.ideExePath,
                uiMode = env.uiMode
            };
        }

        private static string GetColorHex(Color c)
        {
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }

        private static Control FindMainCodesysForm()
        {
            try
            {
                var openForms = Application.OpenForms;
                if (openForms != null && openForms.Count > 0)
                {
                    for (int i = 0; i < openForms.Count; i++)
                    {
                        var f = openForms[i];
                        if (f != null && !f.IsDisposed && f.IsHandleCreated)
                        {
                            if (f.Text.IndexOf("CODESYS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                f.GetType().Name == "MainForm")
                            {
                                return f;
                            }
                        }
                    }
                    return openForms[0];
                }
            }
            catch { }
            return null;
        }

        private static StatusStrip FindOrCreateDedicatedStatusStrip(Control mainForm)
        {
            if (mainForm == null) return null;

            // Check if our dedicated status strip already exists
            foreach (Control c in mainForm.Controls)
            {
                if (c is StatusStrip ss && c.Name == "CodesysMcpStatusStrip" && !c.IsDisposed)
                {
                    return ss;
                }
            }

            // Create dedicated persistent StatusStrip
            var dedicatedStrip = new StatusStrip
            {
                Name = "CodesysMcpStatusStrip",
                Dock = DockStyle.Bottom,
                Height = 22,
                BackColor = SystemColors.Control,
                RenderMode = ToolStripRenderMode.System,
                GripStyle = ToolStripGripStyle.Hidden,
                Padding = Padding.Empty,
                Margin = Padding.Empty,
                ShowItemToolTips = true,
                Visible = true
            };

            mainForm.Controls.Add(dedicatedStrip);
            dedicatedStrip.SendToBack();
            return dedicatedStrip;
        }

        private static bool SafeSetClipboard(string text)
        {
            bool success = false;
            var t = new Thread(() =>
            {
                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        Clipboard.SetDataObject(text, true, 5, 100);
                        success = true;
                        return;
                    }
                    catch
                    {
                        Thread.Sleep(50);
                    }
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(1000);
            return success;
        }

        public static string ResolveSessionLogPath()
        {
            // 1. Explicit environment variable override
            string envPath = Environment.GetEnvironmentVariable("CODESYS_MCP_LOG_FILE");
            if (!string.IsNullOrEmpty(envPath))
            {
                return envPath;
            }

            // 2. Standard user home directory: ~/.codesys-mcp-plugin/session_debug.log
            string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string standardLog = !string.IsNullOrEmpty(userHome)
                ? Path.Combine(userHome, ".codesys-mcp-plugin", "session_debug.log")
                : null;

            if (standardLog != null && File.Exists(standardLog))
            {
                return standardLog;
            }

            // 3. Probe upwards from executing assembly location for an existing session_debug.log
            try
            {
                string asmLocation = typeof(CodesysIdeUiHelper).Assembly.Location;
                if (!string.IsNullOrEmpty(asmLocation))
                {
                    string dir = Path.GetDirectoryName(asmLocation);
                    while (!string.IsNullOrEmpty(dir))
                    {
                        string candidate = Path.Combine(dir, "session_debug.log");
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                        dir = Path.GetDirectoryName(dir);
                    }
                }
            }
            catch
            {
            }

            // 4. Default return standard path (or AppDomain directory fallback)
            return standardLog ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "session_debug.log");
        }

        public static ContextMenuStrip CreateContextMenu()
        {
            var menu = new ContextMenuStrip();

            var copyItem = new ToolStripMenuItem("📋 Copy Named Pipe Address");
            copyItem.Click += (s, e) =>
            {
                try
                {
                    string pipePath = @"\\.\pipe\" + _currentPipeName;
                    bool copied = SafeSetClipboard(pipePath);
                    if (copied)
                    {
                        MessageBox.Show(
                            $"Named Pipe address copied to clipboard:\n\n{pipePath}\n\nYou can use this address in Cursor, Claude Desktop, or your MCP client configuration.",
                            "CODESYS MCP Server",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                    }
                    else
                    {
                        CodesysContext.LogToIde("[ERROR] CodesysIdeUiHelper: SafeSetClipboard failed after retries.");
                    }
                }
                catch (Exception ex)
                {
                    CodesysContext.LogToIde("[ERROR] CodesysIdeUiHelper: Copy pipe to clipboard failed: " + ex.Message);
                }
            };
            menu.Items.Add(copyItem);

            var diagItem = new ToolStripMenuItem("🔍 Show MCP Diagnostics Card...");
            diagItem.Click += (s, e) =>
            {
                try
                {
                    ShowDiagnosticsDialog();
                }
                catch (Exception ex)
                {
                    CodesysContext.LogToIde("[ERROR] CodesysIdeUiHelper: ShowDiagnosticsDialog failed: " + ex.Message);
                }
            };
            menu.Items.Add(diagItem);

            var logItem = new ToolStripMenuItem("📂 Open MCP Session Log");
            logItem.Click += (s, e) =>
            {
                try
                {
                    string logFile = ResolveSessionLogPath();
                    string logDir = Path.GetDirectoryName(logFile);
                    if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
                    {
                        try { Directory.CreateDirectory(logDir); } catch { }
                    }

                    if (!File.Exists(logFile))
                    {
                        try
                        {
                            File.WriteAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] CODESYS MCP Session Log initialized at: {logFile}\r\n");
                        }
                        catch
                        {
                        }
                    }

                    if (File.Exists(logFile))
                    {
                        System.Diagnostics.Process.Start("notepad.exe", logFile);
                    }
                    else
                    {
                        MessageBox.Show("Session log file could not be created or opened at:\n" + logFile, "CODESYS MCP", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                catch (Exception ex)
                {
                    CodesysContext.LogToIde("[ERROR] CodesysIdeUiHelper: Open log failed: " + ex.Message);
                }
            };
            menu.Items.Add(logItem);

            menu.Items.Add(new ToolStripSeparator());

            var mirrorToggleItem = new ToolStripMenuItem("🔄 Auto .st Mirror Sync (Project → .st)");
            mirrorToggleItem.Click += (s, e) =>
            {
                try
                {
                    if (SourceMirrorHelper.HasActiveMirror)
                    {
                        CodesysOperations.SyncSourcesToDisk(disable: true);
                        CodesysContext.LogToIde("[Mirror] Auto .st Mirror Sync disabled via IDE menu.");
                    }
                    else
                    {
                        string targetDir = SourceMirrorHelper.LastBoundMirrorDirectory;
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
                            targetDir = BrowseForMirrorFolder();
                        }
                        if (!string.IsNullOrWhiteSpace(targetDir))
                        {
                            CodesysOperations.SyncSourcesToDisk(targetDir, persistForSession: true, disable: false, rootNode: SourceMirrorHelper.ActiveRootNode, watch: true);
                        }
                    }
                    RefreshMirrorIndicator();
                }
                catch (Exception ex)
                {
                    CodesysContext.LogToIde("[ERROR] CodesysIdeUiHelper: Mirror toggle failed: " + ex.Message);
                    MessageBox.Show("Mirror sync toggle failed:\n" + ex.Message, "CODESYS MCP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            menu.Items.Add(mirrorToggleItem);

            var mirrorFolderItem = new ToolStripMenuItem("📁 Choose .st Mirror Folder...");
            mirrorFolderItem.Click += (s, e) =>
            {
                try
                {
                    string selected = BrowseForMirrorFolder(SourceMirrorHelper.ActiveMirrorDirectory ?? SourceMirrorHelper.LastBoundMirrorDirectory);
                    if (!string.IsNullOrWhiteSpace(selected))
                    {
                        CodesysOperations.SyncSourcesToDisk(selected, persistForSession: true, disable: false, rootNode: SourceMirrorHelper.ActiveRootNode, watch: true);
                        RefreshMirrorIndicator();
                    }
                }
                catch (Exception ex)
                {
                    CodesysContext.LogToIde("[ERROR] CodesysIdeUiHelper: Choose mirror folder failed: " + ex.Message);
                    MessageBox.Show("Failed to bind mirror folder:\n" + ex.Message, "CODESYS MCP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            menu.Items.Add(mirrorFolderItem);

            var openMirrorItem = new ToolStripMenuItem("📂 Open .st Mirror Folder");
            openMirrorItem.Click += (s, e) =>
            {
                try
                {
                    string dir = SourceMirrorHelper.ActiveMirrorDirectory ?? SourceMirrorHelper.LastBoundMirrorDirectory;
                    if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    {
                        System.Diagnostics.Process.Start("explorer.exe", dir);
                    }
                    else
                    {
                        MessageBox.Show("No active .st mirror folder found.", "CODESYS MCP", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                catch (Exception ex)
                {
                    CodesysContext.LogToIde("[ERROR] CodesysIdeUiHelper: Open mirror folder failed: " + ex.Message);
                }
            };
            menu.Items.Add(openMirrorItem);

            var copyConfigItem = new ToolStripMenuItem("📋 Copy MCP Client Config (JSON)");
            copyConfigItem.Click += (s, e) =>
            {
                try
                {
                    string cfg = BuildMcpClientConfigJson();
                    Clipboard.SetText(cfg);
                    MessageBox.Show("MCP client config JSON copied to clipboard!", "CODESYS MCP", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    CodesysContext.LogToIde("[ERROR] CodesysIdeUiHelper: Copy config failed: " + ex.Message);
                }
            };
            menu.Items.Add(copyConfigItem);

            menu.Opening += (s, e) =>
            {
                bool active = SourceMirrorHelper.HasActiveMirror;
                mirrorToggleItem.Checked = active;
                mirrorToggleItem.Text = active
                    ? "🔄 Auto .st Mirror Sync (ON)"
                    : "🔄 Auto .st Mirror Sync (OFF)";
                string dir = SourceMirrorHelper.ActiveMirrorDirectory ?? SourceMirrorHelper.LastBoundMirrorDirectory;
                openMirrorItem.Enabled = !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir);
            };

            menu.Items.Add(new ToolStripSeparator());

            var reanchorItem = new ToolStripMenuItem("🔄 Re-anchor Bottom Status Bar");
            reanchorItem.Click += (s, e) =>
            {
                try
                {
                    EnsureIndicatorVisible();
                    MessageBox.Show("Status bar re-anchored successfully.", "CODESYS MCP", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    CodesysContext.LogToIde("[ERROR] CodesysIdeUiHelper: Re-anchor failed: " + ex.Message);
                }
            };
            menu.Items.Add(reanchorItem);

            return menu;
        }

        private static string BrowseForMirrorFolder(string initialPath = null)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Select folder for bidirectional .st source mirror sync";
                dlg.ShowNewFolderButton = true;
                if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
                {
                    dlg.SelectedPath = initialPath;
                }
                Control owner = FindMainCodesysForm();
                DialogResult res = owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
                return res == DialogResult.OK ? dlg.SelectedPath : null;
            }
        }

        public static string BuildDiagnosticsText()
        {
            int toolCount = 36;
            try
            {
                toolCount = McpServer.RegisteredToolCount;
            }
            catch
            {
            }

            return $"==================================================\r\n" +
                   $" CODESYS MCP Native Server v{GetServerVersion()}\r\n" +
                   $"==================================================\r\n\r\n" +
                   $"Server Status       : {_currentStatus}\r\n" +
                   $"Named Pipe          : \\\\.\\pipe\\{_currentPipeName}\r\n" +
                   $"Registered Tools    : {toolCount} MCP Tools Available\r\n" +
                   $"Total Requests      : {_requestCounter}\r\n" +
                   $"Active Tool         : {_activeTool ?? "None (Idle)"}\r\n" +
                   $"Last Error          : {_lastError ?? "None"}\r\n\r\n" +
                   $"Active Project      : {CodesysContext.PrimaryProject?.path ?? "(No project open)"}\r\n" +
                   $"Online Status       : {(CodesysContext.Online != null ? "Online Engine Active" : "Offline")}\r\n" +
                   $"Host Control        : {_hostStatusStrip?.Name ?? "None"}\r\n" +
                   $"Permanently Visible : {(_hostStatusStrip?.Visible == true ? "Yes" : "No")}\r\n" +
                   $"Timestamp           : {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n";
        }

        public static Form CreateDiagnosticsDialog()
        {
            var form = new Form
            {
                Text = "CODESYS MCP Server Diagnostics",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                StartPosition = FormStartPosition.CenterScreen,
                Size = new Size(480, 360),
                BackColor = Color.White,
                KeyPreview = true
            };

            var tb = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9.5f),
                BackColor = Color.FromArgb(250, 250, 250),
                Text = BuildDiagnosticsText()
            };

            var btnPanel = new Panel { Dock = DockStyle.Bottom, Height = 45, BackColor = Color.FromArgb(240, 240, 240) };
            var btnClose = new Button
            {
                Name = "btnClose",
                Text = "Close",
                DialogResult = DialogResult.OK,
                Size = new Size(85, 28),
                Location = new Point(btnPanel.Width - 100, 8),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            btnClose.Click += (s, e) =>
            {
                try
                {
                    form.Close();
                }
                catch { }
            };
            btnPanel.Controls.Add(btnClose);

            form.Controls.Add(tb);
            form.Controls.Add(btnPanel);
            form.AcceptButton = btnClose;
            form.CancelButton = btnClose;

            form.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    try
                    {
                        form.Close();
                    }
                    catch { }
                }
            };

            return form;
        }

        public static void ShowDiagnosticsDialog()
        {
            var form = CreateDiagnosticsDialog();
            Form owner = null;
            try
            {
                owner = _hostStatusStrip?.FindForm() ?? (_3S.CoDeSys.Core.SystemInstances.FrameForm as Form);
            }
            catch { }

            if (owner != null && !owner.IsDisposed && owner.IsHandleCreated)
            {
                form.Show(owner);
            }
            else
            {
                form.Show();
            }

            try
            {
                form.Activate();
            }
            catch { }
        }

        /// <summary>
        /// Renders only the given WinForms Form to PNG using PrintWindow (with DrawToBitmap fallback),
        /// never capturing external screen regions or non-CODESYS windows.
        /// </summary>
        public static IdeScreenshotResult CaptureFormToPng(Form targetForm, string outputPath = null, int maxWidth = 0)
        {
            if (targetForm == null || targetForm.IsDisposed || !targetForm.IsHandleCreated)
            {
                return new IdeScreenshotResult
                {
                    success = false,
                    message = "No active CODESYS IDE window found (headless mode)."
                };
            }

            string title = targetForm.Text ?? string.Empty;
            if (targetForm.WindowState == FormWindowState.Minimized || targetForm.Width <= 0 || targetForm.Height <= 0)
            {
                return new IdeScreenshotResult
                {
                    success = false,
                    windowTitle = title,
                    message = "CODESYS IDE window is minimized; restore it before capturing screenshot."
                };
            }

            try { targetForm.Refresh(); } catch { }
            try { Application.DoEvents(); } catch { }

            string outPath = !string.IsNullOrWhiteSpace(outputPath)
                ? outputPath.Trim()
                : Path.Combine(Path.GetTempPath(), $"codesys_ide_{DateTime.Now:yyyyMMdd_HHmmss}.png");

            string dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string methodUsed;
            int finalWidth;
            int finalHeight;

            using (var rawBmp = new Bitmap(targetForm.Width, targetForm.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                bool captured = false;
                methodUsed = "PrintWindow_FullContent";
                using (var gfx = Graphics.FromImage(rawBmp))
                {
                    IntPtr hdc = gfx.GetHdc();
                    try
                    {
                        captured = PrintWindow(targetForm.Handle, hdc, PW_RENDERFULLCONTENT);
                        if (!captured)
                        {
                            methodUsed = "PrintWindow";
                            captured = PrintWindow(targetForm.Handle, hdc, 0);
                        }
                    }
                    finally
                    {
                        gfx.ReleaseHdc(hdc);
                    }
                }

                if (!captured)
                {
                    methodUsed = "DrawToBitmap";
                    targetForm.DrawToBitmap(rawBmp, new Rectangle(0, 0, targetForm.Width, targetForm.Height));
                }

                if (maxWidth > 0 && rawBmp.Width > maxWidth)
                {
                    int targetH = Math.Max(1, (int)Math.Round(rawBmp.Height * ((double)maxWidth / rawBmp.Width)));
                    using (var scaled = new Bitmap(maxWidth, targetH, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    {
                        using (var sg = Graphics.FromImage(scaled))
                        {
                            sg.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            sg.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                            sg.DrawImage(rawBmp, 0, 0, maxWidth, targetH);
                        }
                        scaled.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                        finalWidth = scaled.Width;
                        finalHeight = scaled.Height;
                    }
                }
                else
                {
                    rawBmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                    finalWidth = rawBmp.Width;
                    finalHeight = rawBmp.Height;
                }
            }

            long sizeBytes = new FileInfo(outPath).Length;
            return new IdeScreenshotResult
            {
                success = true,
                filePath = outPath,
                width = finalWidth,
                height = finalHeight,
                fileSizeBytes = sizeBytes,
                windowTitle = title,
                captureMethod = methodUsed,
                message = $"Captured CODESYS IDE window '{title}' ({finalWidth}x{finalHeight}, {sizeBytes} bytes) via {methodUsed}."
            };
        }

        public static IdeScreenshotResult CaptureIdeScreenshot(string outputPath = null, int maxWidth = 0)
        {
            return CodesysContext.RunOnPrimaryThread(() =>
                CaptureFormToPng(FindMainCodesysForm() as Form, outputPath, maxWidth));
        }
    }
}
