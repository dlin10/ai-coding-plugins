# A generated file names the fingerprint of the sources its generator was built from

TD-034c binds a generated model to the version of the generator, and a model an older generator wrote
is generated again. ADR 0022 settled the built-in CoreLib models, which the owner regenerates from a
recorded commit, and left the project's generated layer to TD-034c without naming the version. Until
now a generated file named only the assembly it describes: a model or a **Generation refusal** an
older generator wrote stayed until its file was removed. A change of the generator or of the engine it
reads then moved nothing a run could see: the run read the old answer and generated nothing, and the
demo's committed files hid the change from the tests.

The decision: the **Generator version** is a fingerprint of the sources the plugin's engine and
generator are built from, computed when the plugin is built, and a generated file names it in its
header.

- The fingerprint covers the sources and embedded resources of `ConcurrencyHunter.Analysis` and
  `ConcurrencyHunter.Core` and the build inputs that decide how they compile (package versions and the
  shared build properties), with line endings normalised, so a Debug and a Release build of one tree,
  or a checkout with CRLF and one with LF, have one version.
- The file's key is checked before its version: a file with a wrong key is unreadable at any version and is never rewritten.
- A file whose header names another version, or none, holds no answers for the run: the run generates
  what it reaches, and its first write replaces the file whole. Its entries are answers of another
  generator, which no run applies, so nothing that a run could use is dropped. A run that only reads
  the layer names the file in a diagnostic and its targets `not-generated`.
- Within one version a file still only grows: a run adds and replaces entries and never deletes one.

The consequences to hold. Every change to the engine or the generator, a comment included, is a new
version. Amended below (2026-11-04): an edit of comments or whitespace alone in a C# source is not. A repository regenerates the members its runs reach after each upgrade of the plugin, inside
the run's **Generation budget**. A change in this repository that touches the engine leaves the demo's
committed generated files stale, the demo test that forbids generation names the members, and
`build/record-demo-models.ps1` writes them again; the rewrite shows, line by line, which answers the
change moved. A build that does not know the generator version reads the new file as unreadable and leaves it untouched.
Two builds with different fingerprints replace each other's files.

Rejected: the plugin's release version. It does not change between releases, so the runs that change
the generator would not see their own changes in the demo. Rejected: a number bumped by hand, for the
reason ADR 0022 gives: a change far from the generator's code moves models as surely as one inside it,
and nothing would bump the number. Rejected: the version on each entry rather than on the file. It
keeps entries no run applies, and a rewrite of the demo would change every entry's version and bury the
answers that moved. Rejected: the MVID of the built assemblies. A Debug and a Release build differ, so a
file recorded by one build would be stale for the other.

## Amendment, 2026-11-04: a C# source counts by its tokens

The fingerprint read every input as bytes, so an edit of a comment alone was a new version. Every
generated file went stale, and `build/record-demo-models.ps1` recorded all 605 demo entries again (six
runs and about 91 minutes after run L1) for a change no answer can see. This repository keeps the traps
of its code in XML documentation comments, so such edits are frequent.

The decision: a C# source of `ConcurrencyHunter.Analysis` and `ConcurrencyHunter.Core` counts by its
syntax tokens. Its comments, documentation comments included, and its whitespace are left out. What
else the compiler reads stays in: the directives (`#if`, `#define`, `#nullable`, `#pragma`, `#line`
and the rest) with their own tokens, the text of a disabled `#if` branch, the message of `#region`,
`#error` and `#warning`, skipped tokens and conflict markers. The project files, the shared build
properties and the built-in models still count by their bytes, comments included.

- No build step, generator or test that writes models reads the text of a comment. The generator reads
  the documentation comment ids of library members, never the engine's comments, and a refusal records
  an exception's type and message, never its stack, so a line moved by a comment reaches no answer. A
  documentation comment that changed what the generator answers would have to stay counted; there is none.
- No preprocessor symbol is defined when the fingerprint parses a source, so the code under `#if` counts
  either as tokens or as disabled text, and always counts. A disabled branch is text to the parser, so a
  comment inside one still moves the version.
- Each token counts with its length, so a whitespace edit that joins two tokens into one is a new version.
- The parse is the cost. The task that computes the fingerprint took about 45 ms by bytes and takes about
  350 ms with the sources parsed in parallel; a Release build of Core with nothing to compile goes from
  about 1.5 s to about 2 s.
- The build's inline task compiles against the netstandard2.0 Roslyn of the package Core restores
  (`Microsoft.CodeAnalysis.CSharp.Workspaces` and the `Microsoft.CodeAnalysis` packages it pins). The
  Roslyn it runs on depends on the MSBuild. Under `dotnet build`, MSBuild on .NET binds the reference to
  the SDK's own Roslyn. Under Visual Studio's MSBuild on .NET Framework, the task factory loads the
  package's netstandard2.0 Roslyn itself; Visual Studio's own, newer Roslyn is not used. A Debug and a
  Release build from either MSBuild give the same fingerprint.
- A bump of that package past the SDK's Roslyn leaves the Visual Studio build as it is and breaks
  `dotnet build`. The task asks for a newer Roslyn than the SDK holds, and the build stops at the task
  without writing a fingerprint; the fix is an SDK whose Roslyn is no older than the package. An MSBuild
  of an SDK before .NET 10 stops at the task too, because the package's Roslyn needs .NET 10's libraries.
  Two Roslyn versions that split a source into different tokens would give two fingerprints, and
  `GeneratorVersionTests`, which compute with the package's Roslyn, would report the difference.

The consequences to hold, in place of the first sentence of the consequences above. Every change to
what the engine or the generator compiles is a new version; an edit of comments or whitespace alone in
a C# source is not, and does not show in the demo's generated files at all. That is the intent: those
files show the answers a change moved, and such an edit moves none. The change of the rule itself
changes every fingerprint once.

Rejected: keeping the documentation comments counted. Nothing that writes a model reads them, and they
are the comments edited most.
