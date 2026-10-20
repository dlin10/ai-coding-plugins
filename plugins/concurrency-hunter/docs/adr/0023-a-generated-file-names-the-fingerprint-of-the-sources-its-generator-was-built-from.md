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
version. A repository regenerates the members its runs reach after each upgrade of the plugin, inside
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
