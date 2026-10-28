using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace CodesysMcp
{
    public class ExportedPouFiles
    {
        public string PouName { get; set; }
        public string BeforeFilePath { get; set; }
        public string AfterFilePath { get; set; }
        public bool IsNew { get; set; }
        public bool IsDeleted { get; set; }
    }

    public static class DiffViewerHelper
    {
        public static string FormatPouAsSt(string pouName, string declaration, string implementation)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// ========================================================");
            sb.AppendLine($"// POU: {pouName}");
            sb.AppendLine($"// SECTION: DECLARATION");
            sb.AppendLine($"// ========================================================");
            if (!string.IsNullOrEmpty(declaration))
            {
                sb.AppendLine(declaration.TrimEnd());
            }
            sb.AppendLine();
            sb.AppendLine($"// ========================================================");
            sb.AppendLine($"// SECTION: IMPLEMENTATION");
            sb.AppendLine($"// ========================================================");
            if (!string.IsNullOrEmpty(implementation))
            {
                sb.AppendLine(implementation.TrimEnd());
            }
            return sb.ToString();
        }

        public static List<ExportedPouFiles> ExportDiffFiles(
            string outputDir,
            Dictionary<string, PouSnapshot> snapshots,
            Func<string, (string decl, string impl)> currentPouReader,
            string specificPou = null)
        {
            var results = new List<ExportedPouFiles>();
            if (string.IsNullOrEmpty(outputDir)) return results;

            string beforeDir = Path.Combine(outputDir, "before");
            string afterDir = Path.Combine(outputDir, "after");

            if (!Directory.Exists(beforeDir)) Directory.CreateDirectory(beforeDir);
            if (!Directory.Exists(afterDir)) Directory.CreateDirectory(afterDir);

            foreach (var kvp in snapshots)
            {
                string pouName = kvp.Key;
                if (!string.IsNullOrEmpty(specificPou) && !string.Equals(pouName, specificPou, StringComparison.OrdinalIgnoreCase))
                    continue;

                var snapshot = kvp.Value;
                string beforeContent;
                string afterContent;
                bool isNew = snapshot.IsNew;
                bool isDeleted = snapshot.IsDeleted;

                if (isNew)
                {
                    beforeContent = $"// POU '{pouName}' was newly created in this session.\n";
                    var curr = currentPouReader != null ? currentPouReader(pouName) : (null, null);
                    afterContent = FormatPouAsSt(pouName, curr.decl, curr.impl);
                }
                else if (isDeleted)
                {
                    beforeContent = FormatPouAsSt(pouName, snapshot.OriginalDeclaration, snapshot.OriginalImplementation);
                    afterContent = $"// POU '{pouName}' was deleted in this session.\n";
                }
                else
                {
                    beforeContent = FormatPouAsSt(pouName, snapshot.OriginalDeclaration, snapshot.OriginalImplementation);
                    var curr = currentPouReader != null ? currentPouReader(pouName) : (null, null);
                    afterContent = FormatPouAsSt(pouName, curr.decl ?? snapshot.OriginalDeclaration, curr.impl ?? snapshot.OriginalImplementation);
                }

                string beforeFile = Path.Combine(beforeDir, pouName + ".st");
                string afterFile = Path.Combine(afterDir, pouName + ".st");

                File.WriteAllText(beforeFile, beforeContent, Encoding.UTF8);
                File.WriteAllText(afterFile, afterContent, Encoding.UTF8);

                results.Add(new ExportedPouFiles
                {
                    PouName = pouName,
                    BeforeFilePath = beforeFile,
                    AfterFilePath = afterFile,
                    IsNew = isNew,
                    IsDeleted = isDeleted
                });
            }

            return results;
        }

        private static string EscapeHtml(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
        }

        public static string GenerateHtmlDiffReport(string outputDir, List<PouDiffItem> diffs)
        {
            if (string.IsNullOrEmpty(outputDir) || diffs == null) return null;

            if (!Directory.Exists(outputDir)) Directory.CreateDirectory(outputDir);
            string htmlFile = Path.Combine(outputDir, "diff_report.html");

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"zh-CN\">");
            sb.AppendLine("<head>");
            sb.AppendLine("  <meta charset=\"UTF-8\">");
            sb.AppendLine("  <title>CODESYS MCP - 差异对比报告 (Diff Preview)</title>");
            sb.AppendLine("  <style>");
            sb.AppendLine("    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background: #1e1e1e; color: #d4d4d4; margin: 0; padding: 24px; }");
            sb.AppendLine("    .header { padding-bottom: 16px; border-bottom: 1px solid #333; margin-bottom: 24px; }");
            sb.AppendLine("    .header h1 { margin: 0 0 8px 0; color: #61afef; font-size: 24px; }");
            sb.AppendLine("    .summary { display: flex; gap: 16px; margin-bottom: 20px; }");
            sb.AppendLine("    .badge { padding: 6px 12px; border-radius: 4px; font-weight: bold; font-size: 14px; }");
            sb.AppendLine("    .badge-info { background: #2b5278; color: #87c3ff; }");
            sb.AppendLine("    .card { background: #252526; border: 1px solid #3c3c3c; border-radius: 6px; margin-bottom: 20px; overflow: hidden; }");
            sb.AppendLine("    .card-header { background: #2d2d2d; padding: 12px 16px; font-weight: 600; display: flex; justify-content: space-between; align-items: center; border-bottom: 1px solid #3c3c3c; }");
            sb.AppendLine("    .card-title { color: #e5c07b; font-size: 16px; }");
            sb.AppendLine("    .card-body { padding: 16px; overflow-x: auto; font-family: 'Consolas', 'Courier New', monospace; font-size: 13px; line-height: 1.5; }");
            sb.AppendLine("    .diff-line { white-space: pre-wrap; word-break: break-all; margin: 0; padding: 2px 8px; }");
            sb.AppendLine("    .diff-add { background: rgba(46, 160, 67, 0.25); color: #7ee787; border-left: 4px solid #2ea043; }");
            sb.AppendLine("    .diff-del { background: rgba(248, 81, 73, 0.25); color: #ffa198; border-left: 4px solid #f85149; }");
            sb.AppendLine("    .diff-hunk { background: #1f2428; color: #79c0ff; font-weight: bold; padding: 4px 8px; margin-top: 4px; }");
            sb.AppendLine("    .diff-ctx { color: #8b949e; }");
            sb.AppendLine("  </style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine("  <div class=\"header\">");
            sb.AppendLine("    <h1>CODESYS 自动化修改差异对比报告</h1>");
            sb.AppendLine($"    <div class=\"summary\"><span class=\"badge badge-info\">共修改 {diffs.Count} 个 POU</span></div>");
            sb.AppendLine("  </div>");

            foreach (var item in diffs)
            {
                sb.AppendLine("  <div class=\"card\">");
                sb.AppendLine("    <div class=\"card-header\">");
                sb.AppendLine($"      <span class=\"card-title\">POU: {EscapeHtml(item.name)} ({EscapeHtml(item.status)})</span>");
                sb.AppendLine("    </div>");
                sb.AppendLine("    <div class=\"card-body\">");

                string diffText = item.unifiedDiff;
                if (string.IsNullOrWhiteSpace(diffText))
                {
                    sb.AppendLine("      <div class=\"diff-ctx\">（无文本差异或全部一致）</div>");
                }
                else
                {
                    using (var reader = new StringReader(diffText))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            string encoded = EscapeHtml(line);
                            if (line.StartsWith("+++") || line.StartsWith("---"))
                            {
                                sb.AppendLine($"      <div class=\"diff-line diff-ctx\">{encoded}</div>");
                            }
                            else if (line.StartsWith("@@"))
                            {
                                sb.AppendLine($"      <div class=\"diff-line diff-hunk\">{encoded}</div>");
                            }
                            else if (line.StartsWith("+"))
                            {
                                sb.AppendLine($"      <div class=\"diff-line diff-add\">{encoded}</div>");
                            }
                            else if (line.StartsWith("-"))
                            {
                                sb.AppendLine($"      <div class=\"diff-line diff-del\">{encoded}</div>");
                            }
                            else
                            {
                                sb.AppendLine($"      <div class=\"diff-line diff-ctx\">{encoded}</div>");
                            }
                        }
                    }
                }

                sb.AppendLine("    </div>");
                sb.AppendLine("  </div>");
            }

            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            File.WriteAllText(htmlFile, sb.ToString(), Encoding.UTF8);
            return htmlFile;
        }

        public static string FindVsCodeCommand()
        {
            var candidates = new List<string>();

            // Check PATH
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string c1 = Path.Combine(dir.Trim(), "code.cmd");
                string c2 = Path.Combine(dir.Trim(), "code.exe");
                if (File.Exists(c1)) return c1;
                if (File.Exists(c2)) return c2;
            }

            // Check standard User and System locations
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(localAppData, @"Programs\Microsoft VS Code\bin\code.cmd"));
            candidates.Add(Path.Combine(localAppData, @"Programs\Microsoft VS Code\Code.exe"));

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            candidates.Add(Path.Combine(programFiles, @"Microsoft VS Code\bin\code.cmd"));
            candidates.Add(Path.Combine(programFiles, @"Microsoft VS Code\Code.exe"));

            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }

            return null;
        }

        public static bool LaunchVsCodeDiff(string beforeFile, string afterFile, out string commandUsed)
        {
            commandUsed = null;
            string codeCmd = FindVsCodeCommand();
            if (string.IsNullOrEmpty(codeCmd)) return false;

            try
            {
                string args = $"--diff \"{beforeFile}\" \"{afterFile}\"";
                var psi = new ProcessStartInfo
                {
                    FileName = codeCmd,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Process.Start(psi);
                commandUsed = $"code --diff \"{beforeFile}\" \"{afterFile}\"";
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool LaunchBrowserReport(string reportHtml)
        {
            if (string.IsNullOrEmpty(reportHtml) || !File.Exists(reportHtml)) return false;
            try
            {
                Process.Start(new ProcessStartInfo(reportHtml)
                {
                    UseShellExecute = true
                });
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
