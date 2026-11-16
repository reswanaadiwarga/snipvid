// StopPill - the small "recording, click to stop" panel (WO-11).
//
// WHY it exists: once the overlay closes there is no visible sign that a capture
// is running, and no way to end it except the hotkey. The pill is that sign and
// that button.
//
// The one non-obvious requirement is capture exclusion. The pill is on screen
// *while* the recording runs, so without help it would appear in every frame.
// SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE) tells DWM - and
// therefore Desktop Duplication, which is what ddagrab reads - to omit this
// window from the composed image entirely (DESIGN.md section 6). This is applied
// with pixels as the acceptance oracle (PLAN.md AC5), not with the API's return
// value, because a TRUE return that still shows the window would be a silently
// broken guarantee.
//
// Two smaller rules are also deliberate:
//
//   * It never takes focus when it appears. ShowActivated is false, so a press
//     of the capture hotkey that ends in a recording does not pull keyboard focus
//     out of whatever the user was typing in. A pill that stole focus would make
//     "record a 5 second demo without touching the app" impossible.
//   * Its timer is derived from a Stopwatch, not incremented per tick. A
//     DispatcherTimer tick is late under load; adding one second per tick would
//     make the displayed duration drift away from the file's duration.
//   * Pause freezes that stopwatch and Resume restarts it. Stopwatch.Stop/Start
//     measure only running time, so the displayed total and the recording agree by
//     construction: paused seconds are never counted, which is the whole point of
//     pausing (DESIGN.md section 2 - the ~600 ms pause/resume cost is retained in
//     wall clock and discarded from the recording). The wall clock is tracked
//     separately only to log the discarded time, which is how "never counts paused
//     time" is observable after the fact.
//
// Escape support is honest about its limit: the pill handles Esc only while it
// has keyboard focus, which it gets only if the user clicks it. Esc is not
// captured system-wide, because a global hook (WH_KEYBOARD_LL) would be a new
// system-wide input surface and swallowing or observing every keystroke for a
// stop button is not worth it. The hotkey is the always-available stop.
//
// Threading: UI thread only. Every member except the StopRequested event's
// invocation is called from the WPF dispatcher.
//
// ASCII-only source: csc falls back to the ANSI codepage for a file that is not
// valid UTF-8.
using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SnipVid
{
    /// <summary>
    /// The recording indicator: elapsed time, a red dot and a Stop button,
    /// excluded from the capture it is timing.
    /// </summary>
    /// <remarks>
    /// One instance per recording, created and shown on the UI thread. It owns a
    /// dispatcher timer and one window; <see cref="Dismiss"/> releases both. It
    /// makes no decision about what stopping means - it reports that the user
    /// asked and lets the caller run the state machine.
    /// </remarks>
    internal sealed class StopPill : Window
    {
        /// <summary>
        /// WDA_EXCLUDEFROMCAPTURE: omit this window from any capture (Win10 2004+).
        /// </summary>
        /// <remarks>
        /// DESIGN.md section 6. This is the value the criterion depends on; the
        /// fallback below is only for a Windows build old enough not to know it.
        /// </remarks>
        private const int WdaExcludeFromCapture = 0x00000011;

        /// <summary>
        /// WDA_MONITOR: the pre-2004 fallback, which renders the window black in the
        /// capture instead of omitting it.
        /// </summary>
        /// <remarks>
        /// Used only when the exclusion call fails, and reported in the log: a black
        /// rectangle in the recording is a visible defect, so the user must be able
        /// to find out it happened.
        /// </remarks>
        private const int WdaMonitor = 0x00000001;

        /// <summary>Width of the pill in DIP; fixed so the position can be computed before layout.</summary>
        /// <remarks>
        /// Wide enough for the elapsed label plus the Pause and Stop buttons. Fixed
        /// so the window can be placed on the right edge before it is measured.
        /// </remarks>
        private const double PillWidthDip = 214.0;

        /// <summary>Height of the pill in DIP; fixed for the same reason.</summary>
        private const double PillHeightDip = 46.0;

        /// <summary>Gap from the work area's bottom-right corner, in DIP.</summary>
        private const double ScreenMarginDip = 24.0;

        /// <summary>Panel fill: near-black at 90 %, matching the overlay toolbar.</summary>
        private static readonly Brush PanelBrush = Frozen(Color.FromArgb(0xE6, 0x1E, 0x1E, 0x1E));

        /// <summary>Border colour, so the pill reads as a panel over any wallpaper.</summary>
        private static readonly Brush EdgeBrush = Frozen(Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3A));

        /// <summary>The recording dot: unmistakably red.</summary>
        private static readonly Brush DotBrush = Frozen(Color.FromArgb(0xFF, 0xFF, 0x4D, 0x4D));

        /// <summary>Stop button fill.</summary>
        private static readonly Brush ButtonBrush = Frozen(Color.FromArgb(0xFF, 0x3B, 0x3B, 0x3B));

        /// <summary>Text colour; white on dark for contrast.</summary>
        private static readonly Brush TextBrush = Brushes.White;

        /// <summary>Shows the elapsed time; replaced once per second.</summary>
        private TextBlock _elapsedText;

        /// <summary>Pause/Resume button: one control, two labels, chosen by <see cref="_paused"/>.</summary>
        /// <remarks>
        /// One button rather than two because the two actions are mutually
        /// exclusive: a separate Resume button could be visible while a pause is
        /// still being finalised, which is exactly the illegal second press
        /// DESIGN.md section 9 rules out. Its screen position is logged by
        /// <see cref="Open"/> so a manual test can click it without guessing.
        /// </remarks>
        private Button _pauseButton;

        /// <summary>Stop button; its position is logged for the same reason as the pause button.</summary>
        private Button _stopButton;

        /// <summary>Drives the elapsed label; null until <see cref="Open"/>.</summary>
        private DispatcherTimer _timer;

        /// <summary>Monotonic source of the elapsed time; measures from <see cref="Open"/>.</summary>
        /// <remarks>
        /// Stopped while paused and restarted on resume, so its value is recorded
        /// time only. This is what makes the label and the joined file agree.
        /// </remarks>
        private Stopwatch _clock;

        /// <summary>Wall clock since <see cref="Open"/>, paused time included.</summary>
        /// <remarks>Only used to report the discarded pause duration in the log.</remarks>
        private Stopwatch _wall;

        /// <summary>True while the elapsed timer is frozen and the button reads Resume.</summary>
        private bool _paused;

        /// <summary>
        /// True when the currently delivered press is the second click of a double
        /// click, so its Click must not toggle the state.
        /// </summary>
        /// <remarks>
        /// WHY this exists: a pause blocks the UI thread for the trailer write
        /// (~90-180 ms measured), so the second press of a double click is delivered
        /// after the pause already completed and would otherwise be a legal Resume -
        /// producing a third segment of a few frames from a double click. Windows
        /// reports that press with ClickCount 2, which is the platform's own
        /// definition of "twice quickly", so no invented timeout is needed
        /// (DESIGN.md section 9, "a queued pause must not exist").
        /// </remarks>
        private bool _suppressPauseClick;

        /// <summary>True once <see cref="StopRequested"/> has been raised, so a hotkey plus a click stop once.</summary>
        private bool _stopRaised;

        /// <summary>Builds the pill; nothing is visible until <see cref="Open"/>.</summary>
        /// <remarks>
        /// Side effects: creates the WPF element tree. Must run on the UI thread,
        /// like every WPF object. No native handle exists until the window is shown.
        /// Callers subscribe to <see cref="StopRequested"/> before calling
        /// <see cref="Open"/>.
        /// </remarks>
        internal StopPill()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Background = PanelBrush;
            BorderBrush = EdgeBrush;
            BorderThickness = new Thickness(1.0);
            Width = PillWidthDip;
            Height = PillHeightDip;

            // The one focus rule that matters: appearing must not move keyboard
            // focus away from the user's work.
            ShowActivated = false;

            BuildContent();
        }

        /// <summary>Raised once per recording when the user asks to stop (button or Esc).</summary>
        /// <remarks>
        /// Raised on the UI thread. The reason string is for the log, not for logic.
        /// </remarks>
        internal event Action<string> StopRequested;

        /// <summary>Raised when the user clicks the button while it reads Pause.</summary>
        /// <remarks>
        /// Raised on the UI thread, at the click - not when the segment has finished
        /// finalising. The subscriber decides whether the press is legal in the
        /// current state and calls <see cref="SetPaused"/> only when it accepts, so
        /// a rejected press (during <c>Pausing</c>) cannot leave the button claiming
        /// a state the recorder is not in.
        /// </remarks>
        internal event Action<string> PauseRequested;

        /// <summary>Raised when the user clicks the button while it reads Resume.</summary>
        /// <remarks>Same contract as <see cref="PauseRequested"/>: the subscriber owns the transition.</remarks>
        internal event Action<string> ResumeRequested;

        /// <summary>True when SetWindowDisplayAffinity accepted WDA_EXCLUDEFROMCAPTURE.</summary>
        /// <remarks>
        /// The AC5 API half; the pixel half is checked against a real recording.
        /// </remarks>
        internal bool AffinityApplied { get; private set; }

        /// <summary>Human-readable affinity outcome, including the Win32 error on failure.</summary>
        /// <remarks>Logged verbatim, so "affinity applied" is never an inference.</remarks>
        internal string AffinityDetail { get; private set; }

        /// <summary>Elapsed time since the pill was shown; zero before <see cref="Open"/>.</summary>
        internal TimeSpan Elapsed
        {
            get { return _clock == null ? TimeSpan.Zero : _clock.Elapsed; }
        }

        /// <summary>
        /// Positions the pill in the work area's bottom-right corner, shows it,
        /// excludes it from capture and starts the timer at 0:00.
        /// </summary>
        /// <remarks>
        /// Side effects: creates and shows a topmost window, claims the exclusion
        /// flag, starts a one-second dispatcher timer, and writes one log line
        /// carrying the bounds, the affinity return value and the foreground window
        /// before and after - the evidence that showing it did not steal focus. The
        /// work area (not the screen) is used so the pill never sits under the
        /// taskbar. The timer restarts at 0:00 in the Pause state, so a pill is
        /// always shown in a known state regardless of what the previous one did.
        /// The two button centres are logged last: they are the only clickable
        /// targets a manual acceptance run has. Failure modes: an affinity failure
        /// is logged, not thrown; recording continues with the fallback attempted,
        /// because a visible pill is better than a recording that never started.
        /// </remarks>
        internal void Open()
        {
            IntPtr foregroundBefore = WindowNative.GetForegroundWindow();

            Rect work = SystemParameters.WorkArea;
            Left = Math.Max(work.Left, work.Right - PillWidthDip - ScreenMarginDip);
            Top = Math.Max(work.Top, work.Bottom - PillHeightDip - ScreenMarginDip);

            Show();
            UpdateLayout();

            IntPtr handle = new WindowInteropHelper(this).Handle;
            bool excluded = WindowNative.SetWindowDisplayAffinity(handle, WdaExcludeFromCapture);
            int affinityError = excluded ? 0 : Marshal.GetLastWin32Error();
            bool fallback = false;

            if (!excluded)
            {
                // Pre-2004 Windows: the window would otherwise be recorded. Ask for
                // the black-rectangle mode so the user at least sees the defect and
                // the log states what happened.
                fallback = WindowNative.SetWindowDisplayAffinity(handle, WdaMonitor);
            }

            AffinityApplied = excluded;
            AffinityDetail = excluded
                ? "WDA_EXCLUDEFROMCAPTURE accepted"
                : "WDA_EXCLUDEFROMCAPTURE failed with windows error "
                    + affinityError.ToString(CultureInfo.InvariantCulture)
                    + (fallback ? "; WDA_MONITOR accepted instead (pill will be black in the capture)"
                                : "; WDA_MONITOR also failed");

            _elapsedText.Text = "0:00";
            _paused = false;
            _suppressPauseClick = false;
            if (_pauseButton != null)
            {
                _pauseButton.Content = "Pause";
                _pauseButton.IsEnabled = true;
            }
            _clock = Stopwatch.StartNew();
            _wall = Stopwatch.StartNew();
            _timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
            _timer.Interval = TimeSpan.FromSeconds(1.0);
            _timer.Tick += OnTick;
            _timer.Start();

            IntPtr foregroundAfter = WindowNative.GetForegroundWindow();
            AppLog.Line(
                "pill: shown hwnd=0x" + handle.ToInt64().ToString("X", CultureInfo.InvariantCulture)
                + " bounds=X=" + Round(Left) + " Y=" + Round(Top)
                + " W=" + Round(Width) + " H=" + Round(Height) + " dip"
                + " label=" + _elapsedText.Text
                + " topmost=true showActivated=false"
                + " affinity=" + AffinityApplied + " (" + AffinityDetail + ")"
                + " foreground before=0x" + foregroundBefore.ToInt64().ToString("X", CultureInfo.InvariantCulture)
                + " after=0x" + foregroundAfter.ToInt64().ToString("X", CultureInfo.InvariantCulture)
                + " unchanged=" + (foregroundBefore == foregroundAfter));

            // The two button centres are logged because they are the only way a
            // manual acceptance run can click them: the pill is a borderless window
            // with no automation name, and its position depends on the work area.
            AppLog.Line("pill: pause button centre=" + Centre(_pauseButton) + " label=Pause");
            AppLog.Line("pill: stop button centre=" + Centre(_stopButton) + " label=Stop");
        }

        /// <summary>
        /// Freezes or resumes the visible timer and swaps the button label.
        /// </summary>
        /// <param name="paused">
        /// True to freeze the timer and show Resume; false to continue from the
        /// frozen value and show Pause.
        /// </param>
        /// <remarks>
        /// Side effects: stops or starts <see cref="_clock"/> and the one-second
        /// dispatcher timer, rewrites the label immediately (a frozen timer that
        /// waits up to a second to show its value looks broken), and writes the log
        /// line that is the C7/C8 evidence: the elapsed value, the wall clock, and
        /// the difference between them, which is the discarded paused time.
        ///
        /// Idempotent: a repeat call for the state it is already in does nothing, so
        /// a double press cannot double-freeze or subtract time twice.
        /// </remarks>
        internal void SetPaused(bool paused)
        {
            if (_clock == null || _paused == paused)
            {
                return;
            }

            _paused = paused;
            if (paused)
            {
                _clock.Stop();
            }
            else
            {
                _clock.Start();
            }

            if (_pauseButton != null)
            {
                _pauseButton.Content = paused ? "Resume" : "Pause";
            }

            if (paused)
            {
                if (_timer != null)
                {
                    _timer.Stop();
                }
            }
            else if (_timer != null)
            {
                _timer.Start();
            }

            UpdateElapsedLabel();
            AppLog.Line(
                "pill: " + (paused ? "paused" : "resumed")
                + " elapsed=" + Format(Elapsed)
                + " elapsedMs=" + ((long)Elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)
                + " wallMs=" + WallMilliseconds().ToString(CultureInfo.InvariantCulture)
                + " pausedMs=" + ((long)PausedFor.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)
                + " label=" + (paused ? "Resume" : "Pause"));
        }

        /// <summary>Time this window has been shown, paused time included.</summary>
        /// <remarks>Side effects: none. Zero before <see cref="Open"/>.</remarks>
        internal TimeSpan Wall
        {
            get { return _wall == null ? TimeSpan.Zero : _wall.Elapsed; }
        }

        /// <summary>Time spent paused: wall clock minus recorded time.</summary>
        /// <remarks>
        /// Side effects: none. Derived rather than accumulated, so it cannot drift
        /// away from the two stopwatches it is computed from.
        /// </remarks>
        internal TimeSpan PausedFor
        {
            get
            {
                TimeSpan wall = Wall;
                TimeSpan recorded = Elapsed;
                return wall > recorded ? wall - recorded : TimeSpan.Zero;
            }
        }

        /// <summary>
        /// Stops the timer, hides the window and releases its handle.
        /// </summary>
        /// <remarks>
        /// Side effects: the pill disappears immediately. Safe to call when the
        /// window was never shown. Hiding before closing is deliberate: the hide is
        /// what removes it from a capture still in progress, and it happens before
        /// the (slower) teardown.
        /// </remarks>
        internal void Dismiss()
        {
            StopTimer();
            if (_clock != null)
            {
                _clock.Stop();
            }
            if (_wall != null)
            {
                _wall.Stop();
            }
            if (IsVisible)
            {
                Hide();
            }
            Close();
        }

        /// <summary>
        /// Esc while the pill has focus is a stop, matching the overlay's cancel key.
        /// </summary>
        /// <param name="e">Key arguments; Esc is marked handled.</param>
        /// <remarks>
        /// Side effects: raises <see cref="StopRequested"/> once. The pill normally
        /// has no focus (ShowActivated is false), so this is the path for a user who
        /// clicked the pill; the hotkey is the always-available stop.
        /// </remarks>
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                RaiseStop("esc");
            }
        }

        /// <summary>
        /// One-second tick: refreshes the label from the stopwatch.
        /// </summary>
        /// <param name="sender">The dispatcher timer (unused).</param>
        /// <param name="e">Event arguments (unused).</param>
        /// <remarks>
        /// Side effects: replaces the label text and writes one log line per second.
        /// The line is the C8 evidence: the timer starts at 0:00 and advances once
        /// per second, which is not visible after the fact without it.
        /// </remarks>
        private void OnTick(object sender, EventArgs e)
        {
            if (_clock == null || _elapsedText == null)
            {
                return;
            }

            UpdateElapsedLabel();
            AppLog.Line(
                "pill: elapsed=" + _elapsedText.Text
                + " at " + ((long)Elapsed.TotalMilliseconds).ToString("0", CultureInfo.InvariantCulture) + " ms"
                + " pausedFor=" + ((long)PausedFor.TotalMilliseconds).ToString("0", CultureInfo.InvariantCulture) + " ms");
        }

        /// <summary>Rewrites the elapsed label from the recorded-time stopwatch.</summary>
        /// <remarks>
        /// Side effects: replaces the label text. Called by the tick and by
        /// <see cref="SetPaused"/> so freezing shows the frozen value at once rather
        /// than up to a second later.
        /// </remarks>
        private void UpdateElapsedLabel()
        {
            if (_elapsedText != null)
            {
                _elapsedText.Text = Format(Elapsed);
            }
        }

        /// <summary>Rounded wall-clock milliseconds since the pill was shown.</summary>
        /// <returns>The value for a log line.</returns>
        /// <remarks>Side effects: none.</remarks>
        private long WallMilliseconds()
        {
            return (long)Wall.TotalMilliseconds;
        }

        /// <summary>Formats a control's centre in screen pixels for a log line.</summary>
        /// <param name="control">A laid-out control inside this window; may be null.</param>
        /// <returns>"X,Y" in physical pixels, or "(not built)" for null.</returns>
        /// <remarks>
        /// Side effects: none. This is evidence, not logic: the manual acceptance run
        /// clicks the pill with injected input, and the only reliable click target is
        /// the one the app itself reports.
        /// </remarks>
        private string Centre(Button control)
        {
            if (control == null)
            {
                return "(not built)";
            }
            Point centre = control.PointToScreen(
                new Point(control.ActualWidth / 2.0, control.ActualHeight / 2.0));
            return Round(centre.X) + "," + Round(centre.Y);
        }

        /// <summary>
        /// Installs the pill's contents: dot, elapsed label, Pause/Resume button,
        /// Stop button.
        /// </summary>
        /// <remarks>
        /// Side effects: creates the WPF elements and wires both buttons. Neither is
        /// focusable, so a click cannot pull keyboard focus; the panel around them
        /// still can, which is what makes Esc reachable after a click on the pill's
        /// body. The pause button is built first and stored because both its label
        /// and its screen position are reported by <see cref="Open"/> and
        /// <see cref="SetPaused"/>.
        /// </remarks>
        private void BuildContent()
        {
            StackPanel row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };

            Ellipse dot = new Ellipse
            {
                Width = 9.0,
                Height = 9.0,
                Fill = DotBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            row.Children.Add(dot);

            _elapsedText = new TextBlock
            {
                Text = "0:00",
                Foreground = TextBrush,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 14.0,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 40.0,
            };
            row.Children.Add(_elapsedText);

            _pauseButton = new Button
            {
                Content = "Pause",
                Foreground = TextBrush,
                Background = ButtonBrush,
                BorderThickness = new Thickness(0),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12.0,
                Padding = new Thickness(10, 2, 10, 2),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false,
                Cursor = Cursors.Hand,
            };
            _pauseButton.Click += OnPauseClicked;
            _pauseButton.PreviewMouseLeftButtonDown += OnPauseButtonPreviewDown;
            row.Children.Add(_pauseButton);

            _stopButton = new Button
            {
                Content = "Stop",
                Foreground = TextBrush,
                Background = ButtonBrush,
                BorderThickness = new Thickness(0),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12.0,
                Padding = new Thickness(10, 2, 10, 2),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false,
                Cursor = Cursors.Hand,
            };
            _stopButton.Click += OnStopClicked;
            row.Children.Add(_stopButton);

            Content = new Border
            {
                Background = PanelBrush,
                Padding = new Thickness(10, 0, 10, 0),
                Child = row,
            };
        }

        /// <summary>
        /// Pause/Resume button handler: reports which of the two the user asked for.
        /// </summary>
        /// <param name="sender">The button (unused).</param>
        /// <param name="e">Event arguments (unused).</param>
        /// <remarks>
        /// Side effects: raises exactly one of <see cref="PauseRequested"/> and
        /// <see cref="ResumeRequested"/>, or nothing when the press was the second
        /// click of a double click (see <see cref="_suppressPauseClick"/>). It does
        /// not change the label itself: only <see cref="SetPaused"/> does, and only
        /// the subscriber calls that, so a press the state machine rejects cannot
        /// leave the button lying about the state.
        /// </remarks>
        private void OnPauseClicked(object sender, RoutedEventArgs e)
        {
            if (_suppressPauseClick)
            {
                _suppressPauseClick = false;
                return;
            }

            if (_paused)
            {
                Raise("resume", ResumeRequested);
            }
            else
            {
                Raise("pause", PauseRequested);
            }
        }

        /// <summary>
        /// Marks the second click of a double click so its Click handler does nothing.
        /// </summary>
        /// <param name="sender">The pause button (unused).</param>
        /// <param name="e">Mouse arguments; only <c>ClickCount</c> is used.</param>
        /// <remarks>
        /// Side effects: sets <see cref="_suppressPauseClick"/> and writes one log
        /// line when the press is the second of a double click. The press is dropped
        /// rather than queued: one pause per gesture, one child per segment.
        /// </remarks>
        private void OnPauseButtonPreviewDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount > 1)
            {
                _suppressPauseClick = true;
                AppLog.Line(
                    "pill: pause press ignored, click count " + e.ClickCount
                    + " (a second press is dropped, never queued)");
            }
        }

        /// <summary>Invokes one optional subscriber, swallowing its exception.</summary>
        /// <param name="reason">Short reason for the caller's log line.</param>
        /// <param name="handler">Event to raise; null is a no-op.</param>
        /// <remarks>
        /// Side effects: calls the subscriber on the UI thread. A click handler must
        /// not take the window - and therefore the recording - down, so an exception
        /// is logged and dropped.
        /// </remarks>
        private static void Raise(string reason, Action<string> handler)
        {
            if (handler == null)
            {
                return;
            }
            try
            {
                handler(reason);
            }
            catch (Exception ex)
            {
                AppLog.Line("pill: " + reason + " handler failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Stop button handler: reports the request and disables the button.</summary>
        /// <param name="sender">The button (unused).</param>
        /// <param name="e">Event arguments (unused).</param>
        /// <remarks>
        /// Side effects: raises <see cref="StopRequested"/> once. Disabling on the
        /// first click is redundant with the once-only guard, but it also tells the
        /// user the press registered while ffmpeg writes its trailer.
        /// </remarks>
        private void OnStopClicked(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            if (button != null)
            {
                button.IsEnabled = false;
            }
            RaiseStop("pill");
        }

        /// <summary>Raises <see cref="StopRequested"/> at most once per recording.</summary>
        /// <param name="reason">Short reason for the caller's log line.</param>
        /// <remarks>
        /// Side effects: calls the subscriber on the UI thread. Swallows a subscriber
        /// exception: a click handler must not take the window (and the recording)
        /// down.
        /// </remarks>
        private void RaiseStop(string reason)
        {
            if (_stopRaised)
            {
                return;
            }
            _stopRaised = true;

            Action<string> handler = StopRequested;
            if (handler != null)
            {
                try
                {
                    handler(reason);
                }
                catch (Exception ex)
                {
                    AppLog.Line("pill: stop handler failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        /// <summary>Stops the dispatcher timer and forgets it.</summary>
        /// <remarks>Side effects: no further tick can update a closing window.</remarks>
        private void StopTimer()
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Tick -= OnTick;
                _timer = null;
            }
        }

        /// <summary>Formats elapsed time as m:ss, rounded to the nearest second.</summary>
        /// <param name="elapsed">Time since the pill was shown.</param>
        /// <returns>For example "0:07" or "12:05".</returns>
        /// <remarks>
        /// Side effects: none. Invariant culture, so digit shapes never change the
        /// label. Rounded rather than truncated because a DispatcherTimer tick lands
        /// a few milliseconds either side of its interval (measured: the first tick
        /// at 986 ms), and truncating that tick would show 0:00 twice in a row - a
        /// timer that visibly does not advance once per second (C8).
        /// </remarks>
        private static string Format(TimeSpan elapsed)
        {
            int totalSeconds = (int)Math.Round(elapsed.TotalSeconds, MidpointRounding.AwayFromZero);
            if (totalSeconds < 0)
            {
                totalSeconds = 0;
            }
            return (totalSeconds / 60).ToString(CultureInfo.InvariantCulture)
                + ":" + (totalSeconds % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>Rounds a DIP value for a log line.</summary>
        /// <param name="value">Value to round.</param>
        /// <returns>The value with no fractional part, invariant culture.</returns>
        /// <remarks>Side effects: none. Bounds are logged whole because a fractional DIP makes a log noisy without adding information.</remarks>
        private static string Round(double value)
        {
            return Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        }

        /// <summary>Freezes a brush so it can be shared without WPF treating it as mutable state.</summary>
        /// <param name="color">Colour to wrap.</param>
        /// <returns>A frozen solid brush.</returns>
        /// <remarks>Side effects: none.</remarks>
        private static Brush Frozen(Color color)
        {
            SolidColorBrush brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// The two user32 calls this window needs: capture exclusion and the
        /// foreground-window probe used as focus evidence.
        /// </summary>
        /// <remarks>
        /// Declared in this file rather than in Native.cs because the dispatch scope
        /// for this task covers only the recorder, the job and the pill. Both are
        /// read-only queries about this process's own window, apart from the one
        /// affinity write.
        /// </remarks>
        private static class WindowNative
        {
            /// <summary>Reports or changes whether a window may appear in a capture.</summary>
            /// <param name="window">This window's handle.</param>
            /// <param name="affinity">0 to allow capture, 0x11 to exclude, 0x1 to render black.</param>
            /// <returns>True when the request was accepted.</returns>
            /// <remarks>
            /// Side effects: changes how DWM composites this window for every capture
            /// API, including Desktop Duplication. Failure modes: false for an invalid
            /// handle or an unsupported affinity value; the last error says which.
            /// </remarks>
            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetWindowDisplayAffinity(IntPtr window, int affinity);

            /// <summary>Returns the window that currently has keyboard focus system-wide.</summary>
            /// <returns>The foreground window handle, or zero.</returns>
            /// <remarks>
            /// Side effects: none. Used only to prove that showing the pill did not
            /// move focus: the handle before and after must match.
            /// </remarks>
            [DllImport("user32.dll")]
            internal static extern IntPtr GetForegroundWindow();
        }
    }
}
