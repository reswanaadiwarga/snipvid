// Verifier-written acceptance tests for WO-05 (filename generation).
//
// Independent of tests\NamingTests.cs: compiled with src\Logic\*.cs and
// tests\Harness.cs only. Every expected string below is transcribed from the
// spec, not from Naming's output:
//   * DESIGN.md section 7 - "Filename: SnipVid_yyyy-MM-dd_HHmmss.mp4,
//     collision-safe by appending _2, _3..."
//   * PLAN.md AC7         - "file matches SnipVid_\d{4}-\d\d-\d\d_\d{6}\.mp4"
//   * WO-05 C1..C5        - exact name, _2 then _3, 1000 collisions throw,
//     ar-SA/th-TH identical, midnight keeps padding
//   * WO-05 note          - a non-Gregorian culture must not change the year
//   * DESIGN.md section 11 - the caller injects the clock and the "exists"
//     predicate; Logic holds no state and no I/O
//
// The literal 1000 below is WO-05 C3's number, NOT Naming.MaxAttempts. If the
// constant and the work order ever disagree, these checks must stay on the
// work order and go red.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using SnipVid.Logic;

namespace SnipVid.Tests
{
    /// <summary>Independent acceptance checks for WO-05 criteria C1..C5.</summary>
    public static class NamingAcceptanceTests
    {
        /// <summary>AC7's pattern, anchored: AC7 says "matches", not "contains".</summary>
        private const string Ac7Pattern = @"^SnipVid_\d{4}-\d\d-\d\d_\d{6}\.mp4$";

        /// <summary>WO-05 C1's timestamp and the name DESIGN.md section 7 implies.</summary>
        private const string SpecTimeName = "SnipVid_2026-11-15_143002.mp4";

        /// <summary>WO-05 C3's bound, quoted from the work order rather than the code.</summary>
        private const int SpecAttemptLimit = 1000;

        /// <summary>WO-05 C1 / AC7: one timestamp, one exact name, one regex.</summary>
        public static void TestC1_ExactNameAndAc7Regex()
        {
            string name = Naming.FileName(Timestamp(2026, 11, 15, 14, 30, 2));

            Harness.Check("AC-C1 2026-11-15 14:30:02 is exactly " + SpecTimeName + " (DESIGN 7, WO-05 C1)",
                name == SpecTimeName, "got '" + name + "'");
            Harness.Check("AC-C1 the name matches AC7's anchored pattern " + Ac7Pattern,
                Regex.IsMatch(name, Ac7Pattern), "got '" + name + "'");
            Harness.Check("AC-C1 the name is a bare file name (no separator, no folder)",
                HasNoSeparator(name), "got '" + name + "'");
        }

        /// <summary>WO-05 C1/C5: every field is fixed-width, so the name sorts chronologically.</summary>
        public static void TestC1b_EveryFieldIsZeroPadded()
        {
            string name = Naming.FileName(Timestamp(2026, 1, 2, 3, 4, 5));

            Harness.Check("AC-C1b 2026-01-02 03:04:05 keeps six digits in every field",
                name == "SnipVid_2026-01-02_030405.mp4", "got '" + name + "'");
            Harness.Check("AC-C1b the padded name still matches AC7", Regex.IsMatch(name, Ac7Pattern),
                "got '" + name + "'");
        }

        /// <summary>WO-05 C5: midnight is 000000, not a blank or a dropped field.</summary>
        public static void TestC5_MidnightIsZeroPadded()
        {
            string name = Naming.FileName(Timestamp(2026, 1, 2, 0, 0, 0));

            Harness.Check("AC-C5 2026-01-02 00:00:00 is SnipVid_2026-01-02_000000.mp4 (WO-05 C5)",
                name == "SnipVid_2026-01-02_000000.mp4", "got '" + name + "'");
        }

        /// <summary>WO-05 C2: the suffix goes before the extension and starts at 2.</summary>
        public static void TestC2_CollisionSuffixGoesBeforeTheExtension()
        {
            string baseName = Naming.FileName(Timestamp(2026, 11, 15, 14, 30, 2));
            HashSet<string> taken = new HashSet<string>(StringComparer.Ordinal);
            taken.Add(baseName);

            string second = Naming.UniqueFileName(Timestamp(2026, 11, 15, 14, 30, 2), taken.Contains);

            Harness.Check("AC-C2 one collision yields exactly SnipVid_2026-11-15_143002_2.mp4 (WO-05 C2)",
                second == "SnipVid_2026-11-15_143002_2.mp4", "got '" + second + "'");
            Harness.Check("AC-C2 the suffix sits before '.mp4', not after it",
                second.EndsWith("_2.mp4", StringComparison.Ordinal)
                && second.IndexOf(".mp4_", StringComparison.Ordinal) < 0,
                "got '" + second + "'");
            Harness.Check("AC-C2 the suffixed name still ends in an MP4 extension, exactly once",
                CountOccurrences(second, ".mp4") == 1, "got '" + second + "'");

            taken.Add(second);
            string third = Naming.UniqueFileName(Timestamp(2026, 11, 15, 14, 30, 2), taken.Contains);
            Harness.Check("AC-C2 two collisions yield exactly SnipVid_2026-11-15_143002_3.mp4 (WO-05 C2)",
                third == "SnipVid_2026-11-15_143002_3.mp4", "got '" + third + "'");
        }

        /// <summary>WO-05 C2: numbering is contiguous; a gap would be an unexplained hole.</summary>
        public static void TestC2b_NumberingIsContiguousAndMonotone()
        {
            HashSet<string> taken = new HashSet<string>(StringComparer.Ordinal);
            string baseName = "SnipVid_2026-11-15_143002.mp4";
            string stem = "SnipVid_2026-11-15_143002";
            taken.Add(baseName);
            taken.Add(stem + "_2.mp4");
            taken.Add(stem + "_3.mp4");
            taken.Add(stem + "_4.mp4");

            string next = Naming.UniqueFileName(Timestamp(2026, 11, 15, 14, 30, 2), taken.Contains);

            Harness.Check("AC-C2b probe order base, _2, _3, _4 gives exactly _5 (no skipped number)",
                next == stem + "_5.mp4", "got '" + next + "'");
        }

        /// <summary>WO-05 C3: the resolver is bounded and reports the failure, never spins.</summary>
        public static void TestC3_AlwaysTakenThrowsWithBoundedWork()
        {
            int calls = 0;
            Exception caught = null;
            try
            {
                Naming.UniqueFileName(Timestamp(2026, 11, 15, 14, 30, 2),
                    delegate(string candidate) { calls++; return true; });
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            Harness.Check("AC-C3 every candidate taken throws instead of looping forever (WO-05 C3)",
                caught != null, "returned a name after " + calls + " probes");
            Harness.Check("AC-C3 the throw happens within " + SpecAttemptLimit + " probes",
                calls <= SpecAttemptLimit, "probed " + calls + " times");
            Harness.Check("AC-C3 the exception carries a readable message",
                caught != null && !string.IsNullOrEmpty(caught.Message),
                caught == null ? "<no exception>" : "<empty message>");
        }

        /// <summary>
        /// WO-05 C3 boundary: the limit is "1000 collisions throw", so 999 taken must
        /// still resolve to the 1000th candidate. A cap of 100 passes the throw check
        /// but fails this one - that is why both exist.
        /// </summary>
        public static void TestC3b_NineHundredNinetyNineTakenStillResolves()
        {
            int calls = 0;
            List<string> probed = new List<string>();
            string name = Naming.UniqueFileName(Timestamp(2026, 11, 15, 14, 30, 2),
                delegate(string candidate)
                {
                    calls++;
                    probed.Add(candidate);
                    return calls <= 999;
                });

            Harness.Check("AC-C3b 999 collisions resolve to SnipVid_2026-11-15_143002_1000.mp4 (WO-05 C3)",
                name == "SnipVid_2026-11-15_143002_1000.mp4", "got '" + name + "'");
            Harness.Check("AC-C3b resolving it took exactly 1000 probes",
                calls == 1000, "probed " + calls + " times");
            Harness.Check("AC-C3b no candidate name is probed twice",
                CountDistinct(probed) == probed.Count, "probed " + probed.Count + " times, "
                + CountDistinct(probed) + " distinct");
        }

        /// <summary>DESIGN 7: "collision-safe" means never hand back a name already in use.</summary>
        public static void TestOverwrite_NeverReturnsANameThePredicateCalledTaken()
        {
            string baseName = "SnipVid_2026-11-15_143002.mp4";
            string stem = "SnipVid_2026-11-15_143002";
            HashSet<string> taken = new HashSet<string>(StringComparer.Ordinal);
            taken.Add(baseName);
            for (int i = 2; i <= 7; i++)
            {
                taken.Add(stem + "_" + i.ToString(CultureInfo.InvariantCulture) + ".mp4");
            }

            string first = Naming.UniqueFileName(Timestamp(2026, 11, 15, 14, 30, 2), taken.Contains);
            Harness.Check("AC-OW the resolver never returns a name the predicate reported as taken",
                !taken.Contains(first), "returned taken name '" + first + "'");
            Harness.Check("AC-OW with base.._7 taken the answer is _8",
                first == stem + "_8.mp4", "got '" + first + "'");

            taken.Add(first);
            string second = Naming.UniqueFileName(Timestamp(2026, 11, 15, 14, 30, 2), taken.Contains);
            Harness.Check("AC-OW a second call for the same timestamp picks a different, free name",
                second != first && !taken.Contains(second), "first '" + first + "', second '" + second + "'");
        }

        /// <summary>DESIGN 7 at the cap boundary: the last candidate must be free, or it throws.</summary>
        public static void TestOverwrite_CapBoundaryNeverReturnsATakenName()
        {
            string stem = "SnipVid_2026-11-15_143002";
            HashSet<string> taken = new HashSet<string>(StringComparer.Ordinal);
            taken.Add(stem + ".mp4");
            for (int i = 2; i <= 999; i++)
            {
                taken.Add(stem + "_" + i.ToString(CultureInfo.InvariantCulture) + ".mp4");
            }

            string name = Naming.UniqueFileName(Timestamp(2026, 11, 15, 14, 30, 2), taken.Contains);

            Harness.Check("AC-OW at the cap boundary the returned name was reported free",
                !taken.Contains(name), "returned taken name '" + name + "'");
            Harness.Check("AC-OW at the cap boundary the answer is _1000",
                name == stem + "_1000.mp4", "got '" + name + "'");
        }

        /// <summary>DESIGN 11: the predicate sees names, not paths; the result is a bare name.</summary>
        public static void TestPredicate_ReceivesBareNamesOnly()
        {
            List<string> probed = new List<string>();
            string result = Naming.UniqueFileName(Timestamp(2026, 11, 15, 14, 30, 2),
                delegate(string candidate)
                {
                    probed.Add(candidate);
                    return probed.Count <= 2;
                });

            bool allBare = true;
            foreach (string candidate in probed)
            {
                if (!HasNoSeparator(candidate) || candidate.IndexOf(':') >= 0)
                {
                    allBare = false;
                }
            }

            Harness.Check("AC-BARE every predicate argument is a bare name (no '\\', '/', ':')",
                allBare, "probed: " + string.Join(", ", probed.ToArray()));
            Harness.Check("AC-BARE the returned value is a bare name",
                HasNoSeparator(result) && result.IndexOf(':') < 0, "got '" + result + "'");
            Harness.Check("AC-BARE the predicate is consulted once per candidate, base first",
                probed.Count == 3 && probed[0] == "SnipVid_2026-11-15_143002.mp4",
                "probed " + probed.Count + " times, first '" + (probed.Count == 0 ? "<none>" : probed[0]) + "'");
        }

        /// <summary>WO-05 C4: the calendar of the current culture must not leak into the name.</summary>
        public static void TestC4_NonGregorianCulturesProduceTheSameName()
        {
            DateTime stamp = Timestamp(2026, 11, 15, 14, 30, 2);
            string[] cultureNames = { "ar-SA", "th-TH", "tr-TR", "he-IL", "fa-IR" };
            List<string> nonGregorian = new List<string>();

            CultureInfo saved = CultureInfo.CurrentCulture;
            CultureInfo savedUi = CultureInfo.CurrentUICulture;
            try
            {
                foreach (string cultureName in cultureNames)
                {
                    CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);
                    if (culture.DateTimeFormat.Calendar.GetType() != typeof(GregorianCalendar))
                    {
                        nonGregorian.Add(cultureName + "=" + culture.DateTimeFormat.Calendar.GetType().Name);
                    }

                    string actual;
                    CultureInfo.CurrentCulture = culture;
                    CultureInfo.CurrentUICulture = culture;
                    try
                    {
                        actual = Naming.FileName(stamp);
                    }
                    finally
                    {
                        CultureInfo.CurrentCulture = saved;
                        CultureInfo.CurrentUICulture = savedUi;
                    }

                    Harness.Check("AC-C4 culture " + cultureName + " (" + culture.DateTimeFormat.Calendar.GetType().Name
                        + ") still yields " + SpecTimeName,
                        actual == SpecTimeName, "got '" + actual + "'");
                }

                // WO-05 C2 under a non-Gregorian culture: the suffix number is invariant too.
                CultureInfo sa = CultureInfo.GetCultureInfo("ar-SA");
                HashSet<string> taken = new HashSet<string>(StringComparer.Ordinal);
                taken.Add(SpecTimeName);
                string suffixed;
                CultureInfo.CurrentCulture = sa;
                CultureInfo.CurrentUICulture = sa;
                try
                {
                    suffixed = Naming.UniqueFileName(stamp, taken.Contains);
                }
                finally
                {
                    CultureInfo.CurrentCulture = saved;
                    CultureInfo.CurrentUICulture = savedUi;
                }

                Harness.Check("AC-C4 ar-SA collision suffix is ASCII _2, identical to invariant output",
                    suffixed == "SnipVid_2026-11-15_143002_2.mp4", "got '" + suffixed + "'");
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
                CultureInfo.CurrentUICulture = savedUi;
            }

            // Test validity: if every culture above were Gregorian, the checks would be vacuous.
            Harness.Check("AC-C4 at least three of the five cultures use a non-Gregorian calendar, so the check can fail",
                nonGregorian.Count >= 3, "non-Gregorian: " + string.Join(", ", nonGregorian.ToArray()));
        }

        /// <summary>
        /// Calendar edges derived from DESIGN 7's format string: leap day, last second
        /// of a year, first second of a year. These are the values a hand-written
        /// formatter or an off-by-one day arithmetic breaks on.
        /// </summary>
        public static void TestCalendarEdges()
        {
            Harness.Check("AC-EDGE leap day 2028-02-29 00:00:00 is SnipVid_2028-02-29_000000.mp4",
                Naming.FileName(Timestamp(2028, 2, 29, 0, 0, 0)) == "SnipVid_2028-02-29_000000.mp4",
                "got '" + Naming.FileName(Timestamp(2028, 2, 29, 0, 0, 0)) + "'");
            Harness.Check("AC-EDGE 2026-12-31 23:59:59 is SnipVid_2026-12-31_235959.mp4",
                Naming.FileName(Timestamp(2026, 12, 31, 23, 59, 59)) == "SnipVid_2026-12-31_235959.mp4",
                "got '" + Naming.FileName(Timestamp(2026, 12, 31, 23, 59, 59)) + "'");
            Harness.Check("AC-EDGE 2026-01-01 00:00:00 is SnipVid_2026-01-01_000000.mp4",
                Naming.FileName(Timestamp(2026, 1, 1, 0, 0, 0)) == "SnipVid_2026-01-01_000000.mp4",
                "got '" + Naming.FileName(Timestamp(2026, 1, 1, 0, 0, 0)) + "'");
        }

        /// <summary>
        /// DESIGN 11 / WO common contract: time arrives as a parameter, so the
        /// DateTimeKind is data about the caller, not an instruction to convert. The
        /// same ticks must give the same name whether labelled Utc or Local.
        /// </summary>
        public static void TestDateTimeKindDoesNotShiftTheName()
        {
            DateTime unspecified = Timestamp(2026, 11, 15, 14, 30, 2);
            DateTime utc = DateTime.SpecifyKind(unspecified, DateTimeKind.Utc);
            DateTime local = DateTime.SpecifyKind(unspecified, DateTimeKind.Local);

            string fromUtc = Naming.FileName(utc);
            string fromLocal = Naming.FileName(local);

            Harness.Check("AC-KIND the same ticks labelled Utc give " + SpecTimeName,
                fromUtc == SpecTimeName, "got '" + fromUtc + "'");
            Harness.Check("AC-KIND the same ticks labelled Local give " + SpecTimeName,
                fromLocal == SpecTimeName, "got '" + fromLocal + "'");
            Harness.Check("AC-KIND Utc and Local for the same ticks give byte-identical names",
                string.Equals(fromUtc, fromLocal, StringComparison.Ordinal),
                "'" + fromUtc + "' vs '" + fromLocal + "'");
        }

        /// <summary>
        /// Contract check, not a WO-05 criterion: Naming's documented failure mode is
        /// ArgumentNullException for a null predicate, because treating null as
        /// "nothing is taken" would silently allow the overwrite DESIGN.md section 7
        /// forbids. Removing the guard must turn this red (it becomes a
        /// NullReferenceException), so the check is falsifiable.
        /// </summary>
        public static void TestContract_NullPredicateIsRejectedNotTreatedAsFree()
        {
            Exception caught = null;
            string returned = null;
            try
            {
                returned = Naming.UniqueFileName(Timestamp(2026, 11, 15, 14, 30, 2), null);
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            Harness.Check("AC-NULL a null predicate throws ArgumentNullException instead of meaning 'free'",
                caught is ArgumentNullException,
                caught == null
                    ? "returned '" + returned + "'"
                    : "got " + caught.GetType().Name + ": " + caught.Message);
        }

        /// <summary>Builds an Unspecified-kind timestamp; avoids DateTimeKind arithmetic.</summary>
        private static DateTime Timestamp(int year, int month, int day, int hour, int minute, int second)
        {
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        }

        /// <summary>True when the string has no path separators at all.</summary>
        private static bool HasNoSeparator(string value)
        {
            return value != null
                && value.IndexOf('\\') < 0
                && value.IndexOf('/') < 0;
        }

        /// <summary>Counts non-overlapping occurrences of a literal.</summary>
        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            int at = 0;
            while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += needle.Length;
            }
            return count;
        }

        /// <summary>Counts distinct strings, ordinal comparison.</summary>
        private static int CountDistinct(List<string> values)
        {
            return new HashSet<string>(values, StringComparer.Ordinal).Count;
        }
    }
}
