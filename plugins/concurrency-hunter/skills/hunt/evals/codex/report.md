# Concurrency Hunter report

| Field | Value |
| --- | --- |
| Status | CompleteWithFindings |
| Reasons | none |
| Target | C:\Dev\CodexPlugins\plugins\concurrency-hunter\demo\Demo.slnx |
| Run | 20260915-004621-f962ce |
| Started | 2026-09-15T00:46:21.9896845+00:00 |
| Duration | 501.1 s |
| Version | concurrency-hunter 0.1.0 |
| IR schema | 1.0 |
| Providers | aspnetcore (Microsoft.AspNetCore.Mvc.Core 8.0.0.0..11.0.0.0, Microsoft.AspNetCore.Mvc 8.0.0.0..11.0.0.0, Microsoft.AspNetCore.Routing 8.0.0.0..11.0.0.0, Microsoft.AspNetCore.Http.Abstractions 8.0.0.0..11.0.0.0, Microsoft.Extensions.DependencyInjection.Abstractions 8.0.0.0..11.0.0.0); hosting (Microsoft.Extensions.Hosting.Abstractions 8.0.0.0..11.0.0.0) |

### Executive summary
The run found 15 High-confidence groups in which overlapping request or hosted-service roots reach process-wide static or singleton state without an evidenced common guard. The resulting risks include competing writes that silently replace request data, reads whose old-or-new result depends on scheduling, and a stale read-modify-write that can erase a counter increment [E:F1.R] [E:F1.O] [E:F6.S] [E:F15.S].

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
One invocation can replace the value another invocation stored in `Source` before that earlier value is used. The earlier write is therefore lost under `DCA1001` [E:F1.S].

##### Why the accesses overlap
Both writes occur in `Demo.Web.Cases.SharedLibraryStatic.SyncController.Post(string)` at `Demo.Web/Cases/SharedLibraryStatic.cs:12`. The action can process concurrent requests, so two invocations can reach the process-wide state at the same time [E:F1.A] [E:F1.B] [E:F1.O].

##### Why the protection is insufficient
Neither access holds a common guard, leaving the shared `Source` field unprotected [E:F1.P].

##### Interleaving
The first invocation writes `Source`; a second invocation then writes `Source` before the first invocation uses its value. The first invocation consequently observes or acts through the second invocation’s value instead of its own [E:F1.A] [E:F1.B] [E:F1.S].

##### Evidence mode
This `DCA1001` conclusion uses deterministic static evidence showing that both accesses bind to the same process-wide `Demo.Domain.Cases.SharedLibraryStatic.LastSync` region [E:F1.R].

##### Uncertainty
The evidence establishes that the harmful ordering is possible, but not whether that ordering occurs in a particular deployment or whether last-write-wins behavior is intentional [E:F1.O] [E:F1.S].

##### Remediation
- Keep the assignment to `Source` and the operation that must consume that assigned value inside one shared `lock`; verify manually [E:F1.P].
  - Check: confirm every invocation of `Demo.Web.Cases.SharedLibraryStatic.SyncController.Post(string)` uses the same process-wide guard across both the write and its dependent use [E:F1.A] [E:F1.B].

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
A request’s selected `Theme` can be overwritten by another request before the first request uses it. That competing-write outcome loses one request’s value under `DCA1001` [E:F2.S].

##### Why the accesses overlap
Both writes occur in `Demo.Web.Cases.ActionSelfOverlap.ThemeController.Put(string)` at `Demo.Web/Cases/ActionSelfOverlap.cs:20`. Separate invocations of that action may execute concurrently [E:F2.A] [E:F2.B] [E:F2.O].

##### Why the protection is insufficient
Neither write holds an evidenced guard, so concurrent invocations have unrestricted access to the shared `Theme` field [E:F2.P].

##### Interleaving
One invocation writes its value to `Theme`; before it can use that value, another invocation writes a different value. The first invocation then continues without the state it established [E:F2.A] [E:F2.B] [E:F2.S].

##### Evidence mode
This `DCA1001` conclusion uses deterministic static evidence. The dependency-injection binding shows that both controller invocations reach the same singleton `Demo.Web.Cases.ActionSelfOverlap.ThemeSettings` region [E:F2.R].

##### Uncertainty
The analysis establishes possible self-overlap but does not prove that the harmful schedule occurs at runtime or that callers require each assigned value to remain stable [E:F2.O] [E:F2.S].

##### Remediation
- Guard the `Theme` assignment and every operation that assumes that assigned value with the same `lock`; verify manually [E:F2.P].
  - Check: inspect `Demo.Web.Cases.ActionSelfOverlap.ThemeController.Put(string)` and confirm no dependent use can occur after releasing the shared guard [E:F2.A] [E:F2.B].

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
The shutdown path can miss the most recent value written to `_lastItem` and observe the earlier value instead, making the reported stopping state timing-dependent under `DCA1001` [E:F3.S].

##### Why the accesses overlap
`Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker.ExecuteAsync(CancellationToken)` writes at `Demo.Web/Cases/BackgroundStopReadsOwnField.cs:10`, while `Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker.StopAsync(CancellationToken)` reads at `Demo.Web/Cases/BackgroundStopReadsOwnField.cs:16`. The host may begin stopping while execution is still active [E:F3.A] [E:F3.B] [E:F3.O].

##### Why the protection is insufficient
The write and read hold no common guard, so nothing in the evidenced accesses coordinates the value observed during shutdown [E:F3.P].

##### Interleaving
The stopping read may occur immediately before or immediately after the executing path writes `_lastItem`. It can therefore observe either the old value or the new value depending on scheduling [E:F3.A] [E:F3.B] [E:F3.S].

##### Evidence mode
This `DCA1001` conclusion uses deterministic static evidence. The hosted-service registration binds both lifecycle roots to the same singleton `Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker` region [E:F3.R].

##### Uncertainty
Lifecycle ordering was not analyzed, so the evidence proves potential overlap rather than the precise order used by every host shutdown. Confirm whether execution has already quiesced when the read occurs [E:F3.O] [E:F3.S].

##### Remediation
- Establish explicit shutdown ordering so execution is cancelled and its completion is awaited before `Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker.StopAsync(CancellationToken)` reads `_lastItem`; verify manually [E:F3.O].
  - Check: trace the stopping path and confirm the read at `Demo.Web/Cases/BackgroundStopReadsOwnField.cs:16` is unreachable until `Demo.Web.Cases.BackgroundStopReadsOwnField.ProgressWorker.ExecuteAsync(CancellationToken)` has completed [E:F3.A] [E:F3.B].
- If the read must remain concurrent, protect both accesses to `_lastItem` with the same `lock`; verify manually [E:F3.P].
  - Check: confirm the write at `Demo.Web/Cases/BackgroundStopReadsOwnField.cs:10` and read at `Demo.Web/Cases/BackgroundStopReadsOwnField.cs:16` acquire one shared guard [E:F3.A] [E:F3.B].

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
Two concurrent invocations can publish different values to `LastPath` on the process-wide `VisitorStats`; the later assignment replaces the earlier one, so one requested path is lost [E:F4.A] [E:F4.B] [E:F4.S].

##### Why the accesses overlap
`Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController.Post(string)` is an action root that may execute for multiple requests at once, and both writes occur at `Demo.Web/Cases/DiSingletonControllerVsWorker.cs:20` [E:F4.A] [E:F4.B] [E:F4.O].

##### Why the protection is insufficient
Neither invocation holds an evidenced common guard, while dependency-injection binding makes both invocations reach the same `VisitorStats` instance [E:F4.P] [E:F4.R].

##### Interleaving
One invocation writes its path, then the other invocation writes a different path before the first value can remain as the singleton's current value; the second assignment supersedes the first [E:F4.A] [E:F4.B] [E:F4.S].

##### Evidence mode
This `DCA1001` result follows deterministic static evidence connecting the action accesses to the singleton region and establishing self-overlap of the action root [E:F4.R] [E:F4.O].

##### Uncertainty
The evidence establishes that overlap is permitted, not that two requests will be scheduled in the harmful order on every run; confirm whether retaining every submitted path is required [E:F4.O] [E:F4.S].

##### Remediation
- If `LastPath` is intentionally one shared value, put every access behind the same singleton-owned `lock` and define the winning-write policy; verify manually [E:F4.P] [E:F4.R].
  - Check: confirm both accesses at `Demo.Web/Cases/DiSingletonControllerVsWorker.cs:20` use the one process-wide guard [E:F4.A] [E:F4.B].
- If every submitted path must be retained, replace the single slot with a `ConcurrentQueue` or another append-only owner and record each invocation separately; verify manually [E:F4.S].
  - Check: confirm `Demo.Web.Cases.DiSingletonControllerVsWorker.VisitsController.Post(string)` no longer overwrites a prior request's value [E:F4.A] [E:F4.B].

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
The controller's read of `Current` can race the worker's publication, so a request may observe the value from before the worker update or the value after it depending on timing [E:F6.A] [E:F6.B] [E:F6.S].

##### Why the accesses overlap
`Demo.Web.Cases.EscapeIntoSingleton.DraftsController.Get()` and `Demo.Web.Cases.EscapeIntoSingleton.DraftWorker.ExecuteAsync(CancellationToken)` are independent action and hosted-service roots that may run concurrently in the same process [E:F6.A] [E:F6.B] [E:F6.O].

##### Why the protection is insufficient
The read at `Demo.Web/Cases/EscapeIntoSingleton.cs:41` and the write at `Demo.Web/Cases/EscapeIntoSingleton.cs:26` hold no evidenced protection, and dependency-injection binding routes both consumers to the same `DraftRegistry` instance [E:F6.P] [E:F6.R].

##### Interleaving
The request can read `Current` immediately before the worker writes it, returning the old value; if the write occurs first, the same request path can instead return the new value [E:F6.A] [E:F6.B] [E:F6.S].

##### Evidence mode
This `DCA1001` result uses deterministic static evidence for the singleton binding, the read/write pair, and the independently executing roots [E:F6.R] [E:F6.O].

##### Uncertainty
The evidence does not impose a happens-before relationship between the request and worker, so application requirements must determine whether either value is acceptable or whether a completed worker update must be visible to the request [E:F6.O] [E:F6.S].

##### Remediation
- If `Current` is a latest-value snapshot, publish it with `Interlocked.Exchange` and consume it with `Volatile.Read`; verify manually [E:F6.A] [E:F6.B] [E:F6.P].
  - Check: confirm the accesses at `Demo.Web/Cases/EscapeIntoSingleton.cs:26` and `Demo.Web/Cases/EscapeIntoSingleton.cs:41` use the paired atomic publication pattern [E:F6.A] [E:F6.B].
- If Get must observe a particular worker update, route both operations through one state owner and add an explicit execution-order handoff; verify manually [E:F6.O] [E:F6.S].
  - Check: confirm `Demo.Web.Cases.EscapeIntoSingleton.DraftsController.Get()` cannot read until the required `Demo.Web.Cases.EscapeIntoSingleton.DraftWorker.ExecuteAsync(CancellationToken)` update has completed [E:F6.A] [E:F6.B].

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
Concurrent requests can assign different values to `Target` on the process-wide `RelayState`; the later assignment replaces the earlier one, losing one request's target [E:F7.A] [E:F7.B] [E:F7.S].

##### Why the accesses overlap
`Demo.Web.Cases.FromServicesActionParameter.RelayController.Post(RelayState, string)` may execute concurrently with itself for separate requests, and both writes occur at `Demo.Web/Cases/FromServicesActionParameter.cs:16` [E:F7.A] [E:F7.B] [E:F7.O].

##### Why the protection is insufficient
Neither access holds an evidenced guard, and the action parameter resolves to the same singleton `RelayState` instance for both requests [E:F7.P] [E:F7.R].

##### Interleaving
One request writes its target and a second request writes another target before the shared value is consumed as intended; the second write supersedes the first [E:F7.A] [E:F7.B] [E:F7.S].

##### Evidence mode
This `DCA1001` result follows deterministic static evidence connecting the action parameter to the singleton region and establishing action self-overlap [E:F7.R] [E:F7.O].

##### Uncertainty
The evidence proves that concurrent execution is possible but not which request writes last in a particular run; confirm whether overwriting is intended or every target must survive [E:F7.O] [E:F7.S].

##### Remediation
- If `Target` is intentionally one shared value, guard every access with the same singleton-owned `lock` and define how a winner is selected; verify manually [E:F7.P] [E:F7.R].
  - Check: confirm both accesses at `Demo.Web/Cases/FromServicesActionParameter.cs:16` use the one process-wide guard [E:F7.A] [E:F7.B].
- If each request's target must be preserved, keep the target in request-owned state or append it to a `ConcurrentQueue` instead of assigning one singleton slot; verify manually [E:F7.S].
  - Check: confirm `Demo.Web.Cases.FromServicesActionParameter.RelayController.Post(RelayState, string)` no longer overwrites another request's target [E:F7.A] [E:F7.B].

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
A request can miss the startup write to `Stage` and observe the prior value, making the result of `Demo.Web.Cases.HostedStartVsAction.WarmupController.Get()` depend on scheduling [E:F8.A] [E:F8.B] [E:F8.S].

##### Why the accesses overlap
The action root and `Demo.Web.Cases.HostedStartVsAction.WarmupService.StartAsync(CancellationToken)` may execute concurrently in the same process; the host lifecycle evidence does not establish an order between them [E:F8.O].

##### Why the protection is insufficient
Neither the read nor the write holds an evidenced guard, so nothing creates a shared synchronization boundary for `Stage` [E:F8.P].

##### Interleaving
The action can read `Stage` at `Demo.Web/Cases/HostedStartVsAction.cs:35` before the hosted-service write at `Demo.Web/Cases/HostedStartVsAction.cs:19`; that invocation then proceeds with the earlier value even though startup subsequently publishes the new one [E:F8.A] [E:F8.B] [E:F8.S].

##### Evidence mode
This `DCA1001` conclusion uses deterministic static evidence, including dependency-injection binding that places both accesses on the same singleton state object within one process [E:F8.R].

##### Uncertainty
The analysis establishes possible overlap but does not determine the host’s runtime ordering or prove that the old value causes incorrect behavior in every deployment [E:F8.O] [E:F8.S].

##### Remediation
- Add a readiness gate so `Demo.Web.Cases.HostedStartVsAction.WarmupService.StartAsync(CancellationToken)` publishes readiness only after writing `Stage`, and `Demo.Web.Cases.HostedStartVsAction.WarmupController.Get()` passes that gate before reading it; verify manually [E:F8.O] [E:F8.S].
  - Check: confirm the gate is published after `Demo.Web/Cases/HostedStartVsAction.cs:19` and awaited before `Demo.Web/Cases/HostedStartVsAction.cs:35` [E:F8.A] [E:F8.B].

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
When two requests update `Email`, the later store can replace the other request’s value without detecting the conflict, so one requested update disappears from the shared state [E:F9.A] [E:F9.B] [E:F9.S].

##### Why the accesses overlap
Separate invocations of `Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string)` may execute at the same time because the action root can overlap with itself across requests [E:F9.O].

##### Why the protection is insufficient
Neither writer holds an evidenced guard, leaving the singleton `Email` field subject to competing stores [E:F9.P] [E:F9.R].

##### Interleaving
One invocation writes `Email` at `Demo.Web/Cases/LambdaAndLocalFunction.cs:24`, then an overlapping invocation writes the same field at that location; the final state retains only the later value [E:F9.A] [E:F9.B] [E:F9.S].

##### Evidence mode
This `DCA1001` finding comes from deterministic static access, overlap, and dependency-injection binding evidence that connects both writes to one singleton object in the process [E:F9.R] [E:F9.O].

##### Uncertainty
Static evidence proves the conflicting schedule is possible, but it does not establish actual simultaneous traffic or whether application requirements intentionally permit last-writer-wins behavior [E:F9.O] [E:F9.S].

##### Remediation
- Route every assignment to `Email` through one shared synchronization boundary and apply an explicit conflict rule, such as rejecting stale versions rather than blindly replacing the value; verify manually [E:F9.P] [E:F9.S].
  - Check: confirm the assignment at `Demo.Web/Cases/LambdaAndLocalFunction.cs:24` runs only after the conflict check while holding the same `lock` used by every writer [E:F9.A] [E:F9.B].
- If the value belongs to an individual user or request, replace process-wide ownership with per-owner state and synchronize at that owner boundary; verify manually [E:F9.R].
  - Check: trace registration and resolution to confirm unrelated requests no longer share the mutable state object containing `Email` [E:F9.R] [E:F9.O].

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
When two requests update `Name`, the later store can silently replace the other request’s value, leaving no indication that one requested update was discarded [E:F10.A] [E:F10.B] [E:F10.S].

##### Why the accesses overlap
The action root `Demo.Web.Cases.LambdaAndLocalFunction.ProfileController.Update(string, string)` may serve overlapping requests, allowing two invocations to write the singleton field concurrently [E:F10.O] [E:F10.R].

##### Why the protection is insufficient
Both accesses occur without an evidenced shared guard, so the writers have no synchronization or conflict-detection boundary around `Name` [E:F10.P].

##### Interleaving
One invocation writes `Name` at `Demo.Web/Cases/LambdaAndLocalFunction.cs:27`, followed by the overlapping invocation’s write at the same location; only the second value remains [E:F10.A] [E:F10.B] [E:F10.S].

##### Evidence mode
This `DCA1001` conclusion uses deterministic static evidence, with dependency-injection binding showing that both action invocations reach the same singleton state object in one process [E:F10.R].

##### Uncertainty
The evidence demonstrates a feasible conflicting schedule but does not determine how frequently requests overlap or whether last-writer-wins is the intended business rule [E:F10.O] [E:F10.S].

##### Remediation
- Route every assignment to `Name` through one shared synchronization boundary and enforce an explicit conflict rule, such as rejecting stale versions before replacement; verify manually [E:F10.P] [E:F10.S].
  - Check: confirm the assignment at `Demo.Web/Cases/LambdaAndLocalFunction.cs:27` executes only after the conflict check while holding the same `lock` used by every writer [E:F10.A] [E:F10.B].
- If the value belongs to an individual user or request, move it from process-wide singleton ownership to per-owner state and synchronize within that ownership boundary; verify manually [E:F10.R].
  - Check: trace registration and resolution to confirm unrelated requests no longer share the mutable state object containing `Name` [E:F10.R] [E:F10.O].

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
Two concurrent invocations of `Demo.Web.Cases.MinimalApiReadWrite.FeatureFlagEndpoints.Set(FeatureFlags, string)` can assign competing values to `Mode`; the final `FeatureFlags` state retains only one value, so the other requested update is lost [E:F11.A] [E:F11.B] [E:F11.S].

##### Why the accesses overlap
The endpoint handler may handle multiple requests concurrently with itself, while the singleton binding directs those invocations to the same `FeatureFlags` instance [E:F11.O] [E:F11.R].

##### Why the protection is insufficient
Neither invocation holds an evidenced common guard when it writes `Mode`, so nothing excludes the competing assignments [E:F11.P].

##### Interleaving
One request writes `Mode` at `Demo.Web/Cases/MinimalApiReadWrite.cs:12`; before that value becomes the application’s settled state, a second invocation of the same handler writes its value at the same location and replaces the first [E:F11.A] [E:F11.B] [E:F11.S].

##### Evidence mode
This `DCA1001` conclusion uses deterministic static evidence connecting both writes to the same singleton-backed region and self-overlapping handler root [E:F11.A] [E:F11.B] [E:F11.R] [E:F11.O].

##### Uncertainty
The evidence establishes that the overlap is possible, but it does not establish the runtime arrival order of requests or whether last-writer-wins behavior is intentional [E:F11.S].

##### Remediation
- Define one concurrency policy for `Mode`, such as guarding every access with the same `lock` or rejecting stale writes through a versioned conditional update; verify manually [E:F11.P].
  - Check: confirm every read and write of `Mode` follows that policy, then issue competing requests and confirm the documented winner or rejection outcome [E:F11.A] [E:F11.B].
- If mode selection should not be global, move the mutable value out of the singleton `FeatureFlags`; verify manually [E:F11.R].
  - Check: confirm concurrent requests no longer resolve the state they mutate to the same shared instance [E:F11.O].

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
Two concurrent calls to `Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController.Put(string)` can assign competing values to `Label`; the singleton `ShelfState` retains only the later value, losing the other requested update [E:F13.A] [E:F13.B] [E:F13.S].

##### Why the accesses overlap
The same `Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController.Put(string)` action may execute concurrently for separate requests, and its singleton binding makes those executions target one `ShelfState` instance [E:F13.O] [E:F13.R].

##### Why the protection is insufficient
Neither execution holds an evidenced common guard around its write to `Label`, so the two action invocations do not exclude one another [E:F13.P].

##### Interleaving
One action invocation writes `Label` at `Demo.Web/Cases/PocoControllerSelfOverlap.cs:19`, then a competing invocation writes a different value at that same location, leaving no retained representation of the first request’s update [E:F13.A] [E:F13.B] [E:F13.S].

##### Evidence mode
This `DCA1001` conclusion uses deterministic static evidence linking the paired writes to the same singleton region and a self-overlapping controller action [E:F13.A] [E:F13.B] [E:F13.R] [E:F13.O].

##### Uncertainty
The evidence proves a feasible concurrency relationship, not that requests will collide in a particular run or that the application requires both writes to survive [E:F13.S].

##### Remediation
- Apply one explicit conflict policy to `Label`, using the same `lock` for all accesses or a versioned conditional update when stale writes must be rejected; verify manually [E:F13.P].
  - Check: confirm all reads and writes of `Label` participate, then run simultaneous calls to `Demo.Web.Cases.PocoControllerSelfOverlap.ShelfController.Put(string)` and verify the defined outcome [E:F13.A] [E:F13.B].
- If shelf labels are not intended to be application-global, replace the singleton ownership of `ShelfState` with the required narrower or externally persisted ownership; verify manually [E:F13.R].
  - Check: confirm separate requests no longer mutate the same in-memory `ShelfState` unless that sharing is explicitly required [E:F13.O].

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
Concurrent calls to `Demo.Web.Cases.PrimaryConstructorInjection.QuotaController.Post(string)` can assign competing values to `Owner`; because both reach one singleton `QuotaState`, the later write replaces the earlier requested owner [E:F14.A] [E:F14.B] [E:F14.S].

##### Why the accesses overlap
`Demo.Web.Cases.PrimaryConstructorInjection.QuotaController.Post(string)` may execute concurrently with itself for separate requests, while primary-constructor injection supplies those executions with the same singleton `QuotaState` [E:F14.O] [E:F14.R].

##### Why the protection is insufficient
The paired writes to `Owner` hold no evidenced common protection, allowing both action invocations to update the shared state independently [E:F14.P].

##### Interleaving
One request writes `Owner` at `Demo.Web/Cases/PrimaryConstructorInjection.cs:16`; a second concurrent request then writes its owner at the same location, making the singleton’s final value reflect only the second assignment [E:F14.A] [E:F14.B] [E:F14.S].

##### Evidence mode
This `DCA1001` conclusion uses deterministic static evidence connecting both accesses to one singleton region and the self-overlapping action root [E:F14.A] [E:F14.B] [E:F14.R] [E:F14.O].

##### Uncertainty
The analysis establishes possible overlap but does not determine actual request timing or whether overwriting an earlier owner is accepted domain behavior [E:F14.S].

##### Remediation
- Define an atomic ownership-change policy for `Owner`, such as serializing every access with the same `lock` or conditionally accepting a write only for an expected prior state; verify manually [E:F14.P].
  - Check: confirm every read and write of `Owner` uses the policy, then submit competing calls to `Demo.Web.Cases.PrimaryConstructorInjection.QuotaController.Post(string)` and verify one documented outcome [E:F14.A] [E:F14.B].
- If quota ownership should be isolated rather than global, move `Owner` out of the singleton `QuotaState` into the appropriate ownership boundary; verify manually [E:F14.R].
  - Check: confirm requests that require independent quota state no longer resolve and mutate the same instance [E:F14.O].

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
Two concurrent calls to `Demo.Web.Cases.RmwSingletonCounter.HitsController.Post()` can calculate from the same value of `Hits`; a later stale write can overwrite the other call’s increment, so one hit is lost [E:F15.A] [E:F15.B] [E:F15.S].

##### Why the accesses overlap
The read-modify-write at `Demo.Web/Cases/RmwSingletonCounter.cs:20` operates on singleton-backed state, and the same action root may serve concurrent requests [E:F15.R] [E:F15.O].

##### Why the protection is insufficient
Neither invocation holds a common guard, and no atomic update protects the read, calculation, and write as one operation [E:F15.P].

##### Interleaving
Invocation A reads `Hits`; invocation B writes its computed value; invocation A then writes the value it computed from its earlier read, replacing B’s update [E:F15.A] [E:F15.B] [E:F15.S].

##### Evidence mode
This `DCA1002` conclusion follows from deterministic static evidence connecting both read-modify-write accesses to the same singleton region and overlapping action root [E:F15.R] [E:F15.O].

##### Uncertainty
The evidence establishes that the harmful ordering is possible, but not that concurrent requests reach it in every deployment or execution [E:F15.S].

##### Remediation
- Make the update to `Hits` atomic with `Interlocked.Increment`, or guard every access with the same process-wide `lock`; verify manually [E:F15.P].
  - Check: confirm two concurrent calls to `Demo.Web.Cases.RmwSingletonCounter.HitsController.Post()` preserve both increments at `Demo.Web/Cases/RmwSingletonCounter.cs:20` [E:F15.A] [E:F15.B].

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
A request can miss the worker’s latest update to `LastBeat` and observe the previous heartbeat instead of the newly published value [E:F16.S].

##### Why the accesses overlap
`Demo.Web.Cases.StaticFieldHttpVsWorker.HeartbeatController.Get()` reads at `Demo.Web/Cases/StaticFieldHttpVsWorker.cs:26` while `Demo.Web.Cases.StaticFieldHttpVsWorker.HeartbeatWorker.ExecuteAsync(CancellationToken)` writes at `Demo.Web/Cases/StaticFieldHttpVsWorker.cs:16`; the HTTP action and hosted-service execution may run concurrently in the same process [E:F16.A] [E:F16.B] [E:F16.O] [E:F16.R].

##### Why the protection is insufficient
The read and write have no shared synchronization, so nothing orders publication of the worker’s value relative to the request’s observation [E:F16.P].

##### Interleaving
The request can read before the worker’s write and return the old value, or read after it and return the new value; concurrent execution provides no ordering guarantee between those outcomes [E:F16.A] [E:F16.B] [E:F16.S].

##### Evidence mode
This `DCA1001` conclusion uses deterministic static evidence for an unsynchronized read and write of the same process-wide field [E:F16.R] [E:F16.P].

##### Uncertainty
Static evidence does not prove that the endpoint is invoked during a worker update or that an older observation violates the application’s heartbeat contract [E:F16.O] [E:F16.S].

##### Remediation
- Guard every read and write of `LastBeat` with the same process-wide `lock`; verify manually [E:F16.P].
  - Check: confirm the accesses at `Demo.Web/Cases/StaticFieldHttpVsWorker.cs:16` and `Demo.Web/Cases/StaticFieldHttpVsWorker.cs:26` both use that one guard [E:F16.A] [E:F16.B].

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
A read of `_lastVisitor` can miss a concurrent request’s new visitor value and return the previous value instead [E:F17.S].

##### Why the accesses overlap
`Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Get()` reads at `Demo.Web/Cases/StaticFieldUnlockedReadWrite.cs:17`, while `Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Post(string)` writes at `Demo.Web/Cases/StaticFieldUnlockedReadWrite.cs:14`; both action roots may execute concurrently against the same static field [E:F17.A] [E:F17.B] [E:F17.O] [E:F17.R].

##### Why the protection is insufficient
Neither action holds a common guard, so there is no synchronization ordering the write relative to the read [E:F17.P].

##### Interleaving
If the read occurs before the concurrent write, it observes the old visitor; if it occurs after the write, it observes the new visitor [E:F17.A] [E:F17.B] [E:F17.S].

##### Evidence mode
This `DCA1001` conclusion uses deterministic static evidence connecting an unsynchronized read and write to the same process-wide field [E:F17.R] [E:F17.P].

##### Uncertainty
The analysis establishes possible overlap but does not prove that concurrent requests exercise the harmful timing or that returning the prior visitor violates the endpoint contract [E:F17.O] [E:F17.S].

##### Remediation
- Guard every access to `_lastVisitor` with the same process-wide `lock`; verify manually [E:F17.P].
  - Check: confirm the accesses at `Demo.Web/Cases/StaticFieldUnlockedReadWrite.cs:14` and `Demo.Web/Cases/StaticFieldUnlockedReadWrite.cs:17` both use that one guard [E:F17.A] [E:F17.B].

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
- Load: 2.8 s
- Analysis: 2.6 s
- Candidate pairs: 76
- Suppressed pairs: 6
- Skipped pairs: invocation 4, no-self-overlap 8, read-read 40
- Late responses: 0
