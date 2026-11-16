// JobObject - the kernel-enforced promise that ffmpeg dies with SnipVid
// (DESIGN.md section 1).
//
// WHY a job object rather than Process.Kill in a finally block: a finally block
// does not run when the process is ended by Task Manager, by a logoff, or by a
// second failure inside the cleanup path. A job with
// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE is enforced by the kernel: when the last
// handle to the job closes - which happens when this process dies, however it
// dies - every process still in the job is terminated. That is what makes AC6
// possible: kill SnipVid mid-recording and the recorder cannot keep writing into
// the user's Videos folder.
//
// WHY the handle lives for the whole recording: closing it early would kill the
// child immediately, so the handle's lifetime *is* the child's lifetime
// guarantee. The owner is Recorder, which creates the job before the child and
// disposes it only after the child has exited.
//
// WHY the child is created suspended and assigned before it runs: a process that
// is already running can spawn or open handles before it is in the job, and the
// assignment is not retroactive for work it has already done. Creating it
// suspended, assigning it, then resuming it leaves no window in which the child
// exists outside the job (Recorder does that three-step dance).
//
// .NET Framework 4.8 has no managed job-object wrapper, so the three kernel32
// calls are declared here. src\Logic may not contain a DllImport (DESIGN.md
// section 11), which is why this is App code: it is verified by running the app
// and killing it, not by test.ps1.
using System;
using System.Runtime.InteropServices;

namespace SnipVid
{
    /// <summary>
    /// An anonymous job object configured to terminate its members when the last
    /// handle to it closes.
    /// </summary>
    /// <remarks>
    /// Not thread-affine: the handle can be assigned and disposed from any thread,
    /// although Recorder happens to use it from the UI thread only. Disposing is
    /// safe to call more than once. This type throws no managed exception for a
    /// Win32 failure: every failure is reported as a false return plus a message,
    /// because the caller has the user-facing note channel.
    /// </remarks>
    internal sealed class JobObject : IDisposable
    {
        /// <summary>
        /// JobObjectExtendedLimitInformation: the information class that accepts
        /// <see cref="LimitKillOnJobClose"/>.
        /// </summary>
        /// <remarks>
        /// The basic class (2) also has a LimitFlags field, but the documented way
        /// to set kill-on-close is the extended class, and only that one is used
        /// here so there is one path to be wrong about.
        /// </remarks>
        private const int ExtendedLimitInformationClass = 9;

        /// <summary>
        /// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: terminate every member when the last
        /// job handle closes.
        /// </summary>
        /// <remarks>
        /// This single flag is the entire reason the type exists; it is what turns
        /// "the app crashed" into "the recording stopped" instead of an orphan
        /// ffmpeg writing for the rest of the session.
        /// </remarks>
        private const int LimitKillOnJobClose = 0x00002000;

        /// <summary>
        /// The job handle; zero once <see cref="Dispose"/> has run.
        /// </summary>
        /// <remarks>
        /// Holding this is what keeps the job alive. Closing it - including by this
        /// process being terminated - is what kills the members.
        /// </remarks>
        private IntPtr _handle;

        /// <summary>Guards <see cref="_handle"/> against a double dispose.</summary>
        private readonly object _gate = new object();

        /// <summary>Creates the job and applies the kill-on-close limit.</summary>
        /// <param name="error">
        /// Null on success, otherwise one sentence naming the failed call and its
        /// Win32 error. The caller turns this into a note and spawns nothing.
        /// </param>
        /// <returns>The created job, or null when the job could not be configured.</returns>
        /// <remarks>
        /// Side effects: creates one kernel handle. Failure modes: CreateJobObject
        /// or SetInformationJobObject failing - both leave no live job, so nothing
        /// half-configured is returned. The caller must dispose the result; a
        /// failure here aborts a capture rather than recording without the
        /// guarantee, because "the child outlives the app" is the one behaviour
        /// this project must never have.
        /// </remarks>
        internal static JobObject Create(out string error)
        {
            error = null;
            IntPtr handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero)
            {
                error = "CreateJobObject failed with windows error " + Marshal.GetLastWin32Error();
                return null;
            }

            JobObjectExtendedLimitInformation limits = new JobObjectExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = LimitKillOnJobClose;

            // The marshalled size must be the structure's real size, not the size
            // of the flags field: SetInformationJobObject rejects a truncated
            // buffer with ERROR_BAD_LENGTH and silently keeps the old limits.
            int size = Marshal.SizeOf(typeof(JobObjectExtendedLimitInformation));
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, buffer, false);
                if (!SetInformationJobObject(handle, ExtendedLimitInformationClass, buffer, (uint)size))
                {
                    error = "SetInformationJobObject(JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE) failed with windows error "
                        + Marshal.GetLastWin32Error();
                    CloseHandle(handle);
                    return null;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            JobObject job = new JobObject();
            job._handle = handle;
            return job;
        }

        /// <summary>
        /// Adds a process to this job.
        /// </summary>
        /// <param name="processHandle">
        /// Handle to the child, with PROCESS_SET_QUOTA and PROCESS_TERMINATE
        /// access. Recorder passes the handle returned by CreateProcess, which has
        /// both.
        /// </param>
        /// <param name="error">Null on success, otherwise the Win32 failure text.</param>
        /// <returns>True when the child is now a member of this job.</returns>
        /// <remarks>
        /// Side effects: the child is now subject to this job's limits, including
        /// termination when the job handle closes. Failure modes: returns false for
        /// an invalid handle, a disposed job, or an outer job that forbids nested
        /// assignment; Recorder treats a false return as a fatal spawn error,
        /// because a child outside the job would survive the app.
        /// </remarks>
        internal bool Assign(IntPtr processHandle, out string error)
        {
            error = null;
            if (processHandle == IntPtr.Zero)
            {
                error = "AssignProcessToJobObject was given a null process handle";
                return false;
            }

            IntPtr handle;
            lock (_gate)
            {
                handle = _handle;
            }
            if (handle == IntPtr.Zero)
            {
                error = "AssignProcessToJobObject was called on a disposed job";
                return false;
            }

            if (!AssignProcessToJobObject(handle, processHandle))
            {
                error = "AssignProcessToJobObject failed with windows error " + Marshal.GetLastWin32Error();
                return false;
            }
            return true;
        }

        /// <summary>
        /// Closes the job handle, which terminates every process still in the job.
        /// Safe to call more than once.
        /// </summary>
        /// <remarks>
        /// Side effects: kills any surviving member - that is the intended effect,
        /// not a side effect to work around - and releases the handle. Called after
        /// the recorded child has exited, so in the normal path there is nothing
        /// left to kill.
        /// </remarks>
        public void Dispose()
        {
            IntPtr handle;
            lock (_gate)
            {
                handle = _handle;
                _handle = IntPtr.Zero;
            }
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }

        /// <summary>
        /// JOBOBJECT_BASIC_LIMIT_INFORMATION: the limits that apply to the job as a
        /// whole and to each process in it.
        /// </summary>
        /// <remarks>
        /// Layout mirrors the Win32 structure exactly, including the ULONG_PTR
        /// fields; a mismatched size makes SetInformationJobObject fail with
        /// ERROR_BAD_LENGTH, which is why the fields use UIntPtr rather than int.
        /// </remarks>
        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            /// <summary>Per-process user-mode CPU time limit; unused (no limit).</summary>
            public long PerProcessUserTimeLimit;

            /// <summary>Per-job user-mode CPU time limit; unused (no limit).</summary>
            public long PerJobUserTimeLimit;

            /// <summary>Combination of JOB_OBJECT_LIMIT_* flags; only kill-on-close is set.</summary>
            public int LimitFlags;

            /// <summary>Minimum working set; unused.</summary>
            public UIntPtr MinimumWorkingSetSize;

            /// <summary>Maximum working set; unused.</summary>
            public UIntPtr MaximumWorkingSetSize;

            /// <summary>Maximum simultaneous processes; unused.</summary>
            public int ActiveProcessLimit;

            /// <summary>Processor affinity; unused.</summary>
            public UIntPtr Affinity;

            /// <summary>Priority class; unused.</summary>
            public int PriorityClass;

            /// <summary>Scheduling class; unused.</summary>
            public int SchedulingClass;
        }

        /// <summary>
        /// IO_COUNTERS: per-job I/O accounting, present in the extended structure
        /// and left at zero.
        /// </summary>
        /// <remarks>Untouched fields exist only because the structure must be complete.</remarks>
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            /// <summary>Number of read operations.</summary>
            public ulong ReadOperationCount;

            /// <summary>Number of write operations.</summary>
            public ulong WriteOperationCount;

            /// <summary>Number of other operations.</summary>
            public ulong OtherOperationCount;

            /// <summary>Bytes read.</summary>
            public ulong ReadTransferCount;

            /// <summary>Bytes written.</summary>
            public ulong WriteTransferCount;

            /// <summary>Bytes transferred by other operations.</summary>
            public ulong OtherTransferCount;
        }

        /// <summary>
        /// JOBOBJECT_EXTENDED_LIMIT_INFORMATION: the structure
        /// SetInformationJobObject expects for class 9.
        /// </summary>
        /// <remarks>
        /// The memory fields are unused; they are present so the size the kernel
        /// validates matches what it expects.
        /// </remarks>
        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            /// <summary>The basic limits, where kill-on-close is set.</summary>
            public JobObjectBasicLimitInformation BasicLimitInformation;

            /// <summary>I/O accounting, unused.</summary>
            public IoCounters IoInfo;

            /// <summary>Per-process committed memory limit; unused.</summary>
            public UIntPtr ProcessMemoryLimit;

            /// <summary>Per-job committed memory limit; unused.</summary>
            public UIntPtr JobMemoryLimit;

            /// <summary>Peak per-process memory; unused.</summary>
            public UIntPtr PeakProcessMemoryUsed;

            /// <summary>Peak per-job memory; unused.</summary>
            public UIntPtr PeakJobMemoryUsed;
        }

        /// <summary>Creates an anonymous job object.</summary>
        /// <param name="securityAttributes">Always <see cref="IntPtr.Zero"/>: no inheritance needed.</param>
        /// <param name="name">Always null: anonymous, so no other process can open it.</param>
        /// <returns>The job handle, or zero on failure.</returns>
        /// <remarks>Side effects: creates a kernel handle that the caller must close.</remarks>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string name);

        /// <summary>Applies one information class to a job.</summary>
        /// <param name="job">Job handle.</param>
        /// <param name="informationClass">9 for the extended limits used here.</param>
        /// <param name="information">Pointer to the marshalled structure.</param>
        /// <param name="length">Size of that structure in bytes.</param>
        /// <returns>True on success, false with a last error otherwise.</returns>
        /// <remarks>Side effects: replaces the job's current limits.</remarks>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(
            IntPtr job, int informationClass, IntPtr information, uint length);

        /// <summary>Adds a process to a job.</summary>
        /// <param name="job">Job handle.</param>
        /// <param name="process">Process handle with quota and terminate access.</param>
        /// <returns>True on success, false with a last error otherwise.</returns>
        /// <remarks>
        /// Side effects: the process becomes a member until it exits or the job is
        /// terminated, whichever happens first.
        /// </remarks>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        /// <summary>Closes one kernel handle.</summary>
        /// <param name="handle">Handle to close.</param>
        /// <returns>True on success; the result is deliberately ignored here.</returns>
        /// <remarks>
        /// Side effects: for the job handle this terminates surviving members. That
        /// is the design, not an accident.
        /// </remarks>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
