// EncoderProfile - which video encoder a capture uses, and how frames reach it.
//
// WHY this file exists: the validated command line (DESIGN.md section 5) assumes
// the adapter that drives the screen and the NVIDIA GPU are the same device, so
// ddagrab's frames can stay on the GPU and go straight into h264_nvenc. On a
// hybrid laptop that is false: the panel is wired to the Intel iGPU, ddagrab hands
// over frames that live on Intel, and NVENC cannot read them. ffmpeg opens both
// ends without an error and then no frame ever arrives (SnipVid.log shows
// "streams received no packets", frames=0, exit -2). A machine with no NVIDIA GPU
// at all fails the same way, because nothing else was ever tried.
//
// A profile names the encoder and says whether frames are copied through system
// memory first (hwdownload). That copy is the one step that makes any encoder work
// with any display adapter, at the price of some CPU, which is why the GPU-resident
// path is still tried first.
//
// The ladder is ordered best first: discrete-GPU encoding before the integrated
// one, GPU-resident frames before the CPU copy, software last. EncoderSelector
// walks it on the machine itself and keeps the first rung that really produces
// frames; this file is the pure half - names, order and argument lists - so the
// NVENC argv can be asserted byte for byte without a GPU (DESIGN.md section 11).
//
// No WPF, no P/Invoke, no I/O, no clock.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace SnipVid.Logic
{
    /// <summary>
    /// One way of turning ddagrab frames into H.264: an encoder plus the frame path.
    /// </summary>
    /// <remarks>
    /// Immutable. Instances exist only as the static members below, so an id always
    /// means the same argument list.
    /// </remarks>
    public sealed class EncoderProfile
    {
        /// <summary>The config value that means "probe this machine and choose".</summary>
        public const string AutoId = "auto";

        /// <summary>
        /// The filter suffix that copies ddagrab's GPU frames into system memory as
        /// BGRA, which is what every non-zero-copy profile needs.
        /// </summary>
        /// <remarks>
        /// Appended to the ddagrab source. hwdownload needs the format that follows
        /// it to name the pixel layout it is downloading, which is ddagrab's own.
        /// </remarks>
        public const string DownloadFilter = ",hwdownload,format=bgra";

        /// <summary>
        /// NVIDIA NVENC with frames left on the GPU. The validated command line, and
        /// the default whenever a request names no encoder.
        /// </summary>
        public static readonly EncoderProfile NvencDirect =
            new EncoderProfile("nvenc", "NVIDIA NVENC", "h264_nvenc", false);

        /// <summary>
        /// NVIDIA NVENC with frames copied through system memory: the profile for a
        /// laptop whose screen is driven by the integrated GPU.
        /// </summary>
        public static readonly EncoderProfile NvencDownload =
            new EncoderProfile("nvenc-download", "NVIDIA NVENC (frames via system memory)", "h264_nvenc", true);

        /// <summary>AMD AMF, discrete or integrated; frames via system memory.</summary>
        public static readonly EncoderProfile Amf =
            new EncoderProfile("amf", "AMD AMF", "h264_amf", true);

        /// <summary>Intel Quick Sync (the integrated GPU); frames via system memory.</summary>
        public static readonly EncoderProfile Qsv =
            new EncoderProfile("qsv", "Intel Quick Sync", "h264_qsv", true);

        /// <summary>Software x264: slower and heavier on the CPU, but works everywhere.</summary>
        public static readonly EncoderProfile X264 =
            new EncoderProfile("x264", "Software x264", "libx264", true);

        /// <summary>The ladder, best first. Declared after the members it lists.</summary>
        private static readonly EncoderProfile[] LadderProfiles = new EncoderProfile[]
        {
            NvencDirect, NvencDownload, Amf, Qsv, X264
        };

        /// <summary>Stores one profile; only the static members above call this.</summary>
        private EncoderProfile(string id, string label, string codec, bool downloadFrames)
        {
            Id = id;
            Label = label;
            Codec = codec;
            DownloadFrames = downloadFrames;
        }

        /// <summary>Short stable name used in config.ini, the log and the probe cache.</summary>
        public string Id { get; private set; }

        /// <summary>Human-readable name for a log line or a message.</summary>
        public string Label { get; private set; }

        /// <summary>The ffmpeg encoder name, for example <c>h264_nvenc</c>.</summary>
        public string Codec { get; private set; }

        /// <summary>
        /// True when ddagrab's frames are copied to system memory before encoding.
        /// </summary>
        public bool DownloadFrames { get; private set; }

        /// <summary>Every profile, best first. A fresh read-only view on each call.</summary>
        public static IList<EncoderProfile> Ladder
        {
            get { return Array.AsReadOnly(LadderProfiles); }
        }

        /// <summary>The ids a config file may name, for a note or a log line.</summary>
        public static string KnownChoices
        {
            get
            {
                string text = AutoId;
                for (int i = 0; i < LadderProfiles.Length; i++)
                {
                    text += ", " + LadderProfiles[i].Id;
                }
                return text;
            }
        }

        /// <summary>
        /// Builds the encoder-specific part of the output arguments: the codec and
        /// the quality settings that go with it.
        /// </summary>
        /// <param name="cq">
        /// The one quality number the app exposes (1-51, lower is better). It is
        /// mapped onto each encoder's own constant-quality control, so the overlay's
        /// presets mean the same thing whichever encoder is in use.
        /// </param>
        /// <returns>
        /// The arguments, in order, ready to go between the inputs and the
        /// <c>-g</c> flag. For <see cref="NvencDirect"/> this is exactly the list
        /// the validated command line has always used.
        /// </returns>
        /// <remarks>
        /// Side effects: none. Pixel formats are named only where the frame path is
        /// system memory: an explicit format on the GPU-resident path is the known
        /// way to make ddagrab -> h264_nvenc die with -40 (AGENTS.md gotcha 1), but
        /// after hwdownload the frames are ordinary BGRA, and an unnamed format
        /// would let ffmpeg choose 4:4:4 for x264, which many players cannot open.
        /// </remarks>
        public string[] VideoArgs(int cq)
        {
            string quality = cq.ToString(CultureInfo.InvariantCulture);

            switch (Codec)
            {
                case "h264_amf":
                    return new string[]
                    {
                        "-c:v", "h264_amf",
                        "-usage", "transcoding",
                        "-quality", "balanced",
                        "-rc", "cqp",
                        "-qp_i", quality,
                        "-qp_p", quality,
                        "-pix_fmt", "nv12",
                    };

                case "h264_qsv":
                    return new string[]
                    {
                        "-c:v", "h264_qsv",
                        "-preset", "medium",
                        "-global_quality", quality,
                        "-pix_fmt", "nv12",
                    };

                case "libx264":
                    return new string[]
                    {
                        "-c:v", "libx264",
                        "-preset", "veryfast",
                        "-crf", quality,
                        "-pix_fmt", "yuv420p",
                    };

                default:
                    return new string[]
                    {
                        "-c:v", "h264_nvenc",
                        "-preset", "p5",
                        "-tune", "hq",
                        "-cq", quality,
                    };
            }
        }

        /// <summary>Finds a profile by id, ignoring case.</summary>
        /// <param name="id">Id to look up; null, empty and "auto" are not profiles.</param>
        /// <returns>The profile, or null.</returns>
        /// <remarks>Side effects: none.</remarks>
        public static EncoderProfile ById(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            string wanted = id.Trim();
            for (int i = 0; i < LadderProfiles.Length; i++)
            {
                if (string.Equals(LadderProfiles[i].Id, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return LadderProfiles[i];
                }
            }
            return null;
        }

        /// <summary>
        /// Validates a config value for <c>encoder=</c> and normalises its spelling.
        /// </summary>
        /// <param name="text">The value from the file; null is rejected.</param>
        /// <param name="normalised">
        /// <see cref="AutoId"/> or a profile id in its canonical case; null on failure.
        /// </param>
        /// <returns>True when the value names "auto" or a known profile.</returns>
        /// <remarks>Side effects: none. Never throws.</remarks>
        public static bool TryParseChoice(string text, out string normalised)
        {
            normalised = null;
            if (text == null)
            {
                return false;
            }

            string trimmed = text.Trim();
            if (string.Equals(trimmed, AutoId, StringComparison.OrdinalIgnoreCase))
            {
                normalised = AutoId;
                return true;
            }

            EncoderProfile profile = ById(trimmed);
            if (profile == null)
            {
                return false;
            }
            normalised = profile.Id;
            return true;
        }

        /// <summary>The id, so a profile in a log line reads as its name.</summary>
        public override string ToString()
        {
            return Id;
        }
    }
}
