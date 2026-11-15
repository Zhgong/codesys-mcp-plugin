using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using CodesysMcp;

namespace CodesysMcpTests
{
    public class SourceMirrorTests
    {
        private static string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "CodesysMcpMirrorTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void SafeDeleteDir(string dir)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch { }
        }

        [TestMethod]
        public void TestFormatStContentIncludesYamlMetadataHeader()
        {
            var item = new MirrorSourceItem
            {
                Name = "FB_PressureController",
                Type = "FunctionBlock",
                RelativePath = "Application/POUs/FB_PressureController",
                Declaration = "FUNCTION_BLOCK FB_PressureController\r\nVAR_INPUT\r\n    rTarget : REAL;\r\nEND_VAR",
                Implementation = "rOut := rTarget;"
            };

            string formatted = SourceMirrorHelper.FormatStContent(item);
            Assert.Contains("(*\n---\nname: FB_PressureController\ntype: FunctionBlock\npath: Application/POUs/FB_PressureController\n---\n*)", formatted);
            Assert.Contains("FUNCTION_BLOCK FB_PressureController\nVAR_INPUT", formatted);
            Assert.Contains("rOut := rTarget;", formatted);
            Assert.IsFalse(formatted.Contains("\r\n"), "Line endings should be normalized to LF for deterministic Git diffs");
        }

        [TestMethod]
        public void TestSyncCreatesNewFilesAndPreservesGitFiles()
        {
            string tempDir = CreateTempDir();
            try
            {
                string gitDir = Path.Combine(tempDir, ".git");
                Directory.CreateDirectory(gitDir);
                string gitHead = Path.Combine(gitDir, "HEAD");
                File.WriteAllText(gitHead, "ref: refs/heads/main\n");
                string gitIgnore = Path.Combine(tempDir, ".gitignore");
                File.WriteAllText(gitIgnore, "*.tmp\n");

                var items = new List<MirrorSourceItem>
                {
                    new MirrorSourceItem
                    {
                        Name = "PLC_PRG",
                        Type = "Program",
                        RelativePath = "Application/PLC_PRG",
                        Declaration = "PROGRAM PLC_PRG\nVAR\nEND_VAR",
                        Implementation = "nCount := nCount + 1;"
                    },
                    new MirrorSourceItem
                    {
                        Name = "GVL_IO",
                        Type = "GVL",
                        RelativePath = "Application/GVLs/GVL_IO",
                        Declaration = "VAR_GLOBAL\n    bStart : BOOL;\nEND_VAR",
                        Implementation = ""
                    }
                };

                var res = SourceMirrorHelper.SyncAll(tempDir, items);
                Assert.AreEqual(2, res.created);
                Assert.AreEqual(0, res.updated);
                Assert.AreEqual(0, res.deleted);
                Assert.AreEqual(0, res.unchanged);

                Assert.IsTrue(File.Exists(Path.Combine(tempDir, "Application", "PLC_PRG.st")));
                Assert.IsTrue(File.Exists(Path.Combine(tempDir, "Application", "GVLs", "GVL_IO.st")));
                Assert.IsTrue(File.Exists(gitHead), ".git/HEAD must be preserved");
                Assert.IsTrue(File.Exists(gitIgnore), ".gitignore must be preserved");
            }
            finally
            {
                SafeDeleteDir(tempDir);
            }
        }

        [TestMethod]
        public void TestContentDiffGuardSkipsUnchangedFiles()
        {
            string tempDir = CreateTempDir();
            try
            {
                var items = new List<MirrorSourceItem>
                {
                    new MirrorSourceItem
                    {
                        Name = "PLC_PRG",
                        Type = "Program",
                        RelativePath = "Application/PLC_PRG",
                        Declaration = "PROGRAM PLC_PRG\nVAR\nEND_VAR",
                        Implementation = "nCount := 1;"
                    }
                };

                var first = SourceMirrorHelper.SyncAll(tempDir, items);
                Assert.AreEqual(1, first.created);

                string targetPath = Path.Combine(tempDir, "Application", "PLC_PRG.st");
                DateTime oldTime = DateTime.UtcNow.AddMinutes(-10);
                File.SetLastWriteTimeUtc(targetPath, oldTime);
                DateTime recordedTime = File.GetLastWriteTimeUtc(targetPath);

                var second = SourceMirrorHelper.SyncAll(tempDir, items);
                Assert.AreEqual(0, second.created);
                Assert.AreEqual(0, second.updated);
                Assert.AreEqual(0, second.deleted);
                Assert.AreEqual(1, second.unchanged);
                Assert.AreEqual(0, second.changedFiles.Count);
                Assert.AreEqual(recordedTime, File.GetLastWriteTimeUtc(targetPath), "Content-Diff Guard must not touch unchanged file timestamp");
            }
            finally
            {
                SafeDeleteDir(tempDir);
            }
        }

        [TestMethod]
        public void TestSyncUpdatesOnlyModifiedFile()
        {
            string tempDir = CreateTempDir();
            try
            {
                var item1 = new MirrorSourceItem
                {
                    Name = "POU_A",
                    Type = "Program",
                    RelativePath = "Application/POU_A",
                    Declaration = "PROGRAM POU_A\nVAR\nEND_VAR",
                    Implementation = "a := 1;"
                };
                var item2 = new MirrorSourceItem
                {
                    Name = "POU_B",
                    Type = "Program",
                    RelativePath = "Application/POU_B",
                    Declaration = "PROGRAM POU_B\nVAR\nEND_VAR",
                    Implementation = "b := 2;"
                };

                SourceMirrorHelper.SyncAll(tempDir, new[] { item1, item2 });

                string pathA = Path.Combine(tempDir, "Application", "POU_A.st");
                string pathB = Path.Combine(tempDir, "Application", "POU_B.st");
                DateTime oldTime = DateTime.UtcNow.AddMinutes(-5);
                File.SetLastWriteTimeUtc(pathA, oldTime);
                File.SetLastWriteTimeUtc(pathB, oldTime);
                DateTime recordedA = File.GetLastWriteTimeUtc(pathA);

                item2.Implementation = "b := 99;";
                var res = SourceMirrorHelper.SyncAll(tempDir, new[] { item1, item2 });

                Assert.AreEqual(0, res.created);
                Assert.AreEqual(1, res.updated);
                Assert.AreEqual(0, res.deleted);
                Assert.AreEqual(1, res.unchanged);
                Assert.AreEqual(recordedA, File.GetLastWriteTimeUtc(pathA), "Unchanged POU_A timestamp must remain untouched");
                Assert.Contains("b := 99;", File.ReadAllText(pathB));
            }
            finally
            {
                SafeDeleteDir(tempDir);
            }
        }

        [TestMethod]
        public void TestSyncDeletesOrphanedStFilesOnly()
        {
            string tempDir = CreateTempDir();
            try
            {
                var item1 = new MirrorSourceItem
                {
                    Name = "POU_A",
                    Type = "Program",
                    RelativePath = "Application/POU_A",
                    Declaration = "PROGRAM POU_A\nVAR\nEND_VAR",
                    Implementation = "a := 1;"
                };
                var item2 = new MirrorSourceItem
                {
                    Name = "POU_Old",
                    Type = "Program",
                    RelativePath = "Application/Legacy/POU_Old",
                    Declaration = "PROGRAM POU_Old\nVAR\nEND_VAR",
                    Implementation = "x := 0;"
                };

                SourceMirrorHelper.SyncAll(tempDir, new[] { item1, item2 });
                string readmePath = Path.Combine(tempDir, "README.md");
                File.WriteAllText(readmePath, "# Mirror Repo\n");

                var res = SourceMirrorHelper.SyncAll(tempDir, new[] { item1 });
                Assert.AreEqual(0, res.created);
                Assert.AreEqual(0, res.updated);
                Assert.AreEqual(1, res.deleted);
                Assert.AreEqual(1, res.unchanged);

                Assert.IsFalse(File.Exists(Path.Combine(tempDir, "Application", "Legacy", "POU_Old.st")));
                Assert.IsFalse(Directory.Exists(Path.Combine(tempDir, "Application", "Legacy")), "Empty orphan folder should be removed");
                Assert.IsTrue(File.Exists(readmePath), "Non-.st files must be preserved");
            }
            finally
            {
                SafeDeleteDir(tempDir);
            }
        }

        [TestMethod]
        public void TestWriteThroughSingleCreateUpdateAndDelete()
        {
            string tempDir = CreateTempDir();
            try
            {
                var item = new MirrorSourceItem
                {
                    Name = "FB_Single",
                    Type = "FunctionBlock",
                    RelativePath = "Application/POUs/FB_Single",
                    Declaration = "FUNCTION_BLOCK FB_Single\nVAR\nEND_VAR",
                    Implementation = "x := 1;"
                };

                Assert.AreEqual(MirrorWriteOutcome.Created, SourceMirrorHelper.SyncSingleNode(tempDir, item));
                Assert.AreEqual(MirrorWriteOutcome.Unchanged, SourceMirrorHelper.SyncSingleNode(tempDir, item));

                item.Implementation = "x := 2;";
                Assert.AreEqual(MirrorWriteOutcome.Updated, SourceMirrorHelper.SyncSingleNode(tempDir, item));

                string stPath = Path.Combine(tempDir, "Application", "POUs", "FB_Single.st");
                Assert.Contains("x := 2;", File.ReadAllText(stPath));

                Assert.IsTrue(SourceMirrorHelper.DeleteSingleNode(tempDir, "Application/POUs/FB_Single"));
                Assert.IsFalse(File.Exists(stPath));
                Assert.IsFalse(SourceMirrorHelper.DeleteSingleNode(tempDir, "Application/POUs/FB_Single"));
            }
            finally
            {
                SafeDeleteDir(tempDir);
            }
        }

        [TestMethod]
        public void TestDynamicMirrorBindingLifecycle()
        {
            string tempDir = CreateTempDir();
            try
            {
                SourceMirrorHelper.ClearMirrorDirectory();
                Assert.IsFalse(SourceMirrorHelper.HasActiveMirror);

                SourceMirrorHelper.SetMirrorDirectory(tempDir);
                Assert.IsTrue(SourceMirrorHelper.HasActiveMirror);
                Assert.AreEqual(Path.GetFullPath(tempDir), SourceMirrorHelper.ActiveMirrorDirectory);

                SourceMirrorHelper.ClearMirrorDirectory();
                Assert.IsFalse(SourceMirrorHelper.HasActiveMirror);
            }
            finally
            {
                SourceMirrorHelper.ResetToEnvironmentDefault();
                SafeDeleteDir(tempDir);
            }
        }

        [TestMethod]
        public void TestPathTraversalGuardRejectsEscapingMirrorRoot()
        {
            string tempDir = CreateTempDir();
            try
            {
                bool threw = false;
                try
                {
                    SourceMirrorHelper.ResolveSafeStPath(tempDir, "../../outside/evil");
                }
                catch (ArgumentException)
                {
                    threw = true;
                }
                Assert.IsTrue(threw, "Path traversal outside mirrorRoot must throw ArgumentException");
            }
            finally
            {
                SafeDeleteDir(tempDir);
            }
        }

        [TestMethod]
        public void TestParseStContentRoundtripFunctionBlock()
        {
            var original = new MirrorSourceItem
            {
                Name = "FB_CalibrateForceSensor",
                Type = "FunctionBlock",
                RelativePath = "POUs/Force_sensor/Analog/FB_CalibrateForceSensor",
                Declaration = "FUNCTION_BLOCK FB_CalibrateForceSensor\nVAR_INPUT\n    xEnable : BOOL;\nEND_VAR",
                Implementation = "xActive := xEnable;"
            };

            string text = SourceMirrorHelper.FormatStContent(original);
            var parsed = SourceMirrorHelper.ParseStContent(text);

            Assert.AreEqual(original.Name, parsed.Name);
            Assert.AreEqual(original.Type, parsed.Type);
            Assert.AreEqual(original.RelativePath, parsed.RelativePath);
            Assert.AreEqual(original.Declaration, parsed.Declaration);
            Assert.AreEqual(original.Implementation, parsed.Implementation);
        }

        [TestMethod]
        public void TestParseStContentActionImplementationOnly()
        {
            var original = new MirrorSourceItem
            {
                Name = "actInit",
                Type = "Action",
                RelativePath = "POUs/Force_sensor/Analog/FB_CalibrateForceSensor/actInit",
                Declaration = "",
                Implementation = "xActive := TRUE;\nuiMainState := 2;"
            };

            string text = SourceMirrorHelper.FormatStContent(original);
            var parsed = SourceMirrorHelper.ParseStContent(text);

            Assert.AreEqual("actInit", parsed.Name);
            Assert.AreEqual("Action", parsed.Type);
            Assert.AreEqual("", parsed.Declaration);
            Assert.AreEqual("xActive := TRUE;\nuiMainState := 2;", parsed.Implementation);
        }

        [TestMethod]
        public void TestParseStContentGvlDeclarationOnly()
        {
            var original = new MirrorSourceItem
            {
                Name = "GVL_IO",
                Type = "GVL",
                RelativePath = "Global/GVL_IO",
                Declaration = "VAR_GLOBAL\n    bReady : BOOL;\nEND_VAR",
                Implementation = ""
            };

            string text = SourceMirrorHelper.FormatStContent(original);
            var parsed = SourceMirrorHelper.ParseStContent(text);

            Assert.AreEqual("GVL_IO", parsed.Name);
            Assert.AreEqual("GVL", parsed.Type);
            Assert.AreEqual("VAR_GLOBAL\n    bReady : BOOL;\nEND_VAR", parsed.Declaration);
            Assert.AreEqual("", parsed.Implementation);
        }

        [TestMethod]
        public void TestEchoSuppressionIgnoresOwnWriteAndDetectsExternalEdit()
        {
            string tempDir = CreateTempDir();
            try
            {
                var item = new MirrorSourceItem
                {
                    Name = "PLC_PRG",
                    Type = "Program",
                    RelativePath = "POUs/PLC_PRG",
                    Declaration = "PROGRAM PLC_PRG\nVAR\nEND_VAR",
                    Implementation = "n := 1;"
                };

                SourceMirrorHelper.SyncSingleNode(tempDir, item);
                string filePath = Path.Combine(tempDir, "POUs", "PLC_PRG.st");

                Assert.IsTrue(SourceMirrorHelper.ShouldIgnoreDiskChangeEvent(filePath),
                    "Self-write should match recent hash cache and be ignored");

                File.WriteAllText(filePath, File.ReadAllText(filePath).Replace("n := 1;", "n := 2;"));
                Assert.IsFalse(SourceMirrorHelper.ShouldIgnoreDiskChangeEvent(filePath),
                    "External modification must not be ignored");
            }
            finally
            {
                SafeDeleteDir(tempDir);
            }
        }

        [TestMethod]
        public void TestWatcherDebouncesExternalFileEdit()
        {
            string tempDir = CreateTempDir();
            try
            {
                var item = new MirrorSourceItem
                {
                    Name = "PLC_PRG",
                    Type = "Program",
                    RelativePath = "POUs/PLC_PRG",
                    Declaration = "PROGRAM PLC_PRG\nVAR\nEND_VAR",
                    Implementation = "n := 1;"
                };
                SourceMirrorHelper.SyncSingleNode(tempDir, item);
                string filePath = Path.Combine(tempDir, "POUs", "PLC_PRG.st");

                var observed = new List<string>();
                var signal = new System.Threading.ManualResetEventSlim(false);

                SourceMirrorHelper.StartWatching(tempDir, changedPath =>
                {
                    observed.Add(changedPath);
                    signal.Set();
                }, debounceMs: 120);

                // Rapid double write should debounce into a single callback
                string updated = SourceMirrorHelper.FormatStContent(new MirrorSourceItem
                {
                    Name = "PLC_PRG",
                    Type = "Program",
                    RelativePath = "POUs/PLC_PRG",
                    Declaration = "PROGRAM PLC_PRG\nVAR\nEND_VAR",
                    Implementation = "n := 77;"
                });
                File.WriteAllText(filePath, updated);
                File.WriteAllText(filePath, updated.Replace("77", "88"));

                Assert.IsTrue(signal.Wait(3000), "Watcher should fire callback for external .st edit");
                System.Threading.Thread.Sleep(250);
                Assert.AreEqual(1, observed.Count, "Rapid edits should coalesce into 1 debounced callback");
                Assert.AreEqual(Path.GetFullPath(filePath), Path.GetFullPath(observed[0]));
            }
            finally
            {
                SourceMirrorHelper.StopWatching();
                SafeDeleteDir(tempDir);
            }
        }

        [TestMethod]
        public void TestWatcherPollFallbackDetectsExternalEditWithoutFsw()
        {
            string tempDir = CreateTempDir();
            try
            {
                var item = new MirrorSourceItem
                {
                    Name = "FB_Poll",
                    Type = "FunctionBlock",
                    RelativePath = "POUs/FB_Poll",
                    Declaration = "FUNCTION_BLOCK FB_Poll\nVAR\nEND_VAR",
                    Implementation = "x := 1;"
                };
                SourceMirrorHelper.SyncSingleNode(tempDir, item);
                string filePath = Path.Combine(tempDir, "POUs", "FB_Poll.st");

                var observed = new List<string>();
                var signal = new System.Threading.ManualResetEventSlim(false);

                SourceMirrorHelper.StartWatching(tempDir, changedPath =>
                {
                    observed.Add(changedPath);
                    signal.Set();
                }, debounceMs: 50, pollIntervalMs: 100);

                // Modify file externally
                string updated = SourceMirrorHelper.FormatStContent(new MirrorSourceItem
                {
                    Name = "FB_Poll",
                    Type = "FunctionBlock",
                    RelativePath = "POUs/FB_Poll",
                    Declaration = "FUNCTION_BLOCK FB_Poll\nVAR\nEND_VAR",
                    Implementation = "x := 999;"
                });
                File.WriteAllText(filePath, updated);

                // Even if we force a poll directly (simulating VBoxSF where FSW never fires),
                // callback must fire once and not re-fire on subsequent polls
                SourceMirrorHelper.PollWatchedDirectoryOnce();
                Assert.IsTrue(signal.Wait(3000), "Poll fallback should detect external .st edit");
                System.Threading.Thread.Sleep(250);
                SourceMirrorHelper.PollWatchedDirectoryOnce();
                System.Threading.Thread.Sleep(150);
                Assert.AreEqual(1, observed.Count, "Duplicate polls must not re-fire for already-observed edit");
            }
            finally
            {
                SourceMirrorHelper.StopWatching();
                SafeDeleteDir(tempDir);
            }
        }

        [TestMethod]
        public void TestParseStContentActionWithLeadingCommentAndGuidType()
        {
            string raw =
                "(*\n---\nname: actInit\ntype: 8ac092e5-3128-4e26-9e7e-11016c6684f2\npath: POUs/Force_sensor/Common/FB_CalcMovingAverage/actInit\n---\n*)\n" +
                "(*** action for init state ***)\n" +
                "hello\n" +
                "IF uiNoValues = 0 THEN\n" +
                "    RETURN;\n" +
                "END_IF\n";

            var parsed = SourceMirrorHelper.ParseStContent(raw);
            Assert.AreEqual("actInit", parsed.Name);
            Assert.AreEqual("", parsed.Declaration);
            Assert.Contains("hello", parsed.Implementation);
            Assert.Contains("IF uiNoValues = 0 THEN", parsed.Implementation);
        }

        [TestMethod]
        public void TestParseStContentActionWithLeadingCommentAndUnknownType()
        {
            string raw =
                "(*\n---\nname: actCustom\ntype: custom-unknown\npath: POUs/FB_Test/actCustom\n---\n*)\n" +
                "(* leading comment *)\n" +
                "// line comment\n" +
                "xFlag := TRUE;\n";

            var parsed = SourceMirrorHelper.ParseStContent(raw);
            Assert.AreEqual("", parsed.Declaration);
            Assert.Contains("xFlag := TRUE;", parsed.Implementation);
        }

        [TestMethod]
        public void TestFormatCompactDiffShowsAddedAndRemovedLines()
        {
            string before = "line1\nuiNoValues := 10;\nline3\n";
            string after = "line1\nuiNoValues := 100;\nline3\nbNew := TRUE;\n";

            string diff = SourceMirrorHelper.FormatCompactDiff(before, after, maxLines: 10);
            Assert.Contains("(+2/-1)", diff);
            Assert.Contains("- uiNoValues := 10;", diff);
            Assert.Contains("+ uiNoValues := 100;", diff);
            Assert.Contains("+ bNew := TRUE;", diff);
        }

        [TestMethod]
        public void TestFormatCompactDiffTruncatesLargeDiffsCleanly()
        {
            var beforeLines = new List<string>();
            var afterLines = new List<string>();
            for (int i = 0; i < 20; i++)
            {
                beforeLines.Add("old_" + i);
                afterLines.Add("new_" + i);
            }

            string diff = SourceMirrorHelper.FormatCompactDiff(
                string.Join("\n", beforeLines),
                string.Join("\n", afterLines),
                maxLines: 6);
            Assert.Contains("(+20/-20)", diff);
            Assert.Contains("more lines)", diff);
        }

        [TestMethod]
        public void TestLastBoundMirrorDirectoryPreservedAcrossDisable()
        {
            string tempDir = CreateTempDir();
            try
            {
                SourceMirrorHelper.SetMirrorDirectory(tempDir);
                Assert.AreEqual(Path.GetFullPath(tempDir), SourceMirrorHelper.LastBoundMirrorDirectory);

                SourceMirrorHelper.ClearMirrorDirectory();
                Assert.IsFalse(SourceMirrorHelper.HasActiveMirror);
                Assert.AreEqual(Path.GetFullPath(tempDir), SourceMirrorHelper.LastBoundMirrorDirectory);
            }
            finally
            {
                SourceMirrorHelper.ResetToEnvironmentDefault();
                SafeDeleteDir(tempDir);
            }
        }

        [TestMethod]
        public void TestGetDefaultProjectMirrorDirectory()
        {
            string projPath = @"C:\Projects\ServoPress\ServoPress.project";
            string expected = Path.Combine(@"C:\Projects\ServoPress", ".cds_mcp", "src");
            string actual = SourceMirrorHelper.GetDefaultProjectMirrorDirectory(projPath);
            Assert.AreEqual(expected, actual);
        }

        [TestMethod]
        public void TestEnsureGitInitializedAndCommitMirrorChanges()
        {
            string tempDir = CreateTempDir();
            try
            {
                string mirrorDir = Path.Combine(tempDir, ".cds_mcp", "src");
                bool created = SourceMirrorHelper.EnsureGitInitialized(mirrorDir);
                Assert.IsTrue(created, "Should initialize .git directory");
                Assert.IsTrue(Directory.Exists(Path.Combine(mirrorDir, ".git")), ".git folder must exist");
                Assert.IsTrue(File.Exists(Path.Combine(tempDir, ".cds_mcp", "README.md")), ".cds_mcp/README.md should be created for AI discovery");
                Assert.IsTrue(File.Exists(Path.Combine(tempDir, ".cds_mcp", ".gitignore")), ".cds_mcp/.gitignore should be created to keep outer repo clean");
                Assert.AreEqual("*", File.ReadAllText(Path.Combine(tempDir, ".cds_mcp", ".gitignore")).Trim());
                Assert.IsFalse(File.Exists(Path.Combine(mirrorDir, "README.md")), "README.md must live in .cds_mcp/, not inside src/");

                // Calling again should be a no-op
                bool second = SourceMirrorHelper.EnsureGitInitialized(mirrorDir);
                Assert.IsFalse(second, "Second EnsureGitInitialized should return false");

                // Write a .st file via SyncAll and commit
                var items = new[]
                {
                    new MirrorSourceItem
                    {
                        Name = "MAIN",
                        Type = "Program",
                        RelativePath = "Application/MAIN",
                        Declaration = "PROGRAM MAIN\nVAR\n    xRun : BOOL;\nEND_VAR",
                        Implementation = "xRun := TRUE;"
                    }
                };
                SourceMirrorHelper.SyncAll(mirrorDir, items);

                var commit1 = SourceMirrorHelper.CommitMirrorChanges(mirrorDir, "initial snapshot");
                Assert.IsTrue(commit1.gitAvailable, "git should be available in test environment");
                Assert.IsTrue(commit1.committed, "Initial snapshot should be committed");
                Assert.IsFalse(string.IsNullOrEmpty(commit1.commitHash), "Commit hash should be returned");
                Assert.AreEqual(commit1.commitHash, SourceMirrorHelper.LastCommitHash, "LastCommitHash should be updated");

                // Clean working tree should not produce empty commit
                var commitClean = SourceMirrorHelper.CommitMirrorChanges(mirrorDir, "no changes");
                Assert.IsFalse(commitClean.committed, "Clean working tree should not commit");

                // Modify .st file and commit again
                items[0].Implementation = "xRun := FALSE;";
                SourceMirrorHelper.SyncAll(mirrorDir, items);
                var commit2 = SourceMirrorHelper.CommitMirrorChanges(mirrorDir, "update MAIN");
                Assert.IsTrue(commit2.committed, "Modified .st should be committed");
                Assert.IsTrue(!string.Equals(commit1.commitHash, commit2.commitHash, StringComparison.Ordinal), "Second commit hash should differ");
            }
            finally
            {
                SafeDeleteDir(tempDir);
            }
        }

        [TestMethod]
        public void TestExplicitlyDisabledFlagPreventsAutoAttach()
        {
            string tempDir = CreateTempDir();
            try
            {
                SourceMirrorHelper.ResetToEnvironmentDefault();
                Assert.IsFalse(SourceMirrorHelper.IsExplicitlyDisabled);

                SourceMirrorHelper.SetExplicitlyDisabled(true);
                Assert.IsTrue(SourceMirrorHelper.IsExplicitlyDisabled);

                SourceMirrorHelper.SetMirrorDirectory(tempDir);
                Assert.IsFalse(SourceMirrorHelper.IsExplicitlyDisabled, "Setting mirror dir should clear disabled flag");
            }
            finally
            {
                SourceMirrorHelper.ResetToEnvironmentDefault();
                SafeDeleteDir(tempDir);
            }
        }

        private class MockTextDocument
        {
            public string Text { get; set; }
        }

        private class MockTextDocumentProvider
        {
            private readonly MockTextDocument _doc;
            public MockTextDocumentProvider(string text) { _doc = new MockTextDocument { Text = text }; }
            public MockTextDocument GetTextDocument() => _doc;
        }

        private class MockPouEditorHost
        {
            public Guid ObjectGuid { get; set; }
            public object InterfaceEditorView { get; set; }
            public object ImplementationEditorView { get; set; }
        }

        [TestMethod]
        public void TestTryExtractPouEditorSnapshotReadsUnsavedBuffers()
        {
            Guid g = Guid.NewGuid();
            var host = new MockPouEditorHost
            {
                ObjectGuid = g,
                InterfaceEditorView = new MockTextDocumentProvider("PROGRAM MOTION_PRG\nVAR\n    i : INT;\nEND_VAR"),
                ImplementationEditorView = new MockTextDocumentProvider("i := i + 1;")
            };

            bool ok = CodesysEditorBufferHelper.TryExtractPouEditorSnapshot(host, "MOTION_PRG*", out var snap);
            Assert.IsTrue(ok, "Should extract snapshot from duck-typed IPOUEditor");
            Assert.AreEqual(g, snap.ObjectGuid);
            Assert.AreEqual("MOTION_PRG", snap.ObjectName);
            Assert.Contains("i : INT;", snap.Declaration);
            Assert.Contains("i := i + 1;", snap.Implementation);
        }

        [TestMethod]
        public void TestTryExtractPouEditorSnapshotHandlesNullViewsSafely()
        {
            var host = new MockPouEditorHost
            {
                ObjectGuid = Guid.Empty,
                InterfaceEditorView = null,
                ImplementationEditorView = null
            };
            bool ok = CodesysEditorBufferHelper.TryExtractPouEditorSnapshot(host, "Empty", out var snap);
            Assert.IsFalse(ok);
            Assert.IsNull(snap);
        }

        private class MockSynEdDocument
        {
            public string Text { get; set; }
        }

        private class MockSynEdControl
        {
            public MockSynEdDocument Document { get; set; }
            public string Text { get; set; }
        }

        private class MockIecTextEditorView
        {
            public object SynEdControl { get; set; }
        }

        [TestMethod]
        public void TestTryExtractPouEditorSnapshotReadsSynEdControlDocumentAndText()
        {
            Guid g = Guid.NewGuid();
            var host = new MockPouEditorHost
            {
                ObjectGuid = g,
                InterfaceEditorView = new MockIecTextEditorView
                {
                    SynEdControl = new MockSynEdControl
                    {
                        Document = new MockSynEdDocument { Text = "PROGRAM MOTION_PRG\nVAR\n    i_100 : UINT;\nEND_VAR" }
                    }
                },
                ImplementationEditorView = new MockIecTextEditorView
                {
                    SynEdControl = new MockSynEdControl
                    {
                        Document = null,
                        Text = "i_100 := i_100 + 1;"
                    }
                }
            };

            bool ok = CodesysEditorBufferHelper.TryExtractPouEditorSnapshot(host, "MOTION_PRG*", out var snap);
            Assert.IsTrue(ok, "Should extract snapshot from SynEdControl.Document.Text and SynEdControl.Text");
            Assert.AreEqual(g, snap.ObjectGuid);
            Assert.AreEqual("MOTION_PRG", snap.ObjectName);
            Assert.Contains("i_100 : UINT;", snap.Declaration);
            Assert.Contains("i_100 := i_100 + 1;", snap.Implementation);
        }

        [TestMethod]
        public void TestTryExtractPouEditorSnapshotIgnoresEmptyOrSearchBoxControls()
        {
            Guid g = Guid.NewGuid();
            var host = new MockPouEditorHost
            {
                ObjectGuid = g,
                InterfaceEditorView = new MockIecTextEditorView
                {
                    SynEdControl = new MockSynEdControl
                    {
                        Document = new MockSynEdDocument { Text = "   \r\n  " }
                    }
                },
                ImplementationEditorView = new MockIecTextEditorView
                {
                    SynEdControl = new MockSynEdControl
                    {
                        Document = null,
                        Text = ""
                    }
                }
            };

            bool ok = CodesysEditorBufferHelper.TryExtractPouEditorSnapshot(host, "MOTION_PRG*", out var snap);
            Assert.IsFalse(ok, "Empty or whitespace-only editor buffers must not produce a snapshot that wipes existing .st files");
            Assert.IsNull(snap);
        }

        [TestMethod]
        public void TestCodesysPluginEditorContractIfInstalled()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (string.IsNullOrEmpty(programFiles) || !Directory.Exists(programFiles)) return;

            string codesysRoot = Directory.GetDirectories(programFiles, "CODESYS*")
                .Select(d => Path.Combine(d, "CODESYS"))
                .FirstOrDefault(Directory.Exists);
            if (codesysRoot == null) return; // CODESYS not installed on this machine

            string pouPlugin = Directory.GetFiles(Path.Combine(codesysRoot, "PlugIns"), "POUEditor.plugin.dll", SearchOption.AllDirectories).FirstOrDefault();
            string iecPlugin = Directory.GetFiles(Path.Combine(codesysRoot, "PlugIns"), "IECTextEditor.plugin.dll", SearchOption.AllDirectories).FirstOrDefault();
            if (pouPlugin == null || iecPlugin == null) return;

            string commonDir = Path.Combine(codesysRoot, "Common");
            string pluginsDir = Path.Combine(codesysRoot, "PlugIns");
            string lacDir = Path.Combine(codesysRoot, "LacBinaries");
            var resolvedCache = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

            ResolveEventHandler resolver = (sender, args) =>
            {
                try
                {
                    if (resolvedCache.TryGetValue(args.Name, out var cached)) return cached;

                    var reqName = new AssemblyName(args.Name);
                    if (reqName.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) return null;

                    string fileName = reqName.Name + ".dll";
                    var candidates = new List<string>();
                    string inCommon = Path.Combine(commonDir, fileName);
                    if (File.Exists(inCommon)) candidates.Add(inCommon);
                    if (Directory.Exists(lacDir)) candidates.AddRange(Directory.GetFiles(lacDir, fileName, SearchOption.AllDirectories));
                    if (Directory.Exists(pluginsDir)) candidates.AddRange(Directory.GetFiles(pluginsDir, fileName, SearchOption.AllDirectories));

                    string best = null;
                    if (reqName.Version != null)
                    {
                        best = candidates.FirstOrDefault(path =>
                        {
                            try { return AssemblyName.GetAssemblyName(path).Version == reqName.Version; }
                            catch { return false; }
                        });
                    }
                    best = best ?? candidates.FirstOrDefault();
                    if (best != null)
                    {
                        Assembly loaded = Assembly.LoadFrom(best);
                        resolvedCache[args.Name] = loaded;
                        return loaded;
                    }
                }
                catch { }
                return null;
            };

            AppDomain.CurrentDomain.AssemblyResolve += resolver;
            try
            {
                Type FindType(Assembly asm, string fullName)
                {
                    try
                    {
                        return asm.GetType(fullName, false);
                    }
                    catch (ReflectionTypeLoadException rtle)
                    {
                        return rtle.Types?.FirstOrDefault(t => t != null && t.FullName == fullName);
                    }
                }

                var pouAsm = Assembly.LoadFrom(pouPlugin);
                Type pouEditorType = FindType(pouAsm, "_3S.CoDeSys.POUEditor.POUEditor");
                Assert.IsNotNull(pouEditorType, "Expected _3S.CoDeSys.POUEditor.POUEditor in POUEditor.plugin.dll");

                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                Assert.IsNotNull(pouEditorType.GetProperty("ObjectGuid", flags), "POUEditor must expose ObjectGuid");
                Assert.IsNotNull(pouEditorType.GetProperty("InterfaceEditorView", flags), "POUEditor must expose InterfaceEditorView");
                Assert.IsNotNull(pouEditorType.GetProperty("ImplementationEditorView", flags), "POUEditor must expose ImplementationEditorView");

                var iecAsm = Assembly.LoadFrom(iecPlugin);
                Type iecEditorType = FindType(iecAsm, "_3S.CoDeSys.IECTextEditor.IECTextEditor");
                Assert.IsNotNull(iecEditorType, "Expected _3S.CoDeSys.IECTextEditor.IECTextEditor in IECTextEditor.plugin.dll");
                PropertyInfo synProp = iecEditorType.GetProperty("SynEdControl", flags);
                Assert.IsNotNull(synProp, "IECTextEditor must expose SynEdControl");

                // Runtime regression test against real SynEdControl + IncrementalSearchControl
                Type synType = synProp.PropertyType;
                Type incSearchType = FindType(iecAsm, "_3S.CoDeSys.IECTextEditor.IncrementalSearchControl");
                Assert.IsNotNull(incSearchType, "Expected _3S.CoDeSys.IECTextEditor.IncrementalSearchControl in IECTextEditor.plugin.dll");

                object synDecl = Activator.CreateInstance(synType);
                object synImpl = Activator.CreateInstance(synType);
                try
                {
                    void PopulateSynEdWithSearchOverlay(object synCtrl, string code)
                    {
                        object buffer = synType.GetProperty("Buffer", flags).GetValue(synCtrl, null);
                        MethodInfo insertMethod = buffer.GetType().GetMethod("Insert", flags, null, new[] { typeof(int), typeof(string) }, null);
                        Assert.IsNotNull(insertMethod, "SynEdControl.Buffer must expose Insert(int, string)");
                        insertMethod.Invoke(buffer, new object[] { 0, code });

                        object incCtrl = Activator.CreateInstance(incSearchType);
                        object controls = synType.GetProperty("Controls", flags).GetValue(synCtrl, null);
                        MethodInfo addCtrl = controls.GetType().GetMethod("Add", BindingFlags.Public | BindingFlags.Instance);
                        Assert.IsNotNull(addCtrl, "Control.Controls must expose Add");
                        addCtrl.Invoke(controls, new[] { incCtrl });
                    }

                    PopulateSynEdWithSearchOverlay(synDecl, "PROGRAM MOTION_PRG\r\nVAR\r\n    i : INT;\r\n    j : INT;\r\nEND_VAR");
                    PopulateSynEdWithSearchOverlay(synImpl, "fbPower(Enable := TRUE);");

                    Guid g = Guid.NewGuid();
                    var host = new MockPouEditorHost
                    {
                        ObjectGuid = g,
                        InterfaceEditorView = new MockIecTextEditorView { SynEdControl = synDecl },
                        ImplementationEditorView = new MockIecTextEditorView { SynEdControl = synImpl }
                    };

                    bool ok = CodesysEditorBufferHelper.TryExtractPouEditorSnapshot(host, "MOTION_PRG*", out var snap);
                    Assert.IsTrue(ok, "Should extract snapshot from real SynEdControl.Buffer even with IncrementalSearchControl attached");
                    Assert.IsNotNull(snap.Declaration);
                    Assert.Contains("i : INT;", snap.Declaration);
                    Assert.Contains("j : INT;", snap.Declaration);
                    Assert.IsNotNull(snap.Implementation);
                    Assert.Contains("fbPower(Enable := TRUE);", snap.Implementation);

                    string tabPlugin = Directory.GetFiles(pluginsDir, "TabularDeclarationEditor.plugin.dll", SearchOption.AllDirectories).FirstOrDefault();
                    if (tabPlugin != null)
                    {
                        var tabAsm = Assembly.LoadFrom(tabPlugin);
                        Type swType = FindType(tabAsm, "_3S.CoDeSys.TabularDeclarationEditor.SwitchableDeclarationEditor");
                        Assert.IsNotNull(swType, "Expected _3S.CoDeSys.TabularDeclarationEditor.SwitchableDeclarationEditor in TabularDeclarationEditor.plugin.dll");

                        object iecDecl = FormatterServices.GetUninitializedObject(iecEditorType);
                        object iecImpl = FormatterServices.GetUninitializedObject(iecEditorType);
                        object swDecl = FormatterServices.GetUninitializedObject(swType);
                        object pouHost = FormatterServices.GetUninitializedObject(pouEditorType);

                        iecEditorType.GetField("_synEdControl", flags).SetValue(iecDecl, synDecl);
                        iecEditorType.GetField("_synEdControl", flags).SetValue(iecImpl, synImpl);
                        swType.GetField("_text", flags).SetValue(swDecl, iecDecl);
                        pouEditorType.GetField("_interfaceEditorView", flags).SetValue(pouHost, swDecl);
                        pouEditorType.GetField("_implementationEditorView", flags).SetValue(pouHost, iecImpl);
                        pouEditorType.GetField("_objectGuid", flags).SetValue(pouHost, g);

                        bool okRealChain = CodesysEditorBufferHelper.TryExtractPouEditorSnapshot(pouHost, "MOTION_PRG*", out var snapRealChain);
                        Assert.IsTrue(okRealChain, "Should extract snapshot from complete POUEditor -> SwitchableDeclarationEditor -> IECTextEditor -> SynEdControl chain");
                        Assert.AreEqual(g, snapRealChain.ObjectGuid);
                        Assert.Contains("i : INT;", snapRealChain.Declaration);
                        Assert.Contains("j : INT;", snapRealChain.Declaration);
                        Assert.Contains("fbPower(Enable := TRUE);", snapRealChain.Implementation);
                    }
                }
                finally
                {
                    (synDecl as IDisposable)?.Dispose();
                    (synImpl as IDisposable)?.Dispose();
                }
            }
            finally
            {
                AppDomain.CurrentDomain.AssemblyResolve -= resolver;
            }
        }

        [TestMethod]
        public void TestTryApplyLiveSnapshotByGuidAndNameAndNonEmptyGuard()
        {
            Guid guid = Guid.NewGuid();
            var liveBuffers = new Dictionary<string, LiveEditorSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["guid:" + guid.ToString("N")] = new LiveEditorSnapshot
                {
                    ObjectGuid = guid,
                    ObjectName = "MOTION_PRG",
                    Declaration = "PROGRAM MOTION_PRG\r\nVAR\r\n    liveVar : INT;\r\nEND_VAR",
                    Implementation = "liveVar := liveVar + 1;"
                },
                ["name:MOTION_PRG"] = new LiveEditorSnapshot
                {
                    ObjectGuid = Guid.Empty,
                    ObjectName = "MOTION_PRG",
                    Declaration = "PROGRAM MOTION_PRG\r\nVAR\r\n    nameMatched : BOOL;\r\nEND_VAR",
                    Implementation = "nameMatched := TRUE;"
                },
                ["name:MoveAbs"] = new LiveEditorSnapshot
                {
                    ObjectGuid = Guid.Empty,
                    ObjectName = "MoveAbs",
                    Declaration = "METHOD MoveAbs : BOOL",
                    Implementation = "MoveAbs := TRUE;"
                },
                ["name:EmptyLive"] = new LiveEditorSnapshot
                {
                    ObjectGuid = Guid.Empty,
                    ObjectName = "EmptyLive",
                    Declaration = "   \r\n  ",
                    Implementation = null
                }
            };

            // 1. Exact GUID match takes precedence even when objectName differs
            string decl = "OLD_DECL";
            string impl = "OLD_IMPL";
            bool guidApplied = CodesysEditorBufferHelper.TryApplyLiveSnapshot(
                liveBuffers,
                guid,
                "RENAMED_IN_TREE",
                true,
                true,
                ref decl,
                ref impl);
            Assert.IsTrue(guidApplied, "GUID match should apply live snapshot");
            Assert.Contains("liveVar : INT;", decl);
            Assert.AreEqual("liveVar := liveVar + 1;", impl);

            // 2. Exact name match and hierarchical path fallbacks ('/' and '.')
            decl = "OLD_DECL";
            impl = "OLD_IMPL";
            bool pathSlashApplied = CodesysEditorBufferHelper.TryApplyLiveSnapshot(
                liveBuffers,
                Guid.Empty,
                "Application/MOTION_PRG",
                true,
                true,
                ref decl,
                ref impl);
            Assert.IsTrue(pathSlashApplied, "Slash path should fall back to leaf name 'MOTION_PRG'");
            Assert.Contains("nameMatched : BOOL;", decl);
            Assert.AreEqual("nameMatched := TRUE;", impl);

            decl = "OLD_DECL";
            impl = "OLD_IMPL";
            bool pathDotApplied = CodesysEditorBufferHelper.TryApplyLiveSnapshot(
                liveBuffers,
                Guid.Empty,
                "FB_Axis.MoveAbs",
                true,
                true,
                ref decl,
                ref impl);
            Assert.IsTrue(pathDotApplied, "Dot path should fall back to leaf name 'MoveAbs'");
            Assert.AreEqual("METHOD MoveAbs : BOOL", decl);
            Assert.AreEqual("MoveAbs := TRUE;", impl);

            // 3. Non-empty guard: whitespace/null live snapshot must never overwrite persisted text
            decl = "KEEP_EXISTING_DECL";
            impl = "KEEP_EXISTING_IMPL";
            bool emptyApplied = CodesysEditorBufferHelper.TryApplyLiveSnapshot(
                liveBuffers,
                Guid.Empty,
                "EmptyLive",
                true,
                true,
                ref decl,
                ref impl);
            Assert.IsFalse(emptyApplied, "Whitespace/null live snapshot should not report applied");
            Assert.AreEqual("KEEP_EXISTING_DECL", decl);
            Assert.AreEqual("KEEP_EXISTING_IMPL", impl);

            // 4. Respect hasDecl / hasImpl flags
            decl = "GVL_PERSISTED";
            impl = "";
            bool hasFlagsApplied = CodesysEditorBufferHelper.TryApplyLiveSnapshot(
                liveBuffers,
                guid,
                "MOTION_PRG",
                true,
                false,
                ref decl,
                ref impl);
            Assert.IsTrue(hasFlagsApplied);
            Assert.Contains("liveVar : INT;", decl);
            Assert.AreEqual("", impl, "Implementation must stay untouched when hasImpl=false");
        }
    }
}
