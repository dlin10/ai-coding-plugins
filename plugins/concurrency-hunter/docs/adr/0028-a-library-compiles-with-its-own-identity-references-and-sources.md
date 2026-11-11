# A library compiles with its own identity and the references it was built against, from its original sources where its PDB names them

This decision replaces four rules of SPEC TD-034b and TD-005: that a package's copy of an assembly wins over the shared
framework's, that each dependency takes the lowest cached version its ranges admit, that the library compilation carries no
key, and that neither the analyzer nor the generator goes to the network. It amends the rejection in
[ADR 0012](0012-library-semantics-are-data-the-analysis-derives-from-decompiled-code.md) of "a library's published sources
in place of its decompiled code".

## Context

`library-does-not-compile` is the most frequent generation refusal on eShop: 146 of the 414 members the last run with
generation answered, in 15 libraries, Duende.IdentityServer 6.2.0 alone 83. Generated again with the generator of
`7d91f56`, 141 of them still refuse with the same compiler errors; the other five compile since the decompiler repairs. The
errors fall into three classes by what they depend on:

- Identity, 41 members (`Microsoft.AspNetCore.Mvc.Core` 30, `.Mvc.ViewFeatures` 7, `.Mvc` 4) and the demo's ASP.NET Core
  assemblies: CS0281, CS0122, CS0115. The copy has the library's name but not its public key, so the `InternalsVisibleTo`
  of another strong-named framework assembly does not admit it. An `[Experimental]` diagnostic the original build
  suppressed (ASP0029, SYSLIB5006) is an error to the copy.
- References, 100 members: CS0433, CS1705, CS1069, CS0738. Package assets of assemblies a shared framework provides
  (`System.Memory` 4.0.1.1, `System.Threading.Tasks.Extensions` 4.2) win over the framework and duplicate CoreLib's types,
  and a dependency taken at the lowest cached version a range admits is older than the one another reference was built
  against.
- Code: CS0104 and CS8051, repaired; CS7036 in `System.Security.Cryptography` 10.0, where the decompiler lost a base
  constructor call of the ML-DSA and SLH-DSA types: 48 members of the demo.

A build from the library's repository meets the first two classes as well: they come from the compilation's identity and
references, not from its code.

A scratch build that public-signed the copy with the assembly's own key, ignored errors whose id is not the compiler's,
let the higher version win between a package asset and a framework assembly, and raised dependencies to the versions the
assemblies of the set reference compiled all 158 refused members of the eShop libraries and the demo's ASP.NET Core
assemblies. Their next outcomes: 11 models, 83 model refusals (`unknown-touch` 33, `vocabulary` 24, `incomplete` 18,
`library-state` 8), `driver-not-synthesized` 54, `timeout` 5, `overridable` 4, `not-a-candidate` 1. Signing the copy alone
made the `InternalsVisibleTo` the opened copy gives the driver invalid (CS1726: a strong-named assembly names its friends
with their key), and the field opening then spent the member's whole limit inside the library compilation; opening the
fields of `System.Net.Security` with a key had taken over ten minutes in an earlier attempt. The driver signed with the same
key, and named with it, removes both.

Of the 1,027 package assemblies in the owner's packages folder (the newest version of each package), 37% carry a portable
PDB with Source Link, embedded or beside the dll — 11% of the Microsoft, System and Azure packages, 56% of the others. Another
32% are path-mapped CI builds whose PDB is not on disk, which public symbol servers serve; 29% have neither. Source Link
points to `raw.githubusercontent.com` in 382 of 384 cases, once to a private Azure DevOps host. A PDB records every document
with its checksum, the compilation's preprocessor symbols and options, embedded generated sources, and the document and
lines of every method. With the installed assembly's own PDB, the conditional compilation, the generated code and the
binding to the installed build that ADR 0012 named as the cost of published sources are read, not reconstructed.

## Decision

- **Identity.** The library compilation of a strong-named assembly is public-signed with the assembly's own public key.
  The driver compilation is public-signed with the same key, and the `InternalsVisibleTo` the opened copy gives the driver
  names it with that key.
- **Diagnostics.** An error whose id is not the compiler's own — the id an `[Experimental]` or obsoletion attribute names
  — does not make a library unusable: the original build suppressed it, and it changes no binding.
- **References.** An assembly that both a package of the set and a shared framework of the set provide is taken once, as
  the SDK's conflict resolution takes it: the higher assembly version, then the higher file version, on a tie the
  framework. A dependency's version is the lowest cached version its ranges admit whose assembly is not older than the
  version any assembly of the set references it at. An assembly the library itself references that no package or
  framework of the set provides is taken from the packages folder, at the lowest version not older than the reference.
  Packages are never downloaded.
- **Code.** A library compiles from its original sources when its PDB is found — embedded, beside the dll, in the source
  cache, or on a public symbol server by the assembly's PDB id — and every document the PDB lists is obtained, embedded or
  through Source Link, with the checksum the PDB records. The parse takes the preprocessor symbols and the language version
  from the PDB's compilation options; identity, diagnostics and references are as above. Otherwise, and when the original
  sources leave an error the decompiled code does not, the library compiles from its decompiled code, as before.
  The required documents, attributes and portable CodeView selection are amended by [the amendment below](#amendment-2026-11-09-source-retrieval-precedes-generation-and-persistent-failures-admit-storage).
- **Network.** Online is the normal mode. The run and `generate` download only PDBs from the Microsoft and NuGet symbol
  servers and the documents Source Link names, over HTTPS, without credentials; a host that asks for them, or does not
  answer, is skipped for the rest of the run. Each document is checked against its checksum before it is used. What
  arrives goes to the source cache, `%LOCALAPPDATA%\concurrency-hunter\sources\`, keyed by PDB id, outside every
  repository and shared by all of them. The offline mode, an option of the run, of `metrics` and of `generate`, sends no
  request and reads only the disk and the cache. The download time is part of the generation's time.
  Checksum acceptance and the time rule are amended by [the amendment below](#amendment-2026-11-09-source-retrieval-precedes-generation-and-persistent-failures-admit-storage).
- **What is kept.** An answer is kept only when it was built from the best code the assembly has: its sources when the
  PDB names them, its decompiled code when a symbol server answered that it has no PDB or the PDB names no sources. An
  answer built from decompiled code because the run was offline, or a download failed, rests on a reason of the run
  ([ADR 0027](0027-the-run-generates-in-rounds-and-a-generation-takes-its-dependencies-models.md)): it applies in that run
  and is not written. Persistent failures and finality are amended by [the amendment below](#amendment-2026-11-09-source-retrieval-precedes-generation-and-persistent-failures-admit-storage).
- **Member text.** A member's original text is its document and lines from the PDB. Where the cache holds them, the
  input of an AI model of the member shows that text; otherwise the decompiled member. A gap packet keeps its size limit
  (SPEC TD-035) and names the member, not its text.

Rejected: the original sources in place of the identity and reference rules. A build from the repository is unsigned and
resolves the same references; it fails with the same CS0281, CS0433 and CS1705, and only the code class needs sources.
Rejected: a separate command, started only by the user, as the only way to download. A run could not reach the sources a
model needs, and the answers would depend on whether someone had run the command. Rejected: building the sources with the
library's own build. MSBuild and restore run the repository's code; Roslyn with the PDB's options compiles what the PDB
lists. Rejected: the metadata references a PDB records as the reference set. They name the reference assemblies of the
build's own target framework, which the platform's implementation assemblies replace, and the assembly's own references
already give the versions: the measured set compiled without them. Rejected: the framework always winning. A package newer
than the framework, `System.Text.Json` 9 on .NET 8, is the one the program loads. Rejected: suppressing every error. A
declaration error the compiler reports is a fact about binding, and the body rewrite already handles errors in bodies.
Rejected: downloading a missing package. That is restore by another name, and the program the run analyses never loads
an assembly its own restore did not bring. Rejected: credentials for a private Source Link host. They are secrets of the
user's machine, and an answer that needed them could not be repeated elsewhere.

## Consequences

- SPEC TD-005, TD-034b and TD-034c absorb this decision, and PRD FR-21 and 6.3 name the network the plugin uses and the
  source cache. `GeneratorBoundaryTests` changes with them: `System.Net` is used only by the source fetch, and the files
  `generate` writes are its output and the source cache.
- Any of these changes moves the generator version, and every generated file is generated again
  ([ADR 0023](0023-a-generated-file-names-the-fingerprint-of-the-sources-its-generator-was-built-from.md)).
- The measured libraries reach the driver and the model vocabulary, which become the next limits: 54 members stop at the
  driver, 83 at the vocabulary and the observation.
- An online and an offline run of the same code apply different answers only where the offline one used decompiled code,
  and those answers are not kept, so the files agree. The cases that permit storage are refined by
  [the amendment below](#amendment-2026-11-09-source-retrieval-precedes-generation-and-persistent-failures-admit-storage).
- The source cache grows with the libraries the machine has analysed and is not cleaned by a run; nothing of it goes into
  a repository.
- A symbol server or Source Link host that is slow or rate-limits the run makes generation slower, not different: the
  budget cuts what it does not reach. The separation of retrieval from generation budgets and its effect on storage
  are amended by [the amendment below](#amendment-2026-11-09-source-retrieval-precedes-generation-and-persistent-failures-admit-storage).
- The requests name assemblies, PDB ids, packages and the commits of libraries, never the code of the solution.

## Amendment, 2026-11-09: source retrieval precedes generation and persistent failures admit storage

This amendment changes the Code, Network and What is kept decisions above and their consequences for time and storage.
The installed `System.Security.Cryptography` 10.0.14 ReadyToRun assembly has two CodeView entries: the first names a
Windows `ni.pdb` produced by crossgen; the portable entry has minor version `0x504D`. Microsoft's symbol server serves
that portable PDB by its portable SSQP key, not the timestamp key. Of the PDB's 730 documents, two are embedded; the
Source Link documents served with LF match their recorded checksums after conversion to CRLF. Two generated assembly
attribute documents have no method and return 404; without their attributes the source compilation has version `0.0.0.0`.
These observations refine how the assembly's own debug information selects the code, not its identity or references.

- **Time (D-0004, D-0013).** Retrieval finishes before the member's clocks start. It counts against none of the
  generation limits: the member's CPU limit and wall-clock backstop, the run limit, the recording limit or the census
  limit. It is absent from the answer's `seconds` and stages. The MCP run's thirty-minute deadline remains wall-clock
  time and includes retrieval; an answer cut by that deadline is cancellation and is not written.
- **Documents and attributes (D-0005).** A document no PDB method names may be absent in any mode. The source compilation
  adds the assembly-level attributes missing from its sources, without duplicates, from the same dll attribute output
  the decompiled path compiles. The copy therefore keeps the dll's name, version, public key and `InternalsVisibleTo`.
  Parsing uses the PDB's preprocessor symbols and language version; references, signing, body rewriting and disabled
  nullable are the same as on the decompiled path. A source compilation refused with `library-does-not-compile` falls
  back to decompiled code. The compilation cache key includes code identity: sources and decompiled code are distinct;
  sharing a compilation never shares a caller's code kind, cause or right to keep an answer.
- **Document checksums (D-0006).** A document is accepted only if its bytes as received, or after LF-to-CRLF conversion,
  match the PDB checksum. The matching form is what is compiled; neither a mismatch nor another normalization is accepted.
- **PDB identity (D-0007).** The portable CodeView entry supplies the PDB id; the first ReadyToRun Windows `ni.pdb`
  entry does not. The search order is embedded in the dll, beside the dll, the source cache by id, Microsoft, then NuGet.
  The servers are `https://msdl.microsoft.com/download/symbols` and `https://symbols.nuget.org/download/symbols`;
  the portable SSQP key is `<lowercase pdb name>/<GUID in N format>FFFFFFFF/<lowercase pdb name>`.
  NuGet is queried only with `SymbolChecksum` from the dll's PdbChecksum entry; without that entry it is not queried.
  A PDB is accepted only when its id and, if present, the dll's checksum match. A foreign id or checksum means that
  source has no usable PDB.
- **Rate limits and overload (D-0020).** HTTP 429 and 503 are retried up to four attempts per request, whether returned
  as responses or transport exceptions carrying that status. Retry-After supplies the wait as delta-seconds or an
  HTTP-date, capped at sixty seconds; without it the waits are one, two and four seconds. Waiting uses the retrieval
  clock, consumes no generation limit and stops immediately on cancellation, including the MCP run deadline. Only a
  final 429 or 503 is a transient failure, whose detail names that last status. Neither status skips the host for
  subsequent requests in the run.
- **Persistent failures and storage (D-0009).** One owner selects the code kind and cause from the stage outcomes in order:
  1. No portable CodeView entry means decompiled code is the best, with cause no PDB.
  2. A PDB found locally or by either server leads to document retrieval. Otherwise offline unavailability or any
     queried server's transient failure gives decompiled code for this run only. If every queried server persistently
     fails (404, 410, 401, 403, 407 or an unusable PDB), decompiled code is the best, with cause no PDB.
  3. A required document neither embedded nor cached and with no Source Link address gives the persistent cause no
     sources. A 404, 410, 401, 403, 407, non-HTTPS address or checksum mismatch even after LF-to-CRLF gives persistent
     failure. Any persistent document cause wins over offline unavailability and transient failures, with no sources
     before persistent failure. Otherwise no response for thirty seconds, a broken connection, 5xx, 429 or cancellation,
     or offline unavailability of any required document, gives decompiled code for this run only. Otherwise sources win.
  4. A refused source compilation gives decompiled code as the best, with cause sources rejected. If a document no
     method names was unavailable offline or because of a transient failure, the decompiled code is instead for this
     run only, with the cause of that unavailability.

Only HTTPS requests and HTTPS redirects are followed, without credentials or cookies. A host that answers 401, 403 or
407, or gives no response for thirty seconds including while reading the body, is skipped for the rest of the run.
A skipped host retains its cause: persistent for those status codes, transient for the timeout. This cause participates
in the ordering above; it is not turned into an undifferentiated download failure.

An answer from code prepared as decompiled for this run only applies to dependent generations, the scope's layer and
coverage, but is not final and is never written, whether a model, refusal, timeout or budget cut returned by the wrapper.
An answer carrying the view of such a dependency is not final either, directly or along a chain. A synthetic timeout
carries no dependency views, whatever its generation read before the cut; on the best code it remains storable under
ADR 0032. Persistent retrieval failures and rejected sources therefore permit storage under the other storage rules,
except when the rejected source compilation was missing a document for a transient or offline reason as above.
The generated-model file stays schema 2. `generate` reports `generation.code` with the kind and cause when library
compilation is reached, and its response is schema 5.

Consequently a slow or rate-limited host consumes retrieval wall time and may leave answers usable only in that run;
it does not consume the generation budget. Offline and online runs may select different code when disk and cache do not
suffice. Neither run stores an answer based on code available only for that run, so a temporary retrieval failure does
not become a permanent generated answer. The source cache remains outside repositories, written atomically and safe
for concurrent processes, and is never cleaned by a run. Core without a source provider retains decompilation without
network or source-cache access and its existing storage behavior; only the CLI entry points construct the provider.
