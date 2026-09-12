# Z3 ships inside the executable and degrades to Unknown

The path and selector refinement of section 4.10 needs a solver that understands fixed-width integer
overflow and conversions, because TD-043 forbids using algebraic simplification as a safety proof
where that semantics is not modeled. `cache-detective`'s ADR 0001 keeps the single self-extracting
executable deliberately thin, and a native `libz3` adds about 35 MB to it.

Z3 is the solver, through `Microsoft.Z3`, and its native library is extracted beside the executable
on first run the same way Roslyn's build host is. The supported theory is fixed with it: `QF_BV` for
integer selectors and guards, so `i + 1 == j` is decided under the exact `int` semantics, and `QF_UF`
for opaque values and unknown comparers; no strings and no array theory, because a collection's
structure is modeled as its own resource by TD-070. The 150 ms per-query limit of TD-094 is Z3's own
timeout. When the native library fails to load, every query answers `Unknown`, the coverage section
says the solver was unavailable, and the run completes: the solver is last-mile refinement by
TD-091, and `Unknown` is already its ordinary answer.

Rejected: a solver of our own over a small theory — every gap in its handling of overflow and
conversions is either a constant `Unknown` or, worse, an unsound suppression, which is the one class
of defect this product cannot afford. Rejected: a hard dependency that fails the run when the native
library is missing, because a solver that cannot start should cost precision, not the report.
