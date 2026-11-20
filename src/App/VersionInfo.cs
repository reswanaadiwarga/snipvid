// Assembly identity for the 1.0.1 release.
//
// The release assembly identity: the numbers build.ps1 stamps into the exe (and
// therefore into Explorer's Details tab and the tray tooltip). Kept in one file
// so a version bump is a single edit, not a sweep through source.
//
// ASCII-only: csc falls back to the ANSI codepage for non-UTF-8 sources.
using System.Reflection;

[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]
[assembly: AssemblyProduct("SnipVid")]
[assembly: AssemblyTitle("SnipVid")]
[assembly: AssemblyDescription("Windows screen region recorder at 60 fps CFR")]
