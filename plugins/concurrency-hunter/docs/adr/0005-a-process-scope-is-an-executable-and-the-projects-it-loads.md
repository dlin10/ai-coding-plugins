# A process scope is an executable and the projects it loads

Shared state races only inside one OS process. A repository like eShopOnContainers holds a dozen
services that reference the same libraries; read as one process, a static field written by the
Ordering API and read by the Basket API becomes a finding about two objects that never meet.

A process scope is a project that builds an executable, together with every project it transitively
references with its output assembly. Execution roots, DI registrations and heap regions belong to
scopes, and two accesses become a candidate only inside one. A library in the closure of two
executables belongs to both, and its static field is a separate region in each. Test projects are
not scopes: they reference the application to exercise it, and as scopes they would repeat every
finding of the application under a process that never serves traffic. A reference that does not
load the referenced assembly, such as an Aspire AppHost's reference to the services it orchestrates,
does not extend the closure. A solution with no executable project is analyzed as one scope with a
diagnostic, because a library-only solution has no process to divide by, and refusing to analyze it
would hide what the analysis can still say.

Rejected: the whole solution as one process, which is what phase 1a did — it pairs accesses across
services by construction, and every multi-service corpus of section 12 would carry those false
positives into its snapshots. Rejected: every project as its own scope — it separates a web project
from the domain library that declares its singleton types, and loses every case whose registration,
root and resource live in different layers.

The consequence to hold: the finding set of a multi-application repository depends on this rule, so
corpus snapshots from phase 2 on encode it, and changing it later means reviewing every snapshot.
