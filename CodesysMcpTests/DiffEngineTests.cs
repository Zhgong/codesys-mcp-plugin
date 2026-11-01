using System;
using CodesysMcp;

namespace CodesysMcpTests
{
    public class DiffEngineTests
    {
        [TestMethod]
        public void TestIdenticalTextsProduceEmptyDiff()
        {
            string code = "PROGRAM PLC_PRG\nVAR\n    nCounter : DINT := 0;\nEND_VAR";
            string diff = DiffHelper.GenerateUnifiedDiff(code, code, "a/PLC_PRG", "b/PLC_PRG");
            Assert.AreEqual(string.Empty, diff, "Identical code must produce empty diff");
        }

        [TestMethod]
        public void TestPureAddition()
        {
            string orig = "PROGRAM PLC_PRG\nEND_VAR";
            string mod = "PROGRAM PLC_PRG\nVAR\n    bFlag : BOOL;\nEND_VAR";
            string diff = DiffHelper.GenerateUnifiedDiff(orig, mod, "a/PLC_PRG", "b/PLC_PRG");

            Assert.IsTrue(!string.IsNullOrEmpty(diff));
            Assert.Contains("--- a/PLC_PRG", diff);
            Assert.Contains("+++ b/PLC_PRG", diff);
            Assert.Contains("+VAR", diff);
            Assert.Contains("+    bFlag : BOOL;", diff);
        }

        [TestMethod]
        public void TestPureDeletion()
        {
            string orig = "PROGRAM PLC_PRG\nVAR\n    bOld : BOOL;\nEND_VAR";
            string mod = "PROGRAM PLC_PRG\nEND_VAR";
            string diff = DiffHelper.GenerateUnifiedDiff(orig, mod, "a/PLC_PRG", "b/PLC_PRG");

            Assert.IsTrue(!string.IsNullOrEmpty(diff));
            Assert.Contains("-VAR", diff);
            Assert.Contains("-    bOld : BOOL;", diff);
        }

        [TestMethod]
        public void TestModificationWithContext()
        {
            string orig = "nStep := 0;\nnCounter := 1;\nbDone := FALSE;";
            string mod = "nStep := 0;\nnCounter := 2;\nbDone := FALSE;";
            string diff = DiffHelper.GenerateUnifiedDiff(orig, mod, "a/ST", "b/ST");

            Assert.Contains("@@ -1,", diff);
            Assert.Contains(" nStep := 0;", diff);
            Assert.Contains("-nCounter := 1;", diff);
            Assert.Contains("+nCounter := 2;", diff);
            Assert.Contains(" bDone := FALSE;", diff);
        }

        [TestMethod]
        public void TestEmptyOriginalAllAdded()
        {
            string orig = "";
            string mod = "NEW_POU_CONTENT\nLINE_TWO";
            string diff = DiffHelper.GenerateUnifiedDiff(orig, mod, "a/NEW", "b/NEW");

            Assert.IsTrue(!string.IsNullOrEmpty(diff));
            Assert.Contains("+NEW_POU_CONTENT", diff);
            Assert.Contains("+LINE_TWO", diff);
        }

        [TestMethod]
        public void TestEmptyModifiedAllDeleted()
        {
            string orig = "OLD_POU_CONTENT";
            string mod = "";
            string diff = DiffHelper.GenerateUnifiedDiff(orig, mod, "a/OLD", "b/OLD");

            Assert.IsTrue(!string.IsNullOrEmpty(diff));
            Assert.Contains("-OLD_POU_CONTENT", diff);
        }
    }
}
