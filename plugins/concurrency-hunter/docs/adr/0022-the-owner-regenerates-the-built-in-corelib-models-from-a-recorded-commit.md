# The owner regenerates the built-in CoreLib models from a recorded commit

TD-034c binds a generated model to the version of the generator, and an older version is generated
again. For the models a run generates under `.concurrency-hunter/models/generated/` that is cheap: the
run regenerates the members it reaches. For the built-in models of `System.Private.CoreLib` it is not. A
full generation of CoreLib takes about two hours, the models ship inside the exe, and no version number
says when they are stale. If the version were the plugin's build, every change of the engine would
regenerate CoreLib, including the many that move no model. If it were a number bumped by hand, it would
be bumped too late or not at all, because the generator reads the engine's accesses, the heap and the
struct storage. A change far from the generator's code moves models as surely as one inside it.

The decision: the built-in CoreLib models and the generation snapshot
`skills/hunt/evals/models/corelib-generation.tsv` name the commit of the repository the generator was
built from, and the plugin's owner decides when to generate them again. The run does not check that
commit against anything.

- `build/generate-corelib.ps1` writes the commit into the snapshot's header (`# commit <sha>`). `-Record`
  refuses a working tree with uncommitted changes to what the generator is built from, and a resumed
  output folder started on another commit, so the recorded commit names the code that ran.
- `build/generate-corelib.ps1 -Since` lists the commits and files changed since the recorded commit in
  three groups: the generator (its folder in Core and the generation tool), the engine (the rest of
  Analysis and Core), and the build inputs (package versions, SDK). The groups show where to look, not
  whether a model moved; a run over the members a change touches (`-Members`) and the comparison with the
  snapshot answer that.
- The comparison of two snapshots leaves the commit out, as it leaves out nothing else the generator
  answered: two runs on different commits that answer the same are the same snapshot.
- The project's generated layer keeps the version check of TD-034c: there the run is the one that
  regenerates, and nobody decides for it.

The consequences to hold. Between a change and the owner's regeneration, the built-in CoreLib models can
describe an older engine. Such a model is still a model of the right implementation (TD-034c binds it to
the assembly's version and MVID), and what the recorded commit buys is that the gap is visible: `-Since`
names every commit inside it.

Rejected: the plugin's build as the generator version. Every engine change would regenerate CoreLib, two
hours each, mostly to no effect. Rejected: a version number bumped by hand when the generator changes. The
changes that move models without touching the generator's code would not bump it, and nothing would show
that they happened.

## Amendment, 2026-10-17: what ships is the generation of the members programs call

The amendment of ADR 0012 of the same day makes the built-in CoreLib models those of a census: the members
the demo and the corpora call. The recorded commit names both the file built from that list and the
snapshot of every public member. The owner regenerates the file with `-Members` over the list; the
generation of every member, about two hours, shows what a change of the generator or the engine moved and
gates nothing that ships.

## Amendment, 2026-11-07: the owner regenerates the shared frameworks' census, and a release refuses a stale one

The amendment of ADR 0012 of the same day ships built-in models for the census of `Microsoft.NETCore.App` and
`Microsoft.AspNetCore.App` 10.0. Like CoreLib's models, they are the owner's to regenerate, from a recorded commit,
and the run never checks that commit. The ways they differ:

- **Runs find the census.** `build/generate-frameworks.ps1` runs `metrics --generation on --census` on the demo and
  on eShop.
  - The census budget is a run limit of its own, twelve hours of generation summed over members, with the recording's
    four workers (ADR 0033) and a member limit of five minutes, the CoreLib tool's. The recording's two hours are not
    enough: with the census members' entries left out, the demo alone spent about 8,700 s, and its second scope was cut.
  - For a member of the two frameworks' 10.0 assemblies that it can generate, the census run reads no built-in entry,
    so every census member is generated, an owner-written one included.
  - It takes a scratch folder for its repository root, so it writes its generated files and its model lock there,
    never in the corpus's repository, and reads no project model of the corpus: a corpus's project entry, such as the
    demo's `opaque` entry over `Options.Create`, would otherwise keep a framework member from being generated.
  - The census is every member of those assemblies that the runs answered: the targets and the dependencies their
    generation read. It is found again at each regeneration, not kept as a list.
- **What the regeneration writes.**
  - A model replaces the member's built-in entry, wherever that entry stood, and goes to the file of its assembly in
    the framework's folder.
  - A member the regeneration refused keeps the model it already had, whoever wrote it; a member without one goes to
    the failed list.
  - Each regeneration runs over the whole census and rewrites the failed list. A member that left the census keeps
    its model and leaves the failed list.
- **Until the census settles.** Built-in models change what a run reaches. After writing, the tool rebuilds, runs
  both corpora again with what it wrote (`--census` with `--settle`, which reads the built-in models as they ship),
  and adds what they still generate. It stops when a run of each corpus generates no member of the two frameworks.
- **The report**, against the previous census: models gained, models changed, members newly failed, failed members
  that got a model, and members that joined or left the census.
- **The header** names the commit, as CoreLib's snapshot does. It also names the engine fingerprint: the fingerprint
  of ADR 0023 without the built-in models.
- **`-Since`** lists the changes since the recorded commit, in the three groups of CoreLib's tool.
- **The release refuses a stale census.**
  - `build/package.ps1 -Release` fails when the tree's engine fingerprint differs from the census's, and the CI
    workflow passes `-Release`.
  - Without the switch, the script names a stale census and goes on, so a change to the engine is tested before
    anyone regenerates.
  - The check compares fingerprints, not commits: CI checks out a single commit, with no history for `-Since`. It
    leaves the built-in models out because writing the census changes them.

The consequences to hold:

- **Main needs a fresh census.** A change to the engine that reaches `main` needs a regeneration before it merges.
- **Two engines in one run.** Between regenerations, the framework models a run applies describe an older engine
  than the package models it generates. Nothing in the run says so, as with CoreLib.
- **The demo goes stale.** The built-in models are inputs of the generator version (ADR 0023), so each regeneration
  makes the demo's generated files stale. The re-record then covers only the package members.

Rejected:

- **A committed census list generated member by member, as CoreLib's is.** The owner would keep the list by hand.
  The tool would also need rounds of its own for the dependencies a generation reads, which the run already has.
- **The gate in every `package.ps1`.** Every local check after a change to the engine would wait for a regeneration.
- **`-Since` in CI.** The checkout has no history. Fetching it would make every build pay for one check.
- **The generator version as the census's stamp.** It covers the census itself, so a census would be stale the
  moment it was written.
