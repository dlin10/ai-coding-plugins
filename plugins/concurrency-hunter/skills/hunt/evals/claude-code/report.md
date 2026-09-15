# Concurrency Hunter report

| Field | Value |
| --- | --- |
| Status | CompleteWithFindings |
| Reasons | none |
| Target | C:\Dev\CodexPlugins\plugins\concurrency-hunter\demo\Demo.slnx |
| Run | 20260915-004613-6f6551 |
| Started | 2026-09-15T00:46:13.3009303+00:00 |
| Duration | 268.2 s |
| Version | concurrency-hunter 0.1.0 |
| IR schema | 1.0 |
| Providers | aspnetcore (Microsoft.AspNetCore.Mvc.Core 8.0.0.0..11.0.0.0, Microsoft.AspNetCore.Mvc 8.0.0.0..11.0.0.0, Microsoft.AspNetCore.Routing 8.0.0.0..11.0.0.0, Microsoft.AspNetCore.Http.Abstractions 8.0.0.0..11.0.0.0, Microsoft.Extensions.DependencyInjection.Abstractions 8.0.0.0..11.0.0.0); hosting (Microsoft.Extensions.Hosting.Abstractions 8.0.0.0..11.0.0.0) |

### Executive summary
The Demo.Web process has 15 High-confidence groups of unprotected shared state, and none of the accesses holds a guard. Most are process-wide singletons mutated by HTTP actions that run concurrently with themselves, so one request can silently overwrite another's `Theme`, `LastPath`, `Target`, `Mode`, `Label` or `Owner` [E:F2.R] [E:F2.O] [E:F4.S] [E:F7.S] [E:F11.S] [E:F13.S] [E:F14.S]. The `Profile` singleton is especially exposed: `Email` and `Name` are written as separate unguarded steps, so a stored profile can mix two callers' data [E:F9.S] [E:F10.S]. The one `DCA1002` group is a true lost update: concurrent increments of `Hits` undercount requests [E:F15.S]. Static fields behave the same way, including a field declared in a shared library and loaded into the web process [E:F1.R] [E:F16.R] [E:F17.R]. The remaining groups pair a request with a hosted service, or a hosted service's execute and stop methods, and are mostly startup or shutdown ordering hazards: a reader can see a missing, stale or half-built value [E:F3.O] [E:F6.S] [E:F8.O] [E:F16.S]. The fixes fall into three kinds: atomic publication or `Interlocked` updates, moving per-caller data out of singletons and statics, and explicit readiness ordering for hosted services.

### Coverage
- Projects loaded: 4 of 4
- Missing projects: none
- Execution roots: 54
- Process scope Demo.Web: executable Demo.Web; projects Demo.Application, Demo.Domain, Demo.Web
  - Roots per provider: aspnetcore 35, hosting 18
  - Diagnostics from aspnetcore: none
  - Diagnostics from hosting: none
  - Registrations: 42
  - DI diagnostics: UnresolvedBinding Demo.Web.Cases.AmbiguousRegistrationScopedWins.CartDraft: Demo.Web.Cases.AmbiguousRegistrationScopedWins.CartDraft is registered with conflicting implementations or lifetimes (Demo.Web.Cases.AmbiguousRegistrationScopedWins.CartDraft@Scoped at Demo.Web/Cases/AmbiguousRegistrationScopedWins.cs:26, Demo.Web.Cases.AmbiguousRegistrationScopedWins.CartDraft@Singleton at Demo.Web/Cases/AmbiguousRegistrationScopedWins.cs:26); it binds nothing.
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
Two POST requests can each store a sync source in the process-wide `LastSync` type. The later store silently replaces the earlier one before anything has used it, so one caller's `Source` disappears with no error or trace [E:F1.S] [E:F1.R].

##### Why the accesses overlap
There is no second code path here. The same action, `SyncController.Post`, collides with itself: ASP.NET Core serves requests in parallel, so two invocations can run at the same moment in the Demo.Web process [E:F1.O]. The field is declared in the shared Demo.Domain library, but Demo.Web loads that library into its own process, so both requests see one static object [E:F1.R].

##### Why the protection is insufficient
Neither invocation holds any guard. The assignment at `SharedLibraryStatic.cs:12` is a bare write to a public `static` field, and nothing orders or excludes concurrent writers [E:F1.P] [E:F1.A] [E:F1.B].

##### Interleaving
Request 1 writes its value into `Source` [E:F1.A]. Before any code reads that value, request 2 runs the same line and overwrites it [E:F1.B]. Whoever reads next sees only request 2's source, as if request 1 never ran [E:F1.S].

##### Evidence mode
This `DCA1001` conclusion comes from deterministic static analysis of the syntax tree. The field is static, so no dependency injection binding is needed to show that both accesses reach the same object [E:F1.R].

##### Uncertainty
The analysis does not show that any code reads `Source` or cares which request wrote last. If "last writer wins" is the intended behavior, the practical impact may be small. It also cannot see whether a deployment funnels these requests through a single worker [E:F1.S] [E:F1.O].

##### Remediation
- Synchronization: if other code reads the value and then acts on it, wrap every read and write of `Source` in one process-wide `lock` held on a `static` `readonly` object declared next to the field; verify manually [E:F1.P].
  - Check: confirm that every access to `LastSync` goes through that single guard object, including accesses outside `SyncController.Post` [E:F1.A] [E:F1.B].
- Ownership: replace the public static field with a singleton service that owns the value. Record each sync with its caller instead of keeping only one global last value, so no request's source is overwritten; verify manually [E:F1.R].
  - Check: confirm that no code still assigns the static field directly after the change, and that each request's source is still observable [E:F1.S].
- Atomicity: if only the latest value matters, publish it with `Volatile.Write` or `Interlocked.Exchange`, so readers always see a fully published reference and the last-writer-wins rule is intentional; verify manually [E:F1.P].
  - Check: confirm with the product owner that dropping an earlier concurrent source is acceptable [E:F1.S].

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
`ThemeSettings` is registered as a singleton, so one instance serves every request. When two clients change the theme at once, one client's choice is silently discarded, even though both requests completed successfully [E:F2.S] [E:F2.R].

##### Why the accesses overlap
Only one action is involved, and it collides with itself: `ThemeController.Put` can serve two HTTP requests in parallel [E:F2.O]. Each request builds a new controller, but the container injects the same `ThemeSettings` instance into all of them, so the parallel calls share one object [E:F2.R].

##### Why the protection is insufficient
Neither side holds a guard around the property assignment at `ActionSelfOverlap.cs:20`. A new controller per request does not help, because the state lives in the shared singleton, not in the controller [E:F2.P] [E:F2.A] [E:F2.B].

##### Interleaving
Request 1 sets `Theme` [E:F2.A]. Request 2 sets `Theme` again before anything has used request 1's value [E:F2.B]. Every later reader sees request 2's theme, and request 1's update is lost without any signal [E:F2.S].

##### Evidence mode
This `DCA1001` conclusion comes from deterministic static analysis. The singleton registration and the constructor assignment together prove that both writes reach one instance [E:F2.R].

##### Uncertainty
No reader of `Theme` is evidenced, so the analysis cannot tell whether a lost write is noticeable or whether last-writer-wins is intended. It also does not know whether the theme was meant to be global or per user [E:F2.S] [E:F2.O].

##### Remediation
- Ownership: if the theme is really a per-user or per-tenant preference, store it per user, for example in a dictionary keyed by user, rather than in one singleton property; verify manually [E:F2.R].
  - Check: confirm that concurrent requests from different users no longer write to the same field [E:F2.A] [E:F2.B].
- Synchronization: if one global theme is intended, make `ThemeSettings` guard `Theme` with a private `lock` object and expose an update method that compares against the expected current value; verify manually [E:F2.P].
  - Check: confirm that `ThemeController.Put` and every other caller change the value only through the guarded method [E:F2.A].
- Atomicity: if last-writer-wins is acceptable, back the property with a field published through `Volatile.Write`, and document that concurrent updates collapse into one; verify manually [E:F2.S].
  - Check: confirm with the product owner that silently dropping one of two simultaneous theme changes is acceptable [E:F2.O].

##### F2
- Access A: Demo.Web.Cases.ActionSelfOverlap.ThemeController.Put(string) performs write at Demo.Web/Cases/ActionSelfOverlap.cs:20 under root action Demo.Web.Cases.ActionSelfOverlap.ThemeController.Put(string) of controller Demo.Web.Cases.ActionSelfOverlap.ThemeController; holds no protection.
- Access B: Demo.Web.Cases.ActionSelfOverlap.ThemeController.Put(string) performs write at Demo.Web/Cases/ActionSelfOverlap.cs:20 under root action Demo.Web.Cases.ActionSelfOverlap.ThemeController.Put(string) of controller Demo.Web.Cases.ActionSelfOverlap.ThemeController; holds no protection.
- Code path A: action Demo.Web.Cases.ActionSelfOverlap.ThemeController.Put(string) of controller Demo.Web.Cases.ActionSelfOverlap.ThemeController starts (Demo.Web/Cases/ActionSelfOverlap.cs:20) → write Demo.Web.Cases.ActionSelfOverlap.ThemeSettings.Theme (Demo.Web/Cases/ActionSelfOverlap.cs:20)
- Code path B: action Demo.Web.Cases.ActionSelfOverlap.ThemeController.Put(string) of controller Demo.Web.Cases.ActionSelfOverlap.ThemeController starts (Demo.Web/Cases/ActionSelfOverlap.cs:20) → write Demo.Web.Cases.ActionSelfOverlap.ThemeSettings.Theme (Demo.Web/Cases/ActionSelfOverlap.cs:20)
- Resource: Demo.Web · di:Demo.Web.Cases.ActionSelfOverlap.ThemeSettings@Singleton · Theme · scope Demo.Web · shared as process:Demo.Web:Demo.Web.Cases.ActionSelfOverlap.ThemeSettings
- Binding evidence: Demo.Web.Cases.ActionSelfOverlap.ThemeController._settings holds constructor parameter settings at Demo.Web/Cases/ActionSelfOverlap.cs:17; AddSingleton registers Demo.Web.Cases.ActionSelfOverlap.ThemeSettings at Demo.Web/Cases/ActionSelfOverlap.cs:26
- Overlap: The root action Demo.Web.Cases.ActionSelfOverlap.ThemeController.Put(string) of controller Demo.Web.Cases.ActionSelfOverlap.ThemeController may run concurrently with itself in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: Repeated/MayOverlap in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: A writes `Theme`; B writes `Theme` before A's value is used; One of the two writes is lost
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F2.A, F2.B, F2.R, F2.O, F2.P, F2.S

#### G3 · DCA1001 · _lastItem on di:Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker@Singleton (1 findings)
##### What can be lost
When the host shuts down, the progress worker's stop logic may report a stale or missing last item. It reads `_lastItem` while the execute path may still be updating it, so the reported progress may not match the work actually done [E:F3.S] [E:F3.R].

##### Why the accesses overlap
`ProgressWorker` is a hosted service. The host calls `ExecuteAsync` to start it and can call `StopAsync` at shutdown while execution is still in progress. These two lifecycle methods of one service instance can therefore run at the same time [E:F3.O] [E:F3.R].

##### Why the protection is insufficient
Neither the write in `ExecuteAsync` nor the read in `StopAsync` holds a guard. The field is not marked `volatile` either, so nothing controls which value the stop path sees [E:F3.P] [E:F3.A] [E:F3.B].

##### Interleaving
The worker is about to store a new item at `BackgroundStopReadsOwnField.cs:10` [E:F3.A]. At the same moment, shutdown begins and `StopAsync` reads the field at `BackgroundStopReadsOwnField.cs:16` [E:F3.B]. Depending on timing, the stop path reports the previous item, or no item at all, instead of the one just processed [E:F3.S].

##### Evidence mode
This `DCA1001` conclusion comes from deterministic static analysis. The hosted-service registration binds both lifecycle roots to the same `ProgressWorker` instance [E:F3.R].

##### Uncertainty
The analysis does not work out the relative order of `ExecuteAsync` and `StopAsync`. In this code, execution finishes synchronously, so in practice it may always complete before stop is called. The race becomes real once the execute path does asynchronous or looping work [E:F3.O] [E:F3.S].

##### Remediation
- Execution order: in `StopAsync`, cancel the service and await the completion of `ExecuteAsync` (the base `StopAsync` does this) before reading `_lastItem`, so the read happens after the last write; verify manually [E:F3.O].
  - Check: confirm that the call to report the item comes after the awaited base `StopAsync`, not before it [E:F3.B].
- Synchronization: if the stop path must read while execution may still be running, publish the value with `Volatile.Write` in `ExecuteAsync` and read it with `Volatile.Read` in `StopAsync`, or guard both sides with one private `lock`; verify manually [E:F3.P].
  - Check: confirm that both the write in `ExecuteAsync` and the read in `StopAsync` use the same mechanism [E:F3.A] [E:F3.B].

##### F3
- Access A: Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker.ExecuteAsync(CancellationToken) performs write at Demo.Web/Cases/BackgroundStopReadsOwnField.cs:10 under root execute of hosted service Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker; holds no protection.
- Access B: Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker.StopAsync(CancellationToken) performs read at Demo.Web/Cases/BackgroundStopReadsOwnField.cs:16 under root stop of hosted service Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker; holds no protection.
- Code path A: execute of hosted service Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker starts (Demo.Web/Cases/BackgroundStopReadsOwnField.cs:8) → write Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker._lastItem (Demo.Web/Cases/BackgroundStopReadsOwnField.cs:10)
- Code path B: stop of hosted service Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker starts (Demo.Web/Cases/BackgroundStopReadsOwnField.cs:14) → read Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker._lastItem (Demo.Web/Cases/BackgroundStopReadsOwnField.cs:16)
- Resource: Demo.Web · di:Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker@Singleton · _lastItem · scope Demo.Web · shared as process:Microsoft.Extensions.Hosting.Abstractions:Microsoft.Extensions.Hosting.IHostedService
- Binding evidence: AddHostedService registers Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker at Demo.Web/Cases/BackgroundStopReadsOwnField.cs:26
- Overlap: The roots execute of hosted service Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker and stop of hosted service Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker may run concurrently in scope Demo.Web. A: AtMostOnce/Serialized in Demo.Web; B: AtMostOnce/Serialized in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: A writes `_lastItem`; B reads `_lastItem` at the same time; B observes either the old or the new value
- Uncertainty: Path feasibility is not analyzed in this version. Ordering between lifecycle methods of one hosted service is not analyzed in this version.
- Confidence: High (85)
- Evidence: F3.A, F3.B, F3.R, F3.O, F3.P, F3.S

#### G4 · DCA1001 · LastPath on di:Demo.Web.Cases.DiSingletonControllerVsWorker.VisitorStats@Singleton (2 findings)
##### What can be lost
The shared `VisitorStats` object keeps a single `LastPath`, so it can only ever show one caller's path. When two posts overlap, one path overwrites the other before anyone reads it. The overwritten visit is lost without any error [E:F4.R] [E:F4.S].

##### Why the accesses overlap
The container makes one `VisitorStats` for the whole process, and every controller instance gets that same object through its constructor [E:F4.R]. `VisitsController.Post` serves each HTTP request separately, so two requests can run the same assignment at the same time [E:F4.O].

##### Why the protection is insufficient
Neither side holds a guard, and the assignment in `DiSingletonControllerVsWorker.cs:20` touches the shared property directly [E:F4.P]. Nothing makes one request wait for the other, and nothing marks the value as owned by one request.

##### Interleaving
Request A stores its path in `LastPath` [E:F4.A]. Before any reader sees A's value, request B stores its own path [E:F4.B]. A's value is gone, and a later reader sees only B's, even though both requests succeeded [E:F4.S].

##### Evidence mode
This `DCA1001` conclusion comes from static evidence: the singleton registration and constructor binding [E:F4.R], the self-overlap of the action root [E:F4.O], and the absence of any held lock [E:F4.P].

##### Uncertainty
The group reports two findings, but only one is listed here, so this narrative covers only the request-against-request pair [E:F4.S]. The source also has a background reset worker that clears the same property. Whether that pair matters depends on the worker running after startup, which this evidence does not establish. Whether a lost path matters also depends on how `LastPath` is used, which the analysis does not model [E:F4.S].

##### Remediation
- If `LastPath` really means "the most recent visit, whichever wins", make that explicit with an atomic `Volatile.Write` or `Interlocked.Exchange` over a backing field, and document that lost intermediate values are acceptable; verify manually [E:F4.P].
  - Check: confirm no caller expects to see its own path after `Post` returns, and that every write to the backing field goes through the atomic call [E:F4.A] [E:F4.B].
- If every visit matters, replace the single property with an append-only `ConcurrentQueue` of paths, or a counter per path kept in a `ConcurrentDictionary`, owned by `VisitorStats`; verify manually [E:F4.R].
  - Check: confirm `VisitsController.Post` only adds entries, and that readers work from a snapshot rather than the live property [E:F4.S].
- If the value is only meaningful within one request, register the stats object with a scoped lifetime (AddScoped) instead of as a singleton; verify manually [E:F4.R].
  - Check: confirm no other singleton or hosted service depends on the object, since a scoped registration would break that dependency [E:F4.O].

##### F4
- Access A: Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController.Post(string) performs write at Demo.Web/Cases/DiSingletonControllerVsWorker.cs:20 under root action Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController.Post(string) of controller Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController; holds no protection.
- Access B: Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController.Post(string) performs write at Demo.Web/Cases/DiSingletonControllerVsWorker.cs:20 under root action Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController.Post(string) of controller Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController; holds no protection.
- Code path A: action Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController.Post(string) of controller Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController starts (Demo.Web/Cases/DiSingletonControllerVsWorker.cs:20) → write Demo.Web.Cases.DiSingletonControllerVsWorker.VisitorStats.LastPath (Demo.Web/Cases/DiSingletonControllerVsWorker.cs:20)
- Code path B: action Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController.Post(string) of controller Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController starts (Demo.Web/Cases/DiSingletonControllerVsWorker.cs:20) → write Demo.Web.Cases.DiSingletonControllerVsWorker.VisitorStats.LastPath (Demo.Web/Cases/DiSingletonControllerVsWorker.cs:20)
- Resource: Demo.Web · di:Demo.Web.Cases.DiSingletonControllerVsWorker.VisitorStats@Singleton · LastPath · scope Demo.Web · shared as process:Demo.Web:Demo.Web.Cases.DiSingletonControllerVsWorker.VisitorStats
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
- Resource: Demo.Web · di:Demo.Web.Cases.DiSingletonControllerVsWorker.VisitorStats@Singleton · LastPath · scope Demo.Web · shared as process:Demo.Web:Demo.Web.Cases.DiSingletonControllerVsWorker.VisitorStats
- Binding evidence: Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController._stats holds constructor parameter stats at Demo.Web/Cases/DiSingletonControllerVsWorker.cs:17; AddSingleton registers Demo.Web.Cases.DiSingletonControllerVsWorker.VisitorStats at Demo.Web/Cases/DiSingletonControllerVsWorker.cs:39; Demo.Web.Cases.DiSingletonControllerVsWorker.StatsResetWorker._stats holds constructor parameter stats at Demo.Web/Cases/DiSingletonControllerVsWorker.cs:27
- Overlap: The roots action Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController.Post(string) of controller Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController and execute of hosted service Demo.Web.Cases.DiSingletonControllerVsWorker.StatsResetWorker may run concurrently in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: AtMostOnce/Serialized in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: A writes `LastPath`; B writes `LastPath` before A's value is used; One of the two writes is lost
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F5.A, F5.B, F5.R, F5.O, F5.P, F5.S

#### G5 · DCA1001 · Current on di:Demo.Web.Cases.EscapeIntoSingleton.DraftRegistry@Singleton (1 findings)
##### What can be lost
A request can read `Current` while the hosted service is publishing a new draft. The request gets either the old reference or the new one. It can also get a draft whose body has not been filled in yet, or nothing at all, in which case the null-forgiving dereference throws [E:F6.R] [E:F6.S].

##### Why the accesses overlap
One `DraftRegistry` is shared by the hosted service and every controller instance [E:F6.R]. `DraftWorker.ExecuteAsync` runs on the host's background path while `DraftsController.Get` serves requests, and nothing orders the two [E:F6.O].

##### Why the protection is insufficient
Neither the read in `EscapeIntoSingleton.cs:41` nor the write in `EscapeIntoSingleton.cs:26` holds a guard [E:F6.P]. Beyond the missing lock, the worker publishes the new object into `Current` before it finishes filling the object in. Even an atomic reference swap would still expose a half-built draft.

##### Interleaving
The worker creates a draft and assigns it to `Current` [E:F6.B]. At that moment a request in `Get` reads `Current` and dereferences it [E:F6.A]. The worker then sets the body, but the request has already returned without that content. If the request runs before the first assignment, it dereferences a null reference [E:F6.S].

##### Evidence mode
This `DCA1001` conclusion comes from static evidence: the singleton binding shared by both constructors [E:F6.R], the overlap between an action and a hosted service [E:F6.O], and the absence of held locks [E:F6.P].

##### Uncertainty
The hosted service here runs once at startup, so this race matters only while the application is warming up, unless the worker is later changed to publish repeatedly [E:F6.O] [E:F6.S]. The analysis does not model that the draft is incomplete when it is published, nor the null case. Both come from reading the source.

##### Remediation
- Fully build the draft before publishing it: set the body on a local object first, then assign `Current` as the last step, using `Volatile.Write` or `Interlocked.Exchange` so the reference is published atomically; verify manually [E:F6.B] [E:F6.P].
  - Check: confirm that in `DraftWorker.ExecuteAsync` no statement changes the draft after the assignment to `Current` [E:F6.B].
- Make the published draft immutable, with the body set once at construction and exposed read-only, so a reader can never see a partial object; verify manually [E:F6.R].
  - Check: confirm the draft type has no public setters left, and that `DraftsController.Get` only reads through the reference it captured [E:F6.A].
- Handle the case where nothing has been published yet: have `Get` capture `Current` once, return a not-ready result when it is null, and drop the null-forgiving operator; verify manually [E:F6.A] [E:F6.S].
  - Check: confirm a request that arrives before the worker has run gets a defined response instead of an exception [E:F6.O].

##### F6
- Access A: Demo.Web.Cases.EscapeIntoSingleton.DraftsController.Get() performs read at Demo.Web/Cases/EscapeIntoSingleton.cs:41 under root action Demo.Web.Cases.EscapeIntoSingleton.DraftsController.Get() of controller Demo.Web.Cases.EscapeIntoSingleton.DraftsController; holds no protection.
- Access B: Demo.Web.Cases.EscapeIntoSingleton.DraftWorker.ExecuteAsync(CancellationToken) performs write at Demo.Web/Cases/EscapeIntoSingleton.cs:26 under root execute of hosted service Demo.Web.Cases.EscapeIntoSingleton.DraftWorker; holds no protection.
- Code path A: action Demo.Web.Cases.EscapeIntoSingleton.DraftsController.Get() of controller Demo.Web.Cases.EscapeIntoSingleton.DraftsController starts (Demo.Web/Cases/EscapeIntoSingleton.cs:41) → read Demo.Web.Cases.EscapeIntoSingleton.DraftRegistry.Current (Demo.Web/Cases/EscapeIntoSingleton.cs:41)
- Code path B: execute of hosted service Demo.Web.Cases.EscapeIntoSingleton.DraftWorker starts (Demo.Web/Cases/EscapeIntoSingleton.cs:23) → write Demo.Web.Cases.EscapeIntoSingleton.DraftRegistry.Current (Demo.Web/Cases/EscapeIntoSingleton.cs:26)
- Resource: Demo.Web · di:Demo.Web.Cases.EscapeIntoSingleton.DraftRegistry@Singleton · Current · scope Demo.Web · shared as process:Demo.Web:Demo.Web.Cases.EscapeIntoSingleton.DraftRegistry
- Binding evidence: Demo.Web.Cases.EscapeIntoSingleton.DraftsController._registry holds constructor parameter registry at Demo.Web/Cases/EscapeIntoSingleton.cs:38; AddSingleton registers Demo.Web.Cases.EscapeIntoSingleton.DraftRegistry at Demo.Web/Cases/EscapeIntoSingleton.cs:47; Demo.Web.Cases.EscapeIntoSingleton.DraftWorker._registry holds constructor parameter registry at Demo.Web/Cases/EscapeIntoSingleton.cs:21
- Overlap: The roots action Demo.Web.Cases.EscapeIntoSingleton.DraftsController.Get() of controller Demo.Web.Cases.EscapeIntoSingleton.DraftsController and execute of hosted service Demo.Web.Cases.EscapeIntoSingleton.DraftWorker may run concurrently in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: AtMostOnce/Serialized in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: B writes `Current`; A reads `Current` at the same time; A observes either the old or the new value
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F6.A, F6.B, F6.R, F6.O, F6.P, F6.S

#### G6 · DCA1001 · Target on di:Demo.Web.Cases.FromServicesActionParameter.RelayState@Singleton (1 findings)
##### What can be lost
All requests share one `RelayState`, and `Target` holds only the latest assignment. When two posts overlap, one caller's target is overwritten before anything uses it, so a relay can go to the other caller's destination [E:F7.R] [E:F7.S].

##### Why the accesses overlap
The object is registered as a singleton and injected into the action method through a FromServices parameter instead of the constructor. The injection style does not change the lifetime: every request gets the same instance [E:F7.R]. `RelayController.Post` handles concurrent requests independently [E:F7.O].

##### Why the protection is insufficient
The assignment in `FromServicesActionParameter.cs:16` runs without any guard, on both sides [E:F7.P]. Because the value arrives as a method parameter, it looks like per-request data. Nothing in the code signals that it is process-wide.

##### Interleaving
Request A assigns its target to `Target` [E:F7.A]. Request B assigns a different target before A's value is used [E:F7.B]. Anything that later reads the state sees only B's destination, and A's intent is silently lost [E:F7.S].

##### Evidence mode
This `DCA1001` conclusion comes from static evidence: the singleton registration reaching the action parameter [E:F7.R], the action's overlap with itself [E:F7.O], and the absence of any held lock [E:F7.P].

##### Uncertainty
The action only writes. The finding matters when some other code reads `Target` and expects the value the current request set, and this evidence does not show such a reader [E:F7.S]. Whether the relay target is supposed to be shared configuration or per-request data has to be confirmed with the owners.

##### Remediation
- If the target belongs to each request, stop storing it in a singleton: pass the target through the call as a value, or register `RelayState` with a scoped lifetime (AddScoped); verify manually [E:F7.R].
  - Check: confirm every consumer of the target receives the value set by its own request, and that no singleton or hosted service depends on the object [E:F7.O].
- If the target really is shared configuration, keep the singleton but publish changes atomically with `Volatile.Write` or `Interlocked.Exchange`, and document that the last write wins; verify manually [E:F7.P].
  - Check: confirm readers capture `Target` once per operation rather than reading it several times while another `Post` may change it [E:F7.A] [E:F7.B].
- If a change and its use must happen as one step, wrap both in a single `lock`, or a `SemaphoreSlim` if awaits happen inside, owned by `RelayState`; verify manually [E:F7.S].
  - Check: confirm every access to `Target` goes through that one guard, and that no path writes the property directly [E:F7.A] [E:F7.B].

##### F7
- Access A: Demo.Web.Cases.FromServicesActionParameter.RelayController.Post(RelayState, string) performs write at Demo.Web/Cases/FromServicesActionParameter.cs:16 under root action Demo.Web.Cases.FromServicesActionParameter.RelayController.Post(RelayState, string) of controller Demo.Web.Cases.FromServicesActionParameter.RelayController; holds no protection.
- Access B: Demo.Web.Cases.FromServicesActionParameter.RelayController.Post(RelayState, string) performs write at Demo.Web/Cases/FromServicesActionParameter.cs:16 under root action Demo.Web.Cases.FromServicesActionParameter.RelayController.Post(RelayState, string) of controller Demo.Web.Cases.FromServicesActionParameter.RelayController; holds no protection.
- Code path A: action Demo.Web.Cases.FromServicesActionParameter.RelayController.Post(RelayState, string) of controller Demo.Web.Cases.FromServicesActionParameter.RelayController starts (Demo.Web/Cases/FromServicesActionParameter.cs:16) → write Demo.Web.Cases.FromServicesActionParameter.RelayState.Target (Demo.Web/Cases/FromServicesActionParameter.cs:16)
- Code path B: action Demo.Web.Cases.FromServicesActionParameter.RelayController.Post(RelayState, string) of controller Demo.Web.Cases.FromServicesActionParameter.RelayController starts (Demo.Web/Cases/FromServicesActionParameter.cs:16) → write Demo.Web.Cases.FromServicesActionParameter.RelayState.Target (Demo.Web/Cases/FromServicesActionParameter.cs:16)
- Resource: Demo.Web · di:Demo.Web.Cases.FromServicesActionParameter.RelayState@Singleton · Target · scope Demo.Web · shared as process:Demo.Web:Demo.Web.Cases.FromServicesActionParameter.RelayState
- Binding evidence: AddSingleton registers Demo.Web.Cases.FromServicesActionParameter.RelayState at Demo.Web/Cases/FromServicesActionParameter.cs:22
- Overlap: The root action Demo.Web.Cases.FromServicesActionParameter.RelayController.Post(RelayState, string) of controller Demo.Web.Cases.FromServicesActionParameter.RelayController may run concurrently with itself in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: Repeated/MayOverlap in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: A writes `Target`; B writes `Target` before A's value is used; One of the two writes is lost
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F7.A, F7.B, F7.R, F7.O, F7.P, F7.S

#### G7 · DCA1001 · Stage on di:Demo.Web.Cases.HostedStartVsAction.WarmupState@Singleton (1 findings)
##### What can be lost
Warmup progress is not lost, but it can be seen at the wrong moment. A request can read `Stage` on the shared `WarmupState` object while the hosted service is still starting. The action then returns the empty value from before warmup, or a value whose change has not yet reached the request thread [E:F8.S] [E:F8.R].

##### Why the accesses overlap
The action `WarmupController.Get` and the start of the hosted service, `WarmupService.StartAsync`, are two different roots in one web process [E:F8.O]. Both classes receive the same singleton through their constructors, so they touch one object [E:F8.R]. Nothing in the evidence stops the host from serving a request while startup is still running [E:F8.O].

##### Why the protection is insufficient
Neither side holds a guard [E:F8.P]. The write in `StartAsync` is a plain property set, and the read in `Get` is a plain property get [E:F8.A] [E:F8.B]. No `lock`, no `Volatile` access and no ordering handshake connects them.

##### Interleaving
1. A request reaches `HostedStartVsAction.cs:35` and reads `Stage` before startup has run [E:F8.A].
2. The hosted service then sets `Stage` at `HostedStartVsAction.cs:19` [E:F8.B].
3. That request has already returned the old value. With no memory barrier, a later request could in principle still see a stale value [E:F8.S].

##### Evidence mode
This `DCA1001` conclusion comes from deterministic static evidence. That evidence ties the singleton registration and both constructor assignments to the two accesses [E:F8.R] [E:F8.O].

##### Uncertainty
The analysis does not model when the host starts accepting requests compared with when hosted services start. If this host always finishes `StartAsync` before the server listens, this ordering cannot happen in production. It could still happen in tests or in a different hosting setup [E:F8.O] [E:F8.S].

##### Remediation
- Make the order explicit: have the action treat a missing stage as "not ready yet" and not as a valid state, or have it wait on a readiness signal that the hosted service completes. Verify manually that callers can handle the not-ready answer [E:F8.S].
  - Check: confirm that `WarmupController.Get` separates "not started" from "started" and never reports stale data as final [E:F8.A].
- If `Stage` keeps being written after startup, keep it in a backing field read and written through `Volatile` (or under one `lock`) so readers see the latest value. Verify manually that there are no other writers [E:F8.P].
  - Check: confirm that every read and write of `Stage` goes through the same `Volatile` or `lock` path [E:F8.A] [E:F8.B].
- Confirm when this host starts its hosted services compared with when it starts listening for requests, and verify manually that no request can arrive before `StartAsync` returns [E:F8.O].
  - Check: inspect the host startup code and add a test that sends a request during startup [E:F8.O].

##### F8
- Access A: Demo.Web.Cases.HostedStartVsAction.WarmupController.Get() performs read at Demo.Web/Cases/HostedStartVsAction.cs:35 under root action Demo.Web.Cases.HostedStartVsAction.WarmupController.Get() of controller Demo.Web.Cases.HostedStartVsAction.WarmupController; holds no protection.
- Access B: Demo.Web.Cases.HostedStartVsAction.WarmupService.StartAsync(CancellationToken) performs write at Demo.Web/Cases/HostedStartVsAction.cs:19 under root start of hosted service Demo.Web.Cases.HostedStartVsAction.WarmupService; holds no protection.
- Code path A: action Demo.Web.Cases.HostedStartVsAction.WarmupController.Get() of controller Demo.Web.Cases.HostedStartVsAction.WarmupController starts (Demo.Web/Cases/HostedStartVsAction.cs:35) → read Demo.Web.Cases.HostedStartVsAction.WarmupState.Stage (Demo.Web/Cases/HostedStartVsAction.cs:35)
- Code path B: start of hosted service Demo.Web.Cases.HostedStartVsAction.WarmupService starts (Demo.Web/Cases/HostedStartVsAction.cs:17) → write Demo.Web.Cases.HostedStartVsAction.WarmupState.Stage (Demo.Web/Cases/HostedStartVsAction.cs:19)
- Resource: Demo.Web · di:Demo.Web.Cases.HostedStartVsAction.WarmupState@Singleton · Stage · scope Demo.Web · shared as process:Demo.Web:Demo.Web.Cases.HostedStartVsAction.WarmupState
- Binding evidence: Demo.Web.Cases.HostedStartVsAction.WarmupController._state holds constructor parameter state at Demo.Web/Cases/HostedStartVsAction.cs:32; AddSingleton registers Demo.Web.Cases.HostedStartVsAction.WarmupState at Demo.Web/Cases/HostedStartVsAction.cs:41; Demo.Web.Cases.HostedStartVsAction.WarmupService._state holds constructor parameter state at Demo.Web/Cases/HostedStartVsAction.cs:15
- Overlap: The roots action Demo.Web.Cases.HostedStartVsAction.WarmupController.Get() of controller Demo.Web.Cases.HostedStartVsAction.WarmupController and start of hosted service Demo.Web.Cases.HostedStartVsAction.WarmupService may run concurrently in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: AtMostOnce/Serialized in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: B writes `Stage`; A reads `Stage` at the same time; A observes either the old or the new value
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F8.A, F8.B, F8.R, F8.O, F8.P, F8.S

#### G8 · DCA1001 · Email on di:Demo.Web.Cases.LambdaAndLocalFunction.Profile@Singleton (1 findings)
##### What can be lost
Two requests to `ProfileController.Update` can both write `Email` on the one shared `Profile` object. Only the last write survives, so one caller's email disappears without any error, even though that caller got a success response [E:F9.S] [E:F9.R].

##### Why the accesses overlap
Both accesses come from the same action, and that action can run for two requests at the same time [E:F9.O]. The object is a singleton, so every request's controller holds the same instance [E:F9.R].

##### Why the protection is insufficient
The write is unguarded [E:F9.P]. It sits inside a lambda within `Update`, which does not change the fact that it runs on the request thread against shared state. Nothing orders the two requests' writes [E:F9.A] [E:F9.B].

##### Interleaving
1. Request A runs the lambda at `LambdaAndLocalFunction.cs:24` and stores its `Email` [E:F9.A].
2. Before anything reads A's value, request B runs the same line and overwrites it [E:F9.B].
3. A's email is gone, and a later reader sees B's value [E:F9.S].

##### Evidence mode
This `DCA1001` conclusion comes from deterministic static evidence. That evidence traces the singleton registration and constructor injection to the write, which the analysis attributes to the enclosing action [E:F9.R] [E:F9.A].

##### Uncertainty
The analysis cannot tell whether one shared profile is intended, for example a single-user demo, or whether each request should own its profile. If only one client ever calls the action, the race does not occur in practice [E:F9.O] [E:F9.S].

##### Remediation
- Stop keeping per-caller data in a singleton. Give each user a profile keyed by identity in a backing store, or register the type per request if it is only request data. Verify manually what lifetime the application actually needs [E:F9.R].
  - Check: confirm that no other consumer depends on `Profile` being shared process-wide [E:F9.R].
- If one shared profile is intended, replace it as a whole: build a new immutable profile and publish it with `Interlocked.Exchange` or `Volatile.Write`, or set both properties under a single `lock`. Verify manually that readers take the same path [E:F9.P].
  - Check: confirm that every write and read of `Email` uses that one mechanism [E:F9.A] [E:F9.B].

##### F9
- Access A: Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) performs write at Demo.Web/Cases/LambdaAndLocalFunction.cs:24 under root action Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) of controller Demo.Web.Cases.LambdaAndLocalFunction.ProfileController; holds no protection.
- Access B: Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) performs write at Demo.Web/Cases/LambdaAndLocalFunction.cs:24 under root action Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) of controller Demo.Web.Cases.LambdaAndLocalFunction.ProfileController; holds no protection.
- Code path A: action Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) of controller Demo.Web.Cases.LambdaAndLocalFunction.ProfileController starts (Demo.Web/Cases/LambdaAndLocalFunction.cs:21) → write Demo.Web.Cases.LambdaAndLocalFunction.Profile.Email (Demo.Web/Cases/LambdaAndLocalFunction.cs:24)
- Code path B: action Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) of controller Demo.Web.Cases.LambdaAndLocalFunction.ProfileController starts (Demo.Web/Cases/LambdaAndLocalFunction.cs:21) → write Demo.Web.Cases.LambdaAndLocalFunction.Profile.Email (Demo.Web/Cases/LambdaAndLocalFunction.cs:24)
- Resource: Demo.Web · di:Demo.Web.Cases.LambdaAndLocalFunction.Profile@Singleton · Email · scope Demo.Web · shared as process:Demo.Web:Demo.Web.Cases.LambdaAndLocalFunction.Profile
- Binding evidence: Demo.Web.Cases.LambdaAndLocalFunction.ProfileController._profile holds constructor parameter profile at Demo.Web/Cases/LambdaAndLocalFunction.cs:18; AddSingleton registers Demo.Web.Cases.LambdaAndLocalFunction.Profile at Demo.Web/Cases/LambdaAndLocalFunction.cs:34
- Overlap: The root action Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) of controller Demo.Web.Cases.LambdaAndLocalFunction.ProfileController may run concurrently with itself in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: Repeated/MayOverlap in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: A writes `Email`; B writes `Email` before A's value is used; One of the two writes is lost
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F9.A, F9.B, F9.R, F9.O, F9.P, F9.S

#### G9 · DCA1001 · Name on di:Demo.Web.Cases.LambdaAndLocalFunction.Profile@Singleton (1 findings)
##### What can be lost
Concurrent calls to `ProfileController.Update` can overwrite each other's `Name` on the shared `Profile`, so one request's name is dropped without any error [E:F10.S] [E:F10.R]. The same thing happens to the email field in the same action. Name and email are also set as two separate steps, so the stored profile can end up with the name from one request and the email from another. That mixed record was never submitted by any caller [E:F10.S].

##### Why the accesses overlap
The action can serve two requests at once, and both requests reach the same singleton through their controllers [E:F10.O] [E:F10.R].

##### Why the protection is insufficient
The write inside the local function in `Update` holds no guard [E:F10.P]. Moving the assignment into a local function does not isolate it: it still changes the one process-wide object [E:F10.A] [E:F10.B].

##### Interleaving
1. Request A calls its local function and sets `Name` at `LambdaAndLocalFunction.cs:27` [E:F10.A].
2. Request B runs the same line before A's value is used, and replaces it [E:F10.B].
3. Depending on how the email writes interleave, the final profile holds B's name and A's email, or B's name and B's email. Either way, A's name is lost [E:F10.S].

##### Evidence mode
This `DCA1001` conclusion comes from deterministic static evidence. The analysis links the singleton binding to a write that it attributes to the action containing the local function [E:F10.R] [E:F10.A].

##### Uncertainty
The analysis does not know whether the profile is meant to be shared across users. It also does not show that two requests really arrive together in this deployment [E:F10.O] [E:F10.S].

##### Remediation
- Treat name and email as one unit: build a complete immutable profile for each request and publish it in one step with `Interlocked.Exchange`, or set both properties under one `lock`. Verify manually that no caller changes the properties one at a time [E:F10.P].
  - Check: confirm that `Name` is never written without its matching email in the same guarded step [E:F10.A] [E:F10.B].
- Reconsider the lifetime. Per-user data should not live in a process-wide singleton. Store it per identity, or register it per request. Verify manually which lifetime matches the intended behaviour [E:F10.R].
  - Check: confirm that a second concurrent request cannot change the profile another user just saved [E:F10.O].

##### F10
- Access A: Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) performs write at Demo.Web/Cases/LambdaAndLocalFunction.cs:27 under root action Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) of controller Demo.Web.Cases.LambdaAndLocalFunction.ProfileController; holds no protection.
- Access B: Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) performs write at Demo.Web/Cases/LambdaAndLocalFunction.cs:27 under root action Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) of controller Demo.Web.Cases.LambdaAndLocalFunction.ProfileController; holds no protection.
- Code path A: action Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) of controller Demo.Web.Cases.LambdaAndLocalFunction.ProfileController starts (Demo.Web/Cases/LambdaAndLocalFunction.cs:21) → write Demo.Web.Cases.LambdaAndLocalFunction.Profile.Name (Demo.Web/Cases/LambdaAndLocalFunction.cs:27)
- Code path B: action Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) of controller Demo.Web.Cases.LambdaAndLocalFunction.ProfileController starts (Demo.Web/Cases/LambdaAndLocalFunction.cs:21) → write Demo.Web.Cases.LambdaAndLocalFunction.Profile.Name (Demo.Web/Cases/LambdaAndLocalFunction.cs:27)
- Resource: Demo.Web · di:Demo.Web.Cases.LambdaAndLocalFunction.Profile@Singleton · Name · scope Demo.Web · shared as process:Demo.Web:Demo.Web.Cases.LambdaAndLocalFunction.Profile
- Binding evidence: Demo.Web.Cases.LambdaAndLocalFunction.ProfileController._profile holds constructor parameter profile at Demo.Web/Cases/LambdaAndLocalFunction.cs:18; AddSingleton registers Demo.Web.Cases.LambdaAndLocalFunction.Profile at Demo.Web/Cases/LambdaAndLocalFunction.cs:34
- Overlap: The root action Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string) of controller Demo.Web.Cases.LambdaAndLocalFunction.ProfileController may run concurrently with itself in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: Repeated/MayOverlap in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: A writes `Name`; B writes `Name` before A's value is used; One of the two writes is lost
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F10.A, F10.B, F10.R, F10.O, F10.P, F10.S

#### G10 · DCA1001 · Mode on di:Demo.Web.Cases.MinimalApiReadWrite.FeatureFlags@Singleton (2 findings)
##### What can be lost
Two POST requests can each set `Mode` on the one shared `FeatureFlags` object, and only the later value survives. The earlier caller's value disappears, possibly before anything reads it, and neither caller is told [E:F11.S] [E:F11.R].

##### Why the accesses overlap
The container registers `FeatureFlags` as one object for the whole process and hands that same object to every call of the POST handler. The handler is an ordinary endpoint, so two requests can run it at the same time, and both writes land on the same field [E:F11.O] [E:F11.R].

##### Why the protection is insufficient
`FeatureFlagEndpoints.Set` assigns the property directly. Neither side holds a `lock` or any other guard, and there is no atomic swap, so nothing puts the two writes in order or makes them exclusive [E:F11.P].

##### Interleaving
Request A enters `Set` and stores its mode at `MinimalApiReadWrite.cs:12` [E:F11.A]. Before any reader sees that value, request B runs the same line and stores its own mode [E:F11.B]. The field now holds B's value, and A's write left no trace [E:F11.S].

##### Evidence mode
This `DCA1001` conclusion comes from deterministic static evidence. The DI registration and handler-parameter binding place the singleton at both accesses, and the endpoint mapping supplies the self-overlap [E:F11.R] [E:F11.O].

##### Uncertainty
The analysis does not know what the flag means to the application. If last-writer-wins is the intended behaviour, this is benign. If callers expect their mode to take effect or be seen, it is a real defect [E:F11.S]. The group reports two findings, but only one is detailed here. The other pairing is not described, so review other accesses to `Mode`, such as the read endpoint, alongside it [E:F11.R].

##### Remediation
- Decide whether concurrent mode changes may silently overwrite each other; if not, serialize writes to `Mode` behind a single `lock` owned by `FeatureFlags` and verify manually [E:F11.P].
  - Check: every write and read of `Mode` goes through that one guard object, and the guard is one object per process [E:F11.A] [E:F11.B].
- If the flag must change as a whole unit, replace the mutable property with an immutable snapshot published through `Interlocked.Exchange` or `Volatile.Write`, and verify manually that no caller mutates the snapshot [E:F11.R].
  - Check: the setter endpoint builds a new snapshot instead of assigning a field on shared state [E:F11.A].
- If a mode belongs to a caller rather than to the process, move it out of the singleton into per-request or per-user storage, then verify manually that the singleton registration no longer carries writable state [E:F11.R] [E:F11.O].
  - Check: `Set` no longer writes to a process-wide object reachable from concurrent requests [E:F11.S].

##### F11
- Access A: Demo.Web.Cases.MinimalApiReadWrite.FeatureFlagEndpoints.Set(FeatureFlags, string) performs write at Demo.Web/Cases/MinimalApiReadWrite.cs:12 under root MapPost handler Demo.Web.Cases.MinimalApiReadWrite.MinimalApiReadWriteCase.MapMinimalApiReadWrite(WebApplication)#map1; holds no protection.
- Access B: Demo.Web.Cases.MinimalApiReadWrite.FeatureFlagEndpoints.Set(FeatureFlags, string) performs write at Demo.Web/Cases/MinimalApiReadWrite.cs:12 under root MapPost handler Demo.Web.Cases.MinimalApiReadWrite.MinimalApiReadWriteCase.MapMinimalApiReadWrite(WebApplication)#map1; holds no protection.
- Code path A: MapPost handler Demo.Web.Cases.MinimalApiReadWrite.MinimalApiReadWriteCase.MapMinimalApiReadWrite(WebApplication)#map1 starts (Demo.Web/Cases/MinimalApiReadWrite.cs:12) → write Demo.Web.Cases.MinimalApiReadWrite.FeatureFlags.Mode (Demo.Web/Cases/MinimalApiReadWrite.cs:12)
- Code path B: MapPost handler Demo.Web.Cases.MinimalApiReadWrite.MinimalApiReadWriteCase.MapMinimalApiReadWrite(WebApplication)#map1 starts (Demo.Web/Cases/MinimalApiReadWrite.cs:12) → write Demo.Web.Cases.MinimalApiReadWrite.FeatureFlags.Mode (Demo.Web/Cases/MinimalApiReadWrite.cs:12)
- Resource: Demo.Web · di:Demo.Web.Cases.MinimalApiReadWrite.FeatureFlags@Singleton · Mode · scope Demo.Web · shared as process:Demo.Web:Demo.Web.Cases.MinimalApiReadWrite.FeatureFlags
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
- Resource: Demo.Web · di:Demo.Web.Cases.MinimalApiReadWrite.FeatureFlags@Singleton · Mode · scope Demo.Web · shared as process:Demo.Web:Demo.Web.Cases.MinimalApiReadWrite.FeatureFlags
- Binding evidence: AddSingleton registers Demo.Web.Cases.MinimalApiReadWrite.FeatureFlags at Demo.Web/Cases/MinimalApiReadWrite.cs:22
- Overlap: The roots MapPost handler Demo.Web.Cases.MinimalApiReadWrite.MinimalApiReadWriteCase.MapMinimalApiReadWrite(WebApplication)#map1 and MapGet handler Demo.Web.Cases.MinimalApiReadWrite.MinimalApiReadWriteCase.MapMinimalApiReadWrite(WebApplication)#map2 may run concurrently in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: Repeated/MayOverlap in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: A writes `Mode`; B reads `Mode` at the same time; B observes either the old or the new value
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F12.A, F12.B, F12.R, F12.O, F12.P, F12.S

#### G11 · DCA1001 · Label on di:Demo.Web.Cases.PocoControllerSelfOverlap.ShelfState@Singleton (1 findings)
##### What can be lost
Two concurrent PUT requests can each assign `Label` on the one process-wide `ShelfState`. Whichever write lands second silently replaces the first, so one caller's label is never seen [E:F13.S] [E:F13.R].

##### Why the accesses overlap
Every request builds a new controller, but each one receives the same singleton `ShelfState` through its constructor and keeps it in a field. The PUT action can therefore run for two requests at the same time, and both instances write through to one shared object [E:F13.O] [E:F13.R]. The controller has no MVC base class, but that does not change the overlap. It is still routed and dispatched concurrently.

##### Why the protection is insufficient
`ShelfController.Put` writes the property with no `lock`, no atomic primitive and no ownership boundary. Neither side holds any guard [E:F13.P]. Because controller instances are per request, a lock on the controller itself would also exclude nothing.

##### Interleaving
Request A runs `Put` and stores its label at `PocoControllerSelfOverlap.cs:19` [E:F13.A]. Before anything reads that label, request B runs the same line and overwrites it [E:F13.B]. A's update is gone [E:F13.S].

##### Evidence mode
The `DCA1001` result rests on deterministic static evidence. The AddSingleton registration and the constructor assignment tie the shared object to the action, and the action root supplies the self-overlap [E:F13.R] [E:F13.O].

##### Uncertainty
The analysis does not establish whether overwriting a shelf label is acceptable business behaviour, or whether clients rely on reading back their own label [E:F13.S]. It also assumes the controller is actually discovered and routed in this deployment [E:F13.O].

##### Remediation
- Guard writes and reads of `Label` with one `lock` object owned by `ShelfState`, not by the controller, and verify manually that the guard is shared across requests [E:F13.P].
  - Check: the guard lives on the singleton, so two controller instances contend on the same object [E:F13.A] [E:F13.B].
- If a label must be replaced only when it has not changed underneath the caller, add a compare-and-set, for example `Interlocked.CompareExchange` on a reference holder or a version check, and verify manually that conflicting PUTs are rejected or retried [E:F13.S].
  - Check: a PUT that races another PUT either wins cleanly or reports a conflict, and never silently disappears [E:F13.A].
- If the label is really per shelf or per caller, move it into keyed storage such as `ConcurrentDictionary` or a persistent store instead of a single process-wide property, and verify manually that the singleton no longer holds one shared value [E:F13.R].
  - Check: `Put` no longer writes to a single field on `ShelfState` shared by all requests [E:F13.O].

##### F13
- Access A: Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController.Put(string) performs write at Demo.Web/Cases/PocoControllerSelfOverlap.cs:19 under root action Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController.Put(string) of controller Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController; holds no protection.
- Access B: Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController.Put(string) performs write at Demo.Web/Cases/PocoControllerSelfOverlap.cs:19 under root action Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController.Put(string) of controller Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController; holds no protection.
- Code path A: action Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController.Put(string) of controller Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController starts (Demo.Web/Cases/PocoControllerSelfOverlap.cs:19) → write Demo.Web.Cases.PocoControllerSelfOverlap.ShelfState.Label (Demo.Web/Cases/PocoControllerSelfOverlap.cs:19)
- Code path B: action Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController.Put(string) of controller Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController starts (Demo.Web/Cases/PocoControllerSelfOverlap.cs:19) → write Demo.Web.Cases.PocoControllerSelfOverlap.ShelfState.Label (Demo.Web/Cases/PocoControllerSelfOverlap.cs:19)
- Resource: Demo.Web · di:Demo.Web.Cases.PocoControllerSelfOverlap.ShelfState@Singleton · Label · scope Demo.Web · shared as process:Demo.Web:Demo.Web.Cases.PocoControllerSelfOverlap.ShelfState
- Binding evidence: Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController._state holds constructor parameter state at Demo.Web/Cases/PocoControllerSelfOverlap.cs:16; AddSingleton registers Demo.Web.Cases.PocoControllerSelfOverlap.ShelfState at Demo.Web/Cases/PocoControllerSelfOverlap.cs:25
- Overlap: The root action Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController.Put(string) of controller Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController may run concurrently with itself in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: Repeated/MayOverlap in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: A writes `Label`; B writes `Label` before A's value is used; One of the two writes is lost
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F13.A, F13.B, F13.R, F13.O, F13.P, F13.S

#### G12 · DCA1001 · Owner on di:Demo.Web.Cases.PrimaryConstructorInjection.QuotaState@Singleton (1 findings)
##### What can be lost
Two concurrent POST requests can each set `Owner` on the single `QuotaState` shared by the process. The later write wins and the earlier owner vanishes, so a quota can end up recorded against a caller other than the one who just claimed it [E:F14.S] [E:F14.R].

##### Why the accesses overlap
The controller takes `QuotaState` as a primary constructor parameter. The container supplies the same registered singleton to every per-request controller instance, so all requests share it. The POST action can serve two requests at once, and both reach the same field [E:F14.O] [E:F14.R]. Primary constructor syntax makes no difference: the captured parameter is simply a reference to shared state.

##### Why the protection is insufficient
`QuotaController.Post` assigns the property directly. Neither access holds a `lock` or any other synchronization, and the assignment is not coordinated with any read that decides ownership [E:F14.P].

##### Interleaving
Request A enters `Post` and writes its owner at `PrimaryConstructorInjection.cs:16` [E:F14.A]. Before that owner is used, request B executes the same assignment [E:F14.B]. A's claim is overwritten without notice [E:F14.S].

##### Evidence mode
This `DCA1001` finding is based on deterministic static evidence. The singleton registration and the primary constructor capture bind the object to the action, and the action root establishes that it overlaps itself [E:F14.R] [E:F14.O].

##### Uncertainty
The analysis does not show whether any code reads `Owner` to make a decision, or whether last-writer-wins ownership is acceptable. The severity depends on how ownership is used downstream [E:F14.S].

##### Remediation
- If ownership is a claim, make it atomic: take one `lock` on `QuotaState` around check-and-assign, or use `Interlocked.CompareExchange` so only the first claimant succeeds. Verify manually that a losing request is told it lost [E:F14.P] [E:F14.S].
  - Check: two simultaneous POSTs yield exactly one owner, and the other request gets an explicit failure or retry [E:F14.A] [E:F14.B].
- If the owner should simply be overwritable, still publish it through `Volatile.Write` or under a `lock` so readers see a consistent value, and verify manually that no caller assumes its own write persisted [E:F14.R].
  - Check: no code path reads `Owner` immediately after `Post` and trusts it to be its own value [E:F14.A].
- If quota ownership is really per tenant or per resource, move it out of the single process-wide property into keyed storage such as `ConcurrentDictionary` or the database, and verify manually that the singleton holds no single writable owner [E:F14.R] [E:F14.O].
  - Check: `Post` no longer writes to one shared field reachable from all concurrent requests [E:F14.S].

##### F14
- Access A: Demo.Web.Cases.PrimaryConstructorInjection.QuotaController.Post(string) performs write at Demo.Web/Cases/PrimaryConstructorInjection.cs:16 under root action Demo.Web.Cases.PrimaryConstructorInjection.QuotaController.Post(string) of controller Demo.Web.Cases.PrimaryConstructorInjection.QuotaController; holds no protection.
- Access B: Demo.Web.Cases.PrimaryConstructorInjection.QuotaController.Post(string) performs write at Demo.Web/Cases/PrimaryConstructorInjection.cs:16 under root action Demo.Web.Cases.PrimaryConstructorInjection.QuotaController.Post(string) of controller Demo.Web.Cases.PrimaryConstructorInjection.QuotaController; holds no protection.
- Code path A: action Demo.Web.Cases.PrimaryConstructorInjection.QuotaController.Post(string) of controller Demo.Web.Cases.PrimaryConstructorInjection.QuotaController starts (Demo.Web/Cases/PrimaryConstructorInjection.cs:16) → write Demo.Web.Cases.PrimaryConstructorInjection.QuotaState.Owner (Demo.Web/Cases/PrimaryConstructorInjection.cs:16)
- Code path B: action Demo.Web.Cases.PrimaryConstructorInjection.QuotaController.Post(string) of controller Demo.Web.Cases.PrimaryConstructorInjection.QuotaController starts (Demo.Web/Cases/PrimaryConstructorInjection.cs:16) → write Demo.Web.Cases.PrimaryConstructorInjection.QuotaState.Owner (Demo.Web/Cases/PrimaryConstructorInjection.cs:16)
- Resource: Demo.Web · di:Demo.Web.Cases.PrimaryConstructorInjection.QuotaState@Singleton · Owner · scope Demo.Web · shared as process:Demo.Web:Demo.Web.Cases.PrimaryConstructorInjection.QuotaState
- Binding evidence: Demo.Web.Cases.PrimaryConstructorInjection.QuotaController.quota holds constructor parameter quota at Demo.Web/Cases/PrimaryConstructorInjection.cs:13; AddSingleton registers Demo.Web.Cases.PrimaryConstructorInjection.QuotaState at Demo.Web/Cases/PrimaryConstructorInjection.cs:22
- Overlap: The root action Demo.Web.Cases.PrimaryConstructorInjection.QuotaController.Post(string) of controller Demo.Web.Cases.PrimaryConstructorInjection.QuotaController may run concurrently with itself in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: Repeated/MayOverlap in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: A writes `Owner`; B writes `Owner` before A's value is used; One of the two writes is lost
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F14.A, F14.B, F14.R, F14.O, F14.P, F14.S

#### G13 · DCA1002 · Hits on di:Demo.Web.Cases.RmwSingletonCounter.HitStats@Singleton (1 findings)
##### What can be lost
Concurrent POSTs to the hit counter can lose increments. `Hits` lives on one `HitStats` object that the whole process shares. When two requests increment it at the same time, both can start from the same count, so the final value is lower than the number of requests served [E:F15.R] [E:F15.S].

##### Why the accesses overlap
Both accesses come from the same action, `HitsController.Post`. ASP.NET Core runs one action for many requests at once on thread-pool threads, so the action overlaps with itself [E:F15.O]. Every controller instance gets the same injected singleton, so every request touches the same field [E:F15.R].

##### Why the protection is insufficient
Nothing guards the increment. Neither side holds a lock or uses an atomic primitive [E:F15.P]. The increment in `RmwSingletonCounter.cs:20` looks like one step, but it is three: read the property, add one, store the result. Another thread can run between any two of them [E:F15.A] [E:F15.B].

##### Interleaving
Request A reads `Hits` as 5. Request B reads 5 too, adds one and stores 6. A then stores 6, the value it computed from its old read. B's increment is gone: two requests ran and the counter moved by one [E:F15.A] [E:F15.B] [E:F15.S].

##### Evidence mode
This `DCA1002` finding comes from static analysis. It rests on the shared singleton, the read-modify-write in the action and the overlap of concurrent requests [E:F15.R] [E:F15.O]. No runtime observation was made.

##### Uncertainty
The analysis does not check whether real traffic makes these POSTs arrive together. How much is lost depends on load, and exact counts may not matter to the application [E:F15.S]. Nothing else in the code writes to `Hits`, so no other protection is missing.

##### Remediation
- Make the increment atomic: store the count in a plain int field and update it with `Interlocked.Increment`, reading it with `Interlocked.Read` or a plain read; verify manually [E:F15.P].
  - Check: confirm `HitsController.Post` no longer uses the auto-property increment and that every write to the counter goes through `Interlocked` [E:F15.A].
- If the counter later needs more than one step, such as a check before the update, wrap the whole update in a single `lock` held on a private readonly object inside `HitStats`; verify manually [E:F15.B].
  - Check: confirm no code outside that `lock` writes to `Hits`, and that the lock object is the same for every request [E:F15.R].

##### F15
- Access A: Demo.Web.Cases.RmwSingletonCounter.HitsController.Post() performs read-modify-write at Demo.Web/Cases/RmwSingletonCounter.cs:20 under root action Demo.Web.Cases.RmwSingletonCounter.HitsController.Post() of controller Demo.Web.Cases.RmwSingletonCounter.HitsController; holds no protection.
- Access B: Demo.Web.Cases.RmwSingletonCounter.HitsController.Post() performs read-modify-write at Demo.Web/Cases/RmwSingletonCounter.cs:20 under root action Demo.Web.Cases.RmwSingletonCounter.HitsController.Post() of controller Demo.Web.Cases.RmwSingletonCounter.HitsController; holds no protection.
- Code path A: action Demo.Web.Cases.RmwSingletonCounter.HitsController.Post() of controller Demo.Web.Cases.RmwSingletonCounter.HitsController starts (Demo.Web/Cases/RmwSingletonCounter.cs:20) → read-modify-write Demo.Web.Cases.RmwSingletonCounter.HitStats.Hits (Demo.Web/Cases/RmwSingletonCounter.cs:20)
- Code path B: action Demo.Web.Cases.RmwSingletonCounter.HitsController.Post() of controller Demo.Web.Cases.RmwSingletonCounter.HitsController starts (Demo.Web/Cases/RmwSingletonCounter.cs:20) → read-modify-write Demo.Web.Cases.RmwSingletonCounter.HitStats.Hits (Demo.Web/Cases/RmwSingletonCounter.cs:20)
- Resource: Demo.Web · di:Demo.Web.Cases.RmwSingletonCounter.HitStats@Singleton · Hits · scope Demo.Web · shared as process:Demo.Web:Demo.Web.Cases.RmwSingletonCounter.HitStats
- Binding evidence: Demo.Web.Cases.RmwSingletonCounter.HitsController._stats holds constructor parameter stats at Demo.Web/Cases/RmwSingletonCounter.cs:17; AddSingleton registers Demo.Web.Cases.RmwSingletonCounter.HitStats at Demo.Web/Cases/RmwSingletonCounter.cs:26
- Overlap: The root action Demo.Web.Cases.RmwSingletonCounter.HitsController.Post() of controller Demo.Web.Cases.RmwSingletonCounter.HitsController may run concurrently with itself in scope Demo.Web. A: Repeated/MayOverlap in Demo.Web; B: Repeated/MayOverlap in Demo.Web
- Protection: unprotected; A holds no protection; B holds no protection; common single-object protection: none
- Scenario: A reads `Hits`; B writes `Hits`; A writes a value computed from its stale read, overwriting B's update
- Uncertainty: Path feasibility is not analyzed in this version.
- Confidence: High (85)
- Evidence: F15.A, F15.B, F15.R, F15.O, F15.P, F15.S

#### G14 · DCA1001 · LastBeat on static:Demo.Web.Cases.StaticFieldHttpVsWorker.Heartbeat (1 findings)
##### What can be lost
A request can see a heartbeat value that is out of date. `LastBeat` is a static field on `Heartbeat`, so there is one copy for the whole process. The hosted worker writes it while HTTP requests read it, and nothing guarantees a request sees the worker's value [E:F16.R] [E:F16.S].

##### Why the accesses overlap
The read runs in `HeartbeatController.Get` and the write runs in `HeartbeatWorker.ExecuteAsync`. The host starts the background service and the HTTP server at about the same time, so requests can arrive while the worker is still running its first step [E:F16.O] [E:F16.A] [E:F16.B].

##### Why the protection is insufficient
Neither side uses a lock, `volatile` or any other synchronization [E:F16.P]. The field is a string reference, so it is written in one atomic step and a torn read cannot happen. The real risk is that the read can run before the write, or can miss a new value it should see.

##### Interleaving
A request reaches the action at `StaticFieldHttpVsWorker.cs:26` and reads `LastBeat` before the worker has run the assignment at `StaticFieldHttpVsWorker.cs:16`. The caller gets null instead of the worker's value. With no memory barrier, a later read on another core could in principle also miss the write [E:F16.A] [E:F16.B] [E:F16.S].

##### Evidence mode
This `DCA1001` finding comes from static analysis. The resource is a static field, so there is no DI binding to prove [E:F16.R]. The overlap is between an HTTP action and a hosted service's execute method [E:F16.O].

##### Uncertainty
The worker writes only once, at startup, so in practice this is mostly a startup ordering problem rather than a steady race. Whether a null heartbeat matters depends on the callers. The analysis does not model when the host starts the service relative to the first request [E:F16.S].

##### Remediation
- Publish the value with a memory barrier: write it with `Volatile.Write` in `ExecuteAsync` and read it with `Volatile.Read` in `Get`, or mark the field `volatile`; verify manually [E:F16.P].
  - Check: confirm both the write in `HeartbeatWorker.ExecuteAsync` and the read in `HeartbeatController.Get` go through the chosen primitive [E:F16.A] [E:F16.B].
- Fix the startup order: set the first heartbeat before the app starts accepting requests, for example in StartAsync or during startup, or have the action treat null as "not started yet"; verify manually [E:F16.O].
  - Check: confirm a request sent right after startup never returns an unexpected null from `HeartbeatController.Get` [E:F16.S].

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
Visitors' names can be dropped, and readers can see a name that is out of date. `_lastVisitor` is a static field on `LastVisitorController`, so all requests share one copy. When two POSTs arrive together, one name overwrites the other with no defined order, and a concurrent GET can return either one [E:F17.R] [E:F17.S].

##### Why the accesses overlap
Requests to `LastVisitorController.Get` and `LastVisitorController.Post` run in parallel on the thread pool. Because the field is static, every controller instance reads and writes the same memory [E:F17.O] [E:F17.A] [E:F17.B].

##### Why the protection is insufficient
Neither action holds a lock or uses a `Volatile` or `Interlocked` primitive [E:F17.P]. A single reference write is atomic, so the value is never torn. Still, nothing orders the writes, and nothing guarantees that a read sees the most recent one.

##### Interleaving
A GET reads the field at `StaticFieldUnlockedReadWrite.cs:17` while a POST assigns a new name at `StaticFieldUnlockedReadWrite.cs:14`. The GET returns the previous visitor or the new one, depending on timing. If two POSTs overlap, whichever stores last wins, and the other visitor's name is never observed [E:F17.A] [E:F17.B] [E:F17.S].

##### Evidence mode
This `DCA1001` finding comes from static analysis of a static field, so there is no DI binding to prove [E:F17.R]. The overlap is between two actions of the same controller [E:F17.O].

##### Uncertainty
The group reports two findings, but only this read-against-write finding was supplied. The second is probably the POST racing with itself, and it is not analyzed here. Whether "last visitor" needs to be exact, or tracked per user, is a product decision [E:F17.S].

##### Remediation
- Guard the field with one `lock` on a private `static` readonly object, used in both `Post` and `Get`. Alternatively, write it with `Volatile.Write` and read it with `Volatile.Read` if eventual visibility is enough; verify manually [E:F17.P].
  - Check: confirm every read and write of `_lastVisitor` in `LastVisitorController` goes through the same lock or primitive [E:F17.A] [E:F17.B].
- Reconsider ownership: if the last visitor should be per user or per session, move it out of static state into scoped or session storage so that requests stop sharing it; verify manually [E:F17.R].
  - Check: confirm `LastVisitorController` no longer declares `_lastVisitor` as `static`, and that concurrent users no longer see each other's names [E:F17.O].

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
- Load: 2.6 s
- Analysis: 2.5 s
- Candidate pairs: 76
- Suppressed pairs: 6
- Skipped pairs: invocation 4, no-self-overlap 8, read-read 40
- Late responses: 0
