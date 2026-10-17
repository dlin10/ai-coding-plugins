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
