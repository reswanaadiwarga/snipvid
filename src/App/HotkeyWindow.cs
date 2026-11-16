// HotkeyWindow - the hidden window that owns the registration (WO-06).
//
// WHY a window at all: RegisterHotKey delivers WM_HOTKEY as a *posted window
// message*, so the process needs a window to receive it. The alternatives are
// both worse here. A thread-bound registration (hwnd = NULL) only works while the
// registering thread pumps messages, and it ties the chord's lifetime to a thread
// nobody else can name. What SnipVid does own is a WPF dispatcher, and a WPF
// HwndSource puts a real HWND on that dispatcher without a XAML file or a visible
// window - which is exactly the shape DESIGN.md section 1 describes ("one hidden
// window, a hotkey, one tray icon").
//
// The window is created visible-less (no WS_VISIBLE) at 0x0, so it never appears
// on screen, in the taskbar or in Alt+Tab. It is not a message-only window on
// purpose: WM_HOTKEY is posted rather than broadcast, so either would work, and a
// normal hidden window avoids depending on that distinction.
//
// Threading: the HwndSource is created on the thread that calls the constructor,
// and WM_HOTKEY is dispatched on that same thread - the STA UI thread - because
// the message is posted, not delivered cross-thread. The hook therefore runs on
// the UI thread and callers may touch UI state from it without marshalling.
//
// This type owns no policy: it registers what it is told, reports the Win32 error
// verbatim, and forwards the message. Which chord, in which order, and what to do
// when both are taken are decisions of HotkeyRegistration in Program.cs.
using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using SnipVid.Logic;

namespace SnipVid
{
    /// <summary>
    /// A hidden window whose only job is to receive <c>WM_HOTKEY</c> for one
    /// registered chord and forward it to a callback.
    /// </summary>
    /// <remarks>
    /// Lifetime: one instance per process, alive for the whole run. Disposing it
    /// unregisters the chord, removes the message hook and destroys the HWND, in
    /// that order - removing the hook first would leave a live registration
    /// posting messages at a window nothing is watching, and destroying the HWND
    /// first would release the chord silently while the log claimed otherwise.
    /// </remarks>
    internal sealed class HotkeyWindow : IDisposable
    {
        /// <summary>
        /// Registration id. The id is what the window uses to tell its own
        /// registrations apart, so it must be stable for the life of the window;
        /// one chord is registered at a time, so one id is enough.
        /// </summary>
        /// <remarks>
        /// Win32 reserves 0xC000-0xFFFF for <c>GlobalAddAtom</c> ids; 1 is a plain
        /// per-window id and cannot collide with a system chord.
        /// </remarks>
        private const int RegistrationId = 1;

        /// <summary>The hidden window; also what keeps it alive (see the header).</summary>
        private readonly HwndSource _source;

        /// <summary>
        /// The hook delegate, kept in a field on purpose: <see cref="HwndSource.RemoveHook"/>
        /// removes a delegate by equality, so an anonymous lambda passed inline
        /// could never be unhooked.
        /// </summary>
        private readonly HwndSourceHook _hook;

        /// <summary>Raised once per press, on the UI thread.</summary>
        private readonly Action _onHotkey;

        /// <summary>True while a chord is actually owned by this window.</summary>
        private bool _registered;

        /// <summary>Set by <see cref="Dispose"/>; every later call is a no-op.</summary>
        private bool _disposed;

        /// <summary>
        /// Creates the hidden window and starts listening for
        /// <c>WM_HOTKEY</c>.
        /// </summary>
        /// <param name="onHotkey">
        /// Invoked once per press on the UI thread. May be null, in which case
        /// presses are swallowed.
        /// </param>
        /// <remarks>
        /// Side effects: creates a real HWND on the calling thread and adds a
        /// message hook. Must be called on the STA UI thread - the HwndSource takes
        /// that thread's dispatcher, and a hook on a thread that never pumps
        /// messages would silently never fire. Failure modes: a thread without a
        /// dispatcher (or a non-STA thread) surfaces as an
        /// <see cref="InvalidOperationException"/> from WPF; nothing is registered
        /// at that point, so there is nothing to clean up.
        /// </remarks>
        internal HotkeyWindow(Action onHotkey)
        {
            _onHotkey = onHotkey;

            HwndSourceParameters parameters = new HwndSourceParameters("SnipVid.HotkeyWindow");
            // WindowStyle 0 is WS_OVERLAPPED with no WS_VISIBLE: a top-level window
            // that is created but never shown. A visible style here would flash a
            // 0x0 window on the desktop and put SnipVid in Alt+Tab.
            parameters.WindowStyle = 0;
            parameters.Width = 0;
            parameters.Height = 0;
            parameters.PositionX = 0;
            parameters.PositionY = 0;

            _source = new HwndSource(parameters);
            _hook = OnMessage;
            _source.AddHook(_hook);
        }

        /// <summary>
        /// Attempts to claim <paramref name="chord"/> for this window, replacing
        /// any chord already held.
        /// </summary>
        /// <param name="chord">The chord to register; its mask and key are passed through unchanged.</param>
        /// <param name="errorCode">
        /// Zero on success, otherwise the Win32 error from the failing call -
        /// <see cref="Native.ErrorHotkeyAlreadyRegistered"/> when another process
        /// owns the chord.
        /// </param>
        /// <returns>True when the chord is now owned by this window.</returns>
        /// <remarks>
        /// Side effects: on success the chord stops reaching the focused
        /// application until it is unregistered. An existing registration is
        /// released first, so the registered chord and this object's state can
        /// never disagree. Failure modes: returns false with
        /// <paramref name="errorCode"/>; the window and any previous `_registered`
        /// flag are left consistent, so a failed retry leaves the object disarmed
        /// rather than claiming a chord it does not hold.
        /// </remarks>
        internal bool TryRegister(HotkeySpec chord, out int errorCode)
        {
            errorCode = 0;
            if (chord == null)
            {
                // Defensive: no chord is a programming error, not a user-visible
                // condition - treated as a failure so the caller's fallback chain
                // still reports something instead of throwing from a message hook.
                errorCode = 87; // ERROR_INVALID_PARAMETER
                return false;
            }

            if (_disposed)
            {
                errorCode = 6; // ERROR_INVALID_HANDLE: the HWND is gone.
                return false;
            }

            Unregister();

            if (Native.RegisterHotKey(_source.Handle, RegistrationId, chord.Modifiers, chord.VirtualKey))
            {
                _registered = true;
                return true;
            }

            errorCode = Marshal.GetLastWin32Error();
            return false;
        }

        /// <summary>
        /// Releases the currently registered chord, if any. Safe to call when
        /// nothing is registered and safe to call repeatedly.
        /// </summary>
        /// <remarks>
        /// Side effects: the chord reaches the focused application again. Failure
        /// modes: a false return is ignored - it means the id was not registered
        /// for this window, which is the state this method is trying to reach
        /// anyway.
        /// </remarks>
        internal void Unregister()
        {
            if (!_registered)
            {
                return;
            }
            _registered = false;
            Native.UnregisterHotKey(_source.Handle, RegistrationId);
        }

        /// <summary>
        /// Unregisters the chord, removes the message hook and destroys the hidden
        /// window. Safe to call more than once.
        /// </summary>
        /// <remarks>
        /// Side effects: destroys the HWND, so no further press can be delivered.
        /// The chord is released before the window goes away so that a subsequent
        /// registration by another process cannot race a still-live claim.
        /// </remarks>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            Unregister();
            _source.RemoveHook(_hook);
            _source.Dispose();
        }

        /// <summary>
        /// Message hook: forwards <c>WM_HOTKEY</c> for this window's registration
        /// id and marks it handled.
        /// </summary>
        /// <param name="hwnd">The hidden window (unused; there is only one).</param>
        /// <param name="msg">The message id.</param>
        /// <param name="wParam">The registration id for <c>WM_HOTKEY</c>.</param>
        /// <param name="lParam">Modifiers and key, in the low and high words.</param>
        /// <param name="handled">Set to true so WPF does not process the message further.</param>
        /// <returns>Always <see cref="IntPtr.Zero"/>; the return value of a handled WPF hook is unused.</returns>
        /// <remarks>
        /// Side effects: calls <see cref="_onHotkey"/> synchronously on the UI
        /// thread. The id is checked so a message meant for a registration this
        /// window no longer holds cannot start a capture; the callback itself is
        /// never allowed to throw a Win32 message loop down - an exception there
        /// would tear down the dispatcher and the tray with it - so it is called
        /// in a guard that logs and swallows.
        /// </remarks>
        private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != Native.WmHotkey || wParam.ToInt32() != RegistrationId)
            {
                return IntPtr.Zero;
            }

            handled = true;
            if (_onHotkey == null)
            {
                return IntPtr.Zero;
            }

            try
            {
                _onHotkey();
            }
            catch (Exception ex)
            {
                // A press must never take the process down; the log is the only
                // place a failure inside the handler can be seen at all.
                AppLog.Line("hotkey: handler failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            return IntPtr.Zero;
        }
    }
}
