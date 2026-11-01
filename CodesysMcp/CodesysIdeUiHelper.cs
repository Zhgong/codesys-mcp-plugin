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
    }

    public static class CodesysIdeUiHelper
    {
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
                    CodesysContext.LogToIde($"[SUCCESS] CodesysIdeUiHelper: MCP visual status badge installed on persistent strip '{ss.Name}'.");
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
                    _statusBadge.Text = "● MCP: Online";
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

        private static string BuildTooltipText()
        {
            return $"CODESYS MCP Server v1.1.0\n" +
                   $"Status: {_currentStatus}\n" +
                   $"Pipe: \\\\.\\pipe\\{_currentPipeName}\n" +
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
                hasContextMenu = _contextMenu != null && !_contextMenu.IsDisposed
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
                    string scriptDir = AppDomain.CurrentDomain.BaseDirectory;
                    string logFile = Path.Combine(scriptDir, "session_debug.log");
                    if (!File.Exists(logFile))
                    {
                        string localLog = @"C:\Users\vboxuser\Desktop\Repos\codesys-plugin\session_debug.log";
                        if (File.Exists(localLog)) logFile = localLog;
                    }
                    if (File.Exists(logFile))
                    {
                        System.Diagnostics.Process.Start("notepad.exe", logFile);
                    }
                    else
                    {
                        MessageBox.Show("Session log file not found at:\n" + logFile, "CODESYS MCP", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                catch (Exception ex)
                {
                    CodesysContext.LogToIde("[ERROR] CodesysIdeUiHelper: Open log failed: " + ex.Message);
                }
            };
            menu.Items.Add(logItem);

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

        public static void ShowDiagnosticsDialog()
        {
            var form = new Form
            {
                Text = "CODESYS MCP Server Diagnostics",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                StartPosition = FormStartPosition.CenterScreen,
                Size = new Size(480, 360),
                BackColor = Color.White
            };

            var tb = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9.5f),
                BackColor = Color.FromArgb(250, 250, 250),
                Text = $"==================================================\r\n" +
                       $" CODESYS MCP Native Server v1.1.0\r\n" +
                       $"==================================================\r\n\r\n" +
                       $"Server Status       : {_currentStatus}\r\n" +
                       $"Named Pipe          : \\\\.\\pipe\\{_currentPipeName}\r\n" +
                       $"Registered Tools    : 30 MCP Tools Available\r\n" +
                       $"Total Requests      : {_requestCounter}\r\n" +
                       $"Active Tool         : {_activeTool ?? "None (Idle)"}\r\n" +
                       $"Last Error          : {_lastError ?? "None"}\r\n\r\n" +
                       $"Active Project      : {CodesysContext.PrimaryProject?.path ?? "(No project open)"}\r\n" +
                       $"Online Status       : {(CodesysContext.Online != null ? "Online Engine Active" : "Offline")}\r\n" +
                       $"Host Control        : {_hostStatusStrip?.Name ?? "None"}\r\n" +
                       $"Permanently Visible : {(_hostStatusStrip?.Visible == true ? "Yes" : "No")}\r\n" +
                       $"Timestamp           : {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n"
            };

            var btnPanel = new Panel { Dock = DockStyle.Bottom, Height = 45, BackColor = Color.FromArgb(240, 240, 240) };
            var btnClose = new Button
            {
                Text = "Close",
                DialogResult = DialogResult.OK,
                Size = new Size(85, 28),
                Location = new Point(btnPanel.Width - 100, 8),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            btnPanel.Controls.Add(btnClose);

            form.Controls.Add(tb);
            form.Controls.Add(btnPanel);
            form.AcceptButton = btnClose;
            form.Show();
        }
    }
}
