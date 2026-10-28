using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using CodesysMcp;

namespace CodesysMcpTests
{
    public class SearchEngineTests
    {
        private const string SampleDeclaration =
            "PROGRAM PLC_PRG\n" +
            "VAR\n" +
            "    nCounter : DINT := 0;\n" +
            "    nCounterLimit : DINT := 100;\n" +
            "    bEmergencyStop : BOOL := FALSE; // 急停状态监测\n" +
            "END_VAR\n";

        private const string SampleImplementation =
            "// State Machine\n" +
            "IF NOT bEmergencyStop THEN\n" +
            "    nCounter := nCounter + 1;\n" +
            "END_IF;\n";

        [TestMethod]
        public void TestSearchInTextPlainTextInsensitive()
        {
            var regex = new Regex(Regex.Escape("counter"), RegexOptions.IgnoreCase);
            var matches = CodesysOperations.SearchInText(SampleDeclaration, "PLC_PRG", "Program", "declaration", regex, 100);

            Assert.AreEqual(2, matches.Count, "Should match nCounter and nCounterLimit when case-insensitive");
            Assert.AreEqual(3, matches[0].lineNumber, "nCounter should be on line 3");
            Assert.Contains("nCounter : DINT", matches[0].lineContent);
            Assert.AreEqual("declaration", matches[0].section);

            Assert.AreEqual(4, matches[1].lineNumber, "nCounterLimit should be on line 4");
        }

        [TestMethod]
        public void TestSearchInTextPlainTextSensitive()
        {
            var regex = new Regex(Regex.Escape("bEmergencyStop"), RegexOptions.None);
            var matches = CodesysOperations.SearchInText(SampleImplementation, "PLC_PRG", "Program", "implementation", regex, 100);

            Assert.AreEqual(1, matches.Count);
            Assert.AreEqual(2, matches[0].lineNumber);
            Assert.Contains("IF NOT bEmergencyStop THEN", matches[0].lineContent);
            Assert.AreEqual("implementation", matches[0].section);
        }

        [TestMethod]
        public void TestSearchInTextRegexWordBoundary()
        {
            // \bnCounter\b should match nCounter but not nCounterLimit
            var regex = new Regex(@"\bnCounter\b", RegexOptions.IgnoreCase);
            var matches = CodesysOperations.SearchInText(SampleDeclaration, "PLC_PRG", "Program", "declaration", regex, 100);

            Assert.AreEqual(1, matches.Count, "Should match only exact word nCounter, not nCounterLimit");
            Assert.AreEqual(3, matches[0].lineNumber);
            Assert.AreEqual("nCounter", matches[0].matchedText);
        }

        [TestMethod]
        public void TestSearchInTextMaxSlotsTruncation()
        {
            var regex = new Regex(Regex.Escape("DINT"), RegexOptions.IgnoreCase);
            // In SampleDeclaration, DINT appears on line 3 and line 4. With maxSlots=1, it must return only 1.
            var matches = CodesysOperations.SearchInText(SampleDeclaration, "PLC_PRG", "Program", "declaration", regex, 1);

            Assert.AreEqual(1, matches.Count, "Should respect maxSlots truncation limit");
            Assert.AreEqual(3, matches[0].lineNumber);
        }

        [TestMethod]
        public void TestSearchInTextEmptyOrNullText()
        {
            var regex = new Regex("test");
            var matches = CodesysOperations.SearchInText("", "PLC_PRG", "Program", "declaration", regex, 10);
            Assert.AreEqual(0, matches.Count);

            var matchesNull = CodesysOperations.SearchInText(null, "PLC_PRG", "Program", "declaration", regex, 10);
            Assert.AreEqual(0, matchesNull.Count);
        }

        [TestMethod]
        public void TestSearchInTextNonAsciiCharacters()
        {
            var regex = new Regex(Regex.Escape("急停状态"), RegexOptions.None);
            var matches = CodesysOperations.SearchInText(SampleDeclaration, "PLC_PRG", "Program", "declaration", regex, 10);

            Assert.AreEqual(1, matches.Count, "Should find Chinese characters in comments");
            Assert.AreEqual(5, matches[0].lineNumber);
            Assert.Contains("急停状态监测", matches[0].lineContent);
        }
    }
}
