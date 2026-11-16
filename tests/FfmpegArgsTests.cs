// FfmpegArgsTests - WO-03: the validated ffmpeg command line of DESIGN.md section 5/5a.
//
// Oracle provenance: every expected string below is transcribed from DESIGN.md
// section 5 (the validated invocation), section 5a (-progress pipe:1), PLAN.md
// AC2/AC9 and AGENTS.md gotchas 1, 2 and 5. Nothing is copied from FfmpegArgs'
// own output, and the numeric expectations are literals rather than
// FfmpegArgs.RecordingFps, so changing the constant must fail a check here.
//
// Two kinds of check are mixed on purpose:
//   * presence/order checks (C1, C2, C4, C5, C9) catch a flag that went missing;
//   * absence checks (C3) catch a flag that came back. An absence check is
//     vacuously green on an empty argument list, so C3 is paired with a
//     mutation run in the work-order report - that is its red evidence.
//
// The quoting criteria (C6, C7) are verified twice: against the literal
// CommandLineToArgvW escaping, and by round-tripping the real argv through a
// parser written here from the documented rules. The parser is the test's own
// reference implementation, not a copy of the production code.
using System;
using System.Collections.Generic;
using System.Text;
using SnipVid.Logic;

namespace SnipVid.Tests
{
    /// <summary>
    /// Acceptance checks for <see cref="FfmpegArgs"/>: one test method per work
    /// order criterion, with one check per independently observable fact.
    /// </summary>
    public static class FfmpegArgsTests
    {
        /// <summary>
        /// The real output location (DESIGN.md section 7). It contains a space on
        /// purpose: this is the argument quoting exists for.
        /// </summary>
        private const string OutputPath =
            @"C:\Users\x\Videos\Screen Recordings\SnipVid_2026-11-15_143002.mp4";

        /// <summary>The region from WO-02 C1 / PLAN AC2: 100,200 at 640x450.</summary>
        private static readonly PxRect Region = new PxRect(100, 200, 640, 450);

        /// <summary>
        /// The request DESIGN.md section 5 was validated with: monitor 0, the AC2
        /// region, 60 fps and the default -cq 19.
        /// </summary>
        private static FfmpegRequest DesignRequest()
        {
            return new FfmpegRequest(OutputPath, 0, Region, 60, 19);
        }

        /// <summary>C1 (AC2): the ddagrab input string is exact, parameter order included.</summary>
        /// <remarks>
        /// Hand-transcribed from DESIGN.md section 5. Order matters because the
        /// string is compared as a whole, which is the point: a reordered or
        /// re-spelled filter option is a different filter graph.
        /// </remarks>
        public static void TestC1_DdagrabStringMatchesDesign()
        {
            string expected =
                "ddagrab=output_idx=0:framerate=60:offset_x=100:offset_y=200:video_size=640x450:draw_mouse=1";
            string[] args = FfmpegArgs.Build(DesignRequest());

            Harness.Check(
                "C1 monitor 0 region 100,200 640x450 yields exactly " + expected,
                Array.IndexOf(args, expected) >= 0,
                "expected the element " + expected + ", argv was " + Show(args));
        }

        /// <summary>C2: -fps_mode is immediately followed by cfr (gotcha 2).</summary>
        /// <remarks>
        /// Adjacency, not mere presence: "-fps_mode" with any other following
        /// token selects a different mode, and the file silently goes VFR.
        /// </remarks>
        public static void TestC2_FpsModeIsImmediatelyFollowedByCfr()
        {
            string[] args = FfmpegArgs.Build(DesignRequest());
            int index = Array.IndexOf(args, "-fps_mode");
            bool ok = index >= 0 && index + 1 < args.Length && args[index + 1] == "cfr";

            Harness.Check(
                "C2 -fps_mode is immediately followed by cfr (gotcha 2: otherwise the file is VFR)",
                ok,
                index < 0
                    ? "no -fps_mode element; argv was " + Show(args)
                    : "-fps_mode at " + index + " is followed by "
                        + (index + 1 < args.Length ? "'" + args[index + 1] + "'" : "nothing")
                        + "; argv was " + Show(args));
        }

        /// <summary>C3: no -pix_fmt, no -vf, no crop anywhere (gotchas 1 and 5).</summary>
        /// <remarks>
        /// Three separate checks so a failure names the exact trap and the exact
        /// gotcha it re-opens: -pix_fmt makes ddagrab to h264_nvenc die with
        /// -40 before a single frame, a filter forces a GPU to CPU download, and
        /// crop is redundant because ddagrab crops in the capture itself. All
        /// three fail silently rather than loudly, which is why absence is
        /// asserted here instead of relying on a smoke test.
        /// </remarks>
        public static void TestC3_ForbiddenArgumentsAreAbsent()
        {
            string[] args = FfmpegArgs.Build(DesignRequest());

            Harness.Check(
                "C3 no -pix_fmt anywhere (AGENTS gotcha 1: with ddagrab and h264_nvenc it dies with -40 before one frame)",
                NoToken(args, "-pix_fmt"),
                "argv was " + Show(args));

            Harness.Check(
                "C3 no -vf anywhere (a filter graph forces a GPU to CPU download and is measurably the wrong path)",
                NoToken(args, "-vf"),
                "argv was " + Show(args));

            Harness.Check(
                "C3 no crop anywhere (AGENTS gotcha 5: ddagrab crops natively via offset_x, offset_y and video_size)",
                NoToken(args, "crop"),
                "argv was " + Show(args));
        }

        /// <summary>C4: every option of DESIGN.md section 5 and 5a is present, with its value.</summary>
        /// <remarks>
        /// Each option is checked as a pair (option, value) so a renamed value
        /// such as "-preset p4" fails rather than passing on the flag alone. -y
        /// and -hide_banner take no value and are checked as exact elements.
        ///
        /// -g 60 and -flush_packets 1 are part of this table because DESIGN.md
        /// section 5 makes them mandatory for crash safety, and the failure they
        /// prevent cannot be seen in a normal run: without both, a killed ffmpeg
        /// leaves a 36-byte unplayable file instead of playable fragments
        /// (measured 2026-11-15). Each is asserted adjacent to its value, in the
        /// group the design puts it in - -g 60 with the encoder options,
        /// -flush_packets 1 with the muxer options - because the pair, not the
        /// bare flag, is what ffmpeg acts on.
        /// </remarks>
        public static void TestC4_ValidatedOptionsArePresent()
        {
            string[] args = FfmpegArgs.Build(DesignRequest());
            List<string> missing = new List<string>();

            Require(args, "-c:v", "h264_nvenc", missing);
            Require(args, "-preset", "p5", missing);
            Require(args, "-tune", "hq", missing);
            Require(args, "-cq", "19", missing);

            // Encoder options: a fragment boundary every 60 frames, so a killed
            // process has a flushed fragment to fall back on (DESIGN.md section 5).
            Require(args, "-g", "60", missing);

            Require(args, "-movflags", "+frag_keyframe+empty_moov", missing);

            // Muxer options: push each fragment to disk instead of leaving it in
            // the muxer's buffer until the trailer (DESIGN.md section 5).
            Require(args, "-flush_packets", "1", missing);

            Require(args, "-progress", "pipe:1", missing);
            Require(args, "-stats_period", "0.05", missing);
            Require(args, "-f", "lavfi", missing);
            Require(args, "-loglevel", "error", missing);
            Require(args, "-i", null, missing);
            RequireFlag(args, "-y", missing);
            RequireFlag(args, "-hide_banner", missing);

            Harness.Check(
                "C4 DESIGN 5 and 5a options are all present with their values",
                missing.Count == 0,
                "missing " + string.Join(", ", missing.ToArray()) + "; argv was " + Show(args));

            // The group placement, not just presence: -g belongs to the encoder
            // options before the muxer flags, and -flush_packets to the muxer
            // options after -movflags. Reordering them is not merely cosmetic -
            // it moves the flag to a different option group in a reader's head
            // and makes "which half of the fragmentation pair is which"
            // unanswerable. Both must still precede the output path.
            int codec = Array.IndexOf(args, "-c:v");
            int g = Array.IndexOf(args, "-g");
            int fpsMode = Array.IndexOf(args, "-fps_mode");
            int movflags = Array.IndexOf(args, "-movflags");
            int flush = Array.IndexOf(args, "-flush_packets");
            int output = args.Length - 1;

            Harness.Check(
                "C4 -g 60 sits with the encoder options (after -c:v, before -fps_mode), as one adjacent pair",
                codec >= 0 && g > codec && g + 1 < args.Length && args[g + 1] == "60"
                    && g < fpsMode && g < output,
                "-c:v at " + codec + ", -g at " + g + ", -fps_mode at " + fpsMode
                    + "; argv was " + Show(args));

            Harness.Check(
                "C4 -flush_packets 1 sits with the muxer options (after -movflags, before the output path), as one adjacent pair",
                movflags >= 0 && flush > movflags && flush + 1 < args.Length
                    && args[flush + 1] == "1" && flush < output,
                "-movflags at " + movflags + ", -flush_packets at " + flush
                    + ", output at " + output + "; argv was " + Show(args));
        }

        /// <summary>C5 (AC9, decision D1): a requested 120 fps still encodes at 60.</summary>
        /// <remarks>
        /// 60 is the only accepted rate, so the builder must not forward the
        /// request. A 120 fps container of 60 fps content is twice the bytes for
        /// an indistinguishable result (DESIGN.md section 8); the startup note
        /// that tells the user is WO-04's job, not this function's.
        /// </remarks>
        public static void TestC5_Requested120FpsStillEncodes60()
        {
            string[] args = FfmpegArgs.Build(new FfmpegRequest(OutputPath, 0, Region, 120, 19));
            bool framerate60 = false;
            foreach (string arg in args)
            {
                if (arg != null && arg.IndexOf("framerate=60", StringComparison.Ordinal) >= 0)
                {
                    framerate60 = true;
                }
            }

            int index = Array.IndexOf(args, "-fps_mode");
            bool fpsMode60 = index >= 0 && index + 1 < args.Length && args[index + 1] == "cfr";

            Harness.Check(
                "C5 fps=120 still yields framerate=60 and -fps_mode cfr (PLAN AC9, decision D1)",
                framerate60 && fpsMode60 && NoToken(args, "framerate=120"),
                "framerate=60 present " + framerate60 + ", -fps_mode cfr present " + fpsMode60
                    + ", framerate=120 absent " + NoToken(args, "framerate=120")
                    + "; argv was " + Show(args));
        }

        /// <summary>C10 (PLAN D1 as amended): a request for 60 yields framerate=60 and CFR.</summary>
        /// <remarks>
        /// The default is unchanged and must keep producing the validated command
        /// line. Both halves are checked because either one alone is a different
        /// capture: framerate is the capture cadence, -fps_mode cfr is what stops
        /// the file being VFR (gotcha 2).
        /// </remarks>
        public static void TestC10_Requested60FpsYieldsFramerate60()
        {
            string[] args = FfmpegArgs.Build(new FfmpegRequest(OutputPath, 0, Region, 60, 19));
            int index = Array.IndexOf(args, "-fps_mode");
            bool cfr = index >= 0 && index + 1 < args.Length && args[index + 1] == "cfr";
            bool framerate60 = HasFragment(args, "framerate=60");

            Harness.Check(
                "C10 fps=60 yields framerate=60, -fps_mode cfr, and no framerate=30",
                framerate60 && cfr && !HasFragment(args, "framerate=30"),
                "framerate=60 " + framerate60 + ", -fps_mode cfr " + cfr
                    + ", framerate=30 absent " + !HasFragment(args, "framerate=30")
                    + "; argv was " + Show(args));
        }

        /// <summary>C11 (PLAN D1 as amended): a request for 30 yields framerate=30, CFR and -g 60.</summary>
        /// <remarks>
        /// 30 is explicit, not a fallback: the capture cadence changes, so the
        /// ddagrab string is asserted as a whole. -g 60 stays because it is the
        /// keyframe/fragment cadence the crash-recovery path was measured with; at
        /// 30 fps it is a fragment every 2 s, still bounded. The exact string is
        /// hand-transcribed from DESIGN.md section 5 with only the framerate value
        /// changed.
        /// </remarks>
        public static void TestC11_Requested30FpsYieldsFramerate30()
        {
            string[] args = FfmpegArgs.Build(new FfmpegRequest(OutputPath, 0, Region, 30, 19));
            string expected =
                "ddagrab=output_idx=0:framerate=30:offset_x=100:offset_y=200:video_size=640x450:draw_mouse=1";
            int index = Array.IndexOf(args, "-fps_mode");
            int g = Array.IndexOf(args, "-g");
            bool cfr = index >= 0 && index + 1 < args.Length && args[index + 1] == "cfr";
            bool keyframes = g >= 0 && g + 1 < args.Length && args[g + 1] == "60";

            Harness.Check(
                "C11 fps=30 yields the exact ddagrab string with framerate=30",
                Array.IndexOf(args, expected) >= 0,
                "expected the element " + expected + ", argv was " + Show(args));

            Harness.Check(
                "C11 fps=30 keeps -fps_mode cfr and -g 60",
                cfr && keyframes,
                "-fps_mode cfr " + cfr + ", -g 60 " + keyframes
                    + "; argv was " + Show(args));

            Harness.Check(
                "C11 fps=30 does not leave a framerate=60 in the filter",
                !HasFragment(args, "framerate=60"),
                "; argv was " + Show(args));
        }

        /// <summary>C12 (PLAN D1): anything other than 30 or 60 still yields 60.</summary>
        /// <remarks>
        /// This is the narrowing half of the amendment: 30 becoming a choice must
        /// not turn the builder into a free-for-all. 120 is the documented
        /// rejection; 25 and 0 are values a hand-edited file or a stale caller can
        /// produce. Every one of them must come back as the validated 60 fps
        /// command, never as its own framerate.
        /// </remarks>
        public static void TestC12_Only30And60AreHonoured()
        {
            int[] rejected = { 120, 25, 0 };
            foreach (int fps in rejected)
            {
                string[] args = FfmpegArgs.Build(new FfmpegRequest(OutputPath, 0, Region, fps, 19));
                bool framerate60 = HasFragment(args, "framerate=60");
                bool noOther = !HasFragment(args, "framerate=" + fps.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    || fps == 60;

                Harness.Check(
                    "C12 fps=" + fps + " falls back to framerate=60 and forwards no framerate=" + fps,
                    framerate60 && noOther,
                    "framerate=60 " + framerate60 + ", framerate=" + fps + " absent " + noOther
                        + "; argv was " + Show(args));
            }
        }

        /// <summary>C6: the spaced output path is quoted and nothing else is.</summary>
        /// <remarks>
        /// The quote count is part of the check: escaping every argument would
        /// also "pass" a naive ends-with assertion while feeding ffmpeg quoted
        /// flags, which is a different bug.
        /// </remarks>
        public static void TestC6_OutputPathWithSpacesIsQuoted()
        {
            string line = FfmpegArgs.ToCommandLine(FfmpegArgs.Build(DesignRequest()));
            string expectedTail = "\"" + OutputPath + "\"";
            int quotes = 0;
            foreach (char c in line)
            {
                if (c == '"')
                {
                    quotes++;
                }
            }

            Harness.Check(
                "C6 output path with spaces is wrapped in quotes and no other argument is quoted",
                line.EndsWith(expectedTail, StringComparison.Ordinal) && quotes == 2,
                "expected a line ending with " + expectedTail + " and exactly 2 quote characters, actual ("
                    + quotes + " quotes): " + line);

            string bare = FfmpegArgs.ToCommandLine(new string[] { "-i", @"C:\clips\a.mp4" });
            Harness.Check(
                "C6 an argument with no space stays unquoted even though it has backslashes",
                bare == @"-i C:\clips\a.mp4",
                @"expected -i C:\clips\a.mp4, actual " + bare);
        }

        /// <summary>C7: a trailing backslash is doubled so the argument survives one round trip.</summary>
        /// <remarks>
        /// The classic CommandLineToArgvW bug: "C:\a b\" is read by the child as
        /// C:\a b" because the backslash escapes the closing quote. Expected
        /// strings are hand-derived from the documented rule (2n backslashes
        /// before a quote become n backslashes; 2n+1 become n plus a literal
        /// quote), then confirmed by parsing the line back.
        /// </remarks>
        public static void TestC7_TrailingBackslashIsDoubled()
        {
            string line = FfmpegArgs.ToCommandLine(new string[] { @"C:\a b\" });
            Harness.Check(
                "C7 one trailing backslash becomes two before the closing quote",
                line == @"""C:\a b\\""",
                @"expected ""C:\a b\\"" as the whole line, actual " + line);

            string doubled = FfmpegArgs.ToCommandLine(new string[] { @"C:\a b\\" });
            string quoted = FfmpegArgs.ToCommandLine(new string[] { @"C:\a b\""c" });
            bool samples = doubled == @"""C:\a b\\\\"""
                && quoted == @"""C:\a b\\\""c"""
                && RoundTrips(new string[] { @"C:\a b\", @"C:\a b\\", @"C:\a b\""c" });
            Harness.Check(
                "C7 two and three trailing backslashes double too, and an embedded quote stays literal",
                samples,
                @"two: expected ""C:\a b\\\\"", actual " + doubled
                    + @"; embedded: expected ""C:\a b\\\""c"", actual " + quoted);

            string[] real = FfmpegArgs.Build(DesignRequest());
            Harness.Check(
                "C7 the full argument list survives ToCommandLine unchanged",
                RoundTrips(real),
                "re-parsed argv was "
                    + Show(SplitCommandLine(FfmpegArgs.ToCommandLine(real)).ToArray()));
        }

        /// <summary>C8: an empty argument list renders as an empty string, without throwing.</summary>
        /// <remarks>
        /// Degenerate input, but reachable: the recorder builds its command line
        /// before it has a request, and a null reference there would surface as a
        /// crash instead of a rejected capture. A throw would be reported by the
        /// harness as a failed check for this method.
        /// </remarks>
        public static void TestC8_EmptyArrayIsEmptyString()
        {
            string line = FfmpegArgs.ToCommandLine(new string[0]);

            Harness.Check(
                "C8 an empty argv renders as the empty string",
                line == string.Empty,
                "expected an empty string, actual \"" + line + "\"");

            Harness.Check(
                "C8 a null argv is treated as empty rather than throwing",
                FfmpegArgs.ToCommandLine(null) == string.Empty,
                "expected an empty string for null");
        }

        /// <summary>C9: the output path is the final element and appears exactly once.</summary>
        /// <remarks>
        /// ffmpeg takes the last unnamed argument as the destination, so a second
        /// occurrence would silently become an extra output. Both halves are
        /// asserted: count and position.
        /// </remarks>
        public static void TestC9_OutputPathIsLastAndUnique()
        {
            string[] args = FfmpegArgs.Build(DesignRequest());
            int count = 0;
            int last = -1;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == OutputPath)
                {
                    count++;
                    last = i;
                }
            }

            Harness.Check(
                "C9 the output path appears exactly once, as the final element",
                count == 1 && args.Length > 1 && last == args.Length - 1,
                "count " + count + ", last index " + last + " of " + args.Length
                    + "; argv was " + Show(args));
        }

        /// <summary>
        /// C1 (audio): with no microphone the argv is byte-identical to the
        /// validated video-only capture.
        /// </summary>
        /// <remarks>
        /// Oracle: the complete argument list transcribed by hand from DESIGN.md
        /// section 5 plus the two progress options of section 5a, with the AC2
        /// region. Comparing the whole array, not just the interesting flags, is
        /// what "byte-identical" means for a request that asked for no audio: a
        /// stray empty element, a reordered option or a leftover -map would change
        /// the command the user's recordings are made with. The three absence
        /// checks are repeated here because C1 also promises none of the audio
        /// flags leak into the video-only path.
        /// </remarks>
        public static void TestC1_MicOffArgvIsByteIdentical()
        {
            string[] expected = new string[]
            {
                "-hide_banner",
                "-loglevel", "error",
                "-y",
                "-progress", "pipe:1",
                "-stats_period", "0.05",
                "-f", "lavfi",
                "-i",
                "ddagrab=output_idx=0:framerate=60:offset_x=100:offset_y=200:video_size=640x450:draw_mouse=1",
                "-c:v", "h264_nvenc",
                "-preset", "p5",
                "-tune", "hq",
                "-cq", "19",
                "-g", "60",
                "-fps_mode", "cfr",
                "-movflags", "+frag_keyframe+empty_moov",
                "-flush_packets", "1",
                OutputPath,
            };

            string[] args = FfmpegArgs.Build(DesignRequest());
            bool identical = args.Length == expected.Length;
            int mismatch = -1;
            for (int i = 0; identical && i < args.Length; i++)
            {
                if (args[i] != expected[i])
                {
                    identical = false;
                    mismatch = i;
                }
            }

            Harness.Check(
                "C1 mic off: argv is exactly the validated video-only command line, element for element",
                identical,
                mismatch >= 0
                    ? "first difference at index " + mismatch + ": expected ["
                        + expected[mismatch] + "] actual [" + args[mismatch] + "]"
                    : "length " + args.Length + " versus expected " + expected.Length
                        + "; argv was " + Show(args));

            Harness.Check(
                "C1 mic off: no -c:a, no -map and no dshow input",
                NoToken(args, "-c:a") && NoToken(args, "-map") && NoToken(args, "dshow"),
                "argv was " + Show(args));

            Harness.Check(
                "C1 mic off: exactly one input",
                CountToken(args, "-i") == 1,
                "count " + CountToken(args, "-i") + "; argv was " + Show(args));

            // The recorder forwards a resolved name, and "no device" reaches the
            // builder as either null (nothing to resolve) or the empty string (a
            // reader that had nothing to say). Both must mean the video-only
            // command, never "audio=" with nothing after it, which dshow would read
            // as a malformed device and fail the recording over.
            string[] empty = FfmpegArgs.Build(
                new FfmpegRequest(OutputPath, 0, Region, 60, 19, string.Empty));
            bool sameAsNull = empty.Length == args.Length;
            for (int i = 0; sameAsNull && i < args.Length; i++)
            {
                sameAsNull = empty[i] == args[i];
            }
            Harness.Check(
                "C1 an empty microphone string is treated as no audio, exactly like null",
                sameAsNull,
                "argv with an empty device was " + Show(empty));
        }

        /// <summary>
        /// C2 (audio): a microphone adds one dshow input after the ddagrab input
        /// and the AAC encode with an explicit video/audio map.
        /// </summary>
        /// <remarks>
        /// Oracle: the audio block in DESIGN.md section 5 - `-f dshow -i
        /// audio=NAME`, `-c:a aac -b:a 160k -ar 48000`, `-map 0:v -map 1:a`. The
        /// device name carries spaces and parentheses on purpose: that is what a
        /// real Windows microphone is called, and it is the argument quoting has
        /// to survive.
        ///
        /// Presence is checked as adjacent pairs and the order is checked too: the
        /// second input has to come after the first one, or `-map 1:a` addresses
        /// the wrong stream. The forbidden flags are re-asserted with audio on,
        /// because the audio path must not tempt anyone into a -vf or a -pix_fmt.
        /// </remarks>
        public static void TestC2_MicOnAddsDshowInputAndAudioEncode()
        {
            const string Device = "Microphone (Realtek(R) Audio)";
            string[] args = FfmpegArgs.Build(
                new FfmpegRequest(OutputPath, 0, Region, 60, 19, Device));

            int dshow = Array.IndexOf(args, "dshow");
            int ddagrab = -1;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] != null && args[i].StartsWith("ddagrab=", StringComparison.Ordinal))
                {
                    ddagrab = i;
                }
            }

            Harness.Check(
                "C2 mic on: -f dshow followed by -i audio=<device> immediately",
                dshow > 0
                    && dshow + 3 < args.Length
                    && args[dshow - 1] == "-f"
                    && args[dshow + 1] == "-i"
                    && args[dshow + 2] == "audio=" + Device,
                "dshow at " + dshow + "; argv was " + Show(args));

            Harness.Check(
                "C2 mic on: the dshow input comes after the ddagrab input",
                dshow > ddagrab && ddagrab >= 0,
                "ddagrab at " + ddagrab + ", dshow at " + dshow + "; argv was " + Show(args));

            Harness.Check(
                "C2 mic on: -map 0:v and -map 1:a select video from input 0 and audio from input 1",
                Adjacent(args, "-map", "0:v") && Adjacent(args, "-map", "1:a")
                    && Array.LastIndexOf(args, "0:v") < Array.LastIndexOf(args, "1:a"),
                "argv was " + Show(args));

            Harness.Check(
                "C2 mic on: -c:a aac with -b:a 160k and -ar 48000",
                Adjacent(args, "-c:a", "aac")
                    && Adjacent(args, "-b:a", "160k")
                    && Adjacent(args, "-ar", "48000"),
                "argv was " + Show(args));

            Harness.Check(
                "C2 mic on: every video-only option survives unchanged",
                Adjacent(args, "-c:v", "h264_nvenc")
                    && Adjacent(args, "-preset", "p5")
                    && Adjacent(args, "-tune", "hq")
                    && Adjacent(args, "-cq", "19")
                    && Adjacent(args, "-g", "60")
                    && Adjacent(args, "-fps_mode", "cfr")
                    && Adjacent(args, "-movflags", "+frag_keyframe+empty_moov")
                    && Adjacent(args, "-flush_packets", "1"),
                "argv was " + Show(args));

            Harness.Check(
                "C2 mic on: no -pix_fmt, no -vf and no crop filter (gotchas 1 and 5 still apply)",
                NoToken(args, "-pix_fmt") && NoToken(args, "-vf")
                    && Array.IndexOf(args, "crop") < 0 && NoToken(args, "crop="),
                "argv was " + Show(args));

            Harness.Check(
                "C2 mic on: exactly two inputs and the output path is still last and unique",
                CountToken(args, "-i") == 2
                    && args[args.Length - 1] == OutputPath
                    && CountToken(args, OutputPath) == 1,
                "inputs " + CountToken(args, "-i") + ", last [" + args[args.Length - 1] + "]"
                    + "; argv was " + Show(args));
        }

        /// <summary>
        /// C1 (system audio): with no microphone and no loopback pipe the argv is
        /// byte-identical to the validated video-only capture.
        /// </summary>
        /// <remarks>
        /// Oracle: the same complete array as <see cref="TestC1_MicOffArgvIsByteIdentical"/>,
        /// which is the command every existing recording is made with. The request
        /// here names both audio sources as absent explicitly - null microphone and
        /// null pipe - because that is what the recorder passes when both toggles are
        /// off, and a stray empty element or a leftover -map would change the command
        /// under the user's feet.
        /// </remarks>
        public static void TestC1_SystemAudioOffArgvIsByteIdenticalToToday()
        {
            string[] expected = new string[]
            {
                "-hide_banner",
                "-loglevel", "error",
                "-y",
                "-progress", "pipe:1",
                "-stats_period", "0.05",
                "-f", "lavfi",
                "-i",
                "ddagrab=output_idx=0:framerate=60:offset_x=100:offset_y=200:video_size=640x450:draw_mouse=1",
                "-c:v", "h264_nvenc",
                "-preset", "p5",
                "-tune", "hq",
                "-cq", "19",
                "-g", "60",
                "-fps_mode", "cfr",
                "-movflags", "+frag_keyframe+empty_moov",
                "-flush_packets", "1",
                OutputPath,
            };

            string[] args = FfmpegArgs.Build(
                new FfmpegRequest(OutputPath, 0, Region, 60, 19, null, null));
            bool identical = args.Length == expected.Length;
            int mismatch = -1;
            for (int i = 0; identical && i < args.Length; i++)
            {
                if (args[i] != expected[i])
                {
                    identical = false;
                    mismatch = i;
                }
            }

            Harness.Check(
                "C1 system audio off, mic off: argv is exactly the validated video-only command line",
                identical,
                mismatch >= 0
                    ? "first difference at index " + mismatch + ": expected ["
                        + expected[mismatch] + "] actual [" + args[mismatch] + "]"
                    : "length " + args.Length + " versus expected " + expected.Length
                        + "; argv was " + Show(args));

            Harness.Check(
                "C1 both audio sources off: no s16le input, no pipe, no -c:a, no -map",
                NoToken(args, "s16le") && NoToken(args, @"\\.\pipe\")
                    && NoToken(args, "-c:a") && NoToken(args, "-map") && NoToken(args, "-filter_complex"),
                "argv was " + Show(args));
        }

        /// <summary>
        /// C2 (system audio): a loopback pipe adds one raw PCM input after the
        /// ddagrab input, the AAC encode and `-map 0:v -map 1:a`.
        /// </summary>
        /// <remarks>
        /// Oracle: the pipe architecture in the system-audio work order - 48 kHz,
        /// 16-bit, stereo, explicitly typed with `-f s16le` so ffmpeg never probes
        /// the pipe; and section 5d's fixed AAC parameters, which keep every
        /// segment's audio stream identical for the -c copy join. The whole array is
        /// compared as well, because the first audio case must not reorder or drop
        /// any part of the validated video command.
        /// </remarks>
        public static void TestC2_SystemAudioOnAddsPipeInputAndMapsIt()
        {
            const string Pipe = @"\\.\pipe\snipvid-audio-2026-11-16_143002-437-seg-001";
            string[] args = FfmpegArgs.Build(
                new FfmpegRequest(OutputPath, 0, Region, 60, 19, null, Pipe));

            string[] expected = new string[]
            {
                "-hide_banner",
                "-loglevel", "error",
                "-y",
                "-progress", "pipe:1",
                "-stats_period", "0.05",
                "-f", "lavfi",
                "-i",
                "ddagrab=output_idx=0:framerate=60:offset_x=100:offset_y=200:video_size=640x450:draw_mouse=1",
                "-f", "s16le",
                "-ar", "48000",
                "-ac", "2",
                "-i", Pipe,
                "-c:v", "h264_nvenc",
                "-preset", "p5",
                "-tune", "hq",
                "-cq", "19",
                "-g", "60",
                "-fps_mode", "cfr",
                "-c:a", "aac",
                "-b:a", "160k",
                "-ar", "48000",
                "-map", "0:v",
                "-map", "1:a",
                "-movflags", "+frag_keyframe+empty_moov",
                "-flush_packets", "1",
                OutputPath,
            };

            bool identical = args.Length == expected.Length;
            int mismatch = -1;
            for (int i = 0; identical && i < args.Length; i++)
            {
                if (args[i] != expected[i])
                {
                    identical = false;
                    mismatch = i;
                }
            }

            Harness.Check(
                "C2 system audio on: argv is the video command plus one raw PCM pipe input, element for element",
                identical,
                mismatch >= 0
                    ? "first difference at index " + mismatch + ": expected ["
                        + expected[mismatch] + "] actual [" + args[mismatch] + "]"
                    : "length " + args.Length + " versus expected " + expected.Length
                        + "; argv was " + Show(args));

            Harness.Check(
                "C2 system audio on: -f s16le -ar 48000 -ac 2 -i <pipe> is one adjacent input group after ddagrab",
                Adjacent(args, "-f", "s16le")
                    && Adjacent(args, "-ar", "48000")
                    && Adjacent(args, "-ac", "2")
                    && Adjacent(args, "-i", Pipe),
                "argv was " + Show(args));

            Harness.Check(
                "C2 system audio on: exactly two inputs, the output path last and unique",
                CountToken(args, "-i") == 2
                    && args[args.Length - 1] == OutputPath
                    && CountToken(args, OutputPath) == 1,
                "inputs " + CountToken(args, "-i") + "; argv was " + Show(args));

            Harness.Check(
                "C2 system audio on: no -pix_fmt, no -vf and no crop filter (gotchas 1 and 5 still apply)",
                NoToken(args, "-pix_fmt") && NoToken(args, "-vf")
                    && Array.IndexOf(args, "crop") < 0 && NoToken(args, "crop="),
                "argv was " + Show(args));
        }

        /// <summary>
        /// C3 (system audio + microphone): both sources are mixed inside ffmpeg and
        /// one audio stream is mapped.
        /// </summary>
        /// <remarks>
        /// Oracle: the work order's mixing requirement - both sources mixed inside
        /// ffmpeg into one mapped audio stream. The filter string is
        /// <c>[1:a][2:a]amix=inputs=2:duration=longest[a]</c>, not the bare
        /// <c>amix=inputs=2:duration=longest</c> the work order sketched: measured
        /// 2026-11-16 against ffmpeg, a complex filtergraph does not auto-connect
        /// unlabelled inputs, and the bare form failed at spawn with
        /// <c>Error opening output files: Invalid argument</c>. The pipe is input 1
        /// and dshow input 2, so those are the pads. Exactly one audio map is the
        /// other half: a second `-map 1:a` or `-map 2:a` would write two audio
        /// tracks, which is not a mix.
        /// </remarks>
        public static void TestC3_SystemAndMicMixIntoOneAudioStream()
        {
            const string Pipe = @"\\.\pipe\snipvid-audio-2026-11-16_143002-437-seg-001";
            const string Device = "Microphone (Realtek(R) Audio)";
            string[] args = FfmpegArgs.Build(
                new FfmpegRequest(OutputPath, 0, Region, 60, 19, Device, Pipe));

            Harness.Check(
                "C3 system + mic: -filter_complex names both pads and amix=inputs=2:duration=longest",
                Adjacent(args, "-filter_complex", "[1:a][2:a]amix=inputs=2:duration=longest[a]"),
                "argv was " + Show(args));

            Harness.Check(
                "C3 system + mic: -map 0:v and -map [a] select video and the mixed audio stream",
                Adjacent(args, "-map", "0:v") && Adjacent(args, "-map", "[a]"),
                "argv was " + Show(args));

            Harness.Check(
                "C3 system + mic: exactly one audio map, so the file has one audio track",
                CountToken(args, "1:a") == 0 && CountToken(args, "2:a") == 0,
                "argv was " + Show(args));

            Harness.Check(
                "C3 system + mic: three inputs - ddagrab, the pipe, then dshow - with the output last",
                CountToken(args, "-i") == 3
                    && Adjacent(args, "-f", "s16le")
                    && Adjacent(args, "-i", Pipe)
                    && Adjacent(args, "-i", "audio=" + Device)
                    && args[args.Length - 1] == OutputPath,
                "inputs " + CountToken(args, "-i") + "; argv was " + Show(args));

            Harness.Check(
                "C3 system + mic: -c:a aac -b:a 160k -ar 48000 keeps every segment's audio identical",
                Adjacent(args, "-c:a", "aac")
                    && Adjacent(args, "-b:a", "160k")
                    && Adjacent(args, "-ar", "48000"),
                "argv was " + Show(args));

            Harness.Check(
                "C3 system + mic: no -pix_fmt, no -vf and no crop filter (gotchas 1 and 5 still apply)",
                NoToken(args, "-pix_fmt") && NoToken(args, "-vf")
                    && Array.IndexOf(args, "crop") < 0 && NoToken(args, "crop="),
                "argv was " + Show(args));
        }

        /// <summary>
        /// C5: the loopback pipe name is unique per segment.
        /// </summary>
        /// <remarks>
        /// Oracle: the work order's pipe template,
        /// <c>\\.\pipe\snipvid-audio-&lt;session&gt;-&lt;segment&gt;</c>. Two segments of one
        /// session must not share a name: the previous segment's server is still
        /// being torn down when the next one is created, and a shared name would
        /// either connect the new ffmpeg to the old pipe or fail outright. Two
        /// sessions must not share one either, because a leftover pipe from an
        /// earlier capture is not impossible.
        /// </remarks>
        public static void TestC5_PipeNameIsUniquePerSegment()
        {
            const string Session = "2026-11-16_143002-437";
            string first = FfmpegArgs.SystemAudioPipeName(Session, "seg-001");
            string second = FfmpegArgs.SystemAudioPipeName(Session, "seg-002");
            string otherSession = FfmpegArgs.SystemAudioPipeName("2026-11-16_143010-001", "seg-001");

            Harness.Check(
                "C5 two segments of one session get different pipe names",
                first != second,
                "first '" + first + "', second '" + second + "'");

            Harness.Check(
                "C5 the pipe name follows " + @"\\.\pipe\snipvid-audio-<session>-<segment>",
                first == @"\\.\pipe\snipvid-audio-" + Session + "-seg-001",
                "got '" + first + "'");

            Harness.Check(
                "C5 two sessions get different pipe names even for the same segment number",
                first != otherSession,
                "first '" + first + "', other '" + otherSession + "'");

            Harness.Check(
                "C5 the same arguments yield the same name (the name is a function, not a counter)",
                first == FfmpegArgs.SystemAudioPipeName(Session, "seg-001"),
                "got '" + FfmpegArgs.SystemAudioPipeName(Session, "seg-001") + "'");
        }

        /// <summary>True when the option is immediately followed by the value.</summary>
        /// <param name="args">Argument list to scan.</param>
        /// <param name="option">Option name, for example "-map".</param>
        /// <param name="value">Required following element.</param>
        /// <returns>True when that adjacent pair occurs at least once.</returns>
        /// <remarks>
        /// Side effects: none. Adjacency matters for repeated options: -map 0:v
        /// followed later by -map 1:a is two pairs, while a single -map with the
        /// wrong second element would not be.
        /// </remarks>
        private static bool Adjacent(string[] args, string option, string value)
        {
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == option && args[i + 1] == value)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Counts how many elements equal the token exactly.</summary>
        /// <param name="args">Argument list to scan.</param>
        /// <param name="token">Exact element to count, for example "-i".</param>
        /// <returns>The number of occurrences.</returns>
        /// <remarks>Side effects: none. Exact equality: "-i" is not matched inside a path.</remarks>
        private static int CountToken(string[] args, string token)
        {
            int count = 0;
            foreach (string arg in args)
            {
                if (arg == token)
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>Formats an argument list for a failure message.</summary>
        /// <param name="args">Arguments to show; may be null.</param>
        /// <returns>Each element bracketed, so an empty or space-bearing element is visible.</returns>
        /// <remarks>Side effects: none. Never throws, so it is safe in a failure path.</remarks>
        private static string Show(string[] args)
        {
            if (args == null)
            {
                return "<null>";
            }
            return "[" + string.Join("] [", args) + "]";
        }

        /// <summary>True when any element contains the fragment, ordinal.</summary>
        /// <param name="args">Argument list to scan.</param>
        /// <param name="fragment">Exact text to look for, for example "framerate=30".</param>
        /// <returns>True when the fragment appears in at least one element.</returns>
        /// <remarks>
        /// Side effects: none. Ordinal on purpose: these are numbers inside an
        /// ffmpeg filter string, where case has no meaning and the exact spelling
        /// is the thing being asserted.
        /// </remarks>
        private static bool HasFragment(string[] args, string fragment)
        {
            foreach (string arg in args)
            {
                if (arg != null && arg.IndexOf(fragment, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>True when no element contains the token, ignoring case.</summary>
        /// <param name="args">Argument list to scan.</param>
        /// <param name="token">Fragment that must not appear in any element.</param>
        /// <returns>True when the token is absent from every element.</returns>
        /// <remarks>
        /// Side effects: none. Matching is by substring so "-pix_fmt yuv420p" as
        /// one element is caught as well as "-pix_fmt" followed by "yuv420p".
        /// </remarks>
        private static bool NoToken(string[] args, string token)
        {
            foreach (string arg in args)
            {
                if (arg != null && arg.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Records "option value" in the missing list unless that pair is adjacent.</summary>
        /// <param name="args">Argument list to scan.</param>
        /// <param name="option">Option name, for example "-preset".</param>
        /// <param name="value">Required following element, or null to require only the option.</param>
        /// <param name="missing">Accumulator of human-readable gaps.</param>
        /// <remarks>Side effects: appends at most one entry to <paramref name="missing"/>.</remarks>
        private static void Require(string[] args, string option, string value, List<string> missing)
        {
            int index = Array.IndexOf(args, option);
            bool ok = index >= 0
                && (value == null || (index + 1 < args.Length && args[index + 1] == value));
            if (!ok)
            {
                missing.Add(value == null ? option : option + " " + value);
            }
        }

        /// <summary>Records a value-less flag in the missing list unless it is its own element.</summary>
        /// <param name="args">Argument list to scan.</param>
        /// <param name="flag">Flag expected as an exact element, for example "-y".</param>
        /// <param name="missing">Accumulator of human-readable gaps.</param>
        /// <remarks>Side effects: appends at most one entry to <paramref name="missing"/>.</remarks>
        private static void RequireFlag(string[] args, string flag, List<string> missing)
        {
            if (Array.IndexOf(args, flag) < 0)
            {
                missing.Add(flag);
            }
        }

        /// <summary>True when the arguments survive a render and re-parse unchanged.</summary>
        /// <param name="args">Expected argument list.</param>
        /// <returns>True when parsing ToCommandLine(args) reproduces args exactly.</returns>
        /// <remarks>Side effects: none.</remarks>
        private static bool RoundTrips(string[] args)
        {
            List<string> reparsed = SplitCommandLine(FfmpegArgs.ToCommandLine(args));
            if (reparsed.Count != args.Length)
            {
                return false;
            }
            for (int i = 0; i < args.Length; i++)
            {
                if (reparsed[i] != args[i])
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Splits a command line the way CommandLineToArgvW does, from the
        /// documented rules.
        /// </summary>
        /// <param name="line">Command line produced by ToCommandLine.</param>
        /// <returns>The arguments a child process would receive.</returns>
        /// <remarks>
        /// The test's own reference implementation of the Windows parsing rules:
        /// space or tab separates unless quoted; 2n backslashes before a quote
        /// become n backslashes and the quote delimits; 2n+1 become n backslashes
        /// and a literal quote. Written from the documented rule so it cannot
        /// inherit a bug from the code under test. Side effects: none; throws
        /// nothing for any string.
        /// </remarks>
        private static List<string> SplitCommandLine(string line)
        {
            List<string> result = new List<string>();
            if (line == null)
            {
                return result;
            }

            int i = 0;
            while (i < line.Length)
            {
                while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
                {
                    i++;
                }
                if (i >= line.Length)
                {
                    break;
                }

                StringBuilder current = new StringBuilder();
                bool inQuotes = false;
                bool hasContent = false;
                while (i < line.Length)
                {
                    char c = line[i];
                    if (!inQuotes && (c == ' ' || c == '\t'))
                    {
                        break;
                    }

                    if (c == '\\')
                    {
                        int backslashes = 0;
                        while (i < line.Length && line[i] == '\\')
                        {
                            backslashes++;
                            i++;
                        }
                        bool escapesQuote = i < line.Length && line[i] == '"';
                        current.Append('\\', escapesQuote ? backslashes / 2 : backslashes);
                        if (escapesQuote && backslashes % 2 == 1)
                        {
                            current.Append('"');
                            i++;
                        }
                        hasContent = true;
                        continue;
                    }

                    if (c == '"')
                    {
                        inQuotes = !inQuotes;
                        hasContent = true;
                        i++;
                        continue;
                    }

                    current.Append(c);
                    hasContent = true;
                    i++;
                }

                if (hasContent)
                {
                    result.Add(current.ToString());
                }
            }

            return result;
        }
    }
}
