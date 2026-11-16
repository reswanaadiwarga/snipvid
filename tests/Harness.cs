// Harness - SnipVid's test framework, because none is installable here.
//
// A check is real only if it can fail: Check() prints PASS/FAIL and the runner
// returns non-zero when anything failed, which is what test.ps1 propagates.
// Tests are discovered by reflection - public static void Test*() in a class
// whose name ends with "Tests" - so a new tests\*.cs file needs no registration
// list. With no test files the runner prints one SUMMARY line and returns 0.
//
// Compile boundary: test.ps1 compiles src\Logic\*.cs + tests\*.cs only and
// passes no WPF/WinForms reference, so a test cannot reach a window, a monitor,
// the clock, the filesystem or a process (DESIGN.md section 11). Keep it that
// way: the oracle must come from the spec, never from the code under test.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace SnipVid.Tests
{
    /// <summary>
    /// Assertion sink and reflection runner for pure-logic tests.
    /// </summary>
    public static class Harness
    {
        /// <summary>Number of assertions executed so far.</summary>
        public static int Total { get; private set; }

        /// <summary>Number of assertions that passed.</summary>
        public static int Passed { get; private set; }

        /// <summary>Number of assertions that failed.</summary>
        public static int Failed { get; private set; }

        /// <summary>
        /// Records one assertion and prints its verdict.
        /// </summary>
        /// <param name="name">Unique, human-readable assertion name; shown verbatim.</param>
        /// <param name="ok">True when the observed behaviour matched the spec.</param>
        /// <param name="detail">
        /// Expected vs actual, printed only on failure. Pass null or empty when
        /// there is nothing useful to add.
        /// </param>
        /// <remarks>
        /// Side effects: writes one stdout line and increments the counters.
        /// Never throws, so a test can assert several independent facts before
        /// the runner moves on.
        /// </remarks>
        public static void Check(string name, bool ok, string detail = "")
        {
            Total++;
            if (ok)
            {
                Passed++;
                Console.WriteLine("PASS " + name);
            }
            else
            {
                Failed++;
                // Written as an escape so the source stays ASCII regardless of
                // the compiler's /codepage default; prints the em dash in the
                // FAIL line as specified by WO-01.
                Console.WriteLine("FAIL " + name + " \u2014 " + (detail ?? string.Empty));
            }
        }

        /// <summary>
        /// Runs every discovered test method and prints the summary line.
        /// </summary>
        /// <returns>0 when no assertion failed, 1 otherwise.</returns>
        /// <remarks>
        /// A test method that throws counts as one extra failed check, so a crash
        /// cannot be mistaken for a pass. Discovery order is stable (type name,
        /// then method name) to keep output diffable.
        /// </remarks>
        public static int RunAll()
        {
            List<MethodInfo> methods = Discover();
            foreach (MethodInfo method in methods)
            {
                string label = method.DeclaringType.Name + "." + method.Name;
                try
                {
                    method.Invoke(null, null);
                }
                catch (TargetInvocationException ex)
                {
                    Exception inner = ex.InnerException ?? ex;
                    Check(label + " (threw)", false, inner.GetType().Name + ": " + inner.Message);
                }
            }

            Console.WriteLine(
                "SUMMARY {0} checks, {1} passed, {2} failed ({3} test methods)",
                Total, Passed, Failed, methods.Count);
            return Failed == 0 ? 0 : 1;
        }

        /// <summary>
        /// Console-test entry point: runs the suite and returns its exit code.
        /// </summary>
        /// <returns>0 when every check passed, 1 otherwise.</returns>
        /// <remarks>Side effects: stdout output only; the process exits with this value.</remarks>
        public static int Main()
        {
            return RunAll();
        }

        /// <summary>
        /// Finds parameterless public static void methods whose name starts with
        /// "Test" in classes whose name ends with "Tests".
        /// </summary>
        /// <returns>Deterministically ordered methods; empty when no test files exist.</returns>
        /// <remarks>
        /// Side effects: none. Reflection over the executing assembly only, so a
        /// method in the harness itself can never be mistaken for a test.
        /// </remarks>
        private static List<MethodInfo> Discover()
        {
            const BindingFlags flags =
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;

            return typeof(Harness).Assembly.GetTypes()
                .Where(t => t.IsClass && t.Name.EndsWith("Tests", StringComparison.Ordinal))
                .SelectMany(t => t.GetMethods(flags))
                .Where(m => m.Name.StartsWith("Test", StringComparison.Ordinal)
                            && m.ReturnType == typeof(void)
                            && m.GetParameters().Length == 0
                            && !m.ContainsGenericParameters)
                .OrderBy(m => m.DeclaringType.FullName, StringComparer.Ordinal)
                .ThenBy(m => m.Name, StringComparer.Ordinal)
                .ToList();
        }
    }
}
