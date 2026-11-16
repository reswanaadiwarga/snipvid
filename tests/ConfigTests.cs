// ConfigTests - WO-04: the hand-written INI parse of DESIGN.md section 8 and the
// hotkey chord of decision P1.
//
// Oracle provenance: the default block (60 / 19 / <profile>\Videos\Screen
// Recordings / Ctrl+Alt+Shift+S / empty ffmpeg_path) is transcribed from
// DESIGN.md section 8; the fps rejection is PLAN.md AC9 plus decision D1; the
// quality range is NVENC's -cq 1-51. Literals only - no expected value is taken
// from Config's own output, and the profile is spelled out with the injected
// argument (C:\Users\x) so an implementation that read this machine's real
// %USERPROFILE% (C:\Users\you) fails C1 and C7 instead of passing quietly.
//
// Note counts are asserted exactly, not as "at least one": a parser that reports
// one bad value twice, or that reports an unknown key DESIGN.md section 8
// requires to be ignored silently, passes an "at least one" check and fails here.
//
// Every parse passes the profile explicitly, because src\Logic has no filesystem
// and no Environment access (DESIGN.md section 11): the tests must inject it.
using System;
using System.Collections.Generic;
using System.Reflection;
using SnipVid.Logic;

namespace SnipVid.Tests
{
    /// <summary>
    /// Acceptance checks for <see cref="Config"/> and <see cref="HotkeySpec"/>:
    /// one test method per work order criterion, one check per independently
    /// observable fact.
    /// </summary>
    public static class ConfigTests
    {
        /// <summary>Injected user profile; deliberately not this machine's.</summary>
        private const string Profile = @"C:\Users\x";

        /// <summary>Default output folder from DESIGN.md section 8, with the profile substituted.</summary>
        private const string DefaultFolderPath = @"C:\Users\x\Videos\Screen Recordings";

        /// <summary>The default chord from decision P1.</summary>
        private const string DefaultHotkeyText = "Ctrl+Alt+Shift+S";

        /// <summary>Win32 MOD_ALT | MOD_CONTROL | MOD_SHIFT (1 | 2 | 4).</summary>
        private const int DefaultModifiers = 7;

        /// <summary>Win32 VK for 'S'.</summary>
        private const int VkS = 0x53;

        /// <summary>C1: empty text yields exactly the DESIGN.md section 8 defaults and no notes.</summary>
        /// <remarks>
        /// Six separate checks so a failure names the default that is wrong. A
        /// seventh covers null text: Parse is documented never to throw, and a
        /// missing config.ini arrives as empty text at best.
        /// </remarks>
        public static void TestC1_EmptyTextYieldsDesignDefaults()
        {
            Config config = ParseIni(string.Empty);

            Harness.Check("C1 empty config: fps is 60", config.Fps == 60,
                "expected 60, got " + config.Fps);
            Harness.Check("C1 empty config: quality is 19", config.Quality == 19,
                "expected 19, got " + config.Quality);
            Harness.Check("C1 empty config: folder is " + DefaultFolderPath,
                config.Folder == DefaultFolderPath,
                "expected '" + DefaultFolderPath + "', got '" + Show(config.Folder) + "'");
            Harness.Check("C1 empty config: hotkey is " + DefaultHotkeyText,
                config.Hotkey != null && config.Hotkey.ToString() == DefaultHotkeyText,
                "expected '" + DefaultHotkeyText + "', got " + ShowHotkey(config.Hotkey));
            Harness.Check("C1 empty config: ffmpeg_path is empty",
                config.FfmpegPath == string.Empty,
                "expected empty, got '" + Show(config.FfmpegPath) + "'");
            Harness.Check("C1 empty config: zero notes", config.Notes != null && config.Notes.Count == 0,
                ShowNotes(config));

            Config nullText = ParseIni(null);
            Harness.Check("C1 null text is treated as empty and does not throw",
                nullText.Folder == DefaultFolderPath && nullText.Notes.Count == 0,
                "folder '" + Show(nullText.Folder) + "', " + ShowNotes(nullText));
        }

        /// <summary>C2 (AC9, D1): fps=120 is rejected, leaving 60 and exactly one note.</summary>
        public static void TestC2_Fps120IsRejectedWithExactlyOneNote()
        {
            Config config = ParseIni("[record]\r\nfps=120\r\n");

            Harness.Check("C2 fps=120 leaves the frame rate at 60 (decision D1)",
                config.Fps == 60, "expected 60, got " + config.Fps);
            Harness.Check("C2 fps=120 produces exactly one note",
                config.Notes.Count == 1, ShowNotes(config));
            Harness.Check("C2 the note names the rejected value 120",
                NoteContains(config, "120"), ShowNotes(config));
            Harness.Check("C2 fps=120 keeps the requested 120 for the settings panel to show as Custom",
                config.RequestedFps == 120, "requested " + config.RequestedFps);
        }

        /// <summary>C3 (D1 as amended): fps=30 is an explicit choice, accepted silently.</summary>
        /// <remarks>
        /// The amendment makes 30 valid for a 30 fps source; it is neither a
        /// fallback nor a size lever (D2). A clean parse therefore has zero notes,
        /// which is the anti-noise half: every startup balloon would make an
        /// ordinary choice look like a mistake.
        /// </remarks>
        public static void TestC3_Fps30IsAcceptedWithNoNotes()
        {
            Config config = ParseIni("[record]\r\nfps=30\r\n");

            Harness.Check("C3 fps=30 is accepted as 30 (D1 as amended)",
                config.Fps == 30, "expected 30, got " + config.Fps);
            Harness.Check("C3 fps=30 produces zero notes",
                config.Notes.Count == 0, ShowNotes(config));
            Harness.Check("C3 fps=30 keeps 30 as the requested value",
                config.RequestedFps == 30, "requested " + config.RequestedFps);
        }

        /// <summary>C4: quality is NVENC's -cq 1-51; 0 and 52 are rejected, the bounds accepted.</summary>
        /// <remarks>Both bounds are checked, so "rejects everything" cannot pass this.</remarks>
        public static void TestC4_QualityAcceptsOnlyOneToFiftyOne()
        {
            Config zero = ParseIni("[record]\r\nquality=0\r\n");
            Harness.Check("C4 quality=0 falls back to 19 with exactly one note",
                zero.Quality == 19 && zero.Notes.Count == 1,
                "quality " + zero.Quality + ", " + ShowNotes(zero));

            Config fiftyTwo = ParseIni("[record]\r\nquality=52\r\n");
            Harness.Check("C4 quality=52 falls back to 19 with exactly one note",
                fiftyTwo.Quality == 19 && fiftyTwo.Notes.Count == 1,
                "quality " + fiftyTwo.Quality + ", " + ShowNotes(fiftyTwo));

            Config fiftyOne = ParseIni("[record]\r\nquality=51\r\n");
            Harness.Check("C4 quality=51 is the inclusive upper bound and is accepted silently",
                fiftyOne.Quality == 51 && fiftyOne.Notes.Count == 0,
                "quality " + fiftyOne.Quality + ", " + ShowNotes(fiftyOne));

            Config one = ParseIni("[record]\r\nquality=1\r\n");
            Harness.Check("C4 quality=1 is the inclusive lower bound and is accepted silently",
                one.Quality == 1 && one.Notes.Count == 0,
                "quality " + one.Quality + ", " + ShowNotes(one));
        }

        /// <summary>C5: an unknown key is ignored with no note (forward compatibility).</summary>
        public static void TestC5_UnknownKeyIsIgnoredSilently()
        {
            Config config = ParseIni("[record]\r\ncolour=blue\r\n");

            Harness.Check("C5 unknown key colour=blue produces zero notes",
                config.Notes.Count == 0, ShowNotes(config));
            Harness.Check("C5 unknown key colour=blue changes nothing",
                config.Fps == 60 && config.Quality == 19 && config.Folder == DefaultFolderPath,
                "fps " + config.Fps + ", quality " + config.Quality + ", folder '" + Show(config.Folder) + "'");
        }

        /// <summary>C6: comments, blank lines, padded keys/values, CRLF and LF parse identically.</summary>
        /// <remarks>
        /// Non-default values are used (quality 30, a folder, an F-key hotkey), so
        /// "parsed identically" means both inputs really were parsed rather than
        /// both silently falling back to the defaults.
        /// </remarks>
        public static void TestC6_IniSyntaxVariantsParseIdentically()
        {
            string crlf =
                "; semicolon comment\r\n" +
                "# hash comment\r\n" +
                "\r\n" +
                "[record]\r\n" +
                "   quality   =   30   \r\n" +
                "  folder = C:\\Clips\r\n" +
                "[input]\r\n" +
                "\t hotkey = Alt+F9 \r\n";
            string lf = crlf.Replace("\r\n", "\n");

            Config fromCrlf = ParseIni(crlf);
            Config fromLf = ParseIni(lf);

            Harness.Check("C6 CRLF input with comments and padding parses to quality 30, folder C:\\Clips, hotkey Alt+F9, zero notes",
                fromCrlf.Quality == 30
                && fromCrlf.Folder == @"C:\Clips"
                && fromCrlf.Hotkey.ToString() == "Alt+F9"
                && fromCrlf.Notes.Count == 0,
                "quality " + fromCrlf.Quality + ", folder '" + Show(fromCrlf.Folder)
                + "', hotkey " + ShowHotkey(fromCrlf.Hotkey) + ", " + ShowNotes(fromCrlf));

            Harness.Check("C6 LF input parses identically to the CRLF input",
                fromLf.Quality == fromCrlf.Quality
                && fromLf.Folder == fromCrlf.Folder
                && fromLf.Hotkey.ToString() == fromCrlf.Hotkey.ToString()
                && fromLf.Notes.Count == fromCrlf.Notes.Count,
                "LF gave quality " + fromLf.Quality + ", folder '" + Show(fromLf.Folder)
                + "', hotkey " + ShowHotkey(fromLf.Hotkey) + ", " + ShowNotes(fromLf));
        }

        /// <summary>C7: %USERPROFILE% expands from the argument; any other %VAR% stays literal.</summary>
        /// <remarks>
        /// The oracle is the injected profile C:\Users\x, so an implementation
        /// that read the process environment (C:\Users\you here) fails rather
        /// than matching by luck.
        /// </remarks>
        public static void TestC7_UserProfileExpandsFromTheArgument()
        {
            Config expanded = ParseIni("[record]\r\nfolder=%USERPROFILE%\\Clips\r\n");
            Harness.Check("C7 folder=%USERPROFILE%\\Clips becomes C:\\Users\\x\\Clips from the injected profile",
                expanded.Folder == @"C:\Users\x\Clips" && expanded.Notes.Count == 0,
                "folder '" + Show(expanded.Folder) + "', " + ShowNotes(expanded));

            Config otherVar = ParseIni("[record]\r\nfolder=%APPDATA%\\SnipVid\r\n");
            Harness.Check("C7 folder=%APPDATA%\\SnipVid is stored literally with no note",
                otherVar.Folder == @"%APPDATA%\SnipVid" && otherVar.Notes.Count == 0,
                "folder '" + Show(otherVar.Folder) + "', " + ShowNotes(otherVar));

            Config lowerCase = ParseIni("[record]\r\nfolder=%userprofile%\\Clips\r\n");
            Harness.Check("C7 the token is matched case-insensitively, as Windows environment names are",
                lowerCase.Folder == @"C:\Users\x\Clips",
                "folder '" + Show(lowerCase.Folder) + "'");
        }

        /// <summary>C8: an unparseable hotkey falls back to the default with exactly one note.</summary>
        /// <remarks>
        /// The TryParse half is the <see cref="HotkeySpec"/> contract: malformed
        /// text comes from a hand-edited file, so it returns false and never
        /// throws. The out parameter is null on failure so a caller cannot
        /// accidentally register a half-parsed chord.
        /// </remarks>
        public static void TestC8_InvalidHotkeyFallsBackWithOneNote()
        {
            Config config = ParseIni("[input]\r\nhotkey=Ctrl+Banana\r\n");

            Harness.Check("C8 hotkey=Ctrl+Banana falls back to " + DefaultHotkeyText,
                config.Hotkey.ToString() == DefaultHotkeyText,
                "got " + ShowHotkey(config.Hotkey));
            Harness.Check("C8 hotkey=Ctrl+Banana produces exactly one note",
                config.Notes.Count == 1, ShowNotes(config));

            HotkeySpec rejected;
            bool ok = HotkeySpec.TryParse("Ctrl+Banana", out rejected);
            Harness.Check("C8 HotkeySpec.TryParse(\"Ctrl+Banana\") returns false and yields null",
                !ok && rejected == null,
                ok ? "returned true with " + ShowHotkey(rejected) : "returned true/false but out was " + ShowHotkey(rejected));
        }

        /// <summary>C9: a key before the first [section] is ignored with exactly one note.</summary>
        /// <remarks>
        /// The line after the header sets a non-default value, so the check also
        /// proves the parser recovered instead of abandoning the rest of the file.
        /// </remarks>
        public static void TestC9_KeysBeforeASectionAreIgnoredWithOneNote()
        {
            Config config = ParseIni("folder=C:\\before\r\n[record]\r\nquality=30\r\n");

            Harness.Check("C9 a key before the first [section] produces exactly one note",
                config.Notes.Count == 1, ShowNotes(config));
            Harness.Check("C9 the key before the first [section] is not applied",
                config.Folder == DefaultFolderPath,
                "folder '" + Show(config.Folder) + "'");
            Harness.Check("C9 parsing continues after the ignored line",
                config.Quality == 30, "quality " + config.Quality);
        }

        /// <summary>C10: ffmpeg_path is stored verbatim; resolution and validation are WO-12.</summary>
        public static void TestC10_FfmpegPathIsStoredVerbatim()
        {
            Config config = ParseIni("[record]\r\nffmpeg_path=C:\\nope\\ffmpeg.exe\r\n");

            Harness.Check("C10 ffmpeg_path is stored verbatim, with no existence check here",
                config.FfmpegPath == @"C:\nope\ffmpeg.exe",
                "got '" + Show(config.FfmpegPath) + "'");
            Harness.Check("C10 ffmpeg_path produces zero notes (validation belongs to WO-12)",
                config.Notes.Count == 0, ShowNotes(config));
        }

        /// <summary>C11: a chord with no modifier is rejected, whatever key it names.</summary>
        /// <remarks>
        /// DESIGN.md section 8: a bare key is a legal RegisterHotKey chord, which is
        /// the problem - "hotkey=S" would register S system-wide, so every S typed
        /// anywhere in Windows would start a capture. A letter, a function key and
        /// a digit are all asserted because a rule tested on one key is a rule that
        /// works once; F9 also proves there is no function-key exception.
        /// </remarks>
        public static void TestC11_HotkeyWithoutAnyModifierIsRejected()
        {
            Config bareLetter = ParseIni("[input]\r\nhotkey=S\r\n");

            Harness.Check("C11 hotkey=S falls back to " + DefaultHotkeyText,
                bareLetter.Hotkey.ToString() == DefaultHotkeyText,
                "got " + ShowHotkey(bareLetter.Hotkey));
            Harness.Check("C11 hotkey=S produces exactly one note",
                bareLetter.Notes.Count == 1, ShowNotes(bareLetter));
            Harness.Check("C11 the note for hotkey=S names the offending value S",
                NoteContains(bareLetter, "hotkey=S"), ShowNotes(bareLetter));

            Config bareFunctionKey = ParseIni("[input]\r\nhotkey=F9\r\n");
            Harness.Check("C11 hotkey=F9 falls back to " + DefaultHotkeyText + " with exactly one note naming F9",
                bareFunctionKey.Hotkey.ToString() == DefaultHotkeyText
                && bareFunctionKey.Notes.Count == 1
                && NoteContains(bareFunctionKey, "hotkey=F9"),
                "hotkey " + ShowHotkey(bareFunctionKey.Hotkey) + ", " + ShowNotes(bareFunctionKey));

            Config bareDigit = ParseIni("[input]\r\nhotkey=5\r\n");
            Harness.Check("C11 hotkey=5 falls back to " + DefaultHotkeyText + " with exactly one note naming 5",
                bareDigit.Hotkey.ToString() == DefaultHotkeyText
                && bareDigit.Notes.Count == 1
                && NoteContains(bareDigit, "hotkey=5"),
                "hotkey " + ShowHotkey(bareDigit.Hotkey) + ", " + ShowNotes(bareDigit));

            Harness.Check("C11 a bare letter, F-key or digit is not a registrable chord",
                Rejects("S") && Rejects("F9") && Rejects("5"),
                Describe("S") + "; " + Describe("F9") + "; " + Describe("5"));
        }

        /// <summary>C12: a UTF-8 BOM in front of the file does not disable the whole parse.</summary>
        /// <remarks>
        /// Notepad still offers "UTF-8 with BOM" and DESIGN.md section 8 calls this
        /// file human-editable. U+FEFF is not whitespace to .NET, so Trim() keeps
        /// it: "[record]" stops being a section header, every key is ignored and a
        /// note per line replaces the settings the file clearly contains. The
        /// expected values here are the literals DESIGN.md section 8 prints, so the
        /// oracle is the spec and not the non-BOM parse this check compares to.
        /// </remarks>
        public static void TestC12_Utf8BomPrefixDoesNotDisableTheFile()
        {
            string sample =
                "[record]\r\n" +
                "fps=60\r\n" +
                "quality=19\r\n" +
                "folder=%USERPROFILE%\\Videos\\Screen Recordings\r\n" +
                "ffmpeg_path=\r\n" +
                "[input]\r\n" +
                "hotkey=Ctrl+Alt+Shift+S\r\n";

            Config plain = ParseIni(sample);
            Config prefixed = ParseIni("\uFEFF" + sample);

            Harness.Check("C12 the DESIGN.md section 8 sample prefixed with U+FEFF still parses to the defaults with zero notes",
                prefixed.Fps == 60
                && prefixed.Quality == 19
                && prefixed.Folder == DefaultFolderPath
                && prefixed.FfmpegPath == string.Empty
                && prefixed.Hotkey.ToString() == DefaultHotkeyText
                && prefixed.Notes.Count == 0,
                "fps " + prefixed.Fps + ", quality " + prefixed.Quality
                + ", folder '" + Show(prefixed.Folder) + "', path '" + Show(prefixed.FfmpegPath)
                + "', hotkey " + ShowHotkey(prefixed.Hotkey) + ", " + ShowNotes(prefixed));

            Harness.Check("C12 a leading U+FEFF changes nothing compared with the same text without one",
                prefixed.Fps == plain.Fps
                && prefixed.Quality == plain.Quality
                && prefixed.Folder == plain.Folder
                && prefixed.FfmpegPath == plain.FfmpegPath
                && prefixed.Hotkey.ToString() == plain.Hotkey.ToString()
                && prefixed.Notes.Count == plain.Notes.Count,
                "with BOM: " + ShowNotes(prefixed) + " / without: " + ShowNotes(plain));

            // Only the document prefix is stripped. A U+FEFF anywhere else is the
            // file's data, so a parser that removed every occurrence (or every
            // line's leading one) would corrupt a folder name the user typed.
            Config valueWithBom = ParseIni("\uFEFF[record]\r\nfolder=C:\\A\uFEFFB\r\n");
            Harness.Check("C12 a U+FEFF inside a value is data and is not stripped",
                valueWithBom.Folder == "C:\\A\uFEFFB" && valueWithBom.Notes.Count == 0,
                "folder '" + Show(valueWithBom.Folder) + "', " + ShowNotes(valueWithBom));

            // Nor per line: a key that merely starts with U+FEFF is not one of the
            // known names, so it is ignored silently like any other unknown key.
            Config laterLine = ParseIni("\uFEFF[record]\r\nfps=60\r\n\uFEFFquality=30\r\n");
            Harness.Check("C12 only the document prefix is stripped: a U+FEFF later in the file does not make a known key",
                laterLine.Quality == 19 && laterLine.Notes.Count == 0,
                "quality " + laterLine.Quality + ", " + ShowNotes(laterLine));
        }

        /// <summary>C13: a zero-modifier chord is unrepresentable, not merely unreachable from the parser.</summary>
        /// <remarks>
        /// DESIGN.md section 8 forbids the chord RegisterHotKey(0, 'S') would create,
        /// because it swallows every S typed anywhere in Windows. TryParse refuses
        /// it, but a public (modifiers, virtualKey) constructor would still hand
        /// WO-06 that mask through new HotkeySpec(0, 0x53). The constructor is
        /// therefore private and the type itself is the guarantee: the reflection
        /// check is the executable form of the compile-time argument, and the rest
        /// are its runtime half, so the invariant is checked even if someone later
        /// reinstates a public way in.
        /// </remarks>
        public static void TestC13_ZeroModifierChordIsUnrepresentable()
        {
            ConstructorInfo[] publicConstructors =
                typeof(HotkeySpec).GetConstructors(BindingFlags.Public | BindingFlags.Instance);

            Harness.Check("C13 HotkeySpec exposes no public constructor, so new HotkeySpec(0, 0x53) cannot be written",
                publicConstructors.Length == 0,
                "expected no public instance constructor, found " + publicConstructors.Length);

            // A struct would re-open the hole through default(T), which skips every
            // constructor; this pins the class decision that closes it.
            Harness.Check("C13 HotkeySpec is a class, so default(HotkeySpec) cannot be a zero-modifier chord",
                default(HotkeySpec) == null,
                "default(HotkeySpec) was " + ShowHotkey(default(HotkeySpec)));

            // A public setter would re-open the same hole after a valid parse.
            PropertyInfo modifiers = typeof(HotkeySpec).GetProperty("Modifiers");
            Harness.Check("C13 Modifiers has no public setter, so a valid chord cannot be mutated to a zero mask",
                modifiers != null && modifiers.GetSetMethod(false) == null,
                modifiers == null ? "no Modifiers property" : "a public Modifiers setter was found");

            string[] reachable = { "Ctrl+S", "Alt+S", "Shift+S", "Win+S", "Ctrl+Alt+Shift+S", "Win+F24" };
            bool everyMaskNonZero = true;
            string offender = "none";
            foreach (string text in reachable)
            {
                HotkeySpec spec;
                if (!HotkeySpec.TryParse(text, out spec))
                {
                    everyMaskNonZero = false;
                    offender = "'" + text + "' was rejected";
                    break;
                }
                if (spec.Modifiers == 0)
                {
                    everyMaskNonZero = false;
                    offender = "'" + text + "' gave mask 0";
                    break;
                }
            }

            Harness.Check("C13 every chord TryParse can produce carries at least one modifier bit",
                everyMaskNonZero, offender);
            Harness.Check("C13 Default carries at least one modifier bit",
                HotkeySpec.Default != null && HotkeySpec.Default.Modifiers != 0,
                "default mask " + (HotkeySpec.Default == null ? -1 : HotkeySpec.Default.Modifiers));
        }

        /// <summary>C14: an unsupported numeric fps is shown as the file wrote it, not applied.</summary>
        /// <remarks>
        /// The settings panel may only claim a value the file holds. With fps=24
        /// the recording is 60 (the parser fell back), but the panel must show
        /// "Custom (24 fps)" with no preset marked until the user picks one; that
        /// requires the requested value to survive the parse even though the
        /// effective value did not.
        /// </remarks>
        public static void TestC14_UnsupportedFpsKeepsTheRequestedValue()
        {
            Config config = ParseIni("[record]\r\nfps=24\r\n");

            Harness.Check("C14 fps=24 is not applied: the effective frame rate is 60",
                config.Fps == 60, "expected 60, got " + config.Fps);
            Harness.Check("C14 fps=24 keeps 24 as the requested value for the panel",
                config.RequestedFps == 24, "requested " + config.RequestedFps);
            Harness.Check("C14 fps=24 produces exactly one note naming 24",
                config.Notes.Count == 1 && NoteContains(config, "24"), ShowNotes(config));
        }

        /// <summary>C15: the requested fps defaults to 60 when the file does not name one.</summary>
        public static void TestC15_AbsentFpsRequestedValueIs60()
        {
            Config config = ParseIni("[record]\r\nquality=19\r\n");

            Harness.Check("C15 an absent fps key leaves both the effective and requested value at 60",
                config.Fps == 60 && config.RequestedFps == 60,
                "fps " + config.Fps + ", requested " + config.RequestedFps);
        }

        /// <summary>
        /// C5 (audio): `mic=off` parses as off with zero notes.
        /// </summary>
        /// <remarks>
        /// Oracle: the audio work order's config rule - off, default, or a device
        /// name. Zero notes is half the check: an ordinary choice must not look
        /// like a mistake on startup (the same anti-noise rule as fps=30). The
        /// spelling is matched like every other key name, ignoring case, and
        /// normalised to the lowercase keyword so the caller can compare once.
        /// </remarks>
        public static void TestC16_MicOffParsesAsOffWithZeroNotes()
        {
            Config off = ParseIni("[record]\r\nmic=off\r\n");
            Harness.Check("C5 mic=off is off, with zero notes",
                off.Microphone == "off" && off.MicIsOff && !off.MicIsDefault && off.Notes.Count == 0,
                "mic '" + Show(off.Microphone) + "', off " + off.MicIsOff + ", default " + off.MicIsDefault
                    + ", " + ShowNotes(off));

            Config upper = ParseIni("[record]\r\nMIC=OFF\r\n");
            Harness.Check("C5 mic=OFF is the same keyword, matched ignoring case",
                upper.Microphone == "off" && upper.MicIsOff && upper.Notes.Count == 0,
                "mic '" + Show(upper.Microphone) + "', " + ShowNotes(upper));
        }

        /// <summary>
        /// C6 (audio): `mic=Some Device` is stored verbatim with zero notes.
        /// </summary>
        /// <remarks>
        /// Oracle: the work order's rule that an unknown device is NOT validated
        /// here - same separation as ffmpeg_path. A name the parser rejected would
        /// be a name the user cannot use, and only ffmpeg can decide whether a
        /// device exists. The exact spelling is preserved because dshow matches it
        /// character for character; trimming is the only edit, and that is what
        /// makes the value independent of the hand-edited file's spacing.
        /// </remarks>
        public static void TestC17_MicDeviceNameIsStoredVerbatimWithZeroNotes()
        {
            Config named = ParseIni("[record]\r\nmic=Some Device\r\n");
            Harness.Check("C6 mic=Some Device is stored verbatim, not treated as off or default",
                named.Microphone == "Some Device" && !named.MicIsOff && !named.MicIsDefault
                    && named.Notes.Count == 0,
                "mic '" + Show(named.Microphone) + "', " + ShowNotes(named));

            const string Real = "Microphone (4- USB Audio)";
            Config real = ParseIni("[record]\r\nmic=" + Real + "\r\n");
            Harness.Check("C6 a real device name with spaces and parentheses survives untouched",
                real.Microphone == Real && real.Notes.Count == 0,
                "mic '" + Show(real.Microphone) + "', " + ShowNotes(real));

            Config unknown = ParseIni("[record]\r\nmic=No Such Device\r\n");
            Harness.Check("C6 an unknown device name produces no note (existence is a runtime question)",
                unknown.Microphone == "No Such Device" && unknown.Notes.Count == 0,
                "mic '" + Show(unknown.Microphone) + "', " + ShowNotes(unknown));
        }

        /// <summary>
        /// C5/C6 edge: an absent key means "resolve the default", and an empty
        /// value is a note rather than a silent default.
        /// </summary>
        /// <remarks>
        /// Oracle: the work order - absent or `mic=default` means resolve the
        /// default. Empty is neither keyword nor a name, and DESIGN.md section 8's
        /// rule for a known key with an unusable value is a note plus the default;
        /// the same treatment as folder= and quality=. Silence there would let a
        /// user believe they disabled the mic by deleting the value.
        /// </remarks>
        public static void TestC18_MicDefaultsToDefaultWhenAbsent()
        {
            Config absent = ParseIni("[record]\r\nfps=60\r\n");
            Harness.Check("C18 an absent mic key means resolve the default, with zero notes",
                absent.Microphone == "default" && absent.MicIsDefault && !absent.MicIsOff
                    && absent.Notes.Count == 0,
                "mic '" + Show(absent.Microphone) + "', " + ShowNotes(absent));

            Config explicitDefault = ParseIni("[record]\r\nmic=default\r\n");
            Harness.Check("C18 mic=default is the keyword, with zero notes",
                explicitDefault.Microphone == "default" && explicitDefault.MicIsDefault
                    && explicitDefault.Notes.Count == 0,
                "mic '" + Show(explicitDefault.Microphone) + "', " + ShowNotes(explicitDefault));

            Config empty = ParseIni("[record]\r\nmic=\r\n");
            Harness.Check("C18 mic= is rejected with exactly one note and falls back to default",
                empty.Microphone == "default" && empty.Notes.Count == 1,
                "mic '" + Show(empty.Microphone) + "', " + ShowNotes(empty));
        }

        /// <summary>R1: section and key names are case-insensitive.</summary>
        public static void TestR1_SectionAndKeyNamesAreCaseInsensitive()
        {
            string ini =
                "[Record]\r\n" +
                "FPS=60\r\n" +
                "Quality=31\r\n" +
                "FOLDER=C:\\Clips\r\n" +
                "[INPUT]\r\n" +
                "HotKey=Alt+F9\r\n";
            Config config = ParseIni(ini);

            Harness.Check("R1 [Record]/[INPUT] with FPS/Quality/FOLDER/HotKey is parsed as the known names",
                config.Quality == 31
                && config.Folder == @"C:\Clips"
                && config.Hotkey.ToString() == "Alt+F9"
                && config.Notes.Count == 0,
                "quality " + config.Quality + ", folder '" + Show(config.Folder)
                + "', hotkey " + ShowHotkey(config.Hotkey) + ", " + ShowNotes(config));
        }

        /// <summary>R2: a duplicate key keeps its last value, silently when both are valid.</summary>
        /// <remarks>
        /// The second check pins the interaction between "last value wins" and
        /// "one note per rejected value": the rejected 120 keeps its note even
        /// though the following line overrides it, because the user did write an
        /// unsupported value and the note is the only evidence of that.
        /// </remarks>
        public static void TestR2_DuplicateKeyKeepsTheLastValue()
        {
            Config duplicates = ParseIni("[record]\r\nfolder=C:\\one\r\nfolder=C:\\two\r\nquality=51\r\nquality=7\r\n");
            Harness.Check("R2 the last folder and the last quality win, with zero notes",
                duplicates.Folder == @"C:\two" && duplicates.Quality == 7 && duplicates.Notes.Count == 0,
                "folder '" + Show(duplicates.Folder) + "', quality " + duplicates.Quality
                + ", " + ShowNotes(duplicates));

            Config mixed = ParseIni("[record]\r\nfps=120\r\nfps=60\r\n");
            Harness.Check("R2 a rejected value followed by a valid duplicate keeps the note and the last value",
                mixed.Fps == 60 && mixed.Notes.Count == 1,
                "fps " + mixed.Fps + ", " + ShowNotes(mixed));

            // The reverse order is the same rule: the last occurrence decides, and a
            // rejected last value resolves to the default 60, not to the earlier 30.
            Config rejectedLast = ParseIni("[record]\r\nfps=30\r\nfps=120\r\n");
            Harness.Check("R2 a valid value followed by a rejected duplicate resolves to the default 60 with one note",
                rejectedLast.Fps == 60
                && rejectedLast.RequestedFps == 120
                && rejectedLast.Notes.Count == 1,
                "fps " + rejectedLast.Fps + ", requested " + rejectedLast.RequestedFps
                + ", " + ShowNotes(rejectedLast));
        }

        /// <summary>R3: an unknown section is ignored silently, whatever its keys.</summary>
        /// <remarks>DESIGN.md section 8 has no [ui] section; a newer version may.</remarks>
        public static void TestR3_UnknownSectionIsIgnoredSilently()
        {
            Config config = ParseIni("[ui]\r\ncolour=blue\r\nfolder=C:\\nope\r\n[record]\r\nquality=30\r\n");

            Harness.Check("R3 keys inside an unknown section change nothing and produce zero notes",
                config.Folder == DefaultFolderPath
                && config.Quality == 30
                && config.Notes.Count == 0,
                "folder '" + Show(config.Folder) + "', quality " + config.Quality
                + ", " + ShowNotes(config));
        }

        /// <summary>R4: the modifiers mask uses the Win32 MOD_* values and ToString is normalised.</summary>
        /// <remarks>
        /// 1/2/4/8 are MOD_ALT/MOD_CONTROL/MOD_SHIFT/MOD_WIN; vk 0x53 is 'S',
        /// 0x87 is VK_F24, 0x30 is '0'. The order is checked because WO-06 logs
        /// the registered chord and P1 writes it Ctrl+Alt+Shift+*.
        /// </remarks>
        public static void TestR4_HotkeyModifiersUseTheWin32Mask()
        {
            Harness.Check("R4 Ctrl+Alt+Shift+S is mods 7, vk 0x53, text " + DefaultHotkeyText,
                Chord("Ctrl+Alt+Shift+S", DefaultModifiers, VkS, DefaultHotkeyText),
                Describe("Ctrl+Alt+Shift+S"));
            Harness.Check("R4 modifier order in the text does not matter and the text is normalised",
                Chord("Shift+Ctrl+Alt+s", DefaultModifiers, VkS, DefaultHotkeyText),
                Describe("Shift+Ctrl+Alt+s"));
            Harness.Check("R4 Control is a synonym for Ctrl and maps to MOD_CONTROL (2)",
                Chord("Control+F24", 2, 0x87, "Ctrl+F24"),
                Describe("Control+F24"));
            Harness.Check("R4 Win maps to MOD_WIN (8) and is written after the other modifiers",
                Chord("Win+Shift+Alt+0", 8 | 4 | 1, 0x30, "Alt+Shift+Win+0"),
                Describe("Win+Shift+Alt+0"));
            Harness.Check("R4 HotkeySpec.Default is the P1 chord, mods 7, vk 0x53",
                HotkeySpec.Default != null
                && HotkeySpec.Default.Modifiers == DefaultModifiers
                && HotkeySpec.Default.VirtualKey == VkS
                && HotkeySpec.Default.ToString() == DefaultHotkeyText,
                "default was " + ShowHotkey(HotkeySpec.Default));
        }

        /// <summary>R5: the accepted key set is A-Z, 0-9 and F1-F24, and nothing else.</summary>
        public static void TestR5_HotkeyKeyRangeIsLettersDigitsAndF1ToF24()
        {
            Harness.Check("R5 Alt+F1 is vk 0x70 (VK_F1), the inclusive lower F bound",
                Chord("Alt+F1", 1, 0x70, "Alt+F1"), Describe("Alt+F1"));
            Harness.Check("R5 Ctrl+F24 is vk 0x87 (VK_F24), the inclusive upper F bound",
                Chord("Ctrl+F24", 2, 0x87, "Ctrl+F24"), Describe("Ctrl+F24"));
            Harness.Check("R5 Ctrl+0 and Ctrl+9 are vk 0x30 and 0x39 (VK_0, VK_9)",
                Chord("Ctrl+0", 2, 0x30, "Ctrl+0") && Chord("Ctrl+9", 2, 0x39, "Ctrl+9"),
                Describe("Ctrl+0") + "; " + Describe("Ctrl+9"));
            Harness.Check("R5 Ctrl+Z is vk 0x5A (VK_Z), the inclusive upper letter bound",
                Chord("Ctrl+Z", 2, 0x5A, "Ctrl+Z"), Describe("Ctrl+Z"));

            Harness.Check("R5 Ctrl+F25 is out of range and rejected",
                Rejects("Ctrl+F25"), Describe("Ctrl+F25"));
            Harness.Check("R5 Ctrl+F0 is out of range and rejected",
                Rejects("Ctrl+F0"), Describe("Ctrl+F0"));
            Harness.Check("R5 Ctrl+AB (two characters) is rejected",
                Rejects("Ctrl+AB"), Describe("Ctrl+AB"));
            Harness.Check("R5 Ctrl+ with no key is rejected",
                Rejects("Ctrl+"), Describe("Ctrl+"));
            Harness.Check("R5 empty text and null are rejected without throwing",
                Rejects(string.Empty) && Rejects(null), Describe(string.Empty) + "; " + Describe(null));
        }

        /// <summary>R6: an empty value falls back to the default with one note, except ffmpeg_path.</summary>
        /// <remarks>
        /// ffmpeg_path is the exception because DESIGN.md section 8 defines an
        /// empty value as "auto-resolve", which is the shipped default; a note
        /// there would fire on every clean install.
        /// </remarks>
        public static void TestR6_EmptyValuesFallBackToDefaults()
        {
            Config config = ParseIni("[record]\r\nfolder=\r\nquality=\r\n[input]\r\nhotkey=\r\n");

            Harness.Check("R6 folder=, quality= and hotkey= each fall back to their default with one note apiece",
                config.Folder == DefaultFolderPath
                && config.Quality == 19
                && config.Hotkey.ToString() == DefaultHotkeyText
                && config.Notes.Count == 3,
                "folder '" + Show(config.Folder) + "', quality " + config.Quality
                + ", hotkey " + ShowHotkey(config.Hotkey) + ", " + ShowNotes(config));

            Config ffmpegPath = ParseIni("[record]\r\nffmpeg_path=\r\n");
            Harness.Check("R6 ffmpeg_path= is the documented auto-resolve value: empty, zero notes",
                ffmpegPath.FfmpegPath == string.Empty && ffmpegPath.Notes.Count == 0,
                "path '" + Show(ffmpegPath.FfmpegPath) + "', " + ShowNotes(ffmpegPath));
        }

        /// <summary>R7: the file DESIGN.md section 8 prints parses with zero notes.</summary>
        /// <remarks>
        /// This is the anti-noise check: a clean install writes exactly this file,
        /// so any note here would greet every first run with a complaint.
        /// </remarks>
        public static void TestR7_DesignSampleFileProducesNoNotes()
        {
            string ini =
                "[record]\r\n" +
                "fps=60\r\n" +
                "quality=19\r\n" +
                "folder=%USERPROFILE%\\Videos\\Screen Recordings\r\n" +
                "ffmpeg_path=\r\n" +
                "mic=default\r\n" +
                "system_audio=off\r\n" +
                "[input]\r\n" +
                "hotkey=Ctrl+Alt+Shift+S\r\n";
            Config config = ParseIni(ini);

            Harness.Check("R7 the DESIGN.md section 8 sample parses to the defaults with zero notes",
                config.Fps == 60
                && config.Quality == 19
                && config.Folder == DefaultFolderPath
                && config.FfmpegPath == string.Empty
                && config.Microphone == "default"
                && !config.SystemAudio
                && config.Hotkey.ToString() == DefaultHotkeyText
                && config.Notes.Count == 0,
                "fps " + config.Fps + ", quality " + config.Quality + ", folder '" + Show(config.Folder)
                + "', path '" + Show(config.FfmpegPath) + "', mic '" + Show(config.Microphone)
                + "', system audio " + config.SystemAudio
                + ", hotkey " + ShowHotkey(config.Hotkey)
                + ", " + ShowNotes(config));
        }

        /// <summary>
        /// C4 (system audio): `system_audio=on` and `system_audio=off` parse with
        /// zero notes; absent means off; anything else is a note plus off.
        /// </summary>
        /// <remarks>
        /// Oracle: the system-audio work order's config rule - the key takes on/off
        /// and its default is off, which is what keeps today's argv byte-identical
        /// for every existing file. Zero notes for both valid values is half the
        /// check, the same anti-noise rule as fps=30 and mic=off: an ordinary choice
        /// must not look like a mistake on startup. A bad value follows DESIGN.md
        /// section 8's rule for a known key: one note plus the default.
        /// </remarks>
        public static void TestC19_SystemAudioParsesOnAndOffWithZeroNotes()
        {
            Config on = ParseIni("[record]\r\nsystem_audio=on\r\n");
            Harness.Check("C4 system_audio=on is on, with zero notes",
                on.SystemAudio && on.Notes.Count == 0,
                "system audio " + on.SystemAudio + ", " + ShowNotes(on));

            Config off = ParseIni("[record]\r\nsystem_audio=off\r\n");
            Harness.Check("C4 system_audio=off is off, with zero notes",
                !off.SystemAudio && off.Notes.Count == 0,
                "system audio " + off.SystemAudio + ", " + ShowNotes(off));

            Config upper = ParseIni("[record]\r\nSYSTEM_AUDIO=ON\r\n");
            Harness.Check("C4 SYSTEM_AUDIO=ON is the same keyword and value, matched ignoring case",
                upper.SystemAudio && upper.Notes.Count == 0,
                "system audio " + upper.SystemAudio + ", " + ShowNotes(upper));

            Config absent = ParseIni("[record]\r\nfps=60\r\n");
            Harness.Check("C4 an absent system_audio key means off, with zero notes",
                !absent.SystemAudio && absent.Notes.Count == 0,
                "system audio " + absent.SystemAudio + ", " + ShowNotes(absent));

            Config bad = ParseIni("[record]\r\nsystem_audio=yes\r\n");
            Harness.Check("C4 system_audio=yes is rejected with exactly one note naming it and falls back to off",
                !bad.SystemAudio && bad.Notes.Count == 1 && NoteContains(bad, "system_audio=yes"),
                "system audio " + bad.SystemAudio + ", " + ShowNotes(bad));

            Config empty = ParseIni("[record]\r\nsystem_audio=\r\n");
            Harness.Check("C4 system_audio= is rejected with exactly one note and falls back to off",
                !empty.SystemAudio && empty.Notes.Count == 1,
                "system audio " + empty.SystemAudio + ", " + ShowNotes(empty));
        }

        /// <summary>Parses with the injected test profile.</summary>
        /// <param name="iniText">Config text; may be null or empty.</param>
        /// <returns>The parsed config.</returns>
        /// <remarks>Side effects: none. Used so every criterion parses the same way.</remarks>
        private static Config ParseIni(string iniText)
        {
            return Config.Parse(iniText, Profile);
        }

        /// <summary>True when one chord parses to the expected mask, key and normalised text.</summary>
        /// <param name="text">Chord to parse.</param>
        /// <param name="modifiers">Expected Win32 MOD_* mask.</param>
        /// <param name="virtualKey">Expected Win32 virtual-key code.</param>
        /// <param name="normalised">Expected ToString() output.</param>
        /// <returns>True when every part matches.</returns>
        /// <remarks>Side effects: none.</remarks>
        private static bool Chord(string text, int modifiers, int virtualKey, string normalised)
        {
            HotkeySpec spec;
            return HotkeySpec.TryParse(text, out spec)
                && spec != null
                && spec.Modifiers == modifiers
                && spec.VirtualKey == virtualKey
                && spec.ToString() == normalised;
        }

        /// <summary>True when the chord is rejected without throwing.</summary>
        /// <param name="text">Chord to parse; may be null.</param>
        /// <returns>True when TryParse returns false and nulls the output.</returns>
        /// <remarks>Side effects: none; the harness turns an exception into a failed check.</remarks>
        private static bool Rejects(string text)
        {
            HotkeySpec spec;
            return !HotkeySpec.TryParse(text, out spec) && spec == null;
        }

        /// <summary>Describes what TryParse actually did, for a failure message.</summary>
        /// <param name="text">Chord that was parsed.</param>
        /// <returns>Observed mask, key and text, or "returned false".</returns>
        /// <remarks>Side effects: none.</remarks>
        private static string Describe(string text)
        {
            HotkeySpec spec;
            if (!HotkeySpec.TryParse(text, out spec))
            {
                return "'" + Show(text) + "' was rejected";
            }
            return "'" + Show(text) + "' gave mods=" + spec.Modifiers
                + " vk=0x" + spec.VirtualKey.ToString("X2") + " text=" + ShowHotkey(spec);
        }

        /// <summary>True when any note contains the fragment, ignoring case.</summary>
        /// <param name="config">Config whose notes are searched.</param>
        /// <param name="fragment">Substring to look for, for example "120".</param>
        /// <returns>True when a note mentions the fragment.</returns>
        /// <remarks>Side effects: none.</remarks>
        private static bool NoteContains(Config config, string fragment)
        {
            if (config.Notes == null)
            {
                return false;
            }
            foreach (string note in config.Notes)
            {
                if (note != null && note.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Renders the notes for a failure message.</summary>
        /// <param name="config">Config whose notes are shown.</param>
        /// <returns>Note count plus the notes themselves.</returns>
        /// <remarks>Side effects: none.</remarks>
        private static string ShowNotes(Config config)
        {
            if (config.Notes == null)
            {
                return "Notes was null";
            }
            if (config.Notes.Count == 0)
            {
                return "no notes";
            }
            string joined = string.Empty;
            foreach (string note in config.Notes)
            {
                joined += " [" + note + "]";
            }
            return config.Notes.Count + " note(s):" + joined;
        }

        /// <summary>Shows a string, with null made visible.</summary>
        /// <param name="value">Value to render.</param>
        /// <returns>The value, or "&lt;null&gt;".</returns>
        /// <remarks>Side effects: none.</remarks>
        private static string Show(string value)
        {
            return value == null ? "<null>" : value;
        }

        /// <summary>Shows a hotkey, with null made visible.</summary>
        /// <param name="spec">Chord to render.</param>
        /// <returns>The normalised chord, or "&lt;null&gt;".</returns>
        /// <remarks>Side effects: none.</remarks>
        private static string ShowHotkey(HotkeySpec spec)
        {
            return spec == null ? "<null>" : spec.ToString();
        }
    }
}
