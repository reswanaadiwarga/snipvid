// AudioDevices - the microphone's Windows identity, for ffmpeg's dshow input.
//
// WHY this file exists: config.md has no fixed microphone (the user swaps audio
// hardware and wants "whatever is default now"), so the name handed to ffmpeg
// has to be asked of Windows at the moment a recording starts. Core Audio is
// the only API that answers "what is the default capture endpoint", and
// IMMDeviceEnumerator::GetDefaultAudioEndpoint is that answer. This file is the
// thin, untestable edge that talks to COM; every decision built on its result
// lives in FfmpegArgs (argv) and Config (the mic= key), both pure (DESIGN.md
// section 11).
//
// The name that crosses the API boundary is PKEY_Device_FriendlyName, the same
// string the Sound control panel shows. ffmpeg's dshow input wants the
// DirectShow device name, which on this machine is the same string - measured
// 2026-11-16: Core Audio "Microphone (Realtek(R) Audio)" and dshow's
// `-list_devices` both spell it identically. The two enumerations are separate
// APIs, so this is an assumption, not a contract: if a future device spells
// them differently, ffmpeg fails at spawn with its own clear message ("Could
// not find audio only device with name [...] among source devices of type
// audio") and the recording is refused rather than silently recorded without
// sound.
//
// Failure is total, never thrown: a missing audio service, a disabled endpoint,
// a driver that will not answer - every path returns null plus a one-line
// reason. Recording without audio is always better than refusing to record
// (C7). The caller logs the reason; this file writes no log lines, so the
// failure is reported at exactly one place per resolution attempt.
//
// Threading: the enumerator is created and used on the caller's thread. This app
// calls it from the [STAThread] UI thread only. COM handles are released in
// finally blocks, including on the failure paths, so a broken driver cannot
// leak an apartment reference per recording.
//
// ASCII-only source: csc falls back to the ANSI codepage for a file that is not
// valid UTF-8, and a device name is user data, never a literal.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SnipVid
{
    /// <summary>
    /// Resolves the default Windows capture endpoint (microphone) and lists the
    /// active capture endpoints, for ffmpeg's dshow input.
    /// </summary>
    /// <remarks>
    /// Pure COM interop: no WPF, no state, no logging. Every method is total -
    /// a failure returns null/empty plus an explanation and never throws at the
    /// caller, because this runs inside a recording start where an exception
    /// would abort the capture.
    /// </remarks>
    internal static class AudioDevices
    {
        /// <summary>EDataFlow.eCapture: capture endpoints only, never playback.</summary>
        private const int CaptureFlow = 1;

        /// <summary>ERole.eCommunications: the role Windows itself calls "the microphone".</summary>
        /// <remarks>
        /// Chosen over eConsole/eMultimedia because it is the role a chat app
        /// uses, and the user's stated intent is "the mic I am talking into".
        /// Windows keeps all three roles pointing at the same endpoint unless the
        /// user deliberately splits them.
        /// </remarks>
        private const int CommunicationsRole = 2;

        /// <summary>DEVICE_STATE_ACTIVE: present and enabled; disabled or unplugged excluded.</summary>
        private const int ActiveDevices = 0x1;

        /// <summary>STGM_READ: the property store is only read, never written.</summary>
        private const int ReadOnlyStore = 0;

        /// <summary>VARTYPE for VT_LPWSTR, the type PKEY_Device_FriendlyName is stored as.</summary>
        private const ushort VariantTypeString = 31;

        /// <summary>PKEY_Device_FriendlyName's property id (fmtid a45c254e-df1c-4efd-8020-67d146a850e0).</summary>
        private const int FriendlyNameId = 14;

        /// <summary>PKEY_Device_DeviceDesc's property id, the fallback when FriendlyName is absent.</summary>
        private const int DeviceDescriptionId = 2;

        /// <summary>
        /// The default capture endpoint's friendly name, resolved now.
        /// </summary>
        /// <param name="detail">
        /// Null on success; otherwise one human-readable reason, for the caller's
        /// single log line. Always assigned, so a caller can log unconditionally.
        /// </param>
        /// <returns>
        /// The endpoint's friendly name, for example "Microphone (Realtek(R) Audio)",
        /// or null when Windows has no default capture endpoint or the query failed.
        /// </returns>
        /// <remarks>
        /// Side effects: creates and releases COM objects; may start the audio
        /// service. Never throws. Resolving per recording (not once at startup) is
        /// deliberate: the user switches hardware and expects the box they are
        /// talking into, not the one that was default when the app launched.
        /// </remarks>
        internal static string DefaultCaptureName(out string detail)
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            try
            {
                enumerator = CreateEnumerator();
                if (enumerator == null)
                {
                    detail = "the audio endpoint enumerator could not be created";
                    return null;
                }

                int hr = enumerator.GetDefaultAudioEndpoint(CaptureFlow, CommunicationsRole, out device);
                if (hr < 0 || device == null)
                {
                    // E_NOTFOUND (0x80070490) is the normal answer on a machine
                    // with no microphone at all; any other failure is a driver or
                    // service problem. Both mean the same thing here: no audio.
                    detail = "no default capture device (HRESULT 0x"
                        + hr.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ")";
                    return null;
                }

                string name = ReadName(device);
                if (string.IsNullOrEmpty(name))
                {
                    detail = "the default capture device has no name";
                    return null;
                }

                detail = null;
                return name;
            }
            catch (Exception ex)
            {
                // Deliberately broad: a COM, driver or marshalling failure must not
                // abort a recording that has otherwise started fine.
                detail = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
            finally
            {
                Release(device);
                Release(enumerator);
            }
        }

        /// <summary>
        /// The friendly names of every active capture endpoint, in Windows' order.
        /// </summary>
        /// <param name="detail">Null on success; otherwise one reason, as in
        /// <see cref="DefaultCaptureName"/>.</param>
        /// <returns>
        /// A list (possibly empty) of names; never null. Empty means either no
        /// microphone is present or the enumeration failed - <paramref name="detail"/>
        /// distinguishes them.
        /// </returns>
        /// <remarks>
        /// Side effects: creates and releases COM objects. Never throws. This list
        /// is for the log: it is what lets a user see which name ffmpeg will be
        /// offered and why the default is not the one they expected. It is not used
        /// to validate a configured name (Config stores that verbatim, and a bad
        /// name is ffmpeg's runtime failure).
        /// </remarks>
        internal static List<string> CaptureNames(out string detail)
        {
            List<string> names = new List<string>();
            IMMDeviceEnumerator enumerator = null;
            IMMDeviceCollection collection = null;
            try
            {
                enumerator = CreateEnumerator();
                if (enumerator == null)
                {
                    detail = "the audio endpoint enumerator could not be created";
                    return names;
                }

                int hr = enumerator.EnumAudioEndpoints(CaptureFlow, ActiveDevices, out collection);
                if (hr < 0 || collection == null)
                {
                    detail = "capture endpoint enumeration failed (HRESULT 0x"
                        + hr.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ")";
                    return names;
                }

                uint count;
                hr = collection.GetCount(out count);
                if (hr < 0)
                {
                    detail = "capture endpoint count failed (HRESULT 0x"
                        + hr.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ")";
                    return names;
                }

                for (uint i = 0; i < count; i++)
                {
                    IMMDevice device = null;
                    try
                    {
                        // One unreadable endpoint must not hide the others.
                        if (collection.Item(i, out device) >= 0 && device != null)
                        {
                            string name = ReadName(device);
                            if (!string.IsNullOrEmpty(name))
                            {
                                names.Add(name);
                            }
                        }
                    }
                    finally
                    {
                        Release(device);
                    }
                }

                detail = null;
                return names;
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ": " + ex.Message;
                return names;
            }
            finally
            {
                Release(collection);
                Release(enumerator);
            }
        }

        /// <summary>
        /// Creates the Core Audio endpoint enumerator.
        /// </summary>
        /// <returns>The enumerator, or null when COM refused to create it.</returns>
        /// <remarks>
        /// Side effects: activates the MMDeviceEnumerator COM class. Returns null
        /// rather than throwing so every caller has one failure shape.
        /// </remarks>
        private static IMMDeviceEnumerator CreateEnumerator()
        {
            try
            {
                return (IMMDeviceEnumerator)new MmDeviceEnumeratorComObject();
            }
            catch (COMException)
            {
                return null;
            }
        }

        /// <summary>
        /// Reads an endpoint's display name from its property store.
        /// </summary>
        /// <param name="device">Endpoint to read; must not be null.</param>
        /// <returns>
        /// FriendlyName, else DeviceDesc, else null when neither is a string.
        /// </returns>
        /// <remarks>
        /// Side effects: opens a read-only property store and frees the returned
        /// PROPVARIANT string. DeviceDesc is the fallback because a device with no
        /// INF-provided friendly name still has a description, and a name is better
        /// than no audio. Never throws for a failing property store: the caller's
        /// contract is that a name is either available or it is not.
        /// </remarks>
        private static string ReadName(IMMDevice device)
        {
            IPropertyStore store = null;
            try
            {
                if (device.OpenPropertyStore(ReadOnlyStore, out store) < 0 || store == null)
                {
                    return null;
                }

                string name = ReadString(store, FriendlyNameId);
                return string.IsNullOrEmpty(name) ? ReadString(store, DeviceDescriptionId) : name;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                Release(store);
            }
        }

        /// <summary>
        /// Reads one string property and frees its COM-allocated storage.
        /// </summary>
        /// <param name="store">Open property store.</param>
        /// <param name="propertyId">Property id within the device property set.</param>
        /// <returns>The string, or null when the property is absent or not a string.</returns>
        /// <remarks>
        /// Side effects: reads the property and calls CoTaskMemFree on the returned
        /// VT_LPWSTR. That is what PropVariantClear does for this variant type, and
        /// doing it here avoids declaring the 24-byte union purely to clear one
        /// string. A variant of any other type is left alone - only LPWSTR and BSTR
        /// own allocations, and neither is freed through the wrong allocator.
        /// </remarks>
        private static string ReadString(IPropertyStore store, int propertyId)
        {
            PropertyKey key = new PropertyKey();
            key.FormatId = DevicePropertySet;
            key.PropertyId = propertyId;

            PropVariant value;
            if (store.GetValue(ref key, out value) < 0)
            {
                return null;
            }
            if (value.VariantType != VariantTypeString || value.Pointer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(value.Pointer);
            }
            finally
            {
                Marshal.FreeCoTaskMem(value.Pointer);
            }
        }

        /// <summary>Releases one COM object, tolerating null and a non-COM reference.</summary>
        /// <param name="instance">Object to release; may be null.</param>
        /// <remarks>
        /// Side effects: decrements the COM reference count. Never throws: a failure
        /// here must not turn a resolution into an exception. A managed object that
        /// is not a COM object is ignored, which keeps the finally blocks simple.
        /// </remarks>
        private static void Release(object instance)
        {
            if (instance == null || !Marshal.IsComObject(instance))
            {
                return;
            }
            try
            {
                Marshal.ReleaseComObject(instance);
            }
            catch (Exception)
            {
                // A broken reference count is not worth failing a recording over.
            }
        }

        /// <summary>The device property set that holds FriendlyName and DeviceDesc.</summary>
        private static readonly Guid DevicePropertySet =
            new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");

        /// <summary>The MMDeviceEnumerator COM class (CLSID BCDE0395-E52F-467C-8E3D-C4579291692E).</summary>
        /// <remarks>
        /// ComImport + Guid on a class is the documented way to activate a COM
        /// server from C#: the runtime calls CoCreateInstance with this CLSID when
        /// the instance is cast to its interface.
        /// </remarks>
        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MmDeviceEnumeratorComObject
        {
        }

        /// <summary>PROPERTYKEY: a format GUID plus one property id.</summary>
        /// <remarks>Sequential, 20 bytes, the exact Win32 layout.</remarks>
        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            internal Guid FormatId;
            internal int PropertyId;
        }

        /// <summary>
        /// PROPVARIANT, sized for x64 (24 bytes) and x86 (16 bytes).
        /// </summary>
        /// <remarks>
        /// Only the header and the pointer member are named, plus one trailing
        /// IntPtr of padding so the marshalled block is never smaller than the real
        /// union. The union is 8 bytes on x86 and 16 on x64; the padding member is
        /// what makes the sequential size match on both, so COM cannot write past
        /// the end of a stack local.
        /// </remarks>
        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant
        {
            internal ushort VariantType;
            internal ushort Reserved1;
            internal ushort Reserved2;
            internal ushort Reserved3;
            internal IntPtr Pointer;
            internal IntPtr Padding;
        }

        /// <summary>IMMDeviceEnumerator (A95664D2-9614-4F35-A746-DE8DB63617E6).</summary>
        /// <remarks>
        /// Methods are in vtable order and return HRESULTs. Unused ones are still
        /// declared - a missing slot would shift every later method onto the wrong
        /// vtable entry, which fails at call time with a plausible-looking HRESULT.
        /// </remarks>
        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);

            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);

            int GetDevice(IntPtr id, out IMMDevice device);

            int RegisterEndpointNotificationCallback(IntPtr client);

            int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        /// <summary>IMMDeviceCollection (0BD7A1BE-7A1A-44DB-8397-CC5392387B5E).</summary>
        [ComImport]
        [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            int GetCount(out uint count);

            int Item(uint index, out IMMDevice device);
        }

        /// <summary>IMMDevice (D666063F-1587-4E43-81F1-B948E807363F).</summary>
        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid interfaceId, int classContext, IntPtr activationParams, out IntPtr instance);

            int OpenPropertyStore(int access, out IPropertyStore properties);

            int GetId(out string id);

            int GetState(out int state);
        }

        /// <summary>IPropertyStore (886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99).</summary>
        [ComImport]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            int GetCount(out uint count);

            int GetAt(uint index, out PropertyKey key);

            int GetValue(ref PropertyKey key, out PropVariant value);

            int SetValue(ref PropertyKey key, ref PropVariant value);

            int Commit();
        }
    }
}
