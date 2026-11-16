// SessionTests - WO-10: segment naming, the concat list, and the join plan.
//
// Oracle provenance: the segment name form is DESIGN.md section 5b
// ("scratch\<session>\seg-001.mp4"); the list format is DESIGN.md section 5b and
// RESEARCH.md section 4.5 ("file '<path>'", forward slashes, quotes escaped); the
// join command line is RESEARCH.md section 4.5 verbatim; the Concat/None split is
// DESIGN.md section 5b ("Even a single segment goes through this, with a one-entry
// list": a fragmented segment's movie header carries no duration, so a moved
// segment has no Length in Explorer) and section 9 ("0-byte segment excluded from
// the join list"). Nothing here is copied from the implementation's own output.
//
// The apostrophe case is not hypothetical: C:\Users\O'Brien is a real profile
// path, and ffmpeg's concat demuxer ends the quoted token at the first quote it
// finds. Measured with ffmpeg 8.1.1 on 2026-11-15: the unescaped line
// file 'C:/.../O'Brien/seg-001.mp4' was read as C:/.../OBrien/seg-001.mp4 and the
// join failed with exit -2; the same path written as 'O'\''Brien' joined to 120
// frames, 2.000 s, 60/1.
using System;
using System.Collections.Generic;
using SnipVid.Logic;

namespace SnipVid.Tests
{
    /// <summary>
    /// Checks for <see cref="Session"/>: one test method per WO-10 criterion,
    /// with one check per independently observable fact.
    /// </summary>
    public static class SessionTests
    {
        /// <summary>A scratch directory with no spaces, as SessionRunner builds it.</summary>
        private const string Scratch =
            @"C:\Users\x\AppData\Local\SnipVid\scratch\2026-11-15_143002-437";

        /// <summary>C1: segment files are numbered from 1 and zero-padded to three digits.</summary>
        /// <remarks>
        /// DESIGN.md section 5b names the files seg-001.mp4, and the padding is what
        /// makes an ordered directory listing and an ordered segment list the same
        /// order. Above 999 the number simply grows - three is a minimum width, not
        /// a cap - so 20 pauses and 2000 pauses still sort correctly.
        /// </remarks>
        public static void TestC1_SegmentNamingIsZeroPadded()
        {
            Harness.Check(
                "C1 segment 1 is seg-001.mp4 (DESIGN 5b)",
                Session.SegmentFileName(1) == "seg-001.mp4",
                "actual " + Session.SegmentFileName(1));

            Harness.Check(
                "C1 segment 12 is seg-012.mp4",
                Session.SegmentFileName(12) == "seg-012.mp4",
                "actual " + Session.SegmentFileName(12));

            Harness.Check(
                "C1 segment 1234 is seg-1234.mp4: three digits is a minimum, not a cap",
                Session.SegmentFileName(1234) == "seg-1234.mp4",
                "actual " + Session.SegmentFileName(1234));

            Harness.Check(
                "C1 the segment path is the scratch directory plus the name",
                Session.SegmentPath(Scratch, 2) == Scratch + @"\seg-002.mp4",
                "actual " + Session.SegmentPath(Scratch, 2));

            Harness.Check(
                "C1 a trailing separator on the scratch directory does not double",
                Session.SegmentPath(Scratch + @"\", 2) == Scratch + @"\seg-002.mp4",
                "actual " + Session.SegmentPath(Scratch + @"\", 2));

            bool rejected = false;
            try
            {
                Session.SegmentFileName(0);
            }
            catch (ArgumentOutOfRangeException)
            {
                rejected = true;
            }

            Harness.Check(
                "C1 segment index 0 is rejected: segments are 1-based, and seg-000 would sort before every real one",
                rejected,
                "SegmentFileName(0) returned a name instead of throwing");
        }

        /// <summary>C2: one list line is file 'path', with forward slashes and escaped quotes.</summary>
        /// <remarks>
        /// The escaping is asserted literally, not by round-tripping through a
        /// parser written here: ffmpeg's concat parser is the consumer, and its
        /// rule is the one measured on 2026-11-15 (see the file header).
        /// </remarks>
        public static void TestC2_ConcatListLineIsQuotedForwardSlashedText()
        {
            string plain = Session.ConcatListLine(@"C:\Users\x\Videos\seg-001.mp4");
            Harness.Check(
                "C2 a plain Windows path becomes file 'C:/Users/x/Videos/seg-001.mp4'",
                plain == @"file 'C:/Users/x/Videos/seg-001.mp4'",
                "actual " + plain);

            string apostrophe = Session.ConcatListLine(@"C:\Users\O'Brien\seg-001.mp4");
            Harness.Check(
                "C2 an apostrophe in the path is written as '\\'' so the quoted token does not end early",
                apostrophe == @"file 'C:/Users/O'\''Brien/seg-001.mp4'",
                "actual " + apostrophe);

            // "Forward slashes" means every separator backslash is gone. The
            // escaping sequence above contains one on purpose, so it is removed
            // before the check rather than being excused by a looser test: a
            // path like C:\a\b'/c would still fail here.
            string plainStripped = plain.Replace("'\\''", string.Empty);
            string apostropheStripped = apostrophe.Replace("'\\''", string.Empty);
            Harness.Check(
                "C2 the list line carries no separator backslash: every separator is a forward slash",
                plainStripped.IndexOf('\\') < 0 && apostropheStripped.IndexOf('\\') < 0,
                "plain " + plainStripped + " / apostrophe " + apostropheStripped);
        }

        /// <summary>C2: the whole list text is one line per segment, in order, newline-terminated.</summary>
        public static void TestC2_ConcatListTextIsOrderedAndComplete()
        {
            List<string> paths = new List<string>();
            paths.Add(@"C:\scratch\seg-001.mp4");
            paths.Add(@"C:\scratch\seg-002.mp4");
            paths.Add(@"C:\shake'N'Bake\seg-003.mp4");

            string text = Session.ConcatListText(paths);
            string expected =
                @"file 'C:/scratch/seg-001.mp4'" + "\r\n"
                + @"file 'C:/scratch/seg-002.mp4'" + "\r\n"
                + @"file 'C:/shake'\''N'\''Bake/seg-003.mp4'" + "\r\n";

            Harness.Check(
                "C2 the list text is exactly one file line per segment, in order, CRLF-terminated",
                text == expected,
                "expected [" + expected.Replace("\r\n", "\\r\\n") + "], actual ["
                    + text.Replace("\r\n", "\\r\\n") + "]");

            Harness.Check(
                "C2 an empty segment list produces empty text, not a stray newline",
                Session.ConcatListText(new List<string>()) == string.Empty,
                "actual [" + Session.ConcatListText(new List<string>()) + "]");

            Harness.Check(
                "C2 a null segment list produces empty text rather than throwing",
                Session.ConcatListText(null) == string.Empty,
                "actual [" + (Session.ConcatListText(null) ?? "<null>") + "]");
        }

        /// <summary>C5: zero-frame segments are filtered out, order preserved.</summary>
        /// <remarks>
        /// A pause-then-stop-immediately segment reports frame=0: ffmpeg wrote a
        /// header and no stream. Putting it in the list makes the join fail on a
        /// segment that contributes nothing, so it is dropped before the list is
        /// written (DESIGN.md section 9, "0-byte segment").
        /// </remarks>
        public static void TestC5_ZeroFrameSegmentsAreDropped()
        {
            List<SegmentInfo> segments = new List<SegmentInfo>();
            segments.Add(new SegmentInfo(@"C:\s\seg-001.mp4", 120));
            segments.Add(new SegmentInfo(@"C:\s\seg-002.mp4", 0));
            segments.Add(new SegmentInfo(@"C:\s\seg-003.mp4", 54));
            segments.Add(new SegmentInfo(@"C:\s\seg-004.mp4", -1));

            IList<SegmentInfo> usable = Session.UsableSegments(segments);

            Harness.Check(
                "C5 the zero-frame and negative-frame segments are dropped, the rest kept",
                usable.Count == 2
                    && usable[0].Path == @"C:\s\seg-001.mp4"
                    && usable[1].Path == @"C:\s\seg-003.mp4",
                "usable " + Describe(usable));

            Harness.Check(
                "C5 filtering preserves order and returns a new list rather than editing the session's",
                segments.Count == 4
                    && usable.Count != segments.Count
                    && ReferenceEquals(usable, segments) == false,
                "input count " + segments.Count + ", usable count " + usable.Count);

            Harness.Check(
                "C5 a null segment list yields an empty usable list",
                Session.UsableSegments(null).Count == 0,
                "UsableSegments(null) was not empty");
        }

        /// <summary>
        /// C3: any usable segment is a Concat, none is None (DESIGN.md section 5b,
        /// section 9).
        /// </summary>
        /// <remarks>
        /// One usable segment is C2 of WO-10: it goes through the same one-entry
        /// concat as any other count, because a moved fragmented segment has no
        /// duration in its movie header. The plan is what the caller switches on, so
        /// the count that must not regress is asserted here rather than inferred from
        /// the runner.
        /// </remarks>
        public static void TestC3_JoinPlanForEachSegmentCount()
        {
            List<SegmentInfo> zero = new List<SegmentInfo>();
            JoinPlan none = Session.PlanJoin(zero);
            Harness.Check(
                "C3 no segments plans None",
                none.Kind == JoinKind.None && none.Segments.Count == 0,
                "kind " + none.Kind + ", segments " + none.Segments.Count);

            List<SegmentInfo> one = new List<SegmentInfo>();
            one.Add(new SegmentInfo(@"C:\s\seg-001.mp4", 120));
            JoinPlan single = Session.PlanJoin(one);
            Harness.Check(
                "C3 one usable segment plans Concat with a one-entry list, not a move (DESIGN 5b)",
                single.Kind == JoinKind.Concat
                    && single.Segments.Count == 1
                    && single.Segments[0] == @"C:\s\seg-001.mp4",
                "kind " + single.Kind + ", segments ["
                    + string.Join(", ", new List<string>(single.Segments).ToArray()) + "]");

            List<SegmentInfo> several = new List<SegmentInfo>();
            several.Add(new SegmentInfo(@"C:\s\seg-001.mp4", 102));
            several.Add(new SegmentInfo(@"C:\s\seg-002.mp4", 54));
            several.Add(new SegmentInfo(@"C:\s\seg-003.mp4", 138));
            JoinPlan concat = Session.PlanJoin(several);
            Harness.Check(
                "C3 three usable segments plan Concat in segment order",
                concat.Kind == JoinKind.Concat
                    && concat.Segments.Count == 3
                    && concat.Segments[0] == @"C:\s\seg-001.mp4"
                    && concat.Segments[1] == @"C:\s\seg-002.mp4"
                    && concat.Segments[2] == @"C:\s\seg-003.mp4",
                "kind " + concat.Kind + ", segments ["
                    + string.Join(", ", new List<string>(concat.Segments).ToArray()) + "]");

            List<SegmentInfo> onePlusEmpty = new List<SegmentInfo>();
            onePlusEmpty.Add(new SegmentInfo(@"C:\s\seg-001.mp4", 0));
            onePlusEmpty.Add(new SegmentInfo(@"C:\s\seg-002.mp4", 240));
            JoinPlan afterFilter = Session.PlanJoin(onePlusEmpty);
            Harness.Check(
                "C3 the plan is decided after filtering: one usable plus one empty is a one-entry concat",
                afterFilter.Kind == JoinKind.Concat
                    && afterFilter.Segments.Count == 1
                    && afterFilter.Segments[0] == @"C:\s\seg-002.mp4",
                "kind " + afterFilter.Kind + ", segments ["
                    + string.Join(", ", new List<string>(afterFilter.Segments).ToArray()) + "]");

            List<SegmentInfo> allEmpty = new List<SegmentInfo>();
            allEmpty.Add(new SegmentInfo(@"C:\s\seg-001.mp4", 0));
            Harness.Check(
                "C3 all-empty plans None: there is nothing to join and nothing to save",
                Session.PlanJoin(allEmpty).Kind == JoinKind.None,
                "kind " + Session.PlanJoin(allEmpty).Kind);

            Harness.Check(
                "C3 a null session plans None rather than throwing: a stop with no segments is a toast, not a crash",
                Session.PlanJoin(null).Kind == JoinKind.None,
                "PlanJoin(null) did not plan None");
        }

        /// <summary>
        /// C2: the one-segment plan feeds the concat path a real list and a real
        /// command line, not a move (WO-10 C2, DESIGN.md section 5b).
        /// </summary>
        /// <remarks>
        /// Same functions the runner calls, in the same order: plan, then the list
        /// text built from its segments, then the argv built for that list. The
        /// assertion is exact text, so "exactly one file line" cannot be satisfied by
        /// an empty list that happens to produce no lines, and the argv must still be
        /// the concat invocation - a move has neither.
        /// </remarks>
        public static void TestC2_OneSegmentPlanIsAOneEntryConcat()
        {
            JoinPlan plan = Session.PlanJoin(Single(120));

            string listText = Session.ConcatListText(plan.Segments);
            string expectedList = @"file 'C:/s/seg-001.mp4'" + "\r\n";
            Harness.Check(
                "C2 a one-segment plan is a concat, not a move, and writes exactly one file line",
                plan.Kind == JoinKind.Concat && listText == expectedList,
                "kind " + plan.Kind + ", expected [" + expectedList.Replace("\r\n", "\\r\\n")
                    + "], actual [" + listText.Replace("\r\n", "\\r\\n") + "]");

            string[] args = Session.BuildJoinArguments(@"C:\s\list.txt", @"C:\out\joined.mp4");
            Harness.Check(
                "C2 a one-segment plan runs the concat argv, not a move",
                FfmpegArgs.ToCommandLine(args)
                    == "-hide_banner -loglevel error -y -f concat -safe 0 -i C:\\s\\list.txt -c copy C:\\out\\joined.mp4",
                "argv was [" + FfmpegArgs.ToCommandLine(args) + "]");
        }

        /// <summary>C3: the plan's frame count is the sum of the usable segments.</summary>
        /// <remarks>
        /// 1.7 s + 0.9 s + 2.3 s at 60 fps is 102 + 54 + 138 = 294 frames (PLAN
        /// AC12). The sum is what the app logs, so the arithmetic in the log can be
        /// checked against the file's own frame count.
        /// </remarks>
        public static void TestC3_PlanCountsFramesOfUsableSegments()
        {
            List<SegmentInfo> segments = new List<SegmentInfo>();
            segments.Add(new SegmentInfo(@"C:\s\seg-001.mp4", 102));
            segments.Add(new SegmentInfo(@"C:\s\seg-002.mp4", 0));
            segments.Add(new SegmentInfo(@"C:\s\seg-003.mp4", 54));
            segments.Add(new SegmentInfo(@"C:\s\seg-004.mp4", 138));

            JoinPlan plan = Session.PlanJoin(segments);
            Harness.Check(
                "C3 the plan reports 294 frames for 102 + 54 + 138, ignoring the empty segment (PLAN AC12)",
                plan.FrameCount == 294 && plan.Kind == JoinKind.Concat,
                "frames " + plan.FrameCount + ", kind " + plan.Kind);

            Harness.Check(
                "C3 the plan reports 240 frames for a one-entry concat of a 240-frame segment",
                Session.PlanJoin(Single(240)).FrameCount == 240,
                "frames " + Session.PlanJoin(Single(240)).FrameCount);
        }

        /// <summary>C4: the join command line is the measured one, verbatim.</summary>
        /// <remarks>
        /// RESEARCH.md section 4.5: ffmpeg -hide_banner -loglevel error -y
        /// -f concat -safe 0 -i list.txt -c copy joined.mp4. -c copy is the whole
        /// point: no re-encode, no quality loss, no second generation.
        /// </remarks>
        public static void TestC4_JoinArgumentsMatchTheMeasuredCommand()
        {
            string[] args = Session.BuildJoinArguments(@"C:\s\list.txt", @"C:\out\joined.mp4");
            string expected =
                "-hide_banner -loglevel error -y -f concat -safe 0 -i C:\\s\\list.txt -c copy C:\\out\\joined.mp4";

            Harness.Check(
                "C4 the join argv is -hide_banner -loglevel error -y -f concat -safe 0 -i <list> -c copy <out>",
                FfmpegArgs.ToCommandLine(args) == expected,
                "expected [" + expected + "], actual [" + FfmpegArgs.ToCommandLine(args) + "]");

            Harness.Check(
                "C4 the list path and the output path are the only two non-flag operands, in that order",
                Array.IndexOf(args, "-i") + 1 == Array.IndexOf(args, @"C:\s\list.txt")
                    && Array.IndexOf(args, "-c") + 1 == Array.IndexOf(args, "copy")
                    && args[args.Length - 1] == @"C:\out\joined.mp4",
                "argv was [" + string.Join("] [", args) + "]");
        }

        /// <summary>Builds a one-element segment list with the given frame count.</summary>
        private static List<SegmentInfo> Single(int frames)
        {
            List<SegmentInfo> list = new List<SegmentInfo>();
            list.Add(new SegmentInfo(@"C:\s\seg-001.mp4", frames));
            return list;
        }

        /// <summary>Renders a segment list for a failure message.</summary>
        private static string Describe(IList<SegmentInfo> segments)
        {
            if (segments == null)
            {
                return "<null>";
            }
            List<string> parts = new List<string>();
            for (int i = 0; i < segments.Count; i++)
            {
                parts.Add(segments[i].Path + "(" + segments[i].Frames + ")");
            }
            return string.Join(", ", parts.ToArray());
        }
    }
}
