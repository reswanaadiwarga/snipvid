// Recorder - exactly one ffmpeg child, from spawn to a finalised file (WO-09).
//
// WHY this file talks to CreateProcess directly instead of using
// System.Diagnostics.Process: the child has to be created **suspended**, put into
// the job object, and only then resumed (DESIGN.md section 1, and JobObject's
// header). Process.Start has no suspended mode and no way to hand out the process
// handle between creation and first instruction, so the three pipes, the
// STARTUPINFO and the resume are done by hand. Everything else - the argument
// list - is *not* built here: Recorder receives the array from
// FfmpegArgs.Build, which is the only place a command line is written, so the
// -fps_mode cfr / no -pix_fmt rules cannot be re-litigated in this file.
//
// Three things this type guarantees, each because a failure is silent otherwise:
//
//   1. The child dies with the app. Job object, kill-on-close, assigned while
//      suspended. A failed job setup aborts the spawn - a recording that survives
//      a crash is worse than no recording.
//   2. The pipes never fill and never deadlock. ffmpeg with -progress pipe:1
//      writes a block every 50 ms (FfmpegArgs.StatsPeriod) and errors on stderr;
//      both are drained by dedicated background threads for the child's whole
//      life. A pipe nobody reads blocks ffmpeg and the file stops growing, which
//      looks like a frozen app with no error anywhere.
//   3. Stopping is bounded. "q" on stdin is the graceful path (ffmpeg writes the
//      trailer); 1500 ms later the child is terminated. An unbounded wait would
//      hang the stop, and killing first would throw away the trailer of a file
//      that was one read away from being complete.
//
// Threading contract: Start/Stop/Dispose are for the owning (UI) thread. The
// CaptureReady and Exited events are raised on background threads, because they
// are produced by the stdout drain and the wait thread; a subscriber that touches
// WPF must marshal. Stop blocks for at most StopTimeoutMs + KillWaitMs.
//
// What this type deliberately does not know: sessions, segments, pause, joining,
// the output folder, naming, or ffmpeg's path. Those are Program's decisions and
// WO-10/WO-12's work, so one Recorder is one file and nothing more.
//
// ASCII-only source: csc falls back to the ANSI codepage for a file that is not
// valid UTF-8.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SnipVid
{
    /// <summary>
    /// The outcome of one stop: whether the child accepted "q", whether it had to
    /// be terminated, and how long the stop took.
    /// </summary>
    /// <remarks>
    /// A carrier for facts, not a policy: Program decides what a non-zero exit code
    /// means for the file on disk (delete it, keep it, say "ended early").
    /// </remarks>
    internal sealed class RecorderStop
    {
        /// <summary>Stores one measured stop.</summary>
        /// <param name="graceful">True when the child exited within the timeout after "q".</param>
        /// <param name="alreadyExited">True when the child was already gone before "q" was written.</param>
        /// <param name="killed">True when this stop called TerminateProcess.</param>
        /// <param name="elapsedMs">Wall-clock milliseconds from stop request to the child being gone.</param>
        /// <param name="exitCode">The child's exit code, or -1 when it could not be read.</param>
        /// <param name="writeError">Why the "q" write failed, or null when it was written.</param>
        /// <remarks>Side effects: none. Every property is read-only.</remarks>
        internal RecorderStop(
            bool graceful, bool alreadyExited, bool killed, long elapsedMs, int exitCode, string writeError)
        {
            Graceful = graceful;
            AlreadyExited = alreadyExited;
            Killed = killed;
            ElapsedMs = elapsedMs;
            ExitCode = exitCode;
            WriteError = writeError;
        }

        /// <summary>True when "q" was honoured inside the timeout, so the trailer was written.</summary>
        internal bool Graceful { get; private set; }

        /// <summary>True when the child had already exited, so this stop killed nothing and wrote nothing.</summary>
        internal bool AlreadyExited { get; private set; }

        /// <summary>True when the timeout expired and this stop terminated the child.</summary>
        /// <remarks>
        /// Distinct from a non-zero exit code: a forced stop still leaves a playable
        /// fragmented file, while an unrequested non-zero exit means ffmpeg gave up.
        /// Program uses exactly that distinction for the "ended early" message.
        /// </remarks>
        internal bool Killed { get; private set; }

        /// <summary>Milliseconds from the stop request to the child being gone.</summary>
        internal long ElapsedMs { get; private set; }

        /// <summary>Exit code of the child; -1 when it could not be read.</summary>
        internal int ExitCode { get; private set; }

        /// <summary>Non-null when writing "q" to the child's stdin failed.</summary>
        internal string WriteError { get; private set; }
    }

    /// <summary>
    /// Owns one ffmpeg child: spawn it suspended into a job, drain its pipes,
    /// report the first frame, and stop it within a bounded time.
    /// </summary>
    /// <remarks>
    /// One instance per file. Not reusable after <see cref="Stop"/> or
    /// <see cref="Dispose"/>; a new segment is a new instance (WO-10).
    /// </remarks>
    internal sealed class Recorder : IDisposable
    {
        /// <summary>
        /// How long "q" may take before the child is terminated (DESIGN.md section 5:
        /// 1500 ms).
        /// </summary>
        /// <remarks>
        /// ffmpeg needs a few hundred milliseconds to write the trailer; the bound is
        /// generous enough for a slow disk and short enough that a stop never looks
        /// like a hang.
        /// </remarks>
        internal const int StopTimeoutMs = 1500;

        /// <summary>How long a terminated child is given to be reaped before the stop gives up.</summary>
        /// <remarks>
        /// TerminateProcess is asynchronous; without a short wait the caller could
        /// read the exit code while the process still exists and report a stale one.
        /// </remarks>
        private const int KillWaitMs = 750;

        /// <summary>
        /// How long a drain thread may take to reach end-of-pipe after the child is
        /// gone, before the last stderr line is accepted as final.
        /// </summary>
        private const int DrainJoinMs = 750;

        /// <summary>
        /// The fallback for first-frame detection (DESIGN.md section 5a): if no
        /// progress line arrives in this time, recording proceeds and says so.
        /// </summary>
        /// <remarks>
        /// A slow first frame is not a failure. Aborting a working recording because
        /// reporting was late would be the worse error, so the watchdog completes
        /// Starting with an explicit log line instead.
        /// </remarks>
        internal const int FirstFrameTimeoutMs = 2000;

        /// <summary>Flag value for "no value read yet" in <see cref="_exitCode"/>.</summary>
        private const int UnknownExitCode = -1;

        /// <summary>Recorded on every spawn: the executable that was resolved for this child.</summary>
        private readonly string _executable;

        /// <summary>The argument array from <see cref="SnipVid.Logic.FfmpegArgs.Build"/>.</summary>
        private readonly string[] _arguments;

        /// <summary>Guards the mutable state read by the background threads.</summary>
        private readonly object _gate = new object();

        /// <summary>Monotonic clock started in <see cref="Start"/>, for the first-frame latency.</summary>
        private Stopwatch _sinceStart;

        /// <summary>The process handle; zero before spawn and after dispose.</summary>
        private IntPtr _process;

        /// <summary>The thread handle from CreateProcess; closed as soon as the child is resumed.</summary>
        private IntPtr _thread;

        /// <summary>Write end of the child's stdin: the only way "q" reaches ffmpeg.</summary>
        private Stream _stdin;

        /// <summary>Read end of the child's stdout, where -progress writes key=value lines.</summary>
        private Stream _stdout;

        /// <summary>Read end of the child's stderr, which is errors-only (-loglevel error).</summary>
        private Stream _stderr;

        /// <summary>Drains stdout for the child's whole life.</summary>
        private Thread _stdoutThread;

        /// <summary>Drains stderr for the child's whole life, keeping the last line.</summary>
        private Thread _stderrThread;

        /// <summary>Blocks on the process handle and raises <see cref="Exited"/> once.</summary>
        private Thread _waitThread;

        /// <summary>Fires once at <see cref="FirstFrameTimeoutMs"/> when no frame was reported.</summary>
        private Timer _watchdog;

        /// <summary>The job that owns the child; disposed after the child is gone.</summary>
        private JobObject _job;

        /// <summary>1 once <see cref="CaptureReady"/> has been raised; the once-only guard.</summary>
        private int _captureReadyRaised;

        /// <summary>1 once <see cref="Exited"/> has been raised.</summary>
        private int _exitRaised;

        /// <summary>True from the first frame= greater than 0 (or the watchdog timeout).</summary>
        private volatile bool _captureReady;

        /// <summary>True when capture readiness came from the 2000 ms fallback rather than a frame line.</summary>
        private volatile bool _captureReadyTimedOut;

        /// <summary>True once the child is gone, whichever way it went.</summary>
        private volatile bool _exited;

        /// <summary>True once <see cref="Dispose"/> has run.</summary>
        private bool _disposed;

        /// <summary>The child's exit code, or <see cref="UnknownExitCode"/>.</summary>
        private int _exitCode = UnknownExitCode;

        /// <summary>The last non-empty stderr line: ffmpeg's own words about a failure.</summary>
        private string _lastStderr;

        /// <summary>The last frame count ffmpeg reported, or 0 before any report.</summary>
        /// <remarks>
        /// Read once, after <see cref="Stop"/>, to decide whether the segment
        /// contributed a frame. A paused-and-stopped-immediately segment reports
        /// frame=0 while still leaving a non-empty file, so the file's size cannot
        /// answer this question - only the progress line can (WO-10 C6).
        /// </remarks>
        private long _lastFrameCount;

        /// <summary>Milliseconds from spawn to the first reported frame; -1 until then.</summary>
        private long _firstFrameMs = -1;

        /// <summary>Set once <see cref="Stop"/> has an outcome, so a second call reports the same one.</summary>
        private RecorderStop _stop;

        /// <summary>
        /// Describes one spawn: the executable, its argument array and therefore the
        /// output path it will write.
        /// </summary>
        /// <param name="executable">Resolved ffmpeg path; passed to CreateProcess as lpApplicationName.</param>
        /// <param name="arguments">
        /// Complete argument array from <see cref="SnipVid.Logic.FfmpegArgs.Build"/>,
        /// output path last. Never assembled here.
        /// </param>
        /// <remarks>
        /// Side effects: none. Nothing is spawned until <see cref="Start"/>, and an
        /// invalid path is reported there rather than thrown here, so the caller has
        /// one failure channel.
        /// </remarks>
        internal Recorder(string executable, string[] arguments)
        {
            _executable = executable;
            _arguments = arguments ?? new string[0];
        }

        /// <summary>
        /// Raised once when capture is considered started: the first reported frame
        /// greater than 0, or the 2000 ms fallback.
        /// </summary>
        /// <remarks>
        /// Raised on a background thread. Fires at most once per instance.
        /// </remarks>
        internal event EventHandler CaptureReady;

        /// <summary>Raised once when the child has exited, for any reason.</summary>
        /// <remarks>
        /// Raised on a background thread. A subscriber that stops the recording in
        /// response must check whether it asked for the exit first - otherwise a
        /// normal stop would be reported as a crash.
        /// </remarks>
        internal event EventHandler Exited;

        /// <summary>Process id of the child, or 0 before spawn.</summary>
        /// <remarks>Logged so the process list can be matched to the log line.</remarks>
        internal int ProcessId { get; private set; }

        /// <summary>True once a frame= greater than 0 has been seen.</summary>
        internal bool HasFirstFrame
        {
            get { return _firstFrameMs >= 0; }
        }

        /// <summary>True when readiness came from the 2000 ms fallback.</summary>
        internal bool CaptureReadyTimedOut
        {
            get { return _captureReadyTimedOut; }
        }

        /// <summary>Milliseconds from spawn to the first frame; -1 when none was reported.</summary>
        internal long FirstFrameElapsedMs
        {
            get { return Interlocked.Read(ref _firstFrameMs); }
        }

        /// <summary>The last frame count ffmpeg reported for this child.</summary>
        /// <remarks>
        /// A completed 2 s segment at 60 fps reports 120 here. Zero means ffmpeg
        /// never produced a frame, which is what makes a segment unusable to the
        /// join (DESIGN.md section 9, WO-10 C6). Read after <see cref="Stop"/>.
        /// </remarks>
        internal long Frames
        {
            get { return Interlocked.Read(ref _lastFrameCount); }
        }

        /// <summary>True once the child has exited.</summary>
        internal bool HasExited
        {
            get { return _exited; }
        }

        /// <summary>The last non-empty stderr line ffmpeg wrote, or null.</summary>
        /// <remarks>Read after <see cref="Stop"/> so the drain threads have finished.</remarks>
        internal string LastStderrLine
        {
            get
            {
                lock (_gate)
                {
                    return _lastStderr;
                }
            }
        }

        /// <summary>The child's exit code, or -1 when it is not known yet.</summary>
        internal int ExitCode
        {
            get
            {
                lock (_gate)
                {
                    return _exitCode;
                }
            }
        }

        /// <summary>
        /// Spawns ffmpeg suspended, assigns it to a kill-on-close job, resumes it and
        /// starts draining its pipes.
        /// </summary>
        /// <param name="error">
        /// Null on success, otherwise one sentence naming the failed step and its
        /// Win32 error. The caller turns it into a note and deletes any empty file.
        /// </param>
        /// <returns>True when the child is running and owned by this recorder.</returns>
        /// <remarks>
        /// Side effects: creates three pipe pairs, one job handle, one process and
        /// three background threads, and starts the first-frame watchdog. Failure
        /// modes are all non-fatal to the process: a missing executable, a pipe or
        /// CreateProcess failure, a job that cannot be configured or assigned, and a
        /// child that cannot be resumed. Each closes everything created so far and
        /// leaves no child behind, so the caller can retry with a different path
        /// without leaking a handle or a process.
        /// </remarks>
        internal bool Start(out string error)
        {
            error = null;

            if (string.IsNullOrEmpty(_executable))
            {
                error = "no ffmpeg executable was supplied to the recorder";
                return false;
            }

            // The job first: a child that exists before its job is a child that
            // could outlive the app for however long the assignment takes.
            string jobError;
            _job = JobObject.Create(out jobError);
            if (_job == null)
            {
                error = jobError;
                return false;
            }

            Win32Process.SecurityAttributes inherit = new Win32Process.SecurityAttributes();
            inherit.Length = Marshal.SizeOf(typeof(Win32Process.SecurityAttributes));
            inherit.InheritHandle = 1;

            IntPtr stdinRead = IntPtr.Zero;
            IntPtr stdinWrite = IntPtr.Zero;
            IntPtr stdoutRead = IntPtr.Zero;
            IntPtr stdoutWrite = IntPtr.Zero;
            IntPtr stderrRead = IntPtr.Zero;
            IntPtr stderrWrite = IntPtr.Zero;

            if (!Win32Process.CreatePipe(out stdinRead, out stdinWrite, ref inherit)
                || !Win32Process.CreatePipe(out stdoutRead, out stdoutWrite, ref inherit)
                || !Win32Process.CreatePipe(out stderrRead, out stderrWrite, ref inherit))
            {
                error = "CreatePipe failed with windows error " + Marshal.GetLastWin32Error();
                CloseAll(stdinRead, stdinWrite, stdoutRead, stdoutWrite, stderrRead, stderrWrite);
                DisposeJob();
                return false;
            }

            // Both ends of a CreatePipe pair inherit by default. The child must get
            // the three it is pointed at, and nothing else: a child holding the
            // read end of its own stdout would keep that pipe open after it exits,
            // and the drain thread would never see end-of-file.
            Win32Process.SetHandleInformation(stdinWrite, Win32Process.HandleFlagInherit, 0);
            Win32Process.SetHandleInformation(stdoutRead, Win32Process.HandleFlagInherit, 0);
            Win32Process.SetHandleInformation(stderrRead, Win32Process.HandleFlagInherit, 0);

            Win32Process.StartupInfo startup = new Win32Process.StartupInfo();
            startup.Size = Marshal.SizeOf(typeof(Win32Process.StartupInfo));
            startup.Flags = Win32Process.StartfUseStdHandles | Win32Process.StartfUseShowWindow;
            startup.ShowWindow = Win32Process.SwHide;
            startup.StdInput = stdinRead;
            startup.StdOutput = stdoutWrite;
            startup.StdError = stderrWrite;

            // lpApplicationName names the executable and the command line repeats it
            // as argv[0], which is what CreateProcess's child expects. Both go
            // through FfmpegArgs.ToCommandLine, the audited quoting implementation:
            // this machine's real path has no spaces today, but the config key can
            // point anywhere a user likes.
            string commandLine = SnipVid.Logic.FfmpegArgs.ToCommandLine(new string[] { _executable })
                + " " + SnipVid.Logic.FfmpegArgs.ToCommandLine(_arguments);
            StringBuilder mutableCommandLine = new StringBuilder(commandLine, commandLine.Length + 16);

            // The clock starts here, not at the top of Start: the measured interval
            // is "spawn to first frame", which is the number DESIGN.md section 10's
            // budget is about, and pipe setup is not part of it.
            _sinceStart = Stopwatch.StartNew();

            Win32Process.ProcessInformation child;
            bool created = Win32Process.CreateProcess(
                _executable,
                mutableCommandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                true,
                Win32Process.CreateSuspended | Win32Process.CreateNoWindow,
                IntPtr.Zero,
                null,
                ref startup,
                out child);

            if (!created)
            {
                error = "could not start '" + _executable + "': windows error " + Marshal.GetLastWin32Error();
                CloseAll(stdinRead, stdinWrite, stdoutRead, stdoutWrite, stderrRead, stderrWrite);
                DisposeJob();
                return false;
            }

            // The child has its own copies now; keeping these open in the parent
            // would keep the pipes alive after the child exits.
            CloseAll(stdinRead, IntPtr.Zero, IntPtr.Zero, stdoutWrite, IntPtr.Zero, stderrWrite);
            stdinRead = IntPtr.Zero;
            stdoutWrite = IntPtr.Zero;
            stderrWrite = IntPtr.Zero;

            _process = child.Process;
            _thread = child.Thread;
            ProcessId = child.ProcessId;

            string assignError;
            if (!_job.Assign(_process, out assignError))
            {
                // No job, no guarantee, so no recording: terminate the suspended
                // child and report. It has executed no instruction yet, so the
                // output file does not exist.
                error = assignError;
                Win32Process.TerminateProcess(_process, 1);
                Win32Process.CloseHandle(_thread);
                _thread = IntPtr.Zero;
                Win32Process.CloseHandle(_process);
                _process = IntPtr.Zero;
                CloseAll(stdinRead, stdinWrite, stdoutRead, stdoutWrite, stderrRead, stderrWrite);
                DisposeJob();
                return false;
            }

            // Assignment is done, so the child may now run. A resume failure leaves
            // a suspended process that can never produce a frame; terminate it.
            if (Win32Process.ResumeThread(_thread) == 0xFFFFFFFF)
            {
                error = "ResumeThread failed with windows error " + Marshal.GetLastWin32Error();
                Win32Process.TerminateProcess(_process, 1);
                Win32Process.CloseHandle(_thread);
                _thread = IntPtr.Zero;
                Win32Process.CloseHandle(_process);
                _process = IntPtr.Zero;
                CloseAll(stdinRead, stdinWrite, stdoutRead, stdoutWrite, stderrRead, stderrWrite);
                DisposeJob();
                return false;
            }
            Win32Process.CloseHandle(_thread);
            _thread = IntPtr.Zero;

            // SafeFileHandle owns the handle from here, so the failure paths above
            // are the only places the raw values have to be closed by hand.
            _stdin = new FileStream(new SafeFileHandle(stdinWrite, true), FileAccess.Write, 256);
            _stdout = new FileStream(new SafeFileHandle(stdoutRead, true), FileAccess.Read, 4096);
            _stderr = new FileStream(new SafeFileHandle(stderrRead, true), FileAccess.Read, 4096);

            _stdoutThread = StartDrain("SnipVid.Recorder.stdout", DrainStdout);
            _stderrThread = StartDrain("SnipVid.Recorder.stderr", DrainStderr);
            _waitThread = StartDrain("SnipVid.Recorder.wait", WaitForExit);

            // After the spawn clock, and only now: a watchdog that could fire during
            // the first few statements would report a timeout that has not elapsed.
            _watchdog = new Timer(OnFirstFrameWatchdog, null, FirstFrameTimeoutMs, Timeout.Infinite);
            return true;
        }

        /// <summary>
        /// Stops the child: "q" on stdin, up to <see cref="StopTimeoutMs"/> for it to
        /// exit, then TerminateProcess.
        /// </summary>
        /// <returns>
        /// The measured outcome. The same instance is returned by every later call,
        /// so a double stop cannot report two different results.
        /// </returns>
        /// <remarks>
        /// Side effects: writes to the child's stdin, may terminate it, joins the
        /// drain threads so <see cref="LastStderrLine"/> is final, and discloses the
        /// stdin stream. Blocks the caller for at most StopTimeoutMs + KillWaitMs.
        /// Failure modes: a broken stdin is recorded in
        /// <see cref="RecorderStop.WriteError"/> and the timeout path still runs, so
        /// a stop can never do nothing.
        /// </remarks>
        internal RecorderStop Stop()
        {
            lock (_gate)
            {
                if (_stop != null)
                {
                    return _stop;
                }
            }

            Stopwatch clock = Stopwatch.StartNew();

            if (_watchdog != null)
            {
                _watchdog.Dispose();
                _watchdog = null;
            }

            bool alreadyExited = HasExited;
            bool graceful = false;
            bool killed = false;
            string writeError = null;

            if (!alreadyExited)
            {
                try
                {
                    Stream stdin = _stdin;
                    if (stdin == null)
                    {
                        writeError = "stdin is not open";
                    }
                    else
                    {
                        // One command, then newline: ffmpeg's stdin reader wants the
                        // line terminator to act on it.
                        byte[] quit = new byte[] { (byte)'q', (byte)'\n' };
                        stdin.Write(quit, 0, quit.Length);
                        stdin.Flush();
                    }
                }
                catch (IOException ex)
                {
                    writeError = ex.Message;
                }
                catch (ObjectDisposedException ex)
                {
                    writeError = ex.Message;
                }

                graceful = WaitForChild(StopTimeoutMs);
                if (!graceful)
                {
                    killed = true;
                    Win32Process.TerminateProcess(_process, 1);
                    WaitForChild(KillWaitMs);
                }
            }

            // The child is gone, so both pipes are at end-of-file; joining here is
            // what makes the last error line available to the caller instead of a
            // race against a thread that may still be reading.
            JoinDrainer(_stdoutThread);
            JoinDrainer(_stderrThread);

            RefreshExitCode();

            RecorderStop outcome = new RecorderStop(
                graceful, alreadyExited, killed, clock.ElapsedMilliseconds, ExitCode, writeError);
            lock (_gate)
            {
                _stop = outcome;
            }
            return outcome;
        }

        /// <summary>
        /// Terminates a child that is still running and releases every handle. Safe to
        /// call after <see cref="Stop"/> and safe to call more than once.
        /// </summary>
        /// <remarks>
        /// Side effects: may terminate the child (only on the error path, because a
        /// normal stop has already ended it), closes all three streams, the process
        /// handle and the job - and closing the job is what kills a surviving child,
        /// by design. Waits for the wait thread before closing the process handle so
        /// no thread is left using a closed handle.
        /// </remarks>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
            }

            if (_watchdog != null)
            {
                _watchdog.Dispose();
                _watchdog = null;
            }

            if (_process != IntPtr.Zero && !HasExited)
            {
                Win32Process.TerminateProcess(_process, 1);
                WaitForChild(KillWaitMs);
            }

            JoinDrainer(_stdoutThread);
            JoinDrainer(_stderrThread);
            JoinDrainer(_waitThread);

            DisposeStream(ref _stdin);
            DisposeStream(ref _stdout);
            DisposeStream(ref _stderr);

            if (_process != IntPtr.Zero)
            {
                Win32Process.CloseHandle(_process);
                _process = IntPtr.Zero;
            }
            if (_thread != IntPtr.Zero)
            {
                Win32Process.CloseHandle(_thread);
                _thread = IntPtr.Zero;
            }
            DisposeJob();
        }

        /// <summary>
        /// Starts one background drain thread.
        /// </summary>
        /// <param name="name">Thread name, so a dump or debugger shows which pipe it is.</param>
        /// <param name="body">Thread entry point.</param>
        /// <returns>The started thread.</returns>
        /// <remarks>
        /// Side effects: creates and starts an OS thread. Background so a stuck read
        /// can never keep the process alive after Exit.
        /// </remarks>
        private static Thread StartDrain(string name, ThreadStart body)
        {
            Thread thread = new Thread(body);
            thread.Name = name;
            thread.IsBackground = true;
            thread.Start();
            return thread;
        }

        /// <summary>
        /// Reads stdout for the child's whole life and ends Starting on the first
        /// frame greater than 0.
        /// </summary>
        /// <remarks>
        /// Side effects: consumes the stdout pipe (which is the point - an unread
        /// pipe blocks ffmpeg), sets the first-frame fields and raises
        /// <see cref="CaptureReady"/>. Failure modes: an I/O error ends the loop
        /// silently, because the process exit path is what reports a dead child.
        ///
        /// Lines are key=value blocks terminated by a bare "progress=" line; only
        /// "frame=" is used. A frame=0 line is not a first frame - ddagrab reports 0
        /// before the first frame lands, and treating that as started is exactly the
        /// bug Starting exists to prevent.
        /// </remarks>
        private void DrainStdout()
        {
            Stream stdout = _stdout;
            if (stdout == null)
            {
                return;
            }

            try
            {
                using (StreamReader reader = new StreamReader(stdout, Encoding.UTF8, false, 4096))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        int separator = line.IndexOf('=');
                        if (separator <= 0)
                        {
                            continue;
                        }
                        if (!string.Equals(line.Substring(0, separator).Trim(), "frame", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        int frames;
                        if (!int.TryParse(
                            line.Substring(separator + 1).Trim(),
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out frames))
                        {
                            continue;
                        }

                        // Keep the latest count even when it is not a first frame:
                        // the session needs to know whether this segment produced
                        // anything at all before listing it for the join.
                        Interlocked.Exchange(ref _lastFrameCount, frames);

                        if (frames > 0)
                        {
                            OnFirstFrame();
                        }
                    }
                }
            }
            catch (IOException)
            {
                // The child is gone or the pipe was closed by Dispose; either way
                // there is nothing to read and nothing to report from here.
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// Reads stderr for the child's whole life, keeping only the last non-empty
        /// line.
        /// </summary>
        /// <remarks>
        /// Side effects: consumes the stderr pipe. Keeping one line rather than a
        /// buffer is deliberate: -loglevel error means a line is a failure, and the
        /// user-facing message can carry exactly one of them (DESIGN.md section 9).
        /// </remarks>
        private void DrainStderr()
        {
            Stream stderr = _stderr;
            if (stderr == null)
            {
                return;
            }

            try
            {
                using (StreamReader reader = new StreamReader(stderr, Encoding.UTF8, false, 4096))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string trimmed = line.Trim();
                        if (trimmed.Length == 0)
                        {
                            continue;
                        }
                        lock (_gate)
                        {
                            _lastStderr = trimmed;
                        }
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>
        /// Blocks on the process handle, records the exit code and raises
        /// <see cref="Exited"/> once.
        /// </summary>
        /// <remarks>
        /// Side effects: sets the exit fields and raises the event on this thread.
        /// Runs until the child exits, which is the child's whole life by
        /// definition.
        /// </remarks>
        private void WaitForExit()
        {
            IntPtr process = _process;
            if (process == IntPtr.Zero)
            {
                return;
            }

            Win32Process.WaitForSingleObject(process, Win32Process.Infinite);

            int code;
            if (Win32Process.GetExitCodeProcess(process, out code))
            {
                lock (_gate)
                {
                    _exitCode = code;
                }
            }

            _exited = true;
            if (Interlocked.Exchange(ref _exitRaised, 1) == 0)
            {
                EventHandler handler = Exited;
                if (handler != null)
                {
                    try
                    {
                        handler(this, EventArgs.Empty);
                    }
                    catch (Exception)
                    {
                        // A subscriber's failure is its own business: throwing out
                        // of a drain thread would tear down the pipe handling and
                        // hide the real state of the child.
                    }
                }
            }
        }

        /// <summary>
        /// Records the first frame and completes Starting.
        /// </summary>
        /// <remarks>
        /// Side effects: sets the first-frame timestamp (once) and raises
        /// <see cref="CaptureReady"/>. Later frame lines fall through the
        /// Interlocked guard, so the event fires exactly once.
        /// </remarks>
        private void OnFirstFrame()
        {
            if (Interlocked.CompareExchange(ref _firstFrameMs, 0, -1) != -1)
            {
                return;
            }

            Stopwatch clock = _sinceStart;
            if (clock != null)
            {
                Interlocked.Exchange(ref _firstFrameMs, clock.ElapsedMilliseconds);
            }

            if (Interlocked.Exchange(ref _captureReadyRaised, 1) == 0)
            {
                _captureReady = true;
                EventHandler handler = CaptureReady;
                if (handler != null)
                {
                    try
                    {
                        handler(this, EventArgs.Empty);
                    }
                    catch (Exception)
                    {
                        // See WaitForExit: a subscriber must not break the pipe
                        // reader.
                    }
                }
            }
        }

        /// <summary>
        /// Watchdog: completes Starting when no frame line arrived in time.
        /// </summary>
        /// <param name="state">Unused; the heap-allocated box holds nothing.</param>
        /// <remarks>
        /// Side effects: sets <see cref="CaptureReadyTimedOut"/> and raises
        /// <see cref="CaptureReady"/> when the child is still alive and silent. A
        /// child that already exited is left alone: its exit is the real news, and
        /// the Exited handler finalises with the actual error.
        /// </remarks>
        private void OnFirstFrameWatchdog(object state)
        {
            if (_captureReady || HasExited)
            {
                return;
            }

            if (Interlocked.Exchange(ref _captureReadyRaised, 1) == 0)
            {
                _captureReadyTimedOut = true;
                _captureReady = true;
                EventHandler handler = CaptureReady;
                if (handler != null)
                {
                    try
                    {
                        handler(this, EventArgs.Empty);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        /// <summary>
        /// Waits for the child to exit, or for the timeout to expire.
        /// </summary>
        /// <param name="timeoutMs">Maximum wait in milliseconds.</param>
        /// <returns>True when the child has exited.</returns>
        /// <remarks>
        /// Side effects: none beyond the wait. A closed or invalid handle reports
        /// false, which sends the caller down the terminate path - the safe
        /// direction, since it cannot leave a child running.
        /// </remarks>
        private bool WaitForChild(int timeoutMs)
        {
            IntPtr process = _process;
            if (process == IntPtr.Zero)
            {
                return true;
            }
            return Win32Process.WaitForSingleObject(process, timeoutMs) == Win32Process.WaitObject0;
        }

        /// <summary>
        /// Re-reads the exit code after the child is known to be gone.
        /// </summary>
        /// <remarks>
        /// Side effects: updates <see cref="_exitCode"/>. Called from Stop because
        /// the wait thread may not have run its own read yet; both reads agree, so a
        /// race here cannot produce two different codes.
        /// </remarks>
        private void RefreshExitCode()
        {
            IntPtr process = _process;
            if (process == IntPtr.Zero)
            {
                return;
            }

            int code;
            if (Win32Process.GetExitCodeProcess(process, out code))
            {
                lock (_gate)
                {
                    _exitCode = code;
                }
            }
        }

        /// <summary>Joins a drain thread, bounded so a stuck pipe cannot hang a stop.</summary>
        /// <param name="thread">Thread to join; null is ignored.</param>
        /// <remarks>Side effects: blocks the caller for at most <see cref="DrainJoinMs"/>.</remarks>
        private static void JoinDrainer(Thread thread)
        {
            if (thread == null)
            {
                return;
            }
            try
            {
                thread.Join(DrainJoinMs);
            }
            catch (ThreadStateException)
            {
                // Not started: nothing to wait for.
            }
        }

        /// <summary>Disposes one stream and clears the field.</summary>
        /// <param name="stream">Field to clear; null is ignored.</param>
        /// <remarks>Side effects: closes the pipe end, which also unblocks a reader.</remarks>
        private static void DisposeStream(ref Stream stream)
        {
            Stream current = stream;
            stream = null;
            if (current != null)
            {
                try
                {
                    current.Dispose();
                }
                catch (IOException)
                {
                }
            }
        }

        /// <summary>Disposes the job if one was created.</summary>
        /// <remarks>
        /// Side effects: closing the last job handle terminates any surviving member.
        /// Only reached with the child already gone, except on the spawn failure
        /// paths, where the child was terminated first for the same reason.
        /// </remarks>
        private void DisposeJob()
        {
            JobObject job = _job;
            _job = null;
            if (job != null)
            {
                job.Dispose();
            }
        }

        /// <summary>Closes whichever raw pipe handles are still open.</summary>
        /// <param name="handles">Up to six handles; zero entries are ignored.</param>
        /// <remarks>
        /// Side effects: closes the handles. Only used on the paths where no
        /// SafeFileHandle has taken ownership yet, so nothing is closed twice.
        /// </remarks>
        private static void CloseAll(
            IntPtr stdinRead, IntPtr stdinWrite,
            IntPtr stdoutRead, IntPtr stdoutWrite,
            IntPtr stderrRead, IntPtr stderrWrite)
        {
            IntPtr[] handles = new IntPtr[]
            {
                stdinRead, stdinWrite, stdoutRead, stdoutWrite, stderrRead, stderrWrite
            };
            for (int i = 0; i < handles.Length; i++)
            {
                if (handles[i] != IntPtr.Zero)
                {
                    Win32Process.CloseHandle(handles[i]);
                }
            }
        }
    }

    /// <summary>
    /// Resolves the ffmpeg executable: configured path, then the folder that holds
    /// SnipVid.exe, then PATH, then the WinGet links directory (DESIGN.md section 5a).
    /// </summary>
    /// <remarks>
    /// The environment is injected - the app folder, the PATH value, the
    /// local-app-data root and an existence predicate - for the same reason
    /// <see cref="SnipVid.Logic.Naming"/> takes a timestamp and a predicate: the
    /// decision is testable without touching this machine, even though this build
    /// keeps it in App rather than Logic. There is no unit test for it because
    /// <c>tests\</c> compiles only src\Logic; the sibling lookup is covered by the
    /// release smoke test instead.
    /// WO-12 formalises this into <c>Logic/FfmpegLocator.cs</c>; the policy encoded
    /// here is the one that work order has to preserve.
    /// </remarks>
    internal static class FfmpegResolver
    {
        /// <summary>The executable name searched for in every directory.</summary>
        private const string ExecutableName = "ffmpeg.exe";

        /// <summary>
        /// Resolves the executable to run.
        /// </summary>
        /// <param name="configuredPath">
        /// <c>ffmpeg_path</c> from config.ini; empty means auto-resolve. Any non-empty
        /// value is authoritative: it is not a hint, and a miss does not fall
        /// through to PATH.
        /// </param>
        /// <param name="appDirectory">
        /// The folder holding SnipVid.exe (normally
        /// <c>AppDomain.CurrentDomain.BaseDirectory</c>). The portable zip puts
        /// ffmpeg.exe next to the exe, so this candidate comes before PATH; it may be
        /// null or empty, in which case it is skipped.
        /// </param>
        /// <param name="pathVariable">The PATH environment variable; may be null or empty.</param>
        /// <param name="localAppData">
        /// %LOCALAPPDATA%, used for the WinGet links fallback; may be null, in which
        /// case that candidate is skipped.
        /// </param>
        /// <param name="fileExists">Existence predicate, called once per candidate.</param>
        /// <param name="executable">The resolved path on success; null otherwise.</param>
        /// <param name="message">
        /// One user-facing sentence on failure: the configured path when there was
        /// one, otherwise every path that was searched.
        /// </param>
        /// <returns>True when a candidate exists.</returns>
        /// <remarks>
        /// Side effects: none beyond calls to <paramref name="fileExists"/>. Never
        /// throws, never retries: DESIGN.md section 9 rules out a retry loop, and a
        /// wrong binary is worse than a clear failure, which is why a configured
        /// path is authoritative rather than a preference.
        /// </remarks>
        internal static bool TryResolve(
            string configuredPath,
            string appDirectory,
            string pathVariable,
            string localAppData,
            Func<string, bool> fileExists,
            out string executable,
            out string message)
        {
            executable = null;
            message = null;

            if (fileExists == null)
            {
                message = "ffmpeg could not be resolved because no existence check was supplied";
                return false;
            }

            if (!string.IsNullOrEmpty(configuredPath))
            {
                if (fileExists(configuredPath))
                {
                    executable = configuredPath;
                    return true;
                }

                // One message, the configured path named, no fallback: using a
                // different binary than the one the user named would be a silent
                // substitution and is the failure WO-12 C2 forbids.
                message = "ffmpeg_path in config.ini points at '" + configuredPath
                    + "', which does not exist. No other path was tried. Fix the path or"
                    + " clear ffmpeg_path to resolve it automatically.";
                return false;
            }

            List<string> searched = new List<string>();

            // The zip ships ffmpeg.exe beside SnipVid.exe, so the app folder wins
            // over PATH: an unpacked copy must work even when another ffmpeg is
            // installed, and the sibling is the one the user downloaded with it.
            if (!string.IsNullOrEmpty(appDirectory))
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(appDirectory, ExecutableName);
                }
                catch (ArgumentException)
                {
                    candidate = null;
                }

                if (candidate != null)
                {
                    if (fileExists(candidate))
                    {
                        executable = candidate;
                        return true;
                    }
                    searched.Add(candidate);
                }
            }

            if (!string.IsNullOrEmpty(pathVariable))
            {
                string[] directories = pathVariable.Split(';');
                for (int i = 0; i < directories.Length; i++)
                {
                    string directory = directories[i].Trim().Trim('"');
                    if (directory.Length == 0)
                    {
                        continue;
                    }

                    string candidate;
                    try
                    {
                        candidate = Path.Combine(directory, ExecutableName);
                    }
                    catch (ArgumentException)
                    {
                        // A malformed PATH entry is not this app's problem; skip it
                        // and report the rest.
                        continue;
                    }

                    if (fileExists(candidate))
                    {
                        executable = candidate;
                        return true;
                    }
                    searched.Add(candidate);
                }
            }

            if (!string.IsNullOrEmpty(localAppData))
            {
                string winget = Path.Combine(
                    Path.Combine(Path.Combine(localAppData, "Microsoft"), "WinGet"), "Links");
                string candidate;
                try
                {
                    candidate = Path.Combine(winget, ExecutableName);
                }
                catch (ArgumentException)
                {
                    candidate = null;
                }

                if (candidate != null)
                {
                    if (fileExists(candidate))
                    {
                        executable = candidate;
                        return true;
                    }
                    searched.Add(candidate);
                }
            }

            message = "ffmpeg was not found. Searched: "
                + (searched.Count == 0
                    ? "(no app folder was supplied, PATH is empty and %LOCALAPPDATA% is unknown)"
                    : string.Join(", ", searched.ToArray()))
                + ". Install ffmpeg or set ffmpeg_path in config.ini.";
            return false;
        }
    }

    /// <summary>
    /// The kernel32 surface Recorder needs: anonymous pipes and a suspended
    /// process.
    /// </summary>
    /// <remarks>
    /// Declared here rather than in Native.cs because Native.cs is the hotkey and
    /// monitor surface, and this build's scope lock does not extend to it. The
    /// declarations are minimal and named after the Win32 structures they mirror;
    /// a wrong size in <see cref="StartupInfo"/> or <see cref="SecurityAttributes"/>
    /// is the classic way this code fails, so the sizes are computed with
    /// Marshal.SizeOf rather than hard-coded.
    /// </remarks>
    internal static class Win32Process
    {
        /// <summary>STARTF_USESHOWWINDOW: <see cref="StartupInfo.ShowWindow"/> is honoured.</summary>
        internal const int StartfUseShowWindow = 0x00000001;

        /// <summary>STARTF_USESTDHANDLES: the three standard handles in StartupInfo are used.</summary>
        internal const int StartfUseStdHandles = 0x00000100;

        /// <summary>SW_HIDE: no console window is shown for the child.</summary>
        internal const short SwHide = 0;

        /// <summary>CREATE_SUSPENDED: the child runs no instruction until ResumeThread.</summary>
        internal const int CreateSuspended = 0x00000004;

        /// <summary>CREATE_NO_WINDOW: a console child of a GUI parent creates no window.</summary>
        internal const int CreateNoWindow = 0x08000000;

        /// <summary>HANDLE_FLAG_INHERIT: the mask used to clear inheritance.</summary>
        internal const int HandleFlagInherit = 0x00000001;

        /// <summary>INFINITE: wait until the handle is signalled.</summary>
        internal const int Infinite = -1;

        /// <summary>WAIT_OBJECT_0: the wait was satisfied.</summary>
        internal const int WaitObject0 = 0;

        /// <summary>Win32 SECURITY_ATTRIBUTES: makes a pipe handle inheritable.</summary>
        /// <remarks>Layout must match exactly; the size field is set by the caller.</remarks>
        [StructLayout(LayoutKind.Sequential)]
        internal struct SecurityAttributes
        {
            /// <summary>Size of this structure in bytes.</summary>
            public int Length;

            /// <summary>Always zero: the default descriptor.</summary>
            public IntPtr SecurityDescriptor;

            /// <summary>Non-zero to let the child inherit the handle.</summary>
            public int InheritHandle;
        }

        /// <summary>Win32 STARTUPINFO: window placement and the three standard handles.</summary>
        /// <remarks>
        /// The string fields are declared as IntPtr and left zero because this app
        /// has no title, desktop or reserved block to pass; that keeps the
        /// marshalling trivial and the size stable.
        /// </remarks>
        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfo
        {
            /// <summary>Size of this structure in bytes; set before the call.</summary>
            public int Size;

            /// <summary>Reserved; zero.</summary>
            public IntPtr Reserved;

            /// <summary>Desktop name; zero for the parent's desktop.</summary>
            public IntPtr Desktop;

            /// <summary>Window title; zero.</summary>
            public IntPtr Title;

            /// <summary>Window x position; unused without STARTF_USEPOSITION.</summary>
            public int X;

            /// <summary>Window y position; unused.</summary>
            public int Y;

            /// <summary>Window width; unused.</summary>
            public int XSize;

            /// <summary>Window height; unused.</summary>
            public int YSize;

            /// <summary>Console buffer width; unused.</summary>
            public int XCountChars;

            /// <summary>Console buffer height; unused.</summary>
            public int YCountChars;

            /// <summary>Console fill attribute; unused.</summary>
            public int FillAttribute;

            /// <summary>STARTF_* flags actually used.</summary>
            public int Flags;

            /// <summary>SW_HIDE, so no console window flashes.</summary>
            public short ShowWindow;

            /// <summary>Reserved; zero.</summary>
            public short Reserved2;

            /// <summary>Reserved; zero.</summary>
            public IntPtr Reserved2Pointer;

            /// <summary>The child's stdin: the read end of the stdin pipe.</summary>
            public IntPtr StdInput;

            /// <summary>The child's stdout: the write end of the progress pipe.</summary>
            public IntPtr StdOutput;

            /// <summary>The child's stderr: the write end of the error pipe.</summary>
            public IntPtr StdError;
        }

        /// <summary>Win32 PROCESS_INFORMATION: the handles and ids CreateProcess returns.</summary>
        /// <remarks>The thread handle is closed immediately after ResumeThread.</remarks>
        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInformation
        {
            /// <summary>The child's process handle.</summary>
            public IntPtr Process;

            /// <summary>The child's main thread handle.</summary>
            public IntPtr Thread;

            /// <summary>The child's process id.</summary>
            public int ProcessId;

            /// <summary>The child's main thread id.</summary>
            public int ThreadId;
        }

        /// <summary>Creates an anonymous pipe pair.</summary>
        /// <param name="readPipe">Receives the read end.</param>
        /// <param name="writePipe">Receives the write end.</param>
        /// <param name="attributes">Inheritance settings for both ends.</param>
        /// <returns>True on success; false with a last error otherwise.</returns>
        /// <remarks>
        /// Side effects: creates two handles. Both inherit according to
        /// <paramref name="attributes"/>; the caller must clear inheritance on the
        /// end the child should not hold.
        /// </remarks>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreatePipe(
            out IntPtr readPipe, out IntPtr writePipe, ref Win32Process.SecurityAttributes attributes);

        /// <summary>Changes one handle's inheritance flag.</summary>
        /// <param name="handle">Handle to change.</param>
        /// <param name="mask">HANDLE_FLAG_INHERIT.</param>
        /// <param name="flags">Zero to clear, HANDLE_FLAG_INHERIT to set.</param>
        /// <returns>True on success; false with a last error otherwise.</returns>
        /// <remarks>Side effects: affects every later CreateProcess with inheritance on.</remarks>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetHandleInformation(IntPtr handle, int mask, int flags);

        /// <summary>Creates a process, optionally suspended, with inherited standard handles.</summary>
        /// <param name="applicationName">Executable path; avoids any PATH search.</param>
        /// <param name="commandLine">Mutable command line, argv[0] first.</param>
        /// <param name="processAttributes">Zero: not inheritable.</param>
        /// <param name="threadAttributes">Zero: not inheritable.</param>
        /// <param name="inheritHandles">True so the three pipe ends reach the child.</param>
        /// <param name="creationFlags">CREATE_SUSPENDED | CREATE_NO_WINDOW.</param>
        /// <param name="environment">Zero: inherit the parent's environment.</param>
        /// <param name="currentDirectory">Null: inherit the parent's directory.</param>
        /// <param name="startupInfo">Standard handles and show state.</param>
        /// <param name="processInformation">Receives the child handles and ids.</param>
        /// <returns>True on success; false with a last error otherwise.</returns>
        /// <remarks>
        /// Side effects: creates a process (suspended, so it runs nothing yet) and
        /// two handles the caller must close. Called with the Unicode entry point
        /// on purpose, so a user profile with non-ASCII characters works.
        /// </remarks>
        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcess(
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            bool inheritHandles,
            int creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref Win32Process.StartupInfo startupInfo,
            out Win32Process.ProcessInformation processInformation);

        /// <summary>Resumes a thread created suspended.</summary>
        /// <param name="thread">The thread handle from CreateProcess.</param>
        /// <returns>The previous suspend count, or 0xFFFFFFFF on failure.</returns>
        /// <remarks>Side effects: the child starts executing immediately.</remarks>
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint ResumeThread(IntPtr thread);

        /// <summary>Waits for an object to become signalled.</summary>
        /// <param name="handle">Process handle.</param>
        /// <param name="milliseconds">Timeout, or INFINITE.</param>
        /// <returns>WAIT_OBJECT_0 when signalled, another WAIT_* value otherwise.</returns>
        /// <remarks>Side effects: blocks the calling thread.</remarks>
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern int WaitForSingleObject(IntPtr handle, int milliseconds);

        /// <summary>Reads a process's exit code.</summary>
        /// <param name="process">Process handle with query access.</param>
        /// <param name="exitCode">Receives the exit code, or 259 while still running.</param>
        /// <returns>True on success; false with a last error otherwise.</returns>
        /// <remarks>Side effects: none.</remarks>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeProcess(IntPtr process, out int exitCode);

        /// <summary>Terminates a process without giving it a chance to clean up.</summary>
        /// <param name="process">Process handle with terminate access.</param>
        /// <param name="exitCode">Exit code to record; 1 marks a stop that was forced.</param>
        /// <returns>True on success; false with a last error otherwise.</returns>
        /// <remarks>
        /// Side effects: the child dies immediately. Used only after "q" was ignored
        /// for 1500 ms, or on a spawn failure where the child never ran.
        /// </remarks>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateProcess(IntPtr process, uint exitCode);

        /// <summary>Closes one kernel handle.</summary>
        /// <param name="handle">Handle to close.</param>
        /// <returns>True on success; the result is ignored.</returns>
        /// <remarks>Side effects: releases the handle; the object goes away at the last close.</remarks>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
