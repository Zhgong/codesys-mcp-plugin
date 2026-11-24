using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CodesysMcp
{
    public static class DiffHelper
    {
        public static string GenerateUnifiedDiff(
            string originalText,
            string modifiedText,
            string originalHeader = "original",
            string modifiedHeader = "modified")
        {
            string[] origLines = SplitLines(originalText);
            string[] modLines = SplitLines(modifiedText);

            int[,] lcs = ComputeLcsTable(origLines, modLines);
            var diffEntries = BacktrackDiff(lcs, origLines, modLines);

            bool hasDiff = false;
            foreach (var entry in diffEntries)
            {
                if (entry.Type != DiffType.Equal)
                {
                    hasDiff = true;
                    break;
                }
            }

            if (!hasDiff) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine($"--- {originalHeader}");
            sb.AppendLine($"+++ {modifiedHeader}");

            int origLine = 1;
            int modLine = 1;
            int i = 0;

            while (i < diffEntries.Count)
            {
                if (diffEntries[i].Type == DiffType.Equal)
                {
                    origLine++;
                    modLine++;
                    i++;
                    continue;
                }

                int hunkStartOrig = Math.Max(1, origLine - 2);
                int hunkStartMod = Math.Max(1, modLine - 2);

                int hunkOrigCount = 0;
                int hunkModCount = 0;
                var hunkLines = new List<string>();

                int contextBack = origLine - hunkStartOrig;
                for (int c = i - contextBack; c < i; c++)
                {
                    if (c >= 0 && c < diffEntries.Count && diffEntries[c].Type == DiffType.Equal)
                    {
                        hunkLines.Add(" " + diffEntries[c].Text);
                        hunkOrigCount++;
                        hunkModCount++;
                    }
                }

                while (i < diffEntries.Count && (diffEntries[i].Type != DiffType.Equal || HasUpcomingDiff(diffEntries, i, 2)))
                {
                    var entry = diffEntries[i];
                    if (entry.Type == DiffType.Delete)
                    {
                        hunkLines.Add("-" + entry.Text);
                        hunkOrigCount++;
                        origLine++;
                    }
                    else if (entry.Type == DiffType.Add)
                    {
                        hunkLines.Add("+" + entry.Text);
                        hunkModCount++;
                        modLine++;
                    }
                    else
                    {
                        hunkLines.Add(" " + entry.Text);
                        hunkOrigCount++;
                        hunkModCount++;
                        origLine++;
                        modLine++;
                    }
                    i++;
                }

                int trailingContext = 0;
                while (i < diffEntries.Count && diffEntries[i].Type == DiffType.Equal && trailingContext < 2)
                {
                    hunkLines.Add(" " + diffEntries[i].Text);
                    hunkOrigCount++;
                    hunkModCount++;
                    origLine++;
                    modLine++;
                    i++;
                    trailingContext++;
                }

                sb.AppendLine($"@@ -{hunkStartOrig},{hunkOrigCount} +{hunkStartMod},{hunkModCount} @@");
                foreach (var line in hunkLines)
                {
                    sb.AppendLine(line);
                }
            }

            return sb.ToString().TrimEnd();
        }

        private static bool HasUpcomingDiff(List<DiffEntry> entries, int currentIndex, int lookahead)
        {
            for (int k = currentIndex; k <= Math.Min(entries.Count - 1, currentIndex + lookahead); k++)
            {
                if (entries[k].Type != DiffType.Equal) return true;
            }
            return false;
        }

        private static string[] SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return new string[0];
            var lines = new List<string>();
            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lines.Add(line);
                }
            }
            return lines.ToArray();
        }

        private static int[,] ComputeLcsTable(string[] a, string[] b)
        {
            int m = a.Length;
            int n = b.Length;
            int[,] table = new int[m + 1, n + 1];

            for (int i = 0; i < m; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    if (a[i] == b[j])
                    {
                        table[i + 1, j + 1] = table[i, j] + 1;
                    }
                    else
                    {
                        table[i + 1, j + 1] = Math.Max(table[i + 1, j], table[i, j + 1]);
                    }
                }
            }
            return table;
        }

        private enum DiffType { Equal, Add, Delete }

        private class DiffEntry
        {
            public DiffType Type;
            public string Text;
        }

        private static List<DiffEntry> BacktrackDiff(int[,] lcs, string[] a, string[] b)
        {
            var entries = new List<DiffEntry>();
            int i = a.Length;
            int j = b.Length;

            while (i > 0 || j > 0)
            {
                if (i > 0 && j > 0 && a[i - 1] == b[j - 1])
                {
                    entries.Add(new DiffEntry { Type = DiffType.Equal, Text = a[i - 1] });
                    i--;
                    j--;
                }
                else if (j > 0 && (i == 0 || lcs[i, j - 1] >= lcs[i - 1, j]))
                {
                    entries.Add(new DiffEntry { Type = DiffType.Add, Text = b[j - 1] });
                    j--;
                }
                else if (i > 0 && (j == 0 || lcs[i, j - 1] < lcs[i - 1, j]))
                {
                    entries.Add(new DiffEntry { Type = DiffType.Delete, Text = a[i - 1] });
                    i--;
                }
            }

            entries.Reverse();
            return entries;
        }
    }
}
