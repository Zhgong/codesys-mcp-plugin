using System;
using System.Collections.Generic;
using System.IO;
using CodesysMcp;

namespace CodesysMcpTests
{
    public class DiffViewerTests
    {
        [TestMethod]
        public void TestFormatPouAsStProducesStructuredOutput()
        {
            string decl = "VAR\n    x : INT;\nEND_VAR";
            string impl = "x := x + 1;";
            string formatted = DiffViewerHelper.FormatPouAsSt("TestPOU", decl, impl);

            Assert.IsTrue(formatted.Contains("// POU: TestPOU"), "Should contain POU header");
            Assert.IsTrue(formatted.Contains("// SECTION: DECLARATION"), "Should contain declaration marker");
            Assert.IsTrue(formatted.Contains("x : INT;"), "Should contain declaration content");
            Assert.IsTrue(formatted.Contains("// SECTION: IMPLEMENTATION"), "Should contain implementation marker");
            Assert.IsTrue(formatted.Contains("x := x + 1;"), "Should contain implementation content");
        }

        [TestMethod]
        public void TestExportDiffFilesCreatesBeforeAndAfterStFiles()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "CodesysMcp_DiffTest_" + Guid.NewGuid().ToString("N"));
            try
            {
                var snapshots = new Dictionary<string, PouSnapshot>
                {
                    ["POU_Alpha"] = new PouSnapshot
                    {
                        Name = "POU_Alpha",
                        OriginalDeclaration = "VAR\n    origA : BOOL;\nEND_VAR",
                        OriginalImplementation = "origA := TRUE;",
                        ExistedBefore = true
                    },
                    ["POU_Beta"] = new PouSnapshot
                    {
                        Name = "POU_Beta",
                        OriginalDeclaration = "",
                        OriginalImplementation = "",
                        ExistedBefore = false
                    }
                };

                var exported = DiffViewerHelper.ExportDiffFiles(
                    tempDir,
                    snapshots,
                    name =>
                    {
                        if (name == "POU_Alpha") return ("VAR\n    origA : BOOL;\n    newVar : INT;\nEND_VAR", "origA := FALSE;");
                        if (name == "POU_Beta") return ("VAR\n    b : REAL;\nEND_VAR", "b := 3.14;");
                        return (null, null);
                    }
                );

                Assert.AreEqual(2, exported.Count, "Should export 2 files");

                string beforeAlpha = Path.Combine(tempDir, "before", "POU_Alpha.st");
                string afterAlpha = Path.Combine(tempDir, "after", "POU_Alpha.st");
                Assert.IsTrue(File.Exists(beforeAlpha), "before/POU_Alpha.st must exist");
                Assert.IsTrue(File.Exists(afterAlpha), "after/POU_Alpha.st must exist");

                string beforeContent = File.ReadAllText(beforeAlpha);
                string afterContent = File.ReadAllText(afterAlpha);
                Assert.IsTrue(beforeContent.Contains("origA : BOOL;"));
                Assert.IsFalse(beforeContent.Contains("newVar : INT;"));
                Assert.IsTrue(afterContent.Contains("newVar : INT;"));

                string beforeBeta = Path.Combine(tempDir, "before", "POU_Beta.st");
                string afterBeta = Path.Combine(tempDir, "after", "POU_Beta.st");
                Assert.IsTrue(File.Exists(beforeBeta), "before/POU_Beta.st must exist");
                Assert.IsTrue(File.Exists(afterBeta), "after/POU_Beta.st must exist");
                Assert.IsTrue(File.ReadAllText(afterBeta).Contains("b : REAL;"));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [TestMethod]
        public void TestGenerateHtmlDiffReportProducesValidHtml()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "CodesysMcp_HtmlTest_" + Guid.NewGuid().ToString("N"));
            try
            {
                var diffItems = new List<PouDiffItem>
                {
                    new PouDiffItem
                    {
                        name = "POU_Test",
                        status = "modified",
                        unifiedDiff = "--- a/POU_Test\n+++ b/POU_Test\n@@ -1,2 +1,3 @@\n VAR\n+    addedVar : INT;\n END_VAR"
                    }
                };

                string htmlFile = DiffViewerHelper.GenerateHtmlDiffReport(tempDir, diffItems);
                Assert.IsNotNull(htmlFile);
                Assert.IsTrue(File.Exists(htmlFile), "HTML file must exist");

                string htmlContent = File.ReadAllText(htmlFile);
                Assert.IsTrue(htmlContent.Contains("<!DOCTYPE html>"), "Should contain DOCTYPE");
                Assert.IsTrue(htmlContent.Contains("POU_Test"), "Should contain POU name");
                Assert.IsTrue(htmlContent.Contains("addedVar : INT;"), "Should contain diff addition");
                Assert.IsTrue(htmlContent.Contains("diff-add"), "Should contain CSS diff class");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
    }
}
