// Session - the pure half of pause-and-join (DESIGN.md section 5b, decision D4).
//
// WHY pause is a file boundary: ddagrab captures wall-clock time and has no pause
// control, so the only implementation that leaves the validated capture argv
// untouched is to end the current file with "q" and start a new one on resume.
// That makes three decisions that must be identical everywhere and are therefore
// made here, once:
//
//   1. What a segment is called. seg-NNN.mp4 in a per-session scratch directory,
//      numbered from 1, so "ordered list" and "ordered directory" are the same
//      order and 2 pauses and 20 pauses are the same code path.
//   2. What the concat demuxer is handed. One "file '<path>'" line per usable
//      segment, forward slashes, and a single quote escaped as '\''. The escaping
//      is load-bearing: ffmpeg's concat parser ends the quoted token at the first
//      quote it sees, so an unescaped C:\Users\O'Brien\seg-001.mp4 was read as
//      C:\Users\OBrien\seg-001.mp4 and the join failed (measured with ffmpeg
//      8.1.1, 2026-11-15). The '\'' form joined to 120 frames, 2.000 s, 60/1.
//   3. What to do with the segments. Every usable segment goes through one -c copy
//      concat, one entry or twenty - and WHY the single-segment case is not a
//      File.Move: the recorder writes fragmented output (+frag_keyframe+empty_moov)
//      so a killed app still leaves a playable file, and +empty_moov writes a movie
//      header with no duration. Explorer then shows no Length in the tooltip, the
//      properties dialog or a duration sort. The concat pass rewrites the file as
//      an ordinary MP4 and the duration appears (measured 2026-11-15: 148 ms for a
//      3 s clip, 253 ms for 100 MB). None means nothing to save, rather than a join
//      that fails on an empty list. Zero-frame segments never reach the list,
//      because a segment that contributes no frame yet fails the join would turn
//      "paused and stopped immediately" into "your recording is gone" (DESIGN.md
//      section 9, "0-byte segment").
//
// The join command line lives here, next to the list format it consumes, for the
// same reason the capture command line lives in FfmpegArgs: one auditable place.
// It is the measured command from RESEARCH.md section 4.5, not an improvisation:
// -c copy means no re-encode, so a join cannot lose quality or change frame rate.
//
// No WPF, no P/Invoke, no I/O, no clock, no Process: test.ps1 compiles this file
// without any Windows reference (DESIGN.md section 11), so the session decisions
// are testable on a machine with no ffmpeg and no scratch directory.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SnipVid.Logic
{
    /// <summary>
    /// One finished segment: where it is and how many frames it reported.
    /// </summary>
    /// <remarks>
    /// Immutable carrier with no validation. The frame count comes from ffmpeg's
    /// own progress output (Recorder), which is the only source that can tell a
    /// real segment from a header-only stub: a stopped-immediately segment reports
    /// frame=0 while still leaving a non-empty file on disk.
    /// </remarks>
    public sealed class SegmentInfo
    {
        /// <summary>Stores one segment.</summary>
        /// <param name="path">Absolute path inside the session's scratch directory.</param>
        /// <param name="frames">Frames ffmpeg reported for this segment; 0 means none.</param>
        /// <remarks>Side effects: none. Values are stored verbatim, including negatives.</remarks>
        public SegmentInfo(string path, int frames)
        {
            Path = path;
            Frames = frames;
        }

        /// <summary>Absolute path of the segment file.</summary>
        /// <remarks>May be null or empty; <see cref="Session.UsableSegments"/> drops those.</remarks>
        public string Path { get; private set; }

        /// <summary>Frames ffmpeg reported for this segment.</summary>
        /// <remarks>Zero or negative means "usable but empty"; see <see cref="Session.UsableSegments"/>.</remarks>
        public int Frames { get; private set; }
    }

    /// <summary>How a session's segments become the one output file.</summary>
    /// <remarks>
    /// There is deliberately no single-segment fast path (DESIGN.md section 5b):
    /// the recorder's fragmented output carries no duration in its movie header,
    /// so a moved segment has no <c>Length</c> in Explorer while a concat of the
    /// same bytes does. <see cref="None"/> exists so "nothing was recorded" is a
    /// message rather than a join that fails on an empty list.
    /// </remarks>
    public enum JoinKind
    {
        /// <summary>No usable segment: nothing to join, nothing to save.</summary>
        None = 0,

        /// <summary>One or more usable segments: concat them with -c copy.</summary>
        Concat = 1,
    }

    /// <summary>
    /// The decision about how to produce the output file, plus the segments and
    /// frame total it was made from.
    /// </summary>
    /// <remarks>
    /// Produced only by <see cref="Session.PlanJoin"/>, so a caller cannot
    /// construct a plan that disagrees with the rules. Immutable.
    /// </remarks>
    public sealed class JoinPlan
    {
        /// <summary>Stores one plan; called only by <see cref="Session.PlanJoin"/>.</summary>
        /// <param name="kind">Concat or None.</param>
        /// <param name="segments">Usable segment paths, in recording order; empty for None.</param>
        /// <param name="frameCount">Sum of the usable segments' frame counts.</param>
        /// <remarks>Side effects: none. The list is copied, so a later change to the caller's list cannot move this plan.</remarks>
        internal JoinPlan(JoinKind kind, IList<string> segments, int frameCount)
        {
            Kind = kind;
            Segments = new List<string>(segments).AsReadOnly();
            FrameCount = frameCount;
        }

        /// <summary>Which of the two shapes this plan is.</summary>
        public JoinKind Kind { get; private set; }

        /// <summary>The usable segment paths, in recording order.</summary>
        /// <remarks>
        /// Empty for <see cref="JoinKind.None"/>, one entry for a pause-free
        /// recording; the list text is built from exactly this sequence.
        /// </remarks>
        public IList<string> Segments { get; private set; }

        /// <summary>Total frames across the usable segments.</summary>
        /// <remarks>
        /// The arithmetic the log line and the acceptance criteria compare against
        /// the joined file: 102 + 54 + 138 = 294 for PLAN AC12. Counts only what
        /// will actually be in the output.
        /// </remarks>
        public int FrameCount { get; private set; }
    }

    /// <summary>
    /// Segment naming, concat-list generation and the join decision
    /// (DESIGN.md section 5b).
    /// </summary>
    /// <remarks>
    /// Total functions of their arguments: no filesystem access, no clock, no
    /// process. The caller (SessionRunner) owns the directory, the files and the
    /// join child.
    /// </remarks>
    public static class Session
    {
        /// <summary>File name of the generated concat list inside the scratch directory.</summary>
        /// <remarks>
        /// Generated, never hand-edited (DESIGN.md section 5b). Kept next to the
        /// format it holds so a reader cannot change one without seeing the other.
        /// </remarks>
        public const string ListFileName = "list.txt";

        /// <summary>Extension of every segment; the same container the output uses.</summary>
        private const string SegmentExtension = ".mp4";

        /// <summary>
        /// Minimum digits in a segment number.
        /// </summary>
        /// <remarks>
        /// Three, so seg-009 sorts before seg-010 in Explorer and in a plain string
        /// comparison. A minimum, not a cap: the thousandth segment is seg-1000.mp4
        /// and still sorts last.
        /// </remarks>
        private const int SegmentDigits = 3;

        /// <summary>Builds the bare file name for a 1-based segment index.</summary>
        /// <param name="index">Segment number, 1 or greater.</param>
        /// <returns>For example <c>seg-001.mp4</c>.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="index"/> is below 1: segments are numbered from one, and
        /// seg-000 would sort before every real segment for no reason.
        /// </exception>
        /// <remarks>Side effects: none.</remarks>
        public static string SegmentFileName(int index)
        {
            if (index < 1)
            {
                throw new ArgumentOutOfRangeException(
                    "index",
                    index.ToString(CultureInfo.InvariantCulture),
                    "segment numbers start at 1: there is no seg-000.mp4");
            }

            // Invariant culture: a culture with non-ASCII digits would produce a
            // file name the concat list and Explorer disagree about.
            return "seg-" + index.ToString("D" + SegmentDigits.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture) + SegmentExtension;
        }

        /// <summary>Builds the absolute path of a segment inside the scratch directory.</summary>
        /// <param name="scratchDirectory">The session's scratch directory.</param>
        /// <param name="index">Segment number, 1 or greater.</param>
        /// <returns>For example <c>C:\...\scratch\2026-11-15_143002-437\seg-002.mp4</c>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="scratchDirectory"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is below 1.</exception>
        /// <remarks>
        /// Side effects: none. One trailing separator is tolerated so a caller that
        /// builds the directory with a trailing slash does not get a double
        /// separator; the separator is a backslash because this is a Windows file
        /// system, unlike the list text, which ffmpeg wants forward-slashed.
        /// </remarks>
        public static string SegmentPath(string scratchDirectory, int index)
        {
            if (scratchDirectory == null)
            {
                throw new ArgumentNullException("scratchDirectory");
            }

            string directory = scratchDirectory.TrimEnd('\\', '/');
            return directory + "\\" + SegmentFileName(index);
        }

        /// <summary>
        /// Drops the segments that would contribute nothing to the output.
        /// </summary>
        /// <param name="segments">Every stopped segment, in recording order; may be null.</param>
        /// <returns>
        /// A new list holding, in the same order, the segments that have a path and
        /// at least one frame. The input list is never modified.
        /// </returns>
        /// <remarks>
        /// Side effects: none. The rule is "frames greater than zero" rather than
        /// "file is non-empty" because a paused-then-stopped segment leaves a
        /// non-empty file with no stream, and listing it makes the join fail on a
        /// segment that cannot contribute a frame (DESIGN.md section 9).
        /// </remarks>
        public static IList<SegmentInfo> UsableSegments(IList<SegmentInfo> segments)
        {
            List<SegmentInfo> usable = new List<SegmentInfo>();
            if (segments == null)
            {
                return usable;
            }

            for (int i = 0; i < segments.Count; i++)
            {
                SegmentInfo segment = segments[i];
                if (segment != null && !string.IsNullOrEmpty(segment.Path) && segment.Frames > 0)
                {
                    usable.Add(segment);
                }
            }
            return usable;
        }

        /// <summary>Builds the concat list's text for a set of segment paths.</summary>
        /// <param name="segmentPaths">Segment paths in recording order; may be null or empty.</param>
        /// <returns>
        /// One <c>file 'path'</c> line per segment, CRLF-terminated, or the empty
        /// string when there are no segments.
        /// </returns>
        /// <exception cref="ArgumentNullException">One of the paths is null.</exception>
        /// <remarks>
        /// Side effects: none. CRLF because this is a Windows text file; ffmpeg's
        /// concat parser accepts either terminator. The trailing terminator is
        /// written so the last <c>file</c> line cannot be truncated by a reader that
        /// requires one.
        /// </remarks>
        public static string ConcatListText(IList<string> segmentPaths)
        {
            if (segmentPaths == null || segmentPaths.Count == 0)
            {
                return string.Empty;
            }

            StringBuilder text = new StringBuilder();
            for (int i = 0; i < segmentPaths.Count; i++)
            {
                text.Append(ConcatListLine(segmentPaths[i]));
                text.Append("\r\n");
            }
            return text.ToString();
        }

        /// <summary>Builds one <c>file 'path'</c> line for the concat list.</summary>
        /// <param name="segmentPath">Absolute path of one segment.</param>
        /// <returns>The line, without a terminator.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="segmentPath"/> is null.</exception>
        /// <remarks>
        /// Side effects: none. Two transforms, both measured rather than assumed:
        /// backslashes become forward slashes, and a single quote becomes the
        /// sequence <c>'\''</c> (close the quote, an escaped quote, reopen), which is
        /// what ffmpeg's concat parser needs to keep the token alive. An unescaped
        /// apostrophe truncated the path at the quote and the join failed with exit
        /// -2 (ffmpeg 8.1.1, 2026-11-15).
        /// </remarks>
        public static string ConcatListLine(string segmentPath)
        {
            if (segmentPath == null)
            {
                throw new ArgumentNullException("segmentPath");
            }

            string path = segmentPath.Replace('\\', '/');
            path = path.Replace("'", "'\\''");
            return "file '" + path + "'";
        }

        /// <summary>
        /// Decides how the session becomes one file: one concat, or nothing.
        /// </summary>
        /// <param name="segments">Every stopped segment, in recording order; may be null.</param>
        /// <returns>
        /// A plan whose kind and segment list are decided from the <em>usable</em>
        /// segments, never from the raw list. One usable segment is
        /// <see cref="JoinKind.Concat"/> with a one-entry list, for the reason in the
        /// file header: the concat is what gives the file a duration.
        /// </returns>
        /// <remarks>
        /// Side effects: none. Null and all-empty are <see cref="JoinKind.None"/>
        /// rather than an error: stopping a recording that captured nothing is a
        /// toast, not a crash, and the caller has no useful alternative.
        /// </remarks>
        public static JoinPlan PlanJoin(IList<SegmentInfo> segments)
        {
            IList<SegmentInfo> usable = UsableSegments(segments);

            List<string> paths = new List<string>(usable.Count);
            int frameCount = 0;
            for (int i = 0; i < usable.Count; i++)
            {
                paths.Add(usable[i].Path);
                frameCount += usable[i].Frames;
            }

            if (paths.Count > 0)
            {
                return new JoinPlan(JoinKind.Concat, paths, frameCount);
            }
            return new JoinPlan(JoinKind.None, paths, frameCount);
        }

        /// <summary>
        /// Builds the join command line: the measured concat invocation from
        /// RESEARCH.md section 4.5.
        /// </summary>
        /// <param name="listPath">Path of the generated concat list.</param>
        /// <param name="outputPath">Destination file; the child's last argument.</param>
        /// <returns>
        /// <c>-hide_banner -loglevel error -y -f concat -safe 0 -i list.txt -c copy out.mp4</c>.
        /// </returns>
        /// <exception cref="ArgumentNullException">Either path is null.</exception>
        /// <remarks>
        /// Side effects: none. -safe 0 is required because the list holds absolute
        /// paths; -c copy is required because the join must not re-encode, which is
        /// what makes a pause cost nothing but a keyframe boundary. A one-entry list
        /// is deliberately the same command (DESIGN.md section 5b): that pass exists
        /// to give the file a real duration, not to combine segments.
        /// </remarks>
        public static string[] BuildJoinArguments(string listPath, string outputPath)
        {
            if (listPath == null)
            {
                throw new ArgumentNullException("listPath");
            }
            if (outputPath == null)
            {
                throw new ArgumentNullException("outputPath");
            }

            return new string[]
            {
                "-hide_banner",
                "-loglevel", "error",
                "-y",
                "-f", "concat",
                "-safe", "0",
                "-i", listPath,
                "-c", "copy",
                outputPath
            };
        }
    }
}
