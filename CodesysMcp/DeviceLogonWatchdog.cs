using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CodesysMcp
{
    public sealed class DeviceLogonWatchdog : IDisposable
    {
        private readonly int _targetThreadId;
        private readonly string _username;
        private readonly string _password;
        private readonly bool _confirmGenericPrompts;
        private readonly Thread _watchThread;
        private volatile bool _stopRequested;
        private bool _dialogHandled;

        public bool DialogHandled => _dialogHandled;

        private delegate bool EnumThreadWndProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumThreadWindows(int dwThreadId, EnumThreadWndProc lpfn, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumThreadWndProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetDlgCtrlID(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        public static extern int GetCurrentThreadId();

        private const uint WM_SETTEXT = 0x000C;
        private const uint WM_COMMAND = 0x0111;
        private const uint BM_CLICK = 0x00F5;
        private const int IDOK = 1;
        private const int IDCANCEL = 2;
        private const int IDYES = 6;

        public DeviceLogonWatchdog(int targetThreadId, string username, string password, bool confirmGenericPrompts = false)
        {
            _targetThreadId = targetThreadId;
            _username = username ?? "admin";
            _password = password ?? string.Empty;
            _confirmGenericPrompts = confirmGenericPrompts;

            _watchThread = new Thread(WatchLoop)
            {
                IsBackground = true,
                Name = confirmGenericPrompts ? "CodesysPromptConfirmWatchdog" : "CodesysDeviceLogonWatchdog"
            };
            _watchThread.Start();
        }

        public static bool IsDeviceLogonTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return false;
            return title.IndexOf("Device User Logon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   title.IndexOf("User Logon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (title.IndexOf("Logon", StringComparison.OrdinalIgnoreCase) >= 0 && title.IndexOf("Device", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static bool IsConfirmPromptDialog(string className, string title)
        {
            return string.Equals(className, "#32770", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals((title ?? string.Empty).Trim(), "CODESYS", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsConfirmButtonCaption(string caption)
        {
            if (string.IsNullOrWhiteSpace(caption)) return false;
            string s = caption.Replace("&", string.Empty).Trim();
            if (s.Length >= 4 && s[s.Length - 1] == ')' && s[s.Length - 3] == '(' && char.IsLetter(s[s.Length - 2]))
            {
                s = s.Substring(0, s.Length - 3).Trim();
            }
            return string.Equals(s, "确定", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(s, "OK", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(s, "是", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(s, "YES", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(s, "继续", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(s, "CONTINUE", StringComparison.OrdinalIgnoreCase);
        }

        private void WatchLoop()
        {
            int elapsedMs = 0;
            const int pollIntervalMs = 150;
            int maxWaitMs = _confirmGenericPrompts ? 110000 : 15000;

            while (!_stopRequested && elapsedMs < maxWaitMs)
            {
                bool handledInPass = false;
                try
                {
                    EnumThreadWindows(_targetThreadId, (hWnd, lParam) =>
                    {
                        if (!IsWindowVisible(hWnd)) return true;

                        var sbTitle = new StringBuilder(256);
                        GetWindowText(hWnd, sbTitle, 256);
                        string title = sbTitle.ToString();

                        if (IsDeviceLogonTitle(title))
                        {
                            HandleDialog(hWnd, title);
                            handledInPass = true;
                            return false;
                        }

                        if (_confirmGenericPrompts)
                        {
                            var sbClass = new StringBuilder(64);
                            GetClassName(hWnd, sbClass, 64);
                            if (IsConfirmPromptDialog(sbClass.ToString(), title))
                            {
                                HandleConfirmDialog(hWnd, title);
                                handledInPass = true;
                                return false;
                            }
                        }
                        return true;
                    }, IntPtr.Zero);

                    if (_dialogHandled && !_confirmGenericPrompts) break;
                }
                catch
                {
                }

                int sleepMs = handledInPass ? 500 : pollIntervalMs;
                Thread.Sleep(sleepMs);
                elapsedMs += sleepMs;
            }
        }

        private void HandleConfirmDialog(IntPtr hWnd, string title)
        {
            _dialogHandled = true;
            try
            {
                var staticTexts = new List<string>();
                var buttons = new List<string>();
                IntPtr hBtn = IntPtr.Zero;
                string chosenDesc = "Fallback(WM_COMMAND IDYES+IDOK)";

                EnumChildWindows(hWnd, (ch, lParam) =>
                {
                    var sbClass = new StringBuilder(64);
                    GetClassName(ch, sbClass, 64);
                    string cls = sbClass.ToString();

                    var sbText = new StringBuilder(512);
                    GetWindowText(ch, sbText, 512);
                    string text = (sbText.ToString() ?? string.Empty).Trim();

                    if (string.Equals(cls, "Static", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrEmpty(text))
                        {
                            staticTexts.Add(text.Replace("\r", " ").Replace("\n", " "));
                        }
                    }
                    else if (string.Equals(cls, "Button", StringComparison.OrdinalIgnoreCase))
                    {
                        int id = GetDlgCtrlID(ch);
                        buttons.Add($"ID={id} '{text}'");
                        if (hBtn == IntPtr.Zero && IsWindowVisible(ch) &&
                            (IsConfirmButtonCaption(text) || id == IDYES || id == IDOK))
                        {
                            hBtn = ch;
                            chosenDesc = $"ID={id} '{text}'";
                        }
                    }
                    return true;
                }, IntPtr.Zero);

                string staticSummary = string.Join(" | ", staticTexts);
                if (staticSummary.Length > 200)
                {
                    staticSummary = staticSummary.Substring(0, 200) + "...";
                }

                CodesysContext.WriteToSessionLog(
                    $"[WATCHDOG] Auto-confirming CODESYS modal dialog '{title}' on UI Thread {_targetThreadId}. " +
                    $"Static='{staticSummary}' Buttons=[{string.Join(", ", buttons)}] Chosen={chosenDesc}");

                if (hBtn != IntPtr.Zero)
                {
                    PostMessage(hBtn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                }
                else
                {
                    PostMessage(hWnd, WM_COMMAND, new IntPtr(IDYES), IntPtr.Zero);
                    PostMessage(hWnd, WM_COMMAND, new IntPtr(IDOK), IntPtr.Zero);
                }
            }
            catch (Exception ex)
            {
                CodesysContext.WriteToSessionLog($"[WATCHDOG] Error auto-confirming dialog '{title}': {ex.Message}");
            }
        }

        private void HandleDialog(IntPtr hWnd, string title)
        {
            _dialogHandled = true;
            CodesysContext.WriteToSessionLog($"[WATCHDOG] Detected modal dialog '{title}' on UI Thread {_targetThreadId}. Intercepting...");

            try
            {
                IntPtr hUser = FindWindowEx(hWnd, IntPtr.Zero, "Edit", null);
                IntPtr hPass = hUser != IntPtr.Zero ? FindWindowEx(hWnd, hUser, "Edit", null) : IntPtr.Zero;

                if (hUser != IntPtr.Zero && !string.IsNullOrEmpty(_username))
                {
                    SendMessage(hUser, WM_SETTEXT, IntPtr.Zero, _username);
                }

                if (hPass != IntPtr.Zero && !string.IsNullOrEmpty(_password))
                {
                    SendMessage(hPass, WM_SETTEXT, IntPtr.Zero, _password);
                }

                IntPtr hOk = FindWindowEx(hWnd, IntPtr.Zero, "Button", "OK");
                if (hOk != IntPtr.Zero)
                {
                    PostMessage(hOk, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                }
                else
                {
                    PostMessage(hWnd, WM_COMMAND, new IntPtr(IDOK), IntPtr.Zero);
                }

                CodesysContext.WriteToSessionLog($"[WATCHDOG] Auto-submitted credentials (user='{_username}') to dialog '{title}'.");

                Thread.Sleep(1000);
                if (IsWindowVisible(hWnd))
                {
                    CodesysContext.WriteToSessionLog($"[WATCHDOG] Dialog '{title}' did not close after submit. Dismissing via Cancel to unfreeze primary thread.");
                    IntPtr hCancel = FindWindowEx(hWnd, IntPtr.Zero, "Button", "Cancel");
                    if (hCancel != IntPtr.Zero)
                    {
                        PostMessage(hCancel, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    }
                    else
                    {
                        PostMessage(hWnd, WM_COMMAND, new IntPtr(IDCANCEL), IntPtr.Zero);
                    }
                }
            }
            catch (Exception ex)
            {
                CodesysContext.WriteToSessionLog($"[WATCHDOG] Error handling dialog '{title}': {ex.Message}");
            }
        }

        public void Dispose()
        {
            _stopRequested = true;
            try
            {
                if (_watchThread != null && _watchThread.IsAlive)
                {
                    _watchThread.Join(200);
                }
            }
            catch { }
        }
    }
}
