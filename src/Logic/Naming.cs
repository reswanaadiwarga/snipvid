// Naming - the one place the output file name is built (DESIGN.md section 7).
//
// The name carries two promises to the rest of the app and to the acceptance
// regex in PLAN.md AC7: "SnipVid_\d{4}-\d\d-\d\d_\d{6}\.mp4" always matches, and
// a second capture in the same second never overwrites the first.
//
// WHY the timestamp and the "already exists" predicate are parameters rather
// than read here: a module that calls DateTime.Now and File.Exists is testable
// only against a real clock and a real folder, and src\Logic must stay free of
// both (DESIGN.md section 11). With them injected this type is a total function
// of its arguments, and the caller in src\App supplies the clock and the folder -
// the two things it already knows.
//
// WHY the culture is pinned: DateTime.ToString("yyyy-MM-dd") uses the current
// culture's calendar, so on a machine set to ar-SA (Umm al-Qura) the same instant
// renders its year as 1448 and on th-TH (Buddhist) as 2569 - measured, not
// theorised. The name would stop matching AC7's regex - silently, only for some
// users. Formatting with InvariantCulture makes the name identical everywhere,
// and the suffix number is formatted the same way for the same reason.
//
// No I/O, no clock, no Environment, no CultureInfo.CurrentCulture: test.ps1
// compiles this file without any Windows reference (DESIGN.md section 11).
using System;
using System.Globalization;

namespace SnipVid.Logic
{
    /// <summary>
    /// Builds the output file name: <c>SnipVid_yyyy-MM-dd_HHmmss.mp4</c>, with a
    /// <c>_2</c>, <c>_3</c>... suffix when that name is taken.
    /// </summary>
    /// <remarks>
    /// All decisions are parameters. Nothing here touches the filesystem, so a
    /// name can be produced for a folder the process cannot write to, and nothing
    /// here reads the clock, so the same call always returns the same string.
    /// </remarks>
    public static class Naming
    {
        /// <summary>
        /// How many candidates a single <see cref="UniqueFileName"/> call may probe
        /// before giving up (WO-05 C3).
        /// </summary>
        /// <remarks>
        /// A bound exists because "the folder is unwritable and every candidate
        /// reports as taken" must end in an error the user can act on, not in a
        /// spinner. 1000 is far beyond any plausible pile of same-second captures
        /// (one recording per second for over sixteen minutes) while still
        /// finishing instantly, so hitting it means the predicate is wrong or the
        /// folder is unusable - and the exception says exactly that.
        /// </remarks>
        public const int MaxAttempts = 1000;

        /// <summary>Date and time part of the name, in the order that sorts chronologically.</summary>
        private const string TimeFormat = "yyyy-MM-dd_HHmmss";

        /// <summary>Fixed prefix, so recordings are easy to find and to tell apart.</summary>
        private const string Prefix = "SnipVid_";

        /// <summary>Container extension; the recorder always writes MP4.</summary>
        private const string Extension = ".mp4";

        /// <summary>
        /// Builds the unsuffixed file name for a timestamp.
        /// </summary>
        /// <param name="timestamp">
        /// Capture start time, supplied by the caller. The kind is ignored: the
        /// value is formatted as given, never converted to another zone.
        /// </param>
        /// <returns>The name only - no folder, no path separators.</returns>
        /// <remarks>Side effects: none. Cannot fail for any <see cref="DateTime"/> value.</remarks>
        public static string FileName(DateTime timestamp)
        {
            return Prefix + timestamp.ToString(TimeFormat, CultureInfo.InvariantCulture) + Extension;
        }

        /// <summary>
        /// Builds the name to use for a timestamp, appending <c>_2</c>, <c>_3</c>...
        /// while the supplied predicate says the candidate is taken.
        /// </summary>
        /// <param name="timestamp">Capture start time, as for <see cref="FileName"/>.</param>
        /// <param name="isTaken">
        /// Reports whether a candidate name already exists. Called at most
        /// <see cref="MaxAttempts"/> times, with a bare file name (never a path),
        /// in this order: the unsuffixed name first, then the numbered ones.
        /// </param>
        /// <returns>The first candidate the predicate reported as free.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="isTaken"/> is null: there is no default predicate, and
        /// treating null as "nothing exists" would silently allow an overwrite.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// All <see cref="MaxAttempts"/> candidates were taken. The message names
        /// the limit and the base name; the caller turns it into a user-visible
        /// failure rather than starting a capture that would overwrite.
        /// </exception>
        /// <remarks>
        /// Side effects: none beyond the calls to <paramref name="isTaken"/>. The
        /// bound is a count of attempts, not a duration, so behaviour is identical
        /// on a slow machine and a fast one.
        /// </remarks>
        public static string UniqueFileName(DateTime timestamp, Func<string, bool> isTaken)
        {
            if (isTaken == null)
            {
                throw new ArgumentNullException("isTaken");
            }

            string baseName = FileName(timestamp);

            // The suffix goes before the extension, so the file is still an MP4
            // to Explorer, ffmpeg and the AC7 regex - "_2" after ".mp4" would
            // produce an unrecognised file type and break the regex.
            string stem = baseName.Substring(0, baseName.Length - Extension.Length);

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                string candidate = attempt == 1
                    ? baseName
                    : stem + "_" + attempt.ToString(CultureInfo.InvariantCulture) + Extension;

                if (!isTaken(candidate))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException(
                "could not find a free output file name after "
                + MaxAttempts.ToString(CultureInfo.InvariantCulture)
                + " attempts: '" + baseName + "' and every numbered alternative are taken."
                + " Move or delete some files in the output folder.");
        }
    }
}
