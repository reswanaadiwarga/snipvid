// Monitors - display enumeration and DPI for the capture region (WO-08).
//
// WHY this file exists: DESIGN.md section 4 needs three things per display that
// the pure Logic layer cannot know - its rectangle, its DPI scale, and whether it
// is the primary one. Geometry.ToPhysical() takes all three as parameters, so the
// maths stays unit-testable and this file is the thin, untestable edge that reads
// them from Windows.
//
// Frames, the part that is easy to get backwards:
//   - EnumDisplayMonitors/GetMonitorInfo report rectangles in the virtual-screen
//     frame, in PHYSICAL pixels, because this process is PerMonitorV2-aware via
//     app.manifest. A 1920x1080 monitor at (0,0) is PxRect(0,0,1920,1080).
//   - The overlay works in DIP, and it is positioned over the virtual screen, so
//     its mouse coordinates are virtual-screen DIP (DESIGN.md section 4's
//     "overlayRect"). Monitor DIP bounds are therefore derived by dividing the
//     physical bounds by that monitor's own scale, which is exact on a single
//     100 % display (this machine) and is the only self-consistent choice for a
//     mixed-DPI desktop too: each monitor's DIP size must equal its physical size
//     divided by its scale, or WPF could not lay a window out on it.
//   - The capture rectangle handed to ffmpeg is monitor-relative physical pixels,
//     which Geometry produces by subtracting the origin passed in here. Nothing
//     in this file pre-subtracts anything.
//
// Multi-monitor limitation, stated rather than hidden: WO-08 C2 (a selection that
// begins on monitor A clamps to A even if the pointer leaves it) cannot be
// exercised on a single-display machine, and this file's DIP-to-monitor mapping
// is only arithmetic on that one display. The code path exists and is logged; it
// is not claimed to be verified.
//
// No WPF types are used here beyond SystemParameters for the degenerate fallback,
// so the only thing that can throw is the enumeration itself.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using SnipVid.Logic;

namespace SnipVid
{
    /// <summary>
    /// One display monitor: its index in enumeration order, its physical
    /// rectangle, its effective DPI and whether it is primary.
    /// </summary>
    /// <remarks>
    /// Immutable value type. The index is the position in
    /// <see cref="Monitors.Enumerate"/>'s result and is what the log carries next
    /// to a capture, because ddagrab's output_idx is *assumed* to use the same
    /// order (WORKORDERS WO-08 note). Nothing here validates anything: a
    /// nonsensical DPI is clamped by <see cref="DpiScale"/> rather than thrown,
    /// since a display query failing must never refuse a capture.
    /// </remarks>
    internal struct MonitorInfo
    {
        /// <summary>Position in enumeration order; also the assumed ddagrab output index.</summary>
        internal readonly int Index;

        /// <summary>
        /// Rectangle in the virtual-screen frame, physical pixels; right/bottom
        /// edges exclusive, matching PxRect.
        /// </summary>
        internal readonly PxRect Bounds;

        /// <summary>Effective horizontal DPI reported by Windows; 96 at 100 % scale.</summary>
        internal readonly int Dpi;

        /// <summary>True when Windows marks this display as the primary one.</summary>
        internal readonly bool Primary;

        /// <summary>
        /// Initialises one monitor record.
        /// </summary>
        /// <param name="index">Enumeration position, 0-based.</param>
        /// <param name="bounds">Physical rectangle in the virtual-screen frame.</param>
        /// <param name="dpi">Effective DPI; 96 means 100 % scale.</param>
        /// <param name="primary">True for the Windows primary display.</param>
        /// <remarks>Side effects: none. Values are stored verbatim.</remarks>
        internal MonitorInfo(int index, PxRect bounds, int dpi, bool primary)
        {
            Index = index;
            Bounds = bounds;
            Dpi = dpi;
            Primary = primary;
        }

        /// <summary>
        /// Physical pixels per DIP: 1.0 at 96 dpi, 1.5 at 144 dpi.
        /// </summary>
        /// <remarks>
        /// Returns 1.0 for any DPI below 1 rather than dividing by zero. A zero
        /// DPI is representable because <see cref="Monitors"/> substitutes 96 on a
        /// failed query, but a caller passing its own record must not be able to
        /// turn a bad DPI into an exception in the middle of a capture.
        /// </remarks>
        internal double DpiScale
        {
            get { return Dpi >= 1 ? Dpi / 96.0 : 1.0; }
        }

        /// <summary>Left edge in DIP, virtual-screen frame: bounds.X divided by the scale.</summary>
        internal double OriginXDip
        {
            get { return Bounds.X / DpiScale; }
        }

        /// <summary>Top edge in DIP, virtual-screen frame: bounds.Y divided by the scale.</summary>
        internal double OriginYDip
        {
            get { return Bounds.Y / DpiScale; }
        }

        /// <summary>Width in DIP.</summary>
        internal double WidthDip
        {
            get { return Bounds.W / DpiScale; }
        }

        /// <summary>Height in DIP.</summary>
        internal double HeightDip
        {
            get { return Bounds.H / DpiScale; }
        }

        /// <summary>
        /// Tests whether a virtual-screen DIP point lies on this monitor.
        /// </summary>
        /// <param name="x">Point X in virtual-screen DIP.</param>
        /// <param name="y">Point Y in virtual-screen DIP.</param>
        /// <returns>True when the point is inside this monitor's DIP rectangle.</returns>
        /// <remarks>
        /// Side effects: none. The right and bottom edges are exclusive, so a
        /// point on a shared boundary belongs to exactly one of two adjacent
        /// monitors instead of both - which is what makes "the monitor containing
        /// the drag origin" a single answer.
        /// </remarks>
        internal bool ContainsDip(double x, double y)
        {
            return x >= OriginXDip && x < OriginXDip + WidthDip
                && y >= OriginYDip && y < OriginYDip + HeightDip;
        }

        /// <summary>
        /// One-line description used by the startup log and by the overlay when a
        /// drag begins.
        /// </summary>
        /// <returns>
        /// "index=0 bounds=0,0,1920,1080 dpi=96 scale=1.00 primary=true", formatted
        /// with the invariant culture so the log is identical on every machine.
        /// </returns>
        /// <remarks>Side effects: none.</remarks>
        internal string Describe()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "index={0} bounds={1},{2},{3},{4} dpi={5} scale={6:0.00} primary={7}",
                Index,
                Bounds.X,
                Bounds.Y,
                Bounds.W,
                Bounds.H,
                Dpi,
                DpiScale,
                Primary ? "true" : "false");
        }
    }

    /// <summary>
    /// Reads the display list from Windows and answers "which monitor is this
    /// point on" for the overlay (WO-08).
    /// </summary>
    /// <remarks>
    /// Stateless: the monitor list is produced once at startup by
    /// <see cref="Enumerate"/> and passed around as a parameter, so a hot-plug
    /// during a run cannot change the list under a live drag. Re-enumerating per
    /// capture is deliberately not done: WO-08 C2 requires the monitor that
    /// contained the drag origin, and a list that changes mid-drag would make the
    /// origin's owner ambiguous.
    ///
    /// Failure modes are converted, never thrown, into the list this app can still
    /// capture with plus one note per unexpected condition (DESIGN.md section 8:
    /// notes once at startup, never a dialog).
    /// </remarks>
    internal static class Monitors
    {
        /// <summary>
        /// DPI reported in place of a failed query, and for the degenerate
        /// fallback monitor.
        /// </summary>
        /// <remarks>
        /// 96 is the one value that means "no scaling", which is the assumption
        /// the whole DIP pipeline is written against (DESIGN.md section 4), so a
        /// failed query degrades to the documented default instead of refusing the
        /// capture.
        /// </remarks>
        private const int DefaultDpi = 96;

        /// <summary>
        /// Enumerates every display monitor, in the order Windows returns them.
        /// </summary>
        /// <param name="note">
        /// Sink for a condition the user should be told about once - a failed DPI
        /// query or an enumeration that returned nothing. May be null; the log
        /// line is written either way.
        /// </param>
        /// <returns>
        /// At least one monitor: the real list, or one synthetic 96 dpi primary
        /// entry built from WPF's virtual-screen metrics when enumeration fails or
        /// returns nothing. Never null, never empty - the overlay needs a rectangle
        /// to clamp to and there is no correct behaviour without one.
        /// </returns>
        /// <remarks>
        /// Side effects: three P/Invoke calls per monitor, plus the log lines and
        /// notes described above. Must run on a thread with a message pump only
        /// because EnumDisplayMonitors can touch window stations; it does not
        /// require the UI thread in practice, but this app only calls it there.
        /// </remarks>
        internal static List<MonitorInfo> Enumerate(Action<string> note)
        {
            // Handles are collected first, then queried: a GetMonitorInfo call
            // inside the native callback would re-enter a delegate the GC is
            // entitled to move, and this list is also the order ddagrab is assumed
            // to share, so it is worth keeping explicit.
            List<IntPtr> handles = new List<IntPtr>();
            Native.MonitorEnumProc collect = delegate(IntPtr monitor, IntPtr dc, ref Native.NativeRect rect, IntPtr data)
            {
                handles.Add(monitor);
                return true;
            };

            bool enumerated;
            try
            {
                enumerated = Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, collect, IntPtr.Zero);
            }
            finally
            {
                // The delegate must outlive the native call (see MonitorEnumProc).
                GC.KeepAlive(collect);
            }

            if (!enumerated)
            {
                Report(note, "monitor enumeration failed; assuming one 1920x1080 display at 96 dpi");
                return new List<MonitorInfo> { SyntheticFallback() };
            }

            List<MonitorInfo> monitors = new List<MonitorInfo>(handles.Count);
            for (int i = 0; i < handles.Count; i++)
            {
                Native.NativeMonitorInfo info = Query(handles[i]);
                PxRect bounds = new PxRect(
                    info.Monitor.Left,
                    info.Monitor.Top,
                    info.Monitor.Right - info.Monitor.Left,
                    info.Monitor.Bottom - info.Monitor.Top);

                bool primary = (info.Flags & Native.MonitorInfoPrimary) != 0;
                monitors.Add(new MonitorInfo(i, bounds, QueryDpi(handles[i], i, note), primary));
            }

            if (monitors.Count == 0)
            {
                Report(note, "no display monitors were enumerated; assuming one 1920x1080 display at 96 dpi");
                return new List<MonitorInfo> { SyntheticFallback() };
            }
            return monitors;
        }

        /// <summary>
        /// Writes one log line per monitor, in enumeration order.
        /// </summary>
        /// <param name="monitors">List from <see cref="Enumerate"/>; may be null.</param>
        /// <remarks>
        /// Side effects: one app-log line per monitor. This is the WO-08 C1
        /// evidence and also the record of the enumeration order next to the index
        /// the overlay later names in its record line, because ddagrab's
        /// output_idx numbering is only *assumed* to match it.
        /// </remarks>
        internal static void Log(List<MonitorInfo> monitors)
        {
            if (monitors == null)
            {
                AppLog.Line("monitor: none enumerated");
                return;
            }
            for (int i = 0; i < monitors.Count; i++)
            {
                AppLog.Line("monitor: " + monitors[i].Describe());
            }
            AppLog.Line(
                "monitor: count=" + monitors.Count.ToString(CultureInfo.InvariantCulture)
                + " (ddagrab output_idx is assumed to use this order)");
        }

        /// <summary>
        /// Finds the monitor containing a virtual-screen DIP point.
        /// </summary>
        /// <param name="monitors">List from <see cref="Enumerate"/>.</param>
        /// <param name="x">Point X in virtual-screen DIP.</param>
        /// <param name="y">Point Y in virtual-screen DIP.</param>
        /// <param name="monitor">The matching monitor; the first one when nothing matches.</param>
        /// <returns>
        /// True when a monitor actually contains the point; false when the
        /// fallback was used, so the caller can log that a drag began in a gap
        /// (which a non-rectangular desktop layout can produce).
        /// </returns>
        /// <remarks>
        /// Side effects: none. Falls back to the primary monitor, else the first
        /// entry, so a capture from a gap still lands somewhere predictable rather
        /// than failing at the moment the user released the mouse.
        /// </remarks>
        internal static bool TryForPoint(List<MonitorInfo> monitors, double x, double y, out MonitorInfo monitor)
        {
            monitor = default(MonitorInfo);
            if (monitors == null || monitors.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < monitors.Count; i++)
            {
                if (monitors[i].ContainsDip(x, y))
                {
                    monitor = monitors[i];
                    return true;
                }
            }

            for (int i = 0; i < monitors.Count; i++)
            {
                if (monitors[i].Primary)
                {
                    monitor = monitors[i];
                    return false;
                }
            }
            monitor = monitors[0];
            return false;
        }

        /// <summary>
        /// Queries one monitor's rectangle, work area and primary flag.
        /// </summary>
        /// <param name="handle">Monitor handle from the enumeration.</param>
        /// <returns>
        /// The filled structure; an all-zero one when GetMonitorInfo fails, which
        /// produces a zero-size monitor that Geometry will reject rather than a
        /// wrong rectangle silently attributed to a real display.
        /// </returns>
        /// <remarks>
        /// Side effects: one P/Invoke call. Failure modes: a false return is
        /// reported to the log and yields the zeroed structure; cbSize is set
        /// before the call because the API requires it.
        /// </remarks>
        private static Native.NativeMonitorInfo Query(IntPtr handle)
        {
            Native.NativeMonitorInfo info = new Native.NativeMonitorInfo();
            info.Size = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.NativeMonitorInfo));
            if (!Native.GetMonitorInfo(handle, ref info))
            {
                AppLog.Line(
                    "monitor: GetMonitorInfo failed, windows error "
                    + System.Runtime.InteropServices.Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
            }
            return info;
        }

        /// <summary>
        /// Queries one monitor's effective DPI, falling back to 96 with one note.
        /// </summary>
        /// <param name="handle">Monitor handle from the enumeration.</param>
        /// <param name="index">Enumeration position, used in the note.</param>
        /// <param name="note">Note sink; may be null.</param>
        /// <returns>The effective DPI, or <see cref="DefaultDpi"/>.</returns>
        /// <remarks>
        /// Side effects: one P/Invoke call, and a note plus log line on failure.
        /// WO-08 C3 is this fallback: a DPI query that fails must not stop a
        /// capture, it must record that the region may be scaled wrongly.
        ///
        /// Both non-zero HRESULTs and a missing shcore.dll (pre-8.1) are treated
        /// identically, because the consequence for the capture is identical.
        /// </remarks>
        private static int QueryDpi(IntPtr handle, int index, Action<string> note)
        {
            try
            {
                uint dpiX;
                uint dpiY;
                int hr = Native.GetDpiForMonitor(handle, Native.MdtEffectiveDpi, out dpiX, out dpiY);
                if (hr == 0 && dpiX >= 1)
                {
                    return (int)dpiX;
                }
                Report(note, "monitor " + index.ToString(CultureInfo.InvariantCulture)
                    + ": DPI query failed (HRESULT 0x" + hr.ToString("X8", CultureInfo.InvariantCulture)
                    + "); assuming 96 dpi");
            }
            catch (DllNotFoundException)
            {
                Report(note, "monitor " + index.ToString(CultureInfo.InvariantCulture)
                    + ": shcore.dll is unavailable (Windows older than 8.1); assuming 96 dpi");
            }
            catch (EntryPointNotFoundException)
            {
                Report(note, "monitor " + index.ToString(CultureInfo.InvariantCulture)
                    + ": GetDpiForMonitor is unavailable; assuming 96 dpi");
            }
            return DefaultDpi;
        }

        /// <summary>
        /// Builds the one-monitor substitute used when enumeration yields nothing.
        /// </summary>
        /// <returns>
        /// A 96 dpi primary monitor covering WPF's virtual screen at (0,0), or
        /// 1920x1080 if even that is degenerate.
        /// </returns>
        /// <remarks>
        /// Side effects: none. Reads SystemParameters rather than GetSystemMetrics
        /// so the fallback is in the same DIP frame the overlay is laid out in; at
        /// 96 dpi DIP and physical pixels are the same number, which is exactly the
        /// assumption the fallback is asking the user to accept.
        /// </remarks>
        private static MonitorInfo SyntheticFallback()
        {
            int width = (int)Math.Round(SystemParameters.PrimaryScreenWidth);
            int height = (int)Math.Round(SystemParameters.PrimaryScreenHeight);
            if (width < 1 || height < 1)
            {
                width = 1920;
                height = 1080;
            }
            return new MonitorInfo(0, new PxRect(0, 0, width, height), DefaultDpi, true);
        }

        /// <summary>
        /// Sends one condition to both the log and the startup note sink.
        /// </summary>
        /// <param name="note">Note sink; may be null.</param>
        /// <param name="message">Text without a prefix; "monitor: " is added.</param>
        /// <remarks>
        /// Side effects: one log line and, when a sink exists, one balloon tip
        /// request. The log line is written first because it is the durable copy
        /// (see TrayIcon.ShowNote).
        /// </remarks>
        private static void Report(Action<string> note, string message)
        {
            AppLog.Line("monitor: " + message);
            if (note != null)
            {
                note(message);
            }
        }
    }
}
