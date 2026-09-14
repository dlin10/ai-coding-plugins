# Concurrency Hunter report

| Field | Value |
| --- | --- |
| Status | CompleteWithFindings |
| Reasons | none |
| Target | C:\Dev\CodexPlugins\plugins\concurrency-hunter\demo\Demo.slnx |
| Run | 20260914-081113-66acae |
| Started | 2026-09-14T08:11:13.0001689+00:00 |
| Duration | 49.5 s |
| Version | concurrency-hunter 0.1.0 |

### Executive summary
One High-confidence group was found. `LastVisitorController` keeps the last visitor's name in an unprotected `static` field, `_lastVisitor`, and concurrent requests can reach it at the same time [E:F1.R] [E:F1.O]. When two POST requests overlap, one visitor's update is silently overwritten by the other's [E:F2.S]. A GET that overlaps a POST can return a stale name [E:F1.S]. No guard orders these accesses [E:F1.P] [E:F2.P]. Each single store is atomic, so the value is never corrupted. The risk is wrong ordering of the "last" visitor, which a shared `lock` or an explicitly owned, sequenced service would remove.

### Coverage
- Projects loaded: 3 of 3
- Missing projects: none
- Execution roots: 26 public actions of ControllerBase descendants
- Not analyzed in this version: calls made from an action, dependency-injected state, spawned work, locks other than lock on a static field, path feasibility

### High findings
#### G1 · DCA1001 · _lastVisitor on static:Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController (2 findings)
##### What can be lost
The process-wide `_lastVisitor` field has no owner. When two POST requests overlap, one visitor's name is overwritten by the other's, and neither caller can tell whose value survived [E:F2.S] [E:F2.R]. A GET that runs during a POST can return either the previous visitor or the new one, so "last visitor" means only "whichever store happened to land last" [E:F1.S] [E:F1.R].

##### Why the accesses overlap
Both actions are HTTP entry points on a controller. The field is `static`, so every request on every thread sees the same storage [E:F1.O] [E:F2.O]. `LastVisitorController.Post` can run in parallel with itself (`StaticFieldUnlockedReadWrite.cs:14`), and it can also run at the same time as `LastVisitorController.Get` (`StaticFieldUnlockedReadWrite.cs:17`) [E:F1.A] [E:F1.B] [E:F2.A] [E:F2.B].

##### Why the protection is insufficient
Neither access holds a guard. No `lock` is taken, and neither `Volatile` nor `Interlocked` is used, so nothing orders the stores or ties a read to a particular store [E:F1.P] [E:F2.P].

##### Interleaving
For F2, request X enters `LastVisitorController.Post` and request Y enters it too. Y stores its name and then X stores its name. Y has returned success, but its update is already gone [E:F2.A] [E:F2.B] [E:F2.S]. For F1, a POST stores a new name while a GET is loading the field. The GET returns the old value, even though the POST it may be waiting on has completed from the client's point of view [E:F1.A] [E:F1.B] [E:F1.S].

##### Evidence mode
This is a `DCA1001` conclusion drawn from deterministic static evidence. It is based on the declared field, the two action roots, and the absence of any held protection. It was not observed at runtime [E:F1.R] [E:F2.R].

##### Uncertainty
A single reference assignment is atomic in .NET. So the risk is last-writer-wins ordering and stale reads, not a torn or corrupted string. How much that matters depends on whether any caller relies on "last" meaning the most recently completed POST [E:F1.S] [E:F2.S]. The analysis also does not establish that the app runs with enough concurrency, or on more than one instance, for this to occur in practice [E:F1.O] [E:F2.O].

##### Remediation
- Synchronization: guard both the store in `LastVisitorController.Post` and the read in `LastVisitorController.Get` with one private `static` `readonly` `lock` object, so each read sees a completed store; verify manually [E:F1.P] [E:F2.P].
  - Check: confirm that every read and write of `_lastVisitor` in `StaticFieldUnlockedReadWrite.cs` happens inside that same guard and nowhere else [E:F1.A] [E:F1.B] [E:F2.A].
- Ownership: if "last visitor" should reflect an explicit order, stop keeping it in a `static` field. Move it into a registered service that stamps each update with an arrival sequence (for example via `Interlocked.Increment`) and keeps the highest; verify manually [E:F2.R] [E:F2.S].
  - Check: confirm the controller no longer declares `_lastVisitor`, and that a slower POST can no longer replace a newer one [E:F2.B].
- Execution order: if only visibility across threads is needed and last-writer-wins is acceptable, document that choice and use `Volatile.Write` and `Volatile.Read` on the field; verify manually [E:F1.O].
  - Check: confirm the product owner accepts that overlapping POSTs may keep either name [E:F1.S] [E:F2.S].

##### F1
- Access A: Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Get() performs read at Demo.Web/Cases/StaticFieldUnlockedReadWrite.cs:17 under root ControllerBase action Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Get(); holds no protection.
- Access B: Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Post(string) performs write at Demo.Web/Cases/StaticFieldUnlockedReadWrite.cs:14 under root ControllerBase action Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Post(string); holds no protection.
- Resource: Demo.Web · static:Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController · _lastVisitor
- Protection: unprotected
- Scenario: B writes `_lastVisitor`; A reads `_lastVisitor` at the same time; A observes either the old or the new value
- Confidence: High (85) · Path feasibility is not analyzed in this version.
- Evidence: F1.A, F1.B, F1.R, F1.O, F1.P, F1.S

##### F2
- Access A: Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Post(string) performs write at Demo.Web/Cases/StaticFieldUnlockedReadWrite.cs:14 under root ControllerBase action Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Post(string); holds no protection.
- Access B: Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Post(string) performs write at Demo.Web/Cases/StaticFieldUnlockedReadWrite.cs:14 under root ControllerBase action Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController.Post(string); holds no protection.
- Resource: Demo.Web · static:Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController · _lastVisitor
- Protection: unprotected
- Scenario: A writes `_lastVisitor`; B writes `_lastVisitor` before A's value is used; One of the two writes is lost
- Confidence: High (85) · Path feasibility is not analyzed in this version.
- Evidence: F2.A, F2.B, F2.R, F2.O, F2.P, F2.S

### Medium findings
_None._

### Low findings
_None._

### Diagnostics
- Load: 2.5 s
- Analysis: 2.1 s
- Late responses: 0
