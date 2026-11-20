// EncoderSelector - finds, on this machine, the best encoder profile that really
// produces frames, and remembers the answer.
//
// WHY this file exists: which encoder works is a fact about the hardware, not about
// the code. A desktop with one NVIDIA card is served by the zero-copy path; a laptop
// whose screen hangs off the Intel iGPU needs the frames copied to system memory
// first; a machine with no NVIDIA card needs a different encoder altogether. The
// failure that motivated this (SnipVid.log: "streams received no packets",
// frames=0) is silent - ffmpeg opens everything and then waits forever - so the
// only reliable test is to run a tiny real capture and count frames.
//
// Order of work:
//   1. A forced encoder (config encoder=<id>) is trusted as written: no probe, no
//      fallback. It is the escape hatch for a wrong guess.
//   2. Otherwise the cache (%LOCALAPPDATA%\SnipVid\encoder.cache) is consulted. The
//      key is the ffmpeg binary (path, size, timestamp) plus the monitor index, so
//      a new ffmpeg or another monitor is re-probed. A GPU or driver swap does not
//      change the key; the caller covers that by calling Invalidate and Select
//      again when a cached encoder produced no frames (see CaptureController).
//   3. Otherwise probe: first the capture alone (no encoder), because "ddagrab
//      cannot see this output" and "this encoder cannot read these frames" need
//      different messages, then each profile of EncoderProfile.Ladder in order.
//      The first that produces frames wins and is cached.
//
// Blocking: a probe that works takes about a second; one that hangs is killed after
// ProbeTimeoutMs. The worst realistic case is a hybrid laptop (the zero-copy
// probe hangs, the next rung works), so the first recording on a new machine can
// start a few seconds late, once. The caller runs this on the UI thread, so the
// timeout is deliberately short.
//
// This file does I/O and spawns processes, so it lives in the App layer, not in
// Logic (DESIGN.md section 11). All argument text comes from FfmpegArgs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using SnipVid.Logic;

namespace SnipVid
{
    /// <summary>
    /// Chooses the encoder profile for this machine by running short test captures.
    /// </summary>
    internal static class EncoderSelector
    {
        /// <summary>Longest a single probe may run before it is killed, in milliseconds.</summary>
        /// <remarks>
        /// A healthy probe is 20 frames at 30 fps, so under a second plus process
        /// start-up. Four seconds leaves room for a cold driver load and is short
        /// enough that a hung zero-frame probe does not look like a frozen app.
        /// </remarks>
        private const int ProbeTimeoutMs = 2000;

        /// <summary>The result of one probe run.</summary>
        private sealed class ProbeResult
        {
            public bool Ok;
            public int ExitCode;
            public long Frames;
            public long ElapsedMs;
            public bool TimedOut;
            public string Detail;
        }

        /// <summary>
        /// Picks the encoder profile for a capture.
        /// </summary>
        /// <param name="ffmpegPath">Resolved ffmpeg.exe, as used for the recording itself.</param>
        /// <param name="monitorIndex">output_idx the recording will use.</param>
        /// <param name="cq">Quality value, forwarded so the probe uses the real encoder flags.</param>
        /// <param name="configured">
        /// <see cref="Config.Encoder"/>: "auto" or a profile id; anything unknown is
        /// treated as auto.
        /// </param>
        /// <param name="notify">
        /// Receives the one "Detecting..." message shown before a real probe. May be
        /// null.
        /// </param>
        /// <param name="fromCache">True when the answer came from the cache.</param>
        /// <param name="failure">
        /// When the result is null: a message for the user saying what failed.
        /// Otherwise null.
        /// </param>
        /// <returns>
        /// The profile to record with, or null when nothing worked (capture itself
        /// failed, or every encoder failed).
        /// </returns>
        /// <remarks>
        /// Side effects: may start several short ffmpeg children, may write the cache
        /// file, writes log lines. Never throws: any failure to read or write the
        /// cache is treated as "no cache".
        /// </remarks>
        internal static EncoderProfile Select(
            string ffmpegPath, int monitorIndex, int cq, string configured,
            Action<string> notify, out bool fromCache, out string failure)
        {
            fromCache = false;
            failure = null;

            EncoderProfile forced = EncoderProfile.ById(configured);
            if (forced != null)
            {
                AppLog.Line("encoder: forced by config id=" + forced.Id + " (no probe)");
                return forced;
            }

            string key = CacheKey(ffmpegPath, monitorIndex);
            EncoderProfile cached = ReadCache(key);
            if (cached != null)
            {
                fromCache = true;
                AppLog.Line("encoder: cached id=" + cached.Id);
                return cached;
            }

            if (notify != null)
            {
                notify("Detecting the best video encoder for this PC (one time)...");
            }
            AppLog.Line("encoder: probing monitor=" + monitorIndex.ToString(CultureInfo.InvariantCulture));

            // Capture alone first. Twice, because a single transient failure (a UAC
            // prompt, a mode switch) must not be reported as "capture is broken".
            ProbeResult capture = Probe(ffmpegPath, null, monitorIndex, cq, "capture-only");
            if (!capture.Ok)
            {
                capture = Probe(ffmpegPath, null, monitorIndex, cq, "capture-only (retry)");
            }
            if (!capture.Ok)
            {
                failure = "Screen capture is not working on this monitor, so no encoder was tried."
                    + DetailSuffix(capture.Detail);
                AppLog.Line("encoder: none, capture-only probe failed");
                return null;
            }

            IList<EncoderProfile> ladder = EncoderProfile.Ladder;
            List<string> tried = new List<string>();
            for (int i = 0; i < ladder.Count; i++)
            {
                EncoderProfile profile = ladder[i];
                ProbeResult result = Probe(ffmpegPath, profile, monitorIndex, cq, profile.Id);
                tried.Add(profile.Id);
                if (result.Ok)
                {
                    WriteCache(key, profile);
                    AppLog.Line("encoder: selected id=" + profile.Id);
                    return profile;
                }
            }

            failure = "No working video encoder was found (tried " + string.Join(", ", tried.ToArray())
                + "). The screen can be captured, but none of the encoders accepted the frames.";
            AppLog.Line("encoder: none, every profile failed");
            return null;
        }

        /// <summary>Forgets the cached answer so the next <see cref="Select"/> probes again.</summary>
        /// <remarks>Side effects: deletes the cache file. Never throws.</remarks>
        internal static void Invalidate()
        {
            try
            {
                string path = CachePath();
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                AppLog.Line("encoder: cache dropped");
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>
        /// Runs one probe and reports whether it produced frames.
        /// </summary>
        /// <param name="ffmpegPath">ffmpeg.exe to run.</param>
        /// <param name="profile">Profile under test; null for the capture-only probe.</param>
        /// <param name="monitorIndex">output_idx.</param>
        /// <param name="cq">Quality value.</param>
        /// <param name="label">Name used in the log line.</param>
        /// <returns>The outcome; never null.</returns>
        /// <remarks>
        /// Side effects: one child process, killed after <see cref="ProbeTimeoutMs"/>,
        /// and one log line. Success means exit code 0 and at least one frame
        /// reported on the progress channel: the exit code alone proved nothing in
        /// the failure this exists for. Output is drained asynchronously because a
        /// chatty child blocked on a full pipe would otherwise look like a hang.
        /// </remarks>
        private static ProbeResult Probe(
            string ffmpegPath, EncoderProfile profile, int monitorIndex, int cq, string label)
        {
            ProbeResult result = new ProbeResult();
            result.Detail = string.Empty;
            string[] arguments = FfmpegArgs.BuildProbeArguments(profile, monitorIndex, cq);

            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = ffmpegPath;
            info.Arguments = FfmpegArgs.ToCommandLine(arguments);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.RedirectStandardInput = false;

            long frames = 0;
            string lastError = string.Empty;
            object gate = new object();
            Stopwatch stopwatch = Stopwatch.StartNew();

            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo = info;
                    process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        long parsed;
                        if (e.Data != null && e.Data.StartsWith("frame=", StringComparison.Ordinal)
                            && long.TryParse(
                                e.Data.Substring(6).Trim(), NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out parsed))
                        {
                            lock (gate)
                            {
                                if (parsed > frames)
                                {
                                    frames = parsed;
                                }
                            }
                        }
                    };
                    process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                        {
                            lock (gate)
                            {
                                lastError = e.Data.Trim();
                            }
                        }
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(ProbeTimeoutMs))
                    {
                        result.TimedOut = true;
                        try
                        {
                            process.Kill();
                        }
                        catch (InvalidOperationException)
                        {
                        }
                        process.WaitForExit(1000);
                    }
                    else
                    {
                        // The no-argument overload waits for the async readers to
                        // reach end of stream, so the final frame= line is counted.
                        process.WaitForExit();
                    }

                    result.ExitCode = result.TimedOut ? -1 : process.ExitCode;
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                result.ExitCode = -1;
                lastError = "could not start ffmpeg: " + ex.Message;
            }
            catch (InvalidOperationException ex)
            {
                result.ExitCode = -1;
                lastError = "probe failed: " + ex.Message;
            }

            stopwatch.Stop();
            lock (gate)
            {
                result.Frames = frames;
                result.Detail = result.TimedOut ? "no frames within " + ProbeTimeoutMs + " ms" : lastError;
            }
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
            result.Ok = !result.TimedOut && result.ExitCode == 0 && result.Frames > 0;

            AppLog.Line(
                "encoder: probe id=" + label
                + " ok=" + (result.Ok ? "yes" : "no")
                + " exit=" + result.ExitCode.ToString(CultureInfo.InvariantCulture)
                + " frames=" + result.Frames.ToString(CultureInfo.InvariantCulture)
                + " ms=" + result.ElapsedMs.ToString(CultureInfo.InvariantCulture)
                + " detail=" + (result.Detail.Length == 0 ? "(none)" : result.Detail));
            return result;
        }

        /// <summary>Formats a probe's detail as a sentence tail, or nothing.</summary>
        private static string DetailSuffix(string detail)
        {
            return string.IsNullOrEmpty(detail) ? string.Empty : " ffmpeg said: " + detail;
        }

        /// <summary>Where the cache lives: per user, next to nothing that is shipped.</summary>
        private static string CachePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SnipVid", "encoder.cache");
        }

        /// <summary>
        /// Builds the identity of "this ffmpeg on this monitor".
        /// </summary>
        /// <remarks>
        /// Path, size and last-write time stand in for "the same binary"; the monitor
        /// index is part of it because the display adapter, and so the working
        /// profile, can differ per output. Any failure to stat the file yields a key
        /// that never matches, which means "probe".
        /// </remarks>
        private static string CacheKey(string ffmpegPath, int monitorIndex)
        {
            try
            {
                FileInfo file = new FileInfo(ffmpegPath);
                return ffmpegPath.ToLowerInvariant()
                    + "|" + file.Length.ToString(CultureInfo.InvariantCulture)
                    + "|" + file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                    + "|" + monitorIndex.ToString(CultureInfo.InvariantCulture);
            }
            catch (IOException)
            {
                return "unreadable|" + Guid.NewGuid().ToString("N");
            }
            catch (UnauthorizedAccessException)
            {
                return "unreadable|" + Guid.NewGuid().ToString("N");
            }
        }

        /// <summary>
        /// Returns the cached profile when its key matches, else null.
        /// </summary>
        /// <remarks>
        /// Format: one "key|id" per line, so several monitors can be cached side by
        /// side. A malformed line, an unknown id or an unreadable file is simply no
        /// answer.
        /// </remarks>
        private static EncoderProfile ReadCache(string key)
        {
            try
            {
                string path = CachePath();
                if (!File.Exists(path))
                {
                    return null;
                }
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int cut = line.LastIndexOf('=');
                    if (cut <= 0)
                    {
                        continue;
                    }
                    if (string.Equals(line.Substring(0, cut), key, StringComparison.Ordinal))
                    {
                        return EncoderProfile.ById(line.Substring(cut + 1));
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            return null;
        }

        /// <summary>
        /// Stores the answer for this key, replacing any earlier line for it.
        /// </summary>
        /// <remarks>
        /// Side effects: rewrites the cache file. Failure is ignored: the cost of a
        /// missing cache is one more probe next time, never a failed recording.
        /// </remarks>
        private static void WriteCache(string key, EncoderProfile profile)
        {
            try
            {
                string path = CachePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                List<string> lines = new List<string>();
                if (File.Exists(path))
                {
                    foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        int cut = line.LastIndexOf('=');
                        if (cut > 0 && !string.Equals(line.Substring(0, cut), key, StringComparison.Ordinal))
                        {
                            lines.Add(line);
                        }
                    }
                }
                lines.Add(key + "=" + profile.Id);
                File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
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
