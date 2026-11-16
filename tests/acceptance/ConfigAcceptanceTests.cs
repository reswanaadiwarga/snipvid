// Verifier-written acceptance tests for WO-04 (config + hotkey spec).
//
// Independent of tests\ConfigTests.cs: this file is compiled with src\Logic\*.cs
// and tests\Harness.cs only. Every expected value below is transcribed from the
// spec, not from Config's output:
//   * DESIGN.md section 8  - keys, sample file, defaults, "invalid values fall
//     back to defaults and are reported once", unknown keys ignored, fps=60 only,
//     hotkey needs >= 1 modifier from Ctrl/Alt/Shift/Win
//   * PLAN.md D1          - 60 fps only; 120 rejected with one note
//   * PLAN.md P1          - default chord Ctrl+Alt+Shift+S
//   * PLAN.md P2          - folder %USERPROFILE%\Videos\Screen Recordings
//   * WO-04 C4            - NVENC -cq range is 1..51
//   * Win32               - MOD_ALT=1 MOD_CONTROL=2 MOD_SHIFT=4 MOD_WIN=8,
//                           VK_S=0x53, VK_F9=0x78, VK_F1=0x70, VK_F24=0x87
//
// The injected profile is a path that does not exist on this machine, so an
// implementation that read the real %USERPROFILE% cannot pass by coincidence.
using System;
using SnipVid.Logic;

namespace SnipVid.Tests
{
    /// <summary>Independent acceptance checks for WO-04 criteria C1..C11 plus no-throw fuzzing.</summary>
    public static class ConfigAcceptanceTests
    {
        /// <summary>Injected profile: deliberately nonexistent on this machine.</summary>
        private const string Profile = @"Z:\no-such-profile-v04";

        /// <summary>DESIGN.md section 8 default folder with the injected profile applied.</summary>
        private const string DefaultFolder = @"Z:\no-such-profile-v04\Videos\Screen Recordings";

        /// <summary>P1 default chord text.</summary>
        private const string DefaultChord = "Ctrl+Alt+Shift+S";

        /// <summary>C1: empty input gives every DESIGN section 8 default and no notes.</summary>
        public static void TestC1_EmptyInputGivesSpecDefaults()
        {
            Config c = Config.Parse(string.Empty, Profile);

            Harness.Check("AC-C1 fps default is 60 (DESIGN 8, D1)", c.Fps == 60, "got " + c.Fps);
            Harness.Check("AC-C1 quality default is 19 (DESIGN 5 and 8)", c.Quality == 19, "got " + c.Quality);
            Harness.Check("AC-C1 folder default is <profile>\\Videos\\Screen Recordings (DESIGN 8, P2)",
                c.Folder == DefaultFolder, "got '" + c.Folder + "'");
            Harness.Check("AC-C1 ffmpeg_path default is empty (DESIGN 8, auto-resolve)",
                c.FfmpegPath == string.Empty, "got '" + c.FfmpegPath + "'");
            Harness.Check("AC-C1 hotkey default is " + DefaultChord + " (P1)",
                c.Hotkey != null && c.Hotkey.ToString() == DefaultChord,
                "got " + (c.Hotkey == null ? "<null>" : c.Hotkey.ToString()));
            Harness.Check("AC-C1 default chord mask is Ctrl|Alt|Shift = 2|1|4 = 7 and vk 0x53",
                c.Hotkey != null && c.Hotkey.Modifiers == 7 && c.Hotkey.VirtualKey == 0x53,
                "mods " + (c.Hotkey == null ? -1 : c.Hotkey.Modifiers)
                + ", vk " + (c.Hotkey == null ? -1 : c.Hotkey.VirtualKey));
            Harness.Check("AC-C1 empty input produces zero notes",
                c.Notes != null && c.Notes.Count == 0, "note count " + (c.Notes == null ? -1 : c.Notes.Count));
        }

        /// <summary>C1b: the exact file DESIGN.md section 8 prints parses to those defaults, silently.</summary>
        public static void TestC1b_DesignSampleTextParsesSilently()
        {
            string ini =
                "[record]\r\n" +
                "fps=60\r\n" +
                "quality=19\r\n" +
                "folder=%USERPROFILE%\\Videos\\Screen Recordings\r\n" +
                "ffmpeg_path=\r\n" +
                "[input]\r\n" +
                "hotkey=Ctrl+Alt+Shift+S\r\n";

            Config c = Config.Parse(ini, Profile);

            Harness.Check("AC-C1b the DESIGN 8 sample yields 60 / 19 / <profile>\\Videos\\Screen Recordings / empty path / Ctrl+Alt+Shift+S",
                c.Fps == 60
                && c.Quality == 19
                && c.Folder == DefaultFolder
                && c.FfmpegPath == string.Empty
                && c.Hotkey != null
                && c.Hotkey.ToString() == DefaultChord,
                "fps " + c.Fps + ", quality " + c.Quality + ", folder '" + c.Folder
                + "', path '" + c.FfmpegPath + "', hotkey "
                + (c.Hotkey == null ? "<null>" : c.Hotkey.ToString()));
            Harness.Check("AC-C1b the DESIGN 8 sample produces zero notes",
                c.Notes != null && c.Notes.Count == 0,
                "note count " + (c.Notes == null ? -1 : c.Notes.Count));
        }

        /// <summary>C2: fps=120 is rejected with exactly one note (AC9, D1).</summary>
        public static void TestC2_Fps120RejectedExactlyOneNote()
        {
            Config c = Config.Parse("[record]\nfps=120\n", Profile);

            Harness.Check("AC-C2 fps=120 leaves fps at 60 (D1)", c.Fps == 60, "got " + c.Fps);
            Harness.Check("AC-C2 fps=120 gives exactly one note", c.Notes.Count == 1,
                "note count " + c.Notes.Count);
        }

        /// <summary>
        /// C3: fps=30 is accepted explicitly after the D1 amendment - 60 stays the
        /// default, 30 is a labelled choice for 30 fps sources, and anything that is
        /// neither 30 nor 60 is still rejected rather than quietly applied.
        /// </summary>
        public static void TestC3_Fps30AcceptedExactlyNoNotes()
        {
            Config thirty = Config.Parse("[record]\nfps=30\n", Profile);
            Harness.Check("AC-C3 fps=30 is applied as 30 (D1 amended)", thirty.Fps == 30,
                "got " + thirty.Fps);
            Harness.Check("AC-C3 fps=30 produces no note", thirty.Notes.Count == 0,
                "note count " + thirty.Notes.Count);

            Config odd = Config.Parse("[record]\nfps=24\n", Profile);
            Harness.Check("AC-C3 fps=24 is still rejected to 60 with exactly one note",
                odd.Fps == 60 && odd.Notes.Count == 1,
                "fps " + odd.Fps + ", note count " + odd.Notes.Count);
        }

        /// <summary>C4: quality accepts 1..51 inclusive, rejects 0 and 52 with one note each.</summary>
        public static void TestC4_QualityRange()
        {
            Config zero = Config.Parse("[record]\nquality=0\n", Profile);
            Harness.Check("AC-C4 quality=0 falls back to 19 with exactly one note",
                zero.Quality == 19 && zero.Notes.Count == 1,
                "quality " + zero.Quality + ", note count " + zero.Notes.Count);

            Config above = Config.Parse("[record]\nquality=52\n", Profile);
            Harness.Check("AC-C4 quality=52 falls back to 19 with exactly one note",
                above.Quality == 19 && above.Notes.Count == 1,
                "quality " + above.Quality + ", note count " + above.Notes.Count);

            Config top = Config.Parse("[record]\nquality=51\n", Profile);
            Harness.Check("AC-C4 quality=51 is accepted (inclusive upper bound) with zero notes",
                top.Quality == 51 && top.Notes.Count == 0,
                "quality " + top.Quality + ", note count " + top.Notes.Count);

            Config bottom = Config.Parse("[record]\nquality=1\n", Profile);
            Harness.Check("AC-C4 quality=1 is accepted (inclusive lower bound) with zero notes",
                bottom.Quality == 1 && bottom.Notes.Count == 0,
                "quality " + bottom.Quality + ", note count " + bottom.Notes.Count);

            Config middle = Config.Parse("[record]\nquality=31\n", Profile);
            Harness.Check("AC-C4 quality=31 is accepted verbatim with zero notes",
                middle.Quality == 31 && middle.Notes.Count == 0,
                "quality " + middle.Quality + ", note count " + middle.Notes.Count);
        }

        /// <summary>C5: an unknown key is ignored with no note (DESIGN section 8 forward compatibility).</summary>
        public static void TestC5_UnknownKeyIgnoredNoNote()
        {
            Config c = Config.Parse("[record]\ncolour=blue\n", Profile);

            Harness.Check("AC-C5 unknown key colour=blue gives zero notes",
                c.Notes.Count == 0, "note count " + c.Notes.Count);
            Harness.Check("AC-C5 unknown key colour=blue changes no setting",
                c.Fps == 60 && c.Quality == 19 && c.Folder == DefaultFolder
                && c.FfmpegPath == string.Empty && c.Hotkey.ToString() == DefaultChord,
                "fps " + c.Fps + ", quality " + c.Quality + ", folder '" + c.Folder + "'");
        }

        /// <summary>C6: comments, blanks, padded key/value, CRLF and LF parse identically.</summary>
        public static void TestC6_SyntaxVariants()
        {
            string crlf =
                "; semicolon comment\r\n" +
                "# hash comment\r\n" +
                "\r\n" +
                "[record]\r\n" +
                "   quality   =   30   \r\n" +
                "[input]\r\n" +
                "\thotkey = Alt+F9\t\r\n";
            string lf = crlf.Replace("\r\n", "\n");

            Config a = Config.Parse(crlf, Profile);
            Config b = Config.Parse(lf, Profile);

            // Absolute expectations first, so "both ignored the file" cannot pass.
            Harness.Check("AC-C6 padded CRLF input yields quality 30 and hotkey Alt+F9 with zero notes",
                a.Quality == 30 && a.Hotkey.ToString() == "Alt+F9" && a.Notes.Count == 0,
                "quality " + a.Quality + ", hotkey " + a.Hotkey + ", notes " + a.Notes.Count);
            Harness.Check("AC-C6 the LF twin yields the same quality 30 and Alt+F9 with zero notes",
                b.Quality == 30 && b.Hotkey.ToString() == "Alt+F9" && b.Notes.Count == 0,
                "quality " + b.Quality + ", hotkey " + b.Hotkey + ", notes " + b.Notes.Count);
        }

        /// <summary>C7: %USERPROFILE% comes from the argument, not the process environment.</summary>
        public static void TestC7_ProfileIsInjected()
        {
            Config expanded = Config.Parse("[record]\nfolder=%USERPROFILE%\\Clips\n", Profile);

            Harness.Check("AC-C7 folder=%USERPROFILE%\\Clips expands to the injected profile",
                expanded.Folder == @"Z:\no-such-profile-v04\Clips" && expanded.Notes.Count == 0,
                "folder '" + expanded.Folder + "', note count " + expanded.Notes.Count);

            Config defaultFromEmptyProfile = Config.Parse(string.Empty, @"\\?\Q:\another-phantom");
            Harness.Check("AC-C7 the default folder also follows the injected profile",
                defaultFromEmptyProfile.Folder == @"\\?\Q:\another-phantom\Videos\Screen Recordings",
                "folder '" + defaultFromEmptyProfile.Folder + "'");

            // Windows environment variable names are case-insensitive, so the
            // lowercase spelling of the same token must resolve to the same value.
            Config lower = Config.Parse("[record]\nfolder=%userprofile%\\Clips\n", Profile);
            Harness.Check("AC-C7 %userprofile% (lowercase) expands from the injected profile",
                lower.Folder == @"Z:\no-such-profile-v04\Clips",
                "folder '" + lower.Folder + "'");
        }

        /// <summary>C8: an unparseable hotkey falls back to the default with exactly one note.</summary>
        public static void TestC8_BadHotkeyRejected()
        {
            Config c = Config.Parse("[input]\nhotkey=Ctrl+Banana\n", Profile);

            Harness.Check("AC-C8 hotkey=Ctrl+Banana falls back to " + DefaultChord,
                c.Hotkey.ToString() == DefaultChord, "got " + c.Hotkey);
            Harness.Check("AC-C8 hotkey=Ctrl+Banana gives exactly one note",
                c.Notes.Count == 1, "note count " + c.Notes.Count);

            HotkeySpec spec;
            bool ok;
            bool threw = false;
            try { ok = HotkeySpec.TryParse("Ctrl+Banana", out spec); }
            catch (Exception) { ok = true; spec = null; threw = true; }

            Harness.Check("AC-C8 TryParse(\"Ctrl+Banana\") returns false, does not throw, and nulls the out value",
                !threw && !ok && spec == null,
                threw ? "threw" : ("ok " + ok + ", spec " + (spec == null ? "null" : spec.ToString())));
        }

        /// <summary>C9: a key before any [section] is ignored with exactly one note.</summary>
        public static void TestC9_KeyBeforeSection()
        {
            Config c = Config.Parse("folder=C:\\before\n[record]\nquality=30\n", Profile);

            Harness.Check("AC-C9 a key before the first [section] yields exactly one note",
                c.Notes.Count == 1, "note count " + c.Notes.Count);
            Harness.Check("AC-C9 that key is not applied", c.Folder == DefaultFolder,
                "folder '" + c.Folder + "'");
            Harness.Check("AC-C9 the rest of the file is still parsed", c.Quality == 30,
                "quality " + c.Quality);
        }

        /// <summary>C10: ffmpeg_path is stored verbatim with no validation here.</summary>
        public static void TestC10_FfmpegPathVerbatim()
        {
            Config c = Config.Parse("[record]\nffmpeg_path=C:\\nope\\ffmpeg.exe\n", Profile);

            Harness.Check("AC-C10 ffmpeg_path is stored verbatim",
                c.FfmpegPath == @"C:\nope\ffmpeg.exe", "got '" + c.FfmpegPath + "'");
            Harness.Check("AC-C10 ffmpeg_path produces zero notes (WO-12 validates existence)",
                c.Notes.Count == 0, "note count " + c.Notes.Count);
        }

        /// <summary>C11: a chord with no modifier is rejected with one note and the default is used.</summary>
        public static void TestC11_ZeroModifierRejected()
        {
            string[] bare = new string[] { "S", "F9", "5", "s" };
            foreach (string text in bare)
            {
                Config c = Config.Parse("[input]\nhotkey=" + text + "\n", Profile);

                Harness.Check("AC-C11 hotkey=" + text + " is rejected, default chord used, exactly one note",
                    c.Hotkey.ToString() == DefaultChord && c.Notes.Count == 1,
                    "hotkey " + c.Hotkey + ", note count " + c.Notes.Count);
            }

            // Within the chord grammar, no modifier must never produce a spec.
            string[] noModifier = new string[]
            {
                "Alt+S+Shift", // has a modifier - control case, must parse
                "F24",
                "Ctrl+Alt+Shift", // modifier only, no key
                "Ctrl+",
                "+S",
                "Ctrl+Alt+Shift+S+Z", // two keys
            };

            HotkeySpec spec;
            bool control = HotkeySpec.TryParse("Alt+S+Shift", out spec);
            bool f24 = HotkeySpec.TryParse("F24", out spec);
            bool modsOnly = HotkeySpec.TryParse("Ctrl+Alt+Shift", out spec);
            bool trailing = HotkeySpec.TryParse("Ctrl+", out spec);
            bool leading = HotkeySpec.TryParse("+S", out spec);
            bool twoKeys = HotkeySpec.TryParse("Ctrl+Alt+Shift+S+Z", out spec);

            Harness.Check("AC-C11 Alt+S+Shift (one modifier, any order) is accepted",
                control, "rejected");
            Harness.Check("AC-C11 F24 (bare function key) is rejected: no function-key exception",
                !f24, "accepted");
            Harness.Check("AC-C11 modifier-only, empty-key and leading/trailing '+' chords are rejected",
                !modsOnly && !trailing && !leading, "one was accepted");
            Harness.Check("AC-C11 two key tokens are rejected rather than guessing",
                !twoKeys, "accepted");
        }

        /// <summary>Note counts are exactly one per rejected value, not "at least one".</summary>
        public static void TestNoteCountIsExact()
        {
            Config two = Config.Parse("[record]\nfps=120\nquality=52\n", Profile);
            Harness.Check("AC-N two different invalid values produce exactly two notes",
                two.Notes.Count == 2, "note count " + two.Notes.Count);

            Config duplicated = Config.Parse("[record]\nquality=0\nquality=0\n", Profile);
            Harness.Check("AC-N the same invalid value written twice produces two notes, one per line",
                duplicated.Notes.Count == 2, "note count " + duplicated.Notes.Count);

            Config clean = Config.Parse("[record]\nfps=60\nquality=19\nffmpeg_path=\n[input]\nhotkey=Ctrl+Alt+Shift+S\n", Profile);
            Harness.Check("AC-N every valid value produces zero notes",
                clean.Notes.Count == 0, "note count " + clean.Notes.Count);

            bool allNonEmpty = true;
            foreach (string note in two.Notes)
            {
                if (string.IsNullOrEmpty(note)) { allNonEmpty = false; }
            }
            Harness.Check("AC-N every note is a non-empty human-readable line", allNonEmpty, "an empty note was present");
        }

        /// <summary>MOD_* bit values are the Win32 ones, so RegisterHotKey can take them unchanged.</summary>
        public static void TestModifierConstantsAreWin32()
        {
            Harness.Check("AC-MOD HotkeySpec mod consts are MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8",
                HotkeySpec.ModAlt == 1 && HotkeySpec.ModControl == 2
                && HotkeySpec.ModShift == 4 && HotkeySpec.ModWin == 8,
                "got " + HotkeySpec.ModAlt + "/" + HotkeySpec.ModControl + "/"
                + HotkeySpec.ModShift + "/" + HotkeySpec.ModWin);

            HotkeySpec spec;
            bool ok = HotkeySpec.TryParse("Ctrl+Alt+Shift+F9", out spec);
            Harness.Check("AC-MOD the fallback chord Ctrl+Alt+Shift+F9 is 7 / 0x78 (P1, DESIGN 9)",
                ok && spec != null && spec.Modifiers == 7 && spec.VirtualKey == 0x78,
                ok ? ("mods " + spec.Modifiers + ", vk 0x" + spec.VirtualKey.ToString("X2")) : "rejected");
        }

        /// <summary>Every garbage input must be total: no throw, and Note non-null.</summary>
        public static void TestFuzzNeverThrows()
        {
            string big = new string('9', 100 * 1024);
            string[] inputs = new string[]
            {
                null,
                "",
                "\n\n\n",
                "[record",                                  // unclosed section header
                "[record]\rfps=120\rquality=0\r",           // lone CR line endings
                "[record]\nfps 60\n",                       // no '='
                "[record]\n=60\n",                          // '=' only
                "[record]\n[quality]=5\n",                  // key with a leading '['
                "[record]\nquality=" + big + "\n",          // 100 KB line
                "[record]\nquality=1\0\nfps=6\0\n",         // NUL characters
                "[record]\n\0\0\0\n",
                "[record]\nkey=\u00e9\u00e8\u00ea\n",       // non-ASCII garbage
                "[input]\nhotkey=\n",
                "[input]\nhotkey=++++\n",
                "[input]\nhotkey=" + new string('A', 5000) + "\n",
                "[]\n",
                "[=]\n",
                "]]][[[\n",
                "\r\n\r\n",
                "[record]\nquality=999999999999999999999999\n",
            };

            int index = 0;
            foreach (string input in inputs)
            {
                index++;
                Config c = null;
                Exception thrown = null;
                try { c = Config.Parse(input, Profile); }
                catch (Exception ex) { thrown = ex; }

                Harness.Check("AC-FZ input " + index + " does not throw",
                    thrown == null,
                    thrown == null ? "" : thrown.GetType().Name + ": " + thrown.Message);
                Harness.Check("AC-FZ input " + index + " yields a non-null config and non-null notes",
                    c != null && c.Notes != null && c.Hotkey != null && c.Folder != null
                    && c.FfmpegPath != null,
                    c == null ? "config was null" : "a member was null");
            }
        }

        /// <summary>Each malformed shape lands on a stated rule: a note, or documented silence.</summary>
        public static void TestFuzzMatchesStatedRules()
        {
            Config unclosed = Config.Parse("[record\n", Profile);
            Harness.Check("AC-FZ unclosed [record header is reported by one note",
                unclosed.Notes.Count == 1, "note count " + unclosed.Notes.Count);

            Config noSection = Config.Parse("fps=120\n", Profile);
            Harness.Check("AC-FZ a known key before any section is reported by one note (C9)",
                noSection.Notes.Count == 1 && noSection.Fps == 60,
                "note count " + noSection.Notes.Count + ", fps " + noSection.Fps);

            Config unknownSection = Config.Parse("[ui]\nquality=5\n", Profile);
            Harness.Check("AC-FZ an unknown section is ignored silently (no note, no effect)",
                unknownSection.Notes.Count == 0 && unknownSection.Quality == 19,
                "note count " + unknownSection.Notes.Count + ", quality " + unknownSection.Quality);

            Config unknownKey = Config.Parse("[record]\ncolour=blue\n", Profile);
            Harness.Check("AC-FZ an unknown key is ignored silently (DESIGN 8)",
                unknownKey.Notes.Count == 0, "note count " + unknownKey.Notes.Count);
        }
    }
}
