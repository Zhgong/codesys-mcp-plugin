using System;
using System.Collections.Generic;
using System.Drawing;
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
    }

    public static class CodesysIdeUiHelper
    {
        private static readonly object _syncLock = new object();
        private static ToolStripStatusLabel _statusBadge = null;
        private static StatusStrip _hostStatusStrip = null;
        private static McpServerStatus _currentStatus = McpServerStatus.Offline;
        private static string _currentPipeName = "codesys_mcp_pipe";
        private static string _activeTool = null;
        private static string _lastError = null;
        private static int _requestCounter = 0;

        public static McpServerStatus CurrentStatus => _currentStatus;
        public static string CurrentPipeName => _currentPipeName;
        public static int TotalRequests => _requestCounter;

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

                    // Locate or create StatusStrip
                    StatusStrip ss = FindStatusStrip(mainForm);
                    if (ss == null)
                    {
                        CodesysContext.LogToIde("[INFO] CodesysIdeUiHelper: StatusStrip not found in MainForm, creating docked MCP status panel...");
                        ss = new StatusStrip
                        {
                            Name = "CodesysMcpStatusStrip",
                            Dock = DockStyle.Bottom,
                            Height = 22,
                            BackColor = Color.FromArgb(240, 240, 240),
                            ShowItemToolTips = true
                        };
                        mainForm.Controls.Add(ss);
                        ss.BringToFront();
                    }

                    _hostStatusStrip = ss;
                    ss.ShowItemToolTips = true;

                    // Remove existing badge if hot reloaded
                    if (_statusBadge != null)
                    {
                        try { ss.Items.Remove(_statusBadge); } catch { }
                        _statusBadge = null;
                    }

                    foreach (ToolStripItem it in ss.Items)
                    {
                        if (it.Name == "CodesysMcpStatusBadge")
                        {
                            try { ss.Items.Remove(it); } catch { }
                            break;
                        }
                    }

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

                    ss.Items.Add(_statusBadge);
                    _currentStatus = McpServerStatus.Online;
                    CodesysContext.LogToIde($"[SUCCESS] CodesysIdeUiHelper: MCP visual status badge installed on '{ss.Name}'.");
                }
                catch (Exception ex)
                {
                    CodesysContext.LogToIde($"[WARNING] CodesysIdeUiHelper.InstallStatusIndicator warning: {ex.Message}");
                    _currentStatus = McpServerStatus.Online;
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
                   (!string.IsNullOrEmpty(_lastError) ? $"Last Error: {_lastError}" : "State: Healthy");
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
                    });
                }
                catch { }
            }
        }

        public static UiIndicatorInfo GetIndicatorInfo()
        {
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
                hostControlName = _hostStatusStrip?.Name
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

        private static StatusStrip FindStatusStrip(Control parent)
        {
            if (parent == null) return null;
            if (parent is StatusStrip ss && !ss.IsDisposed) return ss;

            foreach (Control child in parent.Controls)
            {
                if (child is StatusStrip s && !s.IsDisposed) return s;
                if (child.HasChildren)
                {
                    var found = FindStatusStrip(child);
                    if (found != null) return found;
                }
            }
            return null;
        }
    }
}
