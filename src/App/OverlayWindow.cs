// OverlayWindow - the full-screen region picker (WO-07).
//
// WHY one window rather than one per monitor: DESIGN.md section 3 specifies a
// single borderless window spanning the virtual screen, because a drag must be
// able to cross monitor boundaries visually even though v1 clamps the resulting
// capture to the monitor it started on (section 4). A per-monitor window would
// need cross-window mouse capture for the same gesture.
//
// Build order in the constructor matters and is not cosmetic:
//   1. a transparent full-window rectangle, because a WPF element with no Fill is
//      not hit-testable - without it, every click in the dim would fall through
//      to the desktop and the overlay would be inert;
//   2. the dim layer, a Path whose geometry excludes the selection (a
//      CombinedGeometry with GeometryCombineMode.Exclude), which is how the
//      interior becomes genuinely unobscured rather than merely lightened;
//   3. the selection visuals (border, 3 px grips, live size badge) in a Canvas,
//      hit-test-disabled so they can never swallow a drag;
//   4. the toolbar, last so it sits above the dim and is clickable.
//
// The window's own Background is Transparent, not the DESIGN.md section 3
// table's #8C000000, because a window background is painted everywhere and would
// dim the punched hole as well. The 55 % dim lives on the geometry instead; the
// colour constant is the same, so what the user sees is the specified 55 % dim
// with a clear selection.
//
// Frames: everything in this file is DIP (WPF's frame). Physical pixels appear
// only in values returned by Geometry.ToPhysical, which is the one place the
// DIP-to-physical conversion is allowed to happen (DESIGN.md section 4). The
// badge shows exactly the numbers the record line logs, both taken from the same
// computation, so a badge cannot disagree with the capture.
//
// State machine (DESIGN.md section 2, the Overlay row):
//   shown -> dragging -> selected        (usable rectangle, Record enabled)
//   shown -> dragging -> too small       (badge red, Record disabled)
//   any   -> cancelled  (Esc, right-click, single click under 4 DIP, Cancel)
//   selected -> accepted (Enter, R, Record) -> window hidden, then the rect is
//               logged and handed to the caller
// Esc/right-click/single-click close with nothing spawned (AC3). A single click is
// distinguished from a rejected small drag by movement distance, never by outcome
// (WORKORDERS WO-07 note): under 4 DIP is a click, 4..15 DIP is a rejected drag
// that leaves the overlay open with a red badge.
//
// The settings panel is a second, local state inside the overlay: the toolbar's
// gear opens a panel below the toolbar with a Quality section and a Frame rate
// section, Esc closes that panel and only a second Esc closes the overlay, and
// clicking a row raises the caller's callback. The panel and each of its rows
// swallow mouse-down exactly as the toolbar does, so a press aimed at a row can
// never start - and therefore never cancel - a selection. The panel reads both
// current values from the caller instead of storing copies, so it can only ever
// mark a value the caller would actually record with.
//
// The settings panel's third section is "Save to", the folder the next recording
// will be written to. It shows only the last path segment, because a full path
// would make the panel wide enough to cover the toolbar; the full configured path
// is the row's tooltip. Clicking it opens a folder picker - and that is the one
// gesture in this file that has to hide the window it lives in: the overlay is
// topmost and full-screen, so a dialog shown while it is visible would be behind
// it and unusable. The order is fixed: remember the committed selection, the
// record-enabled state and the panel state; Hide(); show the dialog; Show() and
// redraw from the remembered rectangle; only then report a chosen path. A cancel
// therefore leaves the user exactly where they were, and the selection is restored
// from the stored rectangle rather than recomputed from a pointer that may have
// moved while the dialog was up.
//
// The microphone toolbar item is a picker: the short "Mic" label with a filled
// or hollow circle shows whether audio is on, the tooltip names the active
// device, and clicking opens a panel of every active capture endpoint plus
// Default device and Off - the same shape as the Quality panel, with the current
// choice marked. A chosen row raises the caller's callback and the toolbar is
// then redrawn from the caller's state, so a failed config save snaps back
// instead of marking a value the file does not hold. Only one panel is open at
// a time.
//
// The System toolbar item is a toggle, not a picker: WASAPI loopback follows the
// default render endpoint, so there is no device to choose - only on or off. Its
// tooltip names the endpoint and states the one limitation (switching output
// devices mid-recording silences that segment), because that is where the user
// looks before recording.
//
// Threading: UI thread only. Mouse and keyboard handlers run there, and so does
// Close(), which is what makes "the log line proves the window is gone before
// anything else happens" (C7) an ordering fact rather than a race.
//
// ASCII-only source: glyphs are written as \u escapes so the visual layer is
// correct while the file stays 7-bit (csc falls back to the ANSI codepage for a
// file that is not valid UTF-8, which would silently corrupt a literal).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SnipVid.Logic;
// System.Windows.Media also defines a Geometry, and both are needed in this file:
// the alias keeps the capture maths (ToPhysical, IsUsable, NormaliseDrag)
// unambiguous at every call site, and a bare Geometry is then always the Logic
// one or a compile error rather than a silent mismatch.
using Geometry = SnipVid.Logic.Geometry;

namespace SnipVid
{
    /// <summary>
    /// Full-virtual-screen region picker: 55 % dim, drag-to-select with a live
    /// size badge, a toolbar with Record / Cancel, a microphone picker, a
    /// system-audio toggle, and a settings panel with Quality, Frame rate and
    /// Save to sections.
    /// </summary>
    /// <remarks>
    /// One instance per capture; it is not reused, so no state can leak from the
    /// previous drag into the next one. Disposing it is not required beyond
    /// closing it: it owns no native handles and no timers.
    /// </remarks>
    internal sealed class OverlayWindow : Window
    {
        /// <summary>
        /// Movement, in DIP, below which a press-and-release counts as a click and
        /// therefore cancels (DESIGN.md section 3: under 4 DIP).
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="Geometry.MinimumSize"/>: this one decides
        /// "click, so cancel" and that one decides "too small to record". A 15 DIP
        /// drag is over this threshold and under the minimum, which is exactly the
        /// C6 case, so the two numbers must not be collapsed into one.
        /// </remarks>
        private const double DragThresholdDip = 4.0;

        /// <summary>Gap between the pointer and the size badge, in DIP.</summary>
        private const double BadgeGapDip = 12.0;

        /// <summary>Size of the corner and edge grips, in DIP (DESIGN.md section 3: 3 px).</summary>
        private const double GripSizeDip = 3.0;

        /// <summary>Distance from the top of the screen to the toolbar, in DIP.</summary>
        private const double ToolbarTopDip = 40.0;

        /// <summary>Distance from the toolbar's bottom edge to the quality panel, in DIP.</summary>
        private const double QualityPanelGapDip = 8.0;

        /// <summary>
        /// Longest device name shown in a microphone row, in characters.
        /// </summary>
        /// <remarks>
        /// Windows device names have no length bound and this panel is a fixed,
        /// centred popup under the toolbar. 46 characters fits every name on this
        /// machine with room to spare; anything longer keeps its distinguishing start
        /// and gains "..." - the full name is in the row's tooltip.
        /// </remarks>
        private const int MicLabelMaxChars = 46;

        /// <summary>
        /// The three presets the quality panel offers, in ascending -cq: better
        /// quality and larger files first, matching the row order in the design.
        /// </summary>
        /// <remarks>
        /// One table feeds both the rows and the label handed to the callback, so
        /// the name Program logs can never disagree with the row that was clicked.
        /// 19 is DESIGN.md section 5's visually-lossless default for UI content;
        /// 22 and 25 trade detail for size and are the only other presets the panel
        /// offers. A hand-edited value outside this table is shown as "Custom"
        /// rather than snapped to the nearest preset.
        /// </remarks>
        private static readonly QualityPreset[] QualityPresets = new QualityPreset[]
        {
            new QualityPreset(19, "Best quality"),
            new QualityPreset(22, "Balanced"),
            new QualityPreset(25, "Smaller files"),
        };

        /// <summary>
        /// The two frame rates the settings panel offers, in the order DESIGN.md
        /// section 3 prints them: the default first, the explicit alternative below.
        /// </summary>
        /// <remarks>
        /// 60 is the default and 30 is for a 30 fps source, where capturing at 60
        /// stores every frame twice (decision D1 as amended). There is deliberately
        /// no 120/144 row: those are refused by the parser and the argv builder.
        /// A hand-edited value outside this table is shown as "Custom" and is not
        /// applied.
        /// </remarks>
        private static readonly FpsPreset[] FpsPresets = new FpsPreset[]
        {
            new FpsPreset(60, "60 fps (default)"),
            new FpsPreset(30, "30 fps (half the frames)"),
        };

        /// <summary>
        /// The 55 % dim from DESIGN.md section 3 (#8C000000): alpha 0x8C, black.
        /// </summary>
        /// <remarks>
        /// Applied to the holed geometry rather than to Window.Background - see the
        /// file header for why a background cannot have a hole.
        /// </remarks>
        private static readonly Color DimColor = Color.FromArgb(0x8C, 0x00, 0x00, 0x00);

        /// <summary>Accent used for the live Record control.</summary>
        private static readonly Brush AccentBrush = Frozen(Color.FromRgb(0x6F, 0xB7, 0xFF));

        /// <summary>Muted colour for Cancel, which is not the primary action.</summary>
        private static readonly Brush MutedBrush = Frozen(Color.FromRgb(0xD0, 0xD0, 0xD0));

        /// <summary>
        /// Colour of the audio items while they are off: dimmer than Muted so "not
        /// recording" reads as inactive rather than merely secondary.
        /// </summary>
        private static readonly Brush DisabledBrush = Frozen(Color.FromRgb(0x7A, 0x7A, 0x7A));

        /// <summary>Toolbar panel and badge backing: near-black at 90 % opacity.</summary>
        private static readonly Brush PanelBrush = Frozen(Color.FromArgb(0xE6, 0x1E, 0x1E, 0x1E));

        /// <summary>
        /// Badge backing while the selection is too small to record: deep red, so
        /// the rejection is visible without reading the number.
        /// </summary>
        private static readonly Brush BadgeErrorBrush = Frozen(Color.FromArgb(0xE6, 0xB0, 0x00, 0x20));

        /// <summary>The monitors enumerated at startup; the drag origin picks one.</summary>
        private readonly List<MonitorInfo> _monitors;

        /// <summary>
        /// Called once, after the window is closed and the record line is written,
        /// with the monitor-relative physical rectangle and the monitor it belongs
        /// to. Null means the result is logged only.
        /// </summary>
        private readonly Action<PxRect, MonitorInfo> _onAccepted;

        /// <summary>Dim layer with the selection excluded; hit-test-disabled.</summary>
        private Path _dim;

        /// <summary>Canvas holding the selection border, grips and badge (DIP coordinates).</summary>
        private Canvas _selectionLayer;

        /// <summary>1 px white outline of the selection.</summary>
        private Rectangle _border;

        /// <summary>Eight 3 px grips: four corners, four edge midpoints.</summary>
        private Rectangle[] _grips;

        /// <summary>The live size badge panel.</summary>
        private Border _badge;

        /// <summary>The badge's text, replaced on every pointer move.</summary>
        private TextBlock _badgeText;

        /// <summary>The Record control's panel, dimmed while the selection is unusable.</summary>
        private Border _recordPanel;

        /// <summary>The Record label, recoloured when the control is disabled.</summary>
        private TextBlock _recordText;

        /// <summary>
        /// The toolbar's row of items. Kept so <see cref="LogToolbar"/> can count
        /// what was actually built instead of restating a number.
        /// </summary>
        private StackPanel _toolbarRow;

        /// <summary>The toolbar panel, kept so its laid-out rectangle can be logged.</summary>
        private Border _toolbarPanel;

        /// <summary>
        /// Reads the current NVENC -cq so the panel can mark the matching preset.
        /// </summary>
        /// <remarks>
        /// A reader rather than a stored value, because the setting lives in exactly
        /// one place (Program) and a copy kept here could keep claiming a preset after
        /// a save failed. May be null; then nothing is marked.
        /// </remarks>
        private readonly Func<int> _currentQuality;

        /// <summary>
        /// Called once with a chosen preset's -cq and its label when a row is clicked.
        /// </summary>
        /// <remarks>
        /// The label travels with the value so the party writing the config file does
        /// not need a second copy of the preset table to name what it saved.
        /// </remarks>
        private readonly Action<int, string> _onQualityChosen;

        /// <summary>
        /// Reads the frame rate the settings panel should mark: 60, 30, or a
        /// rejected value the file asked for (for example 24), so the panel can show
        /// it as Custom.
        /// </summary>
        /// <remarks>
        /// A reader for the same reason <see cref="_currentQuality"/> is one: the
        /// value lives in exactly one place (Program), and a copy here could keep
        /// marking a row after a save failed. May be null; then no row is marked.
        /// </remarks>
        private readonly Func<int> _currentFps;

        /// <summary>Called once with a chosen frame rate and its label when a row is clicked.</summary>
        private readonly Action<int, string> _onFpsChosen;

        /// <summary>
        /// Reads the folder the next recording will be written to, so the Save to
        /// row shows the path actually in effect.
        /// </summary>
        /// <remarks>
        /// A reader for the same reason as the other settings: the answer lives in
        /// exactly one place (Program), and a copy here could keep showing a folder
        /// after the config write that was supposed to change it failed. May be null,
        /// which reads as "not set".
        /// </remarks>
        private readonly Func<string> _currentFolder;

        /// <summary>
        /// Called once with the absolute folder picked in the dialog.
        /// </summary>
        /// <remarks>
        /// The overlay does not adopt the path itself: the callback writes it to the
        /// config file, and the row is redrawn from <see cref="_currentFolder"/> after
        /// this returns, so a rejected or failed save cannot leave the panel showing a
        /// folder the file does not hold. May be null, in which case the dialog is
        /// still shown but nothing is saved.
        /// </remarks>
        private readonly Action<string> _onFolderChosen;

        /// <summary>
        /// Reads the microphone setting in effect: <see cref="Config.MicOff"/>,
        /// <see cref="Config.MicDefault"/>, or an exact device name.
        /// </summary>
        /// <remarks>
        /// A reader rather than a stored copy for the same reason as the quality and
        /// frame-rate readers: the answer lives in exactly one place (Program), and
        /// a copy here could keep marking a row after the config write that was
        /// supposed to make it true failed. May be null; then nothing is marked and
        /// the item reads off.
        /// </remarks>
        private readonly Func<string> _currentMic;

        /// <summary>
        /// Reads the active capture endpoints for the microphone panel's device rows.
        /// </summary>
        /// <remarks>
        /// Called on every panel open rather than once at construction: the user may
        /// plug a device in between two captures, and a list built at startup would
        /// silently omit it. May be null; then the panel shows only Default and Off.
        /// </remarks>
        private readonly Func<List<string>> _micDevices;

        /// <summary>
        /// Reads the microphone item's tooltip: the active device, or why there is none.
        /// </summary>
        /// <remarks>
        /// Read whenever the item is drawn, so a choice made in the panel is reflected
        /// in the tooltip without reopening the overlay. May be null; then the item
        /// gets a generic tooltip.
        /// </remarks>
        private readonly Func<string> _micTooltip;

        /// <summary>
        /// Called with the chosen mic value (<see cref="Config.MicOff"/>,
        /// <see cref="Config.MicDefault"/> or an exact device name) when a row is clicked.
        /// </summary>
        /// <remarks>
        /// The overlay does not keep its own copy of the choice: it re-reads
        /// <see cref="_currentMic"/> after this returns, so a rejected or failed save
        /// cannot leave the toolbar marking something that will not happen. May be
        /// null, in which case the rows are shown but inert.
        /// </remarks>
        private readonly Action<string> _onMicChosen;

        /// <summary>
        /// Reads whether system audio (WASAPI loopback) is in effect.
        /// </summary>
        /// <remarks>
        /// A reader rather than a stored copy, like the other settings: the answer
        /// lives in Program, and a copy here could keep claiming "on" after a failed
        /// config write. May be null, which reads as off.
        /// </remarks>
        private readonly Func<bool> _currentSystemAudio;

        /// <summary>
        /// Reads the System item's tooltip: the render endpoint being captured, or
        /// why there is none.
        /// </summary>
        /// <remarks>
        /// Read whenever the item is drawn, so a toggle names the device immediately.
        /// May be null; then the item gets a generic tooltip.
        /// </remarks>
        private readonly Func<string> _systemTooltip;

        /// <summary>
        /// Called with the new state when the System item is clicked.
        /// </summary>
        /// <remarks>
        /// System audio is a plain on/off toggle, not a picker: loopback follows the
        /// default render endpoint and there is no device to choose. May be null, in
        /// which case the item is drawn but inert.
        /// </remarks>
        private readonly Action<bool> _onSystemAudioChosen;

        /// <summary>The System item's label, recoloured as the setting changes.</summary>
        private TextBlock _systemText;

        /// <summary>The System item's clickable panel, kept so its tooltip can be replaced.</summary>
        private Border _systemItem;

        /// <summary>The microphone item's label, recoloured as the setting changes.</summary>
        private TextBlock _micText;

        /// <summary>The microphone item's clickable panel, kept so its tooltip can be replaced.</summary>
        private Border _micItem;

        /// <summary>The microphone device panel below the toolbar; hidden until Mic is clicked.</summary>
        private Border _microphonePanel;

        /// <summary>True while the microphone panel is visible; Esc closes it first.</summary>
        private bool _microphoneOpen;

        /// <summary>The settings panel below the toolbar; hidden until the gear is clicked.</summary>
        private Border _qualityPanel;

        /// <summary>True while the settings panel is visible; Esc uses it to close the panel first.</summary>
        private bool _qualityOpen;

        /// <summary>True between a left-button press and its release.</summary>
        private bool _dragging;

        /// <summary>Last pointer position in window DIP; the badge follows it.</summary>
        private Point _pointer;

        /// <summary>Where the current drag started, in window DIP (the capture frame's origin).</summary>
        private Point _dragOrigin;

        /// <summary>The current drag as an ordered DIP rectangle.</summary>
        private DipRect _selection;

        /// <summary>
        /// Monitor containing the drag origin, fixed for the life of the drag
        /// (WO-08: the origin picks the monitor, not the pointer's current
        /// position).
        /// </summary>
        private MonitorInfo _monitor;

        /// <summary>True when a drag has been committed and its rectangle is usable.</summary>
        private bool _recordEnabled;

        /// <summary>Set as soon as the window is closing, so no queued event can act twice.</summary>
        private bool _finished;

        /// <summary>
        /// Builds the overlay and its contents. Nothing is shown until
        /// <see cref="Open"/>.
        /// </summary>
        /// <param name="monitors">
        /// Startup monitor list from <see cref="Monitors.Enumerate"/>; a null or
        /// empty list degrades to "no monitor known", in which case a drag is
        /// refused rather than converted with a made-up scale.
        /// </param>
        /// <param name="onAccepted">
        /// Invoked once with the capture rectangle and its monitor, after the
        /// window is closed and the record line is logged. May be null.
        /// </param>
        /// <param name="currentQuality">
        /// Reads the NVENC -cq the next recording would use, so the quality panel can
        /// mark the preset that is actually in effect. May be null, in which case no
        /// preset is marked.
        /// </param>
        /// <param name="onQualityChosen">
        /// Invoked with a chosen preset's -cq and its label when a row is clicked.
        /// May be null, in which case the panel shows the presets but does nothing.
        /// </param>
        /// <param name="currentFps">
        /// Reads the frame rate to mark in the panel: 60, 30, or a rejected value
        /// the file asked for so it can be shown as Custom. May be null.
        /// </param>
        /// <param name="onFpsChosen">
        /// Invoked with a chosen frame rate and its label when a row is clicked. May
        /// be null, in which case the rows are shown but inert.
        /// </param>
        /// <param name="currentFolder">
        /// Reads the folder the next recording will be written to, so the Save to row
        /// shows the path in effect. May be null, which reads as "not set".
        /// </param>
        /// <param name="onFolderChosen">
        /// Invoked once with the absolute folder chosen in the dialog. May be null, in
        /// which case the dialog is shown but nothing is saved.
        /// </param>
        /// <param name="currentMic">
        /// Reads the microphone setting in effect ("off", "default", or an exact
        /// device name) so the panel marks the row the next recording will use.
        /// May be null, which reads as off.
        /// </param>
        /// <param name="micDevices">
        /// Reads the active capture endpoints for the panel's device rows. Called on
        /// every panel open, so a device plugged in after launch appears. May be
        /// null, in which case the panel shows only Default device and Off.
        /// </param>
        /// <param name="micTooltip">
        /// Reads the microphone item's tooltip (the active device, or why there is
        /// none). May be null.
        /// </param>
        /// <param name="onMicChosen">
        /// Invoked with the chosen value ("off", "default", or an exact device name)
        /// when a row is clicked. May be null, in which case the rows are inert.
        /// </param>
        /// <param name="currentSystemAudio">
        /// Reads whether system audio is in effect, so the System item is drawn on or
        /// off. May be null, which reads as off.
        /// </param>
        /// <param name="systemTooltip">
        /// Reads the System item's tooltip (the render endpoint, or why there is
        /// none). May be null.
        /// </param>
        /// <param name="onSystemAudioChosen">
        /// Invoked with the new state when the System item is clicked. May be null, in
        /// which case the item is inert.
        /// </param>
        /// <remarks>
        /// Side effects: creates the WPF element tree and installs handlers. Must
        /// run on the UI thread, like every WPF object. Does not touch the screen
        /// until shown. The microphone readers are called during construction, so
        /// the caller must have resolved the default device before building the
        /// overlay if the tooltip is to name it. The system-audio readers are called
        /// the same way, for the same reason. The folder reader is read when the
        /// settings panel is opened, never at construction, because nothing outside
        /// that panel depends on it.
        /// </remarks>
        internal OverlayWindow(
            List<MonitorInfo> monitors,
            Action<PxRect, MonitorInfo> onAccepted,
            Func<int> currentQuality,
            Action<int, string> onQualityChosen,
            Func<int> currentFps,
            Action<int, string> onFpsChosen,
            Func<string> currentFolder,
            Action<string> onFolderChosen,
            Func<string> currentMic,
            Func<List<string>> micDevices,
            Func<string> micTooltip,
            Action<string> onMicChosen,
            Func<bool> currentSystemAudio,
            Func<string> systemTooltip,
            Action<bool> onSystemAudioChosen)
        {
            _monitors = monitors ?? new List<MonitorInfo>();
            _onAccepted = onAccepted;
            _currentQuality = currentQuality;
            _onQualityChosen = onQualityChosen;
            _currentFps = currentFps;
            _onFpsChosen = onFpsChosen;
            _currentFolder = currentFolder;
            _onFolderChosen = onFolderChosen;
            _currentMic = currentMic;
            _micDevices = micDevices;
            _micTooltip = micTooltip;
            _onMicChosen = onMicChosen;
            _currentSystemAudio = currentSystemAudio;
            _systemTooltip = systemTooltip;
            _onSystemAudioChosen = onSystemAudioChosen;

            // DESIGN.md section 3, row by row. AllowsTransparency plus
            // WindowStyle None is the only combination WPF allows for a real alpha
            // dim; Topmost keeps the overlay above the window it is covering;
            // ShowInTaskbar false keeps it out of the taskbar for a gesture that
            // lasts seconds.
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Cursor = Cursors.Cross;
            Focusable = true;

            // The overlay spans the whole virtual screen, not one monitor, so a
            // drag that begins on the primary display still redraws smoothly when
            // the pointer wanders onto a second one. SystemParameters reports
            // these in DIP, which is the frame the mouse events arrive in.
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;

            BuildContent();
            SizeChanged += OnSizeChanged;
            Closed += OnWindowClosed;
        }

        /// <summary>
        /// Shows the overlay, lays it out and takes keyboard focus.
        /// </summary>
        /// <remarks>
        /// Side effects: makes the window visible over every other window, prints
        /// nothing itself, and changes focus to the overlay - which is required,
        /// because Esc/Enter/R are handled by this window and a window without
        /// keyboard focus never receives them. The window is Topmost, so taking
        /// focus cannot reorder it below the window it is covering (C10).
        /// The caller writes the "overlay: shown" line, because only the caller
        /// holds the hotkey timestamp that makes the C1 latency measurable.
        /// </remarks>
        internal void Open()
        {
            Show();
            UpdateLayout();
            UpdateGeometry();
            Activate();
            Keyboard.Focus(this);
            LogToolbar();
        }

        /// <summary>
        /// Determines whether a monitor is known, so the caller can refuse a
        /// capture instead of guessing a scale.
        /// </summary>
        /// <remarks>Side effects: none.</remarks>
        internal bool HasMonitors
        {
            get { return _monitors.Count > 0; }
        }

        /// <summary>
        /// Installs the four content layers described in the file header.
        /// </summary>
        /// <remarks>
        /// Side effects: creates WPF elements and wires mouse handlers. Called once
        /// from the constructor, before the window has a size.
        /// </remarks>
        private void BuildContent()
        {
            Grid root = new Grid();

            // Layer 0: the hit-test surface. A Window whose content has no filled
            // element reports no hits at all, so this rectangle - invisible, but
            // filled with a brush rather than null - is what makes the whole
            // virtual screen clickable (C10).
            Rectangle hitSurface = new Rectangle { Fill = Brushes.Transparent };
            root.Children.Add(hitSurface);

            // Layer 1: the dim with the selection punched out of it.
            _dim = new Path
            {
                Fill = Frozen(DimColor),
                IsHitTestVisible = false,
            };
            root.Children.Add(_dim);

            // Layer 2: selection visuals. Hit-test-disabled as a whole so the
            // border, grips and badge never intercept the drag that drew them.
            _selectionLayer = new Canvas { IsHitTestVisible = false };
            root.Children.Add(_selectionLayer);

            _border = new Rectangle
            {
                Stroke = Brushes.White,
                StrokeThickness = 1.0,
                Fill = Brushes.Transparent,
                Visibility = Visibility.Collapsed,
            };
            _selectionLayer.Children.Add(_border);

            _grips = new Rectangle[8];
            for (int i = 0; i < _grips.Length; i++)
            {
                _grips[i] = new Rectangle
                {
                    Fill = Brushes.White,
                    Width = GripSizeDip,
                    Height = GripSizeDip,
                    Visibility = Visibility.Collapsed,
                };
                _selectionLayer.Children.Add(_grips[i]);
            }

            _badgeText = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 12.0,
                FontFamily = new FontFamily("Segoe UI"),
            };
            _badge = new Border
            {
                Background = PanelBrush,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 3, 6, 3),
                Child = _badgeText,
                Visibility = Visibility.Collapsed,
            };
            _selectionLayer.Children.Add(_badge);

            // Layer 3: toolbar, 40 DIP below the top of the screen and centred.
            root.Children.Add(BuildToolbar());

            // Layer 4: the two panels (settings and microphone). Last so they paint
            // over the dim and the selection, and hidden until their item is
            // clicked. Only one is ever open, and opening one closes the other.
            root.Children.Add(BuildQualityPanel());
            root.Children.Add(BuildMicrophonePanel());

            Content = root;

            MouseLeftButtonDown += OnLeftDown;
            MouseLeftButtonUp += OnLeftUp;
            MouseMove += OnPointerMoved;
            MouseRightButtonDown += OnRightDown;
        }

        /// <summary>
        /// Builds the toolbar: Record, Cancel, the microphone picker, the
        /// system-audio toggle and the gear that opens the settings panel.
        /// </summary>
        /// <returns>
        /// The panel to add to the window's content. Horizontal alignment centres
        /// it; the top margin is <see cref="ToolbarTopDip"/>, which is "40 px below
        /// the top of the screen" because the window's top edge is the screen's.
        /// </returns>
        /// <remarks>
        /// Side effects: creates the elements and wires Record, Cancel, the mic item,
        /// the system toggle and the gear to their code paths. Both audio items are
        /// real controls now: the mic opens a device list, the System item flips one
        /// boolean.
        /// The gear is muted like Cancel because it is a setting rather than the
        /// primary action, and it carries the word "Quality" next to the glyph so
        /// the control is legible even if U+2699 falls back to a font that draws it
        /// poorly.
        /// </remarks>
        private FrameworkElement BuildToolbar()
        {
            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal };
            _toolbarRow = row;

            _recordText = new TextBlock
            {
                Text = "\u25CF Record",
                Foreground = AccentBrush,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13.0,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _recordPanel = ToolbarItem(row, _recordText, OnRecordClicked, 0.0);

            TextBlock cancelText = new TextBlock
            {
                Text = "\u2715 Cancel",
                Foreground = MutedBrush,
                FontSize = 13.0,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolbarItem(row, cancelText, OnCancelClicked, 14.0);

            // Two audio items, not one: mic and system audio are separate sources
            // and the toolbar mirrors the layout the user already knows. The mic is a
            // picker (it opens a device list); system audio is a plain on/off toggle
            // because WASAPI loopback follows the default render endpoint.
            BuildMicItem(row);
            BuildSystemItem(row);

            // The gear sits after the audio items, so the items the user
            // already knows keep their positions.
            TextBlock gearText = new TextBlock
            {
                Text = "\u2699 Quality",
                Foreground = MutedBrush,
                FontSize = 13.0,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolbarItem(row, gearText, OnGearClicked, 14.0);

            Border panel = new Border
            {
                Background = PanelBrush,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6, 10, 6),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, ToolbarTopDip, 0, 0),
                Child = row,
            };

            // The panel is a control, not dim: a press on its padding or on the
            // gap between two items must not start a selection drag, because that
            // would cancel the overlay (a zero-distance drag is a click) when the
            // user was aiming at a button.
            panel.MouseLeftButtonDown += OnControlPanelLeftDown;

            _toolbarPanel = panel;
            return panel;
        }

        /// <summary>
        /// Swallows a left-button press that landed on a control panel (the toolbar
        /// or the settings panel) rather than on the dim.
        /// </summary>
        /// <param name="sender">The panel (unused).</param>
        /// <param name="e">Mouse arguments; marked handled so no drag starts.</param>
        /// <remarks>
        /// Side effects: none beyond marking the event handled. Clicks that land
        /// on a real item are handled by that item's own handler, which runs first.
        /// The handler is shared because the rule is the same for both panels: a
        /// press on a control must never become a selection, whose zero-distance
        /// release would read as a click and cancel the overlay.
        /// </remarks>
        private void OnControlPanelLeftDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        /// <summary>
        /// Wraps one label in a padded, clickable panel and adds it to the toolbar.
        /// </summary>
        /// <param name="row">Toolbar row to append to.</param>
        /// <param name="label">The label; its colours are set by the caller.</param>
        /// <param name="onClick">Click handler; null leaves the item inert.</param>
        /// <param name="leftGap">Left margin in DIP, used to separate the items.</param>
        /// <returns>The panel, so the caller can restyle it later.</returns>
        /// <remarks>
        /// Side effects: appends to <paramref name="row"/> and installs the click
        /// handler. The handler sets Handled so a toolbar click cannot also start a
        /// drag on the dim behind it.
        /// </remarks>
        private Border ToolbarItem(StackPanel row, TextBlock label, MouseButtonEventHandler onClick, double leftGap)
        {
            Border panel = new Border
            {
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(leftGap, 0, 0, 0),
                Cursor = Cursors.Hand,
                Child = label,
            };
            if (onClick != null)
            {
                panel.MouseLeftButtonUp += onClick;
            }
            row.Children.Add(panel);
            return panel;
        }

        /// <summary>
        /// Adds the microphone toolbar item: a picker that opens a device panel.
        /// </summary>
        /// <param name="row">Toolbar row to append to.</param>
        /// <remarks>
        /// Side effects: appends one clickable item and installs its handler; calls
        /// the caller's setting and tooltip readers once so the label starts correct.
        /// The label stays short ("Mic") and the active device lives in the tooltip,
        /// because a device name can be long enough to push the toolbar off-centre.
        /// </remarks>
        private void BuildMicItem(StackPanel row)
        {
            _micText = new TextBlock
            {
                Text = MicLabel(false),
                Foreground = DisabledBrush,
                FontSize = 13.0,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _micItem = ToolbarItem(row, _micText, OnMicClicked, 14.0);
            ApplyMicState();
        }

        /// <summary>
        /// Draws the microphone item from the caller's current setting and refreshes
        /// its tooltip.
        /// </summary>
        /// <remarks>
        /// Side effects: sets the item's text, colour and tooltip, and logs one line
        /// when the setting could not be read. Called from the constructor and after
        /// a row is chosen, so the visible state and the logged state cannot
        /// disagree. Filled circle and bright text mean audio is on; hollow circle
        /// and muted text mean off - the same glyph convention the settings panel
        /// uses for its marked row.
        /// </remarks>
        private void ApplyMicState()
        {
            string setting = CurrentMicSetting();
            bool on = !IsMicOff(setting);
            if (_micText != null)
            {
                _micText.Text = MicLabel(on);
                _micText.Foreground = on ? MutedBrush : DisabledBrush;
            }
            if (_micItem != null)
            {
                ToolTipService.SetToolTip(_micItem, MicTooltipText());
            }
        }

        /// <summary>Formats the microphone label for one state.</summary>
        /// <param name="on">True for filled, false for hollow.</param>
        /// <returns>"\u25CF Mic" when on, "\u25CB Mic" when off.</returns>
        /// <remarks>
        /// Side effects: none. Deliberately no device name here: the item has to fit
        /// beside Record/Cancel/System/Quality, and a Windows device name has no
        /// length bound. The tooltip carries the name.
        /// </remarks>
        private static string MicLabel(bool on)
        {
            return (on ? "\u25CF " : "\u25CB ") + "Mic";
        }

        /// <summary>True when the setting means "record no audio".</summary>
        /// <param name="setting">Value from the caller's reader; may be null.</param>
        /// <returns>True only for <see cref="Config.MicOff"/>, ignoring case.</returns>
        /// <remarks>
        /// Side effects: none. Ignoring case here as well as in the parser means a
        /// reader that hands back the file's raw spelling still draws correctly.
        /// </remarks>
        private static bool IsMicOff(string setting)
        {
            return string.Equals(setting, Config.MicOff, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Reads the microphone item's tooltip from the caller.</summary>
        /// <returns>
        /// The caller's text (the active device, or why there is none), or a generic
        /// "Microphone" when no reader was supplied. Never null or empty.
        /// </returns>
        /// <remarks>
        /// Side effects: invokes the caller's reader. No reader, or a throwing one,
        /// falls back to the generic label: a tooltip is a courtesy and must not be
        /// able to break the overlay.
        /// </remarks>
        private string MicTooltipText()
        {
            Func<string> source = _micTooltip;
            if (source == null)
            {
                return "Microphone";
            }
            try
            {
                string text = source();
                return string.IsNullOrEmpty(text) ? "Microphone" : text;
            }
            catch (Exception ex)
            {
                AppLog.Line("mic: tooltip unreadable " + ex.GetType().Name + ": " + ex.Message);
                return "Microphone";
            }
        }

        /// <summary>Reads the microphone setting in effect from the caller.</summary>
        /// <returns>
        /// "off", "default" or an exact device name. Unknown (null/empty) reads as
        /// <see cref="Config.MicOff"/>, the safe answer: a toolbar that claimed audio
        /// that will not be recorded is worse than one that understates it.
        /// </returns>
        /// <remarks>
        /// Side effects: invokes the caller's reader. No reader, or a throwing one,
        /// reports off and writes one log line.
        /// </remarks>
        private string CurrentMicSetting()
        {
            Func<string> source = _currentMic;
            if (source == null)
            {
                return Config.MicOff;
            }
            try
            {
                string setting = source();
                return string.IsNullOrEmpty(setting) ? Config.MicOff : setting;
            }
            catch (Exception ex)
            {
                AppLog.Line("mic: current value unreadable " + ex.GetType().Name + ": " + ex.Message);
                return Config.MicOff;
            }
        }

        /// <summary>Reads the active capture endpoints from the caller.</summary>
        /// <returns>A list, possibly empty; never null.</returns>
        /// <remarks>
        /// Side effects: invokes the caller's reader, which enumerates Core Audio.
        /// Called once per panel open, never per row and never at construction. A
        /// failure is one log line plus an empty list, so the panel still shows
        /// Default device and Off - the two choices that need no hardware list.
        /// </remarks>
        private List<string> MicDeviceList()
        {
            Func<List<string>> source = _micDevices;
            if (source == null)
            {
                return new List<string>();
            }
            try
            {
                List<string> devices = source();
                return devices ?? new List<string>();
            }
            catch (Exception ex)
            {
                AppLog.Line("mic: device list unreadable " + ex.GetType().Name + ": " + ex.Message);
                return new List<string>();
            }
        }

        /// <summary>
        /// Handles a click on the microphone item: opens the device panel.
        /// </summary>
        /// <param name="sender">The microphone item (unused).</param>
        /// <param name="e">Mouse arguments; marked handled so no drag starts.</param>
        /// <remarks>
        /// Side effects: closes the Quality panel when it is open, then shows the
        /// microphone panel. Only one panel is open at a time: two stacked panels
        /// would overlap below the same toolbar, and Esc could only close one of
        /// them. The selection is untouched - opening a picker is not a cancel.
        /// </remarks>
        private void OnMicClicked(object sender, MouseButtonEventArgs e)
        {
            if (_finished)
            {
                return;
            }
            e.Handled = true;
            OpenMicrophonePanel();
        }

        /// <summary>
        /// Adds the system-audio toolbar item: a plain on/off toggle.
        /// </summary>
        /// <param name="row">Toolbar row to append to.</param>
        /// <remarks>
        /// Side effects: appends one clickable item, installs its handler and calls
        /// the caller's readers once so the label and tooltip start correct. Unlike
        /// the mic there is no panel: loopback always follows the default render
        /// endpoint, so the only choice the user has is on or off, and the tooltip
        /// names the endpoint being captured.
        /// </remarks>
        private void BuildSystemItem(StackPanel row)
        {
            _systemText = new TextBlock
            {
                Text = SystemLabel(false),
                Foreground = DisabledBrush,
                FontSize = 13.0,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _systemItem = ToolbarItem(row, _systemText, OnSystemClicked, 14.0);
            ApplySystemState();
        }

        /// <summary>
        /// Draws the System item from the caller's current setting and refreshes its
        /// tooltip.
        /// </summary>
        /// <remarks>
        /// Side effects: sets the item's text, colour and tooltip and logs one line
        /// when the setting could not be read. Called from the constructor and after a
        /// toggle, so the visible state and the setting that will be recorded cannot
        /// disagree. Filled circle and bright text mean on; hollow and muted mean off -
        /// the same glyph convention as Mic and the panel rows.
        /// </remarks>
        private void ApplySystemState()
        {
            bool on = CurrentSystemAudioOn();
            if (_systemText != null)
            {
                _systemText.Text = SystemLabel(on);
                _systemText.Foreground = on ? MutedBrush : DisabledBrush;
            }
            if (_systemItem != null)
            {
                ToolTipService.SetToolTip(_systemItem, SystemTooltipText());
            }
        }

        /// <summary>Formats the System label for one state.</summary>
        /// <param name="on">True for filled, false for hollow.</param>
        /// <returns>"\u25CF System" when on, "\u25CB System" when off.</returns>
        /// <remarks>
        /// Side effects: none. No device name here: real endpoint names have no length
        /// bound and the toolbar must stay centred. The tooltip carries the name.
        /// </remarks>
        private static string SystemLabel(bool on)
        {
            return (on ? "\u25CF " : "\u25CB ") + "System";
        }

        /// <summary>Reads whether system audio is in effect from the caller.</summary>
        /// <returns>True only when the caller reports on; unknown reads as off.</returns>
        /// <remarks>
        /// Side effects: invokes the caller's reader. No reader, or a throwing one,
        /// reports off and writes one log line: claiming audio that will not be
        /// recorded is worse than understating it.
        /// </remarks>
        private bool CurrentSystemAudioOn()
        {
            Func<bool> source = _currentSystemAudio;
            if (source == null)
            {
                return false;
            }
            try
            {
                return source();
            }
            catch (Exception ex)
            {
                AppLog.Line("system_audio: current value unreadable " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>Reads the System item's tooltip from the caller.</summary>
        /// <returns>The caller's text, or a generic "System audio" when none was supplied.</returns>
        /// <remarks>
        /// Side effects: invokes the caller's reader. A tooltip is a courtesy and must
        /// not be able to break the overlay, so a throwing reader falls back to the
        /// generic label.
        /// </remarks>
        private string SystemTooltipText()
        {
            Func<string> source = _systemTooltip;
            if (source == null)
            {
                return "System audio";
            }
            try
            {
                string text = source();
                return string.IsNullOrEmpty(text) ? "System audio" : text;
            }
            catch (Exception ex)
            {
                AppLog.Line("system_audio: tooltip unreadable " + ex.GetType().Name + ": " + ex.Message);
                return "System audio";
            }
        }

        /// <summary>
        /// Handles a click on the System item: reports the opposite of the current
        /// state, then redraws from the caller's answer.
        /// </summary>
        /// <param name="sender">The System item (unused).</param>
        /// <param name="e">Mouse arguments; marked handled so no drag starts.</param>
        /// <remarks>
        /// Side effects: writes one log line, invokes the caller's callback (which
        /// saves the config and updates its own state), then reruns
        /// <see cref="ApplySystemState"/>. Order matters: the item is redrawn from the
        /// caller, so a failed config write snaps it back instead of marking a state
        /// the file does not hold. The selection is untouched - toggling audio is not
        /// a cancel. A throwing callback is logged rather than allowed to escape into
        /// a mouse handler.
        /// </remarks>
        private void OnSystemClicked(object sender, MouseButtonEventArgs e)
        {
            if (_finished)
            {
                return;
            }
            e.Handled = true;

            bool next = !CurrentSystemAudioOn();
            AppLog.Line("overlay: system audio toggle requested on=" + (next ? "true" : "false"));

            Action<bool> chosen = _onSystemAudioChosen;
            if (chosen != null)
            {
                try
                {
                    chosen(next);
                }
                catch (Exception ex)
                {
                    AppLog.Line("system_audio: callback failed " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            ApplySystemState();
        }

        /// <summary>
        /// Builds the settings panel: an empty, hidden frame below the toolbar that
        /// <see cref="OpenQualityPanel"/> fills with the current quality, frame-rate and
        /// save-folder rows.
        /// </summary>
        /// <returns>The panel to add to the window's content.</returns>
        /// <remarks>
        /// Side effects: creates the panel and installs the shared mouse-down
        /// swallow. Rows are rebuilt on every open instead of being restyled, because
        /// the marking has to follow the values in effect at that moment; a stale row
        /// could claim a setting that is not in effect.
        /// </remarks>
        private FrameworkElement BuildQualityPanel()
        {
            _qualityPanel = new Border
            {
                Background = PanelBrush,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 8, 12, 8),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed,
            };
            _qualityPanel.MouseLeftButtonDown += OnControlPanelLeftDown;
            return _qualityPanel;
        }

        /// <summary>
        /// Opens the settings panel, rebuilt from the current quality, frame rate and
        /// save folder, under the toolbar.
        /// </summary>
        /// <remarks>
        /// Side effects: closes the microphone panel when it is open (only one panel
        /// at a time), builds the rows, shows the panel, runs two layout passes (one
        /// so the toolbar has real bounds, one so the panel lands under them) and
        /// writes one log line with the marking and the panel's bounds. The selection
        /// is not touched: the panel is an extra control, not a new gesture. A second
        /// gear click while it is open is logged and ignored rather than toggling,
        /// because closing is Esc's job and a toggle would give one click two
        /// meanings.
        /// </remarks>
        private void OpenQualityPanel()
        {
            if (_finished)
            {
                return;
            }
            if (_qualityOpen)
            {
                AppLog.Line("overlay: quality panel already open; ignored");
                return;
            }

            // Two panels would stack below the same toolbar and Esc could only
            // close the top one; opening either closes the other.
            CloseMicrophonePanel("quality panel opened");

            int current = CurrentQuality();
            bool custom = current >= 1 && PresetIndex(current) < 0;
            int currentFps = CurrentFps();
            bool customFps = currentFps >= 1 && FpsIndex(currentFps) < 0;
            string folder = CurrentFolder();

            int qualityRows;
            int fpsRows;
            int folderRows;
            _qualityPanel.Child = BuildPanelContent(
                current, custom, currentFps, customFps, folder,
                out qualityRows, out fpsRows, out folderRows);
            _qualityOpen = true;
            _qualityPanel.Visibility = Visibility.Visible;
            UpdateLayout();
            PlacePanel(_qualityPanel);
            UpdateLayout();

            AppLog.Line(
                "overlay: quality panel opened cq=" + QualityText(current)
                + " presets=" + QualityPresets.Length.ToString(CultureInfo.InvariantCulture)
                + " custom=" + (custom ? "true" : "false")
                + " fps=" + FpsText(currentFps)
                + " fpsPresets=" + FpsPresets.Length.ToString(CultureInfo.InvariantCulture)
                + " fpsCustom=" + (customFps ? "true" : "false")
                + " folder=" + folder
                + " folderLabel=" + FolderLabel(folder)
                + " qualityRows=" + qualityRows.ToString(CultureInfo.InvariantCulture)
                + " fpsRows=" + fpsRows.ToString(CultureInfo.InvariantCulture)
                + " folderRows=" + folderRows.ToString(CultureInfo.InvariantCulture)
                + " bounds=" + BoundsOf(_qualityPanel));
        }

        /// <summary>
        /// Rebuilds the settings panel's rows in place, keeping it open.
        /// </summary>
        /// <remarks>
        /// Side effects: replaces the panel's child with a freshly built tree, runs
        /// two layout passes and writes one log line when it was open. Used after a
        /// folder is saved so the Save to row shows the new path immediately; the
        /// panel is not closed and reopened, because the user is in the middle of
        /// configuring and the selection must stay untouched. Does nothing when the
        /// panel is closed - the next open rebuilds it from the readers anyway.
        /// </remarks>
        private void RefreshQualityPanel()
        {
            if (!_qualityOpen || _finished)
            {
                return;
            }

            int current = CurrentQuality();
            bool custom = current >= 1 && PresetIndex(current) < 0;
            int currentFps = CurrentFps();
            bool customFps = currentFps >= 1 && FpsIndex(currentFps) < 0;
            string folder = CurrentFolder();

            int qualityRows;
            int fpsRows;
            int folderRows;
            _qualityPanel.Child = BuildPanelContent(
                current, custom, currentFps, customFps, folder,
                out qualityRows, out fpsRows, out folderRows);
            UpdateLayout();
            PlacePanel(_qualityPanel);
            UpdateLayout();

            AppLog.Line(
                "overlay: quality panel refreshed folder=" + folder
                + " folderLabel=" + FolderLabel(folder)
                + " folderRows=" + folderRows.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Hides the settings panel and forgets its rows.
        /// </summary>
        /// <param name="reason">Short reason for the log, for example "esc" or "chose cq=22".</param>
        /// <remarks>
        /// Side effects: collapses the panel, drops the row tree (the next open
        /// rebuilds it) and writes one log line. The selection and the overlay stay
        /// exactly as they were, which is what "Esc closes the panel first" means:
        /// the first Esc is not a cancel.
        /// </remarks>
        private void CloseQualityPanel(string reason)
        {
            if (!_qualityOpen)
            {
                return;
            }
            _qualityOpen = false;
            _qualityPanel.Visibility = Visibility.Collapsed;
            _qualityPanel.Child = null;
            AppLog.Line("overlay: quality panel closed reason=" + reason);
        }

        /// <summary>
        /// Builds the microphone panel: an empty, hidden frame below the toolbar that
        /// <see cref="OpenMicrophonePanel"/> fills with the device rows.
        /// </summary>
        /// <returns>The panel to add to the window's content.</returns>
        /// <remarks>
        /// Side effects: creates the panel and installs the shared mouse-down
        /// swallow. Rows are rebuilt on every open because the device list and the
        /// marked setting both change at run time; a cached row tree could offer a
        /// device that was unplugged five minutes ago.
        /// </remarks>
        private FrameworkElement BuildMicrophonePanel()
        {
            _microphonePanel = new Border
            {
                Background = PanelBrush,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 8, 12, 8),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed,
            };
            _microphonePanel.MouseLeftButtonDown += OnControlPanelLeftDown;
            return _microphonePanel;
        }

        /// <summary>
        /// Opens the microphone panel, rebuilt from the active devices and the current
        /// setting, under the toolbar.
        /// </summary>
        /// <remarks>
        /// Side effects: closes the Quality panel when it is open (only one panel at a
        /// time), enumerates the capture endpoints through the caller's reader, builds
        /// the rows, shows the panel, runs two layout passes and writes one log line
        /// naming the setting, the device count, the row count and the panel's bounds.
        /// A second Mic click while it is open is logged and ignored for the same
        /// reason the gear behaves that way: closing is Esc's job.
        /// </remarks>
        private void OpenMicrophonePanel()
        {
            if (_finished)
            {
                return;
            }
            if (_microphoneOpen)
            {
                AppLog.Line("overlay: microphone panel already open; ignored");
                return;
            }

            CloseQualityPanel("microphone panel opened");

            string current = CurrentMicSetting();
            List<string> devices = MicDeviceList();

            int rows;
            _microphonePanel.Child = BuildMicrophonePanelContent(current, devices, out rows);
            _microphoneOpen = true;
            _microphonePanel.Visibility = Visibility.Visible;
            UpdateLayout();
            PlacePanel(_microphonePanel);
            UpdateLayout();

            AppLog.Line(
                "overlay: microphone panel opened mic=" + current
                + " devices=" + devices.Count.ToString(CultureInfo.InvariantCulture)
                + " rows=" + rows.ToString(CultureInfo.InvariantCulture)
                + " bounds=" + BoundsOf(_microphonePanel));
        }

        /// <summary>
        /// Hides the microphone panel and forgets its rows.
        /// </summary>
        /// <param name="reason">Short reason for the log, for example "esc" or "chose mic=off".</param>
        /// <remarks>
        /// Side effects: collapses the panel, drops the row tree (the next open
        /// rebuilds it) and writes one log line when it was actually open. The
        /// selection and the overlay stay exactly as they were, which is what "Esc
        /// closes the panel first" means.
        /// </remarks>
        private void CloseMicrophonePanel(string reason)
        {
            if (!_microphoneOpen)
            {
                return;
            }
            _microphoneOpen = false;
            _microphonePanel.Visibility = Visibility.Collapsed;
            _microphonePanel.Child = null;
            AppLog.Line("overlay: microphone panel closed reason=" + reason);
        }

        /// <summary>
        /// Builds the microphone panel: one row per active device, a separator, then
        /// Default device and Off.
        /// </summary>
        /// <param name="current">
        /// Setting in effect: <see cref="Config.MicOff"/>,
        /// <see cref="Config.MicDefault"/> or an exact device name.
        /// </param>
        /// <param name="devices">Active capture endpoints from the caller; may be empty.</param>
        /// <param name="rows">Receives the number of rows built, for the log line.</param>
        /// <returns>The stack to put inside the panel.</returns>
        /// <remarks>
        /// Side effects: creates elements and wires each row's click. Marking comes
        /// from <paramref name="current"/> alone, so the panel can never claim a
        /// device that is not the one the recorder would use. Three things can be
        /// marked and only one of them is: the matching device row, Default device,
        /// or Off.
        ///
        /// A configured device that is no longer in the active list gets a marked
        /// "(not connected)" row above the separator. Without it the panel would show
        /// nothing marked while the file still names a device, and the user could not
        /// tell why the next recording will fall back to the default.
        /// </remarks>
        private UIElement BuildMicrophonePanelContent(
            string current, List<string> devices, out int rows)
        {
            if (devices == null)
            {
                devices = new List<string>();
            }
            StackPanel content = new StackPanel();
            content.Children.Add(SectionTitle("Microphone", 0.0));

            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            rows = 0;
            bool off = string.Equals(current, Config.MicOff, StringComparison.OrdinalIgnoreCase);
            bool byDefault =
                string.Equals(current, Config.MicDefault, StringComparison.OrdinalIgnoreCase);

            int markedDevice = -1;
            for (int i = 0; i < devices.Count; i++)
            {
                if (!off && !byDefault && SameDevice(devices[i], current))
                {
                    markedDevice = i;
                }
            }

            for (int i = 0; i < devices.Count; i++)
            {
                string name = devices[i];
                AddMicRow(grid, i == markedDevice, name, Truncate(name, MicLabelMaxChars), name);
                rows++;
            }

            if (!off && !byDefault && markedDevice < 0)
            {
                // The file names a device Windows does not list: unplugged, renamed,
                // or disabled. Mark it so the setting is visible, and say what will
                // happen instead - the recording falls back to the default.
                AddMicRow(
                    grid,
                    true,
                    Truncate(current, MicLabelMaxChars) + " (not connected)",
                    current,
                    current);
                rows++;
            }

            content.Children.Add(grid);

            // A separator, because the two rows below are not devices: one is a
            // policy ("ask Windows") and one is a state ("no audio").
            content.Children.Add(new Border
            {
                Height = 1.0,
                Background = DisabledBrush,
                Opacity = 0.5,
                Margin = new Thickness(0, 6, 0, 6),
            });

            Grid choices = new Grid();
            choices.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            choices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            AddMicRow(choices, byDefault, "Default device", Config.MicDefault, null);
            rows++;
            AddMicRow(choices, off, "Off", Config.MicOff, null);
            rows++;
            content.Children.Add(choices);

            return content;
        }

        /// <summary>
        /// Adds one row to the microphone panel: a marker, a label and a hit target.
        /// </summary>
        /// <param name="grid">Panel grid receiving the row.</param>
        /// <param name="marked">True for the row describing the current setting.</param>
        /// <param name="label">Row text without its marker, already truncated for display.</param>
        /// <param name="value">Value this row selects: "off", "default" or an exact device name.</param>
        /// <param name="tooltip">Full text for the tooltip, or null when the label is complete.</param>
        /// <remarks>
        /// Side effects: appends grid elements and installs the click handlers. The
        /// visible label is hit-test-disabled and a transparent border spans the row
        /// on top of it, so a press anywhere on the row is swallowed instead of
        /// starting a drag. The tooltip carries the untruncated device name: a name
        /// cut for layout must still be readable somewhere.
        /// </remarks>
        private void AddMicRow(Grid grid, bool marked, string label, string value, string tooltip)
        {
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock text = new TextBlock
            {
                Text = (marked ? "\u25CF " : "\u25CB ") + label,
                Foreground = marked ? Brushes.White : MutedBrush,
                FontSize = 13.0,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);

            Border hit = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(0, 2, 0, 2),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            Grid.SetRow(hit, row);
            Grid.SetColumn(hit, 0);
            Grid.SetColumnSpan(hit, 2);

            if (tooltip != null)
            {
                ToolTipService.SetToolTip(hit, tooltip);
            }

            string chosen = value;
            hit.MouseLeftButtonDown += OnRowLeftDown;
            hit.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                OnMicRowChosen(e, chosen);
            };

            grid.Children.Add(hit);
        }

        /// <summary>
        /// Handles a click on a microphone row: closes the panel, reports the choice,
        /// then redraws the toolbar from the caller's answer.
        /// </summary>
        /// <param name="e">Mouse arguments; marked handled.</param>
        /// <param name="value">"off", "default" or an exact device name.</param>
        /// <remarks>
        /// Side effects: hides the panel, writes one log line, invokes the caller's
        /// callback (which writes config and updates its own state), then reruns
        /// <see cref="ApplyMicState"/>. Order matters: the toolbar is redrawn from
        /// the caller, so a failed config write snaps the item back to the setting
        /// that is actually in effect instead of marking a value the file does not
        /// hold. A throwing callback is logged rather than allowed to escape into a
        /// mouse handler. The selection is untouched - choosing a device is not a
        /// cancel.
        /// </remarks>
        private void OnMicRowChosen(MouseButtonEventArgs e, string value)
        {
            if (_finished)
            {
                return;
            }
            e.Handled = true;

            CloseMicrophonePanel("chose mic=" + value);
            AppLog.Line("overlay: microphone chose mic=" + value);

            Action<string> chosen = _onMicChosen;
            if (chosen != null)
            {
                try
                {
                    chosen(value);
                }
                catch (Exception ex)
                {
                    AppLog.Line("mic: callback failed " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            ApplyMicState();
        }

        /// <summary>True when two device names name the same endpoint, ignoring case.</summary>
        /// <param name="left">One device name.</param>
        /// <param name="right">The other.</param>
        /// <returns>True when both are non-empty and equal ignoring case.</returns>
        /// <remarks>
        /// Side effects: none. Case-insensitive because a hand-edited file can spell
        /// a device differently from Windows; dshow itself also matches names
        /// case-insensitively, so this mirrors what the recorder can actually use.
        /// </remarks>
        private static bool SameDevice(string left, string right)
        {
            return !string.IsNullOrEmpty(left)
                && !string.IsNullOrEmpty(right)
                && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Shortens a device name for a panel row, keeping the start of the name.
        /// </summary>
        /// <param name="text">Full text; may be null.</param>
        /// <param name="maxChars">Maximum visible characters, ellipsis included.</param>
        /// <returns>The text unchanged when it fits, else its start plus "...".</returns>
        /// <remarks>
        /// Side effects: none. A Windows device name has no length bound and the panel
        /// is a fixed, centred popup; truncating the start keeps the distinguishing
        /// words ("Microphone (Realtek...") visible, which is what a user scans for.
        /// The full name stays in the row's tooltip, so nothing is lost.
        /// </remarks>
        private static string Truncate(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            {
                return text;
            }
            return text.Substring(0, maxChars - 3) + "...";
        }

        /// <summary>
        /// Builds the panel's Quality, Frame rate and Save to sections: one row per
        /// preset plus a Custom row when the current value is not one of them, then the
        /// one row that opens the folder picker.
        /// </summary>
        /// <param name="current">The -cq in effect; below 1 means the caller could not say.</param>
        /// <param name="custom">True when a "Custom (cq N)" row must mark the value.</param>
        /// <param name="currentFps">The frame rate to mark: 60, 30, or a requested value.</param>
        /// <param name="customFps">True when a "Custom (N fps)" row must mark the value.</param>
        /// <param name="folder">Folder in effect, shown as the Save to row's last segment.</param>
        /// <param name="qualityRows">Receives the number of quality rows built, for the log line.</param>
        /// <param name="fpsRows">Receives the number of frame-rate rows built, for the log line.</param>
        /// <param name="folderRows">Receives the number of Save to rows built, for the log line.</param>
        /// <returns>The stack to put inside the panel.</returns>
        /// <remarks>
        /// Side effects: creates elements and wires the preset rows' clicks. Marking
        /// comes from the readers alone, so a value outside a table marks no preset
        /// and adds the custom row instead - the UI never claims a setting the file
        /// does not hold. The frame-rate section sits below Quality because quality
        /// is the lever for size (decision D2); 30 fps exists only for a 30 fps
        /// source. Save to comes last, below the two settings, because it is a
        /// destination rather than a capture parameter.
        /// </remarks>
        private UIElement BuildPanelContent(
            int current, bool custom, int currentFps, bool customFps, string folder,
            out int qualityRows, out int fpsRows, out int folderRows)
        {
            StackPanel content = new StackPanel();

            content.Children.Add(SectionTitle("Quality", 0.0));

            Grid qualityGrid = TwoColumnGrid();

            qualityRows = 0;
            for (int i = 0; i < QualityPresets.Length; i++)
            {
                QualityPreset preset = QualityPresets[i];
                AddQualityRow(
                    qualityGrid,
                    preset.Cq == current,
                    preset.Label,
                    "cq " + preset.Cq.ToString(CultureInfo.InvariantCulture),
                    preset.Cq,
                    preset.Label);
                qualityRows++;
            }

            if (custom)
            {
                AddQualityRow(
                    qualityGrid,
                    true,
                    "Custom (cq " + current.ToString(CultureInfo.InvariantCulture) + ")",
                    string.Empty,
                    0,
                    null);
                qualityRows++;
            }

            content.Children.Add(qualityGrid);

            // A gap and a second title make the two settings read as separate
            // choices; without it the frame-rate rows would look like more quality
            // presets.
            content.Children.Add(SectionTitle("Frame rate", 12.0));

            Grid fpsGrid = new Grid();
            fpsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            fpsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            fpsRows = 0;
            for (int i = 0; i < FpsPresets.Length; i++)
            {
                FpsPreset preset = FpsPresets[i];
                AddFpsRow(fpsGrid, preset.Fps == currentFps, preset.Label, preset.Fps, preset.Label);
                fpsRows++;
            }

            if (customFps)
            {
                // The parser refuses anything that is not 60 or 30, so this row is
                // the value the file asked for, never what is recorded. Say so in
                // the label: a bare "Custom (24 fps)" would read as "24 is in use",
                // and the quality row's Custom - which IS applied - means that.
                AddFpsRow(
                    fpsGrid,
                    true,
                    "Custom (" + currentFps.ToString(CultureInfo.InvariantCulture)
                        + " fps, using "
                        + FpsPresets[0].Fps.ToString(CultureInfo.InvariantCulture) + ")",
                    0,
                    null);
                fpsRows++;
            }

            content.Children.Add(fpsGrid);

            // Save to: one row, because there is one destination. It is an action
            // rather than a choice between rows, so it gets no marker - the folder
            // glyph and the last path segment are the whole statement.
            content.Children.Add(SectionTitle("Save to", 12.0));

            Grid folderGrid = TwoColumnGrid();
            AddFolderRow(folderGrid, folder);
            content.Children.Add(folderGrid);
            folderRows = 1;

            return content;
        }

        /// <summary>Builds a section title at the panel's muted, small-caps style.</summary>
        /// <param name="text">Title text, for example "Frame rate".</param>
        /// <param name="topGap">Top margin in DIP, used to separate the sections.</param>
        /// <returns>The title element.</returns>
        /// <remarks>Side effects: none beyond creating the element.</remarks>
        private static TextBlock SectionTitle(string text, double topGap)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = DisabledBrush,
                FontSize = 11.0,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, topGap, 0, 4),
            };
        }

        /// <summary>Builds the two-column grid the quality rows share.</summary>
        /// <returns>An empty grid with an auto-sized label and trailing column.</returns>
        /// <remarks>Side effects: none.</remarks>
        private static Grid TwoColumnGrid()
        {
            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            return grid;
        }

        /// <summary>
        /// Adds one row to the frame-rate grid: a marker and label, with a hit
        /// target spanning the row.
        /// </summary>
        /// <param name="grid">Panel grid receiving the row.</param>
        /// <param name="marked">True for the row describing the current value: filled marker, bright text.</param>
        /// <param name="label">Row text without its marker, for example "30 fps (half the frames)".</param>
        /// <param name="presetFps">Rate this row selects; 0 for an informational row, which stays inert.</param>
        /// <param name="presetLabel">Label handed to the callback when this row is chosen.</param>
        /// <remarks>
        /// Side effects: appends grid elements and, for a preset row, installs the
        /// click handlers. The visible label is hit-test-disabled and a transparent
        /// border spans both columns on top of it, so the whole row is one target
        /// and a press anywhere on it is swallowed instead of starting a drag.
        /// </remarks>
        private void AddFpsRow(
            Grid grid, bool marked, string label, int presetFps, string presetLabel)
        {
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock text = new TextBlock
            {
                Text = (marked ? "\u25CF " : "\u25CB ") + label,
                Foreground = marked ? Brushes.White : MutedBrush,
                FontSize = 13.0,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);

            Border hit = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(0, 2, 0, 2),
            };
            Grid.SetRow(hit, row);
            Grid.SetColumn(hit, 0);
            Grid.SetColumnSpan(hit, 2);

            if (presetFps > 0)
            {
                int chosenFps = presetFps;
                string chosenLabel = presetLabel;
                hit.Cursor = Cursors.Hand;
                hit.MouseLeftButtonDown += OnRowLeftDown;
                hit.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
                {
                    OnFpsChosen(e, chosenFps, chosenLabel);
                };
            }

            grid.Children.Add(hit);
        }

        /// <summary>
        /// Handles a click on a frame-rate row: closes the panel, then reports the choice.
        /// </summary>
        /// <param name="e">Mouse arguments; marked handled.</param>
        /// <param name="fps">The chosen rate, 60 or 30.</param>
        /// <param name="label">The row's label, passed through unchanged.</param>
        /// <remarks>
        /// Side effects: hides the panel, writes one log line, then invokes the
        /// caller's callback. The panel closes first so the overlay is back in its
        /// normal state before any config write. A throwing callback is logged
        /// rather than allowed to escape into a mouse handler: a failed save must
        /// not take the overlay down. The selection is untouched.
        /// </remarks>
        private void OnFpsChosen(MouseButtonEventArgs e, int fps, string label)
        {
            if (_finished)
            {
                return;
            }
            e.Handled = true;

            CloseQualityPanel("chose fps=" + fps.ToString(CultureInfo.InvariantCulture));
            AppLog.Line(
                "overlay: frame rate chose fps=" + fps.ToString(CultureInfo.InvariantCulture)
                + " (" + label + ")");

            Action<int, string> chosen = _onFpsChosen;
            if (chosen == null)
            {
                return;
            }
            try
            {
                chosen(fps, label);
            }
            catch (Exception ex)
            {
                AppLog.Line("fps: callback failed " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Adds one row to the quality grid: a marker and label, an optional -cq, and
        /// a hit target spanning both.
        /// </summary>
        /// <param name="grid">Panel grid receiving the row.</param>
        /// <param name="marked">True for the row describing the current value: filled marker, bright text.</param>
        /// <param name="label">Row text without its marker.</param>
        /// <param name="trailing">Right-hand text, for example "cq 22"; may be empty.</param>
        /// <param name="presetCq">Preset this row selects; 0 for an informational row, which stays inert.</param>
        /// <param name="presetLabel">Label handed to the callback when this row is chosen.</param>
        /// <remarks>
        /// Side effects: appends grid elements and, for a preset row, installs the
        /// click handlers. The visible labels are hit-test-disabled and a transparent
        /// border spans both columns on top of them, so the whole row is one target
        /// and a press anywhere on it is swallowed (C6) instead of starting a drag.
        /// </remarks>
        private void AddQualityRow(
            Grid grid, bool marked, string label, string trailing, int presetCq, string presetLabel)
        {
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock text = new TextBlock
            {
                Text = (marked ? "\u25CF " : "\u25CB ") + label,
                Foreground = marked ? Brushes.White : MutedBrush,
                FontSize = 13.0,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);

            TextBlock cq = new TextBlock
            {
                Text = trailing,
                Foreground = marked ? Brushes.White : DisabledBrush,
                FontSize = 13.0,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(28, 0, 0, 0),
                IsHitTestVisible = false,
            };
            Grid.SetRow(cq, row);
            Grid.SetColumn(cq, 1);
            grid.Children.Add(cq);

            Border hit = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(0, 2, 0, 2),
            };
            Grid.SetRow(hit, row);
            Grid.SetColumn(hit, 0);
            Grid.SetColumnSpan(hit, 2);

            if (presetCq > 0)
            {
                int chosenCq = presetCq;
                string chosenLabel = presetLabel;
                hit.Cursor = Cursors.Hand;
                hit.MouseLeftButtonDown += OnRowLeftDown;
                hit.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
                {
                    OnPresetChosen(e, chosenCq, chosenLabel);
                };
            }

            grid.Children.Add(hit);
        }

        /// <summary>
        /// Adds the Save to row: a folder glyph, the folder's last segment, and a hit
        /// target spanning both columns.
        /// </summary>
        /// <param name="grid">Panel grid receiving the row.</param>
        /// <param name="folder">Folder in effect; the row shows only its last segment.</param>
        /// <remarks>
        /// Side effects: appends grid elements and installs the click handler. Only
        /// the last path segment is visible because a full path makes the panel wide
        /// enough to hide the toolbar it hangs from; the full configured path is the
        /// row's tooltip, so nothing is lost. The row is an action, not a choice
        /// between values, so it carries no marker glyph.
        /// </remarks>
        private void AddFolderRow(Grid grid, string folder)
        {
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock text = new TextBlock
            {
                // U+1F4C1, written as an escape so this file stays 7-bit.
                Text = "\U0001F4C1 " + FolderLabel(folder),
                Foreground = MutedBrush,
                FontSize = 13.0,
                FontFamily = new FontFamily("Segoe UI"),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);

            Border hit = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(0, 2, 0, 2),
                Cursor = Cursors.Hand,
            };
            Grid.SetRow(hit, row);
            Grid.SetColumn(hit, 0);
            Grid.SetColumnSpan(hit, 2);

            // The visible label is only the last segment, so the tooltip is the only
            // place the full path appears. It must be a path the user can trust, which
            // is why it is the caller's value and not something derived here.
            ToolTipService.SetToolTip(
                hit, string.IsNullOrEmpty(folder) ? "No folder configured" : folder);

            hit.MouseLeftButtonDown += OnRowLeftDown;
            hit.MouseLeftButtonUp += OnFolderClicked;

            grid.Children.Add(hit);
        }

        /// <summary>
        /// Handles a click on the Save to row: opens the folder dialog with the overlay
        /// out of the way, then restores the overlay.
        /// </summary>
        /// <param name="sender">The row's hit target (unused).</param>
        /// <param name="e">Mouse arguments; marked handled so no drag starts.</param>
        /// <remarks>
        /// Side effects: hides and re-shows the window, runs a modal folder picker,
        /// and on OK reports the path to the caller and rebuilds the panel. See
        /// <see cref="ChooseFolder"/> for the ordering, which is the whole reason this
        /// handler exists separately.
        /// </remarks>
        private void OnFolderClicked(object sender, MouseButtonEventArgs e)
        {
            if (_finished)
            {
                return;
            }
            e.Handled = true;
            ChooseFolder();
        }

        /// <summary>
        /// Shows the folder picker with the overlay hidden, restores the overlay, then
        /// reports a chosen absolute path and redraws the Save to row.
        /// </summary>
        /// <remarks>
        /// Side effects, in this order: one log line, Hide(), a modal WinForms
        /// FolderBrowserDialog (which may create nothing on disk by itself), Show(),
        /// a redraw of the stored selection, one more log line, and - only on OK - the
        /// caller's callback plus a panel rebuild.
        ///
        /// WHY the overlay is hidden first: it is topmost and covers the virtual
        /// screen, so a dialog opened while it is visible is behind it and cannot be
        /// used. Hiding is also why the selection, the record-enabled state and the
        /// open panel are captured before the hide and restored afterwards: the user
        /// did not cancel anything by picking a folder, so the dialog is not allowed
        /// to cost them their rectangle.
        ///
        /// The dialog's own Esc or Cancel is a no-op for the app: nothing is written,
        /// nothing is reported, and the overlay is already back by the time this
        /// returns. A path that is not absolute is refused rather than written,
        /// because a relative path would be resolved against the process's working
        /// directory by the recorder, which is not what the user picked.
        /// </remarks>
        private void ChooseFolder()
        {
            string current = CurrentFolder();
            string seed = SeedFolder(current);

            // Captured before Hide(): the rectangle the user committed, the Record
            // state that follows from it, and whether the panel must come back. The
            // fields survive Hide()/Show(), but the restore redraws from this snapshot
            // so a pointer that moved while the dialog was up cannot change the
            // selection.
            DipRect selection = _selection;
            bool recordEnabled = _recordEnabled;
            bool panelOpen = _qualityOpen;

            AppLog.Line(
                "overlay: folder dialog opening folder=" + current
                + " seed=" + (seed == null ? "(default)" : seed)
                + " selection=" + (selection.W > 0.0 && selection.H > 0.0 ? "true" : "false")
                + " record=" + (recordEnabled ? "true" : "false")
                + " panel=" + (panelOpen ? "quality" : "none"));

            string chosen = null;
            try
            {
                // Hide before the dialog, and inside the try: the overlay is topmost
                // and covers the screen, so a dialog shown while it is visible would be
                // behind it. Hiding inside the try means a failed hide still reaches the
                // restore below instead of leaving the user with no overlay and no
                // dialog.
                Hide();
                using (System.Windows.Forms.FolderBrowserDialog dialog =
                    new System.Windows.Forms.FolderBrowserDialog())
                {
                    dialog.Description = "Choose where SnipVid saves recordings";
                    dialog.ShowNewFolderButton = true;
                    // Seeded with the current folder, or its closest existing ancestor
                    // when the folder has not been created yet (the default is created
                    // on the first recording). The picker cannot navigate to a path
                    // that is not on disk, so seeding with one would leave it somewhere
                    // arbitrary instead.
                    if (seed != null)
                    {
                        dialog.SelectedPath = seed;
                    }
                    if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    {
                        chosen = dialog.SelectedPath;
                    }
                }
            }
            catch (Exception ex)
            {
                // A dialog failure must not leave the overlay hidden with no way back.
                AppLog.Line("folder: dialog failed " + ex.GetType().Name + ": " + ex.Message);
            }

            RestoreAfterFolderDialog(selection, recordEnabled, panelOpen);

            if (_finished)
            {
                return;
            }

            if (string.IsNullOrEmpty(chosen))
            {
                AppLog.Line("overlay: folder dialog cancelled; folder unchanged");
                return;
            }

            if (!IsRootedPath(chosen))
            {
                AppLog.Line("folder: ignored, the chosen path is not absolute: " + chosen);
                return;
            }

            AppLog.Line("overlay: folder chose folder=" + chosen);

            Action<string> onChosen = _onFolderChosen;
            if (onChosen != null)
            {
                try
                {
                    onChosen(chosen);
                }
                catch (Exception ex)
                {
                    AppLog.Line("folder: callback failed " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            // Rebuilt from the caller's reader rather than from the chosen value, so a
            // failed config write leaves the row showing the folder that is still in
            // effect.
            RefreshQualityPanel();
        }

        /// <summary>
        /// Re-shows the overlay after the folder dialog and puts it back exactly as it
        /// was: same selection, same Record state, same open panel, keyboard focus.
        /// </summary>
        /// <param name="selection">The committed rectangle captured before the hide.</param>
        /// <param name="recordEnabled">Whether Record was enabled before the hide.</param>
        /// <param name="panelOpen">Whether the settings panel was open before the hide.</param>
        /// <remarks>
        /// Side effects: shows the window, runs layout, redraws the selection from the
        /// stored rectangle, restores the Record state and the panel placement, takes
        /// focus back (and queues one more attempt, because a hidden-then-shown window
        /// can lose the foreground while the mouse event that opened the dialog is
        /// still unwinding) and writes one log line. Never throws: a failure to re-show
        /// is logged, because the alternative - an exception out of a mouse handler -
        /// would take the dispatcher down with the overlay already hidden.
        ///
        /// Redrawing from <paramref name="selection"/> rather than from
        /// <see cref="_dragOrigin"/>/<see cref="_pointer"/> is deliberate: the pointer
        /// may have moved while the dialog was open, and recomputing the drag would
        /// silently give the user a different rectangle than the one they committed.
        /// </remarks>
        private void RestoreAfterFolderDialog(DipRect selection, bool recordEnabled, bool panelOpen)
        {
            try
            {
                Show();
            }
            catch (Exception ex)
            {
                // The window cannot be brought back, so it must not stay as a phantom
                // open overlay that swallows every later hotkey press: close it, which
                // clears Program's reference and lets the next press build a fresh
                // picker.
                AppLog.Line(
                    "overlay: restore failed " + ex.GetType().Name + ": " + ex.Message
                    + "; closing so the next hotkey opens a fresh picker");
                _finished = true;
                try
                {
                    Close();
                }
                catch (Exception)
                {
                    // Already closed, or the dispatcher is gone; nothing more to do.
                }
                return;
            }

            UpdateLayout();
            _selection = selection;
            DrawSelection();
            _recordEnabled = recordEnabled;
            SetRecordEnabled(recordEnabled);

            if (panelOpen && _qualityOpen)
            {
                _qualityPanel.Visibility = Visibility.Visible;
                PlacePanel(_qualityPanel);
            }

            UpdateLayout();
            Activate();
            Keyboard.Focus(this);

            // The immediate Activate can be refused while the mouse event that opened
            // the dialog is still unwinding, and a hidden-then-shown window can lose the
            // foreground to nothing in particular. A deferred second attempt costs
            // nothing and is what makes the first Esc after the dialog reach the
            // overlay again: the panel closes on it, the second Esc cancels.
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_finished)
                {
                    return;
                }
                try
                {
                    Activate();
                    Keyboard.Focus(this);
                }
                catch (Exception ex)
                {
                    AppLog.Line("overlay: refocus failed " + ex.GetType().Name + ": " + ex.Message);
                }
            }));

            AppLog.Line(
                "overlay: restored after folder dialog selection="
                + (selection.W > 0.0 && selection.H > 0.0 ? "true" : "false")
                + " record=" + (recordEnabled ? "true" : "false")
                + " panel=" + (panelOpen ? "quality" : "none"));
        }

        /// <summary>
        /// Reads the folder the next recording will use from the caller.
        /// </summary>
        /// <returns>The configured absolute path, or an empty string when unknown.</returns>
        /// <remarks>
        /// Side effects: invokes the caller's reader. No reader, or a throwing one,
        /// reports an empty string and logs one line: the row then reads "(not set)"
        /// instead of claiming a folder that may not be the one in effect.
        /// </remarks>
        private string CurrentFolder()
        {
            Func<string> source = _currentFolder;
            if (source == null)
            {
                return string.Empty;
            }
            try
            {
                string folder = source();
                return folder ?? string.Empty;
            }
            catch (Exception ex)
            {
                AppLog.Line("folder: current value unreadable " + ex.GetType().Name + ": " + ex.Message);
                return string.Empty;
            }
        }

        /// <summary>
        /// Shortens a configured folder to the one segment the panel shows.
        /// </summary>
        /// <param name="folder">Absolute folder path; may be null or empty.</param>
        /// <returns>
        /// The last path segment, the drive root when there is none (for example
        /// <c>D:\</c>), or "(not set)" for an empty value.
        /// </returns>
        /// <remarks>
        /// Side effects: none. A full path can be three times as wide as the toolbar,
        /// so the row shows the last segment only and the tooltip carries the whole
        /// path. Trailing separators are trimmed first because
        /// <see cref="System.IO.Path.GetFileName(string)"/> reports an empty name for
        /// "D:\Clips\" - without the trim, every folder picked in the dialog would
        /// render as its drive root. Total: a hand-edited value Windows rejects as a
        /// path is shown as written instead of throwing out of the panel's build.
        /// </remarks>
        private static string FolderLabel(string folder)
        {
            if (string.IsNullOrEmpty(folder))
            {
                return "(not set)";
            }

            try
            {
                string trimmed = folder.TrimEnd(
                    System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                string name = System.IO.Path.GetFileName(trimmed);
                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }

                string root = System.IO.Path.GetPathRoot(folder);
                return string.IsNullOrEmpty(root) ? folder : root;
            }
            catch (ArgumentException)
            {
                // A hand-edited value with characters Windows cannot put in a path (a
                // quoted path, for example). Show it as written: a label must never be
                // able to throw out of the panel's build.
                return folder;
            }
        }

        /// <summary>
        /// Picks the folder the dialog should start in.
        /// </summary>
        /// <param name="folder">Folder in effect; may be null, empty or not on disk.</param>
        /// <returns>
        /// The folder itself when it exists, else its closest existing ancestor, else
        /// null so the dialog uses its own default.
        /// </returns>
        /// <remarks>
        /// Side effects: none beyond filesystem existence checks. The configured folder
        /// is not created here: opening a picker must not make the folder a side effect.
        /// The ancestor walk is what makes "seeded with the current folder" true on a
        /// fresh profile, where the default Videos\Screen Recordings has not been
        /// created until the first recording; the dialog then starts at Videos instead
        /// of an unrelated default. Bounded at 32 steps so a malformed path cannot
        /// loop, and total: an invalid path reports null.
        /// </remarks>
        private static string SeedFolder(string folder)
        {
            string candidate = folder;
            for (int depth = 0; depth < 32 && !string.IsNullOrEmpty(candidate); depth++)
            {
                try
                {
                    if (System.IO.Directory.Exists(candidate))
                    {
                        return candidate;
                    }
                    candidate = System.IO.Path.GetDirectoryName(candidate);
                }
                catch (Exception)
                {
                    // Deliberately total: GetDirectoryName throws on invalid or
                    // over-long paths, and this runs before the dialog's own try, from
                    // a mouse handler. A malformed hand-edited folder must open the
                    // dialog at its default, not take the app down.
                    return null;
                }
            }
            return null;
        }

        /// <summary>
        /// True when a path is rooted, without throwing on malformed input.
        /// </summary>
        /// <param name="path">Candidate path; null and empty report false.</param>
        /// <returns>True when the path can be used without a working directory.</returns>
        /// <remarks>
        /// Side effects: none. <see cref="System.IO.Path.IsPathRooted(string)"/> throws
        /// <see cref="ArgumentException"/> for characters Windows does not allow in a
        /// path; this runs from a mouse handler, where a throw would take the overlay
        /// down. The value is validated again, with the same rule, by the callback that
        /// writes it.
        /// </remarks>
        private static bool IsRootedPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }
            try
            {
                return System.IO.Path.IsPathRooted(path);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>
        /// Positions one panel just under the toolbar's laid-out bottom edge.
        /// </summary>
        /// <param name="panel">The settings or microphone panel.</param>
        /// <remarks>
        /// Side effects: sets the panel's top margin. Measured from the toolbar's real
        /// bounds rather than a guessed constant, because the toolbar's height comes
        /// from font metrics and a fixed offset would overlap it under a larger font
        /// or a different DPI theme. The fallback only applies before the first
        /// layout pass, when there is nothing to measure. Both panels share one
        /// placement rule so they cannot land at different heights.
        /// </remarks>
        private void PlacePanel(Border panel)
        {
            double top = ToolbarTopDip + 40.0;
            if (_toolbarPanel != null && _toolbarPanel.RenderSize.Height > 0.0)
            {
                Rect bounds = _toolbarPanel
                    .TransformToAncestor(this)
                    .TransformBounds(new Rect(_toolbarPanel.RenderSize));
                top = bounds.Bottom + QualityPanelGapDip;
            }
            panel.Margin = new Thickness(0, top, 0, 0);
        }

        /// <summary>
        /// Gear click handler: opens the settings panel.
        /// </summary>
        /// <param name="sender">The gear item (unused).</param>
        /// <param name="e">Mouse arguments; marked handled so the click cannot also start a drag.</param>
        /// <remarks>Side effects: shows the panel (see <see cref="OpenQualityPanel"/>).</remarks>
        private void OnGearClicked(object sender, MouseButtonEventArgs e)
        {
            if (_finished)
            {
                return;
            }
            e.Handled = true;
            OpenQualityPanel();
        }

        /// <summary>
        /// Swallows a left-button press that landed on one quality row.
        /// </summary>
        /// <param name="sender">The row (unused).</param>
        /// <param name="e">Mouse arguments; marked handled so no drag starts.</param>
        /// <remarks>
        /// Side effects: none beyond marking the event handled. The panel's own
        /// handler would catch the bubbled press too; handling it at the row keeps the
        /// promise local to the element that is clickable.
        /// </remarks>
        private void OnRowLeftDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        /// <summary>
        /// Handles a click on a preset row: closes the panel, then reports the choice.
        /// </summary>
        /// <param name="e">Mouse arguments; marked handled.</param>
        /// <param name="cq">The preset's NVENC -cq.</param>
        /// <param name="label">The preset's name, passed through unchanged.</param>
        /// <remarks>
        /// Side effects: hides the panel, writes one log line, then invokes the
        /// caller's callback. The panel closes first so the overlay is back in its
        /// normal state before any config write happens. A throwing callback is logged
        /// rather than allowed to escape into a mouse handler: a failed save must not
        /// take the overlay down. The selection is untouched - choosing a preset is
        /// not a cancel.
        /// </remarks>
        private void OnPresetChosen(MouseButtonEventArgs e, int cq, string label)
        {
            if (_finished)
            {
                return;
            }
            e.Handled = true;

            CloseQualityPanel("chose cq=" + cq.ToString(CultureInfo.InvariantCulture));
            AppLog.Line(
                "overlay: quality chose cq=" + cq.ToString(CultureInfo.InvariantCulture)
                + " (" + label + ")");

            Action<int, string> chosen = _onQualityChosen;
            if (chosen == null)
            {
                return;
            }
            try
            {
                chosen(cq, label);
            }
            catch (Exception ex)
            {
                AppLog.Line("quality: callback failed " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Reads the -cq in effect from the caller.
        /// </summary>
        /// <returns>The value the next recording would use, or -1 when it is unknown.</returns>
        /// <remarks>
        /// Side effects: invokes the caller's reader. No reader, or a throwing one,
        /// reports -1 ("unknown"), which marks no preset and shows no custom row.
        /// Guessing would let the panel claim a setting the file does not hold.
        /// </remarks>
        private int CurrentQuality()
        {
            Func<int> source = _currentQuality;
            if (source == null)
            {
                return -1;
            }
            try
            {
                return source();
            }
            catch (Exception ex)
            {
                AppLog.Line("quality: current value unreadable " + ex.GetType().Name + ": " + ex.Message);
                return -1;
            }
        }

        /// <summary>
        /// Reads the frame rate the settings panel should mark from the caller.
        /// </summary>
        /// <returns>
        /// 60 or 30 when one is in effect, a rejected requested value (for example
        /// 24) so it can be shown as Custom, or -1 when it is unknown.
        /// </returns>
        /// <remarks>
        /// Side effects: invokes the caller's reader. No reader, or a throwing one,
        /// reports -1 ("unknown"), which marks no row and shows no custom row.
        /// Guessing would let the panel claim a rate the file does not hold.
        /// </remarks>
        private int CurrentFps()
        {
            Func<int> source = _currentFps;
            if (source == null)
            {
                return -1;
            }
            try
            {
                return source();
            }
            catch (Exception ex)
            {
                AppLog.Line("fps: current value unreadable " + ex.GetType().Name + ": " + ex.Message);
                return -1;
            }
        }

        /// <summary>Finds a frame-rate preset's index by its rate.</summary>
        /// <param name="fps">Rate to look up.</param>
        /// <returns>The index in <see cref="FpsPresets"/>, or -1 when it is not a preset.</returns>
        /// <remarks>Side effects: none.</remarks>
        private static int FpsIndex(int fps)
        {
            for (int i = 0; i < FpsPresets.Length; i++)
            {
                if (FpsPresets[i].Fps == fps)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>Formats a frame rate for a log line, naming the unknown case.</summary>
        /// <param name="fps">Rate to format.</param>
        /// <returns>The number, or "(unknown)" when it is below 1.</returns>
        /// <remarks>Side effects: none.</remarks>
        private static string FpsText(int fps)
        {
            return fps < 1 ? "(unknown)" : fps.ToString(CultureInfo.InvariantCulture);
        }


        /// <param name="cq">Value to look up.</param>
        /// <returns>The index in <see cref="QualityPresets"/>, or -1 when it is not a preset.</returns>
        /// <remarks>Side effects: none.</remarks>
        private static int PresetIndex(int cq)
        {
            for (int i = 0; i < QualityPresets.Length; i++)
            {
                if (QualityPresets[i].Cq == cq)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>Formats a -cq for a log line, naming the unknown case.</summary>
        /// <param name="cq">Value to format.</param>
        /// <returns>The number, or "(unknown)" when it is below 1.</returns>
        /// <remarks>Side effects: none.</remarks>
        private static string QualityText(int cq)
        {
            return cq < 1 ? "(unknown)" : cq.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Records the toolbar's composition in the log.
        /// </summary>
        /// <remarks>
        /// Side effects: one app-log line. C8 is a visual criterion - this line is
        /// not a substitute for looking at it, it only states what was built
        /// (including a count taken from the live row, so adding a fifth item
        /// changes the number).
        /// </remarks>
        private void LogToolbar()
        {
            int items = _toolbarRow == null ? 0 : _toolbarRow.Children.Count;
            string mic = CurrentMicSetting();
            bool system = CurrentSystemAudioOn();
            AppLog.Line(
                "overlay: toolbar items=" + items.ToString(CultureInfo.InvariantCulture)
                + " record=1 cancel=1 mic=" + mic
                + " micTooltip=" + MicTooltipText()
                + " system=" + (system ? "on" : "off")
                + " systemTooltip=" + SystemTooltipText()
                + " quality=1(gear) top="
                + ToolbarTopDip.ToString("0", CultureInfo.InvariantCulture) + "dip centered=true"
                + " bounds=" + BoundsOf(_toolbarPanel)
                + " record=" + BoundsOf(_recordPanel)
                + " micBounds=" + BoundsOf(_micItem)
                + " systemBounds=" + BoundsOf(_systemItem));
        }

        /// <summary>
        /// Describes an element's laid-out rectangle in window DIP.
        /// </summary>
        /// <param name="element">Element to measure; null yields "(none)".</param>
        /// <returns>"X=0 Y=40 W=302.4 H=30" in the invariant culture.</returns>
        /// <remarks>
        /// Side effects: none - reads RenderSize and transforms it into the
        /// window's frame. Logged because the toolbar's absolute position depends
        /// on font metrics, which nobody can compute by reading this file; the
        /// visual layer has to be checked against a number rather than a guess.
        /// </remarks>
        private string BoundsOf(FrameworkElement element)
        {
            if (element == null || element.RenderSize.Width <= 0.0)
            {
                return "(none)";
            }
            Rect bounds = element
                .TransformToAncestor(this)
                .TransformBounds(new Rect(element.RenderSize));
            return string.Format(
                CultureInfo.InvariantCulture,
                "X={0:0.#} Y={1:0.#} W={2:0.#} H={3:0.#}",
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height);
        }

        /// <summary>
        /// Recomputes the dim geometry after the window's size is known or changed.
        /// </summary>
        /// <remarks>
        /// Side effects: replaces the Path's geometry. Called from the constructor's
        /// first layout pass, from <see cref="Open"/> and from every drag update;
        /// the geometry is the only thing that makes the selection interior
        /// genuinely unobscured rather than a lighter shade of dim.
        /// </remarks>
        private void UpdateGeometry()
        {
            Rect full = new Rect(0, 0, Math.Max(ActualWidth, 1.0), Math.Max(ActualHeight, 1.0));
            RectangleGeometry fullGeometry = new RectangleGeometry(full);

            if (_selection.W > 0.0 && _selection.H > 0.0)
            {
                RectangleGeometry hole = new RectangleGeometry(
                    new Rect(_selection.X, _selection.Y, _selection.W, _selection.H));
                _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, fullGeometry, hole);
            }
            else
            {
                _dim.Data = fullGeometry;
            }
        }

        /// <summary>
        /// Keeps the dim geometry in step with the window's size.
        /// </summary>
        /// <param name="sender">The window (unused).</param>
        /// <param name="e">Size change arguments (unused).</param>
        /// <remarks>Side effects: replaces the dim geometry.</remarks>
        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateGeometry();
        }

        /// <summary>
        /// Starts a drag: remembers the origin, picks its monitor and takes mouse
        /// capture so a fast drag cannot be lost outside the window.
        /// </summary>
        /// <param name="sender">The window (unused).</param>
        /// <param name="e">Mouse button arguments; marked handled.</param>
        /// <remarks>
        /// Side effects: clears any previous selection, captures the mouse, logs the
        /// drag origin with its monitor, and fixes the monitor for this drag
        /// (WO-08: the monitor containing the drag origin). A press that starts in
        /// a gap between monitors logs the fallback rather than failing later, when
        /// the user has already committed the gesture.
        /// </remarks>
        private void OnLeftDown(object sender, MouseButtonEventArgs e)
        {
            if (_finished)
            {
                return;
            }

            Point p = e.GetPosition(this);
            _dragOrigin = p;
            _pointer = p;
            _dragging = true;
            _recordEnabled = false;
            _selection = new DipRect(p.X, p.Y, 0.0, 0.0);

            double originXDip = SystemParameters.VirtualScreenLeft + p.X;
            double originYDip = SystemParameters.VirtualScreenTop + p.Y;
            MonitorInfo monitor;
            bool onMonitor = Monitors.TryForPoint(_monitors, originXDip, originYDip, out monitor);
            _monitor = monitor;

            if (onMonitor)
            {
                AppLog.Line(
                    "overlay: drag origin X=" + p.X.ToString("0.##", CultureInfo.InvariantCulture)
                    + " Y=" + p.Y.ToString("0.##", CultureInfo.InvariantCulture)
                    + " monitor=" + monitor.Describe());
            }
            else
            {
                AppLog.Line(
                    "overlay: drag origin X=" + p.X.ToString("0.##", CultureInfo.InvariantCulture)
                    + " Y=" + p.Y.ToString("0.##", CultureInfo.InvariantCulture)
                    + " is not on any enumerated monitor; using the primary fallback "
                    + (HasMonitors ? monitor.Describe() : "(no monitors known)"));
            }

            SetSelectionVisible(false);
            UpdateGeometry();
            CaptureMouse();
            e.Handled = true;
        }

        /// <summary>
        /// Tracks the pointer: redraws the selection and the badge while dragging.
        /// </summary>
        /// <param name="sender">The window (unused).</param>
        /// <param name="e">Mouse arguments; not marked handled.</param>
        /// <remarks>
        /// Side effects: updates four WPF elements and the dim geometry. Runs on
        /// every mouse move, so it does no logging of its own: a redraw per pixel
        /// must not become a log line per pixel, and the committed selection is
        /// logged on release instead.
        /// </remarks>
        private void OnPointerMoved(object sender, MouseEventArgs e)
        {
            if (_finished)
            {
                return;
            }
            _pointer = e.GetPosition(this);
            if (_dragging)
            {
                RedrawSelection();
            }
        }

        /// <summary>
        /// Ends a drag: commits the rectangle, or treats it as a click and cancels.
        /// </summary>
        /// <param name="sender">The window (unused).</param>
        /// <param name="e">Mouse arguments; marked handled.</param>
        /// <remarks>
        /// Side effects: releases mouse capture, redraws once more at the release
        /// point, and writes exactly one log line (the committed selection with its
        /// usability, or the cancellation). Distinguishing the two by distance
        /// rather than by rectangle size is what makes a 15 DIP drag a rejected
        /// capture instead of a click (C5 versus C6).
        /// </remarks>
        private void OnLeftUp(object sender, MouseButtonEventArgs e)
        {
            if (_finished || !_dragging)
            {
                return;
            }

            _dragging = false;
            ReleaseMouseCapture();
            _pointer = e.GetPosition(this);
            RedrawSelection();

            double dx = _pointer.X - _dragOrigin.X;
            double dy = _pointer.Y - _dragOrigin.Y;
            double moved = Math.Sqrt((dx * dx) + (dy * dy));

            if (moved < DragThresholdDip)
            {
                Cancel(
                    "click",
                    "moved " + moved.ToString("0.##", CultureInfo.InvariantCulture) + " dip");
                e.Handled = true;
                return;
            }

            PxRect rect;
            MonitorInfo monitor;
            string reason;
            _recordEnabled = TryBuild(out rect, out monitor, out reason);
            if (_recordEnabled)
            {
                AppLog.Line(
                    "overlay: selection " + rect
                    + " monitor=" + monitor.Index.ToString(CultureInfo.InvariantCulture)
                    + " usable=true");
            }
            else
            {
                AppLog.Line(
                    "overlay: selection " + rect
                    + " monitor=" + monitor.Index.ToString(CultureInfo.InvariantCulture)
                    + " usable=false (" + reason + ")");
            }
            SetRecordEnabled(_recordEnabled);
            e.Handled = true;
        }

        /// <summary>
        /// Right-click cancels, matching the Snipping Tool gesture (DESIGN.md
        /// section 3).
        /// </summary>
        /// <param name="sender">The window (unused).</param>
        /// <param name="e">Mouse arguments; marked handled.</param>
        /// <remarks>Side effects: closes the overlay and logs the reason.</remarks>
        private void OnRightDown(object sender, MouseButtonEventArgs e)
        {
            if (_finished)
            {
                return;
            }
            e.Handled = true;
            Cancel("right-click", null);
        }

        /// <summary>Keyboard handling: Esc closes the settings panel or cancels, Enter and R
        /// accept.</summary>
        /// <param name="e">Key arguments; the handled keys are marked handled.</param>
        /// <remarks>
        /// Side effects: closes the panel or the overlay, and may hand a rectangle to
        /// the callback. Preview stage so the keys work regardless of which element
        /// inside the overlay holds focus. Esc is two-stage while the settings panel
        /// is open: the first press closes the panel, the second cancels. Any other
        /// key is ignored and marked unhandled, because the overlay must not swallow
        /// input it does not use.
        /// </remarks>
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            if (_finished)
            {
                return;
            }

            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                // The panel closes first: one Esc dismisses the control the user
                // just opened, and only a second Esc abandons the whole capture.
                // Collapsing the panel instead would make the overlay disappear
                // under someone who was aiming at the preset or device list.
                if (_qualityOpen)
                {
                    CloseQualityPanel("esc");
                    return;
                }
                if (_microphoneOpen)
                {
                    CloseMicrophonePanel("esc");
                    return;
                }
                Cancel("esc", null);
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Accept("enter");
            }
            else if (e.Key == Key.R)
            {
                e.Handled = true;
                Accept("key-r");
            }
        }

        /// <summary>
        /// Record click handler: the same acceptance as Enter, so the two paths
        /// cannot diverge.
        /// </summary>
        /// <param name="sender">The Record panel (unused).</param>
        /// <param name="e">Mouse arguments; marked handled.</param>
        /// <remarks>
        /// Side effects: closes the overlay and may hand a rectangle to the
        /// callback. A click while the control is disabled is refused by
        /// <see cref="Accept"/>, which logs why and leaves the overlay open.
        /// </remarks>
        private void OnRecordClicked(object sender, MouseButtonEventArgs e)
        {
            if (_finished)
            {
                return;
            }
            e.Handled = true;
            Accept("record-click");
        }

        /// <summary>
        /// Cancel click handler: the same cancellation as Esc.
        /// </summary>
        /// <param name="sender">The Cancel panel (unused).</param>
        /// <param name="e">Mouse arguments; marked handled.</param>
        /// <remarks>Side effects: closes the overlay and logs the reason.</remarks>
        private void OnCancelClicked(object sender, MouseButtonEventArgs e)
        {
            if (_finished)
            {
                return;
            }
            e.Handled = true;
            Cancel("cancel-click", null);
        }

        /// <summary>
        /// Recomputes the selection from the drag origin and the current pointer, then
        /// draws it.
        /// </summary>
        /// <remarks>
        /// Side effects: see <see cref="DrawSelection"/>, which does the drawing. Runs
        /// on every mouse move, so it does no logging of its own: a redraw per pixel
        /// must not become a log line per pixel.
        /// </remarks>
        private void RedrawSelection()
        {
            _selection = Geometry.NormaliseDrag(
                _dragOrigin.X, _dragOrigin.Y, _pointer.X, _pointer.Y);
            DrawSelection();
        }

        /// <summary>
        /// Draws the dim hole, border, grips and badge from the current
        /// <see cref="_selection"/>.
        /// </summary>
        /// <remarks>
        /// Side effects: writes to four elements, replaces the dim geometry, and
        /// enables or disables Record. Side-effect free with respect to the log, so
        /// it can run on every mouse move.
        ///
        /// Split out of <see cref="RedrawSelection"/> because the folder dialog hides
        /// and re-shows the window: the restore path must redraw the rectangle the
        /// user already committed, not recompute one from the drag origin and a
        /// pointer that moved while the dialog was up. Both paths therefore position
        /// the same elements from the same field.
        ///
        /// The badge is fed from Geometry.ToPhysical, the same call the record line
        /// uses, so the displayed numbers and the captured numbers cannot disagree
        /// (AC2). When the rectangle is unusable the numbers are still shown - the
        /// user needs to see how far off the drag was - but the badge turns red.
        /// </remarks>
        private void DrawSelection()
        {
            UpdateGeometry();

            if (_selection.W <= 0.0 || _selection.H <= 0.0)
            {
                SetSelectionVisible(false);
                SetRecordEnabled(false);
                return;
            }

            SetSelectionVisible(true);
            Canvas.SetLeft(_border, _selection.X);
            Canvas.SetTop(_border, _selection.Y);
            _border.Width = _selection.W;
            _border.Height = _selection.H;

            PlaceGrips();

            PxRect rect;
            MonitorInfo monitor;
            string reason;
            bool usable = TryBuild(out rect, out monitor, out reason);
            SetRecordEnabled(usable);

            _badgeText.Text = string.Format(
                CultureInfo.InvariantCulture, "{0} \u00d7 {1}", rect.W, rect.H);
            _badge.Background = usable ? PanelBrush : BadgeErrorBrush;
            PlaceBadge();
        }

        /// <summary>
        /// Positions the eight grips at the corners and edge midpoints.
        /// </summary>
        /// <remarks>
        /// Side effects: sets Canvas.Left/Top on each grip. Grips are centred on
        /// the selection's edges, so they straddle the 1 px border as a handle
        /// should.
        /// </remarks>
        private void PlaceGrips()
        {
            double left = _selection.X;
            double top = _selection.Y;
            double right = _selection.Right;
            double bottom = _selection.Bottom;
            double midX = left + (_selection.W / 2.0);
            double midY = top + (_selection.H / 2.0);
            double offset = GripSizeDip / 2.0;

            Point[] centers = new Point[]
            {
                new Point(left, top),
                new Point(midX, top),
                new Point(right, top),
                new Point(right, midY),
                new Point(right, bottom),
                new Point(midX, bottom),
                new Point(left, bottom),
                new Point(left, midY),
            };

            for (int i = 0; i < _grips.Length; i++)
            {
                Canvas.SetLeft(_grips[i], centers[i].X - offset);
                Canvas.SetTop(_grips[i], centers[i].Y - offset);
            }
        }

        /// <summary>
        /// Places the size badge next to the pointer, flipping it inside the
        /// window near an edge.
        /// </summary>
        /// <remarks>
        /// Side effects: sets Canvas.Left/Top on the badge. The badge follows the
        /// pointer rather than the rectangle's corner so it stays readable while
        /// the drag grows up or to the left, where the bottom-right corner is not
        /// where the eye is.
        /// </remarks>
        private void PlaceBadge()
        {
            _badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Size size = _badge.DesiredSize;
            double width = Math.Max(ActualWidth, Width);
            double height = Math.Max(ActualHeight, Height);

            double x = _pointer.X + BadgeGapDip;
            double y = _pointer.Y + BadgeGapDip;
            if (x + size.Width > width)
            {
                x = _pointer.X - BadgeGapDip - size.Width;
            }
            if (y + size.Height > height)
            {
                y = _pointer.Y - BadgeGapDip - size.Height;
            }
            Canvas.SetLeft(_badge, Math.Max(0.0, x));
            Canvas.SetTop(_badge, Math.Max(0.0, y));
        }

        /// <summary>
        /// Converts the current DIP selection to the physical rectangle the
        /// capture will use.
        /// </summary>
        /// <param name="rect">Monitor-relative physical pixels, even-floored.</param>
        /// <param name="monitor">The monitor the drag started on.</param>
        /// <param name="reason">Why the rectangle is unusable; null when it is usable.</param>
        /// <returns>True when <see cref="Geometry.IsUsable"/> accepts the rectangle.</returns>
        /// <remarks>
        /// Side effects: none - one pure call into Geometry. Both the badge and the
        /// record line go through here, so there is exactly one conversion in the
        /// app and no second opinion about scale, clamping or flooring.
        /// A drag with no monitor known is reported unusable rather than converted
        /// with a guessed scale: recording the wrong rectangle silently is worse
        /// than refusing the capture.
        /// </remarks>
        private bool TryBuild(out PxRect rect, out MonitorInfo monitor, out string reason)
        {
            monitor = _monitor;
            rect = new PxRect(0, 0, 0, 0);

            if (!HasMonitors)
            {
                reason = "no monitor enumerated";
                return false;
            }

            rect = Geometry.ToPhysical(
                _selection,
                monitor.OriginXDip,
                monitor.OriginYDip,
                monitor.Bounds,
                monitor.DpiScale);

            if (rect.W < Geometry.MinimumSize || rect.H < Geometry.MinimumSize)
            {
                reason = "below " + Geometry.MinimumSize.ToString(CultureInfo.InvariantCulture) + "x"
                    + Geometry.MinimumSize.ToString(CultureInfo.InvariantCulture) + " physical px";
                return false;
            }
            if (!Geometry.IsUsable(rect, monitor.Bounds))
            {
                reason = "outside monitor bounds";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// Accepts the current selection: closes the overlay first, then logs and
        /// reports the capture rectangle.
        /// </summary>
        /// <param name="source">Which gesture accepted: "enter", "key-r" or "record-click".</param>
        /// <remarks>
        /// Side effects: closes the window, writes two log lines and invokes the
        /// callback. Order is deliberate and is the C7 evidence: Close() runs
        /// before the record line is written, so the presence of
        /// "overlay: record ..." in the log is proof the overlay was already gone
        /// when the capture rect was handed over - the state machine's whole reason
        /// for the Starting state is that a visible overlay must never be recorded.
        ///
        /// A refused accept (no selection, too small, no monitor) logs one line and
        /// leaves the overlay open, because the user's next move is to adjust the
        /// drag rather than to start over.
        /// </remarks>
        private void Accept(string source)
        {
            if (_finished)
            {
                return;
            }

            if (!_recordEnabled)
            {
                PxRect refused;
                MonitorInfo refusedMonitor;
                string reason = "no selection yet";
                if (_selection.W > 0.0 && _selection.H > 0.0)
                {
                    TryBuild(out refused, out refusedMonitor, out reason);
                    if (reason == null)
                    {
                        reason = "selection not confirmed";
                    }
                }
                AppLog.Line(
                    "overlay: refused source=" + source + " (" + reason + "); overlay stays open");
                return;
            }

            PxRect rect;
            MonitorInfo monitor;
            string unused;
            if (!TryBuild(out rect, out monitor, out unused))
            {
                AppLog.Line(
                    "overlay: refused source=" + source + " (recomputed rectangle is unusable); overlay stays open");
                return;
            }

            _finished = true;
            ReleaseMouseCapture();
            Close();

            AppLog.Line(
                "overlay: record offset_x=" + rect.X.ToString(CultureInfo.InvariantCulture)
                + " offset_y=" + rect.Y.ToString(CultureInfo.InvariantCulture)
                + " video_size=" + rect.W.ToString(CultureInfo.InvariantCulture)
                + "x" + rect.H.ToString(CultureInfo.InvariantCulture)
                + " monitor=" + monitor.Index.ToString(CultureInfo.InvariantCulture)
                + " source=" + source);

            if (_onAccepted != null)
            {
                _onAccepted(rect, monitor);
            }
        }

        /// <summary>
        /// Closes the overlay without accepting anything.
        /// </summary>
        /// <param name="reason">Short reason for the log: "esc", "right-click", "click", "cancel-click".</param>
        /// <param name="detail">Optional extra detail, for example the measured movement of a click.</param>
        /// <remarks>
        /// Side effects: closes the window and writes one log line. Nothing is
        /// spawned and no file is created on any cancel path (AC3), which is why
        /// this method only closes and logs.
        /// </remarks>
        private void Cancel(string reason, string detail)
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            ReleaseMouseCapture();

            AppLog.Line(
                "overlay: cancelled reason=" + reason
                + " selection=" + (_selection.W > 0.0 && _selection.H > 0.0 ? "true" : "false")
                + (detail == null ? string.Empty : " " + detail));
            Close();
        }

        /// <summary>
        /// Shows or hides the border, grips and badge as one group.
        /// </summary>
        /// <param name="visible">True to show.</param>
        /// <remarks>
        /// Side effects: sets Visibility on ten elements. Hidden rather than
        /// collapsed so no layout work happens on every mouse move.
        /// </remarks>
        private void SetSelectionVisible(bool visible)
        {
            Visibility state = visible ? Visibility.Visible : Visibility.Hidden;
            _border.Visibility = state;
            for (int i = 0; i < _grips.Length; i++)
            {
                _grips[i].Visibility = state;
            }
            _badge.Visibility = state;
        }

        /// <summary>
        /// Enables or disables the Record control.
        /// </summary>
        /// <param name="enabled">True when the current selection can be recorded.</param>
        /// <remarks>
        /// Side effects: changes the Record label's colour and the panel's opacity.
        /// A disabled Record still receives clicks; <see cref="Accept"/> refuses
        /// them with a logged reason, so the control cannot silently do nothing.
        /// </remarks>
        private void SetRecordEnabled(bool enabled)
        {
            _recordText.Foreground = enabled ? AccentBrush : DisabledBrush;
            _recordPanel.Opacity = enabled ? 1.0 : 0.45;
            _recordPanel.Cursor = enabled ? Cursors.Hand : Cursors.Arrow;
        }

        /// <summary>
        /// Logs the closed state, including the owned-window count that C3 asks
        /// about.
        /// </summary>
        /// <param name="sender">The window (unused).</param>
        /// <param name="e">Event arguments (unused).</param>
        /// <remarks>
        /// Side effects: one log line. Written here rather than in the accept and
        /// cancel paths so every way the window can close is covered once.
        /// </remarks>
        private void OnWindowClosed(object sender, EventArgs e)
        {
            int owned = OwnedWindows.Count;
            AppLog.Line(
                "overlay: closed (owned windows=" + owned.ToString(CultureInfo.InvariantCulture) + ")");
        }

        /// <summary>
        /// One settings-panel frame-rate choice: a rate and the name shown for it.
        /// </summary>
        /// <remarks>
        /// Immutable and private: the preset table is the only source of instances,
        /// so the row label and the label handed to the callback cannot drift apart.
        /// </remarks>
        private sealed class FpsPreset
        {
            /// <summary>The frame rate this row selects.</summary>
            internal readonly int Fps;

            /// <summary>Human name shown in the row and passed to the callback.</summary>
            internal readonly string Label;

            /// <summary>Stores one frame-rate choice.</summary>
            /// <param name="fps">Frame rate, 60 or 30.</param>
            /// <param name="label">Human name.</param>
            /// <remarks>Side effects: none.</remarks>
            internal FpsPreset(int fps, string label)
            {
                Fps = fps;
                Label = label;
            }
        }

        /// <summary>
        /// One quality panel preset: a -cq value and the name shown for it.
        /// </summary>
        /// <remarks>
        /// Immutable and private: the preset table is the only source of instances,
        /// so the name on a row and the name handed to the callback cannot drift
        /// apart.
        /// </remarks>
        private sealed class QualityPreset
        {
            /// <summary>The NVENC -cq this preset writes.</summary>
            internal readonly int Cq;

            /// <summary>Human name shown in the row and passed to the callback.</summary>
            internal readonly string Label;

            /// <summary>Stores one preset.</summary>
            /// <param name="cq">NVENC -cq value, 1-51.</param>
            /// <param name="label">Human name.</param>
            /// <remarks>Side effects: none.</remarks>
            internal QualityPreset(int cq, string label)
            {
                Cq = cq;
                Label = label;
            }
        }

        /// <summary>
        /// Freezes a brush so it can be shared across elements without WPF
        /// treating it as mutable state.
        /// </summary>
        /// <param name="color">Colour to wrap.</param>
        /// <returns>A frozen solid brush.</returns>
        /// <remarks>
        /// Side effects: none. Frozen brushes are also cheaper: WPF skips change
        /// notification for them and can share the underlying resource.
        /// </remarks>
        private static Brush Frozen(Color color)
        {
            SolidColorBrush brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
