// NamingTests - WO-05: the output file name of DESIGN.md section 7 and PLAN.md AC7.
//
// Oracle provenance: every expected string below is hand-written from
// "SnipVid_yyyy-MM-dd_HHmmss.mp4" (DESIGN.md section 7) and the collision rule
// "appending _2, _3" (same section). None of them is copied from Naming's output,
// so a change to the format string shows up here as a failure rather than as a
// matching baseline.
//
// The tests inject the timestamp and the "already exists" predicate, which is the
// whole reason Naming can live in src\Logic: no clock, no folder, no culture is
// read inside the module, and this file therefore needs no temp directory and no
// fixture on this machine.
//
// C4 is the one that earns the module its keep. DateTime.ToString("yyyy-MM-dd")
// without an explicit culture uses the current culture's calendar, so under
// ar-SA (Umm al-Qura, Hijri) the same instant renders as 1448-11-15 and under
// th-TH (Buddhist) as 2569-11-15. AC7's regex would then reject every recording
// made on such a machine. The test therefore sets Thread.CurrentThread.CurrentCulture
// itself, restores it in a finally block, and compares against the spec literal -
// not against Naming's own output under the invariant culture, which would pass
// for a current-culture implementation too.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using SnipVid.Logic;

namespace SnipVid.Tests
{
    /// <summary>
    /// Acceptance checks for <see cref="Naming"/>: one test method per work order
    /// criterion, each asserting the spec-visible string rather than a property
    /// ("is not empty") that a wrong implementation would also satisfy.
    /// </summary>
    public static class NamingTests
    {
        /// <summary>
        /// The C1 timestamp: 2026-11-15 14:30:02, kind Unspecified because a
        /// timestamp handed in from a session has no zone here and ToString does
        /// not convert.
        /// </summary>
        private static readonly DateTime Afternoon =
            new DateTime(2026, 11, 15, 14, 30, 2, DateTimeKind.Unspecified);

        /// <summary>The expected C1/C2/C4 name, spelled out from DESIGN.md section 7.</summary>
        private const string ExpectedName = "SnipVid_2026-11-15_143002.mp4";

        /// <summary>C1 (AC7): the name is exactly the spec string and matches the acceptance regex.</summary>
        /// <remarks>
        /// Both halves are asserted: the exact string pins the separators and the
        /// extension, while the regex is the oracle AC7 actually uses on the
        /// folder, so a name that satisfies only one of them is a real defect.
        /// </remarks>
        public static void TestC1_TimestampProducesSpecFileName()
        {
            string actual = Naming.FileName(Afternoon);

            Harness.Check(
                "C1 2026-11-15 14:30:02 gives SnipVid_2026-11-15_143002.mp4",
                actual == ExpectedName,
                "expected '" + ExpectedName + "', actual '" + actual + "'");

            Harness.Check(
                "C1b name matches AC7 regex SnipVid_\\d{4}-\\d\\d-\\d\\d_\\d{6}\\.mp4",
                Regex.IsMatch(actual, @"^SnipVid_\d{4}-\d\d-\d\d_\d{6}\.mp4$"),
                "expected a match for " + ExpectedName + ", actual '" + actual + "'");
        }

        /// <summary>C2: a taken name gains a numeric suffix before the extension, starting at _2.</summary>
        /// <remarks>
        /// The free case is asserted first so the test can tell "appends _2 on
        /// collision" from "always appends _2". The predicate is a set lookup, which
        /// is how the caller will wire File.Exists, and it is ordinal-ignore-case the
        /// way Windows compares file names.
        /// </remarks>
        public static void TestC2_CollisionAppendsSuffixBeforeExtension()
        {
            HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string free = Naming.UniqueFileName(Afternoon, taken.Contains);
            Harness.Check(
                "C2a no collision returns the unsuffixed name",
                free == ExpectedName,
                "expected '" + ExpectedName + "', actual '" + free + "'");

            taken.Add(ExpectedName);
            string second = Naming.UniqueFileName(Afternoon, taken.Contains);
            Harness.Check(
                "C2b one collision gives SnipVid_2026-11-15_143002_2.mp4",
                second == "SnipVid_2026-11-15_143002_2.mp4",
                "expected 'SnipVid_2026-11-15_143002_2.mp4', actual '" + second + "'");

            taken.Add(second);
            string third = Naming.UniqueFileName(Afternoon, taken.Contains);
            Harness.Check(
                "C2c two collisions give SnipVid_2026-11-15_143002_3.mp4",
                third == "SnipVid_2026-11-15_143002_3.mp4",
                "expected 'SnipVid_2026-11-15_143002_3.mp4', actual '" + third + "'");
        }

        /// <summary>
        /// C3: when every candidate is taken the resolver gives up after a fixed
        /// number of attempts instead of looping forever or returning a taken name.
        /// </summary>
        /// <remarks>
        /// Boundedness is asserted by counting predicate calls, never by a
        /// wall-clock duration: a count says "exactly this much work" on every
        /// machine, while a duration only says "fast enough here". The message
        /// must name the limit so a user reading the log knows the folder has an
        /// implausible number of files rather than that the app hung.
        /// </remarks>
        public static void TestC3_EveryCandidateTakenThrowsAfterTheLimit()
        {
            int attempts = 0;
            Exception caught = null;
            string returned = null;

            try
            {
                returned = Naming.UniqueFileName(
                    Afternoon,
                    delegate(string candidate)
                    {
                        attempts++;
                        return true;
                    });
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            Harness.Check(
                "C3 a fully taken folder throws InvalidOperationException",
                caught is InvalidOperationException,
                caught == null
                    ? "no exception; returned '" + returned + "' after " + attempts + " attempts"
                    : "expected InvalidOperationException, actual " + caught.GetType().Name
                        + ": " + caught.Message);

            Harness.Check(
                "C3b the exception message names the 1000-attempt limit",
                caught != null && caught.Message.IndexOf("1000", StringComparison.Ordinal) >= 0,
                caught == null
                    ? "no exception to inspect"
                    : "expected the message to contain '1000', actual: " + caught.Message);

            Harness.Check(
                "C3c work is bounded: exactly 1000 candidates were probed",
                attempts == 1000,
                "expected 1000 predicate calls, actual " + attempts);
        }

        /// <summary>
        /// C4: a non-Gregorian current culture must not change the date in the name.
        /// </summary>
        /// <remarks>
        /// ar-SA defaults to the Umm al-Qura calendar (year 1448 here) and th-TH to
        /// the Buddhist one (year 2569), so a ToString without an explicit culture
        /// produces a name that no longer matches AC7's regex - silently, at run
        /// time, only on those machines. The current culture is restored in a
        /// finally block so a failure cannot leak into the other test methods.
        /// </remarks>
        public static void TestC4_NonGregorianCulturesKeepTheInvariantDate()
        {
            CultureInfo original = Thread.CurrentThread.CurrentCulture;
            string hijriName = null;
            string hijriCollisionName = null;
            string thaiName = null;

            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("ar-SA");
                hijriName = Naming.FileName(Afternoon);
                hijriCollisionName = Naming.UniqueFileName(
                    Afternoon,
                    delegate(string candidate) { return candidate == ExpectedName; });

                Thread.CurrentThread.CurrentCulture = new CultureInfo("th-TH");
                thaiName = Naming.FileName(Afternoon);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
            }

            Harness.Check(
                "C4 ar-SA (Hijri) and th-TH (Buddhist) give the invariant 2026-11-15",
                hijriName == ExpectedName && thaiName == ExpectedName,
                "expected both '" + ExpectedName + "', actual ar-SA '" + hijriName
                    + "', th-TH '" + thaiName + "'");

            Harness.Check(
                "C4b the collision suffix under ar-SA is still SnipVid_2026-11-15_143002_2.mp4",
                hijriCollisionName == "SnipVid_2026-11-15_143002_2.mp4",
                "expected 'SnipVid_2026-11-15_143002_2.mp4', actual '" + hijriCollisionName + "'");
        }

        /// <summary>C5: midnight keeps six zero-padded digits, so the name sorts and matches AC7.</summary>
        /// <remarks>
        /// 2026-01-02 00:00:00 puts a single-digit month and day next to three
        /// zero-valued time fields, so a format string shortened to "yyyy-M-d_Hms"
        /// would render "2026-1-2_000" and the exact compare rejects it. Midnight
        /// rather than midday is the case that catches missing padding.
        /// </remarks>
        public static void TestC5_MidnightKeepsZeroPadding()
        {
            DateTime midnight = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Unspecified);
            string actual = Naming.FileName(midnight);

            Harness.Check(
                "C5 2026-01-02 00:00:00 gives SnipVid_2026-01-02_000000.mp4",
                actual == "SnipVid_2026-01-02_000000.mp4",
                "expected 'SnipVid_2026-01-02_000000.mp4', actual '" + actual + "'");
        }
    }
}
