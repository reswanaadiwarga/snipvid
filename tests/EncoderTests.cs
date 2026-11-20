// EncoderTests - the encoder ladder: profile order, per-encoder arguments, the
// frame path, and the config value that can pin one.
//
// Oracle provenance: the NVENC argument list is transcribed from DESIGN.md
// section 5 (the validated invocation); the other encoders' flags come from
// ffmpeg's own documented options for h264_qsv, h264_amf and libx264. The
// ladder order is the design decision in EncoderProfile's header: best first,
// GPU-resident before downloaded, hardware before software.
using System;
using SnipVid.Logic;

namespace SnipVid.Tests
{
    /// <summary>Checks the encoder profiles and the arguments each one produces.</summary>
    public static class EncoderTests
    {
        /// <summary>A request with the given profile; null means "did not say".</summary>
        private static FfmpegRequest Request(EncoderProfile profile)
        {
            return new FfmpegRequest(
                @"C:\out\a.mp4", 0, new PxRect(0, 0, 640, 360), 60, 19, null, null, profile);
        }

        /// <summary>True when the list contains the pair, in that order.</summary>
        private static bool HasPair(string[] args, string first, string second)
        {
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == first && args[i + 1] == second)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>True when any element equals the value.</summary>
        private static bool Has(string[] args, string value)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == value)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>The ladder is ordered best-first and starts with the validated path.</summary>
        public static void TestE1_LadderOrderAndValidatedDefault()
        {
            string order = string.Join(",", new string[]
            {
                EncoderProfile.Ladder[0].Id,
                EncoderProfile.Ladder[1].Id,
                EncoderProfile.Ladder[2].Id,
                EncoderProfile.Ladder[3].Id,
                EncoderProfile.Ladder[4].Id,
            });
            Harness.Check("E1 the ladder is nvenc,nvenc-download,amf,qsv,x264 best first",
                order == "nvenc,nvenc-download,amf,qsv,x264", "got " + order);

            string[] args = EncoderProfile.NvencDirect.VideoArgs(19);
            string text = string.Join(" ", args);
            Harness.Check("E1 the zero-copy profile is exactly the validated NVENC line",
                text == "-c:v h264_nvenc -preset p5 -tune hq -cq 19", "got " + text);
            Harness.Check("E1 the zero-copy profile leaves frames on the GPU",
                !EncoderProfile.NvencDirect.DownloadFrames, "DownloadFrames is true");
        }

        /// <summary>Every non-zero-copy profile downloads frames, and only that one does not.</summary>
        public static void TestE2_FramePathPerProfile()
        {
            Harness.Check("E2 nvenc-download copies frames through system memory",
                EncoderProfile.NvencDownload.DownloadFrames, "DownloadFrames is false");
            Harness.Check("E2 amf and qsv and x264 all copy frames through system memory",
                EncoderProfile.Amf.DownloadFrames && EncoderProfile.Qsv.DownloadFrames
                    && EncoderProfile.X264.DownloadFrames, "one of amf/qsv/x264 does not download");
            Harness.Check("E2 the download suffix names bgra, ddagrab's own format",
                EncoderProfile.DownloadFilter == ",hwdownload,format=bgra",
                "got " + EncoderProfile.DownloadFilter);
        }

        /// <summary>Each encoder gets its own quality control, mapped from the one cq number.</summary>
        public static void TestE3_PerEncoderQualityFlags()
        {
            string[] qsv = EncoderProfile.Qsv.VideoArgs(22);
            Harness.Check("E3 qsv uses -global_quality and asks for nv12",
                HasPair(qsv, "-global_quality", "22") && HasPair(qsv, "-pix_fmt", "nv12")
                    && HasPair(qsv, "-c:v", "h264_qsv"),
                "got " + string.Join(" ", qsv));

            string[] x264 = EncoderProfile.X264.VideoArgs(25);
            Harness.Check("E3 x264 uses -crf and pins yuv420p so players can open it",
                HasPair(x264, "-crf", "25") && HasPair(x264, "-pix_fmt", "yuv420p")
                    && HasPair(x264, "-c:v", "libx264"),
                "got " + string.Join(" ", x264));

            string[] amf = EncoderProfile.Amf.VideoArgs(19);
            Harness.Check("E3 amf uses cqp with both qp values set",
                HasPair(amf, "-rc", "cqp") && HasPair(amf, "-qp_i", "19")
                    && HasPair(amf, "-qp_p", "19") && HasPair(amf, "-c:v", "h264_amf"),
                "got " + string.Join(" ", amf));
        }

        /// <summary>Config values resolve to profiles, and "auto" is not a profile.</summary>
        public static void TestE4_ProfileLookup()
        {
            Harness.Check("E4 ById is case-insensitive",
                EncoderProfile.ById("QSV") == EncoderProfile.Qsv, "ById(\"QSV\") did not find qsv");
            Harness.Check("E4 ById refuses auto and empty, because neither names an encoder",
                EncoderProfile.ById("auto") == null && EncoderProfile.ById("") == null
                    && EncoderProfile.ById(null) == null, "one of auto/empty/null resolved");

            string normalised;
            Harness.Check("E4 auto parses to auto",
                EncoderProfile.TryParseChoice("AUTO", out normalised) && normalised == "auto",
                "got " + (normalised ?? "null"));
            Harness.Check("E4 a profile id is normalised to its canonical spelling",
                EncoderProfile.TryParseChoice("X264", out normalised) && normalised == "x264",
                "got " + (normalised ?? "null"));
            Harness.Check("E4 an unknown id is rejected rather than guessed",
                !EncoderProfile.TryParseChoice("cuda", out normalised), "cuda was accepted");
        }

        /// <summary>The named choices list every id, so a note can tell the user what is valid.</summary>
        public static void TestE5_KnownChoices()
        {
            string choices = EncoderProfile.KnownChoices;
            bool all = choices.Contains("auto") && choices.Contains("nvenc")
                && choices.Contains("nvenc-download") && choices.Contains("amf")
                && choices.Contains("qsv") && choices.Contains("x264");
            Harness.Check("E5 KnownChoices names auto and all five profiles", all, "got " + choices);
        }

        /// <summary>A request that names no encoder builds exactly the argv it always did.</summary>
        public static void TestE6_DefaultRequestIsUnchanged()
        {
            string unspecified = FfmpegArgs.ToCommandLine(FfmpegArgs.Build(Request(null)));
            string explicitNvenc = FfmpegArgs.ToCommandLine(
                FfmpegArgs.Build(Request(EncoderProfile.NvencDirect)));
            Harness.Check("E6 no encoder and nvenc produce the same command line",
                unspecified == explicitNvenc, "the two differ");
            Harness.Check("E6 the default command line carries no hwdownload",
                unspecified.IndexOf("hwdownload", StringComparison.Ordinal) < 0,
                "hwdownload appeared in the default argv");
        }

        /// <summary>A download profile rewrites the ddagrab input and swaps the encoder.</summary>
        public static void TestE7_DownloadProfileArgv()
        {
            string[] args = FfmpegArgs.Build(Request(EncoderProfile.Qsv));
            string input = null;
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "-i")
                {
                    input = args[i + 1];
                    break;
                }
            }
            Harness.Check("E7 the ddagrab input ends with the download filter",
                input != null && input.EndsWith(",hwdownload,format=bgra", StringComparison.Ordinal),
                "input was " + (input ?? "(none)"));
            Harness.Check("E7 the encoder is qsv and the NVENC quality flag is gone",
                HasPair(args, "-c:v", "h264_qsv") && !Has(args, "-cq"),
                "argv has the wrong codec or kept -cq");
        }

        /// <summary>The probe is a short, silent capture on the same progress channel.</summary>
        public static void TestE8_ProbeArguments()
        {
            string[] captureOnly = FfmpegArgs.BuildProbeArguments(null, 0, 19);
            string text = string.Join(" ", captureOnly);
            Harness.Check("E8 the capture-only probe downloads frames and writes nothing",
                text.Contains("hwdownload,format=bgra") && text.EndsWith("-f null -", StringComparison.Ordinal),
                "got " + text);
            Harness.Check("E8 the probe is bounded to 20 frames at 256x256",
                text.Contains("video_size=256x256") && HasPair(captureOnly, "-frames:v", "20"),
                "got " + text);

            string[] qsv = FfmpegArgs.BuildProbeArguments(EncoderProfile.Qsv, 0, 19);
            Harness.Check("E8 a profile probe uses that profile's encoder",
                HasPair(qsv, "-c:v", "h264_qsv"), "got " + string.Join(" ", qsv));
        }

        /// <summary>The config key accepts auto and each id, and never pins a typo.</summary>
        public static void TestE9_ConfigEncoderChoice()
        {
            Config none = Config.Parse("[record]\nfps=60\n", @"C:\Users\example");
            Harness.Check("E9 an absent encoder means auto, with no note",
                none.Encoder == "auto" && none.Notes.Count == 0,
                "got " + none.Encoder + ", notes " + none.Notes.Count);

            Config pinned = Config.Parse("[record]\nencoder=qsv\n", @"C:\Users\example");
            Harness.Check("E9 a named profile is kept and produces no note",
                pinned.Encoder == "qsv" && pinned.Notes.Count == 0,
                "got " + pinned.Encoder + ", notes " + pinned.Notes.Count);

            Config typo = Config.Parse("[record]\nencoder=cuda\n", @"C:\Users\example");
            Harness.Check("E9 an unknown encoder falls back to auto with exactly one note",
                typo.Encoder == "auto" && typo.Notes.Count == 1,
                "got " + typo.Encoder + ", notes " + typo.Notes.Count);
        }
    }
}
