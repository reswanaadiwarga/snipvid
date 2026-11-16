// FfmpegArgs - the one place the ffmpeg command line is written (DESIGN.md section 5).
//
// WHY this file exists: the validated invocation is a fixed list of flags whose
// absence is silent rather than fatal. Dropping -fps_mode cfr yields a VFR file
// (gotcha 2, the defect this project exists to remove); adding -pix_fmt yuv420p
// makes ddagrab -> h264_nvenc die with -40 before the first frame (gotcha 1);
// adding a crop filter forces a GPU to CPU download (gotcha 5). None of those
// raise an error the user can act on, so the argument list lives in exactly one
// function that a unit test asserts on, and no other file may concatenate an
// ffmpeg command line.
//
// The public shape is an array, not a string, because the recorder needs the
// arguments and the person reading a failure needs the quoted line: Build gives
// the former, ToCommandLine the latter, and quoting stays in one auditable place
// (.NET Framework 4.8 has no ProcessStartInfo.ArgumentList).
//
// Crash safety is a pair of flags, not a mode: -g 60 makes a fragment boundary
// every second and -flush_packets 1 pushes each fragment to disk. -movflags
// +frag_keyframe+empty_moov alone leaves everything in the muxer's buffer until
// the trailer, so a killed ffmpeg wrote a 36-byte file that would not open.
// Measured 2026-11-15: -g 60 alone still 36 bytes, -flush_packets 1 alone 816
// bytes with no stream, both playable. Cost +0.7 % (DESIGN.md section 5).
//
// The progress channel (DESIGN.md section 5a) is two options, not one:
// -progress pipe:1 moves the report to stdout, and -stats_period 0.05 tightens
// the cadence. Without the second, ffmpeg reports every 0.5 s by default, so the
// first frame= line can arrive half a second after capture began and first-frame
// detection (the Starting state) waits blind - measured at 800x450: 974 ms at the
// default cadence versus 810 ms at 0.05.
//
// Microphone audio (DESIGN.md section 5d) is one conditional block: a second
// input (-f dshow -i audio=NAME), the AAC encode and an explicit -map 0:v -map
// 1:a. The video only path must stay byte-identical when no microphone was
// resolved - that is C1 of the audio work order, asserted as a whole array,
// because the capture that already works is the one thing this feature must not
// disturb. The name is passed through unvalidated: dshow is the only authority on
// which devices exist, and a bad name fails at spawn with ffmpeg's own message.
//
// System audio (DESIGN.md section 5d) is a second conditional block and always
// exactly one raw PCM input: -f s16le -ar 48000 -ac 2 -i \\.\pipe\... . The pipe
// is created before ffmpeg is spawned, so the child's open never races a missing
// server; the explicit -f s16le is what stops ffmpeg probing a pipe that can
// neither seek nor be reopened. Three cases, no fourth:
//   no audio          -> the validated video-only command line, byte for byte
//   one source        -> -map 0:v -map 1:a, whether that source is input 1
//                        (a loopback pipe) or the only other input (dshow)
//   both sources      -> -filter_complex amix and -map "[a]", one mixed track
// An absent pipe is null or empty, exactly like an absent microphone, so a caller
// that resolved nothing cannot produce an input ffmpeg would wait on forever.
//
// No WPF, no P/Invoke, no I/O, no clock: test.ps1 compiles this file without any
// Windows reference (DESIGN.md section 11).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SnipVid.Logic
{
    /// <summary>
    /// One capture request: what to record, from which monitor, into which file.
    /// </summary>
    /// <remarks>
    /// Immutable carrier with no validation and no side effects. The values are
    /// produced by the overlay (region), monitor selection (index), config (fps,
    /// cq) and naming (path); this type exists so <see cref="FfmpegArgs.Build"/>
    /// has a single stable parameter instead of five positional ones.
    /// </remarks>
    public sealed class FfmpegRequest
    {
        /// <summary>
        /// Initialises a request.
        /// </summary>
        /// <param name="outputPath">Destination file; the ffmpeg child's last argument.</param>
        /// <param name="monitorIndex">Monitor index in the DDA enumeration, for output_idx.</param>
        /// <param name="region">Monitor-relative physical pixels from Geometry.ToPhysical.</param>
        /// <param name="fps">Requested frame rate; 60 or 30 is honoured (decision D1 as amended).</param>
        /// <param name="cq">NVENC constant-quality value, 1-51, 19 by default.</param>
        /// <remarks>Side effects: none. Values are stored verbatim, including nonsense.</remarks>
        public FfmpegRequest(string outputPath, int monitorIndex, PxRect region, int fps, int cq)
            : this(outputPath, monitorIndex, region, fps, cq, null)
        {
        }

        /// <summary>
        /// Initialises a request that may also record microphone audio.
        /// </summary>
        /// <param name="outputPath">Destination file; the ffmpeg child's last argument.</param>
        /// <param name="monitorIndex">Monitor index in the DDA enumeration, for output_idx.</param>
        /// <param name="region">Monitor-relative physical pixels from Geometry.ToPhysical.</param>
        /// <param name="fps">Requested frame rate; 60 or 30 is honoured.</param>
        /// <param name="cq">NVENC constant-quality value, 1-51.</param>
        /// <param name="microphoneDevice">
        /// Microphone name for ffmpeg's dshow input, already resolved by the caller;
        /// null or empty means video only. Not validated here: dshow decides whether
        /// the name exists, and this type has no way to ask.
        /// </param>
        /// <remarks>
        /// Side effects: none. The overload without a device is the video-only
        /// capture that every existing test and caller describes, so adding audio
        /// could not change that path's meaning.
        /// </remarks>
        public FfmpegRequest(
            string outputPath, int monitorIndex, PxRect region, int fps, int cq, string microphoneDevice)
            : this(outputPath, monitorIndex, region, fps, cq, microphoneDevice, null)
        {
        }

        /// <summary>
        /// Initialises a request that may record microphone audio, system audio, or
        /// both.
        /// </summary>
        /// <param name="outputPath">Destination file; the ffmpeg child's last argument.</param>
        /// <param name="monitorIndex">Monitor index in the DDA enumeration, for output_idx.</param>
        /// <param name="region">Monitor-relative physical pixels from Geometry.ToPhysical.</param>
        /// <param name="fps">Requested frame rate; 60 or 30 is honoured.</param>
        /// <param name="cq">NVENC constant-quality value, 1-51.</param>
        /// <param name="microphoneDevice">
        /// Microphone name for ffmpeg's dshow input; null or empty means no
        /// microphone. Resolved by the caller and stored verbatim.
        /// </param>
        /// <param name="systemAudioPipe">
        /// Full path of the named pipe the loopback writer feeds, for example
        /// <c>\\.\pipe\snipvid-audio-&lt;session&gt;-&lt;segment&gt;</c>; null or empty means
        /// no system audio. Already created by the caller; this type never touches
        /// the pipe, it only writes the path into the argv.
        /// </param>
        /// <remarks>
        /// Side effects: none. The two audio sources are independent choices: the
        /// argv builder reads them as a pair, because the mapping differs when both
        /// are present (mixed into one stream) and when only one is.
        /// </remarks>
        public FfmpegRequest(
            string outputPath, int monitorIndex, PxRect region, int fps, int cq,
            string microphoneDevice, string systemAudioPipe)
        {
            OutputPath = outputPath;
            MonitorIndex = monitorIndex;
            Region = region;
            Fps = fps;
            Cq = cq;
            MicrophoneDevice = microphoneDevice;
            SystemAudioPipe = systemAudioPipe;
        }

        /// <summary>
        /// Destination file, absolute. Not validated here: the caller resolves and
        /// creates the folder before spawning (WO-12).
        /// </summary>
        /// <remarks>May be null; Build emits it verbatim in that case.</remarks>
        public string OutputPath { get; private set; }

        /// <summary>Monitor index in the Desktop Duplication enumeration, 0-based.</summary>
        /// <remarks>
        /// Assumed, not documented, to match EnumDisplayMonitors order - see
        /// DESIGN.md section 4 and WO-08.
        /// </remarks>
        public int MonitorIndex { get; private set; }

        /// <summary>
        /// Capture region in monitor-relative physical pixels, even sizes.
        /// </summary>
        /// <remarks>
        /// Not re-validated here: Geometry.IsUsable already rejected anything
        /// unusable, and duplicating the rule would let the two disagree.
        /// </remarks>
        public PxRect Region { get; private set; }

        /// <summary>Requested frame rate: 60 (default) or 30; anything else is recorded at 60.</summary>
        /// <remarks>Carried so the config layer's note and the argv describe the same request.</remarks>
        public int Fps { get; private set; }

        /// <summary>NVENC constant-quality value, 1-51; lower is better quality.</summary>
        /// <remarks>
        /// Written verbatim: range checking belongs to the config parser (WO-04),
        /// which has the startup note channel this pure function does not.
        /// </remarks>
        public int Cq { get; private set; }

        /// <summary>
        /// Microphone device name for ffmpeg's dshow input; null or empty means no audio.
        /// </summary>
        /// <remarks>
        /// Stored verbatim, including a name this machine does not have: dshow is
        /// the only authority on which devices exist, and a bad name is a spawn
        /// failure with ffmpeg's own message rather than a parse error. The recorder
        /// decides when to resolve it; this type only carries the answer.
        /// </remarks>
        public string MicrophoneDevice { get; private set; }

        /// <summary>
        /// Full path of the loopback pipe for ffmpeg's raw PCM input; null or empty
        /// means no system audio.
        /// </summary>
        /// <remarks>
        /// Stored verbatim. The pipe itself is created by the recorder before the
        /// spawn, because ffmpeg's open must find a server and its first read must
        /// find a writer; this type only names it. A path naming no pipe is a spawn
        /// failure with ffmpeg's own message, which is why the caller creates it
        /// first - the same reason <see cref="MicrophoneDevice"/> is resolved before
        /// the spawn instead of validated here.
        /// </remarks>
        public string SystemAudioPipe { get; private set; }
    }

    /// <summary>
    /// Builds the validated ffmpeg argument list (DESIGN.md sections 5 and 5a).
    /// </summary>
    /// <remarks>
    /// Pure: no I/O, no clock, no process, no validation that would throw. The
    /// caller owns spawning, stopping and failure handling (WO-09).
    /// </remarks>
    public static class FfmpegArgs
    {
        /// <summary>
        /// The default frame rate this build records, and the value used whenever a
        /// request names anything other than the two supported rates.
        /// </summary>
        /// <remarks>
        /// Decision D1 as amended: 60 stays the default because screen content
        /// renders at 60 or less, so a 120 fps container of 60 fps content is twice
        /// the bytes for an indistinguishable result. 30 is an explicit choice for a
        /// 30 fps source, where capturing at 60 stores every frame twice. 120/144
        /// are still refused here rather than forwarded. The startup note that says
        /// so is the config parser's job, because this type has no place to report it.
        /// </remarks>
        public const int RecordingFps = 60;

        /// <summary>The one non-default rate: for a 30 fps source, where 60 would duplicate every frame.</summary>
        /// <remarks>
        /// Named for what it selects, not "low fps": the D2 rule still says file size
        /// is solved with -cq, never by lowering the frame rate.
        /// </remarks>
        public const int HalfFps = 30;

        /// <summary>Answers whether a request is one of the two rates this build records.</summary>
        /// <param name="fps">Requested frame rate; any integer, including nonsense.</param>
        /// <returns>True only for 60 and 30.</returns>
        /// <remarks>
        /// Side effects: none. Shared with <see cref="Config.Parse"/> so the parser
        /// and the argv builder cannot disagree about which rates exist: a config
        /// that accepted a rate this builder silently replaced would make the file
        /// lie about the recording.
        /// </remarks>
        public static bool IsSupportedFps(int fps)
        {
            return fps == RecordingFps || fps == HalfFps;
        }

        /// <summary>
        /// Prefix of every loopback pipe this app creates (DESIGN.md section 5d).
        /// </summary>
        /// <remarks>
        /// The pipe lives in the kernel's pipe namespace, not on disk: ffmpeg opens
        /// it through the normal file protocol, so the argument must be the full
        /// <c>\\.\pipe\</c> path. Named per session and segment so a previous
        /// segment's server cannot be picked up by the next child.
        /// </remarks>
        public const string SystemAudioPipePrefix = @"\\.\pipe\snipvid-audio-";

        /// <summary>
        /// Builds the loopback pipe path for one segment.
        /// </summary>
        /// <param name="sessionId">
        /// Session token, unique per capture. The recorder passes the session's
        /// scratch directory name, which already carries milliseconds and a suffix
        /// when needed.
        /// </param>
        /// <param name="segmentName">Segment token, for example <c>seg-001</c>.</param>
        /// <returns>
        /// For example <c>\\.\pipe\snipvid-audio-2026-11-16_143002-437-seg-001</c>.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="sessionId"/> or <paramref name="segmentName"/> is null or
        /// empty: an unnamed pipe would be shared by every capture, which is the
        /// collision this function exists to prevent.
        /// </exception>
        /// <remarks>
        /// Side effects: none. A pure function of its arguments, so the caller can
        /// log the name before the pipe exists and the tests can assert two segments
        /// differ without a filesystem. The tokens are deliberately passed through
        /// unchanged rather than sanitised: they come from this app's own naming
        /// (<see cref="Session.SegmentFileName"/> and the scratch directory), and a
        /// silent rewrite here would hide a caller that got them wrong.
        /// </remarks>
        public static string SystemAudioPipeName(string sessionId, string segmentName)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                throw new ArgumentNullException("sessionId");
            }
            if (string.IsNullOrEmpty(segmentName))
            {
                throw new ArgumentNullException("segmentName");
            }

            return SystemAudioPipePrefix + sessionId + "-" + segmentName;
        }

        /// <summary>
        /// Period in seconds between ffmpeg progress reports on stdout (DESIGN.md section 5a).
        /// </summary>
        /// <remarks>
        /// -progress alone is not enough for first-frame detection: ffmpeg's default
        /// stats cadence is 0.5 s, so the first "frame=" line can arrive half a second
        /// after the first frame was captured, and the Starting state waits blind.
        /// Measured at 800x450: default reported the first non-zero frame at 974 ms,
        /// 0.05 reports it at 810 ms. The value is a string because ffmpeg wants the
        /// decimal spelled exactly, and a culture-sensitive conversion could not.
        /// </remarks>
        private const string StatsPeriod = "0.05";

        /// <summary>
        /// Builds the complete ffmpeg argument list for one capture.
        /// </summary>
        /// <param name="request">Capture request; its output path becomes the last argument.</param>
        /// <returns>
        /// Every argument as its own element, in the order of DESIGN.md section 5,
        /// with -progress pipe:1 and -stats_period 0.05 from section 5a, plus the
        /// audio block of section 5d for whichever of the microphone and the
        /// loopback pipe the request names. The output path is last and appears
        /// once; with no audio source at all the elements are exactly the video-only
        /// command line.
        /// </returns>
        /// <remarks>
        /// Side effects: none. Throws ArgumentNullException for a null request,
        /// because there is no meaningful argument list for "no request"; every
        /// other value is passed through unvalidated, including a microphone name
        /// that does not exist on this machine and a pipe path that was never
        /// created.
        /// </remarks>
        public static string[] Build(FfmpegRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }

            // Parameter order is asserted by C1 as a whole string: it is the form
            // the validated command line in DESIGN.md section 5 uses, and reordering
            // it reorders a filter graph nobody has smoke-tested.
            //
            // The framerate is narrowed, not forwarded: 30 and 60 are the only
            // rates this build records, and every other request keeps the
            // validated 60 (D1 as amended). For a 30 fps source this is the whole
            // point - at 60 the same picture would be stored twice.
            int fps = IsSupportedFps(request.Fps) ? request.Fps : RecordingFps;
            string input = string.Concat(
                "ddagrab=output_idx=", Invariant(request.MonitorIndex),
                ":framerate=", Invariant(fps),
                ":offset_x=", Invariant(request.Region.X),
                ":offset_y=", Invariant(request.Region.Y),
                ":video_size=", Invariant(request.Region.W), "x", Invariant(request.Region.H),
                ":draw_mouse=1");

            // Audio is a block, not a mode: with no device every element below is
            // skipped and the list is the validated video-only command line, which
            // C1 asserts as a whole array. Empty is treated like null because an
            // unresolved name from a caller is "no audio", never "audio=" with
            // nothing after it (which dshow would read as a malformed device). The
            // pipe path is treated the same way: an absent path must not become an
            // input ffmpeg waits on.
            string microphone = request.MicrophoneDevice;
            string pipe = request.SystemAudioPipe;
            bool hasMic = !string.IsNullOrEmpty(microphone);
            bool hasSystem = !string.IsNullOrEmpty(pipe);

            List<string> args = new List<string>
            {
                "-hide_banner",
                "-loglevel", "error",
                "-y",

                // stdout carries the progress lines and nothing else; stdin stays
                // free for the q stop and stderr stays errors-only (section 5a).
                // -stats_period tightens the report cadence from the 0.5 s default
                // so the first frame= line is not half a second late (section 5a).
                "-progress", "pipe:1",
                "-stats_period", StatsPeriod,

                // lavfi is only the input *format* here: ddagrab takes its frames
                // straight from the GPU, so no pixel format is named anywhere.
                "-f", "lavfi",
                "-i", input,
            };

            if (hasSystem)
            {
                // Input 1 is the raw loopback stream. 48 kHz, 16-bit, stereo are
                // fixed because that is what the capture writer produces and what
                // the endpoint mixes; -f s16le is mandatory because a pipe cannot be
                // probed or seeked and ffmpeg must not try (DESIGN.md section 5d).
                args.Add("-f");
                args.Add("s16le");
                args.Add("-ar");
                args.Add("48000");
                args.Add("-ac");
                args.Add("2");
                args.Add("-i");
                args.Add(pipe);
            }

            if (hasMic)
            {
                // The microphone comes after the pipe, so with both sources it is
                // input 2 while the pipe is input 1. dshow names the device by the
                // string Windows gives it, spaces and parentheses included
                // (DESIGN.md section 5d).
                args.Add("-f");
                args.Add("dshow");
                args.Add("-i");
                args.Add("audio=" + microphone);
            }

            args.Add("-c:v");
            args.Add("h264_nvenc");
            args.Add("-preset");
            args.Add("p5");
            args.Add("-tune");
            args.Add("hq");
            args.Add("-cq");
            args.Add(Invariant(request.Cq));

            // A keyframe - and therefore a fragment boundary - every 60 frames.
            // Without it the fragmentation flags below have nothing to break the
            // file at, and a killed ffmpeg leaves a 36-byte file that will not
            // open (DESIGN.md section 5, measured 2026-11-15). Neither this flag
            // nor -flush_packets works alone. Deliberately still 60 at the 30 fps
            // option: the measured crash-safety path used this value, and 2 s
            // between fragments remains bounded.
            args.Add("-g");
            args.Add("60");

            // Mandatory, and the reason this project exists: without it the
            // muxer writes the timestamps it is given and the result is a
            // variable-frame-rate file (AGENTS.md gotcha 2).
            args.Add("-fps_mode");
            args.Add("cfr");

            if (hasMic || hasSystem)
            {
                // AAC 160 kbps at 48 kHz: speech needs nothing more, and the rate
                // is fixed so every segment of a capture has identical codec
                // parameters - a -c copy join of two AAC segments with different
                // rates would be rejected or desynchronised (DESIGN.md section 5d).
                args.Add("-c:a");
                args.Add("aac");
                args.Add("-b:a");
                args.Add("160k");
                args.Add("-ar");
                args.Add("48000");
            }

            if (hasMic && hasSystem)
            {
                // Two live sources, one track: the pipe is input 1 and dshow is
                // input 2, so the two pads are named explicitly. duration=longest
                // keeps the mix alive for the whole segment even if one source stops
                // early. -map "[a]" is the mixed output, so the file still has
                // exactly one audio stream - which is what the -c copy join needs.
                //
                // WHY the pads are labelled: a bare "amix=inputs=2" in
                // -filter_complex was measured to fail at spawn with
                // "Error opening output files: Invalid argument", because complex
                // filtergraphs do not auto-connect unlabelled inputs (only -vf/-af
                // do). The dispatch's shorthand was not runnable; this is.
                args.Add("-filter_complex");
                args.Add("[1:a][2:a]amix=inputs=2:duration=longest[a]");
                args.Add("-map");
                args.Add("0:v");
                args.Add("-map");
                args.Add("[a]");
            }
            else if (hasMic || hasSystem)
            {
                // One audio source is input 1 in both cases: the pipe is added
                // first, and a microphone without system audio is the only other
                // input. Explicit because two inputs make automatic stream
                // selection ambiguous: this says video comes from input 0 and audio
                // from input 1, which is also the stream order the file then has.
                args.Add("-map");
                args.Add("0:v");
                args.Add("-map");
                args.Add("1:a");
            }

            // Fragmented so a killed process still leaves a playable segment
            // (DESIGN.md section 5b, crash recovery in section 5c).
            args.Add("-movflags");
            args.Add("+frag_keyframe+empty_moov");

            // Push every fragment to disk as it is written. Fragmentation on
            // its own keeps the data in the muxer's buffer until the trailer,
            // so a killed process wrote nothing: 36 bytes with -g 60 alone,
            // 816 bytes with this flag alone, playable only with both
            // (DESIGN.md section 5, measured 2026-11-15).
            args.Add("-flush_packets");
            args.Add("1");

            // Last and only once: ffmpeg reads the final unnamed argument as
            // the destination.
            args.Add(request.OutputPath);
            return args.ToArray();
        }

        /// <summary>
        /// Renders an argument list as one command line, quoting only what needs it.
        /// </summary>
        /// <param name="args">Arguments from <see cref="Build"/> or any other source.</param>
        /// <returns>
        /// The arguments joined by single spaces, with the Windows
        /// CommandLineToArgvW escaping rules applied; empty string for no arguments.
        /// </returns>
        /// <remarks>
        /// Side effects: none. Never throws: null is treated as no arguments and a
        /// null element as an empty argument.
        /// </remarks>
        public static string ToCommandLine(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return string.Empty;
            }

            StringBuilder line = new StringBuilder();
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0)
                {
                    line.Append(' ');
                }
                AppendArgument(args[i], line);
            }
            return line.ToString();
        }

        /// <summary>
        /// Appends one argument to a command line, quoting it only when the child's
        /// parser would otherwise split it.
        /// </summary>
        /// <param name="arg">Argument to append; null is written as an empty argument.</param>
        /// <param name="line">Command line being built.</param>
        /// <remarks>
        /// Side effects: appends to <paramref name="line"/>. The escaping is
        /// CommandLineToArgvW's, not a shell's: 2n backslashes before a quote become
        /// n, 2n+1 become n plus a literal quote, and the backslashes at the very end
        /// sit in front of the closing quote and must be doubled. Missing that last
        /// rule turns "C:\a b\" into "C:\a b" for the child - the classic quoting bug
        /// and the reason the output folder path is tested (C7).
        /// </remarks>
        private static void AppendArgument(string arg, StringBuilder line)
        {
            if (string.IsNullOrEmpty(arg))
            {
                // An empty argument still occupies a position, so it cannot be
                // written as nothing at all.
                line.Append("\"\"");
                return;
            }

            if (!NeedsQuoting(arg))
            {
                // Backslashes alone are safe: they only have meaning in front of a
                // quote, and this argument has none.
                line.Append(arg);
                return;
            }

            line.Append('"');
            int backslashes = 0;
            for (int i = 0; i < arg.Length; i++)
            {
                char c = arg[i];
                if (c == '\\')
                {
                    line.Append(c);
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    // Inside quotes a bare quote is not representable, so escape it;
                    // the backslashes already written in front of it have to be
                    // doubled first or they would escape the escaping quote.
                    line.Append('\\', backslashes + 1);
                    line.Append('"');
                    backslashes = 0;
                    continue;
                }
                line.Append(c);
                backslashes = 0;
            }

            // Trailing backslashes: double them, or the first one escapes the
            // closing quote and the argument loses its tail.
            line.Append('\\', backslashes);
            line.Append('"');
        }

        /// <summary>
        /// Decides whether an argument must be quoted to survive the child's parser.
        /// </summary>
        /// <param name="arg">Non-empty argument to test.</param>
        /// <returns>True when the argument contains a separator that would split it.</returns>
        /// <remarks>
        /// Side effects: none. Space and tab are CommandLineToArgvW's documented
        /// separators, and a quote forces quoting by definition. Newline and
        /// vertical tab are quoted as well even though they do not separate: quoting
        /// is always reversible (the parser strips the pair), and a raw control
        /// character in an argument is worse than two quotes in a log line.
        /// </remarks>
        private static bool NeedsQuoting(string arg)
        {
            for (int i = 0; i < arg.Length; i++)
            {
                char c = arg[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\v' || c == '"')
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Formats an integer for the filter graph and the output arguments.
        /// </summary>
        /// <param name="value">Integer to format.</param>
        /// <returns>The invariant-culture decimal digits of the value.</returns>
        /// <remarks>
        /// Side effects: none. Invariant on purpose: a culture with a non-ASCII
        /// minus sign or digit shape would produce a filter string ffmpeg cannot
        /// parse, and the failure would look like a broken capture, not a locale bug.
        /// </remarks>
        private static string Invariant(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
