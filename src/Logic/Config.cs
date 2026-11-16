// Config - the one place config.ini text is interpreted (DESIGN.md section 8).
//
// WHY the parse lives in src\Logic and takes the file text plus the profile path
// as parameters: the file is hand-editable, so every mistake in it has to become
// a startup note rather than an exception or a silent fallback. Behind a
// filesystem call the rules would only be testable against a real %APPDATA%, and
// reading the process environment here would make the tests depend on this
// machine's user. Both are injected instead (DESIGN.md section 11).
//
// Three rules decide most of the behaviour:
//   * fps accepts 60 (the default) and 30 (an explicit choice for a 30 fps
//     source); everything else is a note (decision D1 as amended). 120/144 are
//     still refused because they store the same 60 fps picture twice, and a
//     quiet fallback would leave the user believing the setting took effect.
//     The accepted set lives in FfmpegArgs.IsSupportedFps so the parser and the
//     argv builder cannot disagree.
//   * unknown keys and unknown sections are ignored *silently*; only a bad value
//     of a known key makes a note (DESIGN.md section 8). A note per unknown key
//     would punish forward compatibility: a newer version's key would make this
//     version look broken.
//   * nothing here validates the outside world. ffmpeg_path is stored verbatim
//     and the folder is not created; resolution is WO-12 and creation is the
//     caller's job, so a parse cannot fail for a reason the user cannot see. The
//     same separation applies to mic: "off" and "default" are keywords, anything
//     else is a device name stored verbatim, and whether that device exists is
//     ffmpeg's question at spawn time (DESIGN.md sections 5d and 8).
//   * system_audio is a boolean keyword pair, not a device: loopback follows the
//     default render endpoint, so there is nothing for the file to pin. Off is
//     the default and therefore what an absent key means, keeping today's argv
//     for every existing file (DESIGN.md section 5d).
//
// Parse never throws, for any input including null: malformed text and nonsense
// values come back as defaults plus one human-readable note per rejected value.
// Notes are shown once on startup, never in a modal dialog. The text may also
// arrive with a UTF-8 byte-order mark - Notepad still offers that encoding - so
// the mark is stripped from the document start; without that, the first section
// header is not recognised and the entire file becomes notes.
//
// No WPF, no P/Invoke, no I/O, no clock, no Environment: test.ps1 compiles this
// file without any Windows reference (DESIGN.md section 11).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SnipVid.Logic
{
    /// <summary>
    /// The parsed configuration: one immutable carrier of the six settings in
    /// DESIGN.md section 8, plus the notes that explain every rejected value.
    /// </summary>
    /// <remarks>
    /// No side effects and no validation once constructed - <see cref="Parse"/>
    /// has already turned every unusable value into a default plus a note. The
    /// notes are read-only so a later stage cannot quietly drop the evidence
    /// that the file disagreed with the running build.
    /// </remarks>
    public sealed class Config
    {
        /// <summary>The default frame rate, used whenever the file's value is not supported (decision D1).</summary>
        private const int FpsValue = 60;

        /// <summary>Default NVENC -cq: visually lossless enough for screen content (DESIGN.md section 5).</summary>
        private const int DefaultQuality = 19;

        /// <summary>NVENC's lowest -cq; 1 is the best quality the encoder offers.</summary>
        private const int MinQuality = 1;

        /// <summary>NVENC's highest -cq; 51 is the worst quality the encoder offers.</summary>
        private const int MaxQuality = 51;

        /// <summary>Folder tail that matches Snipping Tool's default (decision P2).</summary>
        private const string FolderSuffix = @"\Videos\Screen Recordings";

        /// <summary>The one environment token that is substituted (parameterisation rule).</summary>
        private const string ProfileToken = "%USERPROFILE%";

        /// <summary>Section holding the recording settings (DESIGN.md section 8).</summary>
        private const string RecordSection = "record";

        /// <summary>Section holding the hotkey (DESIGN.md section 8).</summary>
        private const string InputSection = "input";

        /// <summary>The value of mic= that records no audio at all (DESIGN.md section 8).</summary>
        public const string MicOff = "off";

        /// <summary>
        /// The value of mic= that means "ask Windows for the default capture device
        /// each time a recording starts" (DESIGN.md section 5d).
        /// </summary>
        /// <remarks>
        /// Also what an absent key means: the user switches audio hardware and
        /// expects "whatever is default now", so the default has to be the file's
        /// default too, or every fresh install would pin one device name.
        /// </remarks>
        public const string MicDefault = "default";

        /// <summary>The value of system_audio= that enables WASAPI loopback capture.</summary>
        /// <remarks>
        /// Two keywords only, because system audio follows the default render
        /// endpoint and has no device to name (DESIGN.md section 5d). Matched like
        /// every other value here (ignoring case) and normalised to this spelling,
        /// so the caller has one comparison.
        /// </remarks>
        public const string SystemAudioOn = "on";

        /// <summary>
        /// The value of system_audio= that records no system audio, and what an
        /// absent key means.
        /// </summary>
        /// <remarks>
        /// Off is the default so an existing config file, and the shipped sample,
        /// keep producing exactly the command line they produced before this
        /// feature existed.
        /// </remarks>
        public const string SystemAudioOff = "off";

        /// <summary>
        /// Parses config.ini text into a config plus one note per rejected value.
        /// </summary>
        /// <param name="iniText">
        /// Whole file content, or null/empty when the file does not exist yet.
        /// CRLF and LF are both accepted, and so is a UTF-8 byte-order mark in
        /// front of the file (see <see cref="StripByteOrderMark"/>).
        /// </param>
        /// <param name="userProfile">
        /// Value used for %USERPROFILE% expansion and for the default folder -
        /// injected so this function never reads the process environment.
        /// </param>
        /// <returns>
        /// A config whose values are either what the file asked for verbatim or
        /// the DESIGN.md section 8 default, plus the notes to show on startup.
        /// </returns>
        /// <remarks>
        /// Side effects: none. Never throws: null text, a section-less file, a
        /// duplicated key and every malformed line are handled as described in
        /// the file header. Duplicated keys are resolved in file order, so the
        /// last occurrence supplies the value (INI convention) while an earlier
        /// rejected occurrence keeps its note - the user did write an
        /// unsupported value and the note is the only trace of it.
        /// </remarks>
        public static Config Parse(string iniText, string userProfile)
        {
            List<string> notes = new List<string>();

            // The defaults are the values of an empty file (C1), so the body below
            // only ever overwrites one of them with an accepted value.
            int fps = FpsValue;
            // What the file asked for, even when rejected. The settings panel shows
            // that raw value as "Custom"; the effective fps above is what records.
            int requestedFps = FpsValue;
            int quality = DefaultQuality;
            string folder = DefaultFolder(userProfile);
            string ffmpegPath = string.Empty;
            string microphone = MicDefault;
            bool systemAudio = false;
            HotkeySpec hotkey = HotkeySpec.Default;

            // Null until the first [section] header: a key before it belongs to no
            // section DESIGN.md section 8 defines, so it is reported rather than
            // guessed at.
            string section = null;

            foreach (string rawLine in SplitLines(StripByteOrderMark(iniText)))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                char first = line[0];
                if (first == ';' || first == '#')
                {
                    continue;
                }

                if (first == '[')
                {
                    int close = line.IndexOf(']');
                    if (close < 0)
                    {
                        notes.Add("ignoring a section header without ']': " + line);
                        continue;
                    }
                    section = line.Substring(1, close - 1).Trim();
                    continue;
                }

                if (section == null)
                {
                    notes.Add("ignoring '" + line + "' because it appears before any [section] header");
                    continue;
                }

                if (!IsName(section, RecordSection) && !IsName(section, InputSection))
                {
                    // Forward compatibility, not a mistake: DESIGN.md section 8 has
                    // no [ui], and a future version may add a section this build
                    // must not complain about.
                    continue;
                }

                // "=" at index 0 means the line has no key at all, which is a typo
                // worth naming: silently ignoring it would hide "fps 60" written
                // with a space instead of "=".
                int separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    notes.Add("ignoring a line without a key and '=': " + line);
                    continue;
                }

                string key = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim();

                if (IsName(key, "fps"))
                {
                    // 60 is the default and 30 is the one other supported value;
                    // everything else is replaced by 60 and named in a note, which
                    // is the entire observable effect of a bad value (D1 as
                    // amended). The 30 path deliberately produces no note: it is
                    // an ordinary choice, not a fallback. A numeric value is kept
                    // in RequestedFps even when rejected, so the settings panel can
                    // show the file's "Custom (24 fps)" without applying it.
                    int parsed;
                    if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed))
                    {
                        requestedFps = parsed;
                        if (FfmpegArgs.IsSupportedFps(parsed))
                        {
                            fps = parsed;
                            continue;
                        }
                    }
                    // A rejected occurrence resolves the key to the default, even
                    // when an earlier duplicate was valid: the last occurrence
                    // supplies the value (INI convention) and its value is invalid.
                    fps = FpsValue;
                    notes.Add("fps=" + value + " rejected: must be 60 (default) or 30; using 60");
                    continue;
                }

                if (IsName(key, "quality"))
                {
                    int parsed;
                    if (value.Length > 0
                        && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed)
                        && parsed >= MinQuality
                        && parsed <= MaxQuality)
                    {
                        quality = parsed;
                    }
                    else
                    {
                        quality = DefaultQuality;
                        notes.Add("quality=" + value + " rejected: must be 1-51; using 19");
                    }
                    continue;
                }

                if (IsName(key, "folder"))
                {
                    if (value.Length == 0)
                    {
                        folder = DefaultFolder(userProfile);
                        notes.Add("folder= rejected: an empty folder is unusable; using " + folder);
                    }
                    else
                    {
                        folder = ExpandUserProfile(value, userProfile);
                    }
                    continue;
                }

                if (IsName(key, "ffmpeg_path"))
                {
                    // Empty is not a bad value here: DESIGN.md section 8 defines it
                    // as "auto-resolve", which is what the shipped file contains,
                    // so a note would fire on every clean install. Stored verbatim;
                    // existence is WO-12's question, not this parser's.
                    ffmpegPath = value;
                    continue;
                }

                if (IsName(key, "mic"))
                {
                    // Two keywords and free text. "off" and "default" are matched
                    // like every other name here (ignoring case) and normalised to
                    // the lowercase spelling, so the caller has one comparison.
                    // Anything else IS the device name and is kept verbatim: dshow
                    // matches it character for character, and only ffmpeg can say
                    // whether it exists - the same separation as ffmpeg_path. An
                    // empty value is neither keyword nor a name, so it is a note
                    // plus the default, exactly like an empty folder=.
                    if (value.Length == 0)
                    {
                        microphone = MicDefault;
                        notes.Add("mic= rejected: needs off, default or a device name; using default");
                    }
                    else if (IsName(value, MicOff))
                    {
                        microphone = MicOff;
                    }
                    else if (IsName(value, MicDefault))
                    {
                        microphone = MicDefault;
                    }
                    else
                    {
                        microphone = value;
                    }
                    continue;
                }

                if (IsName(key, "system_audio"))
                {
                    // Two keywords and nothing else: system audio follows the
                    // default render endpoint, so there is no device name to store
                    // (DESIGN.md section 5d). Both are accepted silently - an
                    // ordinary on/off choice must not greet startup with a note -
                    // while an empty or unknown value is the section 8 rule for a
                    // known key: one note plus the default. The default is off, so a
                    // rejected value can never start capturing audio the user did
                    // not ask for.
                    if (IsName(value, SystemAudioOn))
                    {
                        systemAudio = true;
                    }
                    else if (IsName(value, SystemAudioOff))
                    {
                        systemAudio = false;
                    }
                    else
                    {
                        systemAudio = false;
                        notes.Add("system_audio=" + value
                            + " rejected: must be on or off; using off");
                    }
                    continue;
                }

                if (IsName(key, "hotkey"))
                {
                    HotkeySpec parsed;
                    if (value.Length > 0 && HotkeySpec.TryParse(value, out parsed))
                    {
                        hotkey = parsed;
                    }
                    else
                    {
                        hotkey = HotkeySpec.Default;
                        // Names both requirements TryParse enforces, because the one
                        // note covers two different mistakes: a bare key ("S") is a
                        // well-formed key missing a modifier, while "Ctrl+Banana" is
                        // a modifier with no valid key. A message that only mentioned
                        // one of them would send the user hunting for the other.
                        notes.Add("hotkey=" + value + " rejected: needs at least one modifier"
                            + " (Ctrl/Alt/Shift/Win) and a key from A-Z, 0-9 or F1-F24; using "
                            + HotkeySpec.Default);
                    }
                    continue;
                }

                // Any other key: ignored with no note (DESIGN.md section 8).
            }

            return new Config(
                fps, requestedFps, quality, folder, ffmpegPath, microphone, systemAudio, hotkey, notes);
        }

        /// <summary>Stores already-validated settings; use <see cref="Parse"/>.</summary>
        /// <param name="fps">Accepted frame rate: 60 or 30.</param>
        /// <param name="requestedFps">Value the file asked for, including a rejected one; 60 when absent.</param>
        /// <param name="quality">NVENC -cq in 1-51.</param>
        /// <param name="folder">Output folder, absolute unless the file said otherwise.</param>
        /// <param name="ffmpegPath">Configured ffmpeg path; empty means auto-resolve.</param>
        /// <param name="microphone">"off", "default", or a device name stored verbatim.</param>
        /// <param name="systemAudio">True when system_audio=on was in effect.</param>
        /// <param name="hotkey">Parsed chord.</param>
        /// <param name="notes">Startup notes, copied into a read-only view.</param>
        /// <remarks>Side effects: allocates the read-only notes view.</remarks>
        private Config(
            int fps, int requestedFps, int quality, string folder, string ffmpegPath,
            string microphone, bool systemAudio, HotkeySpec hotkey, List<string> notes)
        {
            Fps = fps;
            RequestedFps = requestedFps;
            Quality = quality;
            Folder = folder;
            FfmpegPath = ffmpegPath;
            Microphone = microphone;
            SystemAudio = systemAudio;
            Hotkey = hotkey;
            Notes = notes.AsReadOnly();
        }

        /// <summary>Frame rate to record with: 60 by default, 30 when the file asks for it (decision D1).</summary>
        public int Fps { get; private set; }

        /// <summary>
        /// Frame rate the file asked for, even when it was rejected; 60 when absent.
        /// </summary>
        /// <remarks>
        /// Exists only so the overlay's settings panel can show a hand-edited
        /// <c>fps=24</c> as "Custom (24 fps)" without claiming it is in effect.
        /// <see cref="Fps"/> is the value that records; this one is display-only and
        /// is never passed to <see cref="FfmpegArgs.Build"/>.
        /// </remarks>
        public int RequestedFps { get; private set; }

        /// <summary>NVENC constant-quality value, 1-51; lower is better quality.</summary>
        public int Quality { get; private set; }

        /// <summary>
        /// Folder the recording is written to, with %USERPROFILE% already expanded.
        /// </summary>
        /// <remarks>Not created and not checked here (WO-12).</remarks>
        public string Folder { get; private set; }

        /// <summary>
        /// Configured ffmpeg executable path, stored verbatim.
        /// </summary>
        /// <remarks>
        /// Empty means "resolve automatically" (DESIGN.md section 5a). Never
        /// validated for existence here - a config parse has no filesystem.
        /// </remarks>
        public string FfmpegPath { get; private set; }

        /// <summary>
        /// The microphone choice: <see cref="MicOff"/>, <see cref="MicDefault"/>, or
        /// a device name exactly as the file spelled it.
        /// </summary>
        /// <remarks>
        /// The two keywords are normalised to lowercase at parse time, so an ordinal
        /// comparison is enough and the caller has exactly one representation to
        /// reason about. The keywords win over a device that happens to be spelled
        /// "off" or "default": a keyword that could be shadowed by hardware would
        /// make "record no audio" impossible to express reliably.
        /// </remarks>
        public string Microphone { get; private set; }

        /// <summary>
        /// True when the next recording also captures what the default render
        /// endpoint is playing (WASAPI loopback, DESIGN.md section 5d).
        /// </summary>
        /// <remarks>
        /// Off by default, so an absent key or an existing file keeps the validated
        /// video-only (or microphone-only) command line. There is no device name to
        /// store: loopback binds to whatever Windows has as the default
        /// <em>render</em> device when the stream starts, which is the whole reason
        /// this feature uses loopback rather than Stereo Mix.
        /// </remarks>
        public bool SystemAudio { get; private set; }

        /// <summary>True when the file (or the overlay) turned microphone audio off.</summary>
        /// <remarks>
        /// Side effects: none. Equivalent to <c>Microphone == MicOff</c>; exists so
        /// the recording path never has to restate the keyword comparison.
        /// </remarks>
        public bool MicIsOff
        {
            get { return string.Equals(Microphone, MicOff, StringComparison.Ordinal); }
        }

        /// <summary>True when the microphone must be resolved from Windows at each recording start.</summary>
        /// <remarks>
        /// Side effects: none. False for both "off" and an explicit device name, so
        /// a caller that checks this before resolving cannot accidentally pin a
        /// configured device to the default one.
        /// </remarks>
        public bool MicIsDefault
        {
            get { return string.Equals(Microphone, MicDefault, StringComparison.Ordinal); }
        }

        /// <summary>The chord to register; the default when the file's value was unusable.</summary>
        /// <remarks>
        /// This instance carries the Win32 mask and virtual-key code, so the
        /// registration call passes them through without re-interpreting the text
        /// (WO-06).
        /// </remarks>
        public HotkeySpec Hotkey { get; private set; }

        /// <summary>
        /// One human-readable line per rejected value, in file order; empty when
        /// the file was clean.
        /// </summary>
        /// <remarks>
        /// Shown once on startup as tray text or a log line, never as a modal
        /// dialog (DESIGN.md section 8). Unknown keys and unknown sections do not
        /// appear here by design.
        /// </remarks>
        public IReadOnlyList<string> Notes { get; private set; }

        /// <summary>
        /// Removes one UTF-8 byte-order mark from the start of the document.
        /// </summary>
        /// <param name="iniText">Whole file content; may be null.</param>
        /// <returns>The text without a leading U+FEFF; unchanged otherwise.</returns>
        /// <remarks>
        /// Side effects: none. Notepad still offers "UTF-8 with BOM", and DESIGN.md
        /// section 8 calls this file human-editable, so the encoding a user picks
        /// must not change the meaning. U+FEFF is not whitespace to .NET, so
        /// Trim() keeps it and "[record]" would never match a section name: every
        /// key would be ignored and a note per line would replace the user's
        /// settings. Exactly one character, at the document start, is removed - a
        /// U+FEFF anywhere else is the file's own data, and stripping per line or
        /// per occurrence would corrupt a value the user typed.
        /// </remarks>
        private static string StripByteOrderMark(string iniText)
        {
            if (string.IsNullOrEmpty(iniText) || iniText[0] != '\uFEFF')
            {
                return iniText;
            }
            return iniText.Substring(1);
        }

        /// <summary>
        /// Splits the file into lines without assuming a line ending.
        /// </summary>
        /// <param name="iniText">Whole file content; may be null.</param>
        /// <returns>One entry per line; empty for null or empty text.</returns>
        /// <remarks>
        /// Side effects: none. The split is on '\n' only because every line is
        /// trimmed afterwards, and trim removes the '\r' of a CRLF pair - so CRLF
        /// and LF input take the same path (C6) instead of one of them leaving a
        /// stray '\r' inside every value.
        /// </remarks>
        private static string[] SplitLines(string iniText)
        {
            if (string.IsNullOrEmpty(iniText))
            {
                return new string[0];
            }
            return iniText.Split('\n');
        }

        /// <summary>Compares a section or key name the way Windows compares file names: ignoring case.</summary>
        /// <param name="left">Name from the file.</param>
        /// <param name="right">Known name, for example "quality".</param>
        /// <returns>True when the names match ignoring case.</returns>
        /// <remarks>Side effects: none.</remarks>
        private static bool IsName(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Builds the default output folder for a profile path.</summary>
        /// <param name="userProfile">Injected profile path; may be null.</param>
        /// <returns>
        /// "<paramref name="userProfile"/>\Videos\Screen Recordings", or the
        /// unexpanded token plus that tail when no profile was supplied.
        /// </returns>
        /// <remarks>
        /// Side effects: none. The empty-profile case keeps the token instead of
        /// producing a relative path that would silently write somewhere
        /// unexpected; the caller always has a profile (Windows guarantees one),
        /// so this is a degenerate-input guard, not a supported mode.
        /// </remarks>
        private static string DefaultFolder(string userProfile)
        {
            string root = ProfileRoot(userProfile);
            return root.Length == 0 ? ProfileToken + FolderSuffix : root + FolderSuffix;
        }

        /// <summary>Normalises an injected profile path for concatenation.</summary>
        /// <param name="userProfile">Profile path; may be null.</param>
        /// <returns>The path without surrounding space or trailing separators; empty when absent.</returns>
        /// <remarks>
        /// Side effects: none. The trailing separator is dropped so "C:\Users\x\"
        /// does not produce "C:\Users\x\\Videos\...".
        /// </remarks>
        private static string ProfileRoot(string userProfile)
        {
            return userProfile == null ? string.Empty : userProfile.Trim().TrimEnd('\\', '/');
        }

        /// <summary>Substitutes the injected profile for the one recognised token.</summary>
        /// <param name="value">Trimmed value from the file.</param>
        /// <param name="userProfile">Injected profile path; may be null.</param>
        /// <returns>
        /// The value with every %USERPROFILE% replaced, or the value unchanged when
        /// there is no profile to substitute.
        /// </returns>
        /// <remarks>
        /// Side effects: none. Only this one token is expanded: any other %VAR% is
        /// left literally and produces no note, because a config that reads
        /// arbitrary environment variables is a different feature (C7). Matching
        /// ignores case, the way Windows environment names do, so a hand-typed
        /// %userprofile% behaves like the shipped spelling. The search resumes
        /// past the inserted text, so a path repeated twice is expanded twice and
        /// the loop cannot spin.
        /// </remarks>
        private static string ExpandUserProfile(string value, string userProfile)
        {
            string root = ProfileRoot(userProfile);
            if (root.Length == 0)
            {
                return value;
            }

            StringBuilder expanded = new StringBuilder(value.Length + root.Length);
            int copied = 0;
            int index = value.IndexOf(ProfileToken, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                expanded.Append(value, copied, index - copied);
                expanded.Append(root);
                copied = index + ProfileToken.Length;
                index = value.IndexOf(ProfileToken, copied, StringComparison.OrdinalIgnoreCase);
            }
            expanded.Append(value, copied, value.Length - copied);
            return expanded.ToString();
        }
    }
}
