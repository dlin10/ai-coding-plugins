# Concurrency Hunter report

| Field | Value |
| --- | --- |
| Status | CompleteWithFindings |
| Reasons | none |
| Target | C:\Dev\CodexPlugins\plugins\concurrency-hunter\demo\Demo.slnx |
| Run | 20260914-081616-071919 |
| Started | 2026-09-14T08:16:16.1774404+00:00 |
| Duration | 36.6 s |
| Version | concurrency-hunter 0.1.0 |

### Executive summary
The demo exposes one high-confidence group of unprotected static state in `LastVisitorController`. Concurrent GET and POST requests can race on `_lastVisitor`, and concurrent POST requests can overwrite each other's writes, producing lost updates and torn reads [E:F1.R] [E:F1.O] [E:F2.O] [E:F2.S].

### Coverage
- Projects loaded: 3 of 3
- Missing projects: none
- Execution roots: 26 public actions of ControllerBase descendants
- Not analyzed in this version: calls made from an action, dependency-injected state, spawned work, locks other than lock on a static field, path feasibility

### High findings
#### G1 · DCA1001 · _lastVisitor on static:Demo.Web.Cases.StaticFieldUnlockedReadWrite.LastVisitorController (2 findings)
##### What can be lost
A concurrent GET can observe a stale or partially updated `_lastVisitor` while a POST is writing it [E:F1.S]. Two concurrent POST requests can overwrite each other's updates, so one visitor name is lost [E:F2.S].

##### Why the accesses overlap
The GET and POST action roots can execute for different requests at the same time [E:F1.O]. Multiple POST invocations can also run concurrently against the same static field [E:F2.O].

##### Why the protection is insufficient
The read and write paths share no evidenced guard around `_lastVisitor` [E:F1.P]. Concurrent writes to the same static slot are likewise unprotected [E:F2.P].

##### Interleaving
A POST write at `StaticFieldUnlockedReadWrite.cs:14` can interleave with a GET read at `StaticFieldUnlockedReadWrite.cs:17`, so the reader may see either the old or new value [E:F1.A] [E:F1.B] [E:F1.S]. Two POST writes both targeting `_lastVisitor` at `StaticFieldUnlockedReadWrite.cs:14` can complete in either order, leaving only the last writer's name [E:F2.A] [E:F2.B] [E:F2.S].

##### Evidence mode
This `DCA1001` conclusion uses deterministic static evidence for the shared static region [E:F1.R] [E:F2.R].

##### Uncertainty
Path feasibility is not established, so confirm that both GET and POST endpoints can receive concurrent traffic in the same deployment [E:F1.S] [E:F2.S].

##### Remediation
- Guard every access to `_lastVisitor` with the same `lock` object; verify manually [E:F1.P] [E:F2.P].
  - Check: confirm `Post` and `Get` both acquire that guard before touching `_lastVisitor` at `StaticFieldUnlockedReadWrite.cs:14` and `StaticFieldUnlockedReadWrite.cs:17` [E:F1.A] [E:F1.B].
- Replace the mutable static field with request-scoped or thread-safe storage; verify manually [E:F2.R].
  - Check: confirm no concurrent writes still share the same unprotected static slot after the change [E:F2.A] [E:F2.B].

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
- Load: 2.3 s
- Analysis: 2.0 s
- Late responses: 0
