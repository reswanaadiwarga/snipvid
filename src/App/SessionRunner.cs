// SessionRunner - the on-disk half of pause-and-join (DESIGN.md section 5b, WO-10).
//
// WHY the scratch directory exists: a pause is a file boundary, so a session owns
// several segments before it owns one output file. Segments must never appear in
// the user's Videos folder - the promise is still exactly one file per capture -
// so they live in %LOCALAPPDATA%\SnipVid\scratch\<session>\seg-NNN.mp4 and are
// joined there before anything is moved into place.
//
// Three rules this type exists to make true, each because the alternative loses
// the user's recording:
//
//   1. The output file is created only by a successful join. The join writes
//      <scratch>\joined.mp4 first and the finished file is moved into the output
//      folder afterwards, so a failed join cannot leave a half-file next to the
//      user's other recordings (DESIGN.md section 5b, "the final file does not
//      exist until the join").
//   2. On failure nothing is deleted. Every segment stays, and the scratch path is
//      reported so the user can join them by hand; a failed join that also deleted
//      its input would be the worst possible outcome, so deletion only happens on
//      the success path (DESIGN.md section 9, "never delete evidence").
//   3. Every finalise runs one concat, even for a single segment. WHY there is no
//      File.Move fast path (DESIGN.md section 5b): the recorder writes fragmented
//      output (+frag_keyframe+empty_moov) so a killed app leaves a playable file,
//      and +empty_moov writes a movie header with no duration - a moved segment
//      shows no Length in Explorer's tooltip, its properties dialog, or any
//      duration-based sort. The concat rewrites it as an ordinary MP4 and the
//      duration appears. One code path beats a fast path that quietly produces a
//      lesser file; measured cost is 148 ms for a 3 s clip.
//
// The decisions - names, list text, which plan applies - are in Logic\Session.cs,
// which is compiled by test.ps1. This file only performs them: directories, a text
// file, one child process and moves.
//
// Threading: every member is called from the UI thread. The join child runs while
// the caller blocks in Join, which is why the caller hides the pill before calling
// it. Nothing here is reentrant.
//
// ASCII-only source: csc falls back to the ANSI codepage for a file that is not
// valid UTF-8.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using SnipVid.Logic;

namespace SnipVid
{
    /// <summary>
    /// The result of turning a session's segments into the one output file.
    /// </summary>
    /// <remarks>
    /// A carrier for facts, not a policy: the caller composes the user-facing
    /// message, because the toast channel is Program's. <see cref="ScratchDir"/> is
    /// always set, including on success, so a failure message can name where the
    /// surviving segments are.
    /// </remarks>
    internal sealed class JoinOutcome
    {
        /// <summary>Stores one join result.</summary>
        /// <param name="kind">Concat or None, from the plan that was executed.</param>
        /// <param name="succeeded">True only when the output file exists and is complete.</param>
        /// <param name="outputPath">The file that exists on success; null otherwise.</param>
        /// <param name="scratchDir">The session's scratch directory, whether or not it still exists.</param>
        /// <param name="segmentCount">Usable segments that were joined.</param>
        /// <param name="frameCount">Frames those segments reported, summed.</param>
        /// <param name="elapsedMs">Wall-clock milliseconds spent producing the output.</param>
        /// <param name="detail">ffmpeg's last stderr line on failure; null otherwise.</param>
        /// <remarks>Side effects: none. Every property is read-only.</remarks>
        internal JoinOutcome(
            JoinKind kind, bool succeeded, string outputPath, string scratchDir,
            int segmentCount, int frameCount, long elapsedMs, string detail)
        {
            Kind = kind;
            Succeeded = succeeded;
            OutputPath = outputPath;
            ScratchDir = scratchDir;
            SegmentCount = segmentCount;
            FrameCount = frameCount;
            ElapsedMs = elapsedMs;
            Detail = detail;
        }

        /// <summary>Which plan was executed.</summary>
        internal JoinKind Kind { get; private set; }

        /// <summary>True only when the output file exists at <see cref="OutputPath"/>.</summary>
        internal bool Succeeded { get; private set; }

        /// <summary>The output file on success; null when there is none.</summary>
        internal string OutputPath { get; private set; }

        /// <summary>The session's scratch directory; named in every failure message.</summary>
        internal string ScratchDir { get; private set; }

        /// <summary>How many usable segments the plan contained.</summary>
        internal int SegmentCount { get; private set; }

        /// <summary>Sum of those segments' reported frame counts.</summary>
        internal int FrameCount { get; private set; }

        /// <summary>Milliseconds from the start of the join to the output being in place.</summary>
        internal long ElapsedMs { get; private set; }

        /// <summary>ffmpeg's last error line when the join failed; null otherwise.</summary>
        internal string Detail { get; private set; }
    }

    /// <summary>
    /// Owns one capture session's scratch directory, its ordered segment list and
    /// the join that turns them into one file.
    /// </summary>
    /// <remarks>
    /// Created when a capture starts and discarded when the capture is finished,
    /// however it finishes. Not reusable: a new capture is a new instance, which is
    /// what makes "2 pauses and 20 pauses are the same code path" true.
    /// </remarks>
    internal sealed class SessionRunner
    {
        /// <summary>Directory under %LOCALAPPDATA% that holds every session's scratch dir.</summary>
        private const string ScratchFolderName = "scratch";

        /// <summary>
        /// How long the join child may run before it is treated as failed.
        /// </summary>
        /// <remarks>
        /// Join throughput was measured at ~320 MB/s (RESEARCH.md section 4.5), so
        /// a fixed short bound would kill a legitimate multi-gigabyte join: 5
        /// minutes is 96 GB at that rate. The bound exists so a stuck child cannot
        /// hang the finalise forever, not to police duration.
        /// </remarks>
        private const int JoinTimeoutMs = 300000;

        /// <summary>How long a killed join child is given to be reaped.</summary>
        private const int KillWaitMs = 2000;

        /// <summary>Name of the joined file inside the scratch directory, before the move.</summary>
        private const string JoinedFileName = "joined.mp4";

        /// <summary>Bounded suffix attempts for a session directory that already exists.</summary>
        private const int MaxSessionAttempts = 100;

        /// <summary>
        /// Session directory: <c>scratch\yyyy-MM-dd_HHmmss-fff</c>, with a numeric
        /// suffix if that name is already taken.
        /// </summary>
        private readonly string _scratchDir;

        /// <summary>Every stopped segment, in recording order, including empty ones.</summary>
        private readonly List<SegmentInfo> _segments = new List<SegmentInfo>();

        /// <summary>Number the next segment will use; 1-based.</summary>
        private int _nextIndex = 1;

        /// <summary>
        /// Creates the session's scratch directory.
        /// </summary>
        /// <param name="scratchRoot">
        /// Parent directory for session folders, normally
        /// <see cref="DefaultScratchRoot"/>. Created if absent.
        /// </param>
        /// <param name="start">
        /// Capture start time, used for the directory name so a leftover session can
        /// be identified later (DESIGN.md section 5c, crash recovery).
        /// </param>
        /// <exception cref="ArgumentException"><paramref name="scratchRoot"/> is null or empty.</exception>
        /// <exception cref="IOException">The directory could not be created.</exception>
        /// <exception cref="UnauthorizedAccessException">The directory could not be created.</exception>
        /// <remarks>
        /// Side effects: creates <c>scratchRoot</c> and one session directory inside
        /// it. The timestamp includes milliseconds so two captures started in the
        /// same second cannot share a directory and overwrite each other's segments;
        /// a suffix loop covers the remaining (clock-resolution) case.
        /// </remarks>
        internal SessionRunner(string scratchRoot, DateTime start)
        {
            if (string.IsNullOrEmpty(scratchRoot))
            {
                throw new ArgumentException("a scratch root is required", "scratchRoot");
            }

            string stem = start.ToString("yyyy-MM-dd_HHmmss-fff", CultureInfo.InvariantCulture);
            string directory = null;
            for (int attempt = 1; attempt <= MaxSessionAttempts; attempt++)
            {
                string name = attempt == 1
                    ? stem
                    : stem + "_" + attempt.ToString(CultureInfo.InvariantCulture);
                string candidate = Path.Combine(scratchRoot, name);
                if (!Directory.Exists(candidate))
                {
                    directory = candidate;
                    break;
                }
            }

            if (directory == null)
            {
                throw new IOException(
                    "could not find a free session directory under '" + scratchRoot
                    + "' after " + MaxSessionAttempts.ToString(CultureInfo.InvariantCulture)
                    + " attempts; move or delete the existing scratch sessions.");
            }

            _scratchDir = directory;
            Directory.CreateDirectory(_scratchDir);
        }

        /// <summary>The session's scratch directory; never in the user's output folder.</summary>
        internal string ScratchDir
        {
            get { return _scratchDir; }
        }

        /// <summary>Stopped segments, in recording order, including ones with no frames.</summary>
        /// <remarks>
        /// A read-only view: the filter and the plan must see the same list, so only
        /// <see cref="Add"/> changes it.
        /// </remarks>
        internal IList<SegmentInfo> Segments
        {
            get { return _segments.AsReadOnly(); }
        }

        /// <summary>
        /// Returns the path the next segment must be written to and advances the
        /// index.
        /// </summary>
        /// <returns>For example <c>C:\...\scratch\2026-11-15_143002-437\seg-002.mp4</c>.</returns>
        /// <remarks>
        /// Side effects: increments the segment counter. The path is not created:
        /// ffmpeg creates the file when the segment starts, and a spawn that fails
        /// leaves it absent (or empty), which the frame filter then ignores.
        /// </remarks>
        internal string NextSegmentPath()
        {
            string path = Logic.Session.SegmentPath(_scratchDir, _nextIndex);
            _nextIndex++;
            return path;
        }

        /// <summary>
        /// Appends a stopped segment to the ordered list.
        /// </summary>
        /// <param name="path">The path ffmpeg was given for that segment.</param>
        /// <param name="frames">The frame count it reported; 0 means it contributed nothing.</param>
        /// <remarks>
        /// Side effects: appends one entry. Empty segments are recorded and filtered
        /// later, not dropped on arrival: the count of paused segments and the count
        /// of joined segments are different facts, and the log states both.
        /// </remarks>
        internal void Add(string path, int frames)
        {
            _segments.Add(new SegmentInfo(path, frames));
        }

        /// <summary>
        /// Produces the one output file from the session's segments, or reports why
        /// it could not.
        /// </summary>
        /// <param name="ffmpegPath">Resolved ffmpeg executable, used by every join, including a one-segment one.</param>
        /// <param name="outputPath">Destination file in the configured output folder.</param>
        /// <returns>
        /// What was done, including the scratch path and the frame total. Never
        /// throws: a join that fails for any reason is a value, because the caller
        /// must still return to Idle and keep the segments.
        /// </returns>
        /// <remarks>
        /// Side effects: writes <c>list.txt</c> and <c>joined.mp4</c> in the scratch
        /// directory, runs one ffmpeg child, moves the result to
        /// <paramref name="outputPath"/>, and deletes the scratch directory
        /// <em>only</em> when the output file is in place.
        ///
        /// Blocks the caller for the duration of the join (measured at 211 ms for
        /// 67 MB) or until <see cref="JoinTimeoutMs"/>. Failure modes: no usable
        /// segment, a list that cannot be written, ffmpeg missing, ffmpeg exiting
        /// non-zero (a corrupt segment is one), a timeout, a zero-byte joined file,
        /// or a move that cannot complete - each leaves every segment on disk and
        /// names the scratch directory in the outcome.
        /// </remarks>
        internal JoinOutcome Join(string ffmpegPath, string outputPath)
        {
            Stopwatch clock = Stopwatch.StartNew();
            JoinPlan plan = Logic.Session.PlanJoin(_segments);

            AppLog.Line(
                "join: plan=" + plan.Kind
                + " usableSegments=" + plan.Segments.Count.ToString(CultureInfo.InvariantCulture)
                + " of=" + _segments.Count.ToString(CultureInfo.InvariantCulture)
                + " reportedFrames=" + plan.FrameCount.ToString(CultureInfo.InvariantCulture)
                + " scratch=" + _scratchDir);

            if (plan.Kind == JoinKind.None)
            {
                // Nothing usable: there is no evidence worth keeping, and leaving an
                // empty session directory behind would accumulate after every
                // accidental hotkey press.
                DiscardScratch();
                return new JoinOutcome(
                    JoinKind.None, false, null, _scratchDir, 0, 0, clock.ElapsedMilliseconds, null);
            }

            return Concat(ffmpegPath, plan, outputPath, clock);
        }

        /// <summary>
        /// Removes the scratch directory and everything in it, best effort.
        /// </summary>
        /// <remarks>
        /// Side effects: deletes files and directories. A failure is logged and
        /// swallowed, because by the time this is called the outcome the user cares
        /// about (a saved file, or nothing to save) is already decided; a leftover
        /// directory is recovered by DESIGN.md section 5c on the next launch.
        /// </remarks>
        internal void DiscardScratch()
        {
            try
            {
                if (Directory.Exists(_scratchDir))
                {
                    Directory.Delete(_scratchDir, true);
                    AppLog.Line("join: scratch deleted " + _scratchDir);
                }
            }
            catch (IOException ex)
            {
                AppLog.Line("join: could not delete scratch '" + _scratchDir + "': " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                AppLog.Line("join: could not delete scratch '" + _scratchDir + "': " + ex.Message);
            }
        }

        /// <summary>
        /// The default scratch root: <c>%LOCALAPPDATA%\SnipVid\scratch</c>.
        /// </summary>
        /// <returns>An absolute path; not created here.</returns>
        /// <remarks>
        /// Side effects: none. %LOCALAPPDATA% rather than the output folder because
        /// segments are internal state, and rather than %TEMP% because a crash must
        /// leave them somewhere predictable for recovery (DESIGN.md section 5c).
        /// </remarks>
        internal static string DefaultScratchRoot()
        {
            return Path.Combine(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SnipVid"),
                ScratchFolderName);
        }

        /// <summary>Joins the plan's segments with one ffmpeg child, then moves the result into place.</summary>
        /// <param name="ffmpegPath">Resolved ffmpeg executable.</param>
        /// <param name="plan">A plan of kind Concat: one or more segments.</param>
        /// <param name="outputPath">Destination file.</param>
        /// <param name="clock">Timer started at the top of <see cref="Join"/>.</param>
        /// <returns>The outcome, successful when the destination exists afterwards.</returns>
        /// <remarks>
        /// Side effects: writes the list, spawns and waits for one child, moves the
        /// joined file, deletes the scratch directory on success. Failure modes: list
        /// write failure, spawn failure, non-zero exit, timeout, zero-byte output,
        /// move failure - each keeps every segment and reports ffmpeg's last line
        /// where there is one.
        ///
        /// A one-entry list is the common case (no pause at all), not a special case:
        /// handing ffmpeg a list of one is what rewrites the fragmented segment into
        /// an MP4 with a duration (DESIGN.md section 5b).
        /// </remarks>
        private JoinOutcome Concat(string ffmpegPath, JoinPlan plan, string outputPath, Stopwatch clock)
        {
            string listPath = Path.Combine(_scratchDir, Logic.Session.ListFileName);
            string joinedPath = Path.Combine(_scratchDir, JoinedFileName);

            try
            {
                File.WriteAllText(
                    listPath,
                    Logic.Session.ConcatListText(plan.Segments),
                    new UTF8Encoding(false));
            }
            catch (IOException ex)
            {
                return Failed(JoinKind.Concat, plan, clock, "list write failed: " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Failed(JoinKind.Concat, plan, clock, "list write failed: " + ex.Message);
            }

            string[] arguments = Logic.Session.BuildJoinArguments(listPath, joinedPath);
            AppLog.Line("join: argv ffmpeg " + FfmpegArgs.ToCommandLine(arguments));

            int exitCode;
            string lastError;
            bool timedOut;
            string spawnError;
            RunChild(ffmpegPath, arguments, out exitCode, out lastError, out timedOut, out spawnError);

            if (spawnError != null)
            {
                return Failed(JoinKind.Concat, plan, clock, spawnError);
            }
            if (timedOut)
            {
                return Failed(
                    JoinKind.Concat, plan, clock,
                    "ffmpeg did not finish within "
                    + JoinTimeoutMs.ToString(CultureInfo.InvariantCulture) + " ms and was terminated");
            }

            long joinedBytes = Length(joinedPath);
            if (exitCode != 0)
            {
                return Failed(
                    JoinKind.Concat, plan, clock,
                    "ffmpeg exit " + exitCode.ToString(CultureInfo.InvariantCulture)
                    + Describe(lastError));
            }
            if (joinedBytes <= 0)
            {
                // Exit 0 with no file is not a join; treat it as a failure rather
                // than moving an absent file over the user's capture.
                return Failed(
                    JoinKind.Concat, plan, clock,
                    "ffmpeg exited 0 but wrote no joined file" + Describe(lastError));
            }

            AppLog.Line(
                "join: ffmpeg exit=0 elapsedMs=" + clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)
                + " joinedBytes=" + joinedBytes.ToString(CultureInfo.InvariantCulture));

            try
            {
                MoveFile(joinedPath, outputPath);
            }
            catch (IOException ex)
            {
                return Failed(JoinKind.Concat, plan, clock, "move failed: " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Failed(JoinKind.Concat, plan, clock, "move failed: " + ex.Message);
            }
            catch (ArgumentException ex)
            {
                return Failed(JoinKind.Concat, plan, clock, "move failed: " + ex.Message);
            }

            AppLog.Line(
                "join: moved bytes=" + Length(outputPath).ToString(CultureInfo.InvariantCulture)
                + " elapsedMs=" + clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
            DiscardScratch();
            return new JoinOutcome(
                JoinKind.Concat, true, outputPath, _scratchDir, plan.Segments.Count, plan.FrameCount,
                clock.ElapsedMilliseconds, null);
        }

        /// <summary>Builds and logs a failed outcome, leaving every segment in place.</summary>
        /// <param name="kind">The plan that failed.</param>
        /// <param name="plan">The plan, for its counts.</param>
        /// <param name="clock">Timer started at the top of <see cref="Join"/>.</param>
        /// <param name="detail">What went wrong, for the log and the toast.</param>
        /// <returns>The failed outcome; nothing has been deleted.</returns>
        /// <remarks>
        /// Side effects: one log line. The scratch directory is deliberately not
        /// touched: DESIGN.md section 9 forbids deleting evidence on failure.
        /// </remarks>
        private JoinOutcome Failed(JoinKind kind, JoinPlan plan, Stopwatch clock, string detail)
        {
            AppLog.Line(
                "join: failed kind=" + kind + " detail=" + detail
                + " segments kept in " + _scratchDir);
            return new JoinOutcome(
                kind, false, null, _scratchDir, plan.Segments.Count, plan.FrameCount,
                clock.ElapsedMilliseconds, detail);
        }

        /// <summary>
        /// Runs one ffmpeg child to completion, draining stderr so it can never
        /// block on a full pipe.
        /// </summary>
        /// <param name="executable">ffmpeg path.</param>
        /// <param name="arguments">Arguments from <see cref="Logic.Session.BuildJoinArguments"/>.</param>
        /// <param name="exitCode">The child's exit code, or -1 when it never ran.</param>
        /// <param name="lastError">The last non-empty stderr line, or null.</param>
        /// <param name="timedOut">True when the child was killed for exceeding <see cref="JoinTimeoutMs"/>.</param>
        /// <param name="spawnError">Non-null when the child could not be started at all.</param>
        /// <remarks>
        /// Side effects: creates and waits for one process, and may terminate it on
        /// timeout. stderr is read asynchronously because a full pipe would stop the
        /// child before it exits - the classic 4 KB deadlock. stdout is not
        /// redirected: the join writes nothing there and an unread pipe would be one
        /// more way to hang.
        ///
        /// Not in the capture job object: the child is short-lived and awaited, and
        /// the only consequence of the app dying mid-join is a stray ffmpeg writing
        /// into the app's own scratch directory, which crash recovery already
        /// handles (DESIGN.md section 5c).
        /// </remarks>
        private static void RunChild(
            string executable,
            string[] arguments,
            out int exitCode,
            out string lastError,
            out bool timedOut,
            out string spawnError)
        {
            exitCode = -1;
            lastError = null;
            timedOut = false;
            spawnError = null;

            // A local, not the out parameter: the stderr handler runs on a thread of
            // its own and cannot capture an out parameter.
            string capturedError = null;

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = FfmpegArgs.ToCommandLine(arguments),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = false,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty,
            };

            Process child = new Process();
            child.StartInfo = startInfo;

            object gate = new object();
            DataReceivedEventHandler drain = delegate(object sender, DataReceivedEventArgs e)
            {
                if (e.Data == null)
                {
                    return;
                }
                string line = e.Data.Trim();
                if (line.Length == 0)
                {
                    return;
                }
                lock (gate)
                {
                    capturedError = line;
                }
            };

            child.ErrorDataReceived += drain;
            try
            {
                if (!child.Start())
                {
                    spawnError = "could not start '" + executable + "'";
                    return;
                }
            }
            catch (Exception ex)
            {
                spawnError = "could not start '" + executable + "': " + ex.Message;
                return;
            }

            try
            {
                child.BeginErrorReadLine();
                if (!child.WaitForExit(JoinTimeoutMs))
                {
                    timedOut = true;
                    try
                    {
                        child.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                        // Already gone between the timeout and the kill.
                    }
                    child.WaitForExit(KillWaitMs);
                }

                // The parameterless overload waits for the asynchronous stderr
                // handler to finish, so the last line is final before it is read.
                child.WaitForExit();
                try
                {
                    exitCode = child.ExitCode;
                }
                catch (InvalidOperationException)
                {
                    exitCode = -1;
                }
            }
            finally
            {
                child.ErrorDataReceived -= drain;
                child.Dispose();
                lastError = capturedError;
            }
        }

        /// <summary>
        /// Moves a file into place, falling back to copy-then-delete across volumes.
        /// </summary>
        /// <param name="source">Existing file.</param>
        /// <param name="destination">Target path; must not exist.</param>
        /// <remarks>
        /// Side effects: renames or copies then deletes. Failure modes: IOException
        /// when the destination exists, a directory is in the way, the source is
        /// locked, or the copy fails. Copy is only attempted after a failed move
        /// because MoveFile does not cross volumes; the overwrite flag is false so a
        /// racing file with the same name is never silently replaced.
        /// </remarks>
        private static void MoveFile(string source, string destination)
        {
            try
            {
                File.Move(source, destination);
                return;
            }
            catch (IOException)
            {
                // Either a cross-volume move (not supported by MoveFile) or a real
                // error; the copy below distinguishes them, and rethrows a genuine
                // problem with the copy's own message.
            }

            File.Copy(source, destination, false);
            File.Delete(source);
        }

        /// <summary>Returns a file's length in bytes, or -1 when it does not exist.</summary>
        /// <param name="path">Path to inspect; null yields -1.</param>
        /// <returns>The length, or -1.</returns>
        /// <remarks>Side effects: none. Used to reject an exit-0 join that wrote nothing.</remarks>
        private static long Length(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return -1;
            }
            try
            {
                FileInfo info = new FileInfo(path);
                return info.Exists ? info.Length : -1;
            }
            catch (IOException)
            {
                return -1;
            }
            catch (UnauthorizedAccessException)
            {
                return -1;
            }
        }

        /// <summary>Formats ffmpeg's last words for a log line or a message.</summary>
        /// <param name="line">Last stderr line; null when ffmpeg said nothing.</param>
        /// <returns>An empty string, or " ffmpeg said: ...".</returns>
        /// <remarks>Side effects: none.</remarks>
        private static string Describe(string line)
        {
            return string.IsNullOrEmpty(line) ? string.Empty : " ffmpeg said: " + line;
        }
    }
}
