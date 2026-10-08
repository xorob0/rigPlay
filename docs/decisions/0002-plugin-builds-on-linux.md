# ADR 0002: The plugin is code-only WPF and builds on Linux

**Date:** 2026-10-03
**Status:** Accepted

## Context

SimHub plugins are .NET Framework 4.8 class libraries, and their settings pages are WPF. The usual
way to build one is Visual Studio or MSBuild on Windows, with the panel in XAML. Development here
happens on Linux; the only Windows machine is a small VM that runs SimHub for manual testing. XAML
compilation needs the WPF build targets, which exist only on Windows. A Windows CI runner would work
but is slow and makes every local build depend on the VM.

The same problem was solved in OpenDash (its ADR 0005), and the recipe is known to work with SimHub.

## Decision

`plugin/RigPlay` targets `net48` through the `Microsoft.NETFramework.ReferenceAssemblies` package and
references the WPF assemblies as plain reference assemblies, without `UseWPF`. The settings page is
built in C# code; SimHub's styles are looked up at runtime and applied when present. The SimHub
assemblies the plugin compiles against are committed under `plugin/lib` and referenced with
`Private=false`, so they are not copied next to the plugin and SimHub's own copies are used at run
time.

`plugin/RigPlay.Tests` is a `net8.0` xunit project. It does not reference the plugin assembly; it
compiles the plugin's pure-logic source files directly (protocol messages, codecs, pairing, settings
normalisation). Code that touches SimHub or WPF types stays out of those files.

## Consequences

`dotnet build` and `dotnet test` work on Linux, macOS and Windows, and CI needs no Windows runner. The
settings page and anything that touches SimHub at run time are verified by hand on the VM. Upgrading
SimHub means replacing the DLLs in `plugin/lib` and checking that the plugin still loads. Converting
the page to XAML would bring back the Windows-only build, so it is not planned.
