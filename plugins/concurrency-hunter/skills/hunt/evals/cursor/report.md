# Concurrency Hunter report

| Field | Value |
| --- | --- |
| Status | CompleteWithFindings |
| Reasons | none |
| Target | C:\Dev\CodexPlugins\plugins\concurrency-hunter\demo\Demo.slnx |
| Run | 20260914-180341-e2891e |
| Started | 2026-09-14T18:03:41.7897636+00:00 |
| Duration | 266.5 s |
| Version | concurrency-hunter 0.1.0 |
| IR schema | 1.0 |
| Providers | aspnetcore (Microsoft.AspNetCore.Mvc.Core 8.0.0.0..11.0.0.0, Microsoft.AspNetCore.Mvc 8.0.0.0..11.0.0.0, Microsoft.AspNetCore.Routing 8.0.0.0..11.0.0.0, Microsoft.AspNetCore.Http.Abstractions 8.0.0.0..11.0.0.0); hosting (Microsoft.Extensions.Hosting.Abstractions 8.0.0.0..11.0.0.0) |

### Executive summary
The demo hosts many high-confidence races on process-wide mutable state: static fields such as `Source`, `LastBeat`, and `_lastVisitor`, and DI singletons such as `ThemeSettings`, `VisitorStats`, `DraftRegistry`, `RelayState`, `WarmupState`, `Profile`, `FeatureFlags`, `ShelfState`, `QuotaState`, `HitStats`, and `ProgressWorker` [E:F1.R] [E:F2.R] [E:F4.R] [E:F15.R] [E:F17.R]. Concurrent HTTP self-overlap loses updates on write/write pairs [E:F1.O] [E:F11.S] [E:F18.S], action-versus-worker and start/stop pairs can tear reads [E:F5.O] [E:F8.O] [E:F16.S] [E:F3.S], and the `Hits` RMW can overwrite a peer increment [E:F15.S].

### Coverage
- Projects loaded: 4 of 4
- Missing projects: none
- Execution roots: 54
- Process scope Demo.Web: executable Demo.Web; projects Demo.Application, Demo.Domain, Demo.Web
  - Roots per provider: aspnetcore 35, hosting 18
  - Diagnostics from aspnetcore: none
  - Diagnostics from hosting: none
  - Registrations: 42
  - DI diagnostics: none
  - Skipped accesses: allocation 3, call-result 2, local 26, nested-field 3, unbound-member 3
  - Other diagnostics: none
- Process scope Demo.Worker: executable Demo.Worker; projects Demo.Domain, Demo.Worker
  - Roots per provider: aspnetcore 0, hosting 1
  - Diagnostics from aspnetcore: none
  - Diagnostics from hosting: none
  - Registrations: 1
  - DI diagnostics: none
  - Skipped accesses: none
  - Other diagnostics: none
- Reachable set: not analyzed in this version
- Semantic gaps: not analyzed in this version
- Calls from roots: not analyzed in this version
- Path feasibility: not analyzed in this version

### High findings
#### G1 · DCA1001 · Source on static:Demo.Domain.Cases.SharedLibraryStatic.LastSync (1 findings)
##### What can be lost
Concurrent callers can overwrite `Source` on `LastSync` so one update never becomes the lasting value under `DCA1001` [E:F1.R] [E:F1.S].

##### Why the accesses overlap
Both sides are the same write path through `Post` at `SharedLibraryStatic.cs:12`, so one request can race another on the identical static field [E:F1.A] [E:F1.B] [E:F1.O].

##### Why the protection is insufficient
Nothing serializes those writes; the pair is unprotected shared mutation [E:F1.P].

##### Interleaving
One request stores a new `Source`, then another stores another before anything consumes the first value, so the first write is discarded [E:F1.S] [E:F1.O].

##### Evidence mode
This `DCA1001` conclusion uses deterministic static evidence for the shared `LastSync` region [E:F1.R].

##### Uncertainty
Path feasibility is not established; confirm concurrent `Post` traffic reaches this endpoint in the same deployment [E:F1.S].

##### Remediation
- Wrap every read and write of `Source` in one shared `lock` so overlapping `Post` calls cannot tear the field; verify manually [E:F1.P].
  - Check: both write sites at `SharedLibraryStatic.cs:12` take that same guard before touching `Source` [E:F1.A] [E:F1.B].

##### F1
- Access A: Demo.Web.Cases.SharedLibraryStatic.SyncController.Post(string) performs write at Demo.Web/Cases/SharedLibraryStatic.cs:12 under root action Demo.Web.Cases.SharedLibraryStatic.SyncController.Post(string) of controller Demo.Web.Cases.SharedLibraryStatic.SyncController; holds no protection.
- Access B: Demo.Web.Cases.SharedLibraryStatic.SyncController.Post(string) performs write at Demo.Web/Cases/SharedLibraryStatic.cs:12 under root action Demo.Web.Cases.SharedLibraryStatic.SyncController.Post(string) of controller Demo.Web.Cases.SharedLibraryStatic.SyncController; holds no protection.
- Code path A: action Demo.Web.Cases.SharedLibraryStatic.SyncController.Post(string) of controller Demo.Web.Cases.SharedLibraryStatic.SyncController starts (Demo.Web/Cases/SharedLibraryStatic.cs:12) → write Demo.Domain.Cases.SharedLibraryStatic.LastSync.Source (Demo.Web/Cases/SharedLibraryStatic.cs:12)
- Code path B: action Demo.Web.Cases.SharedLibraryStatic.SyncController.Post(string) of controller Demo.Web.Cases.SharedLibraryStatic.SyncController starts (Demo.Web/Cases/SharedLibraryStatic.cs:12) → write Demo.Domain.Cases.SharedLibraryStatic.LastSync.Source (Demo.Web/Cases/SharedLibraryStatic.cs:12)
- Resource: Demo.Domain · static:Demo.Domain.Cases.SharedLibraryStatic.LastSync · Source · scope Demo.Web · shared as process
- Binding evidence: none
- Overlap: The root action Demo.Web.Cases.SharedLibraryStatic.SyncController.Post(string) of controller Demo.Web.Cases.SharedLibraryStatic.SyncController may run concurrently with itself in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: Repeated/MayOverlap in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: A writes `Source`; B writes `Source` before A's value is used; One of the two writes is lost
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F1.A, F1.B, F1.R, F1.O, F1.P, F1.S

#### G2 · DCA1001 · Theme on di:Demo.Web.Cases.ActionSelfOverlap.ThemeSettings@Singleton (1 findings)
##### What can be lost
Two overlapping `Put` calls can each assign `Theme` on the shared `ThemeSettings` instance and drop one update under `DCA1001` [E:F2.R] [E:F2.S].

##### Why the accesses overlap
Both accesses are writes at `ActionSelfOverlap.cs:20` via `Put`, so concurrent handlers collide on one DI singleton field [E:F2.A] [E:F2.B] [E:F2.O].

##### Why the protection is insufficient
No `lock` or other fence covers `Theme`; the field is unprotected [E:F2.P].

##### Interleaving
One handler writes `Theme`, another writes `Theme` before the first value is observed, and only the later store remains [E:F2.S].

##### Evidence mode
This `DCA1001` conclusion uses deterministic static evidence for the DI-bound `ThemeSettings` region [E:F2.R].

##### Uncertainty
Path feasibility is not established; confirm concurrent `Put` traffic hits this endpoint in the same deployment [E:F2.S].

##### Remediation
- Make updates to `Theme` atomic under one `lock` or an `Interlocked` swap; verify manually [E:F2.P].
  - Check: every path through `Put` at `ActionSelfOverlap.cs:20` uses that same synchronization before assigning `Theme` [E:F2.A] [E:F2.B].

##### F2
- Access A: Demo.Web.Cases.ActionSelfOverlap.ThemeController.Put(string) performs write at Demo.Web/Cases/ActionSelfOverlap.cs:20 under root action Demo.Web.Cases.ActionSelfOverlap.ThemeController.Put(string) of controller Demo.Web.Cases.ActionSelfOverlap.ThemeController; holds no protection.
- Access B: Demo.Web.Cases.ActionSelfOverlap.ThemeController.Put(string) performs write at Demo.Web/Cases/ActionSelfOverlap.cs:20 under root action Demo.Web.Cases.ActionSelfOverlap.ThemeController.Put(string) of controller Demo.Web.Cases.ActionSelfOverlap.ThemeController; holds no protection.
- Code path A: action Demo.Web.Cases.ActionSelfOverlap.ThemeController.Put(string) of controller Demo.Web.Cases.ActionSelfOverlap.ThemeController starts (Demo.Web/Cases/ActionSelfOverlap.cs:20) → write Demo.Web.Cases.ActionSelfOverlap.ThemeSettings.Theme (Demo.Web/Cases/ActionSelfOverlap.cs:20)
- Code path B: action Demo.Web.Cases.ActionSelfOverlap.ThemeController.Put(string) of controller Demo.Web.Cases.ActionSelfOverlap.ThemeController starts (Demo.Web/Cases/ActionSelfOverlap.cs:20) → write Demo.Web.Cases.ActionSelfOverlap.ThemeSettings.Theme (Demo.Web/Cases/ActionSelfOverlap.cs:20)
- Resource: Demo.Web · di:Demo.Web.Cases.ActionSelfOverlap.ThemeSettings@Singleton · Theme · scope Demo.Web · shared as process:Demo.Web.Cases.ActionSelfOverlap.ThemeSettings
- Binding evidence: Demo.Web.Cases.ActionSelfOverlap.ThemeController._settings holds constructor parameter settings at Demo.Web/Cases/ActionSelfOverlap.cs:17; AddSingleton registers Demo.Web.Cases.ActionSelfOverlap.ThemeSettings at Demo.Web/Cases/ActionSelfOverlap.cs:26
- Overlap: The root action Demo.Web.Cases.ActionSelfOverlap.ThemeController.Put(string) of controller Demo.Web.Cases.ActionSelfOverlap.ThemeController may run concurrently with itself in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: Repeated/MayOverlap in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: A writes `Theme`; B writes `Theme` before A's value is used; One of the two writes is lost
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F2.A, F2.B, F2.R, F2.O, F2.P, F2.S

#### G3 · DCA1001 · _lastItem on di:Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker@Singleton (1 findings)
##### What can be lost
`StopAsync` may observe a stale or just-updated `_lastItem` while `ExecuteAsync` is still publishing progress, so shutdown logic can act on the wrong item [E:F3.R] [E:F3.S].

##### Why the accesses overlap
One side writes `_lastItem` in `ExecuteAsync` at `BackgroundStopReadsOwnField.cs:10`; the other reads it in `StopAsync` at `BackgroundStopReadsOwnField.cs:16`. Hosted-service execute and stop can run together [E:F3.A] [E:F3.B] [E:F3.O].

##### Why the protection is insufficient
The field has no shared guard between the background loop and stop; the pair is unprotected [E:F3.P].

##### Interleaving
A write assigns `_lastItem` while stop reads it, so the reader may see the pre-write or post-write value [E:F3.S].

##### Evidence mode
This `DCA1001` conclusion uses deterministic static evidence for the DI-bound `ProgressWorker` field [E:F3.R].

##### Uncertainty
Relative ordering of execute versus stop was not analyzed; whether stop overlaps mid-iteration is unknown [E:F3.O].

##### Remediation
- Publish `_lastItem` under one `lock` shared by `ExecuteAsync` and `StopAsync`, or drain before reading; verify manually [E:F3.P].
  - Check: the write at `BackgroundStopReadsOwnField.cs:10` and the read at `BackgroundStopReadsOwnField.cs:16` use the same fence or proven stop-before-read ordering [E:F3.A] [E:F3.B].

##### F3
- Access A: Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker.ExecuteAsync(CancellationToken) performs write at Demo.Web/Cases/BackgroundStopReadsOwnField.cs:10 under root execute of hosted service Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker; holds no protection.
- Access B: Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker.StopAsync(CancellationToken) performs read at Demo.Web/Cases/BackgroundStopReadsOwnField.cs:16 under root stop of hosted service Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker; holds no protection.
- Code path A: execute of hosted service Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker starts (Demo.Web/Cases/BackgroundStopReadsOwnField.cs:8) → write Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker._lastItem (Demo.Web/Cases/BackgroundStopReadsOwnField.cs:10)
- Code path B: stop of hosted service Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker starts (Demo.Web/Cases/BackgroundStopReadsOwnField.cs:14) → read Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker._lastItem (Demo.Web/Cases/BackgroundStopReadsOwnField.cs:16)
- Resource: Demo.Web · di:Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker@Singleton · _lastItem · scope Demo.Web · shared as process:Microsoft.Extensions.Hosting.IHostedService
- Binding evidence: AddHostedService registers Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker at Demo.Web/Cases/BackgroundStopReadsOwnField.cs:26
- Overlap: The roots execute of hosted service Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker and stop of hosted service Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker may run concurrently in scope Demo.Web. A: AtMostOnce/Serialized in Demo.Web; B: AtMostOnce/Serialized in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: A writes `_lastItem`; B reads `_lastItem` at the same time; B observes either the old or the new value
- Uncertainty: Path feasibility is not analyzed in this version. Ordering between lifecycle methods of one hosted service is not analyzed in this version.
- Confidence: High (85)
- Evidence: F3.A, F3.B, F3.R, F3.O, F3.P, F3.S

#### G4 · DCA1001 · LastPath on di:Demo.Web.Cases.DiSingletonControllerVsWorker.VisitorStats@Singleton (2 findings)
##### What can be lost
Concurrent writers to `LastPath` on the shared `VisitorStats` instance can overwrite each other, so a recorded path never becomes the lasting state [E:F4.S] [E:F5.S].

##### Why the accesses overlap
`Post` may run against itself on the same singleton [E:F4.O], and the same action may also race `ExecuteAsync`, which clears or writes the same field [E:F5.O].

##### Why the protection is insufficient
Neither pairing shows a shared guard around `LastPath`; both sides are unprotected [E:F4.P] [E:F5.P].

##### Interleaving
One `Post` write at `DiSingletonControllerVsWorker.cs:20` can land after another `Post` write before the first value is used [E:F4.A] [E:F4.B]. Separately, a worker write at `DiSingletonControllerVsWorker.cs:31` can interleave with `Post` the same way [E:F5.A] [E:F5.B].

##### Evidence mode
This `DCA1001` group follows deterministic static pairing of unsynchronized writes on one DI singleton field [E:F4.R] [E:F5.R].

##### Uncertainty
Static overlap does not prove every deployment schedules both roots together; confirm request traffic and the hosted reset worker can run in the same process [E:F4.O] [E:F5.O].

##### Remediation
- Guard every access to `LastPath` with the same process-wide `lock`; verify manually [E:F4.P] [E:F5.P].
  - Check: `Post` and `ExecuteAsync` both take that guard before touching `LastPath` [E:F4.A] [E:F5.B].
- Or stop mutating shared path state from the action and the worker; verify manually [E:F4.R] [E:F5.R].
  - Check: after the change, no concurrent write path remains on `LastPath` from `Post` and `ExecuteAsync` [E:F4.S] [E:F5.S].

##### F4
- Access A: Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController.Post(string) performs write at Demo.Web/Cases/DiSingletonControllerVsWorker.cs:20 under root action Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController.Post(string) of controller Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController; holds no protection.
- Access B: Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController.Post(string) performs write at Demo.Web/Cases/DiSingletonControllerVsWorker.cs:20 under root action Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController.Post(string) of controller Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController; holds no protection.
- Code path A: action Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController.Post(string) of controller Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController starts (Demo.Web/Cases/DiSingletonControllerVsWorker.cs:20) → write Demo.Web.Cases.DiSingletonControllerVsWorker.VisitorStats.LastPath (Demo.Web/Cases/DiSingletonControllerVsWorker.cs:20)
- Code path B: action Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController.Post(string) of controller Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController starts (Demo.Web/Cases/DiSingletonControllerVsWorker.cs:20) → write Demo.Web.Cases.DiSingletonControllerVsWorker.VisitorStats.LastPath (Demo.Web/Cases/DiSingletonControllerVsWorker.cs:20)
- Resource: Demo.Web · di:Demo.Web.Cases.DiSingletonControllerVsWorker.VisitorStats@Singleton · LastPath · scope Demo.Web · shared as process:Demo.Web.Cases.DiSingletonControllerVsWorker.VisitorStats
- Binding evidence: Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController._stats holds constructor parameter stats at Demo.Web/Cases/DiSingletonControllerVsWorker.cs:17; AddSingleton registers Demo.Web.Cases.DiSingletonControllerVsWorker.VisitorStats at Demo.Web/Cases/DiSingletonControllerVsWorker.cs:39
- Overlap: The root action Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController.Post(string) of controller Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController may run concurrently with itself in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: Repeated/MayOverlap in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: A writes `LastPath`; B writes `LastPath` before A's value is used; One of the two writes is lost
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F4.A, F4.B, F4.R, F4.O, F4.P, F4.S

##### F5
- Access A: Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController.Post(string) performs write at Demo.Web/Cases/DiSingletonControllerVsWorker.cs:20 under root action Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController.Post(string) of controller Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController; holds no protection.
- Access B: Demo.Web.Cases.DiSingletonControllerVsWorker.StatsResetWorker.ExecuteAsync(CancellationToken) performs write at Demo.Web/Cases/DiSingletonControllerVsWorker.cs:31 under root execute of hosted service Demo.Web.Cases.DiSingletonControllerVsWorker.StatsResetWorker; holds no protection.
- Code path A: action Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController.Post(string) of controller Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController starts (Demo.Web/Cases/DiSingletonControllerVsWorker.cs:20) → write Demo.Web.Cases.DiSingletonControllerVsWorker.VisitorStats.LastPath (Demo.Web/Cases/DiSingletonControllerVsWorker.cs:20)
- Code path B: execute of hosted service Demo.Web.Cases.DiSingletonControllerVsWorker.StatsResetWorker starts (Demo.Web/Cases/DiSingletonControllerVsWorker.cs:29) → write Demo.Web.Cases.DiSingletonControllerVsWorker.VisitorStats.LastPath (Demo.Web/Cases/DiSingletonControllerVsWorker.cs:31)
- Resource: Demo.Web · di:Demo.Web.Cases.DiSingletonControllerVsWorker.VisitorStats@Singleton · LastPath · scope Demo.Web · shared as process:Demo.Web.Cases.DiSingletonControllerVsWorker.VisitorStats
- Binding evidence: Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController._stats holds constructor parameter stats at Demo.Web/Cases/DiSingletonControllerVsWorker.cs:17; AddSingleton registers Demo.Web.Cases.DiSingletonControllerVsWorker.VisitorStats at Demo.Web/Cases/DiSingletonControllerVsWorker.cs:39; Demo.Web.Cases.DiSingletonControllerVsWorker.StatsResetWorker._stats holds constructor parameter stats at Demo.Web/Cases/DiSingletonControllerVsWorker.cs:27
- Overlap: The roots action Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController.Post(string) of controller Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController and execute of hosted service Demo.Web.Cases.DiSingletonControllerVsWorker.StatsResetWorker may run concurrently in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: AtMostOnce/Serialized in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: A writes `LastPath`; B writes `LastPath` before A's value is used; One of the two writes is lost
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F5.A, F5.B, F5.R, F5.O, F5.P, F5.S

#### G5 · DCA1001 · Current on di:Demo.Web.Cases.EscapeIntoSingleton.DraftRegistry@Singleton (1 findings)
##### What can be lost
A concurrent read of `Current` on the shared `DraftRegistry` can observe either the pre-update or post-update value with no defined ordering against the writer [E:F6.S].

##### Why the accesses overlap
`Get` and `ExecuteAsync` can run at the same time against the process-scoped registry [E:F6.O].

##### Why the protection is insufficient
The paired accesses do not share an evidenced synchronization edge around `Current` [E:F6.P].

##### Interleaving
While `ExecuteAsync` writes `Current` at `EscapeIntoSingleton.cs:26`, a simultaneous `Get` at `EscapeIntoSingleton.cs:41` can sample the field mid-update [E:F6.A] [E:F6.B].

##### Evidence mode
This `DCA1001` finding is grounded in deterministic static evidence for one DI-bound field [E:F6.R].

##### Uncertainty
Confirm the hosted worker and HTTP `Get` actually overlap in the deployed host; path reachability is not proven here [E:F6.O] [E:F6.S].

##### Remediation
- Protect every access to `Current` with one shared `lock`; verify manually [E:F6.P].
  - Check: both `Get` and `ExecuteAsync` use that same guard for `Current` [E:F6.A] [E:F6.B].
- Or replace the mutable field with an immutable snapshot handoff; verify manually [E:F6.R].
  - Check: no unsynchronized write to `Current` remains beside a concurrent `Get` [E:F6.S].

##### F6
- Access A: Demo.Web.Cases.EscapeIntoSingleton.DraftsController.Get() performs read at Demo.Web/Cases/EscapeIntoSingleton.cs:41 under root action Demo.Web.Cases.EscapeIntoSingleton.DraftsController.Get() of controller Demo.Web.Cases.EscapeIntoSingleton.DraftsController; holds no protection.
- Access B: Demo.Web.Cases.EscapeIntoSingleton.DraftWorker.ExecuteAsync(CancellationToken) performs write at Demo.Web/Cases/EscapeIntoSingleton.cs:26 under root execute of hosted service Demo.Web.Cases.EscapeIntoSingleton.DraftWorker; holds no protection.
- Code path A: action Demo.Web.Cases.EscapeIntoSingleton.DraftsController.Get() of controller Demo.Web.Cases.EscapeIntoSingleton.DraftsController starts (Demo.Web/Cases/EscapeIntoSingleton.cs:41) → read Demo.Web.Cases.EscapeIntoSingleton.DraftRegistry.Current (Demo.Web/Cases/EscapeIntoSingleton.cs:41)
- Code path B: execute of hosted service Demo.Web.Cases.EscapeIntoSingleton.DraftWorker starts (Demo.Web/Cases/EscapeIntoSingleton.cs:23) → write Demo.Web.Cases.EscapeIntoSingleton.DraftRegistry.Current (Demo.Web/Cases/EscapeIntoSingleton.cs:26)
- Resource: Demo.Web · di:Demo.Web.Cases.EscapeIntoSingleton.DraftRegistry@Singleton · Current · scope Demo.Web · shared as process:Demo.Web.Cases.EscapeIntoSingleton.DraftRegistry
- Binding evidence: Demo.Web.Cases.EscapeIntoSingleton.DraftsController._registry holds constructor parameter registry at Demo.Web/Cases/EscapeIntoSingleton.cs:38; AddSingleton registers Demo.Web.Cases.EscapeIntoSingleton.DraftRegistry at Demo.Web/Cases/EscapeIntoSingleton.cs:47; Demo.Web.Cases.EscapeIntoSingleton.DraftWorker._registry holds constructor parameter registry at Demo.Web/Cases/EscapeIntoSingleton.cs:21
- Overlap: The roots action Demo.Web.Cases.EscapeIntoSingleton.DraftsController.Get() of controller Demo.Web.Cases.EscapeIntoSingleton.DraftsController and execute of hosted service Demo.Web.Cases.EscapeIntoSingleton.DraftWorker may run concurrently in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: AtMostOnce/Serialized in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: B writes `Current`; A reads `Current` at the same time; A observes either the old or the new value
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F6.A, F6.B, F6.R, F6.O, F6.P, F6.S

#### G6 · DCA1001 · Target on di:Demo.Web.Cases.FromServicesActionParameter.RelayState@Singleton (1 findings)
##### What can be lost
Two concurrent `Post` calls can each write `Target` on the shared `RelayState` and leave only one write visible, dropping the other update [E:F7.S].

##### Why the accesses overlap
`Post` may execute for distinct requests at the same time against the same FromServices singleton parameter instance [E:F7.O].

##### Why the protection is insufficient
Both write sites hold no protection around `Target` [E:F7.P].

##### Interleaving
One request writes `Target` at `FromServicesActionParameter.cs:16`, then another writes `Target` before the first value is consumed, so the first write is lost [E:F7.A] [E:F7.B].

##### Evidence mode
This `DCA1001` conclusion uses deterministic static evidence for the DI singleton field [E:F7.R].

##### Uncertainty
Path feasibility is not established; confirm concurrent `Post` traffic can reach this action in the same deployment [E:F7.S].

##### Remediation
- Serialize all mutations of `Target` with one process-wide `lock`; verify manually [E:F7.P].
  - Check: every `Post` write of `Target` enters that same guard [E:F7.A] [E:F7.B].
- Or publish `Target` through an atomic exchange pattern; verify manually [E:F7.R].
  - Check: after the redesign, concurrent `Post` calls cannot overwrite each other's `Target` without coordination [E:F7.S].

##### F7
- Access A: Demo.Web.Cases.FromServicesActionParameter.RelayController.Post(RelayState, string) performs write at Demo.Web/Cases/FromServicesActionParameter.cs:16 under root action Demo.Web.Cases.FromServicesActionParameter.RelayController.Post(RelayState, string) of controller Demo.Web.Cases.FromServicesActionParameter.RelayController; holds no protection.
- Access B: Demo.Web.Cases.FromServicesActionParameter.RelayController.Post(RelayState, string) performs write at Demo.Web/Cases/FromServicesActionParameter.cs:16 under root action Demo.Web.Cases.FromServicesActionParameter.RelayController.Post(RelayState, string) of controller Demo.Web.Cases.FromServicesActionParameter.RelayController; holds no protection.
- Code path A: action Demo.Web.Cases.FromServicesActionParameter.RelayController.Post(RelayState, string) of controller Demo.Web.Cases.FromServicesActionParameter.RelayController starts (Demo.Web/Cases/FromServicesActionParameter.cs:16) → write Demo.Web.Cases.FromServicesActionParameter.RelayState.Target (Demo.Web/Cases/FromServicesActionParameter.cs:16)
- Code path B: action Demo.Web.Cases.FromServicesActionParameter.RelayController.Post(RelayState, string) of controller Demo.Web.Cases.FromServicesActionParameter.RelayController starts (Demo.Web/Cases/FromServicesActionParameter.cs:16) → write Demo.Web.Cases.FromServicesActionParameter.RelayState.Target (Demo.Web/Cases/FromServicesActionParameter.cs:16)
- Resource: Demo.Web · di:Demo.Web.Cases.FromServicesActionParameter.RelayState@Singleton · Target · scope Demo.Web · shared as process:Demo.Web.Cases.FromServicesActionParameter.RelayState
- Binding evidence: AddSingleton registers Demo.Web.Cases.FromServicesActionParameter.RelayState at Demo.Web/Cases/FromServicesActionParameter.cs:22
- Overlap: The root action Demo.Web.Cases.FromServicesActionParameter.RelayController.Post(RelayState, string) of controller Demo.Web.Cases.FromServicesActionParameter.RelayController may run concurrently with itself in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: Repeated/MayOverlap in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: A writes `Target`; B writes `Target` before A's value is used; One of the two writes is lost
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F7.A, F7.B, F7.R, F7.O, F7.P, F7.S

#### G7 · DCA1001 · Stage on di:Demo.Web.Cases.HostedStartVsAction.WarmupState@Singleton (1 findings)
##### What can be lost
A concurrent `Get` can observe either a stale or freshly written `Stage` while `StartAsync` publishes on the shared `WarmupState` [E:F8.S] [E:F8.R].

##### Why the accesses overlap
The HTTP action and hosted-service start may run at the same time in one process [E:F8.O].

##### Why the protection is insufficient
Neither side holds a shared guard around `Stage` [E:F8.P].

##### Interleaving
`StartAsync` writes at `HostedStartVsAction.cs:19` while `Get` reads at `HostedStartVsAction.cs:35`, so the returned value depends on timing [E:F8.A] [E:F8.B] [E:F8.S].

##### Evidence mode
This `DCA1001` conclusion uses deterministic static evidence [E:F8.R].

##### Uncertainty
Path feasibility is not established; confirm the hosted start and the action can run together in the deployed host [E:F8.S].

##### Remediation
- Serialize every access to `Stage` on the shared `WarmupState` with one `lock`; verify manually [E:F8.P].
  - Check: confirm the write in `StartAsync` and the read in `Get` take that same guard [E:F8.A] [E:F8.B].

##### F8
- Access A: Demo.Web.Cases.HostedStartVsAction.WarmupController.Get() performs read at Demo.Web/Cases/HostedStartVsAction.cs:35 under root action Demo.Web.Cases.HostedStartVsAction.WarmupController.Get() of controller Demo.Web.Cases.HostedStartVsAction.WarmupController; holds no protection.
- Access B: Demo.Web.Cases.HostedStartVsAction.WarmupService.StartAsync(CancellationToken) performs write at Demo.Web/Cases/HostedStartVsAction.cs:19 under root start of hosted service Demo.Web.Cases.HostedStartVsAction.WarmupService; holds no protection.
- Code path A: action Demo.Web.Cases.HostedStartVsAction.WarmupController.Get() of controller Demo.Web.Cases.HostedStartVsAction.WarmupController starts (Demo.Web/Cases/HostedStartVsAction.cs:35) → read Demo.Web.Cases.HostedStartVsAction.WarmupState.Stage (Demo.Web/Cases/HostedStartVsAction.cs:35)
- Code path B: start of hosted service Demo.Web.Cases.HostedStartVsAction.WarmupService starts (Demo.Web/Cases/HostedStartVsAction.cs:17) → write Demo.Web.Cases.HostedStartVsAction.WarmupState.Stage (Demo.Web/Cases/HostedStartVsAction.cs:19)
- Resource: Demo.Web · di:Demo.Web.Cases.HostedStartVsAction.WarmupState@Singleton · Stage · scope Demo.Web · shared as process:Demo.Web.Cases.HostedStartVsAction.WarmupState
- Binding evidence: Demo.Web.Cases.HostedStartVsAction.WarmupController._state holds constructor parameter state at Demo.Web/Cases/HostedStartVsAction.cs:32; AddSingleton registers Demo.Web.Cases.HostedStartVsAction.WarmupState at Demo.Web/Cases/HostedStartVsAction.cs:41; Demo.Web.Cases.HostedStartVsAction.WarmupService._state holds constructor parameter state at Demo.Web/Cases/HostedStartVsAction.cs:15
- Overlap: The roots action Demo.Web.Cases.HostedStartVsAction.WarmupController.Get() of controller Demo.Web.Cases.HostedStartVsAction.WarmupController and start of hosted service Demo.Web.Cases.HostedStartVsAction.WarmupService may run concurrently in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: AtMostOnce/Serialized in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: B writes `Stage`; A reads `Stage` at the same time; A observes either the old or the new value
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F8.A, F8.B, F8.R, F8.O, F8.P, F8.S

#### G8 · DCA1001 · Email on di:Demo.Web.Cases.LambdaAndLocalFunction.Profile@Singleton (1 findings)
##### What can be lost
Two concurrent `Update` calls can each store into `Email` on the shared `Profile`, and only one write survives [E:F9.S] [E:F9.R].

##### Why the accesses overlap
The action root may run concurrently with itself across requests [E:F9.O].

##### Why the protection is insufficient
The writes are unprotected; no common guard is evidenced [E:F9.P].

##### Interleaving
One write to `Email` at `LambdaAndLocalFunction.cs:24` can land after another request has already decided its value, so the earlier update is discarded [E:F9.A] [E:F9.B] [E:F9.S].

##### Evidence mode
This `DCA1001` conclusion uses deterministic static evidence [E:F9.R].

##### Uncertainty
Confirm that concurrent `Update` requests can hit the same singleton in production [E:F9.O] [E:F9.S].

##### Remediation
- Guard every write to `Email` on `Profile` with one process-wide `lock`; verify manually [E:F9.P].
  - Check: confirm both concurrent `Update` paths take that same guard before assigning `Email` [E:F9.A] [E:F9.B].

##### F9
- Access A: Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) performs write at Demo.Web/Cases/LambdaAndLocalFunction.cs:24 under root action Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) of controller Demo.Web.Cases.LambdaAndLocalFunction.ProfileController; holds no protection.
- Access B: Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) performs write at Demo.Web/Cases/LambdaAndLocalFunction.cs:24 under root action Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) of controller Demo.Web.Cases.LambdaAndLocalFunction.ProfileController; holds no protection.
- Code path A: action Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) of controller Demo.Web.Cases.LambdaAndLocalFunction.ProfileController starts (Demo.Web/Cases/LambdaAndLocalFunction.cs:21) → write Demo.Web.Cases.LambdaAndLocalFunction.Profile.Email (Demo.Web/Cases/LambdaAndLocalFunction.cs:24)
- Code path B: action Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) of controller Demo.Web.Cases.LambdaAndLocalFunction.ProfileController starts (Demo.Web/Cases/LambdaAndLocalFunction.cs:21) → write Demo.Web.Cases.LambdaAndLocalFunction.Profile.Email (Demo.Web/Cases/LambdaAndLocalFunction.cs:24)
- Resource: Demo.Web · di:Demo.Web.Cases.LambdaAndLocalFunction.Profile@Singleton · Email · scope Demo.Web · shared as process:Demo.Web.Cases.LambdaAndLocalFunction.Profile
- Binding evidence: Demo.Web.Cases.LambdaAndLocalFunction.ProfileController._profile holds constructor parameter profile at Demo.Web/Cases/LambdaAndLocalFunction.cs:18; AddSingleton registers Demo.Web.Cases.LambdaAndLocalFunction.Profile at Demo.Web/Cases/LambdaAndLocalFunction.cs:34
- Overlap: The root action Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) of controller Demo.Web.Cases.LambdaAndLocalFunction.ProfileController may run concurrently with itself in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: Repeated/MayOverlap in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: A writes `Email`; B writes `Email` before A's value is used; One of the two writes is lost
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F9.A, F9.B, F9.R, F9.O, F9.P, F9.S

#### G9 · DCA1001 · Name on di:Demo.Web.Cases.LambdaAndLocalFunction.Profile@Singleton (1 findings)
##### What can be lost
Two concurrent `Update` calls can each store into `Name` on the shared `Profile`, and only one write survives [E:F10.S] [E:F10.R].

##### Why the accesses overlap
The action root may run concurrently with itself across requests [E:F10.O].

##### Why the protection is insufficient
The writes are unprotected; no common guard is evidenced [E:F10.P].

##### Interleaving
One write to `Name` at `LambdaAndLocalFunction.cs:27` can land after another request has already decided its value, so the earlier update is discarded [E:F10.A] [E:F10.B] [E:F10.S].

##### Evidence mode
This `DCA1001` conclusion uses deterministic static evidence [E:F10.R].

##### Uncertainty
Confirm that concurrent `Update` requests can hit the same singleton in production [E:F10.O] [E:F10.S].

##### Remediation
- Guard every write to `Name` on `Profile` with one process-wide `lock`; verify manually [E:F10.P].
  - Check: confirm both concurrent `Update` paths take that same guard before assigning `Name` [E:F10.A] [E:F10.B].

##### F10
- Access A: Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) performs write at Demo.Web/Cases/LambdaAndLocalFunction.cs:27 under root action Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) of controller Demo.Web.Cases.LambdaAndLocalFunction.ProfileController; holds no protection.
- Access B: Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) performs write at Demo.Web/Cases/LambdaAndLocalFunction.cs:27 under root action Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) of controller Demo.Web.Cases.LambdaAndLocalFunction.ProfileController; holds no protection.
- Code path A: action Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) of controller Demo.Web.Cases.LambdaAndLocalFunction.ProfileController starts (Demo.Web/Cases/LambdaAndLocalFunction.cs:21) → write Demo.Web.Cases.LambdaAndLocalFunction.Profile.Name (Demo.Web/Cases/LambdaAndLocalFunction.cs:27)
- Code path B: action Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) of controller Demo.Web.Cases.LambdaAndLocalFunction.ProfileController starts (Demo.Web/Cases/LambdaAndLocalFunction.cs:21) → write Demo.Web.Cases.LambdaAndLocalFunction.Profile.Name (Demo.Web/Cases/LambdaAndLocalFunction.cs:27)
- Resource: Demo.Web · di:Demo.Web.Cases.LambdaAndLocalFunction.Profile@Singleton · Name · scope Demo.Web · shared as process:Demo.Web.Cases.LambdaAndLocalFunction.Profile
- Binding evidence: Demo.Web.Cases.LambdaAndLocalFunction.ProfileController._profile holds constructor parameter profile at Demo.Web/Cases/LambdaAndLocalFunction.cs:18; AddSingleton registers Demo.Web.Cases.LambdaAndLocalFunction.Profile at Demo.Web/Cases/LambdaAndLocalFunction.cs:34
- Overlap: The root action Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) of controller Demo.Web.Cases.LambdaAndLocalFunction.ProfileController may run concurrently with itself in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: Repeated/MayOverlap in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: A writes `Name`; B writes `Name` before A's value is used; One of the two writes is lost
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F10.A, F10.B, F10.R, F10.O, F10.P, F10.S

#### G10 · DCA1001 · Mode on di:Demo.Web.Cases.MinimalApiReadWrite.FeatureFlags@Singleton (2 findings)
##### What can be lost
Two concurrent `Set` calls can each store a different `Mode` on the shared `FeatureFlags` instance, and only the later write remains [E:F11.S] [E:F11.R]. While a write is in flight, `Get` may return either the previous or the newly written `Mode` [E:F12.S].

##### Why the accesses overlap
The MapPost handler root may run for more than one request at once, so `Set` overlaps itself on one process-wide object [E:F11.O]. That same MapPost root may also run beside the MapGet root that calls `Get` [E:F12.O].

##### Why the protection is insufficient
Neither the write/write pair nor the write/read pair shows a shared evidenced guard around `Mode` [E:F11.P] [E:F12.P].

##### Interleaving
One assignment at `MinimalApiReadWrite.cs:12` can land after another write to `Mode` but before the first value is used, so one update is discarded [E:F11.A] [E:F11.B] [E:F11.S]. A write at `MinimalApiReadWrite.cs:12` can also race a read at `MinimalApiReadWrite.cs:14`, leaving the reader with old or new `Mode` [E:F12.A] [E:F12.B] [E:F12.S].

##### Evidence mode
This `DCA1001` conclusion rests on deterministic static pairing for the `FeatureFlags` region [E:F11.R] [E:F12.R].

##### Uncertainty
Path feasibility is not established; confirm concurrent MapPost and MapGet traffic can reach these handlers in the same deployment [E:F11.S] [E:F12.S].

##### Remediation
- Guard every access to `Mode` with the same `lock`; verify manually [E:F11.P] [E:F12.P].
  - Check: confirm `Set` at `MinimalApiReadWrite.cs:12` and `Get` at `MinimalApiReadWrite.cs:14` both take that one guard [E:F11.A] [E:F12.A] [E:F12.B].
- Stop mutating shared `FeatureFlags` from overlapping request roots, or publish `Mode` via an atomic hand-off; verify manually [E:F11.R] [E:F12.R].
  - Check: confirm concurrent `Set` and `Get` no longer share an unprotected `Mode` slot [E:F11.B] [E:F11.O] [E:F12.O] [E:F12.S].

##### F11
- Access A: Demo.Web.Cases.MinimalApiReadWrite.FeatureFlagEndpoints.Set(FeatureFlags, string) performs write at Demo.Web/Cases/MinimalApiReadWrite.cs:12 under root MapPost handler Demo.Web.Cases.MinimalApiReadWrite.MinimalApiReadWriteCase.MapMinimalApiReadWrite(WebApplication)#map1; holds no protection.
- Access B: Demo.Web.Cases.MinimalApiReadWrite.FeatureFlagEndpoints.Set(FeatureFlags, string) performs write at Demo.Web/Cases/MinimalApiReadWrite.cs:12 under root MapPost handler Demo.Web.Cases.MinimalApiReadWrite.MinimalApiReadWriteCase.MapMinimalApiReadWrite(WebApplication)#map1; holds no protection.
- Code path A: MapPost handler Demo.Web.Cases.MinimalApiReadWrite.MinimalApiReadWriteCase.MapMinimalApiReadWrite(WebApplication)#map1 starts (Demo.Web/Cases/MinimalApiReadWrite.cs:12) → write Demo.Web.Cases.MinimalApiReadWrite.FeatureFlags.Mode (Demo.Web/Cases/MinimalApiReadWrite.cs:12)
- Code path B: MapPost handler Demo.Web.Cases.MinimalApiReadWrite.MinimalApiReadWriteCase.MapMinimalApiReadWrite(WebApplication)#map1 starts (Demo.Web/Cases/MinimalApiReadWrite.cs:12) → write Demo.Web.Cases.MinimalApiReadWrite.FeatureFlags.Mode (Demo.Web/Cases/MinimalApiReadWrite.cs:12)
- Resource: Demo.Web · di:Demo.Web.Cases.MinimalApiReadWrite.FeatureFlags@Singleton · Mode · scope Demo.Web · shared as process:Demo.Web.Cases.MinimalApiReadWrite.FeatureFlags
- Binding evidence: AddSingleton registers Demo.Web.Cases.MinimalApiReadWrite.FeatureFlags at Demo.Web/Cases/MinimalApiReadWrite.cs:22
- Overlap: The root MapPost handler Demo.Web.Cases.MinimalApiReadWrite.MinimalApiReadWriteCase.MapMinimalApiReadWrite(WebApplication)#map1 may run concurrently with itself in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: Repeated/MayOverlap in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: A writes `Mode`; B writes `Mode` before A's value is used; One of the two writes is lost
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F11.A, F11.B, F11.R, F11.O, F11.P, F11.S

##### F12
- Access A: Demo.Web.Cases.MinimalApiReadWrite.FeatureFlagEndpoints.Set(FeatureFlags, string) performs write at Demo.Web/Cases/MinimalApiReadWrite.cs:12 under root MapPost handler Demo.Web.Cases.MinimalApiReadWrite.MinimalApiReadWriteCase.MapMinimalApiReadWrite(WebApplication)#map1; holds no protection.
- Access B: Demo.Web.Cases.MinimalApiReadWrite.FeatureFlagEndpoints.Get(FeatureFlags) performs read at Demo.Web/Cases/MinimalApiReadWrite.cs:14 under root MapGet handler Demo.Web.Cases.MinimalApiReadWrite.MinimalApiReadWriteCase.MapMinimalApiReadWrite(WebApplication)#map2; holds no protection.
- Code path A: MapPost handler Demo.Web.Cases.MinimalApiReadWrite.MinimalApiReadWriteCase.MapMinimalApiReadWrite(WebApplication)#map1 starts (Demo.Web/Cases/MinimalApiReadWrite.cs:12) → write Demo.Web.Cases.MinimalApiReadWrite.FeatureFlags.Mode (Demo.Web/Cases/MinimalApiReadWrite.cs:12)
- Code path B: MapGet handler Demo.Web.Cases.MinimalApiReadWrite.MinimalApiReadWriteCase.MapMinimalApiReadWrite(WebApplication)#map2 starts (Demo.Web/Cases/MinimalApiReadWrite.cs:14) → read Demo.Web.Cases.MinimalApiReadWrite.FeatureFlags.Mode (Demo.Web/Cases/MinimalApiReadWrite.cs:14)
- Resource: Demo.Web · di:Demo.Web.Cases.MinimalApiReadWrite.FeatureFlags@Singleton · Mode · scope Demo.Web · shared as process:Demo.Web.Cases.MinimalApiReadWrite.FeatureFlags
- Binding evidence: AddSingleton registers Demo.Web.Cases.MinimalApiReadWrite.FeatureFlags at Demo.Web/Cases/MinimalApiReadWrite.cs:22
- Overlap: The roots MapPost handler Demo.Web.Cases.MinimalApiReadWrite.MinimalApiReadWriteCase.MapMinimalApiReadWrite(WebApplication)#map1 and MapGet handler Demo.Web.Cases.MinimalApiReadWrite.MinimalApiReadWriteCase.MapMinimalApiReadWrite(WebApplication)#map2 may run concurrently in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: Repeated/MayOverlap in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: A writes `Mode`; B reads `Mode` at the same time; B observes either the old or the new value
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F12.A, F12.B, F12.R, F12.O, F12.P, F12.S

#### G11 · DCA1001 · Label on di:Demo.Web.Cases.PocoControllerSelfOverlap.ShelfState@Singleton (1 findings)
##### What can be lost
Two overlapping `Put` requests can each assign `Label` on the shared `ShelfState` instance, and only the later write survives [E:F13.S] [E:F13.R].

##### Why the accesses overlap
The `Put` action root may run concurrently with itself for different requests [E:F13.O].

##### Why the protection is insufficient
Both writes hold no evidenced shared guard around `Label` [E:F13.P].

##### Interleaving
One write at `PocoControllerSelfOverlap.cs:19` can complete after another write to `Label` but before the first value is used, leaving only the later writer [E:F13.A] [E:F13.B] [E:F13.S].

##### Evidence mode
This `DCA1001` conclusion uses deterministic static evidence for the `ShelfState` region [E:F13.R].

##### Uncertainty
Path feasibility is not established; confirm concurrent Put traffic can reach this action in the same deployment [E:F13.S].

##### Remediation
- Guard every write to `Label` with the same `lock`; verify manually [E:F13.P].
  - Check: confirm each `Put` path acquires that guard before assigning `Label` at `PocoControllerSelfOverlap.cs:19` [E:F13.A] [E:F13.B].
- Narrow `ShelfState` ownership or publish `Label` atomically; verify manually [E:F13.R].
  - Check: confirm overlapping Put requests no longer share an unprotected `Label` slot [E:F13.O] [E:F13.S].

##### F13
- Access A: Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController.Put(string) performs write at Demo.Web/Cases/PocoControllerSelfOverlap.cs:19 under root action Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController.Put(string) of controller Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController; holds no protection.
- Access B: Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController.Put(string) performs write at Demo.Web/Cases/PocoControllerSelfOverlap.cs:19 under root action Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController.Put(string) of controller Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController; holds no protection.
- Code path A: action Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController.Put(string) of controller Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController starts (Demo.Web/Cases/PocoControllerSelfOverlap.cs:19) → write Demo.Web.Cases.PocoControllerSelfOverlap.ShelfState.Label (Demo.Web/Cases/PocoControllerSelfOverlap.cs:19)
- Code path B: action Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController.Put(string) of controller Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController starts (Demo.Web/Cases/PocoControllerSelfOverlap.cs:19) → write Demo.Web.Cases.PocoControllerSelfOverlap.ShelfState.Label (Demo.Web/Cases/PocoControllerSelfOverlap.cs:19)
- Resource: Demo.Web · di:Demo.Web.Cases.PocoControllerSelfOverlap.ShelfState@Singleton · Label · scope Demo.Web · shared as process:Demo.Web.Cases.PocoControllerSelfOverlap.ShelfState
- Binding evidence: Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController._state holds constructor parameter state at Demo.Web/Cases/PocoControllerSelfOverlap.cs:16; AddSingleton registers Demo.Web.Cases.PocoControllerSelfOverlap.ShelfState at Demo.Web/Cases/PocoControllerSelfOverlap.cs:25
- Overlap: The root action Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController.Put(string) of controller Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController may run concurrently with itself in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: Repeated/MayOverlap in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: A writes `Label`; B writes `Label` before A's value is used; One of the two writes is lost
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F13.A, F13.B, F13.R, F13.O, F13.P, F13.S

#### G12 · DCA1001 · Owner on di:Demo.Web.Cases.PrimaryConstructorInjection.QuotaState@Singleton (1 findings)
##### What can be lost
Two overlapping `Post` requests can each assign `Owner` on the shared `QuotaState` instance, and only the later write survives [E:F14.S] [E:F14.R].

##### Why the accesses overlap
The `Post` action root may run concurrently with itself for different requests [E:F14.O].

##### Why the protection is insufficient
Both writes hold no evidenced shared guard around `Owner` [E:F14.P].

##### Interleaving
One write at `PrimaryConstructorInjection.cs:16` can complete after another write to `Owner` but before the first value is used, leaving only the later writer [E:F14.A] [E:F14.B] [E:F14.S].

##### Evidence mode
This `DCA1001` conclusion uses deterministic static evidence for the `QuotaState` region [E:F14.R].

##### Uncertainty
Path feasibility is not established; confirm concurrent Post traffic can reach this action in the same deployment [E:F14.S].

##### Remediation
- Guard every write to `Owner` with the same `lock`; verify manually [E:F14.P].
  - Check: confirm each `Post` path acquires that guard before assigning `Owner` at `PrimaryConstructorInjection.cs:16` [E:F14.A] [E:F14.B].
- Narrow `QuotaState` ownership or publish `Owner` atomically; verify manually [E:F14.R].
  - Check: confirm overlapping Post requests no longer share an unprotected `Owner` slot [E:F14.O] [E:F14.S].

##### F14
- Access A: Demo.Web.Cases.PrimaryConstructorInjection.QuotaController.Post(string) performs write at Demo.Web/Cases/PrimaryConstructorInjection.cs:16 under root action Demo.Web.Cases.PrimaryConstructorInjection.QuotaController.Post(string) of controller Demo.Web.Cases.PrimaryConstructorInjection.QuotaController; holds no protection.
- Access B: Demo.Web.Cases.PrimaryConstructorInjection.QuotaController.Post(string) performs write at Demo.Web/Cases/PrimaryConstructorInjection.cs:16 under root action Demo.Web.Cases.PrimaryConstructorInjection.QuotaController.Post(string) of controller Demo.Web.Cases.PrimaryConstructorInjection.QuotaController; holds no protection.
- Code path A: action Demo.Web.Cases.PrimaryConstructorInjection.QuotaController.Post(string) of controller Demo.Web.Cases.PrimaryConstructorInjection.QuotaController starts (Demo.Web/Cases/PrimaryConstructorInjection.cs:16) → write Demo.Web.Cases.PrimaryConstructorInjection.QuotaState.Owner (Demo.Web/Cases/PrimaryConstructorInjection.cs:16)
- Code path B: action Demo.Web.Cases.PrimaryConstructorInjection.QuotaController.Post(string) of controller Demo.Web.Cases.PrimaryConstructorInjection.QuotaController starts (Demo.Web/Cases/PrimaryConstructorInjection.cs:16) → write Demo.Web.Cases.PrimaryConstructorInjection.QuotaState.Owner (Demo.Web/Cases/PrimaryConstructorInjection.cs:16)
- Resource: Demo.Web · di:Demo.Web.Cases.PrimaryConstructorInjection.QuotaState@Singleton · Owner · scope Demo.Web · shared as process:Demo.Web.Cases.PrimaryConstructorInjection.QuotaState
- Binding evidence: Demo.Web.Cases.PrimaryConstructorInjection.QuotaController.quota holds constructor parameter quota at Demo.Web/Cases/PrimaryConstructorInjection.cs:13; AddSingleton registers Demo.Web.Cases.PrimaryConstructorInjection.QuotaState at Demo.Web/Cases/PrimaryConstructorInjection.cs:22
- Overlap: The root action Demo.Web.Cases.PrimaryConstructorInjection.QuotaController.Post(string) of controller Demo.Web.Cases.PrimaryConstructorInjection.QuotaController may run concurrently with itself in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: Repeated/MayOverlap in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: A writes `Owner`; B writes `Owner` before A's value is used; One of the two writes is lost
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F14.A, F14.B, F14.R, F14.O, F14.P, F14.S

#### G13 · DCA1002 · Hits on di:Demo.Web.Cases.RmwSingletonCounter.HitStats@Singleton (1 findings)
##### What can be lost
A concurrent increment of `Hits` on the shared `HitStats` instance can drop an update: one `Post` finishes its write-back from a stale read and erases the other request's update [E:F15.S].

##### Why the accesses overlap
`Post` can serve overlapping requests against the same DI singleton [E:F15.O].

##### Why the protection is insufficient
Neither RMW side holds a common guard around `Hits`, so the read-compute-write is not atomic across requests [E:F15.P].

##### Interleaving
At `RmwSingletonCounter.cs:20`, one request reads `Hits`, another completes its increment, then the first writes back from its stale value and overwrites the peer update [E:F15.A] [E:F15.B] [E:F15.S].

##### Evidence mode
This `DCA1002` conclusion rests on deterministic static evidence for the DI-bound `HitStats` field [E:F15.R].

##### Uncertainty
Path feasibility is not analyzed here; confirm concurrent `Post` traffic can hit this endpoint in one process [E:F15.O] [E:F15.S].

##### Remediation
- Replace the non-atomic `Hits` increment with `Interlocked` or one shared `lock` around the RMW; verify manually [E:F15.P].
  - Check: every `Post` path updates `Hits` only through that atomic or locked RMW at `RmwSingletonCounter.cs:20` [E:F15.A] [E:F15.B].

##### F15
- Access A: Demo.Web.Cases.RmwSingletonCounter.HitsController.Post() performs read-modify-write at Demo.Web/Cases/RmwSingletonCounter.cs:20 under root action Demo.Web.Cases.RmwSingletonCounter.HitsController.Post() of controller Demo.Web.Cases.RmwSingletonCounter.HitsController; holds no protection.
- Access B: Demo.Web.Cases.RmwSingletonCounter.HitsController.Post() performs read-modify-write at Demo.Web/Cases/RmwSingletonCounter.cs:20 under root action Demo.Web.Cases.RmwSingletonCounter.HitsController.Post() of controller Demo.Web.Cases.RmwSingletonCounter.HitsController; holds no protection.
- Code path A: action Demo.Web.Cases.RmwSingletonCounter.HitsController.Post() of controller Demo.Web.Cases.RmwSingletonCounter.HitsController starts (Demo.Web/Cases/RmwSingletonCounter.cs:20) → read-modify-write Demo.Web.Cases.RmwSingletonCounter.HitStats.Hits (Demo.Web/Cases/RmwSingletonCounter.cs:20)
- Code path B: action Demo.Web.Cases.RmwSingletonCounter.HitsController.Post() of controller Demo.Web.Cases.RmwSingletonCounter.HitsController starts (Demo.Web/Cases/RmwSingletonCounter.cs:20) → read-modify-write Demo.Web.Cases.RmwSingletonCounter.HitStats.Hits (Demo.Web/Cases/RmwSingletonCounter.cs:20)
- Resource: Demo.Web · di:Demo.Web.Cases.RmwSingletonCounter.HitStats@Singleton · Hits · scope Demo.Web · shared as process:Demo.Web.Cases.RmwSingletonCounter.HitStats
- Binding evidence: Demo.Web.Cases.RmwSingletonCounter.HitsController._stats holds constructor parameter stats at Demo.Web/Cases/RmwSingletonCounter.cs:17; AddSingleton registers Demo.Web.Cases.RmwSingletonCounter.HitStats at Demo.Web/Cases/RmwSingletonCounter.cs:26
- Overlap: The root action Demo.Web.Cases.RmwSingletonCounter.HitsController.Post() of controller Demo.Web.Cases.RmwSingletonCounter.HitsController may run concurrently with itself in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: Repeated/MayOverlap in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: A reads `Hits`; B writes `Hits`; A writes a value computed from its stale read, overwriting B's update
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F15.A, F15.B, F15.R, F15.O, F15.P, F15.S

#### G14 · DCA1001 · LastBeat on static:Demo.Web.Cases.StaticFieldHttpVsWorker.Heartbeat (1 findings)
##### What can be lost
A concurrent `Get` of `LastBeat` can observe either the prior value or the worker's new write, with no stable publication between them [E:F16.S].

##### Why the accesses overlap
`Get` and `ExecuteAsync` can run at the same time in the host [E:F16.O].

##### Why the protection is insufficient
The unprotected static read and write share no evidenced guard around `LastBeat` [E:F16.P].

##### Interleaving
While `ExecuteAsync` stores `LastBeat` at `StaticFieldHttpVsWorker.cs:16`, a simultaneous `Get` at `StaticFieldHttpVsWorker.cs:26` can sample the old or new value [E:F16.A] [E:F16.B] [E:F16.S].

##### Evidence mode
This `DCA1001` conclusion uses deterministic static evidence for the shared `Heartbeat` region [E:F16.R].

##### Uncertainty
Confirm the hosted worker and HTTP `Get` can overlap in the deployed process; reachability is not proven here [E:F16.O] [E:F16.S].

##### Remediation
- Publish and read `LastBeat` with `Volatile` or `Interlocked`, or guard both sides with one `lock`; verify manually [E:F16.P].
  - Check: `Get` and `ExecuteAsync` both use that same publication protocol for `LastBeat` at `StaticFieldHttpVsWorker.cs:26` and `StaticFieldHttpVsWorker.cs:16` [E:F16.A] [E:F16.B].

##### F16
- Access A: Demo.Web.Cases.StaticFieldHttpVsWorker.HeartbeatController.Get() performs read at Demo.Web/Cases/StaticFieldHttpVsWorker.cs:26 under root action Demo.Web.Cases.StaticFieldHttpVsWorker.HeartbeatController.Get() of controller Demo.Web.Cases.StaticFieldHttpVsWorker.HeartbeatController; holds no protection.
- Access B: Demo.Web.Cases.StaticFieldHttpVsWorker.HeartbeatWorker.ExecuteAsync(CancellationToken) performs write at Demo.Web/Cases/StaticFieldHttpVsWorker.cs:16 under root execute of hosted service Demo.Web.Cases.StaticFieldHttpVsWorker.HeartbeatWorker; holds no protection.
- Code path A: action Demo.Web.Cases.StaticFieldHttpVsWorker.HeartbeatController.Get() of controller Demo.Web.Cases.StaticFieldHttpVsWorker.HeartbeatController starts (Demo.Web/Cases/StaticFieldHttpVsWorker.cs:26) → read Demo.Web.Cases.StaticFieldHttpVsWorker.Heartbeat.LastBeat (Demo.Web/Cases/StaticFieldHttpVsWorker.cs:26)
- Code path B: execute of hosted service Demo.Web.Cases.StaticFieldHttpVsWorker.HeartbeatWorker starts (Demo.Web/Cases/StaticFieldHttpVsWorker.cs:14) → write Demo.Web.Cases.StaticFieldHttpVsWorker.Heartbeat.LastBeat (Demo.Web/Cases/StaticFieldHttpVsWorker.cs:16)
- Resource: Demo.Web · static:Demo.Web.Cases.StaticFieldHttpVsWorker.Heartbeat · LastBeat · scope Demo.Web · shared as process
- Binding evidence: none
- Overlap: The roots action Demo.Web.Cases.StaticFieldHttpVsWorker.HeartbeatController.Get() of controller Demo.Web.Cases.StaticFieldHttpVsWorker.HeartbeatController and execute of hosted service Demo.Web.Cases.StaticFieldHttpVsWorker.HeartbeatWorker may run concurrently in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: AtMostOnce/Serialized in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: B writes `LastBeat`; A reads `LastBeat` at the same time; A observes either the old or the new value
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F16.A, F16.B, F16.R, F16.O, F16.P, F16.S

#### G15 · DCA1001 · _lastVisitor on static:Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController (2 findings)
##### What can be lost
A concurrent `Get` can see either the old or new `_lastVisitor`, and two concurrent `Post` writes can overwrite each other so one visitor name never sticks [E:F17.S] [E:F18.S].

##### Why the accesses overlap
`Get` and `Post` on `LastVisitorController` can run for different requests at once, and `Post` can also self-overlap [E:F17.O] [E:F18.O].

##### Why the protection is insufficient
Neither the read/write nor the write/write pairing shows a shared guard around `_lastVisitor` [E:F17.P] [E:F18.P].

##### Interleaving
A write at `StaticFieldUnlockedReadWrite.cs:14` can race a read at `StaticFieldUnlockedReadWrite.cs:17`, so the reader sees timing-dependent values [E:F17.A] [E:F17.B] [E:F17.S]. Two `Post` stores at `StaticFieldUnlockedReadWrite.cs:14` can land so the later write replaces the earlier before it is used [E:F18.A] [E:F18.B] [E:F18.S].

##### Evidence mode
This `DCA1001` group uses deterministic static evidence for the shared `LastVisitorController` static field [E:F17.R] [E:F18.R].

##### Uncertainty
Path feasibility is not established, so confirm concurrent GET and POST traffic can reach this controller in one process [E:F17.S] [E:F18.S].

##### Remediation
- Guard every access to `_lastVisitor` with the same `lock`; verify manually [E:F17.P] [E:F18.P].
  - Check: `Post` and `Get` both take that guard before touching `_lastVisitor` at `StaticFieldUnlockedReadWrite.cs:14` and `StaticFieldUnlockedReadWrite.cs:17` [E:F17.A] [E:F17.B] [E:F18.A] [E:F18.B].
- Or stop sharing mutable static visitor state across requests; verify manually [E:F17.R] [E:F18.R].
  - Check: after the change, concurrent `Post` and `Get` no longer race on one unprotected `_lastVisitor` slot [E:F17.S] [E:F18.S].

##### F17
- Access A: Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Get() performs read at Demo.Web/Cases/StaticFieldUnlockedReadWrite.cs:17 under root action Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Get() of controller Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController; holds no protection.
- Access B: Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Post(string) performs write at Demo.Web/Cases/StaticFieldUnlockedReadWrite.cs:14 under root action Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Post(string) of controller Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController; holds no protection.
- Code path A: action Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Get() of controller Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController starts (Demo.Web/Cases/StaticFieldUnlockedReadWrite.cs:17) → read Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController._lastVisitor (Demo.Web/Cases/StaticFieldUnlockedReadWrite.cs:17)
- Code path B: action Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Post(string) of controller Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController starts (Demo.Web/Cases/StaticFieldUnlockedReadWrite.cs:14) → write Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController._lastVisitor (Demo.Web/Cases/StaticFieldUnlockedReadWrite.cs:14)
- Resource: Demo.Web · static:Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController · _lastVisitor · scope Demo.Web · shared as process
- Binding evidence: none
- Overlap: The roots action Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Get() of controller Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController and action Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Post(string) of controller Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController may run concurrently in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: Repeated/MayOverlap in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: B writes `_lastVisitor`; A reads `_lastVisitor` at the same time; A observes either the old or the new value
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F17.A, F17.B, F17.R, F17.O, F17.P, F17.S

##### F18
- Access A: Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Post(string) performs write at Demo.Web/Cases/StaticFieldUnlockedReadWrite.cs:14 under root action Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Post(string) of controller Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController; holds no protection.
- Access B: Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Post(string) performs write at Demo.Web/Cases/StaticFieldUnlockedReadWrite.cs:14 under root action Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Post(string) of controller Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController; holds no protection.
- Code path A: action Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Post(string) of controller Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController starts (Demo.Web/Cases/StaticFieldUnlockedReadWrite.cs:14) → write Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController._lastVisitor (Demo.Web/Cases/StaticFieldUnlockedReadWrite.cs:14)
- Code path B: action Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Post(string) of controller Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController starts (Demo.Web/Cases/StaticFieldUnlockedReadWrite.cs:14) → write Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController._lastVisitor (Demo.Web/Cases/StaticFieldUnlockedReadWrite.cs:14)
- Resource: Demo.Web · static:Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController · _lastVisitor · scope Demo.Web · shared as process
- Binding evidence: none
- Overlap: The root action Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Post(string) of controller Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController may run concurrently with itself in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: Repeated/MayOverlap in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: A writes `_lastVisitor`; B writes `_lastVisitor` before A's value is used; One of the two writes is lost
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F18.A, F18.B, F18.R, F18.O, F18.P, F18.S

### Medium findings
_None._

### Low findings
_None._

### Suppressed findings
Suppressions are not analyzed in this version.

### Diagnostics
- Load: 2.7 s
- Analysis: 2.6 s
- Candidate pairs: 76
- Suppressed pairs: 6
- Skipped pairs: invocation 4, no-self-overlap 8, read-read 40
- Late responses: 0
