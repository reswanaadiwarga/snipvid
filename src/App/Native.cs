// Native - the entire P/Invoke surface this build needs: hotkeys (WO-06) and
// monitor enumeration (WO-08).
//
// WHY this file exists at all: src\Logic may not contain a DllImport (DESIGN.md
// section 11), so the calls that touch the OS live here, in App, where the rest
// of the untestable plumbing already is. It is deliberately tiny - only the
// symbols the dispatched work orders use. SetWindowDisplayAffinity and the
// job-object calls belong to WO-11 and WO-09 and are added there, not
// speculatively here.
//
// user32.dll carries the window and monitor calls; shcore.dll carries exactly
// one, GetDpiForMonitor, which has no user32 equivalent (WO-08). Every entry
// point that reports failure is declared with SetLastError so the caller reads
// the real reason with Marshal.GetLastWin32Error(); without that flag the value
// is whatever the last unrelated call left behind, and the
// ERROR_HOTKEY_ALREADY_REGISTERED decision that C2/C3 depend on would be a coin
// flip.
//
// No WPF types are used here, so nothing in this file needs a dispatcher. The
// RECT and MONITORINFO mirrors are named NativeRect/NativeMonitorInfo because
// System.Windows.Rect exists and an accidental `Rect` in a file that later
// imports WPF namespaces would silently mean the wrong type.
using System;
using System.Runtime.InteropServices;

namespace SnipVid
{
    /// <summary>
    /// The Win32 calls and constants used for hotkey registration and for
    /// recognising the hotkey message on the hidden window.
    /// </summary>
    /// <remarks>
    /// Thin and stateless: no wrapper logic lives here, because every decision
    /// about what to do with a failure is a policy the caller documents (the
    /// fallback and disarm rules of DESIGN.md section 9 are in
    /// <see cref="HotkeyRegistration"/>, not in a P/Invoke declaration).
    /// </remarks>
    internal static class Native
    {
        /// <summary>
        /// WM_HOTKEY: posted to the registering window when its chord is pressed.
        /// </summary>
        /// <remarks>
        /// A posted message, not a broadcast, which is why a hidden window receives
        /// it. <c>wParam</c> carries the registration id and <c>lParam</c> the two
        /// key halves.
        /// </remarks>
        internal const int WmHotkey = 0x0312;

        /// <summary>
        /// ERROR_HOTKEY_ALREADY_REGISTERED: another process (or this one) already
        /// owns the chord.
        /// </summary>
        /// <remarks>
        /// The one failure DESIGN.md section 9 defines a recovery for - fall back to
        /// Ctrl+Alt+Shift+F9 - so it is named instead of comparing against a
        /// literal 1409 at the call site.
        /// </remarks>
        internal const int ErrorHotkeyAlreadyRegistered = 1409;

        /// <summary>
        /// Registers a system-wide chord that posts <see cref="WmHotkey"/> to
        /// <paramref name="window"/>.
        /// </summary>
        /// <param name="window">
        /// Window that receives the message; the hidden hotkey window's handle.
        /// IntPtr.Zero would bind the chord to the calling thread instead, which
        /// leaves no handle for <see cref="UnregisterHotKey"/> to name.
        /// </param>
        /// <param name="id">Registration id in 0x0000-0xBFFF, unique per window.</param>
        /// <param name="modifiers">
        /// Bitwise combination of the MOD_* values, which are also exported by
        /// HotkeySpec; never 0 (a zero mask captures a bare key system-wide).
        /// </param>
        /// <param name="virtualKey">Win32 virtual-key code, for example 0x53 for 'S'.</param>
        /// <returns>True when the chord is now owned by this window.</returns>
        /// <remarks>
        /// Side effects: claims the chord for every process in this logon session
        /// until <see cref="UnregisterHotKey"/> runs or the window is destroyed; the
        /// keystroke stops reaching the focused application. Failure modes: returns
        /// false and sets the last error, most usefully
        /// <see cref="ErrorHotkeyAlreadyRegistered"/>. Never throws a managed
        /// exception.
        /// </remarks>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(IntPtr window, int id, int modifiers, int virtualKey);

        /// <summary>
        /// Releases a chord previously registered by
        /// <see cref="RegisterHotKey"/> for the same window and id.
        /// </summary>
        /// <param name="window">The window the chord was registered for.</param>
        /// <param name="id">The id it was registered with.</param>
        /// <returns>True when a registration was released.</returns>
        /// <remarks>
        /// Side effects: the chord reaches the focused application again. Failure
        /// modes: returns false when the id is not registered for that window -
        /// harmless, and it is what a double unregister looks like. The process
        /// tearing down would release the chord anyway; calling this explicitly is
        /// what makes the release observable before the process exits, and what
        /// keeps the "no hotkey left behind" check honest after a normal quit.
        /// </remarks>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(IntPtr window, int id);

        /// <summary>
        /// MONITORINFOF_PRIMARY: this monitor is the primary display.
        /// </summary>
        /// <remarks>Reported in <see cref="NativeMonitorInfo.Flags"/>.</remarks>
        internal const int MonitorInfoPrimary = 1;

        /// <summary>
        /// MDT_EFFECTIVE_DPI: the DPI the desktop is actually rendered at, which
        /// is the scale WPF lays the overlay out in.
        /// </summary>
        /// <remarks>
        /// MDT_ANGULAR_DPI and MDT_RAW_DPI are deliberately not used: they report
        /// the panel's physical characteristics and do not match the DIP grid
        /// (DESIGN.md section 4 is about the DIP grid).
        /// </remarks>
        internal const int MdtEffectiveDpi = 0;

        /// <summary>
        /// Win32 RECT: edges in physical pixels, right/bottom exclusive.
        /// </summary>
        /// <remarks>
        /// Sequential layout so the marshaler can blit it; no properties, because
        /// nothing here does arithmetic on a RECT - <see cref="Monitors"/>
        /// converts it to the Logic layer's PxRect first.
        /// </remarks>
        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeRect
        {
            /// <summary>Left edge in physical pixels.</summary>
            public int Left;

            /// <summary>Top edge in physical pixels.</summary>
            public int Top;

            /// <summary>Exclusive right edge in physical pixels.</summary>
            public int Right;

            /// <summary>Exclusive bottom edge in physical pixels.</summary>
            public int Bottom;
        }

        /// <summary>
        /// MONITORINFO: what <see cref="GetMonitorInfo"/> fills in for one monitor.
        /// </summary>
        /// <remarks>
        /// The MONITORINFOEX variant is not mirrored: its extra monitor name is
        /// unused by this build and would only add an unmanaged string to marshal.
        /// </remarks>
        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeMonitorInfo
        {
            /// <summary>
            /// Size of this structure in bytes; the caller must set it before the
            /// call or GetMonitorInfo fails with ERROR_INVALID_PARAMETER.
            /// </summary>
            public int Size;

            /// <summary>The monitor's rectangle in the virtual-screen frame, physical pixels.</summary>
            public NativeRect Monitor;

            /// <summary>The monitor's work area (the rectangle minus taskbar and docked appbars).</summary>
            public NativeRect Work;

            /// <summary><see cref="MonitorInfoPrimary"/> when this is the primary display, else 0.</summary>
            public int Flags;
        }

        /// <summary>
        /// Callback signature for <see cref="EnumDisplayMonitors"/>.
        /// </summary>
        /// <param name="monitor">Handle of one display monitor.</param>
        /// <param name="deviceContext">A DC for the monitor; unused here.</param>
        /// <param name="rect">The monitor's rectangle, in the same frame as NativeMonitorInfo.Monitor.</param>
        /// <param name="data">Caller data passed through unchanged; unused here.</param>
        /// <returns>True to continue enumeration, false to stop.</returns>
        /// <remarks>
        /// Must stay referenced for the whole call: the GC cannot see a native
        /// callback, so a delegate that is only an inline lambda argument may be
        /// collected mid-enumeration. <see cref="Monitors"/> keeps it in a local
        /// and calls GC.KeepAlive afterwards for exactly that reason.
        /// </remarks>
        internal delegate bool MonitorEnumProc(
            IntPtr monitor, IntPtr deviceContext, ref NativeRect rect, IntPtr data);

        /// <summary>
        /// Enumerates every display monitor in the session.
        /// </summary>
        /// <param name="deviceContext">
        /// IntPtr.Zero to enumerate all monitors that intersect the virtual screen.
        /// </param>
        /// <param name="clip">IntPtr.Zero for no clipping rectangle.</param>
        /// <param name="callback">Invoked once per monitor, in enumeration order.</param>
        /// <param name="data">Opaque value passed back to the callback.</param>
        /// <returns>False when the enumeration itself failed; true otherwise.</returns>
        /// <remarks>
        /// Side effects: none beyond the callback. Failure modes: returns false and
        /// sets the last error; the callback returning false also stops the
        /// enumeration and makes this return false, which is why the callback used
        /// here always returns true.
        /// </remarks>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayMonitors(
            IntPtr deviceContext, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        /// <summary>
        /// Fills in the rectangle, work area and primary flag for one monitor.
        /// </summary>
        /// <param name="monitor">Handle from <see cref="EnumDisplayMonitors"/>.</param>
        /// <param name="info">
        /// Structure to fill; its <see cref="NativeMonitorInfo.Size"/> field must
        /// already hold the size in bytes.
        /// </param>
        /// <returns>True on success, false with a last error otherwise.</returns>
        /// <remarks>
        /// Side effects: writes through <paramref name="info"/>. The rectangles are
        /// in the virtual-screen frame and in physical pixels, because this process
        /// is PerMonitorV2-aware via app.manifest (otherwise Windows would return
        /// DPI-virtualised coordinates).
        /// </remarks>
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo info);

        /// <summary>
        /// Reports the effective DPI of one monitor, per axis.
        /// </summary>
        /// <param name="monitor">Handle from <see cref="EnumDisplayMonitors"/>.</param>
        /// <param name="dpiType">Usually <see cref="MdtEffectiveDpi"/>.</param>
        /// <param name="dpiX">Horizontal DPI; 96 at 100 % scale.</param>
        /// <param name="dpiY">Vertical DPI; 96 at 100 % scale.</param>
        /// <returns>
        /// An HRESULT: 0 (S_OK) on success, non-zero on failure. It does not set
        /// the last error, so it is checked as a return value, not through
        /// Marshal.GetLastWin32Error.
        /// </returns>
        /// <remarks>
        /// Side effects: none. Failure modes: a non-zero HRESULT for an invalid
        /// handle or an unsupported DPI type, and - the case WO-08 C3 exists for -
        /// an <see cref="EntryPointNotFoundException"/> or
        /// <see cref="DllNotFoundException"/> on a Windows build without
        /// shcore.dll (pre-8.1). Callers treat every failure as 96 dpi plus one
        /// note, because losing the scale is survivable and losing the capture is
        /// not.
        /// </remarks>
        [DllImport("shcore.dll")]
        internal static extern int GetDpiForMonitor(
            IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
    }
}
