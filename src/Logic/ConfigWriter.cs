// ConfigWriter - the one place config.ini text is edited (DESIGN.md section 8).
//
// WHY this is a separate file from Config.cs: Config.Parse only reads, and it is
// frozen because every rule in it has a test that depends on the exact wording of
// its notes. The overlay's quality picker has to write one value back into a file
// the user hand-edits, and that write must leave every other byte alone -
// comments, unknown keys, blank lines, the key's spelling, the spacing around
// '=', the document's line endings and an optional UTF-8 byte-order mark. That is
// a pure string transformation, so it lives in src\Logic and is unit-tested; the
// read and the byte-level write stay in ConfigFile, next to the app, where the
// filesystem is allowed (DESIGN.md section 11).
//
// The rules, in the order the function applies them:
//   1. find "[section]" ignoring case;
//   2. inside it, find the first "key = value" line ignoring the key's case;
//   3. replace only the value's text, keeping everything before and after it;
//   4. a key missing from an existing section is appended at the end of that
//      section - after its last line, before the next header or EOF;
//   5. a missing section is appended at the end of the file: header, then key.
//
// Malformed input is not an error here. Config.Parse already turns a malformed
// file into defaults plus notes; making the writer throw on the same file would
// mean a user with one typo could not change the quality preset at all. So an
// unclosed header, a key before any header and a duplicated key all resolve the
// same way Config.Parse resolves them: the header that is not a header is not a
// section, the orphan key belongs to no section, and the first duplicate is the
// one edited.
//
// No WPF, no P/Invoke, no I/O, no clock, no Environment: test.ps1 compiles this
// file without any Windows reference (DESIGN.md section 11).
using System;
using System.Collections.Generic;
using System.Text;

namespace SnipVid.Logic
{
    /// <summary>
    /// Rewrites one value in hand-editable INI text without disturbing the rest of
    /// the document.
    /// </summary>
    /// <remarks>
    /// Pure and total: no I/O, no state, no exception for any input. The caller
    /// owns reading and writing the file; this type only owns what the bytes mean.
    /// </remarks>
    public static class ConfigWriter
    {
        /// <summary>Line ending used when the document has none to copy.</summary>
        /// <remarks>
        /// CRLF because this is a Windows/Notepad file (ConfigFile writes the
        /// sample with CRLF); a document that already has a line ending always
        /// keeps its own, so this only decides the very first line of an empty file.
        /// </remarks>
        private const string DefaultLineEnding = "\r\n";

        /// <summary>
        /// Sets one key's value in one section, leaving every other byte alone.
        /// </summary>
        /// <param name="iniText">
        /// Whole file content; null is treated as empty. CRLF and LF are preserved,
        /// and one leading UTF-8 byte-order mark is kept in front of the result.
        /// </param>
        /// <param name="section">
        /// Section name without brackets, matched ignoring case. Null or empty
        /// returns the document unchanged: there is nothing nameable to write.
        /// </param>
        /// <param name="key">
        /// Key name, matched ignoring case. Null or empty returns the document
        /// unchanged, for the same reason. A null <paramref name="value"/> is
        /// written as an empty value.
        /// </param>
        /// <param name="value">New value text, written verbatim.</param>
        /// <returns>
        /// The new document: the value replaced in place, or the key appended to its
        /// section, or the section and key appended at EOF. An appended line always
        /// ends with the document's own line ending, so the file stays appendable.
        /// </returns>
        /// <remarks>
        /// Side effects: none. Never throws: malformed text is handled as
        /// "not found, append", so a file with a typo can still be edited through
        /// the UI. When the section exists but the key does not, the key is placed
        /// before the next header rather than immediately after the last key, which
        /// keeps trailing blank lines and comments of that section where the user
        /// put them. The first duplicate key inside the section is the one changed:
        /// Config.Parse resolves duplicates last-wins, so editing the first leaves
        /// the user's later override intact and edits no more than was asked.
        /// </remarks>
        public static string SetValue(string iniText, string section, string key, string value)
        {
            string text = iniText ?? string.Empty;
            if (string.IsNullOrEmpty(section) || string.IsNullOrEmpty(key))
            {
                return text;
            }

            // The mark is held aside and re-attached, because the split below must
            // see "[record]" as the first line's text. U+FEFF is not whitespace to
            // .NET, so Trim() would keep it and the header would never match -
            // the same trap Config.Parse documents in StripByteOrderMark.
            bool hasByteOrderMark = text.Length > 0 && text[0] == '\uFEFF';
            if (hasByteOrderMark)
            {
                text = text.Substring(1);
            }

            List<Line> lines = SplitLines(text);
            string lineEnding = FirstLineEnding(lines);
            string newValue = value ?? string.Empty;

            int header = FindSection(lines, section);
            if (header < 0)
            {
                AppendSection(lines, section, key, newValue, lineEnding);
            }
            else
            {
                int sectionEnd = FindSectionEnd(lines, header);
                int keyLine = FindKey(lines, header + 1, sectionEnd, key);
                if (keyLine >= 0)
                {
                    lines[keyLine].Text = ReplaceValue(lines[keyLine].Text, newValue);
                }
                else
                {
                    InsertLine(lines, sectionEnd, key + "=" + newValue, lineEnding);
                }
            }

            string result = Join(lines);
            return hasByteOrderMark ? "\uFEFF" + result : result;
        }

        /// <summary>One line: its text without the terminator, and the terminator itself.</summary>
        /// <remarks>
        /// Splitting the text from its ending is what lets an edit keep CRLF and LF
        /// as the file had them, without one branch per operation. The last line of
        /// a file may have an empty <see cref="Ending"/>; every line before it does
        /// not, because that is how the split finds them.
        /// </remarks>
        private sealed class Line
        {
            /// <summary>Line content, excluding any CR/LF.</summary>
            internal string Text;

            /// <summary>The line's own terminator: "\r\n", "\n", or empty at EOF.</summary>
            internal string Ending;

            /// <summary>Stores one split line.</summary>
            /// <param name="text">Content without the terminator.</param>
            /// <param name="ending">The terminator that followed it, or empty.</param>
            /// <remarks>Side effects: none.</remarks>
            internal Line(string text, string ending)
            {
                Text = text;
                Ending = ending;
            }
        }

        /// <summary>Splits text into lines without assuming an ending convention.</summary>
        /// <param name="text">Document text; may be empty, never null here.</param>
        /// <returns>One entry per line, in order; empty for empty text.</returns>
        /// <remarks>
        /// Side effects: none. A CR is only removed when it immediately precedes the
        /// LF that ends that line, so a lone CR stays the file's own data.
        /// </remarks>
        private static List<Line> SplitLines(string text)
        {
            List<Line> lines = new List<Line>();
            int start = 0;
            while (start < text.Length)
            {
                int newline = text.IndexOf('\n', start);
                if (newline < 0)
                {
                    lines.Add(new Line(text.Substring(start), string.Empty));
                    break;
                }

                int contentEnd = newline;
                if (contentEnd > start && text[contentEnd - 1] == '\r')
                {
                    contentEnd--;
                }

                lines.Add(new Line(
                    text.Substring(start, contentEnd - start),
                    text.Substring(contentEnd, newline + 1 - contentEnd)));
                start = newline + 1;
            }
            return lines;
        }

        /// <summary>Rebuilds the document from its lines.</summary>
        /// <param name="lines">Lines with their own terminators.</param>
        /// <returns>The concatenated text.</returns>
        /// <remarks>Side effects: none.</remarks>
        private static string Join(List<Line> lines)
        {
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                text.Append(lines[i].Text);
                text.Append(lines[i].Ending);
            }
            return text.ToString();
        }

        /// <summary>Returns the line ending the document already uses.</summary>
        /// <param name="lines">Split document.</param>
        /// <returns>The first terminator found, or <see cref="DefaultLineEnding"/>.</returns>
        /// <remarks>
        /// Side effects: none. Taking the first rather than the majority means a
        /// hand-edited file that is mostly CRLF but starts with one LF keeps getting
        /// LF in that first spot instead of being rewritten; the point is to add no
        /// new convention, not to normalise the old one.
        /// </remarks>
        private static string FirstLineEnding(List<Line> lines)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Ending.Length > 0)
                {
                    return lines[i].Ending;
                }
            }
            return DefaultLineEnding;
        }

        /// <summary>Finds the section header matching <paramref name="section"/>.</summary>
        /// <param name="lines">Split document.</param>
        /// <param name="section">Section name without brackets.</param>
        /// <returns>Line index of the first match, or -1.</returns>
        /// <remarks>
        /// Side effects: none. Matching ignores case the way Windows file names do,
        /// which is also how Config.Parse compares them, so "[Record]" and
        /// "[record]" are one section to both sides of the file.
        /// </remarks>
        private static int FindSection(List<Line> lines, string section)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                string name;
                if (!TryHeaderName(lines[i].Text, out name))
                {
                    continue;
                }
                if (string.Equals(name, section, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>Finds where the section starting at <paramref name="header"/> ends.</summary>
        /// <param name="lines">Split document.</param>
        /// <param name="header">Index of the section's header line.</param>
        /// <returns>Index of the next header line, or the line count at EOF.</returns>
        /// <remarks>
        /// Side effects: none. A line without a closing ']' is not a header, exactly
        /// as in Config.Parse, so it does not end the section and a key appended
        /// after it still belongs to the section that is open.
        /// </remarks>
        private static int FindSectionEnd(List<Line> lines, int header)
        {
            for (int i = header + 1; i < lines.Count; i++)
            {
                string unused;
                if (TryHeaderName(lines[i].Text, out unused))
                {
                    return i;
                }
            }
            return lines.Count;
        }

        /// <summary>Finds the first key line in a range.</summary>
        /// <param name="lines">Split document.</param>
        /// <param name="from">First line index to inspect (inclusive).</param>
        /// <param name="to">Line index to stop at (exclusive).</param>
        /// <param name="key">Key name to match ignoring case.</param>
        /// <returns>The matching line index, or -1.</returns>
        /// <remarks>Side effects: none.</remarks>
        private static int FindKey(List<Line> lines, int from, int to, string key)
        {
            for (int i = from; i < to; i++)
            {
                string name = KeyOf(lines[i].Text);
                if (name != null && string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
                // KeyOf null means "not a key line": a comment, a blank line or a
                // header. Those cannot match, so the scan simply moves on.
            }
            return -1;
        }

        /// <summary>Extracts a section name from a header line.</summary>
        /// <param name="line">Raw line text.</param>
        /// <returns>True when the line is a header with a closing ']'; the name is trimmed.</returns>
        /// <remarks>
        /// Side effects: none. The name stops at the first ']', so a trailing comment
        /// after the bracket does not become part of the name - the same reading
        /// Config.Parse applies.
        /// </remarks>
        private static bool TryHeaderName(string line, out string name)
        {
            name = null;
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '[')
            {
                return false;
            }

            int close = trimmed.IndexOf(']');
            if (close < 0)
            {
                return false;
            }

            name = trimmed.Substring(1, close - 1).Trim();
            return true;
        }

        /// <summary>Extracts a key name from a key line.</summary>
        /// <param name="line">Raw line text.</param>
        /// <returns>
        /// The trimmed key, or null when the line is blank, a comment, a header, or
        /// has no '=' after the first character.
        /// </returns>
        /// <remarks>
        /// Side effects: none. '=' at the first non-space character means the line
        /// has no key at all; it is a typo Config.Parse notes and a line this writer
        /// must not confuse with "key =".
        /// </remarks>
        private static string KeyOf(string line)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                return null;
            }

            char first = trimmed[0];
            if (first == ';' || first == '#' || first == '[')
            {
                return null;
            }

            int separator = trimmed.IndexOf('=');
            if (separator <= 0)
            {
                return null;
            }
            return trimmed.Substring(0, separator).Trim();
        }

        /// <summary>Replaces only the value's text inside one key line.</summary>
        /// <param name="line">Raw key line that <see cref="KeyOf"/> accepted.</param>
        /// <param name="value">New value text, written verbatim.</param>
        /// <returns>
        /// The line with its leading whitespace, key spelling, spacing around '='
        /// and trailing spaces intact, and only the old value's text replaced.
        /// </returns>
        /// <remarks>
        /// Side effects: none. The split is "everything after '=' that is not
        /// whitespace is the value", so " quality = 19 " becomes " quality = 22 "
        /// rather than "quality=22": the writer must be invisible in the file's
        /// formatting, because the user owns that formatting. An empty value keeps
        /// the whitespace after '=' and appends the new text, which is what a
        /// human would type.
        /// </remarks>
        private static string ReplaceValue(string line, string value)
        {
            int separator = line.IndexOf('=');
            if (separator < 0)
            {
                // Unreachable for a line KeyOf accepted; returning the line unchanged
                // is still better than throwing inside a UI click handler.
                return line;
            }

            string rest = line.Substring(separator + 1);
            int lead = 0;
            while (lead < rest.Length && char.IsWhiteSpace(rest[lead]))
            {
                lead++;
            }
            int end = rest.Length;
            while (end > lead && char.IsWhiteSpace(rest[end - 1]))
            {
                end--;
            }

            StringBuilder rebuilt = new StringBuilder(line.Length + value.Length);
            rebuilt.Append(line, 0, separator + 1);
            rebuilt.Append(rest, 0, lead);
            rebuilt.Append(value);
            rebuilt.Append(rest, end, rest.Length - end);
            return rebuilt.ToString();
        }

        /// <summary>Inserts one new line at <paramref name="index"/>.</summary>
        /// <param name="lines">Split document.</param>
        /// <param name="index">Line index the new line takes; the line count means EOF.</param>
        /// <param name="text">New line content, no terminator.</param>
        /// <param name="lineEnding">Terminator to use when the previous line has none.</param>
        /// <remarks>
        /// Side effects: mutates <paramref name="lines"/>. When the preceding line is
        /// a final line that had no newline, it gets one first; otherwise the new
        /// line would be glued onto its end and turn "ffmpeg_path=" into
        /// "ffmpeg_path=quality=22".
        /// </remarks>
        private static void InsertLine(List<Line> lines, int index, string text, string lineEnding)
        {
            if (index > 0 && lines[index - 1].Ending.Length == 0)
            {
                lines[index - 1].Ending = lineEnding;
            }
            lines.Insert(index, new Line(text, lineEnding));
        }

        /// <summary>Appends a whole section and one key at EOF.</summary>
        /// <param name="lines">Split document.</param>
        /// <param name="section">Section name without brackets.</param>
        /// <param name="key">Key name.</param>
        /// <param name="value">Value text.</param>
        /// <param name="lineEnding">Terminator to use when the last line has none.</param>
        /// <remarks>
        /// Side effects: mutates <paramref name="lines"/>. Both new lines are
        /// terminated, so the file ends with a newline and a later append has a clean
        /// place to start. The header is written with the requested spelling, not the
        /// document's - there is no existing spelling to preserve.
        /// </remarks>
        private static void AppendSection(
            List<Line> lines, string section, string key, string value, string lineEnding)
        {
            if (lines.Count > 0 && lines[lines.Count - 1].Ending.Length == 0)
            {
                lines[lines.Count - 1].Ending = lineEnding;
            }
            lines.Add(new Line("[" + section + "]", lineEnding));
            lines.Add(new Line(key + "=" + value, lineEnding));
        }
    }
}
