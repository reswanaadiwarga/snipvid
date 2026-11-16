// SnipVid entry point: single instance, tray, and the hotkey that wakes it (M1).
//
// The process owns exactly four things here - the instance mutex, the tray icon,
// the hidden hotkey window, and the WPF dispatcher lifetime. There is no window
// to close, because this is a tray utility by design (DESIGN.md section 1); the
// WPF Application exists only to pump messages for the tray and for the hotkey's
// HwndSource.
//
// Startup order is deliberate: tray icon, then config, then hotkey. The icon has
// to be visible before the first note is surfaced, because a note is delivered as
// a balloon tip on that icon (DESIGN.md section 8: once on startup, never a modal
// dialog). The hotkey is armed last so no press can arrive while the config is
// still being read, and so the arming outcome can be reported through the icon
// that already exists.
//
// Capture is record -> stop -> one file on disk: a hotkey press opens the region
// picker, the picker hands back a monitor-relative rectangle, and
// CaptureController then resolves ffmpeg, creates the output folder, picks a
// collision-safe name, spawns the recorder, shows the stop pill and finalises the
// file. The state machine (Idle -> Starting -> Recording -> Finalising) lives in
// CaptureController; this file only routes events to it.
//
// The child never outlives the process: Recorder assigns it to a kill-on-close
// job object before it runs a single instruction (DESIGN.md section 1), which is
// what makes a Task Manager kill leave a playable file instead of an orphan
// writing to the user's Videos folder.
//
// Threading: everything runs on the single [STAThread] UI thread. The tray click
// handlers, the hotkey message hook and the WPF objects therefore share one
// thread and need no marshalling.
//
// ASCII-only source: csc falls back to the ANSI codepage for files that are not
// valid UTF-8, so anything non-ASCII here could silently change a string
// literal. Keep it ASCII.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Win32;
using SnipVid.Logic;
using Application = System.Windows.Application;

namespace SnipVid
{
    internal static class Program
    {
        // Deliberately no "Global\" prefix: the lock is per logon session, so
        // two signed-in users can each run one copy and neither needs admin
        // rights. A live instance also owns the hotkey registration, which a
        // second copy must not duplicate (DESIGN.md sections 1 and 9).
        private const string MutexName = "SnipVid.SingleInstance";

        /// <summary>
        /// Monotonic clock for invocation timestamps. Started by the first statement
        /// of <see cref="Main"/> and assigned nowhere else.
        /// </summary>
        /// <remarks>
        /// WHY this is a plain field assigned in <see cref="Main"/> instead of
        /// <c>static readonly Stopwatch ... = Stopwatch.StartNew()</c>: this class has
        /// no explicit static constructor, so it is <c>beforefieldinit</c> and the
        /// runtime may run its static field initializers at any point before the
        /// first *read* of a static field - in practice at that read, which happens
        /// inside the hotkey handler. A field initializer therefore started the clock
        /// on the first press, not at process start: measured on 2026-11-15, armed at
        /// 12:58:01 and pressed at 12:58:20, the log said <c>t+0</c>. The deltas were
        /// right and the origin was 19 s wrong, which is the worst kind of wrong -
        /// every latency taken from this clock (DESIGN.md section 10, WO-07 and
        /// WO-09 budgets) was silently relative to a key press instead of launch.
        /// An assignment in <see cref="Main"/> is ordered by the code, so the origin
        /// is process start by construction rather than by initialisation policy.
        /// Do not move this into a field initializer or a static constructor.
        ///
        /// Invariant: non-null from the first statement of Main onwards. The hotkey
        /// cannot fire earlier, because the window that receives WM_HOTKEY is only
        /// created later in Main and messages are only pumped by Application.Run.
        ///
        /// A Stopwatch measures elapsed time and is immune to wall-clock jumps (NTP
        /// correction, a manual clock change), which is the monotonic part of C1.
        /// <see cref="AppLog"/> still stamps each line with wall-clock time, so a
        /// press can be placed in time as well as ordered against the others.
        /// </remarks>
        private static Stopwatch _clock;

        /// <summary>
        /// The displays enumerated once at startup (WO-08). Read by the overlay to
        /// pick the monitor a drag started on; null until <see cref="Main"/> has
        /// enumerated them, which is before the hotkey is armed, so a press can
        /// never see null.
        /// </summary>
        /// <remarks>
        /// Deliberately enumerated once and not per capture: WO-08 requires the
        /// monitor that contained the drag origin, and a list that changed under a
        /// live drag would make that answer move. A display hot-plugged later is
        /// therefore invisible until the next launch - acceptable for v1, and
        /// stated in the log line that names the enumeration order.
        /// </remarks>
        private static List<MonitorInfo> _monitors;

        /// <summary>
        /// The overlay currently on screen, or null when idle. One at a time: a
        /// second hotkey press while it is open is ignored rather than stacking a
        /// second picker on top of the first.
        /// </summary>
        private static OverlayWindow _overlay;

        /// <summary>
        /// The parsed config, assigned in <see cref="Main"/> before the hotkey is
        /// armed. Null only in the brief window before the config has been read.
        /// </summary>
        /// <remarks>
        /// Held as a field rather than captured in a closure because both the hotkey
        /// handler and the tray's Open folder command need it, and they are
        /// registered at different points in startup.
        /// </remarks>
        private static Config _config;

        /// <summary>
        /// The NVENC -cq the next recording will use: the config file's value at
        /// startup, replaced after the overlay's quality picker saves a preset.
        /// </summary>
        /// <remarks>
        /// A field rather than a change to <see cref="Config"/> because Config is
        /// immutable and parsed once. The overlay and the capture controller both read
        /// it through <see cref="CurrentQuality"/>, so there is exactly one answer to
        /// "which -cq is in effect", and a failed save cannot leave the panel and the
        /// recorder disagreeing. Assigned in <see cref="Main"/> before the hotkey is
        /// armed.
        /// </remarks>
        private static int _quality;

        /// <summary>
        /// The frame rate the next recording will use: 60 by default, replaced after
        /// the overlay's settings panel saves a choice.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Config"/> for the same reason as
        /// <see cref="_quality"/>: Config is immutable and parsed once, while the
        /// panel may change this between captures. The capture controller reads it
        /// at every segment spawn, so a saved choice applies to the next recording
        /// without a restart.
        /// </remarks>
        private static int _fps;

        /// <summary>
        /// The frame rate the settings panel marks: the effective rate, or the raw
        /// value a hand-edited file asked for (for example 24) so it can be shown as
        /// Custom without being applied.
        /// </summary>
        /// <remarks>
        /// Assigned from <see cref="Config.RequestedFps"/> at startup and to the
        /// chosen rate after a save. Never passed to the recorder: <see cref="_fps"/>
        /// is the applied value.
        /// </remarks>
        private static int _fpsShown;

        /// <summary>
        /// The folder the next recording is written to and the tray's Open folder
        /// command opens: an absolute path, assigned from the parsed config at startup
        /// and replaced when the overlay's Save to row saves a new one.
        /// </summary>
        /// <remarks>
        /// A field rather than a read of <see cref="Config.Folder"/> because Config is
        /// immutable and parsed once, while the picker may change the folder between
        /// captures. It is the single source both paths share: the capture controller
        /// reads it through <see cref="CurrentFolder"/>, the tray and the overlay's row
        /// through <see cref="OutputFolderPath"/>, so a recording can never land in a
        /// different folder from the one the tray opens or the panel shows.
        /// </remarks>
        private static string _folder;

        /// <summary>
        /// The microphone choice in effect for the next recording: "off", "default",
        /// or an exact device name.
        /// </summary>
        /// <remarks>
        /// Starts as the parsed config's <see cref="Config.Microphone"/> and is
        /// replaced when a row in the overlay's microphone panel saves a new value.
        /// "default" is not a device: it means "ask Windows at each recording start",
        /// which is why it is kept as a choice rather than resolved once here.
        /// </remarks>
        private static string _micChoice;

        /// <summary>
        /// The default capture device named by the last successful resolution, for
        /// the overlay's tooltip; null when nothing has been resolved or the last
        /// attempt failed.
        /// </summary>
        /// <remarks>
        /// A cache for display only. The recording path resolves again at every
        /// Begin, so a device change after the overlay opened is still honoured -
        /// this field can be stale and must never be read as the recording's device.
        /// </remarks>
        private static string _micResolved;

        /// <summary>
        /// True when the next recording also captures system audio (WASAPI loopback
        /// on the default render endpoint).
        /// </summary>
        /// <remarks>
        /// Starts as the parsed config's <see cref="Config.SystemAudio"/> and is
        /// replaced when the overlay's System item toggles it. Read once per capture,
        /// at Begin, because every segment must have the same stream layout for the
        /// -c copy join - a toggle clicked mid-recording applies to the next one.
        /// </remarks>
        private static bool _systemAudio;

        /// <summary>
        /// The default render endpoint named by the last successful resolution, for
        /// the System item's tooltip; null when nothing has been resolved or the
        /// last attempt failed.
        /// </summary>
        /// <remarks>
        /// Display only, exactly like <see cref="_micResolved"/>: the recording
        /// resolves the endpoint again when the loopback client opens, so a device
        /// switched after the overlay opened is still honoured.
        /// </remarks>
        private static string _renderResolved;

        /// <summary>Tray toast channel, assigned in <see cref="Main"/> before the hotkey is armed.</summary>
        /// <remarks>
        /// The same sink the capture controller is given: a quality save failure must
        /// reach the user as one non-blocking balloon plus one log line, never a dialog
        /// (DESIGN.md section 8).
        /// </remarks>
        private static Action<string> _inform;

        /// <summary>
        /// The one capture in flight, or null before startup finishes. Owns the
        /// recorder, the stop pill and the output path.
        /// </summary>
        private static CaptureController _capture;

        /// <summary>
        /// Process entry point. Starts the monotonic clock, acquires the
        /// single-instance mutex, then runs the tray and the hotkey until Exit is
        /// chosen.
        /// </summary>
        /// <returns>
        /// 0 on a normal tray exit and also for a duplicate launch, which is a
        /// successful no-op, not an error (DESIGN.md section 9, "second instance").
        /// </returns>
        /// <remarks>
        /// Side effects: starts <see cref="_clock"/>, creates the named mutex, may
        /// create %APPDATA%\SnipVid\config.ini on first run, parses it, claims the
        /// hotkey chord (or its fallback), shows a tray icon and writes startup lines
        /// to bin\SnipVid.log. No main window is created at any point. Failure modes:
        /// an unreadable config falls back to the defaults with a note; a hotkey
        /// another program owns falls back to Ctrl+Alt+Shift+F9; both taken
        /// disarms the hotkey and leaves the app running and quittable
        /// (DESIGN.md section 9). A mutex that cannot be created surfaces as an
        /// unhandled exception - nothing sensible can be done without it.
        /// </remarks>
        [STAThread]
        private static int Main()
        {
            // First statement on purpose: this is the hotkey clock's origin, so it
            // must be as close to process start as the code can get. See _clock for
            // what happens when it is a field initializer instead.
            _clock = Stopwatch.StartNew();

            bool createdNew;
            var mutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew && !TakeOwnership(mutex))
            {
                // Another live SnipVid owns the tray and the hotkey; leave
                // silently. Re-arming here would manufacture the very
                // "already taken" conflict DESIGN.md section 9 describes.
                return 0;
            }

            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                using (var tray = new TrayIcon(app))
                {
                    tray.Show();

                    Config config = ConfigFile.Load(tray.ShowNote);
                    foreach (string note in config.Notes)
                    {
                        tray.ShowNote(note);
                    }

                    // Assigned before the hotkey is armed: a press can arrive the
                    // instant the chord is registered, and both the capture path and
                    // the tray's Open folder command read these. Creating the
                    // controller here also means a press can never see a half-built
                    // one. The quality is the one setting the overlay can change at
                    // run time, so it is held here and read through CurrentQuality by
                    // both the controller and the panel.
                    _config = config;
                    _quality = config.Quality;
                    _fps = config.Fps;
                    _fpsShown = config.RequestedFps;
                    // Resolved once here so every later read of OutputFolderPath is
                    // the same absolute path: the config default is the profile's
                    // Videos folder, never an empty string.
                    _folder = string.IsNullOrEmpty(config.Folder)
                        ? OutputFolder.ResolveDefault()
                        : config.Folder;
                    _micChoice = config.Microphone;
                    _systemAudio = config.SystemAudio;
                    _inform = tray.Inform;
                    _capture = new CaptureController(
                        config, tray.Inform, _clock, CurrentQuality, AppliedFps, MicDeviceForCapture,
                        SystemAudioEnabled, CurrentFolder);

                    // Enumerated before the hotkey is armed, so the first press
                    // already has a monitor list and its drag origin can pick one
                    // (WO-08). The lines are the C1 evidence: one line per display,
                    // in the order ddagrab's output_idx is assumed to share.
                    _monitors = Monitors.Enumerate(tray.ShowNote);
                    Monitors.Log(_monitors);

                    // The capture-device list is startup evidence: it is what lets
                    // a user see which microphone ffmpeg will be offered. The
                    // default is deliberately NOT resolved here - it is resolved
                    // when the overlay opens (for the tooltip) and again when a
                    // recording starts (so a device change in between is honoured).
                    LogAudioDevices();

                    // Disposal order matters: the hotkey is released while the
                    // tray still exists, so the release is logged as part of the
                    // run rather than during a shutdown that already tore the
                    // rest down.
                    using (HotkeyRegistration hotkey = HotkeyRegistration.Arm(config, OnHotkey, tray.ShowNote))
                    {
                        return app.Run();
                    }
                }
            }
            finally
            {
                // Release first so a relaunch can take the name even when the
                // process is torn down by an exception.
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Not owned (duplicate launch that never acquired it).
                }
                mutex.Dispose();
            }
        }

        /// <summary>
        /// Invocation handler for the hotkey: logs the press, then either stops the
        /// running capture or opens the overlay (WO-07).
        /// </summary>
        /// <param name="chord">
        /// The chord that is actually registered, so a log line is unambiguous when
        /// the fallback is in use.
        /// </param>
        /// <remarks>
        /// Side effects: one press line in bin\SnipVid.log with its monotonic
        /// timestamp, then either a stop (which logs its own transitions) or the
        /// overlay with its shown line. Runs on the UI thread, which is where WPF
        /// expects a window to be created. Never throws: <see cref="HotkeyWindow"/>
        /// guards the call, because an exception here would come out of a window
        /// message hook and take the dispatcher down.
        /// </remarks>
        private static void OnHotkey(HotkeySpec chord)
        {
            long pressMs = _clock.ElapsedMilliseconds;
            AppLog.Line(
                "hotkey: " + chord + " invoked at t+"
                + pressMs.ToString(CultureInfo.InvariantCulture) + " ms");

            // One chord, two meanings, chosen by state: while a capture is running
            // it is the stop command (DESIGN.md section 2, Recording -> Finalising),
            // and only otherwise does it open the picker. This is why the hotkey is
            // the always-available stop even though the pill never takes focus.
            if (_capture != null && _capture.IsCapturing)
            {
                _capture.StopFromHotkey();
                return;
            }

            ShowOverlay(pressMs);
        }

        /// <summary>
        /// Opens the region picker for the tray's Capture command, through the same
        /// path as the hotkey so the two cannot diverge.
        /// </summary>
        /// <remarks>
        /// Side effects: one log line and possibly one overlay, exactly as a press
        /// would produce. Refused while a capture is running; a tray click is not a
        /// stop command, because the menu item says Capture.
        /// </remarks>
        internal static void StartCaptureFromTray()
        {
            long nowMs = _clock == null ? 0L : _clock.ElapsedMilliseconds;
            AppLog.Line("tray: capture clicked at t+" + nowMs.ToString(CultureInfo.InvariantCulture) + " ms");
            if (_capture != null && _capture.IsCapturing)
            {
                AppLog.Line("capture: ignored, a recording is already in progress");
                return;
            }
            ShowOverlay(nowMs);
        }

        /// <summary>
        /// Stops a running capture before the process exits, so the trailer is
        /// written instead of relying on the job object's kill.
        /// </summary>
        /// <remarks>
        /// Side effects: may finalise a recording and write the file. Called from the
        /// tray Exit handler before the dispatcher shuts down; the job object is the
        /// backstop if this never runs.
        /// </remarks>
        internal static void PrepareForExit()
        {
            if (_capture != null)
            {
                _capture.StopForExit();
            }
        }

        /// <summary>
        /// The folder recordings are written to and the tray's Open folder command
        /// opens: the configured folder once config is loaded, the default otherwise.
        /// </summary>
        /// <remarks>
        /// Side effects: none; does not create the folder. This is the one answer to
        /// "which folder is in effect": the tray's Open folder command reads this
        /// property, the overlay's Save to row reads it through
        /// <see cref="CurrentFolder"/>, and the capture controller reads it through
        /// the same reader at every Begin - so a folder saved from the picker changes
        /// all three at once, and none of them can name a different folder from the
        /// one the file holds. <see cref="_folder"/> is assigned in <see cref="Main"/>
        /// before the hotkey is armed; the config fallback below only covers the
        /// window before that assignment.
        /// </remarks>
        internal static string OutputFolderPath
        {
            get
            {
                string folder = _folder;
                if (!string.IsNullOrEmpty(folder))
                {
                    return folder;
                }

                Config config = _config;
                if (config != null && !string.IsNullOrEmpty(config.Folder))
                {
                    return config.Folder;
                }
                return OutputFolder.ResolveDefault();
            }
        }

        /// <summary>
        /// The folder the next recording will be written to, for the overlay's Save to
        /// row and the capture controller.
        /// </summary>
        /// <returns>
        /// An absolute path; never null or empty once <see cref="Main"/> has resolved
        /// it.
        /// </returns>
        /// <remarks>
        /// Side effects: none. A separate method only because the overlay and the
        /// controller take readers; it returns <see cref="OutputFolderPath"/> rather
        /// than repeating the resolution, so there is one place a folder can be
        /// chosen from.
        /// </remarks>
        private static string CurrentFolder()
        {
            return OutputFolderPath;
        }

        /// <summary>
        /// Creates and shows the region picker, and measures how long that took.
        /// </summary>
        /// <param name="pressMs">Monotonic milliseconds at which the hotkey fired.</param>
        /// <remarks>
        /// Side effects: maybe one COM query (when the mic is on and resolves to the
        /// default), creates a full-screen topmost window, writes one "overlay:
        /// shown" line carrying the press-to-shown delta, and leaves
        /// <see cref="_overlay"/> set until the window closes.
        ///
        /// The shown line is written here rather than inside the overlay because
        /// only the caller holds the press timestamp, and C1 is a measurement of
        /// the pair. If the overlay cannot be created, the failure is logged and
        /// the app keeps running: the tray is the only way out, so no UI failure
        /// may take the process down (DESIGN.md section 9).
        ///
        /// The default microphone is resolved here so the toolbar's tooltip can
        /// name the device the user is about to record with. It is resolved AGAIN
        /// at Begin: this answer is display-only, and a device changed while the
        /// overlay is open must still be honoured by the recording.
        /// </remarks>
        private static void ShowOverlay(long pressMs)
        {
            if (_overlay != null)
            {
                AppLog.Line("overlay: ignored, one is already open");
                return;
            }
            if (_monitors == null)
            {
                AppLog.Line("overlay: ignored, monitors were not enumerated at startup");
                return;
            }

            if (MicEnabled() && string.Equals(_micChoice, Config.MicDefault, StringComparison.Ordinal))
            {
                ResolveDefaultMic("the overlay cannot name a device");
            }
            if (_systemAudio)
            {
                ResolveRenderDevice("the overlay cannot name the output device");
            }

            OverlayWindow overlay = new OverlayWindow(
                _monitors, OnCaptureReady, CurrentQuality, OnQualityChosen, CurrentFps, OnFpsChosen,
                CurrentFolder, OnFolderChosen,
                CurrentMicSetting, MicDeviceList, MicTooltip, OnMicChosen,
                SystemAudioEnabled, SystemAudioTooltip, OnSystemAudioChosen);
            overlay.Closed += OnOverlayClosed;
            _overlay = overlay;

            try
            {
                overlay.Open();
            }
            catch (Exception ex)
            {
                // A failed window build must not leave a phantom "open" flag that
                // silently swallows every later press.
                _overlay = null;
                AppLog.Line("overlay: failed " + ex.GetType().Name + ": " + ex.Message);
                return;
            }

            long shownMs = _clock.ElapsedMilliseconds;
            AppLog.Line(
                "overlay: shown at t+" + shownMs.ToString(CultureInfo.InvariantCulture)
                + " ms (press t+" + pressMs.ToString(CultureInfo.InvariantCulture)
                + " ms, delta " + (shownMs - pressMs).ToString(CultureInfo.InvariantCulture)
                + " ms)");
        }

        /// <summary>
        /// Receives the accepted capture rectangle from the overlay and starts the
        /// recording.
        /// </summary>
        /// <param name="rect">Monitor-relative physical pixels, even-floored.</param>
        /// <param name="monitor">The monitor the drag started on.</param>
        /// <remarks>
        /// Side effects: hands the rectangle to <see cref="CaptureController.Begin"/>,
        /// which resolves ffmpeg, creates the folder, picks the name, spawns the child
        /// and logs the transition. A failure (missing ffmpeg, an unusable folder) is
        /// reported as one note; the app stays in the tray.
        ///
        /// Called from the overlay's accept path, which runs after its window is
        /// closed, so nothing this spawns can record the overlay (C7 of WO-07).
        /// </remarks>
        private static void OnCaptureReady(PxRect rect, MonitorInfo monitor)
        {
            AppLog.Line(
                "capture: accepted offset_x=" + rect.X.ToString(CultureInfo.InvariantCulture)
                + " offset_y=" + rect.Y.ToString(CultureInfo.InvariantCulture)
                + " video_size=" + rect.W.ToString(CultureInfo.InvariantCulture)
                + "x" + rect.H.ToString(CultureInfo.InvariantCulture)
                + " monitor=" + monitor.Index.ToString(CultureInfo.InvariantCulture));

            CaptureController capture = _capture;
            if (capture == null)
            {
                AppLog.Line("capture: ignored, the capture controller was not created");
                return;
            }
            capture.Begin(rect, monitor);
        }

        /// <summary>
        /// Clears the overlay reference once its window is gone.
        /// </summary>
        /// <param name="sender">The overlay that closed (unused).</param>
        /// <param name="e">Event arguments (unused).</param>
        /// <remarks>
        /// Side effects: allows the next hotkey press to open a fresh overlay. The
        /// overlay logs its own closed line, so nothing is written here.
        /// </remarks>
        private static void OnOverlayClosed(object sender, EventArgs e)
        {
            _overlay = null;
        }

        /// <summary>
        /// The -cq the next recording would use.
        /// </summary>
        /// <returns>The current quality, 1-51, or 0 before <see cref="Main"/> assigned it.</returns>
        /// <remarks>
        /// Side effects: none. Read by the overlay's quality panel to mark the preset
        /// in effect and by <see cref="CaptureController"/> at every segment spawn, so a
        /// picker change reaches the next capture without a restart.
        /// </remarks>
        private static int CurrentQuality()
        {
            return _quality;
        }

        /// <summary>
        /// The frame rate the next recording will use.
        /// </summary>
        /// <returns>60 or 30; 0 only before <see cref="Main"/> assigned it.</returns>
        /// <remarks>
        /// Side effects: none. Read by <see cref="CaptureController"/> at every
        /// segment spawn. Deliberately not <see cref="CurrentFps"/>: the panel's
        /// reader may return a rejected value the file asked for, and a Custom value
        /// must never reach the recorder.
        /// </remarks>
        private static int AppliedFps()
        {
            return _fps;
        }

        /// <summary>
        /// The frame rate the settings panel should mark.
        /// </summary>
        /// <returns>
        /// 60 or 30 when one is in effect, or a rejected value the file requested
        /// (for example 24) so the panel can show it as Custom; 0 before
        /// <see cref="Main"/> assigned it.
        /// </returns>
        /// <remarks>
        /// Side effects: none. Read only by the overlay's settings panel. The
        /// recorder reads <see cref="AppliedFps"/>, so a Custom value is displayed
        /// but never applied.
        /// </remarks>
        private static int CurrentFps()
        {
            return _fpsShown;
        }

        /// <summary>
        /// Whether the next recording will have microphone audio.
        /// </summary>
        /// <returns>True unless the choice is <see cref="Config.MicOff"/>.</returns>
        /// <remarks>
        /// Side effects: none. Read by the overlay to draw the mic item and by the
        /// recording path before it resolves anything, so "off" costs no COM call.
        /// </remarks>
        private static bool MicEnabled()
        {
            return !string.Equals(_micChoice, Config.MicOff, StringComparison.Ordinal);
        }

        /// <summary>
        /// The microphone setting in effect for the next recording, for the overlay's
        /// panel to mark.
        /// </summary>
        /// <returns>
        /// <see cref="Config.MicOff"/>, <see cref="Config.MicDefault"/>, or the
        /// exact device name from the file.
        /// </returns>
        /// <remarks>Side effects: none. Display only; the recorder uses <see cref="MicDeviceForCapture"/>.</remarks>
        private static string CurrentMicSetting()
        {
            return _micChoice;
        }

        /// <summary>
        /// The active capture endpoints, for the overlay's microphone panel.
        /// </summary>
        /// <returns>A list, possibly empty; never null.</returns>
        /// <remarks>
        /// Side effects: one COM enumeration, plus one log line when it fails.
        /// Called when the panel opens, not at startup, because a device plugged in
        /// during the session must appear in the list the user sees.
        /// </remarks>
        private static List<string> MicDeviceList()
        {
            string detail;
            List<string> names = AudioDevices.CaptureNames(out detail);
            if (detail != null)
            {
                AppLog.Line("audio: capture device list unavailable: " + detail);
            }
            return names;
        }

        /// <summary>
        /// The microphone device name the next recording should use, resolved now.
        /// </summary>
        /// <returns>
        /// A device name, or null when the recording must proceed without audio.
        /// </returns>
        /// <remarks>
        /// Side effects: may query Core Audio (one or two COM calls) and writes one
        /// log line when the default is resolved, when it cannot be, or when a
        /// configured device has gone. Called by the capture controller at Begin, once
        /// per recording - never per segment, because a segment boundary must not be
        /// able to change the audio stream a -c copy join depends on.
        ///
        /// A configured device that Core Audio no longer lists (unplugged, renamed,
        /// disabled) falls back to the default with one line naming it. A missing
        /// microphone must never abort a recording, so this path degrades to the
        /// default and then to silence, never to a refusal. If the enumeration itself
        /// fails there is no way to know whether the device is present, so the
        /// configured name is passed through and dshow gets the final say.
        /// </remarks>
        private static string MicDeviceForCapture()
        {
            if (!MicEnabled())
            {
                return null;
            }

            string choice = _micChoice;
            if (string.Equals(choice, Config.MicDefault, StringComparison.Ordinal))
            {
                return ResolveDefaultMic("recording without audio");
            }

            string detail;
            List<string> devices = AudioDevices.CaptureNames(out detail);
            if (detail == null && !ContainsDevice(devices, choice))
            {
                AppLog.Line(
                    "audio: microphone '" + choice + "' is not present; using the default device instead");
                return ResolveDefaultMic("recording without audio");
            }
            return choice;
        }

        /// <summary>True when the list contains the device name, ignoring case.</summary>
        /// <param name="devices">Active capture endpoints; may be null.</param>
        /// <param name="name">Configured device name; may be null.</param>
        /// <returns>True when one entry names the same endpoint.</returns>
        /// <remarks>
        /// Side effects: none. Case-insensitive because dshow matches device names
        /// that way and a hand-edited file may use different capitalisation.
        /// </remarks>
        private static bool ContainsDevice(List<string> devices, string name)
        {
            if (devices == null || string.IsNullOrEmpty(name))
            {
                return false;
            }
            for (int i = 0; i < devices.Count; i++)
            {
                if (string.Equals(devices[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Asks Windows for the default capture endpoint and remembers the name.
        /// </summary>
        /// <param name="context">
        /// What happens next, appended to the one log line written on failure, for
        /// example "recording without audio".
        /// </param>
        /// <returns>The device name, or null when there is none.</returns>
        /// <remarks>
        /// Side effects: one COM query, one log line (success or failure), and
        /// <see cref="_micResolved"/> is updated so the overlay tooltip agrees with
        /// the latest answer. Never throws: <see cref="AudioDevices"/> is total, and
        /// a missing microphone must never prevent a recording (C7).
        /// </remarks>
        private static string ResolveDefaultMic(string context)
        {
            string detail;
            string name = AudioDevices.DefaultCaptureName(out detail);
            if (name == null)
            {
                _micResolved = null;
                AppLog.Line("audio: default capture device not available (" + detail + "); " + context);
                return null;
            }

            _micResolved = name;
            AppLog.Line("audio: default capture device=" + name);
            return name;
        }

        /// <summary>
        /// The overlay's microphone tooltip: the device in effect, or why there is none.
        /// </summary>
        /// <returns>One line of display text; never null or empty.</returns>
        /// <remarks>
        /// Side effects: none. Reads <see cref="_micResolved"/>, which is a display
        /// cache updated by every resolution attempt - it is never the device the
        /// recorder uses, because Begin resolves again. The device name is returned
        /// bare rather than prefixed with "Microphone:": real names often start with
        /// that word themselves ("Microphone (Realtek(R) Audio)").
        /// </remarks>
        private static string MicTooltip()
        {
            if (!MicEnabled())
            {
                return "Microphone off (mic=off)";
            }

            string choice = _micChoice;
            if (!string.Equals(choice, Config.MicDefault, StringComparison.Ordinal))
            {
                return choice;
            }
            return _micResolved == null ? "Microphone not found" : _micResolved;
        }

        /// <summary>
        /// Applies a choice from the overlay's microphone panel: saves it, then adopts
        /// it for the next recording.
        /// </summary>
        /// <param name="value">
        /// <see cref="Config.MicOff"/>, <see cref="Config.MicDefault"/>, or an exact
        /// device name from the panel.
        /// </param>
        /// <remarks>
        /// Side effects: rewrites the config file's <c>mic</c> value through
        /// <see cref="ConfigWriter"/> (every other byte survives), updates
        /// <see cref="_micChoice"/> on success, resolves the default device when that
        /// row was chosen (so the tooltip names it immediately) and writes one log
        /// line. A failed write logs <c>mic: could not save config &lt;detail&gt;</c>
        /// and shows one balloon, and deliberately leaves the choice alone: the
        /// overlay re-reads it and redraws the item, so the toolbar cannot claim a
        /// setting the file does not hold.
        ///
        /// The value is written verbatim. A device name is not validated here for the
        /// same reason <c>ffmpeg_path</c> is not: only Windows' enumeration and dshow
        /// can say whether a device exists, and the spawn path already falls back to
        /// the default when it does not.
        /// </remarks>
        private static void OnMicChosen(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                AppLog.Line("mic: ignored, the chosen value was empty");
                return;
            }

            string detail;
            if (!TrySaveRecordValue("mic", value, out detail))
            {
                AppLog.Line("mic: could not save config " + detail);
                Inform("Could not save the microphone setting: " + detail);
                return;
            }

            _micChoice = value;
            if (string.Equals(value, Config.MicDefault, StringComparison.Ordinal))
            {
                // Name the device now rather than at the next overlay open, so the
                // tooltip can say what the Default row will actually record with.
                ResolveDefaultMic("the overlay cannot name a device");
            }
            AppLog.Line(
                "mic: set mic=" + value
                + " (audio " + (MicEnabled() ? "on" : "off") + "), config saved");
        }

        /// <summary>
        /// Whether the next recording will capture system audio.
        /// </summary>
        /// <returns>True when the System item is on, or before startup assigned it.</returns>
        /// <remarks>
        /// Side effects: none. Read by the overlay to draw the System item and by the
        /// capture controller once per capture, so "off" costs no COM call and no
        /// pipe. Unlike the microphone there is no device to resolve: loopback always
        /// follows the default render endpoint, which is the point of the feature.
        /// </remarks>
        private static bool SystemAudioEnabled()
        {
            return _systemAudio;
        }

        /// <summary>
        /// Asks Windows for the default render endpoint and remembers the name.
        /// </summary>
        /// <param name="context">
        /// What happens next, appended to the one log line written on failure, for
        /// example "the overlay cannot name the output device".
        /// </param>
        /// <returns>The device name, or null when there is none.</returns>
        /// <remarks>
        /// Side effects: one COM query and one log line.
        /// <see cref="_renderResolved"/> is updated so the tooltip agrees with the
        /// latest answer. Never throws: <see cref="LoopbackCapture"/> is total, and a
        /// missing output device must never prevent a recording.
        /// </remarks>
        private static string ResolveRenderDevice(string context)
        {
            string detail;
            string name = LoopbackCapture.DefaultRenderName(out detail);
            if (name == null)
            {
                _renderResolved = null;
                AppLog.Line("audio: default render device not available (" + detail + "); " + context);
                return null;
            }

            _renderResolved = name;
            AppLog.Line("audio: default render device=" + name);
            return name;
        }

        /// <summary>
        /// The overlay's System item tooltip: the endpoint being captured, or why
        /// there is none.
        /// </summary>
        /// <returns>One line of display text; never null or empty.</returns>
        /// <remarks>
        /// Side effects: none. Reads <see cref="_renderResolved"/>, which is a display
        /// cache updated by every resolution attempt - never the endpoint the
        /// recorder uses, because the loopback client resolves again when it opens.
        ///
        /// The second sentence is the documented limitation, in the place the user
        /// will see it before recording: loopback binds to the endpoint when the
        /// stream starts, so switching output devices mid-recording silences the rest
        /// of that segment. Re-binding is deliberately not attempted (DESIGN.md
        /// section 5d).
        /// </remarks>
        private static string SystemAudioTooltip()
        {
            if (!_systemAudio)
            {
                return "System audio off (system_audio=off)";
            }

            string device = _renderResolved == null
                ? "System audio (no default output device found)"
                : "System audio: " + _renderResolved;
            return device + " - switching the output device during a recording leaves that segment silent";
        }

        /// <summary>
        /// Applies the overlay's System toggle: saves it, then adopts it for the next
        /// recording.
        /// </summary>
        /// <param name="enabled">True to capture system audio, false to stop.</param>
        /// <remarks>
        /// Side effects: rewrites the config file's <c>system_audio</c> value through
        /// <see cref="ConfigWriter"/> (every other byte survives), updates
        /// <see cref="_systemAudio"/> on success, resolves the render endpoint when
        /// turning on (so the tooltip names it immediately) and writes one log line.
        /// A failed write logs <c>system_audio: could not save config &lt;detail&gt;</c>
        /// and shows one balloon, and deliberately leaves the choice alone: the
        /// overlay is redrawn from this state, so the item cannot claim a setting the
        /// file does not hold. A running capture is not disturbed: the controller
        /// fixed its layout at Begin, which is what the -c copy join requires.
        /// </remarks>
        private static void OnSystemAudioChosen(bool enabled)
        {
            string value = enabled ? Config.SystemAudioOn : Config.SystemAudioOff;
            string detail;
            if (!TrySaveRecordValue("system_audio", value, out detail))
            {
                AppLog.Line("system_audio: could not save config " + detail);
                Inform("Could not save the system audio setting: " + detail);
                return;
            }

            _systemAudio = enabled;
            if (enabled)
            {
                ResolveRenderDevice("the overlay cannot name the output device");
            }
            AppLog.Line("system_audio: set system_audio=" + value
                + " (system audio " + (enabled ? "on" : "off") + "), config saved");
        }

        /// <summary>
        /// Records the capture endpoints Windows currently offers.
        /// </summary>
        /// <remarks>
        /// Side effects: one COM enumeration and one log line per device plus a
        /// count. Startup evidence only - it is not used to validate a configured
        /// name, and the default is resolved separately because a device plugged in
        /// after launch must still be usable.
        /// </remarks>
        private static void LogAudioDevices()
        {
            string detail;
            List<string> names = AudioDevices.CaptureNames(out detail);
            for (int i = 0; i < names.Count; i++)
            {
                AppLog.Line("audio: capture device[" + i.ToString(CultureInfo.InvariantCulture) + "]=" + names[i]);
            }
            AppLog.Line(
                "audio: capture devices=" + names.Count.ToString(CultureInfo.InvariantCulture)
                + " mic=" + _micChoice
                + " systemAudio=" + (_systemAudio ? "on" : "off")
                + (detail == null ? string.Empty : " listError=" + detail));
        }

        /// <summary>
        /// Surfaces one message through the tray's informational channel.
        /// </summary>
        /// <param name="message">Text to show; the sink ignores null and empty.</param>
        /// <remarks>
        /// Side effects: one log line and one balloon tip when a sink was supplied. A
        /// missing sink is a no-op rather than an error: the log line has already been
        /// written by the caller, and a toast is a courtesy, not the record.
        /// </remarks>
        private static void Inform(string message)
        {
            Action<string> inform = _inform;
            if (inform != null)
            {
                inform(message);
            }
        }

        /// <summary>
        /// Applies a preset chosen in the overlay: writes it to config.ini, then adopts
        /// it as the value the next recording uses.
        /// </summary>
        /// <param name="cq">The chosen NVENC -cq; the panel only sends 19, 22 or 25.</param>
        /// <param name="preset">The preset's name, for the log line.</param>
        /// <remarks>
        /// Side effects: rewrites the config file's <c>quality</c> value (through
        /// <see cref="ConfigWriter"/>, so every other byte of the file survives),
        /// updates <see cref="_quality"/> on success, and writes one log line. A value
        /// outside 1-51 is refused before touching the file. A failed write logs
        /// <c>quality: could not save config &lt;detail&gt;</c> and shows one tray
        /// balloon, and deliberately leaves <see cref="_quality"/> alone: the setting
        /// only takes effect once the file that will be read at the next launch
        /// agrees, so the panel can never show a preset the file does not hold.
        /// Nothing restarts: the next capture reads the new value and a running one is
        /// not interrupted.
        /// </remarks>
        private static void OnQualityChosen(int cq, string preset)
        {
            string label = string.IsNullOrEmpty(preset) ? "Custom" : preset;
            if (cq < 1 || cq > 51)
            {
                AppLog.Line(
                    "quality: ignored cq=" + cq.ToString(CultureInfo.InvariantCulture)
                    + ", must be 1-51");
                return;
            }

            string detail;
            if (!TrySaveQuality(cq, out detail))
            {
                AppLog.Line("quality: could not save config " + detail);
                Inform("Could not save the quality setting (" + label + "): " + detail);
                return;
            }

            _quality = cq;
            AppLog.Line(
                "quality: set cq=" + cq.ToString(CultureInfo.InvariantCulture)
                + " (" + label + "), config saved");
        }

        /// <summary>
        /// Applies a frame rate chosen in the overlay: writes it to config.ini, then
        /// adopts it as the value the next recording uses.
        /// </summary>
        /// <param name="fps">The chosen rate; the panel only sends 60 or 30.</param>
        /// <param name="label">The row's label, for the log line.</param>
        /// <remarks>
        /// Side effects: rewrites the config file's <c>fps</c> value (through
        /// <see cref="ConfigWriter"/>, so every other byte survives), updates
        /// <see cref="_fps"/> and <see cref="_fpsShown"/> on success, and writes one
        /// log line. An unsupported rate is refused before touching the file. A
        /// failed write logs <c>fps: could not save config &lt;detail&gt;</c> and
        /// shows one tray balloon, and deliberately leaves both fields alone: the
        /// setting takes effect only once the file that will be read at the next
        /// launch agrees, so the panel cannot show a rate the file does not hold.
        /// Nothing restarts: the next capture reads the new value and a running one
        /// is not interrupted.
        /// </remarks>
        private static void OnFpsChosen(int fps, string label)
        {
            string name = string.IsNullOrEmpty(label) ? "Custom" : label;
            if (!FfmpegArgs.IsSupportedFps(fps))
            {
                AppLog.Line(
                    "fps: ignored fps=" + fps.ToString(CultureInfo.InvariantCulture)
                    + ", must be 60 or 30");
                return;
            }

            string detail;
            if (!TrySaveFps(fps, out detail))
            {
                AppLog.Line("fps: could not save config " + detail);
                Inform("Could not save the frame rate setting (" + name + "): " + detail);
                return;
            }

            _fps = fps;
            _fpsShown = fps;
            AppLog.Line(
                "fps: set fps=" + fps.ToString(CultureInfo.InvariantCulture)
                + " (" + name + "), config saved");
        }

        /// <summary>
        /// Applies a folder chosen in the overlay's Save to row: writes it to
        /// config.ini, then adopts it for the next recording.
        /// </summary>
        /// <param name="folder">Absolute folder path chosen in the dialog.</param>
        /// <remarks>
        /// Side effects: rewrites the config file's <c>folder</c> value through
        /// <see cref="ConfigWriter"/> (every other byte survives), updates
        /// <see cref="_folder"/> on success - which is what moves the tray's Open folder
        /// command and the next capture to the new folder - and writes one log line.
        ///
        /// A value that is not an absolute path is refused before touching the file:
        /// the recorder resolves a relative path against its own working directory, so
        /// writing one would make the key mean something different from the path the
        /// picker returned. Existence is not required, because the capture path creates
        /// the folder at Begin; writability is deliberately not checked here either, so
        /// a folder on a disconnected drive is reported when recording rather than
        /// silently replaced by the default.
        ///
        /// A failed write logs <c>folder: could not save config &lt;detail&gt;</c> and
        /// shows one balloon, and leaves <see cref="_folder"/> alone: the overlay's row
        /// is redrawn from this state, so it cannot show a folder the file does not
        /// hold.
        /// </remarks>
        private static void OnFolderChosen(string folder)
        {
            if (string.IsNullOrEmpty(folder))
            {
                AppLog.Line("folder: ignored, the chosen path was empty");
                return;
            }
            if (!IsAbsolutePath(folder))
            {
                AppLog.Line("folder: ignored, the chosen path is not absolute: " + folder);
                return;
            }

            string detail;
            if (!TrySaveRecordValue("folder", folder, out detail))
            {
                AppLog.Line("folder: could not save config " + detail);
                Inform("Could not save the output folder: " + detail);
                return;
            }

            _folder = folder;
            AppLog.Line("folder: set folder=" + folder + ", config saved");
        }

        /// <summary>
        /// True when a path is rooted and free of characters Windows cannot put in a
        /// path.
        /// </summary>
        /// <param name="path">Candidate path; null and empty report false.</param>
        /// <returns>True when the path can be used without a working directory.</returns>
        /// <remarks>
        /// Side effects: none. <see cref="Path.IsPathRooted"/> alone would accept a
        /// path with an embedded NUL, which the config writer would happily store and
        /// the recorder would reject at Begin; the invalid-character check keeps a
        /// broken value out of the file instead.
        /// </remarks>
        private static bool IsAbsolutePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }
            try
            {
                return Path.IsPathRooted(path)
                    && path.IndexOfAny(Path.GetInvalidPathChars()) < 0;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>
        /// Rewrites the <c>quality</c> value in config.ini and nothing else.
        /// </summary>
        /// <param name="cq">Value to write; already range-checked by the caller.</param>
        /// <param name="detail">Failure description when the write did not happen; null on success.</param>
        /// <returns>True when the file now holds <paramref name="cq"/>.</returns>
        /// <remarks>
        /// Side effects: reads and then overwrites %APPDATA%\SnipVid\config.ini.
        /// Never throws: every failure comes back as false plus a one-line detail,
        /// because this runs from a mouse handler and a tray utility must not die
        /// for a config file. The writer keeps the file's own spelling, spacing,
        /// comments, line endings and byte-order mark (that is its whole contract).
        /// </remarks>
        private static bool TrySaveQuality(int cq, out string detail)
        {
            return TrySaveRecordValue("quality", cq.ToString(CultureInfo.InvariantCulture), out detail);
        }

        /// <summary>
        /// Rewrites the <c>fps</c> value in config.ini and nothing else.
        /// </summary>
        /// <param name="fps">Value to write; already checked against the supported set.</param>
        /// <param name="detail">Failure description when the write did not happen; null on success.</param>
        /// <returns>True when the file now holds <paramref name="fps"/>.</returns>
        /// <remarks>
        /// Side effects and failure modes are identical to <see cref="TrySaveQuality"/>:
        /// one value is replaced, every other byte survives, and no failure throws.
        /// </remarks>
        private static bool TrySaveFps(int fps, out string detail)
        {
            return TrySaveRecordValue("fps", fps.ToString(CultureInfo.InvariantCulture), out detail);
        }

        /// <summary>
        /// Rewrites one value under <c>[record]</c> in config.ini.
        /// </summary>
        /// <param name="key">Key name, for example "quality" or "fps".</param>
        /// <param name="value">Value text, written verbatim.</param>
        /// <param name="detail">Failure description when the write did not happen; null on success.</param>
        /// <returns>True when the file was read, edited and written.</returns>
        /// <remarks>
        /// Side effects: reads and overwrites %APPDATA%\SnipVid\config.ini. Never
        /// throws: every read, decode or write failure is reported as false plus a
        /// one-line detail, because a settings click must not take the tray down.
        /// </remarks>
        private static bool TrySaveRecordValue(string key, string value, out string detail)
        {
            detail = null;
            try
            {
                string path = ConfigFile.Location();
                string text = ConfigFile.ReadText(path);
                string updated = ConfigWriter.SetValue(text, "record", key, value);
                ConfigFile.WriteText(path, updated);
                return true;
            }
            catch (Exception ex)
            {
                // Deliberately broad: a save is a courtesy, and no failure mode of a
                // file read/write may turn a preset click into a crashed tray app.
                detail = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Takes ownership of the mutex when an earlier process died without
        /// releasing it.
        /// </summary>
        /// <param name="mutex">A mutex that this process did not create.</param>
        /// <returns>True when the caller now owns <paramref name="mutex"/>.</returns>
        /// <remarks>
        /// Side effects: may acquire the mutex. An abandoned mutex raises
        /// <see cref="AbandonedMutexException"/> and ownership passes to us, which
        /// is exactly what we want - the crash must not lock the user out.
        /// </remarks>
        private static bool TakeOwnership(Mutex mutex)
        {
            try
            {
                return mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// One capture from accepted rectangle to one file on disk: the Idle ->
    /// Starting -> Recording -> (Pausing -> Paused -> Starting ...) -> Finalising
    /// state machine in DESIGN.md section 2.
    /// </summary>
    /// <remarks>
    /// WHY this is a separate type from Program: the state transitions, the
    /// session, the failure messages and the pill's lifetime are one coherent job,
    /// and Program already owns startup, the tray and the hotkey.
    ///
    /// Pause is a segment boundary, not a freeze (decision D4): <see cref="Pause"/>
    /// finalises the current segment with "q", <see cref="Resume"/> starts the next
    /// one with the identical argv, and <see cref="Stop"/> joins them into the one
    /// file the user asked for. Nothing about the capture command changes per
    /// segment - the session owns names, order and the join, and Pause/Resume only
    /// decide *when* a segment ends and another begins.
    ///
    /// The one-process rule is enforced here rather than hoped for: a segment is
    /// spawned only after the previous child has exited (Pause blocks in
    /// <see cref="Recorder.Stop"/> until it has), and the join runs only after the
    /// last segment has been added. Pause is legal only from Recording, Resume only
    /// from Paused, and any other press is logged and dropped - never queued - so a
    /// double click cannot produce two children (DESIGN.md section 9, WO-11 C6).
    ///
    /// Threading: every method here runs on the UI thread. <see cref="Recorder"/>
    /// raises its events on background threads, and both handlers marshal through
    /// the dispatcher before touching state, so the state field needs no lock.
    /// </remarks>
    internal sealed class CaptureController
    {
        /// <summary>The states DESIGN.md section 2 defines.</summary>
        /// <remarks>
        /// Starting exists because ffmpeg needs a few hundred milliseconds before
        /// the first frame lands; without it the dim overlay would be in the file.
        /// It runs once per segment, so a resume goes through it too. Pausing is the
        /// mirror image: "q" needs ~300 ms to write the trailer and the UI must not
        /// claim Paused while the child is still alive. Finalising exists so a
        /// second stop request while the join or a trailer is in progress is ignored
        /// instead of killing something twice.
        /// </remarks>
        private enum CaptureState
        {
            /// <summary>No capture running; the hotkey opens the overlay.</summary>
            Idle,

            /// <summary>A segment is spawned, no frame reported yet; the pill is not shown.</summary>
            Starting,

            /// <summary>A frame landed; the pill is visible and the timer runs.</summary>
            Recording,

            /// <summary>Pause requested; the current segment's trailer is being written.</summary>
            Pausing,

            /// <summary>No child; the session is open and the timer is frozen.</summary>
            Paused,

            /// <summary>Stop requested; the last segment is ended and the join runs.</summary>
            Finalising
        }

        /// <summary>Config values the capture needs: folder, fps and ffmpeg_path.</summary>
        private readonly Config _config;

        /// <summary>
        /// Reads the -cq to record with; null falls back to the parsed config's value.
        /// </summary>
        /// <remarks>
        /// A reader rather than a copied value because the overlay's quality picker can
        /// change it between captures. Reading it at each spawn is what makes a saved
        /// preset apply to the next recording without restarting anything, and it
        /// leaves the segment argv identical across a pause/resume unless the user
        /// really did change the setting in between.
        /// </remarks>
        private readonly Func<int> _quality;

        /// <summary>
        /// Reads the frame rate to record with; null falls back to the parsed config's
        /// value.
        /// </summary>
        /// <remarks>
        /// A reader for the same reason <see cref="_quality"/> is one: a choice saved
        /// in the overlay applies to the next segment spawn without a restart, and the
        /// segment argv stays identical across a pause/resume unless the user really
        /// changed the setting in between. The value is always one the argv builder
        /// accepts, because the panel and the parser share its supported set.
        /// </remarks>
        private readonly Func<int> _fps;

        /// <summary>
        /// Reads the folder the capture is written to; null falls back to the parsed
        /// config's folder, or to the default when that is empty.
        /// </summary>
        /// <remarks>
        /// A reader rather than a copied value for the same reason
        /// <see cref="_quality"/> is one: the overlay's Save to row can change the
        /// folder between captures, and reading it at Begin is what makes that choice
        /// apply without a restart. <see cref="Begin"/> stores the answer for the whole
        /// capture, so every segment of one recording is written to the same folder
        /// even if the setting changes mid-recording.
        /// </remarks>
        private readonly Func<string> _folder;

        /// <summary>Information channel for a saved path or an actionable failure.</summary>
        private readonly Action<string> _inform;

        /// <summary>
        /// Resolves the microphone device name for a capture; null means no audio.
        /// </summary>
        /// <remarks>
        /// Called once per capture, at <see cref="Begin"/>, not per segment: the
        /// join with -c copy requires every segment to have identical streams, so a
        /// device change in the middle of a recording must not be able to add or
        /// remove the audio stream. May be null, in which case every capture is
        /// video only.
        /// </remarks>
        private readonly Func<string> _microphone;

        /// <summary>
        /// Reads whether the next capture should include system audio (WASAPI
        /// loopback). May be null, in which case every capture is without it.
        /// </summary>
        /// <remarks>
        /// Called once per capture, at <see cref="Begin"/>, not per segment: the join
        /// is -c copy, so every segment must have the same stream layout, and a
        /// toggle clicked mid-recording must apply to the next recording only.
        /// </remarks>
        private readonly Func<bool> _systemAudio;

        /// <summary>Process-wide monotonic clock, shared with the hotkey timestamp log.</summary>
        private readonly Stopwatch _clock;

        /// <summary>The current state; UI thread only.</summary>
        private CaptureState _state = CaptureState.Idle;

        /// <summary>The running segment's child, or null when idle or paused.</summary>
        private Recorder _recorder;

        /// <summary>The visible pill, or null before the first frame / after a stop.</summary>
        private StopPill _pill;

        /// <summary>The open session, or null when idle.</summary>
        /// <remarks>
        /// Owns the scratch directory, the segment order and the join. Kept for the
        /// whole capture, including across pauses - that is what makes two pauses
        /// and twenty pauses the same code path.
        /// </remarks>
        private SessionRunner _session;

        /// <summary>The resolved ffmpeg path, resolved once per capture.</summary>
        /// <remarks>
        /// Resolved at Begin and reused for every segment and for the join, so a
        /// resume cannot silently pick a different binary than the first segment.
        /// </remarks>
        private string _ffmpeg;

        /// <summary>The accepted region, reused unchanged for every segment (D4).</summary>
        private PxRect _region;

        /// <summary>The monitor the drag started on; its index becomes output_idx.</summary>
        private MonitorInfo _monitor;

        /// <summary>The configured output folder, created at Begin.</summary>
        private string _outputFolder;

        /// <summary>
        /// The microphone resolved for this capture, or null when it records no
        /// audio. Fixed at <see cref="Begin"/> and reused by every segment.
        /// </summary>
        private string _micDevice;

        /// <summary>
        /// True when this capture includes system audio, fixed at <see cref="Begin"/>.
        /// </summary>
        /// <remarks>
        /// Fixed for the whole capture like <see cref="_micDevice"/>: a pause cannot
        /// add or remove the audio stream, because the join is -c copy.
        /// </remarks>
        private bool _systemAudioEnabled;

        /// <summary>
        /// The current segment's loopback writer and pipe, or null when this segment
        /// records no system audio.
        /// </summary>
        private LoopbackCapture _loopback;

        /// <summary>
        /// True once the current segment's loopback writer was started. It starts
        /// with the segment (see <see cref="SpawnSegment"/>), so the pipe always has
        /// a writer and ffmpeg can reach its first frame; a loopback that was never
        /// started is disposed without draining.
        /// </summary>
        private bool _loopbackStarted;

        /// <summary>The current segment's pipe path, for the argv and the log.</summary>
        private string _audioPipe;

        /// <summary>The current segment's path inside the scratch directory.</summary>
        private string _segmentPath;

        /// <summary>Capture start time: the output name is derived from it, not from the finish.</summary>
        private DateTime _startTime;

        /// <summary>Monotonic milliseconds at spawn, for the spawn-to-first-frame delta.</summary>
        private long _spawnMs;

        /// <summary>
        /// Set on the UI thread before asking a child to end, read by the recorder's
        /// exit handler on a background thread.
        /// </summary>
        /// <remarks>
        /// A pause ends a child exactly like a stop does, so a single flag covers
        /// both: without it, the exit event of a paused segment would be reported as
        /// a crash and finalise a capture the user only paused.
        /// </remarks>
        private volatile bool _exitRequested;

        /// <summary>Builds a controller; <see cref="Begin"/> starts captures.</summary>
        /// <param name="config">Parsed config; not null. Used for folder, fps and ffmpeg_path.</param>
        /// <param name="inform">Toast sink for the saved path and for actionable failures; may be null.</param>
        /// <param name="clock">Process-wide monotonic clock; must not be null.</param>
        /// <param name="quality">
        /// Reads the -cq to record with, so a preset saved by the overlay applies to the
        /// next capture. May be null, in which case the parsed config's value is used.
        /// </param>
        /// <param name="fps">
        /// Reads the frame rate to record with, so a rate chosen in the overlay applies
        /// to the next capture. May be null, in which case the parsed config's value is
        /// used.
        /// </param>
        /// <param name="microphone">
        /// Resolves the microphone device name at Begin, or null for no audio. May be
        /// null, in which case every capture is video only. Called once per capture,
        /// never per segment, so a pause cannot change the stream layout.
        /// </param>
        /// <param name="systemAudio">
        /// Reads whether the next capture includes system audio. May be null, in which
        /// case every capture is without it. Called once per capture, never per
        /// segment, for the same reason as <paramref name="microphone"/>.
        /// </param>
        /// <param name="folder">
        /// Reads the folder the capture is written to, so a folder saved by the overlay
        /// applies to the next recording. May be null, in which case the parsed config's
        /// folder is used.
        /// </param>
        /// <remarks>Side effects: none. No child, window or file exists until Begin.</remarks>
        internal CaptureController(
            Config config, Action<string> inform, Stopwatch clock, Func<int> quality, Func<int> fps,
            Func<string> microphone, Func<bool> systemAudio, Func<string> folder)
        {
            _config = config;
            _inform = inform;
            _clock = clock;
            _quality = quality;
            _fps = fps;
            _microphone = microphone;
            _systemAudio = systemAudio;
            _folder = folder;
        }

        /// <summary>True when a capture is starting, recording or finalising.</summary>
        /// <remarks>
        /// Read by the hotkey handler to choose between "stop" and "open the
        /// picker". UI thread only, like everything else here.
        /// </remarks>
        internal bool IsCapturing
        {
            get { return _state != CaptureState.Idle; }
        }

        /// <summary>
        /// Starts a capture for an accepted rectangle: resolves ffmpeg, ensures the
        /// output folder, opens the session and spawns segment 1.
        /// </summary>
        /// <param name="region">Monitor-relative physical pixels from the overlay.</param>
        /// <param name="monitor">The monitor the drag started on; its index becomes output_idx.</param>
        /// <remarks>
        /// Side effects, in order: resolves ffmpeg, creates the output folder and the
        /// session's scratch directory, logs the command line, spawns the child
        /// (which creates segment 1), and leaves the controller in Starting until the
        /// first frame arrives. Every failure is one note - missing ffmpeg, an
        /// unwritable folder or scratch root, a spawn error - and leaves the app idle
        /// and alive with nothing spawned. Never throws: this runs from a mouse
        /// handler and a failed capture must not take the tray down.
        ///
        /// The output file's name is deliberately not chosen here: it is derived from
        /// <see cref="_startTime"/> when the capture ends, so a collision that
        /// appears during a long recording is still avoided (PLAN AC7).
        /// </remarks>
        internal void Begin(PxRect region, MonitorInfo monitor)
        {
            if (_state != CaptureState.Idle)
            {
                AppLog.Line("record: refused, already in state " + _state);
                return;
            }
            if (_config == null)
            {
                AppLog.Line("record: refused, config was not loaded");
                return;
            }

            string ffmpeg;
            string resolveMessage;
            string pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!FfmpegResolver.TryResolve(
                    _config.FfmpegPath, AppDomain.CurrentDomain.BaseDirectory, pathVariable, localAppData,
                    File.Exists, out ffmpeg, out resolveMessage))
            {
                // One message, one log line, no spawn, no retry (DESIGN.md section 9).
                AppLog.Line("record: aborted, " + resolveMessage);
                Inform(resolveMessage);
                return;
            }
            AppLog.Line(
                "record: ffmpeg=" + ffmpeg
                + " (ffmpeg_path=" + (_config.FfmpegPath.Length == 0 ? "(auto)" : _config.FfmpegPath) + ")");

            string folder = OutputFolderPath();
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (IOException ex)
            {
                Inform("Could not create the output folder '" + folder + "': " + ex.Message);
                AppLog.Line("record: aborted, output folder failed: " + ex.Message);
                return;
            }
            catch (UnauthorizedAccessException ex)
            {
                Inform("Could not create the output folder '" + folder + "': " + ex.Message);
                AppLog.Line("record: aborted, output folder failed: " + ex.Message);
                return;
            }
            catch (ArgumentException ex)
            {
                Inform("The output folder '" + folder + "' is not a usable path: " + ex.Message);
                AppLog.Line("record: aborted, output folder failed: " + ex.Message);
                return;
            }

            // The session, before the first segment: a segment with nowhere to live
            // must be a refused capture, not a file in the user's Videos folder.
            DateTime start = DateTime.Now;
            SessionRunner session;
            try
            {
                session = new SessionRunner(SessionRunner.DefaultScratchRoot(), start);
            }
            catch (IOException ex)
            {
                Inform("Could not create the scratch folder for this recording: " + ex.Message);
                AppLog.Line("record: aborted, scratch folder failed: " + ex.Message);
                return;
            }
            catch (UnauthorizedAccessException ex)
            {
                Inform("Could not create the scratch folder for this recording: " + ex.Message);
                AppLog.Line("record: aborted, scratch folder failed: " + ex.Message);
                return;
            }
            catch (ArgumentException ex)
            {
                Inform("Could not create the scratch folder for this recording: " + ex.Message);
                AppLog.Line("record: aborted, scratch folder failed: " + ex.Message);
                return;
            }

            _ffmpeg = ffmpeg;
            _region = region;
            _monitor = monitor;
            _outputFolder = folder;
            _startTime = start;
            _session = session;

            // Audio is fixed for the whole capture, before the first segment
            // exists: every segment must have the same stream layout, because the
            // join is -c copy. Resolving here (not per segment) is also what makes
            // a pause unable to add or remove the audio stream.
            _micDevice = _microphone == null ? null : _microphone();
            _systemAudioEnabled = _systemAudio != null && _systemAudio();

            AppLog.Line(
                "record: session started scratch=" + session.ScratchDir
                + " outputFolder=" + folder
                + " microphone=" + (_micDevice ?? "(none)")
                + " systemAudio=" + (_systemAudioEnabled ? "on" : "off")
                + " start=" + start.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));

            SpawnSegment(true);
        }

        /// <summary>
        /// Spawns the next segment with the session's next path and the unchanged
        /// request, and enters Starting until its first frame.
        /// </summary>
        /// <param name="first">True for segment 1, false for a resume.</param>
        /// <remarks>
        /// Side effects: one log line with the full command line (identical for every
        /// segment except the path), one child, three drain threads, and the
        /// first-frame watchdog. Failure modes: a spawn error aborts a first segment
        /// (scratch discarded, one note) and returns a resume to Paused with a note,
        /// because the recording so far is still worth keeping.
        /// </remarks>
        private void SpawnSegment(bool first)
        {
            SessionRunner session = _session;
            if (session == null)
            {
                AppLog.Line("segment: refused, no session is open");
                return;
            }

            string segmentPath = session.NextSegmentPath();

            // System audio (DESIGN.md section 5d) is prepared before the spawn: the
            // pipe must exist first, because ffmpeg opens it and reads it as a normal
            // file, and its first read must find this writer. The name carries the
            // session and the segment, so the previous segment's writer cannot be
            // picked up by this child.
            string pipePath = null;
            LoopbackCapture loopback = null;
            if (_systemAudioEnabled)
            {
                pipePath = FfmpegArgs.SystemAudioPipeName(
                    Path.GetFileName(session.ScratchDir), Path.GetFileNameWithoutExtension(segmentPath));
                string pipeError;
                loopback = LoopbackCapture.Create(pipePath, AppLog.Line, out pipeError);
                if (loopback == null)
                {
                    // Same handling as a failed spawn, and for the same reason: no
                    // segment may exist with a different stream layout from its
                    // siblings, because the join is -c copy.
                    AppLog.Line("segment: system audio pipe failed: " + pipeError);
                    Inform("System audio could not start: " + pipeError);
                    if (first)
                    {
                        session.DiscardScratch();
                        _session = null;
                        _state = CaptureState.Idle;
                        AppLog.Line("record: idle");
                    }
                    else
                    {
                        _state = CaptureState.Paused;
                        if (_pill != null)
                        {
                            _pill.SetPaused(true);
                        }
                        AppLog.Line("resume: failed (system audio pipe), state=Paused (earlier segments kept)");
                    }
                    return;
                }
                AppLog.Line("segment: system audio pipe=" + pipePath);
            }

            int cq = _quality == null ? _config.Quality : _quality();
            int fps = _fps == null ? _config.Fps : _fps();
            FfmpegRequest request = new FfmpegRequest(
                segmentPath, _monitor.Index, _region, fps, cq, _micDevice, pipePath);
            string[] arguments = FfmpegArgs.Build(request);

            AppLog.Line("segment: argv ffmpeg " + FfmpegArgs.ToCommandLine(arguments));

            Recorder recorder = new Recorder(_ffmpeg, arguments);
            recorder.CaptureReady += OnCaptureReady;
            recorder.Exited += OnRecorderExited;

            _spawnMs = _clock.ElapsedMilliseconds;
            string startError;
            if (!recorder.Start(out startError))
            {
                recorder.Dispose();
                if (loopback != null)
                {
                    // The child never opened the pipe; nothing is draining it, so
                    // there is nothing to keep open.
                    loopback.Stop();
                    loopback.Dispose();
                }
                AppLog.Line("segment: spawn failed: " + startError);
                Inform("Recording could not start: " + startError);

                if (first)
                {
                    session.DiscardScratch();
                    _session = null;
                    _state = CaptureState.Idle;
                    AppLog.Line("record: idle");
                }
                else
                {
                    // The earlier segments are still on disk and the session stays
                    // open: the user can press Resume again or Stop to save what
                    // exists, which is better than throwing the session away. The
                    // pill is frozen again so the visible timer still excludes the
                    // interval that produced no frames.
                    _state = CaptureState.Paused;
                    if (_pill != null)
                    {
                        _pill.SetPaused(true);
                    }
                    AppLog.Line("resume: failed, state=Paused (earlier segments kept)");
                }
                return;
            }

            _recorder = recorder;
            _segmentPath = segmentPath;
            _loopback = loopback;
            _loopbackStarted = false;
            _audioPipe = pipePath;
            _exitRequested = false;
            _state = CaptureState.Starting;

            if (loopback != null)
            {
                // Started with the segment, not with the first frame. WHY: ffmpeg
                // cannot report a first video frame until its audio input has PCM to
                // interleave - measured with a synthetic pipeline, where a zero-audio
                // pipe produced no frame= line at all while the same pipeline with
                // PCM reported within 0.4 s. Starting the writer only on that report
                // would deadlock until the 2 s first-frame fallback, losing the first
                // seconds of the recording. The writer's gate stays closed until the
                // first-frame event, so it feeds paced silence meanwhile and the real
                // pre-roll never reaches the file (DESIGN.md section 5d).
                loopback.Start();
                _loopbackStarted = true;
                AppLog.Line(
                    "audio: loopback writer started with the segment at t+"
                    + _spawnMs.ToString(CultureInfo.InvariantCulture) + " ms (gate closed)");
            }
            AppLog.Line(
                "segment: spawned pid=" + recorder.ProcessId.ToString(CultureInfo.InvariantCulture)
                + " path=" + segmentPath
                + " offset_x=" + _region.X.ToString(CultureInfo.InvariantCulture)
                + " offset_y=" + _region.Y.ToString(CultureInfo.InvariantCulture)
                + " video_size=" + _region.W.ToString(CultureInfo.InvariantCulture)
                + "x" + _region.H.ToString(CultureInfo.InvariantCulture)
                + " pipe=" + (pipePath ?? "(none)")
                + " state=Starting t+" + _spawnMs.ToString(CultureInfo.InvariantCulture) + " ms");
        }

        /// <summary>
        /// Stops the capture because the hotkey was pressed.
        /// </summary>
        /// <remarks>
        /// Side effects: the same as <see cref="Stop"/>, plus a log line when there
        /// is nothing to stop, so a press that did nothing is still visible.
        /// </remarks>
        internal void StopFromHotkey()
        {
            if (_state == CaptureState.Idle)
            {
                AppLog.Line("stop: ignored, the hotkey arrived while idle");
                return;
            }
            Stop("hotkey");
        }

        /// <summary>
        /// Stops the capture because the app is quitting.
        /// </summary>
        /// <remarks>
        /// Side effects: finalises a running recording, so Exit writes the trailer
        /// and the join instead of relying on the job object's kill. A no-op when
        /// idle.
        /// </remarks>
        internal void StopForExit()
        {
            if (_state == CaptureState.Idle)
            {
                return;
            }
            AppLog.Line("stop: requested because the app is exiting");
            Stop("exit");
        }

        /// <summary>
        /// Pauses the capture: ends the current segment and keeps the session open
        /// (DESIGN.md section 2, Recording -> Pausing -> Paused).
        /// </summary>
        /// <param name="reason">Short reason for the log: "pill".</param>
        /// <remarks>
        /// Side effects: freezes the pill's timer at the click, writes "q" to the
        /// current child (or terminates it after the stop timeout), adds that segment
        /// to the session with its frame count, disposes the child and leaves the
        /// controller in Paused with the session and the pill alive.
        ///
        /// Legal only from Recording. A press in any other state is logged and
        /// dropped, never queued: during Pausing the child is still writing its
        /// trailer, and queueing a pause would produce a second child behind the
        /// first (DESIGN.md section 9, WO-11 C6). Never throws.
        /// </remarks>
        internal void Pause(string reason)
        {
            if (_state != CaptureState.Recording)
            {
                AppLog.Line(
                    "pause: ignored, state=" + _state + " (" + reason + ") - pause is legal only while recording");
                return;
            }

            Recorder recorder = _recorder;
            if (recorder == null)
            {
                AppLog.Line("pause: ignored, no child is running (" + reason + ")");
                return;
            }

            _state = CaptureState.Pausing;
            _exitRequested = true;
            AppLog.Line(
                "pause: requested reason=" + reason + " state=Pausing t+"
                + _clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");

            // Frozen here, not when the child is gone: the frames that matter to the
            // user stopped being captured when they clicked, and a timer that kept
            // running for the ~300 ms trailer write would overstate the recording.
            if (_pill != null)
            {
                _pill.SetPaused(true);
            }

            // Before the child is asked to end: capture stops now, but the pipe stays
            // open with silence until the trailer is written and ffmpeg exits, or the
            // child would see EOF and end the segment on its own terms.
            StopLoopback("pause");

            RecorderStop outcome = recorder.Stop();
            LogRecorderStop(recorder, outcome);
            AddCurrentSegment(recorder, outcome);

            recorder.Dispose();
            DisposeLoopback();
            _recorder = null;
            _segmentPath = null;
            _state = CaptureState.Paused;

            SessionRunner session = _session;
            AppLog.Line(
                "pause: done state=Paused segmentsRecorded="
                + (session == null ? 0 : session.Segments.Count).ToString(CultureInfo.InvariantCulture)
                + " t+" + _clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
        }

        /// <summary>
        /// Resumes a paused capture: spawns the next segment with the identical argv
        /// (DESIGN.md section 2, Paused -> Starting).
        /// </summary>
        /// <param name="reason">Short reason for the log: "pill".</param>
        /// <remarks>
        /// Side effects: restarts the pill's timer from the frozen value and spawns one
        /// child, whose first frame completes Starting. Legal only from Paused; every
        /// other press is logged and dropped. On a spawn failure the session stays
        /// open and the pill is frozen again, so the recording so far can still be
        /// saved (see <see cref="SpawnSegment"/>).
        /// </remarks>
        internal void Resume(string reason)
        {
            if (_state != CaptureState.Paused)
            {
                AppLog.Line(
                    "resume: ignored, state=" + _state + " (" + reason + ") - resume is legal only while paused");
                return;
            }

            AppLog.Line(
                "resume: requested reason=" + reason + " t+"
                + _clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
            if (_pill != null)
            {
                _pill.SetPaused(false);
            }
            SpawnSegment(false);
        }

        /// <summary>
        /// Ends the capture and produces the one output file.
        /// </summary>
        /// <param name="reason">Short reason for the log: "pill", "esc", "hotkey", "exit", "ffmpeg-exited".</param>
        /// <remarks>
        /// Side effects: hides the pill, ends the current child if there is one
        /// (writing "q", or terminating it after the stop timeout), adds the last
        /// segment, then joins everything in the session into one file in the output
        /// folder. Directories and children are released on every path, including
        /// the failure paths, so no capture can leave the controller stuck in
        /// Finalising.
        ///
        /// From Paused there is no child: what exists is joined as it stands. From
        /// Pausing a stop is unable to be honoured without queueing, so it is dropped
        /// with a log line - the pause is at most a trailer write away from Paused,
        /// where Stop is legal again (DESIGN.md section 2, which has no
        /// Pausing -> Finalising row).
        ///
        /// Blocking: at most the recorder's stop budget plus the join. The pill is
        /// hidden first, so the visible state changes before the wait.
        /// </remarks>
        internal void Stop(string reason)
        {
            if (_state == CaptureState.Idle)
            {
                AppLog.Line("stop: ignored, nothing is recording (" + reason + ")");
                return;
            }
            if (_state == CaptureState.Finalising)
            {
                AppLog.Line("stop: ignored, already finalising (" + reason + ")");
                return;
            }
            if (_state == CaptureState.Pausing)
            {
                AppLog.Line(
                    "stop: ignored, the current segment is still being finalised (pausing); press again (" + reason + ")");
                return;
            }

            Recorder recorder = _recorder;
            _state = CaptureState.Finalising;
            _exitRequested = true;

            Stopwatch stopwatch = Stopwatch.StartNew();
            AppLog.Line(
                "stop: requested reason=" + reason
                + " state=Finalising t+" + _clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");

            HidePill();

            bool endedEarly = false;
            int earlyExitCode = 0;
            string earlyStderr = null;

            if (recorder != null)
            {
                StopLoopback("stop");

                RecorderStop outcome = recorder.Stop();
                LogRecorderStop(recorder, outcome);
                AddCurrentSegment(recorder, outcome);

                // "Ended early" is a segment that produced frames and then gave up
                // on its own, whichever way we found out: DESIGN.md section 9's
                // "exits non-zero mid-recording" row covers both a child that died
                // while recording (AlreadyExited) and one that answers the stop with
                // a failure. A killed segment is this app's own timeout, not
                // ffmpeg's verdict, so it is not reported as ended early.
                if (!outcome.Killed && outcome.ExitCode != 0 && recorder.Frames > 0)
                {
                    endedEarly = true;
                    earlyExitCode = outcome.ExitCode;
                    earlyStderr = recorder.LastStderrLine;
                }

                recorder.Dispose();
                DisposeLoopback();
                _recorder = null;
                _segmentPath = null;
            }
            else
            {
                AppLog.Line("stop: no child to stop (state was paused), joining what exists");
            }

            Finalise(endedEarly, earlyExitCode, earlyStderr, stopwatch);
        }

        /// <summary>
        /// Adds the just-stopped segment to the session, with its frame count.
        /// </summary>
        /// <param name="recorder">The child that has been stopped.</param>
        /// <param name="outcome">How it stopped, for the log.</param>
        /// <remarks>
        /// Side effects: appends one entry to the session and writes one log line with
        /// the segment's path, frame count and byte size. Empty segments are added
        /// rather than dropped here: the join plan filters them, and the log keeps the
        /// fact that a pause produced nothing.
        /// </remarks>
        private void AddCurrentSegment(Recorder recorder, RecorderStop outcome)
        {
            SessionRunner session = _session;
            if (session == null || string.IsNullOrEmpty(_segmentPath))
            {
                AppLog.Line("segment: not added, the session is closed");
                return;
            }

            int frames = (int)recorder.Frames;
            long bytes = FileLength(_segmentPath);
            session.Add(_segmentPath, frames);
            AppLog.Line(
                "segment: closed path=" + _segmentPath
                + " frames=" + frames.ToString(CultureInfo.InvariantCulture)
                + " bytes=" + bytes.ToString(CultureInfo.InvariantCulture)
                + " captured=" + OutcomeName(outcome)
                + " index=" + session.Segments.Count.ToString(CultureInfo.InvariantCulture)
                + " state=" + _state);
        }

        /// <summary>
        /// Runs the join, reports the outcome and returns the controller to Idle.
        /// </summary>
        /// <param name="endedEarly">True when the last segment reported frames and then failed.</param>
        /// <param name="exitCode">That segment's exit code, for the message.</param>
        /// <param name="stderr">Its last error line, or null.</param>
        /// <param name="stopwatch">Timer started when the stop was requested.</param>
        /// <remarks>
        /// Side effects: resolves the collision-safe output name from the capture's
        /// start time, runs <see cref="SessionRunner.Join"/> (which writes exactly one
        /// ffmpeg child and deletes the scratch directory only on
        /// success), toasts one message and logs the stop-to-file measurement.
        ///
        /// The messages are chosen from the join outcome, not from the last segment
        /// alone: "nothing was recorded" only when no segment contributed a frame,
        /// and "segments kept" naming the scratch directory whenever a join failed -
        /// never a silent loss, never a deleted segment (DESIGN.md section 9).
        /// </remarks>
        private void Finalise(bool endedEarly, int exitCode, string stderr, Stopwatch stopwatch)
        {
            SessionRunner session = _session;
            if (session == null)
            {
                AppLog.Line("join: no session to finalise");
                Finish();
                return;
            }

            string outputPath;
            try
            {
                outputPath = ResolveOutputPath();
            }
            catch (InvalidOperationException ex)
            {
                // No free name: the recording is still worth keeping, so the segments
                // stay and the message says where.
                AppLog.Line("join: aborted, no free output name: " + ex.Message);
                Inform("Could not name the output file. The recording is kept in " + session.ScratchDir
                    + ". " + ex.Message);
                Finish();
                return;
            }

            JoinOutcome join = session.Join(_ffmpeg, outputPath);
            long bytes = join.Succeeded ? FileLength(join.OutputPath) : -1;

            if (!join.Succeeded)
            {
                if (join.Kind == JoinKind.None)
                {
                    AppLog.Line("stop: nothing usable, scratch=" + join.ScratchDir);
                    Inform("Nothing was recorded: no segment contained a frame." + StderrSuffix(stderr));
                }
                else
                {
                    // DESIGN.md section 9's "join fails" row: keep every segment and
                    // name the place they are in, so the user can recover the clip.
                    AppLog.Line(
                        "stop: join failed, segments kept scratch=" + join.ScratchDir
                        + " detail=" + join.Detail);
                    Inform("Could not join the recording, so every segment is kept in "
                        + join.ScratchDir + ". " + join.Detail);
                }
            }
            else if (endedEarly)
            {
                AppLog.Line(
                    "stop: saved partial join " + join.OutputPath
                    + " bytes=" + bytes.ToString(CultureInfo.InvariantCulture)
                    + " exit=" + exitCode.ToString(CultureInfo.InvariantCulture));
                Inform("Recording ended early (ffmpeg exit "
                    + exitCode.ToString(CultureInfo.InvariantCulture) + "): " + join.OutputPath
                    + StderrSuffix(stderr));
            }
            else
            {
                AppLog.Line(
                    "stop: saved " + join.OutputPath
                    + " bytes=" + bytes.ToString(CultureInfo.InvariantCulture)
                    + " segments=" + join.SegmentCount.ToString(CultureInfo.InvariantCulture)
                    + " frames=" + join.FrameCount.ToString(CultureInfo.InvariantCulture));
                Inform("Saved " + join.OutputPath);
            }

            // Measured after the file's final state is settled, because that is when
            // "the file is on disk" becomes true for the user (DESIGN.md section 10).
            AppLog.Line(
                "stop: stop->file " + stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)
                + " ms (path=" + (join.OutputPath ?? "(none)")
                + " bytes=" + bytes.ToString(CultureInfo.InvariantCulture)
                + " kind=" + join.Kind
                + " segments=" + join.SegmentCount.ToString(CultureInfo.InvariantCulture)
                + " reportedFrames=" + join.FrameCount.ToString(CultureInfo.InvariantCulture)
                + " joinMs=" + join.ElapsedMs.ToString(CultureInfo.InvariantCulture) + ")");

            Finish();
        }

        /// <summary>
        /// Chooses the output file name for this capture, collision-safe at the moment
        /// of the join.
        /// </summary>
        /// <returns>An absolute path in the configured folder.</returns>
        /// <exception cref="InvalidOperationException">
        /// Every candidate name is taken; the caller keeps the segments and reports it.
        /// </exception>
        /// <remarks>
        /// Side effects: none beyond the existence probes. The timestamp is the
        /// capture's start, so the name identifies when the recording began, and it is
        /// resolved now rather than at Begin so a file that appeared during a long
        /// recording cannot be overwritten (DESIGN.md section 7, PLAN AC7).
        /// </remarks>
        private string ResolveOutputPath()
        {
            string fileName = Naming.UniqueFileName(
                _startTime,
                candidate => File.Exists(Path.Combine(_outputFolder, candidate)));
            return Path.Combine(_outputFolder, fileName);
        }

        /// <summary>Writes the one-line summary of how a child stopped.</summary>
        /// <param name="recorder">The stopped child, for its last error line.</param>
        /// <param name="outcome">Measured stop facts.</param>
        /// <remarks>Side effects: one log line. Used by both Pause and Stop so the two paths report identically.</remarks>
        private void LogRecorderStop(Recorder recorder, RecorderStop outcome)
        {
            AppLog.Line(
                "recorder: stopped graceful=" + outcome.Graceful
                + " killed=" + outcome.Killed
                + " alreadyExited=" + outcome.AlreadyExited
                + " exit=" + outcome.ExitCode.ToString(CultureInfo.InvariantCulture)
                + " stopMs=" + outcome.ElapsedMs.ToString(CultureInfo.InvariantCulture)
                + (outcome.WriteError == null ? string.Empty : " stdinError=" + outcome.WriteError)
                + StderrSuffix(recorder.LastStderrLine));
        }

        /// <summary>
        /// Marshals the recorder's first-frame event onto the UI thread.
        /// </summary>
        /// <param name="sender">The recorder that reported readiness.</param>
        /// <param name="e">Event arguments (unused).</param>
        /// <remarks>
        /// Side effects: queues one dispatcher callback. Reading the recorder's
        /// fields here rather than inside the callback keeps the values from the
        /// moment the event fired.
        /// </remarks>
        private void OnCaptureReady(object sender, EventArgs e)
        {
            Recorder recorder = sender as Recorder;
            if (recorder == null)
            {
                return;
            }

            bool timedOut = recorder.CaptureReadyTimedOut;
            long firstFrameMs = recorder.FirstFrameElapsedMs;
            bool hasFrame = recorder.HasFirstFrame;
            OnUi(delegate { EnterRecording(recorder, hasFrame, timedOut, firstFrameMs); });
        }

        /// <summary>
        /// Ends Starting: logs whether a frame or the fallback completed it, then
        /// shows the pill.
        /// </summary>
        /// <param name="recorder">The recorder that reported readiness.</param>
        /// <param name="hasFrame">True when a frame= greater than 0 was actually seen.</param>
        /// <param name="timedOut">True when the 2000 ms fallback fired.</param>
        /// <param name="firstFrameMs">Measured spawn-to-frame time, or -1.</param>
        /// <remarks>
        /// Side effects: sets Recording, shows and times the pill, and writes the log
        /// line that makes the two readiness paths distinguishable. Ignored when the
        /// capture already moved on (stopped during Starting, or a stale event from a
        /// previous segment).
        /// </remarks>
        private void EnterRecording(Recorder recorder, bool hasFrame, bool timedOut, long firstFrameMs)
        {
            if (_state != CaptureState.Starting || !object.ReferenceEquals(_recorder, recorder))
            {
                return;
            }

            _state = CaptureState.Recording;
            if (hasFrame)
            {
                AppLog.Line(
                    "record: first frame at t+" + _clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)
                    + " ms (spawn->frame "
                    + firstFrameMs.ToString(CultureInfo.InvariantCulture) + " ms) state=Recording");
            }
            else if (timedOut)
            {
                // A slow first frame is not a failure (DESIGN.md section 5a): say the
                // assumption out loud rather than aborting a working recording.
                AppLog.Line(
                    "record: no progress line within "
                    + Recorder.FirstFrameTimeoutMs.ToString(CultureInfo.InvariantCulture)
                    + " ms; assuming capture started state=Recording");
            }

            OpenAudioGate(hasFrame, firstFrameMs);
            ShowPill();        }

        /// <summary>
        /// Opens this segment's audio gate at the event that ends Starting, and logs
        /// the measurement that goes with it.
        /// </summary>
        /// <param name="hasFrame">True when a real first frame was reported.</param>
        /// <param name="firstFrameMs">Measured spawn-to-frame milliseconds, or -1.</param>
        /// <remarks>
        /// Side effects: flips the loopback writer from paced silence to real captured
        /// samples (one log line from the writer records how much pre-roll it
        /// discarded) and writes one line here with the measured
        /// <c>spawnToFrame</c> value. WHY the gate: the pipe must be fed from the
        /// moment the segment starts, because ffmpeg reports no video frame while its
        /// audio input is empty (its muxer holds the first video packet until an audio
        /// packet exists); but audio captured during ddagrab's warm-up belongs before
        /// video t=0, so it is discarded rather than shifted into the file. Opening
        /// here keeps the pipe flowing and makes the first real sample follow the
        /// silence already written (DESIGN.md section 5d).
        ///
        /// The fallback branch opens the gate too: a slow progress line must not cost
        /// the recording its sound for the whole segment. No-op when the segment has
        /// no loopback.
        /// </remarks>
        private void OpenAudioGate(bool hasFrame, long firstFrameMs)
        {
            LoopbackCapture loopback = _loopback;
            if (loopback == null)
            {
                return;
            }

            loopback.OpenGate();

            long nowMs = _clock.ElapsedMilliseconds;
            long frameMs = hasFrame && firstFrameMs >= 0 ? _spawnMs + firstFrameMs : nowMs;
            AppLog.Line(
                "audio: loopback gate opened by the recorder pipe=" + _audioPipe
                + " spawnToFrame " + (frameMs - _spawnMs).ToString(CultureInfo.InvariantCulture) + " ms"
                + (hasFrame ? " (first frame)" : " (no first frame line; 2 s fallback)")
                + " gatedSilenceFrames=" + loopback.SilenceFrames.ToString(CultureInfo.InvariantCulture)
                + " discardedPreRollFrames=" + loopback.DiscardedFrames.ToString(CultureInfo.InvariantCulture)
                + " device=" + (loopback.DeviceName ?? "(not open yet)"));
        }

        /// <summary>
        /// Stops this segment's capture and keeps its pipe open with silence.
        /// </summary>
        /// <param name="reason">Short reason for the log: "pause", "stop".</param>
        /// <remarks>
        /// Side effects: signals the writer (which switches to silence and keeps the
        /// pipe alive until ffmpeg exits) and writes one log line. A loopback that
        /// never started is disposed here instead: no child has attached to its pipe
        /// yet, so there is nothing to keep open and closing it releases an ffmpeg
        /// still waiting for its first PCM. Never throws.
        /// </remarks>
        private void StopLoopback(string reason)
        {
            LoopbackCapture loopback = _loopback;
            if (loopback == null)
            {
                return;
            }

            loopback.Stop();
            AppLog.Line(
                "audio: loopback stop requested reason=" + reason
                + " started=" + (_loopbackStarted ? "true" : "false")
                + " capturedFrames=" + loopback.CapturedFrames.ToString(CultureInfo.InvariantCulture)
                + " silenceFrames=" + loopback.SilenceFrames.ToString(CultureInfo.InvariantCulture));

            if (!_loopbackStarted)
            {
                DisposeLoopback();
            }
        }

        /// <summary>
        /// Releases this segment's loopback writer and pipe. Call after ffmpeg exited.
        /// </summary>
        /// <remarks>
        /// Side effects: joins the writer briefly and closes the pipe; see
        /// <see cref="LoopbackCapture.Dispose"/>. Idempotent, so the pause, stop and
        /// finalise paths can all call it. The counters are logged by Dispose itself,
        /// after the final numbers are known.
        /// </remarks>
        private void DisposeLoopback()
        {
            LoopbackCapture loopback = _loopback;
            _loopback = null;
            _loopbackStarted = false;
            _audioPipe = null;
            if (loopback != null)
            {
                loopback.Dispose();
            }
        }

        /// <summary>
        /// Creates and shows the stop pill for the current capture.
        /// </summary>
        /// <remarks>
        /// Side effects: creates a topmost window with Stop and Pause/Resume, starts
        /// its timer at 0:00 and writes the pill's own log line (bounds, affinity
        /// result, focus evidence, button positions). A failure to build the window is
        /// logged and the recording continues - losing the buttons must not lose the
        /// recording, because the hotkey still stops it.
        ///
        /// Called once per capture, not once per segment: on resume the pill already
        /// exists, so the frozen timer simply continues from where it was.
        /// </remarks>
        private void ShowPill()
        {
            if (_pill != null)
            {
                return;
            }

            try
            {
                StopPill pill = new StopPill();
                pill.StopRequested += OnPillStopRequested;
                pill.PauseRequested += OnPillPauseRequested;
                pill.ResumeRequested += OnPillResumeRequested;
                _pill = pill;
                pill.Open();
            }
            catch (Exception ex)
            {
                // A UI failure must never take the capture down.
                AppLog.Line("pill: failed " + ex.GetType().Name + ": " + ex.Message);
                _pill = null;
            }
        }

        /// <summary>
        /// Handles the pill's stop request.
        /// </summary>
        /// <param name="reason">"pill" for a click, "esc" for the keyboard.</param>
        /// <remarks>Side effects: one stop, exactly like the hotkey's.</remarks>
        private void OnPillStopRequested(string reason)
        {
            Stop(reason);
        }

        /// <summary>
        /// Handles a click on the button while it reads Pause.
        /// </summary>
        /// <param name="reason">"pause".</param>
        /// <remarks>
        /// Side effects: <see cref="Pause"/> decides whether the press is legal; this
        /// indirection exists so the pill stays a dumb control and every state rule
        /// lives in the controller.
        /// </remarks>
        private void OnPillPauseRequested(string reason)
        {
            Pause(reason);
        }

        /// <summary>
        /// Handles a click on the button while it reads Resume.
        /// </summary>
        /// <param name="reason">"resume".</param>
        /// <remarks>Side effects: <see cref="Resume"/> decides whether the press is legal.</remarks>
        private void OnPillResumeRequested(string reason)
        {
            Resume(reason);
        }

        /// <summary>
        /// Hides the pill and forgets it.
        /// </summary>
        /// <remarks>
        /// Side effects: the window disappears (and is closed), its timer stops and
        /// the handler is detached, so a queued click cannot reach a finished
        /// capture. Logged once per stop so "hidden while finalising" is visible in
        /// the log rather than inferred.
        /// </remarks>
        private void HidePill()
        {
            StopPill pill = _pill;
            _pill = null;
            if (pill == null)
            {
                return;
            }

            try
            {
                pill.StopRequested -= OnPillStopRequested;
                pill.PauseRequested -= OnPillPauseRequested;
                pill.ResumeRequested -= OnPillResumeRequested;
                pill.Dismiss();
                AppLog.Line("pill: hidden (state=" + _state + ")");
            }
            catch (Exception ex)
            {
                AppLog.Line("pill: dismiss failed " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Marshals the recorder's exit event onto the UI thread.
        /// </summary>
        /// <param name="sender">The recorder that exited.</param>
        /// <param name="e">Event arguments (unused).</param>
        /// <remarks>
        /// Side effects: queues one dispatcher callback. The exit code and the
        /// stop-requested flag are captured here so the callback reports the state at
        /// the moment of exit, not whatever it became by the time it ran.
        /// </remarks>
        private void OnRecorderExited(object sender, EventArgs e)
        {
            Recorder recorder = sender as Recorder;
            if (recorder == null)
            {
                return;
            }

            int exitCode = recorder.ExitCode;
            bool requested = _exitRequested;
            OnUi(delegate { HandleRecorderExit(recorder, exitCode, requested); });
        }

        /// <summary>
        /// Finalises the capture when ffmpeg exits without being asked to.
        /// </summary>
        /// <param name="recorder">The recorder that exited.</param>
        /// <param name="exitCode">Its exit code.</param>
        /// <param name="requested">
        /// True when a stop or a pause had already asked this child to end, so the
        /// exit is expected.
        /// </param>
        /// <remarks>
        /// Side effects: routes into <see cref="Stop"/>, so the file is inspected and
        /// reported by exactly one code path - a crash mid-recording must reach the
        /// same "ended early, file kept" outcome as any other failure. Ignored for an
        /// exit this app asked for (a pause ends a child exactly like a stop), a
        /// stale recorder, or a capture that is already finalising or paused.
        /// </remarks>
        private void HandleRecorderExit(Recorder recorder, int exitCode, bool requested)
        {
            if (!object.ReferenceEquals(recorder, _recorder) || requested
                || _state == CaptureState.Finalising || _state == CaptureState.Pausing
                || _state == CaptureState.Paused || _state == CaptureState.Idle)
            {
                return;
            }

            AppLog.Line(
                "recorder: exited unexpectedly exit=" + exitCode.ToString(CultureInfo.InvariantCulture)
                + " state=" + _state);
            Stop("ffmpeg-exited");
        }

        /// <summary>
        /// Returns the controller to Idle and releases the session and the recorder.
        /// </summary>
        /// <remarks>
        /// Side effects: disposes the recorder (which closes the job handle, killing a
        /// surviving child by design), forgets the session, the resolved ffmpeg path
        /// and the region, and logs the idle line. Called once per stop, including the
        /// failure paths, so no capture can leave the controller stuck in Finalising.
        /// The scratch directory is not touched here: <see cref="SessionRunner.Join"/>
        /// decided its fate before this runs, and deleting it again could remove a
        /// kept-failure directory.
        /// </remarks>
        private void Finish()
        {
            Recorder recorder = _recorder;
            _recorder = null;
            if (recorder != null)
            {
                recorder.Dispose();
            }
            // Safety net: every path above already released it, but a capture that
            // ends through an unexpected branch must not leave a pipe or a background
            // writer behind.
            DisposeLoopback();

            _session = null;
            _ffmpeg = null;
            _outputFolder = null;
            _segmentPath = null;
            _exitRequested = false;
            _state = CaptureState.Idle;
            AppLog.Line("record: idle");
        }

        /// <summary>The configured output folder, or the default when config left it empty.</summary>
        /// <returns>An absolute path; not created here.</returns>
        /// <remarks>
        /// Side effects: invokes the caller's folder reader once per capture. The answer
        /// is stored in <see cref="_outputFolder"/> at <see cref="Begin"/>, so every
        /// segment of this capture uses the same folder - which is what makes the -c
        /// copy join safe when the user changes the setting mid-recording. A throwing
        /// reader falls back to the parsed config rather than aborting a capture: a
        /// folder that cannot be read is no reason to refuse to record.
        /// </remarks>
        private string OutputFolderPath()
        {
            Func<string> source = _folder;
            if (source != null)
            {
                try
                {
                    string folder = source();
                    if (!string.IsNullOrEmpty(folder))
                    {
                        return folder;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Line("record: folder unreadable " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            return string.IsNullOrEmpty(_config.Folder) ? OutputFolder.ResolveDefault() : _config.Folder;
        }

        /// <summary>Runs a claim-or-report action through the tray's information channel.</summary>
        /// <param name="message">Message to show; null and empty are ignored by the sink.</param>
        /// <remarks>Side effects: a balloon tip and a log line when a sink was supplied.</remarks>
        private void Inform(string message)
        {
            Action<string> inform = _inform;
            if (inform != null)
            {
                inform(message);
            }
        }

        /// <summary>
        /// Returns a file's length, or -1 when it does not exist.
        /// </summary>
        /// <param name="path">Path to inspect; null yields -1.</param>
        /// <returns>The length in bytes, or -1.</returns>
        /// <remarks>
        /// Side effects: none. Distinguishes "absent" from "empty" because the two
        /// get different messages and only the empty one is deleted.
        /// </remarks>
        private static long FileLength(string path)
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
        private static string StderrSuffix(string line)
        {
            return string.IsNullOrEmpty(line) ? string.Empty : " ffmpeg said: " + line;
        }

        /// <summary>Names a stop outcome for the measurement line.</summary>
        /// <param name="outcome">Outcome to describe.</param>
        /// <returns>"graceful", "killed", or "already-exited".</returns>
        /// <remarks>Side effects: none.</remarks>
        private static string OutcomeName(RecorderStop outcome)
        {
            if (outcome.Graceful)
            {
                return "graceful";
            }
            return outcome.Killed ? "killed" : "already-exited";
        }

        /// <summary>
        /// Queues an action on the WPF dispatcher.
        /// </summary>
        /// <param name="action">Action to run on the UI thread.</param>
        /// <remarks>
        /// Side effects: queues the action. Silently does nothing when there is no
        /// application or its dispatcher has shut down - at that point the process is
        /// exiting and the job object is the only thing that still matters.
        /// </remarks>
        private static void OnUi(Action action)
        {
            Application app = Application.Current;
            if (app == null)
            {
                return;
            }
            try
            {
                app.Dispatcher.BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // Dispatcher already shutting down.
            }
        }
    }

    /// <summary>
    /// Tray presence, the four commands (Capture / Open folder / Start with
    /// Windows / Exit), and the one channel startup notes are surfaced through.
    /// </summary>
    /// <remarks>
    /// A WinForms <see cref="NotifyIcon"/> is used because WPF has no tray API
    /// and no package may be added. Disposing it is mandatory: otherwise a ghost
    /// icon survives in the notification area until the user hovers over it.
    ///
    /// "Start with Windows" is a checkable item backed by
    /// <see cref="AutoStart"/>; it is the same registry value install.ps1 writes,
    /// so the tray and the installer agree about which entry they own.
    /// </remarks>
    internal sealed class TrayIcon : IDisposable
    {
        private readonly Application _app;
        private readonly NotifyIcon _icon;
        private bool _disposed;

        /// <summary>
        /// The "Start with Windows" item, kept so a click can correct its check
        /// state from what the registry actually says after the write.
        /// </summary>
        private readonly ToolStripMenuItem _autoStartItem;

        /// <summary>
        /// Builds the tray icon and its menu.
        /// </summary>
        /// <param name="app">The WPF application whose lifetime Exit ends.</param>
        /// <remarks>
        /// Side effects: creates a hidden NotifyIcon (not yet visible) and the
        /// context menu. Does not touch the notification area until
        /// <see cref="Show"/> is called.
        /// </remarks>
        internal TrayIcon(Application app)
        {
            _app = app;

            var menu = new ContextMenuStrip();
            menu.Items.Add(MenuItem("Capture", OnCapture));
            menu.Items.Add(MenuItem("Open folder", OnOpenFolder));

            // The check mark is the only visible state, and it must describe the
            // registry before the user can click. Read once here rather than on
            // every menu open: a running process cannot have its value changed
            // behind it, because install.ps1 stops the app first and this item is
            // the only other writer.
            _autoStartItem = MenuItem("Start with Windows", OnToggleAutoStart);
            _autoStartItem.Checked = ReadAutoStartState();
            menu.Items.Add(_autoStartItem);

            menu.Items.Add(MenuItem("Exit", OnExit));

            _icon = new NotifyIcon
            {
                // Cloned on purpose: NotifyIcon disposes the Icon it is handed,
                // and SystemIcons.Application is a shared process-wide instance.
                Icon = (Icon)SystemIcons.Application.Clone(),
                Text = "SnipVid 1.0.0",
                ContextMenuStrip = menu,
                Visible = false,
            };
        }

        /// <summary>Adds the icon to the notification area.</summary>
        /// <remarks>Side effects: a Shell_NotifyIcon call; the icon appears immediately.</remarks>
        internal void Show()
        {
            _icon.Visible = true;
        }

        /// <summary>
        /// Surfaces one startup note: one log line plus one balloon tip.
        /// </summary>
        /// <param name="note">
        /// Human-readable note from <see cref="Config.Parse"/> or from the hotkey
        /// arm step; null or empty is ignored.
        /// </param>
        /// <remarks>
        /// Side effects: appends to bin\SnipVid.log and requests a balloon tip on
        /// the visible icon. DESIGN.md section 8 requires notes once on startup and
        /// never as a modal dialog: a balloon is non-blocking and does not steal
        /// focus from whatever the user is doing. The log line is written first
        /// because the shell shows one balloon at a time, so a burst of notes
        /// replaces itself on screen while the log keeps every one of them - the
        /// log is the durable copy, and the balloon is the courtesy. Requires a
        /// visible icon, so it is only called after <see cref="Show"/>.
        /// </remarks>
        internal void ShowNote(string note)
        {
            if (string.IsNullOrEmpty(note))
            {
                return;
            }
            AppLog.Line("note: " + note);
            _icon.ShowBalloonTip(10000, "SnipVid", note, ToolTipIcon.Warning);
        }

        /// <summary>
        /// Reports a normal outcome - where a recording was saved - as one log line
        /// plus one informational balloon.
        /// </summary>
        /// <param name="message">Text to show; null or empty is ignored.</param>
        /// <remarks>
        /// Side effects: appends to bin\SnipVid.log and requests a balloon tip with
        /// the informational icon, deliberately distinct from
        /// <see cref="ShowNote"/>, which warns about a rejected setting. The path
        /// itself is the message (DESIGN.md section 7: the file is the only output
        /// side effect), so no modal dialog, no clipboard and no player launch.
        /// Requires a visible icon, like ShowNote.
        /// </remarks>
        internal void Inform(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }
            AppLog.Line("toast: " + message);
            _icon.ShowBalloonTip(5000, "SnipVid", message, ToolTipIcon.Info);
        }

        /// <summary>
        /// Removes the icon from the notification area and releases its native
        /// handle. Safe to call more than once.
        /// </summary>
        /// <remarks>Side effects: hides the icon; no further clicks can arrive.</remarks>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _icon.Visible = false;
            _icon.Dispose();
        }

        /// <summary>
        /// Capture command: opens the same region picker the hotkey does.
        /// </summary>
        /// <remarks>
        /// Side effects: one log line, then the overlay through
        /// <see cref="Program.StartCaptureFromTray"/>. Routed through Program rather
        /// than calling the overlay directly so a tray click and a hotkey press take
        /// exactly one code path.
        /// </remarks>
        private void OnCapture(object sender, EventArgs e)
        {
            Program.StartCaptureFromTray();
        }

        /// <summary>
        /// Open folder command: creates the output folder if absent, then shows
        /// it in Explorer.
        /// </summary>
        /// <remarks>
        /// Side effects: may create a directory and always spawns explorer.exe
        /// (a shell child, not an owned child - it is not tracked or killed), plus
        /// one log line naming the folder it opened. The folder comes from
        /// <see cref="Program.OutputFolderPath"/>, the same source the recorder reads
        /// at Begin, so this opens the folder the next recording will actually use -
        /// including one chosen in the overlay's Save to row since launch.
        /// Failure modes: creation or launch errors are logged and surfaced as a
        /// balloon tip; the tray stays alive.
        /// </remarks>
        private void OnOpenFolder(object sender, EventArgs e)
        {
            try
            {
                string folder = Program.OutputFolderPath;
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + folder + "\"",
                    UseShellExecute = false,
                });
                AppLog.Line("open folder: " + folder);
            }
            catch (Exception ex)
            {
                // A tray command must never take the process down.
                AppLog.Line("open folder failed: " + ex.Message);
                _icon.ShowBalloonTip(
                    3000, "SnipVid", "Could not open the folder: " + ex.Message, ToolTipIcon.Warning);
            }
        }

        /// <summary>
        /// Toggles the per-user Run entry for the executable this process is
        /// running from.
        /// </summary>
        /// <param name="sender">The menu item (unused).</param>
        /// <param name="e">Event arguments (unused).</param>
        /// <remarks>
        /// Side effects: writes or deletes
        /// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run\SnipVid</c>, then
        /// re-reads the entry so the check mark reports what is actually stored
        /// rather than what the write intended. A failure (permissions, an
        /// unreadable hive, an executable path that cannot be found) is one log
        /// line and one balloon - never a dialog (DESIGN.md section 8) - and the
        /// check mark is restored from state. Never throws: a tray command must not
        /// be able to take the process down.
        /// </remarks>
        private void OnToggleAutoStart(object sender, EventArgs e)
        {
            try
            {
                if (AutoStart.IsEnabled())
                {
                    AutoStart.Disable();
                }
                else
                {
                    AutoStart.Enable();
                }

                bool enabled = AutoStart.IsEnabled();
                _autoStartItem.Checked = enabled;
                AppLog.Line(
                    "autostart: " + (enabled ? "enabled" : "disabled")
                    + " path=" + AutoStart.ExecutablePath);
            }
            catch (Exception ex)
            {
                AppLog.Line("autostart: failed " + ex.GetType().Name + ": " + ex.Message);
                _autoStartItem.Checked = ReadAutoStartState();
                _icon.ShowBalloonTip(
                    3000,
                    "SnipVid",
                    "Could not change 'Start with Windows': " + ex.Message,
                    ToolTipIcon.Warning);
            }
        }

        /// <summary>
        /// Reads whether Windows is set to start this executable.
        /// </summary>
        /// <returns>True only when the Run value names the running executable.</returns>
        /// <remarks>
        /// Side effects: one log line when the registry cannot be read; no toast,
        /// because this runs while the tray icon is still being built and a balloon
        /// needs a visible icon. Returns false in that case, which is the state the
        /// user can act on: clicking the item then attempts a write and reports its
        /// own failure. Never throws, so building the tray can never be the reason
        /// startup fails.
        /// </remarks>
        private bool ReadAutoStartState()
        {
            try
            {
                return AutoStart.IsEnabled();
            }
            catch (Exception ex)
            {
                AppLog.Line("autostart: state unreadable: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Exit command: stops a running capture, hides the icon and shuts the WPF
        /// dispatcher down, which ends <c>Application.Run()</c> with code 0.
        /// </summary>
        /// <remarks>
        /// Side effects: may finalise a recording (up to the stop timeout) and then
        /// ends the process once the click handler returns. Stopping here rather
        /// than relying on the job object keeps the trailer of a finished recording;
        /// the job is the backstop for the paths where this never runs. The icon
        /// itself is disposed by the using block in Program.Main.
        /// </remarks>
        private void OnExit(object sender, EventArgs e)
        {
            Program.PrepareForExit();
            _icon.Visible = false;
            _app.Shutdown(0);
        }

        /// <summary>Creates one menu item wired to a handler.</summary>
        private static ToolStripMenuItem MenuItem(string text, EventHandler onClick)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += onClick;
            return item;
        }
    }

    /// <summary>
    /// The per-user auto-start entry: <c>HKCU\Software\Microsoft\Windows\
    /// CurrentVersion\Run</c>, value name <c>SnipVid</c>, data the quoted path of
    /// the running executable.
    /// </summary>
    /// <remarks>
    /// WHY the running executable and not the installed path: the toggle has to
    /// describe and change the entry for the copy the user is looking at, and
    /// install.ps1 writes the same value for the installed copy. A value naming
    /// another path therefore reads as "off" - Windows would start something that
    /// is not this process, which is the only claim the check mark makes.
    ///
    /// Per-user (HKCU) on purpose: no elevation, no machine-wide state, and
    /// switching it off deletes one value instead of leaving a disabled script for
    /// the user to find. Registry access can throw (SecurityException,
    /// UnauthorizedAccessException, IOException); nothing here swallows those -
    /// the tray handlers turn them into one log line and one balloon, because a
    /// tray command may not take the process down (DESIGN.md section 9).
    /// </remarks>
    internal static class AutoStart
    {
        /// <summary>Run key, without the HKCU hive prefix.</summary>
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>
        /// Value name. install.ps1 writes the same name, so the two cannot disagree
        /// about which entry they own.
        /// </summary>
        private const string ValueName = "SnipVid";

        /// <summary>
        /// Full path of the executable this process is running from.
        /// </summary>
        /// <remarks>
        /// Read from the process rather than from a literal, so a moved install or
        /// the run.ps1 development copy reports its own path. Side effects: none.
        /// Throws when the main module path is unavailable; the tray handlers catch
        /// that and report it.
        /// </remarks>
        internal static string ExecutablePath
        {
            get { return Process.GetCurrentProcess().MainModule.FileName; }
        }

        /// <summary>
        /// Answers whether Windows is set to start this executable.
        /// </summary>
        /// <returns>
        /// True only for an entry whose data - unquoted and compared
        /// case-insensitively - equals <see cref="ExecutablePath"/>.
        /// </returns>
        /// <remarks>
        /// Side effects: none; read-only registry access. A missing Run key, a
        /// missing value or a REG_SZ naming another path all report false instead
        /// of throwing, because "off" is a valid answer and a missing key is the
        /// normal state on a fresh profile. Other registry failures (permissions,
        /// an unreadable hive) propagate to the caller.
        /// </remarks>
        internal static bool IsEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                if (key == null)
                {
                    return false;
                }

                string value = key.GetValue(ValueName) as string;
                if (string.IsNullOrEmpty(value))
                {
                    return false;
                }

                return string.Equals(Unquote(value), ExecutablePath, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Writes or replaces the Run entry so Windows starts this executable.
        /// </summary>
        /// <remarks>
        /// Side effects: creates the Run key when it is missing and writes one
        /// REG_SZ. The data is the executable path wrapped in double quotes,
        /// because the Run key hands its data to CreateProcess unparsed and a path
        /// with spaces would otherwise be split. Idempotent: writing the same value
        /// twice is the same value. Throws on a registry failure; the tray reports
        /// it.
        /// </remarks>
        internal static void Enable()
        {
            string data = "\"" + ExecutablePath + "\"";
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (key == null)
                {
                    throw new InvalidOperationException(
                        "HKCU\\" + RunKeyPath + " could not be opened for writing");
                }

                key.SetValue(ValueName, data, RegistryValueKind.String);
            }
        }

        /// <summary>
        /// Removes the Run entry, leaving every other program's entries alone.
        /// </summary>
        /// <remarks>
        /// Side effects: deletes one value. A missing key or a missing value is a
        /// successful no-op - the requested end state ("no entry") is already true,
        /// which is also why deleting twice cannot fail. Throws on a registry
        /// failure other than absence.
        /// </remarks>
        internal static void Disable()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (key == null)
                {
                    return;
                }

                key.DeleteValue(ValueName, false);
            }
        }

        /// <summary>
        /// Strips surrounding whitespace and double quotes from Run data.
        /// </summary>
        /// <param name="value">Raw REG_SZ data; null and empty are accepted.</param>
        /// <returns>The unquoted path, or an empty string for null/empty input.</returns>
        /// <remarks>
        /// Side effects: none. Windows accepts both quoted and bare Run data, so the
        /// comparison normalises rather than insisting on the exact form
        /// <see cref="Enable"/> writes; a value the user typed by hand then still
        /// reads as "on" when it names this exe.
        /// </remarks>
        private static string Unquote(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            string trimmed = value.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"')
            {
                trimmed = trimmed.Substring(1, trimmed.Length - 2);
            }

            return trimmed;
        }
    }

    /// <summary>
    /// Where recordings land when config has not supplied a folder.
    /// </summary>
    /// <remarks>
    /// Hard-coded to the same default Snipping Tool uses (decision P2). The live
    /// path is <see cref="Program.OutputFolderPath"/>, which honours
    /// <see cref="Config.Folder"/> and only falls back here; this stays the single
    /// place the default is spelled, so the config default and the fallback cannot
    /// drift apart.
    /// </remarks>
    internal static class OutputFolder
    {
        /// <summary>
        /// Returns the default output folder without touching the filesystem.
        /// </summary>
        /// <returns>
        /// <c>%USERPROFILE%\Videos\Screen Recordings</c>, or a path under the
        /// profile if the Videos known folder has been redirected.
        /// </returns>
        internal static string ResolveDefault()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                "Screen Recordings");
        }
    }

    /// <summary>
    /// The on-disk side of config.ini: where it lives, first-run creation, the read
    /// and the write.
    /// </summary>
    /// <remarks>
    /// All filesystem work for the config lives here; interpreting its text is
    /// <see cref="Config.Parse"/> and editing one value is
    /// <see cref="ConfigWriter"/>, both pure and taking the text as an argument.
    /// That split is why the parse and write rules are unit-testable in src\Logic
    /// while this class stays a few lines with no decisions in it (DESIGN.md
    /// section 11).
    /// </remarks>
    internal static class ConfigFile
    {
        /// <summary>
        /// Written on first run: the DESIGN.md section 8 sample verbatim, so the
        /// shipped defaults and the file cannot drift apart. CRLF because this file
        /// is edited in Notepad, and no byte-order mark because the parse treats one
        /// as a special case for the user's sake, not for ours.
        /// </summary>
        private const string SampleText =
            "[record]\r\n"
            + "fps=60\r\n"
            + "quality=19\r\n"
            + "folder=%USERPROFILE%\\Videos\\Screen Recordings\r\n"
            + "ffmpeg_path=\r\n"
            + "mic=default\r\n"
            + "system_audio=off\r\n"
            + "[input]\r\n"
            + "hotkey=Ctrl+Alt+Shift+S\r\n";

        /// <summary>
        /// Full path of the config file: <c>%APPDATA%\SnipVid\config.ini</c>.
        /// </summary>
        /// <returns>An absolute path; Windows always supplies %APPDATA%.</returns>
        /// <remarks>
        /// Side effects: none - this does not create the directory. Kept separate
        /// from <see cref="Load"/> so the location can be logged even when the read
        /// fails.
        /// </remarks>
        internal static string Location()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SnipVid",
                "config.ini");
        }

        /// <summary>
        /// Reads the config file as UTF-8 without dropping a byte-order mark.
        /// </summary>
        /// <param name="path">Absolute path from <see cref="Location"/>.</param>
        /// <returns>The whole file text, including a leading U+FEFF when it has a BOM.</returns>
        /// <exception cref="IOException">The file is missing or unreadable.</exception>
        /// <exception cref="ArgumentException">The bytes are not valid UTF-8.</exception>
        /// <remarks>
        /// Side effects: none. <see cref="File.ReadAllText(string)"/> strips a BOM, and
        /// the write path has to be able to re-emit one, so the bytes are decoded here.
        /// The decoder is strict on purpose: config.ini is human-editable and Notepad
        /// still offers ANSI, so an invalid byte fails the save with a message instead
        /// of being replaced by U+FFFD and destroying the user's text on the next write.
        /// </remarks>
        internal static string ReadText(string path)
        {
            return new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
        }

        /// <summary>
        /// Writes the whole config file as UTF-8, adding no byte-order mark.
        /// </summary>
        /// <param name="path">Absolute path from <see cref="Location"/>.</param>
        /// <param name="text">Complete file content to write.</param>
        /// <exception cref="IOException">The file cannot be written.</exception>
        /// <exception cref="UnauthorizedAccessException">The file is read-only or owned by another user.</exception>
        /// <remarks>
        /// Side effects: replaces the file's bytes. A BOM-less file stays BOM-less; a
        /// leading U+FEFF in the text is encoded as EF BB BF, so a Notepad BOM survives
        /// without the writer inventing one.
        /// </remarks>
        internal static void WriteText(string path, string text)
        {
            File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));
        }

        /// <summary>
        /// Ensures the config file exists, creates it from the DESIGN.md section 8
        /// sample when absent, then parses it.
        /// </summary>
        /// <param name="note">
        /// Sink for a problem that is not a rejected value (an unreadable file), so
        /// it reaches the user by the same route as the parse notes; may be null.
        /// </param>
        /// <returns>
        /// A <see cref="Config"/>; the DESIGN.md section 8 defaults when the file is
        /// absent and could not be written, or exists and cannot be read.
        /// </returns>
        /// <remarks>
        /// Side effects: may create %APPDATA%\SnipVid\ and write config.ini, and
        /// appends startup lines to the log. An existing file is never rewritten -
        /// first run only - because silently replacing a hand-edited file would
        /// discard the settings the user just changed. Never throws: a read or
        /// write failure becomes a note plus defaults, and startup continues, since
        /// a broken config must not leave the user without a tray to quit from.
        /// </remarks>
        internal static Config Load(Action<string> note)
        {
            string location = Location();
            string text = null;

            try
            {
                if (!File.Exists(location))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(location));
                    File.WriteAllText(location, SampleText, new UTF8Encoding(false));
                    AppLog.Line("config: created " + location);
                }
                text = File.ReadAllText(location);
            }
            catch (IOException ex)
            {
                if (note != null)
                {
                    note("config " + location + " could not be read, using defaults: " + ex.Message);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                if (note != null)
                {
                    note("config " + location + " could not be read, using defaults: " + ex.Message);
                }
            }

            // The profile is injected rather than read inside the parser, so this
            // process's environment is the only place it comes from (C7 of WO-04).
            Config config = Config.Parse(text, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            AppLog.Line(
                "config: fps=" + config.Fps.ToString(CultureInfo.InvariantCulture)
                + " quality=" + config.Quality.ToString(CultureInfo.InvariantCulture)
                + " folder=" + config.Folder
                + " ffmpeg_path=" + (config.FfmpegPath.Length == 0 ? "(auto)" : config.FfmpegPath)
                + " mic=" + config.Microphone
                + " hotkey=" + config.Hotkey
                + " notes=" + config.Notes.Count.ToString(CultureInfo.InvariantCulture));
            return config;
        }
    }

    /// <summary>
    /// Arms the hotkey: the configured chord, else Ctrl+Alt+Shift+F9, else
    /// disarmed.
    /// </summary>
    /// <remarks>
    /// This is the whole of WO-06 C2/C3 and the "hotkey already taken" row of
    /// DESIGN.md section 9. It is a separate type from
    /// <see cref="HotkeyWindow"/> so the window stays a dumb message pump and the
    /// three outcomes can be read in one place:
    ///
    ///   configured chord free            -> armed with it
    ///   configured chord taken (1409)    -> armed with Ctrl+Alt+Shift+F9, one note
    ///   both taken                       -> disarmed, one note naming both
    ///
    /// A failure other than 1409 is not a conflict, so the fallback is not tried:
    /// section 9 defines the fallback for "already taken" only, and quietly taking
    /// a different chord after, say, an access error would hide the real fault. That
    /// case disarms with the Win32 error named, and the process keeps running
    /// exactly like the both-taken case.
    ///
    /// Staying alive is the point of the disarm paths: the tray is the only way out
    /// of this app, so no startup condition may prevent quitting.
    /// </remarks>
    internal sealed class HotkeyRegistration : IDisposable
    {
        /// <summary>
        /// The fallback chord (decision P1). Its text also has to be parsed, not
        /// hand-built, because HotkeySpec has no public constructor - which is what
        /// keeps a zero-modifier chord unrepresentable (C13 of WO-04).
        /// </summary>
        private const string FallbackText = "Ctrl+Alt+Shift+F9";

        /// <summary>The parsed fallback chord, built once.</summary>
        private static readonly HotkeySpec FallbackChord = ParseFallback();

        /// <summary>Raised once per press with the chord that is actually registered.</summary>
        private readonly Action<HotkeySpec> _onInvoke;

        /// <summary>The hidden window that owns the registration.</summary>
        private readonly HotkeyWindow _window;

        /// <summary>The registered chord, or null while disarmed.</summary>
        private HotkeySpec _chord;

        /// <summary>Set by <see cref="Dispose"/>; every later call is a no-op.</summary>
        private bool _disposed;

        /// <summary>Creates the hidden window; registration happens in <see cref="Arm"/>.</summary>
        /// <param name="onInvoke">Press handler; may be null.</param>
        /// <remarks>
        /// Side effects: creates the hidden HWND on the calling (UI) thread. Must be
        /// called on that thread - see <see cref="HotkeyWindow"/>.
        /// </remarks>
        private HotkeyRegistration(Action<HotkeySpec> onInvoke)
        {
            _onInvoke = onInvoke;
            _window = new HotkeyWindow(OnHotkey);
        }

        /// <summary>
        /// Arms the hotkey for a parsed config, applying the fallback and disarm
        /// rules.
        /// </summary>
        /// <param name="config">
        /// The parsed config; must not be null (<see cref="Config.Parse"/> never
        /// returns null). Its chord carries the Win32 mask and key already.
        /// </param>
        /// <param name="onInvoke">Press handler; receives the chord actually registered.</param>
        /// <param name="note">
        /// Sink for the one user-facing note each non-trivial outcome produces
        /// (fallback registered, or disarmed). May be null in a headless context;
        /// the log line is written either way.
        /// </param>
        /// <returns>
        /// A registration that is either armed (presses invoke
        /// <paramref name="onInvoke"/>) or disarmed (no registration; presses cannot
        /// arrive). Either way the caller disposes it at shutdown.
        /// </returns>
        /// <remarks>
        /// Side effects: creates a hidden window, may claim a system-wide chord, and
        /// writes one or two log lines. Never throws for a registration failure -
        /// those are the outcomes this method exists to turn into a log line and a
        /// note; only a failure to create the window (no dispatcher on this thread)
        /// escapes, after the window is cleaned up so nothing leaks.
        /// </remarks>
        internal static HotkeyRegistration Arm(Config config, Action<HotkeySpec> onInvoke, Action<string> note)
        {
            HotkeyRegistration registration = new HotkeyRegistration(onInvoke);
            try
            {
                HotkeySpec primary = config.Hotkey;
                int error;

                if (registration.TryArm(primary, out error))
                {
                    AppLog.Line("hotkey: armed " + primary);
                    return registration;
                }

                if (error != Native.ErrorHotkeyAlreadyRegistered)
                {
                    if (note != null)
                    {
                        note("hotkey " + primary + " could not be registered (Windows error "
                            + error + "); the hotkey is disarmed");
                    }
                    AppLog.Line("hotkey: disarmed, " + primary + " failed with windows error " + error);
                    return registration;
                }

                // Already taken by another program: this is the case section 9 gives
                // a fallback for. When the configured chord *is* the fallback there is
                // nothing left to try, and the note must not claim two different
                // chords were rejected.
                bool fallbackIsSameChord = string.Equals(
                    primary.ToString(), FallbackChord.ToString(), StringComparison.OrdinalIgnoreCase);

                if (!fallbackIsSameChord)
                {
                    if (registration.TryArm(FallbackChord, out error))
                    {
                        if (note != null)
                        {
                            note(primary + " is already registered by another program; using "
                                + FallbackChord + " instead");
                        }
                        AppLog.Line("hotkey: armed " + FallbackChord
                            + " (fallback: " + primary + " is already registered)");
                        return registration;
                    }
                }

                if (note != null)
                {
                    note(fallbackIsSameChord
                        ? primary + " is already registered by another program and the fallback chord is the same chord; the hotkey is disarmed, SnipVid stays in the tray"
                        : primary + " and " + FallbackChord + " are both registered by another program; the hotkey is disarmed, SnipVid stays in the tray");
                }
                AppLog.Line("hotkey: disarmed, " + primary
                    + (fallbackIsSameChord
                        ? " is already registered and the fallback chord is the same chord"
                        : " and " + FallbackChord + " are both already registered"));
                return registration;
            }
            catch
            {
                // A half-built registration must not leak the hidden window.
                registration.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Releases the chord and destroys the hidden window. Safe to call more than
        /// once, and safe on a disarmed registration.
        /// </summary>
        /// <remarks>
        /// Side effects: the chord reaches the focused application again and the
        /// release is logged, so "no hotkey left behind after quit" is observable in
        /// the log rather than inferred from the process disappearing.
        /// </remarks>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            HotkeySpec chord = _chord;
            _chord = null;
            _window.Unregister();
            if (chord != null)
            {
                AppLog.Line("hotkey: unregistered " + chord);
            }
            _window.Dispose();
        }

        /// <summary>
        /// Registers one chord and records it as the armed chord.
        /// </summary>
        /// <param name="chord">Chord to claim.</param>
        /// <param name="error">Win32 error from the failing call; 0 on success.</param>
        /// <returns>True when the chord is now owned by this process.</returns>
        /// <remarks>
        /// Side effects: may claim a system-wide chord; releases any previously held
        /// one first (the window does that). On failure the object stays disarmed, so
        /// a press handler can never report a chord that is not actually held.
        /// </remarks>
        private bool TryArm(HotkeySpec chord, out int error)
        {
            if (_window.TryRegister(chord, out error))
            {
                _chord = chord;
                return true;
            }
            _chord = null;
            return false;
        }

        /// <summary>
        /// Window message callback: forwards a press to the caller's handler with
        /// the chord that is actually registered.
        /// </summary>
        /// <remarks>
        /// Side effects: calls <see cref="_onInvoke"/> on the UI thread. The null
        /// check is the disarmed state, where no message can arrive.
        /// </remarks>
        private void OnHotkey()
        {
            HotkeySpec chord = _chord;
            if (chord == null || _onInvoke == null)
            {
                return;
            }
            _onInvoke(chord);
        }

        /// <summary>
        /// Parses <see cref="FallbackText"/> once.
        /// </summary>
        /// <returns>The fallback chord.</returns>
        /// <remarks>
        /// Side effects: none. Throws <see cref="InvalidOperationException"/> when
        /// the literal does not parse - unreachable, because the literal is fixed by
        /// this build and HotkeySpec accepts Ctrl+Alt+Shift+F1..F24, and failing
        /// loudly is better than registering nothing while claiming to have fallen
        /// back.
        /// </remarks>
        private static HotkeySpec ParseFallback()
        {
            HotkeySpec chord;
            if (!HotkeySpec.TryParse(FallbackText, out chord))
            {
                throw new InvalidOperationException(
                    "the built-in fallback chord '" + FallbackText + "' does not parse");
            }
            return chord;
        }
    }

    /// <summary>
    /// Append-only text log next to the executable (bin\SnipVid.log).
    /// </summary>
    /// <remarks>
    /// Deliberately dumb: all callers are on the UI thread. Failures are
    /// swallowed - diagnostics must never be the reason the tray dies.
    /// </remarks>
    internal static class AppLog
    {
        private static readonly string LogPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SnipVid.log");

        /// <summary>Appends one timestamped, newline-terminated line.</summary>
        /// <param name="message">Text to record; null is written as an empty line.</param>
        /// <remarks>
        /// Side effects: creates or appends to bin\SnipVid.log. Failure modes: any
        /// I/O or permission error is ignored, leaving no log entry.
        /// </remarks>
        internal static void Line(string message)
        {
            try
            {
                string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                File.AppendAllText(LogPath, stamp + " " + (message ?? string.Empty) + Environment.NewLine);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
