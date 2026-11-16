// ConfigWriterTests - the write side of config.ini (DESIGN.md section 8).
//
// Oracle provenance: every expected document below is written out literally from
// the rules in the work order and the DESIGN.md section 8 sample - no expected
// value is copied from ConfigWriter's own output. The padded " quality = 19 "
// form and the same key in another section are the two ways a naive "find '='
// and rebuild the line" implementation passes a value check while silently
// reformatting, or corrupting, the file the user hand-edited.
//
// The checks compare whole documents, not just the changed value: "every other
// byte is unchanged" is only observable by comparing the complete text, so a
// writer that also rewrites a comment, drops a blank line, changes a key's
// spelling or swaps CRLF for LF fails here even when quality=22 is correct.
//
// The writer is pure (text in, text out) and lives in src\Logic, so no test here
// touches %APPDATA% - see DESIGN.md section 11 for why that boundary exists.
using System;
using SnipVid.Logic;

namespace SnipVid.Tests
{
    /// <summary>
    /// Checks for <see cref="ConfigWriter"/>: one value changes, every other byte
    /// of the document survives.
    /// </summary>
    public static class ConfigWriterTests
    {
        /// <summary>The section and key DESIGN.md section 8 gives the quality setting.</summary>
        private const string RecordSection = "record";
        private const string QualityKey = "quality";

        /// <summary>
        /// The DESIGN.md section 8 sample with a comment line and one unknown key
        /// added, so "ignored parts are byte-identical" has something to assert.
        /// </summary>
        private const string DesignSample =
            "; SnipVid config - hand-editable\r\n"
            + "[record]\r\n"
            + "fps=60\r\n"
            + "quality=19\r\n"
            + "mystery=keep me\r\n"
            + "folder=%USERPROFILE%\\Videos\\Screen Recordings\r\n"
            + "ffmpeg_path=\r\n"
            + "[input]\r\n"
            + "hotkey=Ctrl+Alt+Shift+S\r\n";

        /// <summary>
        /// The same document with the one permitted difference: quality 19 became
        /// 22. Transcribed by hand from the input above.
        /// </summary>
        private const string DesignSampleWith22 =
            "; SnipVid config - hand-editable\r\n"
            + "[record]\r\n"
            + "fps=60\r\n"
            + "quality=22\r\n"
            + "mystery=keep me\r\n"
            + "folder=%USERPROFILE%\\Videos\\Screen Recordings\r\n"
            + "ffmpeg_path=\r\n"
            + "[input]\r\n"
            + "hotkey=Ctrl+Alt+Shift+S\r\n";

        /// <summary>
        /// W1: the DESIGN.md section 8 sample keeps its comment, unknown key,
        /// blank-free layout and other section; only the quality value moves.
        /// </summary>
        public static void TestW1_DesignSampleChangesOnlyTheValue()
        {
            string output = ConfigWriter.SetValue(DesignSample, RecordSection, QualityKey, "22");

            Harness.Check(
                "W1 quality=19 becomes quality=22 and every other byte is unchanged",
                output == DesignSampleWith22,
                Describe(DesignSampleWith22, output));
            Harness.Check(
                "W1 the comment line survives",
                output.Contains("; SnipVid config - hand-editable"),
                "comment missing from <" + Escape(output) + ">");
            Harness.Check(
                "W1 the unknown key survives verbatim",
                output.Contains("mystery=keep me"),
                "unknown key missing from <" + Escape(output) + ">");
            Harness.Check(
                "W1 the folder value and its CRLF survive",
                output.Contains("folder=%USERPROFILE%\\Videos\\Screen Recordings\r\n"),
                "folder line missing from <" + Escape(output) + ">");
            Harness.Check(
                "W1 the [input] section and its hotkey survive",
                output.Contains("[input]\r\nhotkey=Ctrl+Alt+Shift+S\r\n"),
                "[input] missing from <" + Escape(output) + ">");
            Harness.Check(
                "W1 exactly one value changed: 19 is gone, 22 appears once",
                !output.Contains("quality=19") && CountOf(output, "quality=22") == 1,
                "counts: 19=" + CountOf(output, "quality=19")
                    + " 22=" + CountOf(output, "quality=22"));
        }

        /// <summary>
        /// W2: " quality = 19 " keeps its leading whitespace, key spelling and the
        /// spacing on both sides of '='. Only the digits may move.
        /// </summary>
        public static void TestW2_PaddedValueKeepsOriginalSpacing()
        {
            const string input =
                "[record]\r\n"
                + "  quality  =  19  \r\n"
                + "[input]\r\n"
                + "hotkey=Ctrl+Alt+Shift+S\r\n";
            const string expected =
                "[record]\r\n"
                + "  quality  =  22  \r\n"
                + "[input]\r\n"
                + "hotkey=Ctrl+Alt+Shift+S\r\n";

            string output = ConfigWriter.SetValue(input, RecordSection, QualityKey, "22");

            Harness.Check(
                "W2 padded key and value spacing are preserved digit-for-digit",
                output == expected,
                Describe(expected, output));
            Harness.Check(
                "W2 the line did not collapse to quality=22",
                !output.Contains("quality=22"),
                "collapsed line present in <" + Escape(output) + ">");
            Harness.Check(
                "W2 the trailing spaces after the value are still there",
                output.Contains("  =  22  \r\n"),
                "value tail missing from <" + Escape(output) + ">");
        }

        /// <summary>W3: a CRLF document never gains a bare LF.</summary>
        public static void TestW3_CrlfLineEndingsArePreserved()
        {
            const string input = "[record]\r\nfps=60\r\nquality=19\r\n[input]\r\nhotkey=Ctrl+Alt+Shift+S\r\n";
            const string expected = "[record]\r\nfps=60\r\nquality=22\r\n[input]\r\nhotkey=Ctrl+Alt+Shift+S\r\n";

            string output = ConfigWriter.SetValue(input, RecordSection, QualityKey, "22");

            Harness.Check("W3 the CRLF document is replaced exactly", output == expected, Describe(expected, output));
            Harness.Check("W3 no bare LF was introduced", BareLfCount(output) == 0,
                "bare LF count " + BareLfCount(output) + " in <" + Escape(output) + ">");
            Harness.Check("W3 no CR was dropped", CountOf(output, "\n") == 5,
                "LF count " + CountOf(output, "\n"));
        }

        /// <summary>W4: an LF document never gains a CR.</summary>
        public static void TestW4_LfLineEndingsArePreserved()
        {
            const string input = "[record]\nfps=60\nquality=19\n[input]\nhotkey=Ctrl+Alt+Shift+S\n";
            const string expected = "[record]\nfps=60\nquality=22\n[input]\nhotkey=Ctrl+Alt+Shift+S\n";

            string output = ConfigWriter.SetValue(input, RecordSection, QualityKey, "22");

            Harness.Check("W4 the LF document is replaced exactly", output == expected, Describe(expected, output));
            Harness.Check("W4 no CR was introduced", output.IndexOf('\r') < 0,
                "CR at " + output.IndexOf('\r') + " in <" + Escape(output) + ">");
        }

        /// <summary>
        /// W5: a key absent from an existing section is appended inside that
        /// section - after its last key, before the next header.
        /// </summary>
        public static void TestW5_KeyMissingFromSectionIsAppendedInsideIt()
        {
            const string input =
                "[record]\r\n"
                + "fps=60\r\n"
                + "ffmpeg_path=\r\n"
                + "[input]\r\n"
                + "hotkey=Ctrl+Alt+Shift+S\r\n";
            const string expected =
                "[record]\r\n"
                + "fps=60\r\n"
                + "ffmpeg_path=\r\n"
                + "quality=22\r\n"
                + "[input]\r\n"
                + "hotkey=Ctrl+Alt+Shift+S\r\n";

            string output = ConfigWriter.SetValue(input, RecordSection, QualityKey, "22");

            Harness.Check("W5 the key is inserted at the end of its own section",
                output == expected, Describe(expected, output));
            Harness.Check("W5 the following section is still after the new key",
                output.IndexOf("quality=22", StringComparison.Ordinal)
                    < output.IndexOf("[input]", StringComparison.Ordinal),
                "line order in <" + Escape(output) + ">");
            Harness.Check("W5 only one line was added", CountOf(output, "quality=") == 1,
                "quality lines " + CountOf(output, "quality="));
        }

        /// <summary>W6: a missing section is appended at EOF, header then key.</summary>
        public static void TestW6_MissingSectionIsAppendedAtEndOfFile()
        {
            const string input = "[record]\r\nfps=60\r\n";
            const string expected = "[record]\r\nfps=60\r\n[input]\r\nhotkey=Ctrl+Alt+Shift+F9\r\n";

            string output = ConfigWriter.SetValue(input, "input", "hotkey", "Ctrl+Alt+Shift+F9");

            Harness.Check("W6 the missing section and key are appended at EOF",
                output == expected, Describe(expected, output));
            Harness.Check("W6 the header precedes its key",
                output.IndexOf("[input]", StringComparison.Ordinal)
                    < output.IndexOf("hotkey=Ctrl+Alt+Shift+F9", StringComparison.Ordinal),
                "line order in <" + Escape(output) + ">");
            Harness.Check("W6 the appended lines use the document's CRLF",
                output.EndsWith("hotkey=Ctrl+Alt+Shift+F9\r\n", StringComparison.Ordinal),
                "tail <" + Escape(Tail(output, 40)) + ">");
        }

        /// <summary>
        /// W7: the same key in a different section is a different setting and must
        /// not be touched.
        /// </summary>
        public static void TestW7_KeyInAnotherSectionIsNotTouched()
        {
            const string input = "[record]\r\nquality=19\r\n[input]\r\nquality=30\r\n";
            const string expected = "[record]\r\nquality=22\r\n[input]\r\nquality=30\r\n";

            string output = ConfigWriter.SetValue(input, RecordSection, QualityKey, "22");

            Harness.Check("W7 only the [record] quality changes",
                output == expected, Describe(expected, output));
            Harness.Check("W7 the [input] quality is still 30",
                output.EndsWith("[input]\r\nquality=30\r\n", StringComparison.Ordinal),
                "tail <" + Escape(Tail(output, 40)) + ">");
        }

        /// <summary>W8: writing the same value twice is the same text as once.</summary>
        public static void TestW8_SettingTheSameValueTwiceIsIdempotent()
        {
            string once = ConfigWriter.SetValue(DesignSample, RecordSection, QualityKey, "22");
            string twice = ConfigWriter.SetValue(once, RecordSection, QualityKey, "22");

            Harness.Check("W8 the second write is byte-identical to the first",
                twice == once, Describe(once, twice));
            Harness.Check("W8 the result still holds exactly one quality line",
                CountOf(twice, "quality=") == 1, "quality lines " + CountOf(twice, "quality="));
            Harness.Check("W8 the result is still the hand-written expectation",
                twice == DesignSampleWith22, Describe(DesignSampleWith22, twice));
        }

        /// <summary>
        /// W9: a leading UTF-8 byte-order mark is preserved and is not doubled.
        /// Notepad offers "UTF-8 with BOM", so this is a real file state.
        /// </summary>
        public static void TestW9_ByteOrderMarkIsPreserved()
        {
            const string input = "\uFEFF[record]\r\nquality=19\r\n";
            const string expected = "\uFEFF[record]\r\nquality=22\r\n";

            string output = ConfigWriter.SetValue(input, RecordSection, QualityKey, "22");

            Harness.Check("W9 the BOM stays at the front and the value changed",
                output == expected, Describe(expected, output));
            Harness.Check("W9 no second BOM is added", CountOf(output, "\uFEFF") == 1,
                "BOM count " + CountOf(output, "\uFEFF"));
            Harness.Check("W9 the section is still recognised behind the BOM",
                output.Contains("quality=22"), "value missing from <" + Escape(output) + ">");
        }

        /// <summary>
        /// W10: malformed or empty input never throws and behaves as "append".
        /// A key before any header belongs to no section (Config.Parse notes it),
        /// so it must not be mistaken for the [record] setting.
        /// </summary>
        public static void TestW10_MalformedOrEmptyTextIsTreatedAsAppend()
        {
            string nullText = ConfigWriter.SetValue(null, RecordSection, QualityKey, "22");
            Harness.Check("W10 null text yields a fresh section and key",
                nullText == "[record]\r\nquality=22\r\n", Describe("[record]\r\nquality=22\r\n", nullText));

            string emptyText = ConfigWriter.SetValue(string.Empty, RecordSection, QualityKey, "22");
            Harness.Check("W10 empty text yields a fresh section and key",
                emptyText == "[record]\r\nquality=22\r\n", Describe("[record]\r\nquality=22\r\n", emptyText));

            const string headerless = "quality=19\r\n";
            string orphan = ConfigWriter.SetValue(headerless, RecordSection, QualityKey, "22");
            const string orphanExpected = "quality=19\r\n[record]\r\nquality=22\r\n";
            Harness.Check("W10 a key before any header is left alone and not edited",
                orphan == orphanExpected, Describe(orphanExpected, orphan));

            const string unclosed = "[record\r\nquality=19\r\n";
            string unclosedOutput = ConfigWriter.SetValue(unclosed, RecordSection, QualityKey, "22");
            const string unclosedExpected = "[record\r\nquality=19\r\n[record]\r\nquality=22\r\n";
            Harness.Check("W10 an unclosed header is not a section and is left alone",
                unclosedOutput == unclosedExpected, Describe(unclosedExpected, unclosedOutput));

            const string noTrailingEol = "[record]\r\nfps=60";
            string extended = ConfigWriter.SetValue(noTrailingEol, "input", "hotkey", "Ctrl+Alt+Shift+F9");
            const string extendedExpected =
                "[record]\r\nfps=60\r\n[input]\r\nhotkey=Ctrl+Alt+Shift+F9\r\n";
            Harness.Check("W10 a file with no final newline gains one before the append",
                extended == extendedExpected, Describe(extendedExpected, extended));
        }

        /// <summary>
        /// W11: a duplicated key inside one section is edited at its first
        /// occurrence only. Config.Parse resolves duplicates last-wins, so editing
        /// the first is what a user who reads the top-down sees; editing both
        /// would be a second, unrequested change.
        /// </summary>
        public static void TestW11_FirstDuplicateKeyInSectionIsReplaced()
        {
            const string input = "[record]\r\nquality=19\r\nquality=19\r\n";
            const string expected = "[record]\r\nquality=22\r\nquality=19\r\n";

            string output = ConfigWriter.SetValue(input, RecordSection, QualityKey, "22");

            Harness.Check("W11 only the first duplicate changes",
                output == expected, Describe(expected, output));
        }

        /// <summary>
        /// W12: the section and the key match ignoring case, as Config.Parse compares
        /// them, and the file's own spelling survives. A writer that matched
        /// case-sensitively would silently append a second key (making the file hold
        /// both 19 and 22), and one that rewrote the line would change
        /// "QUALITY" to "quality".
        /// </summary>
        public static void TestW12_SectionAndKeyMatchIgnoringCase()
        {
            const string input = "[Record]\r\nQUALITY=19\r\n[input]\r\nhotkey=Ctrl+Alt+Shift+S\r\n";
            const string expected = "[Record]\r\nQUALITY=22\r\n[input]\r\nhotkey=Ctrl+Alt+Shift+S\r\n";

            string output = ConfigWriter.SetValue(input, "RECORD", "Quality", "22");

            Harness.Check("W12 mixed-case section and key are found and the spelling kept",
                output == expected, Describe(expected, output));
            Harness.Check("W12 no second quality line was appended",
                CountOf(output.ToLowerInvariant(), "quality=") == 1,
                "quality lines " + CountOf(output.ToLowerInvariant(), "quality="));
        }

        /// <summary>Renders the first difference between two documents.</summary>
        /// <param name="expected">Hand-written expectation.</param>
        /// <param name="actual">What the writer produced.</param>
        /// <returns>Empty when equal, otherwise the offset and a window around it.</returns>
        /// <remarks>
        /// Side effects: none. Printed only on failure, so a near-miss (one digit,
        /// or one line ending) is readable instead of "expected != actual".
        /// </remarks>
        private static string Describe(string expected, string actual)
        {
            if (string.Equals(expected, actual, StringComparison.Ordinal))
            {
                return string.Empty;
            }

            int limit = Math.Min(expected.Length, actual.Length);
            int index = 0;
            while (index < limit && expected[index] == actual[index])
            {
                index++;
            }

            return "first difference at " + index
                + ": expected <" + Window(expected, index) + ">"
                + " actual <" + Window(actual, index) + ">";
        }

        /// <summary>Returns a readable slice of <paramref name="text"/> around one offset.</summary>
        /// <param name="text">Document to slice.</param>
        /// <param name="index">Offset to centre on.</param>
        /// <returns>At most 40 characters, with CR and LF escaped.</returns>
        /// <remarks>Side effects: none.</remarks>
        private static string Window(string text, int index)
        {
            int start = Math.Max(0, index - 20);
            int length = Math.Min(text.Length - start, 40);
            if (length <= 0)
            {
                return string.Empty;
            }
            return Escape(text.Substring(start, length));
        }

        /// <summary>Returns the last <paramref name="length"/> characters, escaped.</summary>
        /// <param name="text">Document to slice.</param>
        /// <param name="length">Maximum number of characters.</param>
        /// <returns>An ASCII-only tail for a failure message.</returns>
        /// <remarks>Side effects: none.</remarks>
        private static string Tail(string text, int length)
        {
            return Escape(text.Substring(Math.Max(0, text.Length - length)));
        }

        /// <summary>Escapes CR and LF so a failure message stays on one line.</summary>
        /// <param name="text">Text to escape; null is treated as empty.</param>
        /// <returns>The text with "\r" and "\n" spelled out.</returns>
        /// <remarks>Side effects: none.</remarks>
        private static string Escape(string text)
        {
            if (text == null)
            {
                return string.Empty;
            }
            return text.Replace("\r", "\\r").Replace("\n", "\\n");
        }

        /// <summary>Counts non-overlapping occurrences of <paramref name="needle"/>.</summary>
        /// <param name="text">Text to search; null yields 0.</param>
        /// <param name="needle">Non-empty literal to count.</param>
        /// <returns>The number of occurrences.</returns>
        /// <remarks>Side effects: none.</remarks>
        private static int CountOf(string text, string needle)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle))
            {
                return 0;
            }
            int count = 0;
            int index = text.IndexOf(needle, StringComparison.Ordinal);
            while (index >= 0)
            {
                count++;
                index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
            }
            return count;
        }

        /// <summary>Counts '\n' characters not preceded by '\r'.</summary>
        /// <param name="text">Text to inspect.</param>
        /// <returns>The number of bare LF line endings.</returns>
        /// <remarks>Side effects: none.</remarks>
        private static int BareLfCount(string text)
        {
            int count = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n' && (i == 0 || text[i - 1] != '\r'))
                {
                    count++;
                }
            }
            return count;
        }
    }
}
