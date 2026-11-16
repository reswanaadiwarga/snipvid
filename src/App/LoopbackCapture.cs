// LoopbackCapture - what the default render endpoint is playing, fed to ffmpeg
// through a named pipe (DESIGN.md section 5d).
//
// WHY WASAPI loopback and not Stereo Mix: Stereo Mix is a capture device owned by
// the on-board audio driver, and this machine's default output is a USB DAC, so a
// Stereo Mix recording is silence. Loopback is the only API that asks Windows for
// the *default render endpoint* and receives its mix; it therefore follows the
// device the user actually hears, including the headset/speaker switch on the same
// DAC. Measured 2026-11-16: the default render endpoint was
// "Headphones (AB13X USB Audio)", and this writer captured its mix (288,000
// samples in 3.00 s, mean_volume -21.4 dB, max_volume -10.1 dB while a tone
// played).
//
// WHY a named pipe rather than writing a WAV: ffmpeg stays the only thing that
// writes the recording. This process owns PCM and the pipe; ffmpeg owns the muxer,
// the encoder, the crash-safe fragmentation and the -c copy join. That keeps the
// audio path additive to the validated video command line instead of a second
// recording implementation.
//
// The pipe is created by Create(), before ffmpeg is spawned: the child's open of
// \\.\pipe\... blocks until a server exists, so there is no race and no "connect
// failed" retry. ffmpeg's input is -f s16le -ar 48000 -ac 2, which is the format
// this writer emits regardless of what the endpoint mixes internally: the shared
// audio engine converts (AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM), and if the engine
// refuses the conversion the writer fails over to silence for that segment rather
// than writing audio at the wrong rate. Wrong-rate audio would silently desynch
// against the video, which is worse than a segment with no sound.
//
// THE KEEP-OPEN RULE: an EOF on this input ends ffmpeg's recording. So the pipe is
// never closed early. When capture stops (pause, stop, a device error) the writer
// switches to silence and keeps writing at real time until ffmpeg closes its end -
// which it does when it exits after "q" or is killed. Only then does the thread
// end and the handle get released. That is the whole reason Stop() and Dispose()
// are two operations rather than one.
//
// Timing: Start() is called when the segment is spawned, not when the first
// video frame arrives. WHY, measured 2026-11-16: ffmpeg cannot report a video
// frame while its audio input is empty - its muxer holds the first video packet
// until an audio packet exists, so a pipe with no writer produced no "frame="
// line at all, and the same pipeline reported within 0.4 s once PCM flowed.
// Starting the writer on the first-frame report would therefore deadlock until
// the recorder's 2 s fallback.
//
// The gate is the sync mechanism that makes feeding early safe: while OpenGate
// has not been called, the writer consumes the loopback packets (so the client
// cannot overflow) and emits silence of the same duration, paced by the loopback
// clock. The recorder opens the gate on the event that ends Starting (the first
// frame= greater than 0), so the real pre-roll samples captured during ddagrab's
// warm-up are discarded instead of being shifted into the file. The pipe keeps
// flowing from spawn to segment end: Stop() switches to silence, Dispose()
// closes it. Gate state is runtime, never argv or a filter.
//
// Threading: one background thread owns the pipe, the audio client and every COM
// pointer created for the capture; the UI thread only creates the pipe, signals
// Stop/Dispose and reads counters after the thread has ended. Every failure is
// logged (through the injected sink) and swallowed: a broken audio path must cost
// the segment's sound, never the recording.
//
// COM interop only - no WPF, no Unmanaged exports, no package. The interfaces are
// declared here rather than shared with AudioDevices so the capture endpoint and
// the render endpoint cannot be confused by a reader; the duplication is five
// small declarations.
//
// ASCII-only source: csc falls back to the ANSI codepage for a file that is not
// valid UTF-8, and device names are runtime data, never literals.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;

namespace SnipVid
{
    /// <summary>
    /// Records the default render endpoint's mix (WASAPI loopback) into a named
    /// pipe as 48 kHz, 16-bit, stereo PCM, for ffmpeg's raw input.
    /// </summary>
    /// <remarks>
    /// Total: every method returns a value or a log line; nothing here throws at the
    /// caller, because this runs inside a recording start where an exception would
    /// abort the capture. Created before the child is spawned, started when the
    /// first video frame arrives, stopped without closing the pipe, and disposed
    /// only after the child has ended.
    /// </remarks>
    internal sealed class LoopbackCapture : IDisposable
    {
        /// <summary>
        /// Sample rate ffmpeg is told to expect. Fixed, not the endpoint's mix rate,
        /// because the shared audio engine converts and every segment must have
        /// identical PCM parameters for the -c copy join.
        /// </summary>
        private const int OutputSampleRate = 48000;

        /// <summary>Channel count ffmpeg is told to expect.</summary>
        private const int OutputChannels = 2;

        /// <summary>Bytes per sample in the emitted stream: 16-bit signed little endian.</summary>
        private const int OutputBytesPerSample = 2;

        /// <summary>Bytes per emitted frame (stereo, 16-bit).</summary>
        private const int OutputBytesPerFrame = OutputChannels * OutputBytesPerSample;

        /// <summary>Output frames in one millisecond; the silence pacing uses it.</summary>
        private const double OutputFramesPerMs = OutputSampleRate / 1000.0;

        /// <summary>Frames of silence written per drain tick once the segment is ending.</summary>
        private const int SilenceFramesPerTick = OutputSampleRate / 100;

        /// <summary>Longest single silence write, in frames: a suspended thread must not dump minutes of it at once.</summary>
        private const int MaxSilenceCatchUpFrames = OutputSampleRate * 2;

        /// <summary>How often the writer polls for a connection and for audio packets.</summary>
        private const int LivePollMs = 5;

        /// <summary>How often the writer feeds silence once capture has stopped.</summary>
        private const int DrainPollMs = 10;

        /// <summary>How long the writer waits for a connection before re-checking Stop.</summary>
        private const int ConnectPollMs = 100;

        /// <summary>How long Dispose waits for the writer thread before closing the pipe under it.</summary>
        private const int WriterJoinMs = 2000;

        /// <summary>The Win32 pipe namespace prefix the server constructor does not take.</summary>
        private const string PipeNamespacePrefix = @"\\.\pipe\";

        /// <summary>Source format as the audio engine was asked to deliver it.</summary>
        private const int ShareModeShared = 0;

        /// <summary>AUDCLNT_STREAMFLAGS_LOOPBACK: capture what the render endpoint plays.</summary>
        private const int StreamFlagsLoopback = 0x00020000;

        /// <summary>
        /// AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM: insert the engine's matrixer and
        /// resampler so a format other than the mix format is accepted.
        /// </summary>
        private const int StreamFlagsAutoConvert = unchecked((int)0x80000000);

        /// <summary>AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY: the resampler's normal quality.</summary>
        private const int StreamFlagsSrcDefaultQuality = 0x08000000;

        /// <summary>One second of shared-mode buffer; the audio engine's default period still drives reads.</summary>
        private const long BufferDurationHns = 10000000;

        /// <summary>AUDCLNT_BUFFERFLAGS_SILENT: the packet is silence and its data pointer is undefined.</summary>
        private const int BufferFlagSilent = 0x2;

        /// <summary>EDataFlow.eRender: playback endpoints only.</summary>
        private const int RenderFlow = 0;

        /// <summary>ERole.eConsole: the endpoint the user hears as "the" output device.</summary>
        private const int ConsoleRole = 0;

        /// <summary>STGM_READ: the property store is only read.</summary>
        private const int ReadOnlyStore = 0;

        /// <summary>VARTYPE for VT_LPWSTR, the type PKEY_Device_FriendlyName is stored as.</summary>
        private const ushort VariantTypeString = 31;

        /// <summary>PKEY_Device_FriendlyName's property id (fmtid a45c254e-df1c-4efd-8020-67d146a850e0).</summary>
        private const int FriendlyNameId = 14;

        /// <summary>PKEY_Device_DeviceDesc's property id, the fallback when FriendlyName is absent.</summary>
        private const int DeviceDescriptionId = 2;

        /// <summary>The device property set that holds FriendlyName and DeviceDesc.</summary>
        private static readonly Guid DevicePropertySet =
            new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");

        /// <summary>IID_IAudioClient (1CB9AD4C-DBFA-4c32-B178-C2F568A703B2).</summary>
        private static readonly Guid AudioClientId =
            new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

        /// <summary>IID_IAudioCaptureClient (C8ADBD64-E71E-48a0-A4DE-185C395CD317).</summary>
        private static readonly Guid AudioCaptureClientId =
            new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

        /// <summary>Full argv path of the pipe, for the log line.</summary>
        private readonly string _pipePath;

        /// <summary>The created server; ffmpeg connects to it as a client.</summary>
        private readonly NamedPipeServerStream _pipe;

        /// <summary>Log sink; never null (a null caller gets a no-op).</summary>
        private readonly Action<string> _log;

        /// <summary>Writer thread; null until <see cref="Start"/>.</summary>
        private Thread _writer;

        /// <summary>Guards <see cref="_started"/>; set once by Start, read once.</summary>
        private readonly object _gate = new object();

        /// <summary>True once Start has been called; a second call is ignored.</summary>
        private bool _started;

        /// <summary>True once Dispose has run; every later call is a no-op.</summary>
        private bool _disposed;

        /// <summary>
        /// Set by <see cref="Stop"/>: switch the writer to silence and keep the pipe
        /// open. Read by the writer thread on every tick.
        /// </summary>
        private volatile bool _stopRequested;

        /// <summary>
        /// Set by <see cref="OpenGate"/>: from then on the writer emits real captured
        /// samples instead of paced silence. Read on every tick.
        /// </summary>
        /// <remarks>
        /// Runtime state, never argv: the gate changes what the writer sends, not the
        /// ffmpeg command line. Closed from Start until the recorder observes the
        /// event that ends Starting.
        /// </remarks>
        private volatile bool _gateOpen;

        /// <summary>Real loopback frames discarded while the gate was closed.</summary>
        private long _discardedFrames;

        /// <summary>Frames of real PCM written to the pipe; written only by the writer thread.</summary>
        private long _capturedFrames;

        /// <summary>Frames of silence written, including the drain after Stop.</summary>
        private long _silenceFrames;

        /// <summary>Endpoint name resolved when the client opened; null when it never did.</summary>
        private volatile string _deviceName;

        /// <summary>Description of the format the engine accepted, for the log.</summary>
        private volatile string _formatDescription;

        /// <summary>Reusable silence buffer; zero bytes by contract.</summary>
        private byte[] _silence;

        /// <summary>Reusable packet decode buffer.</summary>
        private byte[] _packet;

        /// <summary>Reusable converted-audio buffer.</summary>
        private byte[] _converted;

        /// <summary>Audio clock origin: zero at the moment the client was started.</summary>
        private Stopwatch _clock;

        /// <summary>
        /// Creates the named pipe server for one segment. Call before spawning ffmpeg.
        /// </summary>
        /// <param name="pipePath">
        /// Full path from <see cref="SnipVid.Logic.FfmpegArgs.SystemAudioPipeName"/>,
        /// for example <c>\\.\pipe\snipvid-audio-&lt;session&gt;-&lt;segment&gt;</c>.
        /// </param>
        /// <param name="log">Sink for one line per transition or failure; may be null.</param>
        /// <param name="error">
        /// Null on success; otherwise one human-readable reason. Always assigned, so
        /// the caller can report failure without guessing.
        /// </param>
        /// <returns>
        /// A capture whose pipe exists and is ready for ffmpeg to open, or null when
        /// the pipe could not be created.
        /// </returns>
        /// <remarks>
        /// Side effects: creates a kernel pipe server object. The pipe name must be
        /// unique per segment; a second server on a live name fails with
        /// ERROR_PIPE_BUSY, which is reported rather than retried, because reusing a
        /// name would attach the new ffmpeg to the previous segment's writer.
        /// Never throws.
        /// </remarks>
        internal static LoopbackCapture Create(string pipePath, Action<string> log, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(pipePath))
            {
                error = "no pipe path was supplied";
                return null;
            }
            if (!pipePath.StartsWith(PipeNamespacePrefix, StringComparison.OrdinalIgnoreCase))
            {
                error = "not a named pipe path: " + pipePath;
                return null;
            }

            string name = pipePath.Substring(PipeNamespacePrefix.Length);
            try
            {
                NamedPipeServerStream pipe = new NamedPipeServerStream(
                    name,
                    PipeDirection.Out,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.None);
                return new LoopbackCapture(pipePath, pipe, log);
            }
            catch (Exception ex)
            {
                // Deliberately broad: a pipe failure must be a log line and a plain
                // "no system audio for this segment", never an exception escaping
                // into the capture path.
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        /// <summary>Stores the created pipe; use <see cref="Create"/>.</summary>
        /// <param name="pipePath">Full argv path, for logging.</param>
        /// <param name="pipe">The created server; ownership passes here.</param>
        /// <param name="log">Log sink; may be null.</param>
        /// <remarks>Side effects: none beyond field assignment.</remarks>
        private LoopbackCapture(string pipePath, NamedPipeServerStream pipe, Action<string> log)
        {
            _pipePath = pipePath;
            _pipe = pipe;
            _log = log ?? delegate(string unused) { };
        }

        /// <summary>The endpoint name resolved when the capture opened; null when it did not.</summary>
        internal string DeviceName
        {
            get { return _deviceName; }
        }

        /// <summary>Frames of real PCM written to the pipe.</summary>
        internal long CapturedFrames
        {
            get { return Interlocked.Read(ref _capturedFrames); }
        }

        /// <summary>Real loopback frames discarded while the gate was closed.</summary>
        internal long DiscardedFrames
        {
            get { return Interlocked.Read(ref _discardedFrames); }
        }

        /// <summary>Frames of silence written, including the drain after Stop.</summary>
        internal long SilenceFrames
        {
            get { return Interlocked.Read(ref _silenceFrames); }
        }

        /// <summary>
        /// Starts the writer thread: wait for ffmpeg to open the pipe, open the
        /// loopback client, then stream PCM.
        /// </summary>
        /// <remarks>
        /// Side effects: starts one background thread. Returns immediately; the
        /// endpoint is resolved and the client started on that thread, so a slow
        /// audio service never delays the spawn. A second call or a call after
        /// Dispose is ignored.
        ///
        /// Called with the segment, before any frame is reported, because ffmpeg
        /// will not report a frame while this input is empty (see the file header).
        /// The recorder logs the measured lead between audio t=0 and the first video
        /// frame.
        /// </remarks>
        internal void Start()
        {
            lock (_gate)
            {
                if (_started || _disposed)
                {
                    return;
                }
                _started = true;
            }

            Thread thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "SnipVid loopback";
            _writer = thread;
            thread.Start();
        }

        /// <summary>
        /// Opens the gate: real captured samples replace paced silence from the next
        /// buffered packet.
        /// </summary>
        /// <remarks>
        /// Side effects: sets a flag the writer reads on its next tick. Everything
        /// the client buffered before the flag was seen is discarded once, so the
        /// pre-roll captured during ddagrab's warm-up never reaches the file. Called
        /// by the recorder on the event that ends Starting (first frame= greater than
        /// 0, or the 2 s fallback). Safe to call more than once and before
        /// <see cref="Start"/>; never throws. The gate is runtime state - it adds
        /// nothing to the ffmpeg argv.
        /// </remarks>
        internal void OpenGate()
        {
            _gateOpen = true;
        }

        /// <summary>
        /// Stops capturing and switches the writer to silence, keeping the pipe open.
        /// </summary>
        /// <remarks>
        /// Side effects: sets a flag the writer reads on its next tick; no join, no
        /// close. That is deliberate: ffmpeg's audio input must not reach EOF while
        /// the child is still writing the segment, so the only safe moment to close
        /// the pipe is after the child has exited (see <see cref="Dispose"/>). Safe
        /// to call more than once and before <see cref="Start"/>.
        /// </remarks>
        internal void Stop()
        {
            _stopRequested = true;
        }

        /// <summary>
        /// Stops the writer and releases the pipe. Call only after ffmpeg has exited.
        /// </summary>
        /// <remarks>
        /// Side effects: signals Stop, waits up to <see cref="WriterJoinMs"/> for the
        /// background thread, then disposes the pipe. A writer still blocked at that
        /// point sees its next write fail and exits; that is logged, not thrown. Safe
        /// to call more than once. Never throws: this runs from a finalise path where
        /// an exception would lose the recording.
        /// </remarks>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _stopRequested = true;

            Thread thread = _writer;
            if (thread != null)
            {
                try
                {
                    if (!thread.Join(WriterJoinMs))
                    {
                        _log("audio: loopback writer did not end within "
                            + WriterJoinMs.ToString(CultureInfo.InvariantCulture)
                            + " ms; closing the pipe under it");
                    }
                }
                catch (ThreadStateException)
                {
                    // Already gone; the pipe below is still released.
                }
            }

            try
            {
                _pipe.Dispose();
            }
            catch (Exception ex)
            {
                _log("audio: loopback pipe dispose failed " + ex.GetType().Name + ": " + ex.Message);
            }

            _log(
                "audio: loopback closed pipe=" + _pipePath
                + " capturedFrames=" + CapturedFrames.ToString(CultureInfo.InvariantCulture)
                + " silenceFrames=" + SilenceFrames.ToString(CultureInfo.InvariantCulture)
                + " device=" + (_deviceName ?? "(none)"));
        }

        /// <summary>
        /// The default render endpoint's friendly name, resolved now, for tooltips.
        /// </summary>
        /// <param name="detail">Null on success; otherwise one reason, as everywhere else here.</param>
        /// <returns>
        /// The name, for example "Speakers (USB Audio Device)", or null when Windows
        /// has no default render endpoint or the query failed.
        /// </returns>
        /// <remarks>
        /// Side effects: creates and releases COM objects. Never throws. Display
        /// only: the recording resolves the endpoint again when the capture opens,
        /// so a device switched between opening the overlay and starting the
        /// recording is honoured.
        /// </remarks>
        internal static string DefaultRenderName(out string detail)
        {
            IMMDevice device = null;
            try
            {
                if (!TryDefaultRenderDevice(out device, out detail))
                {
                    return null;
                }

                string name = ReadName(device);
                if (string.IsNullOrEmpty(name))
                {
                    detail = "the default render device has no name";
                    return null;
                }

                detail = null;
                return name;
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
            finally
            {
                Release(device);
            }
        }

        /// <summary>
        /// The writer thread: connect, open the endpoint, stream, then drain silence.
        /// </summary>
        /// <remarks>
        /// Side effects: reads and writes the pipe, opens and releases COM objects,
        /// starts and stops one audio client. Exits when the pipe's client goes away,
        /// which happens when ffmpeg exits - by "q", by a kill, or by a crash. Every
        /// exception is logged and ends the thread; none escapes.
        /// </remarks>
        private void Run()
        {
            IAudioClient client = null;
            IAudioCaptureClient capture = null;
            AudioFormat format = null;
            try
            {
                _log("audio: loopback waiting for the recorder to open " + _pipePath);
                System.Threading.Tasks.Task connecting = _pipe.WaitForConnectionAsync();
                try
                {
                    while (!_stopRequested && !connecting.IsCompleted)
                    {
                        connecting.Wait(ConnectPollMs);
                    }
                }
                catch (AggregateException ex)
                {
                    _log("audio: loopback connect failed: " + ex.GetBaseException().Message);
                    return;
                }
                if (!_pipe.IsConnected)
                {
                    _log("audio: loopback stopped before ffmpeg connected; no PCM was written");
                    return;
                }
                _log("audio: loopback connected pipe=" + _pipePath);

                string detail;
                if (!TryOpenDefaultRender(out client, out capture, out format, out detail))
                {
                    // A failed capture is not a failed recording: the pipe stays
                    // open and carries silence until the segment ends, so ffmpeg's
                    // timeline is the same as a successful one.
                    _log("audio: loopback capture unavailable (" + detail
                        + "); writing silence for the rest of this segment");
                    client = null;
                    capture = null;
                }

                if (client != null)
                {
                    int hr = client.Start();
                    if (hr < 0)
                    {
                        _log("audio: loopback start failed HRESULT 0x"
                            + hr.ToString("X8", CultureInfo.InvariantCulture)
                            + "; writing silence for the rest of this segment");
                        capture = null;
                    }
                    else
                    {
                        _clock = Stopwatch.StartNew();
                        _log("audio: loopback capturing device=" + _deviceName
                            + " format=" + _formatDescription
                            + " -> s16le 48000 2ch");
                    }
                }

                bool streaming = false;
                while (true)
                {
                    if (_stopRequested || capture == null)
                    {
                        // Drain: real time silence, never a close. This is the
                        // keep-open rule; ffmpeg ends the segment, not the writer.
                        WriteSilence(SilenceFramesPerTick);
                        Thread.Sleep(DrainPollMs);
                        continue;
                    }

                    if (!_gateOpen)
                    {
                        // Gated. The loopback client is already producing samples,
                        // and they are thrown away: ffmpeg needs PCM from the first
                        // moment (or it reports no frame at all), but the audio that
                        // belongs before the first video frame must not reach the
                        // file. Silence of the same duration keeps the pipe's byte
                        // rate even, so no consumer sees a stall.
                        DiscardPackets(capture);
                        PadWithSilence();
                        Thread.Sleep(LivePollMs);
                        continue;
                    }

                    if (!streaming)
                    {
                        // The gate opened between two ticks. Everything buffered at
                        // this instant was captured before the opening, so it is
                        // discarded once; the next packet is the first real sample.
                        DiscardPackets(capture);
                        streaming = true;
                        _log("audio: loopback gate opened pipe=" + _pipePath
                            + " gatedSilenceFrames=" + Interlocked.Read(ref _silenceFrames)
                            + " discardedPreRollFrames=" + Interlocked.Read(ref _discardedFrames)
                            + " at t+" + _clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
                        Thread.Sleep(LivePollMs);
                        continue;
                    }

                    DrainPackets(capture, format);
                    PadWithSilence();
                    Thread.Sleep(LivePollMs);
                }
            }
            catch (IOException ex)
            {
                _log("audio: loopback pipe closed (" + ex.Message + "); writer stopped");
            }
            catch (ObjectDisposedException)
            {
                _log("audio: loopback pipe was disposed; writer stopped");
            }
            catch (Exception ex)
            {
                // Deliberately broad: the thread must die quietly, because by the
                // time it runs the capture has already been handed to ffmpeg.
                _log("audio: loopback writer failed " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (client != null)
                {
                    try
                    {
                        client.Stop();
                    }
                    catch (Exception)
                    {
                        // A driver that refuses Stop is not worth a second log line.
                    }
                }
                Release(capture);
                Release(client);
            }
        }

        /// <summary>
        /// Writes silence until the emitted stream covers the loopback clock.
        /// </summary>
        /// <remarks>
        /// Side effects: writes the pipe, increments the silence counter. Paced by the
        /// audio client's clock, so the pipe's byte rate follows real time in both
        /// states: while the gate is closed this is the whole stream, and after it
        /// opens it fills only the gaps the engine did not deliver (nothing playing,
        /// or a packet a few milliseconds late). The catch-up cap is a guard against
        /// a suspended thread dumping seconds of silence at once.
        /// </remarks>
        private void PadWithSilence()
        {
            long expected = (long)(_clock.ElapsedMilliseconds * OutputFramesPerMs);
            long written = Interlocked.Read(ref _capturedFrames)
                + Interlocked.Read(ref _silenceFrames);
            long missing = expected - written;
            if (missing > MaxSilenceCatchUpFrames)
            {
                missing = MaxSilenceCatchUpFrames;
            }
            if (missing > 0)
            {
                WriteSilence(missing);
            }
        }

        /// <summary>
        /// Consumes every queued loopback packet without writing it.
        /// </summary>
        /// <param name="capture">The capture client.</param>
        /// <remarks>
        /// Side effects: reads and releases the client's buffers and counts the frames
        /// as discarded. WHY this exists: while the gate is closed the endpoint is
        /// already playing, and a client that is never read would eventually overflow;
        /// the pre-roll belongs to wall time before the first video frame and must not
        /// enter the file. A failure is logged and stops this tick; the outer loop
        /// then finishes the segment with silence rather than ending it.
        /// </remarks>
        private void DiscardPackets(IAudioCaptureClient capture)
        {
            while (true)
            {
                uint packetFrames;
                int hr = capture.GetNextPacketSize(out packetFrames);
                if (hr < 0)
                {
                    _log("audio: loopback GetNextPacketSize failed HRESULT 0x"
                        + hr.ToString("X8", CultureInfo.InvariantCulture)
                        + "; finishing this segment with silence");
                    _stopRequested = true;
                    return;
                }
                if (packetFrames == 0)
                {
                    return;
                }

                IntPtr data;
                uint frames;
                uint flags;
                long devicePosition;
                long qpcPosition;
                hr = capture.GetBuffer(out data, out frames, out flags, out devicePosition, out qpcPosition);
                if (hr < 0)
                {
                    _log("audio: loopback GetBuffer failed HRESULT 0x"
                        + hr.ToString("X8", CultureInfo.InvariantCulture)
                        + "; finishing this segment with silence");
                    _stopRequested = true;
                    return;
                }

                try
                {
                    Interlocked.Add(ref _discardedFrames, frames);
                }
                finally
                {
                    capture.ReleaseBuffer(frames);
                }
            }
        }

        /// <summary>
        /// Copies every queued loopback packet to the pipe.
        /// </summary>
        /// <param name="capture">The capture client.</param>
        /// <param name="format">The format the client was initialised with.</param>
        /// <remarks>
        /// Side effects: reads the client's buffers, writes the pipe and releases
        /// each buffer. A failure stops this tick with one log line; the outer loop
        /// then treats the capture as finished and switches to silence, so one
        /// driver hiccup cannot end the segment. Packets flagged SILENT are zeroed
        /// data on some drivers and an undefined pointer on others, so they are
        /// written as silence by length rather than read.
        /// </remarks>
        private void DrainPackets(IAudioCaptureClient capture, AudioFormat format)
        {
            while (true)
            {
                uint packetFrames;
                int hr = capture.GetNextPacketSize(out packetFrames);
                if (hr < 0)
                {
                    _log("audio: loopback GetNextPacketSize failed HRESULT 0x"
                        + hr.ToString("X8", CultureInfo.InvariantCulture)
                        + "; finishing this segment with silence");
                    _stopRequested = true;
                    return;
                }
                if (packetFrames == 0)
                {
                    return;
                }

                IntPtr data;
                uint frames;
                uint flags;
                long devicePosition;
                long qpcPosition;
                hr = capture.GetBuffer(out data, out frames, out flags, out devicePosition, out qpcPosition);
                if (hr < 0)
                {
                    _log("audio: loopback GetBuffer failed HRESULT 0x"
                        + hr.ToString("X8", CultureInfo.InvariantCulture)
                        + "; finishing this segment with silence");
                    _stopRequested = true;
                    return;
                }

                try
                {
                    if (frames == 0)
                    {
                        continue;
                    }
                    if ((flags & BufferFlagSilent) != 0 || data == IntPtr.Zero)
                    {
                        WriteSilence(frames);
                    }
                    else
                    {
                        WriteConverted(data, frames, format);
                    }
                }
                finally
                {
                    capture.ReleaseBuffer(frames);
                }
            }
        }

        /// <summary>
        /// Converts one packet to s16le stereo 48 kHz and writes it.
        /// </summary>
        /// <param name="data">Packet data; non-null.</param>
        /// <param name="frames">Frames in the packet.</param>
        /// <param name="format">Source format.</param>
        /// <remarks>
        /// Side effects: reads the unmanaged packet, writes the pipe. The fast path
        /// is exact: the engine accepted s16 stereo 48 kHz, so the block is copied
        /// byte for byte. The conversion path exists because a driver may still
        /// refuse auto-conversion; it never changes the rate (the client is only
        /// opened at 48 kHz), so no sample-rate drift can be introduced here.
        /// </remarks>
        private void WriteConverted(IntPtr data, uint frames, AudioFormat format)
        {
            int sourceBytes = checked((int)frames * format.BlockAlign);
            if (format.IsPassthrough)
            {
                if (_packet == null || _packet.Length < sourceBytes)
                {
                    _packet = new byte[sourceBytes];
                }
                Marshal.Copy(data, _packet, 0, sourceBytes);
                _pipe.Write(_packet, 0, sourceBytes);
                Interlocked.Add(ref _capturedFrames, frames);
                return;
            }

            int outputBytes = checked((int)frames * OutputBytesPerFrame);
            if (_packet == null || _packet.Length < sourceBytes)
            {
                _packet = new byte[sourceBytes];
            }
            if (_converted == null || _converted.Length < outputBytes)
            {
                _converted = new byte[outputBytes];
            }
            Marshal.Copy(data, _packet, 0, sourceBytes);

            int destination = 0;
            for (uint frame = 0; frame < frames; frame++)
            {
                int offset = (int)frame * format.BlockAlign;
                short left = format.ReadSample(_packet, offset, 0);
                short right = format.Channels > 1 ? format.ReadSample(_packet, offset, 1) : left;

                _converted[destination++] = (byte)(left & 0xFF);
                _converted[destination++] = (byte)((left >> 8) & 0xFF);
                _converted[destination++] = (byte)(right & 0xFF);
                _converted[destination++] = (byte)((right >> 8) & 0xFF);
            }

            _pipe.Write(_converted, 0, outputBytes);
            Interlocked.Add(ref _capturedFrames, frames);
        }

        /// <summary>
        /// Writes this many frames of silence to the pipe.
        /// </summary>
        /// <param name="frames">Frames to write; zero or negative is a no-op.</param>
        /// <remarks>
        /// Side effects: writes the pipe and increments the silence counter. The
        /// buffer is allocated once and stays zeroed; it is never written to, so
        /// there is no path by which noise could be mistaken for silence.
        /// </remarks>
        private void WriteSilence(long frames)
        {
            if (frames <= 0)
            {
                return;
            }

            int bytes = checked((int)(frames * OutputBytesPerFrame));
            if (_silence == null || _silence.Length < bytes)
            {
                _silence = new byte[bytes];
            }
            _pipe.Write(_silence, 0, bytes);
            Interlocked.Add(ref _silenceFrames, frames);
        }

        /// <summary>
        /// Resolves the default render endpoint and activates a loopback client on it.
        /// </summary>
        /// <param name="client">Receives the started-able audio client.</param>
        /// <param name="capture">Receives the capture client from that audio client.</param>
        /// <param name="format">Receives the format the engine accepted.</param>
        /// <param name="detail">Null on success; the reason otherwise.</param>
        /// <returns>True when both COM clients are ready to be started.</returns>
        /// <remarks>
        /// Side effects: creates and releases COM objects and reads the device name.
        /// Never throws. The requested format is 48 kHz s16 stereo with the engine's
        /// auto-convert flags; the mix format is tried next, and only at 48 kHz -
        /// because ffmpeg is told 48 kHz, and accepting any other rate would produce
        /// audio whose duration does not match the video. A refused format is a
        /// silent segment, not a desynchronised one.
        /// </remarks>
        private bool TryOpenDefaultRender(
            out IAudioClient client, out IAudioCaptureClient capture, out AudioFormat format, out string detail)
        {
            client = null;
            capture = null;
            format = null;

            IMMDevice device = null;
            try
            {
                if (!TryDefaultRenderDevice(out device, out detail))
                {
                    return false;
                }

                string name = ReadName(device);
                _deviceName = name;

                IntPtr instance;
                Guid audioClientId = AudioClientId;
                int hr = device.Activate(ref audioClientId, 1, IntPtr.Zero, out instance);
                if (hr < 0 || instance == IntPtr.Zero)
                {
                    detail = "IAudioClient activation failed (HRESULT 0x"
                        + hr.ToString("X8", CultureInfo.InvariantCulture) + ")";
                    return false;
                }

                IAudioClient audioClient;
                try
                {
                    audioClient = (IAudioClient)Marshal.GetObjectForIUnknown(instance);
                }
                finally
                {
                    Marshal.Release(instance);
                }

                int flags = StreamFlagsLoopback | StreamFlagsAutoConvert | StreamFlagsSrcDefaultQuality;
                AudioFormat accepted;
                hr = InitializeShared(audioClient, flags, BufferDurationHns, 48000, 2, 16, false, out accepted);
                if (hr < 0)
                {
                    // Some drivers reject a non-zero duration for loopback; the
                    // periodicity stays 0 (shared mode) either way.
                    hr = InitializeShared(audioClient, flags, 0L, 48000, 2, 16, false, out accepted);
                }
                if (hr < 0)
                {
                    // Last attempt: the endpoint's own mix format, but only if it is
                    // already 48 kHz. Anything else would make the PCM duration
                    // disagree with the video.
                    IntPtr mixPointer;
                    int mixResult = audioClient.GetMixFormat(out mixPointer);
                    if (mixResult < 0 || mixPointer == IntPtr.Zero)
                    {
                        detail = "IAudioClient.GetMixFormat failed (HRESULT 0x"
                            + mixResult.ToString("X8", CultureInfo.InvariantCulture)
                            + "); no system audio for this segment";
                        Release(audioClient);
                        return false;
                    }

                    try
                    {
                        AudioFormat mix = AudioFormat.FromPointer(mixPointer, OutputSampleRate);
                        if (mix == null)
                        {
                            detail = "the endpoint mix format is not 48000 Hz"
                                + " and 48000 Hz was refused; no system audio for this segment";
                            Release(audioClient);
                            return false;
                        }

                        hr = InitializeShared(
                            audioClient, flags, 0L, mix.SampleRate, mix.Channels,
                            mix.BitsPerSample, mix.IsFloat, out accepted);
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(mixPointer);
                    }
                }

                if (hr < 0)
                {
                    detail = "IAudioClient.Initialize failed (HRESULT 0x"
                        + hr.ToString("X8", CultureInfo.InvariantCulture)
                        + "); 48000 Hz s16 stereo was refused";
                    Release(audioClient);
                    return false;
                }

                IntPtr captureInstance;
                Guid captureId = AudioCaptureClientId;
                hr = audioClient.GetService(ref captureId, out captureInstance);
                if (hr < 0 || captureInstance == IntPtr.Zero)
                {
                    detail = "IAudioCaptureClient was not available (HRESULT 0x"
                        + hr.ToString("X8", CultureInfo.InvariantCulture) + ")";
                    Release(audioClient);
                    return false;
                }

                IAudioCaptureClient captureClient;
                try
                {
                    captureClient = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(captureInstance);
                }
                finally
                {
                    Marshal.Release(captureInstance);
                }

                _formatDescription = accepted.Describe();
                client = audioClient;
                capture = captureClient;
                format = accepted;
                detail = null;
                return true;
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                Release(device);
            }
        }

        /// <summary>
        /// Initialises a shared-mode client and records the format it accepted.
        /// </summary>
        /// <param name="client">Client to initialise.</param>
        /// <param name="streamFlags">Loopback plus the auto-convert flags.</param>
        /// <param name="bufferDuration">Buffer duration in 100 ns units; 0 means the engine's default.</param>
        /// <param name="sampleRate">Requested sample rate.</param>
        /// <param name="channels">Requested channel count.</param>
        /// <param name="bitsPerSample">Requested container bits.</param>
        /// <param name="isFloat">True for IEEE float samples.</param>
        /// <param name="accepted">Receives the accepted format on success.</param>
        /// <returns>The HRESULT; non-negative means the client is initialised.</returns>
        /// <remarks>
        /// Side effects: allocates and frees one unmanaged WAVEFORMATEXTENSIBLE.
        /// The extensible form is used even for plain PCM because it is the only
        /// layout that can describe a channel mask, and the audio engine accepts it
        /// for both float and integer requests. No throw.
        /// </remarks>
        private static int InitializeShared(
            IAudioClient client, int streamFlags, long bufferDuration, int sampleRate, int channels,
            int bitsPerSample, bool isFloat, out AudioFormat accepted)
        {
            accepted = null;
            int blockAlign = channels * (bitsPerSample / 8);
            WaveFormatExtensible format = new WaveFormatExtensible();
            format.Format.Tag = 0xFFFE; // WAVE_FORMAT_EXTENSIBLE
            format.Format.Channels = (ushort)channels;
            format.Format.SamplesPerSec = (uint)sampleRate;
            format.Format.AverageBytesPerSec = (uint)(sampleRate * blockAlign);
            format.Format.BlockAlign = (ushort)blockAlign;
            format.Format.BitsPerSample = (ushort)bitsPerSample;
            format.Format.ExtraSize = 22; // WAVEFORMATEXTENSIBLE's cbSize
            format.Samples = (ushort)bitsPerSample;
            format.ChannelMask = channels == 1 ? 0x4u : 0x3u; // SPEAKER_FRONT_CENTER / LEFT|RIGHT
            format.SubFormat = isFloat ? MediaSubtypeFloat : MediaSubtypePcm;

            IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WaveFormatExtensible)));
            try
            {
                Marshal.StructureToPtr(format, pointer, false);
                int hr = client.Initialize(ShareModeShared, streamFlags, bufferDuration, 0L, pointer, IntPtr.Zero);
                if (hr >= 0)
                {
                    accepted = new AudioFormat(channels, sampleRate, bitsPerSample, isFloat);
                }
                return hr;
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }

        /// <summary>
        /// Finds the default render endpoint (eRender, eConsole).
        /// </summary>
        /// <param name="device">Receives the endpoint; null on failure.</param>
        /// <param name="detail">Null on success; the reason otherwise.</param>
        /// <returns>True when the endpoint exists.</returns>
        /// <remarks>
        /// Side effects: creates the MMDeviceEnumerator COM object; the caller
        /// releases <paramref name="device"/>. Never throws. eConsole is deliberate:
        /// it is the endpoint the user calls "my speakers/headphones", which is what
        /// system audio means. It is also the role Windows keeps pointing at the
        /// device the user just selected.
        /// </remarks>
        private static bool TryDefaultRenderDevice(out IMMDevice device, out string detail)
        {
            device = null;
            IMMDeviceEnumerator enumerator = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MmDeviceEnumeratorComObject();
                int hr = enumerator.GetDefaultAudioEndpoint(RenderFlow, ConsoleRole, out device);
                if (hr < 0 || device == null)
                {
                    // E_NOTFOUND is the normal answer on a machine with no output
                    // device at all; every other failure means the audio service or a
                    // driver is unhappy. Both mean "no system audio".
                    detail = "no default render device (HRESULT 0x"
                        + hr.ToString("X8", CultureInfo.InvariantCulture) + ")";
                    return false;
                }
                detail = null;
                return true;
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                Release(enumerator);
            }
        }

        /// <summary>
        /// Reads an endpoint's display name from its property store.
        /// </summary>
        /// <param name="device">Endpoint to read; must not be null.</param>
        /// <returns>FriendlyName, else DeviceDesc, else null.</returns>
        /// <remarks>
        /// Side effects: opens a read-only property store and frees the returned
        /// PROPVARIANT string. Never throws for a failing store: the tooltip and the
        /// log tolerate an unnamed device, the stream does not depend on the name.
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
        /// <returns>The string, or null when absent or not a string.</returns>
        /// <remarks>
        /// Side effects: reads the property and frees the VT_LPWSTR allocation, which
        /// is what PropVariantClear does for that variant type.
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
        /// Side effects: decrements the COM reference count. Never throws: a broken
        /// reference count must not turn a finished segment into an exception.
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
                // Not worth failing a recording over.
            }
        }

        /// <summary>WAVE_FORMAT_IEEE_FLOAT (3), as a media subtype GUID.</summary>
        private static readonly Guid MediaSubtypeFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");

        /// <summary>WAVE_FORMAT_PCM (1), as a media subtype GUID.</summary>
        private static readonly Guid MediaSubtypePcm = new Guid("00000001-0000-0010-8000-00aa00389b71");

        /// <summary>
        /// The PCM layout a client was initialised with, and how to read one sample.
        /// </summary>
        /// <remarks>
        /// Immutable. <see cref="IsPassthrough"/> is the fast path: the engine
        /// accepted exactly s16 stereo 48 kHz, so packets are the output block.
        /// </remarks>
        private sealed class AudioFormat
        {
            /// <summary>Channel count in the source block.</summary>
            internal readonly int Channels;

            /// <summary>Sample rate; the caller only accepts 48000.</summary>
            internal readonly int SampleRate;

            /// <summary>Container bits per sample (16, 24 or 32).</summary>
            internal readonly int BitsPerSample;

            /// <summary>True when samples are IEEE float rather than integer PCM.</summary>
            internal readonly bool IsFloat;

            /// <summary>Bytes in one source frame.</summary>
            internal readonly int BlockAlign;

            /// <summary>Bytes in one source sample.</summary>
            internal readonly int BytesPerSample;

            /// <summary>Stores one accepted format.</summary>
            /// <param name="channels">Channel count.</param>
            /// <param name="sampleRate">Sample rate.</param>
            /// <param name="bitsPerSample">Container bits.</param>
            /// <param name="isFloat">True for IEEE float samples.</param>
            /// <remarks>Side effects: none.</remarks>
            internal AudioFormat(int channels, int sampleRate, int bitsPerSample, bool isFloat)
            {
                Channels = channels;
                SampleRate = sampleRate;
                BitsPerSample = bitsPerSample;
                IsFloat = isFloat;
                BytesPerSample = bitsPerSample / 8;
                BlockAlign = channels * BytesPerSample;
            }

            /// <summary>True when packets are already s16 stereo 48 kHz.</summary>
            internal bool IsPassthrough
            {
                get
                {
                    return !IsFloat && BitsPerSample == 16 && Channels == 2 && SampleRate == OutputSampleRate;
                }
            }

            /// <summary>
            /// Reads the WAVEFORMATEX header, keeping only formats this writer can
            /// consume at the expected output rate.
            /// </summary>
            /// <param name="pointer">Format pointer from GetMixFormat.</param>
            /// <param name="requiredRate">Rate the writer needs; anything else is refused.</param>
            /// <returns>The format, or null when the rate, depth or encoding is unsupported.</returns>
            /// <remarks>
            /// Side effects: reads unmanaged memory; frees nothing (the caller owns the
            /// block). WAVEFORMATEXTENSIBLE's wFormatTag is 0xFFFE and the real
            /// encoding lives in SubFormat's first four bytes; those are compared as
            /// little-endian integers because only the first field of the GUID carries
            /// meaning here.
            /// </remarks>
            internal static AudioFormat FromPointer(IntPtr pointer, int requiredRate)
            {
                WaveFormatEx header = (WaveFormatEx)Marshal.PtrToStructure(pointer, typeof(WaveFormatEx));
                if (header.SamplesPerSec != (uint)requiredRate
                    || header.Channels == 0
                    || header.BlockAlign == 0
                    || header.BlockAlign != header.Channels * (header.BitsPerSample / 8))
                {
                    return null;
                }

                bool isFloat;
                if (header.Tag == 3)
                {
                    isFloat = true;
                }
                else if (header.Tag == 1)
                {
                    isFloat = false;
                }
                else if (header.Tag == 0xFFFE && header.ExtraSize >= 22)
                {
                    int subFormat = Marshal.ReadInt32(pointer, 24);
                    if (subFormat == 3)
                    {
                        isFloat = true;
                    }
                    else if (subFormat == 1)
                    {
                        isFloat = false;
                    }
                    else
                    {
                        return null;
                    }
                }
                else
                {
                    return null;
                }

                int bits = header.BitsPerSample;
                if (bits != 16 && bits != 24 && bits != 32)
                {
                    return null;
                }
                if (isFloat && bits != 32)
                {
                    return null;
                }
                // More than two channels is accepted: the writer takes the front
                // left/right pair, which is what a stereo capture means. Fewer is
                // duplicated by the writer.
                return new AudioFormat(header.Channels, requiredRate, bits, isFloat);
            }

            /// <summary>Reads one source sample as s16.</summary>
            /// <param name="source">Packet bytes in source format.</param>
            /// <param name="frameOffset">Byte offset of the frame.</param>
            /// <param name="channel">Channel index within the frame.</param>
            /// <returns>The sample, clamped to the s16 range.</returns>
            /// <remarks>
            /// Side effects: none. Little-endian is Windows' byte order and the
            /// engine's, so the manual shifts are exact rather than an assumption
            /// about the host.
            /// </remarks>
            internal short ReadSample(byte[] source, int frameOffset, int channel)
            {
                int offset = frameOffset + (channel * BytesPerSample);
                if (IsFloat)
                {
                    float value = BitConverter.ToSingle(source, offset);
                    if (value >= 1.0f)
                    {
                        return short.MaxValue;
                    }
                    if (value <= -1.0f)
                    {
                        return short.MinValue;
                    }
                    return (short)(value * 32767.0f);
                }

                switch (BitsPerSample)
                {
                    case 16:
                        return (short)(source[offset] | (source[offset + 1] << 8));
                    case 24:
                        int value24 = source[offset]
                            | (source[offset + 1] << 8)
                            | (source[offset + 2] << 16);
                        if ((value24 & 0x800000) != 0)
                        {
                            value24 |= unchecked((int)0xFF000000);
                        }
                        return (short)(value24 >> 16);
                    default:
                        int value32 = source[offset]
                            | (source[offset + 1] << 8)
                            | (source[offset + 2] << 16)
                            | (source[offset + 3] << 24);
                        return (short)(value32 >> 16);
                }
            }

            /// <summary>Describes the format for a log line.</summary>
            /// <returns>"s16 48000 2ch" or "f32 48000 6ch" and so on.</returns>
            /// <remarks>Side effects: none.</remarks>
            internal string Describe()
            {
                string kind = IsFloat ? "f32" : ("s" + BitsPerSample.ToString(CultureInfo.InvariantCulture));
                return kind + " " + SampleRate.ToString(CultureInfo.InvariantCulture)
                    + " " + Channels.ToString(CultureInfo.InvariantCulture) + "ch";
            }
        }

        /// <summary>WAVEFORMATEX: the 18-byte format header.</summary>
        /// <remarks>Sequential with Pack 1: the exact Win32 layout, not the CLR's default padding.</remarks>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WaveFormatEx
        {
            internal ushort Tag;
            internal ushort Channels;
            internal uint SamplesPerSec;
            internal uint AverageBytesPerSec;
            internal ushort BlockAlign;
            internal ushort BitsPerSample;
            internal ushort ExtraSize;
        }

        /// <summary>WAVEFORMATEXTENSIBLE: WAVEFORMATEX plus depth, mask and subtype.</summary>
        /// <remarks>Sequential with Pack 1; 40 bytes, the size the audio engine expects.</remarks>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WaveFormatExtensible
        {
            internal WaveFormatEx Format;
            internal ushort Samples;
            internal uint ChannelMask;
            internal Guid SubFormat;
        }

        /// <summary>PROPERTYKEY: a format GUID plus one property id.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            internal Guid FormatId;
            internal int PropertyId;
        }

        /// <summary>PROPVARIANT sized for x64 and x86; only the string member is used.</summary>
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

        /// <summary>The MMDeviceEnumerator COM class (CLSID BCDE0395-E52F-467C-8E3D-C4579291692E).</summary>
        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MmDeviceEnumeratorComObject
        {
        }

        /// <summary>IMMDeviceEnumerator (A95664D2-9614-4F35-A746-DE8DB63617E6).</summary>
        /// <remarks>Methods in vtable order; a missing slot would misalign every later one.</remarks>
        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);

            int GetDevice(IntPtr id, out IMMDevice device);

            int RegisterEndpointNotificationCallback(IntPtr client);

            int UnregisterEndpointNotificationCallback(IntPtr client);
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

        /// <summary>IAudioClient (1CB9AD4C-DBFA-4c32-B178-C2F568A703B2).</summary>
        /// <remarks>
        /// Declared in full vtable order even though the writer uses five methods:
        /// a missing slot shifts GetService onto the wrong entry and fails with a
        /// plausible HRESULT.
        /// </remarks>
        [ComImport]
        [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);

            int GetBufferSize(out uint bufferFrames);

            int GetStreamLatency(out long latency);

            int GetCurrentPadding(out uint padding);

            int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);

            int GetMixFormat(out IntPtr format);

            int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

            int Start();

            int Stop();

            int Reset();

            int SetEventHandle(IntPtr eventHandle);

            int GetService(ref Guid interfaceId, out IntPtr instance);
        }

        /// <summary>IAudioCaptureClient (C8ADBD64-E71E-48a0-A4DE-185C395CD317).</summary>
        [ComImport]
        [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            int GetBuffer(out IntPtr data, out uint frames, out uint flags, out long devicePosition, out long qpcPosition);

            int ReleaseBuffer(uint frames);

            int GetNextPacketSize(out uint frames);
        }
    }
}
